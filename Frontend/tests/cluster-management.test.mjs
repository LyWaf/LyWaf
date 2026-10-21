import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const read = path => readFileSync(new URL(path, import.meta.url), 'utf8')
test('cluster management has its own menu and keeps the previous URL compatible', () => {
  const sidebar = read('../src/components/Layout/Sidebar.vue')
  assert.match(sidebar, /\{ path: '\/cluster-management', name: '群集管理', icon:/)
  assert.doesNotMatch(sidebar, /path: '\/multi-node'/)
  assert.match(read('../src/router/index.ts'), /path: '\/cluster-management',\s+alias: '\/multi-node'/)
})

test('standalone and node modes never call controller-only APIs; promotion loads the dashboard', async () => {
  const source = read('../src/views/MultiNode.vue').match(/<script setup lang="ts">([\s\S]*?)<\/script>/)[1]
  const exports = {}, calls = []
  const code = ts.transpileModule(source + '\nexport { load, handleSetupMode, dashboard, setupMode }', {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 },
  }).outputText
  vm.runInNewContext(code, { exports, require: name => {
    if (name === 'vue') return { ref: value => ({ value }), computed: get => ({ get value() { return get() } }), onMounted() {}, onBeforeUnmount() {} }
    if (name === '@/api') return { distributionApi: {
      getDashboard: async () => { calls.push('dashboard'); return { success: true, dashboard: { nodes: [] } } },
      getDeployments: async () => { calls.push('deployments'); return { success: true, deployments: [] } },
    } }
    if (name === '@/i18n') return { t: key => key }
    if (name === '@/composables/useToast') return { useToast: () => ({ showError() {}, showSuccess() {} }) }
    return {}
  } })
  await exports.load()
  for (const mode of ['Standalone', 'Node']) { exports.handleSetupMode(mode); await exports.load() }
  assert.equal(calls.length, 0)
  exports.handleSetupMode('Controller')
  await new Promise(resolve => setImmediate(resolve))
  assert.deepEqual(calls, ['dashboard', 'deployments'])
  assert.ok(exports.dashboard.value)
  exports.handleSetupMode('Standalone')
  assert.equal(exports.dashboard.value, null)
})
