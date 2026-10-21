import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const source = readFileSync(new URL('../src/components/nodes/NodeSetupPanel.vue', import.meta.url), 'utf8')
function panel(restart = async () => ({ success: true })) {
  const calls = { restart: 0, save: 0, saved: [], success: [], errors: [] }
  const settings = { mode: 'Controller', activeMode: 'Standalone', restartRequired: true, revision: 1 }
  const exports = {}
  const script = source.match(/<script setup lang="ts">([\s\S]*?)<\/script>/)[1]
  const compiled = ts.transpileModule(script + '\nexport { settings, load, mode, synchronization, supportsSynchronization, syncHistoryChange, controllerUrl, credential, save, openRestart, confirmRestart, restartConfirm, restartSent, busy, blocked }',
    { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 } }).outputText
  vm.runInNewContext(compiled, {
    exports, defineEmits: () => () => {},
    require: name => {
      if (name === 'vue') return { ref: value => ({ value }), computed: get => ({ get value() { return get() } }), nextTick: async () => {}, onMounted() {}, onBeforeUnmount() {} }
      if (name === '@/api') return {
        nodeSetupApi: {
          save: async body => { calls.save++; calls.saved.push(body); return { settings: { ...settings } } },
          get: async () => ({ settings: { ...settings }, peers: [], effectiveMode: 'Standalone' }),
        },
        serviceApi: { restart: async () => { calls.restart++; return restart() } },
      }
      if (name === '@/i18n') return { t: key => key }
      if (name === '@/composables/useToast') return { useToast: () => ({ showError: value => calls.errors.push(value), showSuccess: value => calls.success.push(value) }) }
      throw new Error(`Unexpected dependency: ${name}`)
    },
  })
  return { api: exports, calls, serverSettings: settings }
}

test('background upgrade status refresh preserves unsaved drafts and ignores older revisions', async () => {
  const { api, serverSettings } = panel()
  await api.load()
  api.mode.value = 'Node'
  api.controllerUrl.value = 'https://unsaved.example/'
  api.credential.value = 'unsaved-private-credential'
  serverSettings.authorityUpgradePending = true
  serverSettings.revision = 2
  await api.load(false)
  assert.equal(api.settings.value.authorityUpgradePending, true)
  assert.equal(api.mode.value, 'Node')
  assert.equal(api.controllerUrl.value, 'https://unsaved.example/')
  assert.equal(api.credential.value, 'unsaved-private-credential')
  serverSettings.revision = 1
  serverSettings.authorityUpgradePending = false
  await api.load(false)
  assert.equal(api.settings.value.revision, 2)
  assert.equal(api.settings.value.authorityUpgradePending, true)
})

test('new managed roles default to synchronization and allow explicit opt-out', async () => {
  for (const enabled of [true, false]) {
    const { api, calls, serverSettings } = panel()
    Object.assign(serverSettings, { mode: 'Standalone', synchronizeRuntimeState: false, runtimeSynchronizationConfigured: false })
    await api.load()
    assert.equal(api.synchronization.value, true)
    api.mode.value = 'Node'
    api.synchronization.value = enabled
    await api.save()
    assert.equal(calls.saved[0].synchronizeRuntimeState, enabled)
    assert.equal(calls.restart, 0)
  }
})

test('unchanged legacy settings are not adopted and background refresh retains synchronization draft', async () => {
  const { api, calls, serverSettings } = panel()
  Object.assign(serverSettings, { synchronizeRuntimeState: true, runtimeSynchronizationConfigured: false })
  await api.load()
  await api.save()
  assert.equal(calls.saved[0].synchronizeRuntimeState, undefined)
  api.synchronization.value = false
  await api.load(false)
  assert.equal(api.synchronization.value, false)
  assert.equal(api.syncHistoryChange.value, false)
  serverSettings.runtimeHistoryChangePending = true
  await api.load(false)
  assert.equal(api.syncHistoryChange.value, true)
})

test('saving node setup never restarts automatically and restart requires pending settings', async () => {
  const { api, calls } = panel()
  await api.openRestart()
  await api.confirmRestart()
  assert.equal(calls.restart, 0)
  await api.save()
  assert.equal(calls.save, 1)
  assert.equal(calls.restart, 0)
  assert.equal(api.restartConfirm.value, false)
  await api.openRestart()
  assert.equal(api.restartConfirm.value, true)
  assert.equal(calls.restart, 0)
  await api.confirmRestart()
  assert.equal(calls.restart, 1)
  assert.equal(api.restartSent.value, true)
  assert.equal(api.restartConfirm.value, false)
  assert.equal(api.blocked.value, true)
  await api.confirmRestart()
  await api.save()
  assert.equal(calls.restart, 1)
  assert.equal(calls.save, 1)
})

test('restart confirmation prevents duplicate calls while awaiting acknowledgement', async () => {
  let acknowledge
  const { api, calls } = panel(() => new Promise(resolve => { acknowledge = resolve }))
  await api.save()
  const first = api.confirmRestart()
  await api.confirmRestart()
  assert.equal(calls.restart, 1)
  assert.equal(api.busy.value, true)
  acknowledge({ success: true })
  await first
  assert.equal(api.busy.value, false)
  assert.equal(api.restartSent.value, true)
})

test('failed restart stays retryable without automatic retries or exposing remote prose', async () => {
  const { api, calls } = panel(async () => { throw { response: { data: { messageKey: 'node.errors.node_http_rejected', message: 'PRIVATE_ERROR' } } } })
  await api.save()
  await api.openRestart()
  await api.confirmRestart()
  assert.equal(calls.restart, 1)
  assert.equal(api.restartSent.value, false)
  assert.equal(api.restartConfirm.value, true)
  assert.equal(api.blocked.value, false)
  assert.deepEqual(calls.errors, ['node.errors.node_http_rejected'])
  assert.match(source, /role="dialog"[\s\S]*node-restart-title/)
  assert.doesNotMatch(source, /(?:window\.)?(?:alert|confirm)\(/)
})
