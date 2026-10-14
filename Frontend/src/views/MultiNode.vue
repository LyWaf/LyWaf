<script setup lang="ts">
import { locale } from '@/i18n'
import { t } from '@/i18n'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { configFileApi, distributionApi, type ConfigVersionInfo } from '@/api'
import type { ConfigurationDeployment, MultiNodeDashboard } from '@/types'
import { useToast } from '@/composables/useToast'

type View = 'nodes' | 'upstreams' | 'events' | 'deployments'

const dashboard = ref<MultiNodeDashboard | null>(null)
const deployments = ref<ConfigurationDeployment[]>([])
const versions = ref<ConfigVersionInfo[]>([])
const view = ref<View>('nodes')
const loading = ref(false)
const actionId = ref('')
const error = ref('')
const showCreate = ref(false)
const versionId = ref('')
const selectedNodes = ref<string[]>([])
const targetAll = ref(false)
const canaryCount = ref(1)
const batchSize = ref(10)
const pauseAfterEachBatch = ref(true)
const { showError, showSuccess } = useToast()
let timer: number | undefined

const onlineCount = computed(() => dashboard.value?.nodes.filter(node => node.online).length ?? 0)
const unhealthyCount = computed(() => dashboard.value?.nodes.flatMap(node => node.snapshot?.upstreams ?? [])
  .filter(item => item.activeHealth === 'Unhealthy' || item.passiveHealth === 'Unhealthy').length ?? 0)
const upstreams = computed(() => dashboard.value?.nodes.flatMap(node =>
  (node.snapshot?.upstreams ?? []).map(upstream => ({ node, upstream }))) ?? [])
const operableNodes = computed(() => dashboard.value?.nodes.filter(node => node.canOperate) ?? [])
const dashboardNodes = computed(() => dashboard.value?.nodes ?? [])
const canOperateGlobally = computed(() => dashboard.value?.canOperateGlobally ?? false)

const load = async (silent = false) => {
  if (!silent) loading.value = true
  try {
    const response = await distributionApi.getDashboard()
    if (!response.success || !response.dashboard) throw new Error(response.message || t('page.cb92a7006b88'))
    dashboard.value = response.dashboard
    error.value = ''
    const deploymentResponse = await distributionApi.getDeployments()
    if (deploymentResponse.success) deployments.value = deploymentResponse.deployments
  } catch (e: any) {
    error.value = e?.message || t('page.67c7507b61b3')
  } finally {
    loading.value = false
  }
}

const openCreate = async () => {
  try {
    versions.value = (await configFileApi.getAllHistory()).filter(item => item.kind === 'main')
    versionId.value ||= versions.value[0]?.id ?? ''
    selectedNodes.value = operableNodes.value.map(node => node.nodeId)
    showCreate.value = true
  } catch (e: any) { showError(e?.message || t('page.5814e5c2fef4')) }
}

const createDeployment = async () => {
  if (!versionId.value) return showError(t('page.576ed3998142'))
  if (!targetAll.value && selectedNodes.value.length === 0) return showError(t('page.37f73782aba8'))
  actionId.value = 'create'
  try {
    const response = await distributionApi.createDeployment({
      versionId: versionId.value,
      nodeIds: targetAll.value ? undefined : selectedNodes.value,
      canaryCount: canaryCount.value,
      batchSize: batchSize.value,
      pauseAfterEachBatch: pauseAfterEachBatch.value,
    })
    if (!response.success) throw new Error(response.message || t('page.785ef93f83f6'))
    showSuccess(t('page.b2e2f1435592'))
    showCreate.value = false
    view.value = 'deployments'
    await load(true)
  } catch (e: any) { showError(e?.message || t('page.785ef93f83f6')) }
  finally { actionId.value = '' }
}

const runCommand = async (deployment: ConfigurationDeployment, command: 'pause' | 'resume' | 'rollback') => {
  if (command === 'rollback' && !confirm(t('page.0de46aa44ade'))) return
  actionId.value = `${deployment.id}:${command}`
  try {
    const response = await distributionApi.command(deployment.id, command)
    if (!response.success) throw new Error(response.message || t('settings.operation.failed'))
    showSuccess(t('page.d09df6f2d955'))
    await load(true)
  } catch (e: any) { showError(e?.message || t('settings.operation.failed')) }
  finally { actionId.value = '' }
}

