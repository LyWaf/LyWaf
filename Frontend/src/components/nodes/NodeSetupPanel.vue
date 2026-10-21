<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { nodeSetupApi, serviceApi, type NodeSetupMode, type NodeSetupPeer, type NodeSetupSettings } from '@/api'
import { t } from '@/i18n'
import { useToast } from '@/composables/useToast'

const emit = defineEmits<{ mode: [value: NodeSetupMode] }>()
const settings = ref<NodeSetupSettings | null>(null)
const peers = ref<NodeSetupPeer[]>([])
const mode = ref<NodeSetupMode>('Standalone')
const controllerUrl = ref('')
const credential = ref('')
const synchronization = ref(true)
const supportsSynchronization = computed(() => typeof settings.value?.synchronizeRuntimeState === 'boolean')
const syncHistoryChange = computed(() => settings.value?.runtimeHistoryChangePending === true)
const peerName = ref('')
const issued = ref('')
const busy = ref(false)
const restartConfirm = ref(false)
const restartSent = ref(false)
const restartCancel = ref<HTMLButtonElement | null>(null)
const blocked = computed(() => busy.value || restartSent.value)
const error = ref('')
let refreshTimer: number | undefined
let refreshing = false
const { showError, showSuccess } = useToast()
const modeText = (value: string) => t(`node.setup.mode.${value}`)
const errorText = (e: any) => {
  const key = e?.response?.data?.messageKey
  return key ? t(key) : t('node.setup.failed')
}
const load = async (refreshDraft = true) => {
  if (refreshing) return
  refreshing = true
  try {
    const response = await nodeSetupApi.get()
    if (settings.value && response.settings.revision < settings.value.revision) return
    settings.value = response.settings
    peers.value = response.peers
    if (refreshDraft) {
      mode.value = response.settings.mode
      controllerUrl.value = response.settings.controllerUrl || ''
      synchronization.value = response.settings.mode === 'Standalone' && !response.settings.runtimeSynchronizationConfigured
        ? true : response.settings.synchronizeRuntimeState ?? true
    }
    error.value = ''
    emit('mode', response.effectiveMode)
  } catch (e) { error.value = errorText(e) }
  finally { refreshing = false }
}
const save = async () => {
  if (blocked.value) return
  busy.value = true
  try {
    const synchronizeRuntimeState = supportsSynchronization.value && mode.value !== 'Standalone'
      && (mode.value !== settings.value?.mode || synchronization.value !== settings.value?.synchronizeRuntimeState) ? synchronization.value : undefined
    const response = await nodeSetupApi.save({ mode: mode.value, controllerUrl: controllerUrl.value,
      credential: credential.value, synchronizeRuntimeState })
    settings.value = response.settings
    credential.value = ''
    showSuccess(t('node.setup.saved'))
  } catch (e) { showError(errorText(e)) }
  finally { busy.value = false }
}
const createPeer = async () => {
  if (blocked.value) return
  busy.value = true
  try {
    const response = await nodeSetupApi.createPeer(peerName.value)
    issued.value = response.credential
    peerName.value = ''
    await load()
  } catch (e) { showError(errorText(e)) }
  finally { busy.value = false }
}
const setEnabled = async (peer: NodeSetupPeer) => {
  if (blocked.value) return
  busy.value = true
  try { await nodeSetupApi.setPeerEnabled(peer.nodeId, !peer.enabled); await load() }
  catch (e) { showError(errorText(e)) }
  finally { busy.value = false }
}
const copy = async () => {
  try { await navigator.clipboard.writeText(issued.value); showSuccess(t('node.setup.copied')) }
  catch { showError(t('node.setup.copyFailed')) }
}
const openRestart = async () => {
  if (blocked.value || !settings.value?.restartRequired) return
  restartConfirm.value = true
  await nextTick()
  restartCancel.value?.focus()
}
const confirmRestart = async () => {
  if (blocked.value || !settings.value?.restartRequired) return
  busy.value = true
  try {
    await serviceApi.restart()
    restartSent.value = true
    restartConfirm.value = false
    showSuccess(t('node.setup.restartRequested'))
  } catch (e) { showError(errorText(e)) }
  finally { busy.value = false }
}
onMounted(() => {
  void load()
  refreshTimer = window.setInterval(() => { if (!blocked.value) void load(false) }, 5000)
})
onBeforeUnmount(() => { if (refreshTimer !== undefined) window.clearInterval(refreshTimer) })
</script>

<template>
  <section class="border-y border-dark-border py-5 space-y-4" aria-labelledby="node-setup-title">
    <div class="flex flex-wrap items-center gap-3">
      <h2 id="node-setup-title" class="text-base font-semibold text-gray-100">{{ t('node.setup.title') }}</h2>
      <span v-if="settings" class="text-sm text-gray-500">{{ t('node.setup.active') }}: {{ modeText(settings.activeMode) }}</span>
    </div>
    <p v-if="error" role="alert" class="text-sm text-red-400">{{ error }}</p>
    <form v-if="settings" class="space-y-4" @submit.prevent="save">
      <div class="grid grid-cols-1 md:grid-cols-2 gap-4 max-w-4xl">
        <label class="block text-sm text-gray-400">{{ t('node.setup.mode') }}
          <select v-model="mode" class="input w-full mt-2" :disabled="blocked">
            <option v-for="value in (['Standalone', 'Controller', 'Node'] as const)" :key="value" :value="value">{{ modeText(value) }}</option>
          </select>
        </label>
        <label v-if="mode === 'Node'" class="block text-sm text-gray-400">{{ t('node.setup.controllerUrl') }}
          <input v-model="controllerUrl" class="input w-full mt-2" type="url" required maxlength="2048" autocomplete="off" :disabled="blocked" />
        </label>
        <label v-if="mode === 'Node'" class="block text-sm text-gray-400 md:col-span-2">{{ t('node.setup.credential') }}
          <input v-model="credential" class="input w-full mt-2" type="password" maxlength="4096" autocomplete="new-password"
            :required="!settings.hasCredential" :placeholder="settings.hasCredential ? t('node.setup.preserveCredential') : ''" :disabled="blocked" />
        </label>
      </div>
      <div v-if="mode !== 'Standalone' && supportsSynchronization" class="space-y-2">
        <label class="flex items-center gap-2 text-sm text-gray-300">
          <input v-model="synchronization" type="checkbox" class="h-4 w-4" :disabled="blocked" />
          {{ t('node.setup.runtimeSync') }}
        </label>
        <p class="text-xs text-gray-500">{{ t('node.setup.runtimeSyncRunning') }}:
          {{ t(settings.runtimeSynchronizationActive ? 'node.setup.runtimeSyncEnabled' : 'node.setup.runtimeSyncDisabled') }}
        </p>
      </div>
      <p v-if="settings.restartRequired" role="status" class="text-sm text-amber-400">{{ t('node.setup.restartRequired') }}</p>
      <p v-if="settings.authorityUpgradePending" role="status" class="text-sm text-amber-400">{{ t('node.setup.authorityUpgradePending') }}</p>
      <p v-if="restartSent" role="status" class="text-sm text-amber-400">{{ t('node.setup.restartRequested') }}</p>
      <div class="flex flex-wrap gap-2">
        <button type="submit" class="btn btn-primary" :disabled="blocked">{{ t('common.save') }}</button>
        <button v-if="settings.restartRequired" type="button" class="btn btn-secondary" :disabled="blocked" @click="openRestart">{{ t('node.setup.restartNow') }}</button>
      </div>
    </form>
    <div v-if="settings?.mode === 'Controller'" class="space-y-3 pt-4 border-t border-dark-border">
      <form class="flex flex-wrap gap-2 max-w-xl" @submit.prevent="createPeer">
        <input v-model="peerName" class="input min-w-0 flex-1" required maxlength="128" :aria-label="t('node.setup.peerName')"
          :placeholder="t('node.setup.peerName')" :disabled="blocked" />
        <button class="btn btn-secondary" type="submit" :disabled="blocked || !peerName.trim()">{{ t('node.setup.createCredential') }}</button>
      </form>
      <div v-if="peers.length" class="overflow-x-auto">
        <table class="w-full text-sm text-left">
          <thead class="text-gray-500 border-b border-dark-border"><tr><th class="py-2">{{ t('node.setup.peerName') }}</th><th class="py-2">{{ t('node.setup.nodeId') }}</th><th class="py-2">{{ t('settings.status') }}</th></tr></thead>
          <tbody class="divide-y divide-dark-border">
            <tr v-for="peer in peers" :key="peer.nodeId">
              <td class="py-3 text-gray-300">{{ peer.name }}</td><td class="py-3 font-mono text-xs text-gray-500 break-all">{{ peer.nodeId }}</td>
              <td class="py-3"><button type="button" role="switch" :aria-checked="peer.enabled" :aria-label="t('node.setup.peerAccess')"
                class="relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors" :class="peer.enabled ? 'bg-primary-500' : 'bg-gray-700'"
                :disabled="blocked" @click="setEnabled(peer)"><span class="h-4 w-4 rounded-full bg-white transition-transform" :class="peer.enabled ? 'translate-x-6' : 'translate-x-1'" /></button></td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
    <div v-if="issued" class="fixed inset-0 z-50 bg-black/60 flex items-center justify-center p-4" @click.self="issued = ''">
      <div role="dialog" aria-modal="true" aria-labelledby="node-credential-title" class="bg-dark-card border border-dark-border rounded-lg p-5 max-w-2xl w-full space-y-4">
        <h3 id="node-credential-title" class="font-semibold text-gray-100">{{ t('node.setup.credentialOnce') }}</h3>
        <textarea :value="issued" readonly rows="6" class="input w-full font-mono text-xs break-all" :aria-label="t('node.setup.credential')" />
        <div class="flex justify-end gap-2"><button type="button" class="btn btn-secondary" @click="issued = ''">{{ t('common.close') }}</button><button type="button" class="btn btn-primary" @click="copy">{{ t('common.copy') }}</button></div>
      </div>
    </div>
  </section>
  <Teleport to="body">
    <div v-if="restartConfirm" class="fixed inset-0 z-[100] bg-black/60 flex items-center justify-center p-4"
      @click.self="!busy && (restartConfirm = false)" @keydown.esc="!busy && (restartConfirm = false)">
      <div role="dialog" aria-modal="true" aria-labelledby="node-restart-title" aria-describedby="node-restart-description"
        class="bg-dark-card border border-dark-border rounded-lg p-5 max-w-md w-full space-y-4">
        <h3 id="node-restart-title" class="font-semibold text-gray-100">{{ t('node.setup.restartTitle') }}</h3>
        <p id="node-restart-description" class="text-sm text-gray-400">{{ t('node.setup.restartDescription') }}</p>
        <p v-if="syncHistoryChange" role="note" class="text-sm text-amber-400">{{ t('node.setup.syncHistoryWarning') }}</p>
        <div class="flex justify-end gap-2">
          <button ref="restartCancel" type="button" class="btn btn-secondary" :disabled="busy" @click="restartConfirm = false">{{ t('common.cancel') }}</button>
          <button type="button" class="btn btn-primary" :disabled="blocked" @click="confirmRestart">{{ t('node.setup.restartNow') }}</button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