const formatTime = (value?: string | null) => value ? new Date(value).toLocaleString(locale.value) : '-'
const formatUptime = (seconds = 0) => t('page.c949c765bb67', { p0: Math.floor(seconds / 86400), p1: Math.floor(seconds % 86400 / 3600) })
const nodeCanOperateTask = (task: ConfigurationDeployment) => task.nodeOrder.every(id =>
  dashboard.value?.nodes.find(node => node.nodeId === id)?.canOperate)

onMounted(() => {
  load()
  timer = window.setInterval(() => load(true), 15000)
})
onBeforeUnmount(() => timer && window.clearInterval(timer))
</script>

<template>
  <div class="space-y-5">
    <div class="flex flex-col sm:flex-row sm:items-start sm:justify-between gap-4">
      <div>
        <h1 class="text-2xl font-semibold text-gray-100">{{ t('nav.nodes') }}</h1>
        <p class="text-sm text-gray-500 mt-1">{{ t('page.09bf2e0a6d90') }}</p>
      </div>
      <div class="flex flex-wrap items-center gap-2 sm:justify-end">
        <button class="btn btn-secondary" :disabled="loading" :title="t('page.b00509deaace')" @click="load()">
          {{ loading ? t('ui.refreshing') : t('common.refresh') }}
        </button>
        <button class="btn btn-primary" :disabled="!operableNodes.length" :title="t('page.f8f418e0bf4e')" @click="openCreate">
          {{ t('page.fb37740bf99e') }}
        </button>
      </div>
    </div>

    <div v-if="error" class="border border-red-800 bg-red-950/30 px-4 py-3 text-sm text-red-300 rounded">
      {{ error }}
    </div>

    <div v-if="dashboard" class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 border border-dark-border rounded overflow-hidden sm:divide-x divide-dark-border">
      <div class="bg-dark-card px-4 py-3"><div class="text-xs text-gray-500">{{ t('page.1ef941b4e346') }}</div><div class="text-xl text-gray-100 mt-1">{{ onlineCount }} / {{ dashboard.nodes.length }}</div></div>
      <div class="bg-dark-card px-4 py-3"><div class="text-xs text-gray-500">{{ t('page.08d417edefcd') }}</div><div class="text-xl mt-1" :class="unhealthyCount ? 'text-red-400' : 'text-emerald-400'">{{ unhealthyCount }}</div></div>
      <div class="bg-dark-card px-4 py-3"><div class="text-xs text-gray-500">{{ t('page.16287d49c55b') }}</div><div class="text-xl text-gray-100 mt-1">{{ dashboard.security.totalInterceptCount.toLocaleString(locale) }}</div></div>
      <div class="bg-dark-card px-4 py-3"><div class="text-xs text-gray-500">{{ t('page.74912f87becc') }}</div><div class="text-xl text-gray-100 mt-1">{{ dashboard.security.uniqueAttackIps.toLocaleString(locale) }}</div></div>
    </div>

    <div class="flex items-center border-b border-dark-border overflow-x-auto">
      <button v-for="item in ([['nodes',t('page.2410d860f093')],['upstreams',t('page.cf56992d35f9')],['events',t('page.971f019596ec')],['deployments',t('page.c8444b75c6c4')]] as const)"
        :key="item[0]" class="shrink-0 px-4 py-2.5 text-sm border-b-2 transition-colors"
        :class="view === item[0] ? 'text-primary-400 border-primary-500' : 'text-gray-500 border-transparent hover:text-gray-300'"
        @click="view = item[0]">{{ item[1] }}</button>
    </div>

    <div v-if="dashboard && view === 'nodes'" class="border border-dark-border rounded overflow-x-auto">
      <table class="w-full min-w-[900px] text-sm">
        <thead class="bg-dark-card text-gray-500"><tr><th class="px-4 py-3 text-left">{{ t('page.2410d860f093') }}</th><th class="px-4 py-3 text-left">{{ t('settings.status') }}</th><th class="px-4 py-3 text-left">{{ t('page.7bc40b49398a') }}</th><th class="px-4 py-3 text-left">{{ t('page.e46b0272bd64') }}</th><th class="px-4 py-3 text-left">{{ t('page.d83c9566d440') }}</th><th class="px-4 py-3 text-left">{{ t('page.e2b38c85a2a6') }}</th><th class="px-4 py-3 text-left">{{ t('page.978cbca6265d') }}</th></tr></thead>
        <tbody class="divide-y divide-dark-border">
          <tr v-for="node in dashboard.nodes" :key="node.nodeId" class="bg-dark-bg-secondary">
            <td class="px-4 py-3"><div class="font-mono text-gray-200">{{ node.nodeId }}</div><div class="text-xs text-gray-600 mt-1">{{ node.endpoint }}</div><div class="flex gap-1 mt-1"><span v-for="tag in node.tags" :key="tag" class="text-[11px] px-1.5 py-0.5 bg-gray-800 text-gray-400 rounded">{{ tag }}</span></div></td>
            <td class="px-4 py-3"><span class="px-2 py-1 text-xs rounded" :class="node.online ? 'bg-emerald-900/50 text-emerald-400' : 'bg-red-950 text-red-400'">{{ node.online ? t('page.b9086662b1df') : node.stale ? t('page.a00963a24f1b') : t('page.be1b4f3c6c1c') }}</span><div v-if="node.error" class="text-xs text-red-400 mt-1 max-w-56 truncate" :title="node.error">{{ node.error }}</div></td>
            <td class="px-4 py-3 font-mono text-xs text-gray-400"><div>{{ node.snapshot?.applicationVersion || '-' }}</div><div class="mt-1">{{ node.snapshot?.configVersionId || t('page.7dfdf51d2277') }}</div></td>
            <td class="px-4 py-3 text-gray-400"><div>{{ node.snapshot?.state || '-' }}</div><div class="text-xs text-gray-600 mt-1">{{ formatUptime(node.snapshot?.uptimeSeconds) }} · {{ node.snapshot?.runtimeStateEntries || 0 }} {{ t('settings.status') }}</div></td>
            <td class="px-4 py-3" :class="node.clockSkewWarning ? 'text-amber-400' : 'text-gray-400'">{{ node.clockSkewSeconds == null ? '-' : t('page.67d54f9c74b7', { p0: node.clockSkewSeconds.toFixed(1) }) }}<div v-if="node.clockSkewWarning" class="text-xs mt-1">{{ t('page.a504a2e99cdb') }}</div></td>
            <td class="px-4 py-3 text-gray-400">{{ formatTime(node.lastSeenAt) }}</td>
            <td class="px-4 py-3"><span :class="node.canOperate ? 'text-emerald-400' : 'text-gray-600'">{{ node.canOperate ? t('page.5adc58e04910') : t('page.3b5ec3533b0e') }}</span></td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-if="dashboard && view === 'upstreams'" class="border border-dark-border rounded overflow-x-auto">
      <table class="w-full text-sm"><thead class="bg-dark-card text-gray-500"><tr><th class="px-4 py-3 text-left">{{ t('page.23d3be81e2ba') }}</th><th class="px-4 py-3 text-left">{{ t('ui.destination') }}</th><th class="px-4 py-3 text-left">{{ t('page.37aac5b4e639') }}</th><th class="px-4 py-3 text-left">{{ t('page.9df99c1f9986') }}</th><th class="px-4 py-3 text-left">{{ t('page.0365b54d847a') }}</th><th class="px-4 py-3 text-right">{{ t('page.2b66a2b53a76') }}</th></tr></thead>
        <tbody class="divide-y divide-dark-border"><tr v-for="item in upstreams" :key="`${item.node.nodeId}:${item.upstream.clusterId}:${item.upstream.destinationId}`" class="bg-dark-bg-secondary"><td class="px-4 py-3"><div>{{ item.node.nodeId }}</div><div class="text-xs text-gray-600 font-mono">{{ item.upstream.clusterId }}</div></td><td class="px-4 py-3 font-mono text-gray-300">{{ item.upstream.destinationId }}</td><td class="px-4 py-3 font-mono text-xs text-gray-400">{{ item.upstream.address }}</td><td class="px-4 py-3 text-gray-400">{{ item.upstream.activeHealth }}</td><td class="px-4 py-3 text-gray-400">{{ item.upstream.passiveHealth }}</td><td class="px-4 py-3 text-right text-gray-300">{{ item.upstream.concurrentRequests }}</td></tr><tr v-if="!upstreams.length"><td colspan="6" class="px-4 py-10 text-center text-gray-600">{{ t('page.f38f0b0c8d9f') }}</td></tr></tbody>
      </table>
    </div>

    <div v-if="dashboard && view === 'events'" class="border border-dark-border rounded overflow-x-auto">
      <table class="w-full text-sm"><thead class="bg-dark-card text-gray-500"><tr><th class="px-4 py-3 text-left">{{ t('page.4bf0843a4518') }}</th><th class="px-4 py-3 text-left">{{ t('page.63c73c4730f4') }}</th><th class="px-4 py-3 text-left">{{ t('fields.Rules') }}</th><th class="px-4 py-3 text-left">{{ t('settings.type') }}</th><th class="px-4 py-3 text-right">{{ t('page.05c518eb94a0') }}</th><th class="px-4 py-3 text-left">{{ t('page.b4f894efed07') }}</th></tr></thead>
        <tbody class="divide-y divide-dark-border"><tr v-for="event in dashboard.recentEvents" :key="event.eventId" class="bg-dark-bg-secondary"><td class="px-4 py-3 font-mono text-gray-300">{{ event.sourceIp }}</td><td class="px-4 py-3 text-gray-400">{{ event.application }}</td><td class="px-4 py-3 text-gray-300">{{ event.ruleName }}</td><td class="px-4 py-3 text-gray-400">{{ event.ruleType }}</td><td class="px-4 py-3 text-right text-gray-300">{{ event.hitCount }}</td><td class="px-4 py-3 text-gray-400">{{ formatTime(event.lastHitAt) }}</td></tr><tr v-if="!dashboard.recentEvents.length"><td colspan="6" class="px-4 py-10 text-center text-gray-600">{{ t('page.ee9496421c55') }}</td></tr></tbody>
      </table>
    </div>

    <div v-if="dashboard && view === 'deployments'" class="border border-dark-border rounded overflow-x-auto">
      <div v-if="deployments.some(task => task.taskId)" class="flex flex-wrap gap-3 border-b border-dark-border px-4 py-3">
        <RouterLink v-for="task in deployments.filter(item => item.taskId)" :key="task.id"
          :to="{ path: '/management-tasks', query: { task: task.taskId } }"
          class="text-sm text-primary-400 hover:underline" :title="task.id">
          {{ t('page.325d4c93af0f') }} {{ task.id.slice(0, 8) }}
        </RouterLink>
      </div>
      <table class="w-full text-sm"><thead class="bg-dark-card text-gray-500"><tr><th class="px-4 py-3 text-left">{{ t('ui.task') }}</th><th class="px-4 py-3 text-left">{{ t('page.feb7d97d77a4') }}</th><th class="px-4 py-3 text-left">{{ t('page.99f94d299217') }}</th><th class="px-4 py-3 text-left">{{ t('settings.status') }}</th><th class="px-4 py-3 text-right">{{ t('settings.actions') }}</th></tr></thead>
        <tbody class="divide-y divide-dark-border"><tr v-for="task in deployments" :key="task.id" class="bg-dark-bg-secondary"><td class="px-4 py-3"><div class="font-mono text-xs text-gray-300">{{ task.id }}</div><div class="text-xs text-gray-600 mt-1">{{ formatTime(task.createdAt) }}</div></td><td class="px-4 py-3 font-mono text-xs text-gray-400">{{ task.versionId }}</td><td class="px-4 py-3"><div class="flex flex-wrap gap-1"><span v-for="target in Object.values(task.targets)" :key="target.nodeId" class="px-1.5 py-0.5 rounded text-xs bg-gray-800 text-gray-400" :title="target.error || ''">{{ target.nodeId }}: {{ target.state }}</span></div></td><td class="px-4 py-3 text-gray-300">{{ task.state }}</td><td class="px-4 py-3"><div class="flex justify-end gap-3"><button v-if="task.state === 'Running' || task.state === 'Pausing'" class="text-amber-400 disabled:text-gray-700" :disabled="!nodeCanOperateTask(task) || !!actionId" @click="runCommand(task, 'pause')">{{ t('page.8d12fc0d4eb2') }}</button><button v-if="task.state === 'Paused' || task.state === 'Pausing'" class="text-primary-400 disabled:text-gray-700" :disabled="!nodeCanOperateTask(task) || !!actionId" @click="runCommand(task, 'resume')">{{ t('page.7c9691192f1b') }}</button><button class="text-red-400 disabled:text-gray-700" :disabled="!nodeCanOperateTask(task) || !!actionId" @click="runCommand(task, 'rollback')">{{ t('plugins.rollback') }}</button></div></td></tr><tr v-if="!deployments.length"><td colspan="5" class="px-4 py-10 text-center text-gray-600">{{ t('page.44da6b1d4b94') }}</td></tr></tbody>
      </table>
    </div>

    <div v-if="showCreate" class="fixed inset-0 z-50 bg-black/70 flex items-center justify-center p-4" @click.self="showCreate = false">
      <div class="w-full max-w-xl bg-dark-card border border-dark-border rounded shadow-xl">
        <div class="px-5 py-4 border-b border-dark-border flex justify-between"><h2 class="text-lg text-gray-100">{{ t('page.5098f973579f') }}</h2><button class="text-gray-500 hover:text-gray-200" :title="t('common.close')" @click="showCreate = false">×</button></div>
        <div class="p-5 space-y-4">
          <label class="block"><span class="text-sm text-gray-400">{{ t('page.feb7d97d77a4') }}</span><select v-model="versionId" class="input w-full mt-1 font-mono"><option v-for="version in versions" :key="version.id" :value="version.id">{{ version.id }} · {{ formatTime(version.createdAt) }}</option></select></label>
          <label class="flex items-center gap-2 text-sm text-gray-300"><input v-model="targetAll" type="checkbox" :disabled="!canOperateGlobally">{{ t('page.e891b1321b64') }}</label>
          <div v-if="!targetAll"><div class="text-sm text-gray-400 mb-2">{{ t('page.36a7f36ef8e8') }}</div><div class="grid grid-cols-2 gap-2"><label v-for="node in dashboardNodes" :key="node.nodeId" class="flex items-center gap-2 text-sm" :class="node.canOperate ? 'text-gray-300' : 'text-gray-600'"><input v-model="selectedNodes" type="checkbox" :value="node.nodeId" :disabled="!node.canOperate">{{ node.nodeId }}</label></div></div>
          <div class="grid grid-cols-2 gap-4"><label><span class="text-sm text-gray-400">{{ t('page.b17932392127') }}</span><input v-model.number="canaryCount" type="number" min="1" class="input w-full mt-1"></label><label><span class="text-sm text-gray-400">{{ t('page.bbb86292b89f') }}</span><input v-model.number="batchSize" type="number" min="1" class="input w-full mt-1"></label></div>
          <label class="flex items-center gap-2 text-sm text-gray-300"><input v-model="pauseAfterEachBatch" type="checkbox">{{ t('page.1963434cd74c') }}</label>
        </div>
        <div class="px-5 py-4 border-t border-dark-border flex justify-end gap-2"><button class="btn btn-secondary" @click="showCreate = false">{{ t('common.cancel') }}</button><button class="btn btn-primary" :disabled="actionId === 'create'" @click="createDeployment">{{ actionId === 'create' ? t('page.687d8f3ba99e') : t('ui.create.task') }}</button></div>
      </div>
    </div>
  </div>
</template>
