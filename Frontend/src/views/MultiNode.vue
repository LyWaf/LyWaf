<script setup lang="ts">
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
    if (!response.success || !response.dashboard) throw new Error(response.message || '当前实例未启用多节点控制器')
    dashboard.value = response.dashboard
    error.value = ''
    const deploymentResponse = await distributionApi.getDeployments()
    if (deploymentResponse.success) deployments.value = deploymentResponse.deployments
  } catch (e: any) {
    error.value = e?.message || '节点状态加载失败'
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
  } catch (e: any) { showError(e?.message || '配置版本加载失败') }
}

const createDeployment = async () => {
  if (!versionId.value) return showError('请选择配置版本')
  if (!targetAll.value && selectedNodes.value.length === 0) return showError('请选择目标节点')
  actionId.value = 'create'
  try {
    const response = await distributionApi.createDeployment({
      versionId: versionId.value,
      nodeIds: targetAll.value ? undefined : selectedNodes.value,
      canaryCount: canaryCount.value,
      batchSize: batchSize.value,
      pauseAfterEachBatch: pauseAfterEachBatch.value,
    })
    if (!response.success) throw new Error(response.message || '创建分发任务失败')
    showSuccess('配置分发任务已创建')
    showCreate.value = false
    view.value = 'deployments'
    await load(true)
  } catch (e: any) { showError(e?.message || '创建分发任务失败') }
  finally { actionId.value = '' }
}

const runCommand = async (deployment: ConfigurationDeployment, command: 'pause' | 'resume' | 'rollback') => {
  if (command === 'rollback' && !confirm('确认回滚该任务已提交的节点？')) return
  actionId.value = `${deployment.id}:${command}`
  try {
    const response = await distributionApi.command(deployment.id, command)
    if (!response.success) throw new Error(response.message || '操作失败')
    showSuccess('任务状态已更新')
    await load(true)
  } catch (e: any) { showError(e?.message || '操作失败') }
  finally { actionId.value = '' }
}

const formatTime = (value?: string | null) => value ? new Date(value).toLocaleString() : '-'
const formatUptime = (seconds = 0) => `${Math.floor(seconds / 86400)}天 ${Math.floor(seconds % 86400 / 3600)}时`
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
        <h1 class="text-2xl font-semibold text-gray-100">节点管理</h1>
        <p class="text-sm text-gray-500 mt-1">集中查看节点版本、上游健康、安全事件和配置分发状态。</p>
      </div>
      <div class="flex flex-wrap items-center gap-2 sm:justify-end">
        <button class="btn btn-secondary" :disabled="loading" title="立即刷新所有节点状态" @click="load()">
          {{ loading ? '刷新中...' : '刷新' }}
        </button>
        <button class="btn btn-primary" :disabled="!operableNodes.length" title="从已保存版本创建配置分发任务" @click="openCreate">
          创建分发任务
        </button>
      </div>
    </div>

    <div v-if="error" class="border border-red-800 bg-red-950/30 px-4 py-3 text-sm text-red-300 rounded">
      {{ error }}
    </div>

    <div v-if="dashboard" class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 border border-dark-border rounded overflow-hidden sm:divide-x divide-dark-border">
      <div class="bg-dark-card px-4 py-3"><div class="text-xs text-gray-500">在线节点</div><div class="text-xl text-gray-100 mt-1">{{ onlineCount }} / {{ dashboard.nodes.length }}</div></div>
      <div class="bg-dark-card px-4 py-3"><div class="text-xs text-gray-500">异常上游</div><div class="text-xl mt-1" :class="unhealthyCount ? 'text-red-400' : 'text-emerald-400'">{{ unhealthyCount }}</div></div>
      <div class="bg-dark-card px-4 py-3"><div class="text-xs text-gray-500">累计拦截</div><div class="text-xl text-gray-100 mt-1">{{ dashboard.security.totalInterceptCount.toLocaleString() }}</div></div>
      <div class="bg-dark-card px-4 py-3"><div class="text-xs text-gray-500">攻击来源</div><div class="text-xl text-gray-100 mt-1">{{ dashboard.security.uniqueAttackIps.toLocaleString() }}</div></div>
    </div>

    <div class="flex items-center border-b border-dark-border overflow-x-auto">
      <button v-for="item in ([['nodes','节点'],['upstreams','上游健康'],['events','安全事件'],['deployments','分发任务']] as const)"
        :key="item[0]" class="shrink-0 px-4 py-2.5 text-sm border-b-2 transition-colors"
        :class="view === item[0] ? 'text-primary-400 border-primary-500' : 'text-gray-500 border-transparent hover:text-gray-300'"
        @click="view = item[0]">{{ item[1] }}</button>
    </div>

    <div v-if="dashboard && view === 'nodes'" class="border border-dark-border rounded overflow-x-auto">
      <table class="w-full min-w-[900px] text-sm">
        <thead class="bg-dark-card text-gray-500"><tr><th class="px-4 py-3 text-left">节点</th><th class="px-4 py-3 text-left">状态</th><th class="px-4 py-3 text-left">程序 / 配置版本</th><th class="px-4 py-3 text-left">运行状态</th><th class="px-4 py-3 text-left">时钟偏差</th><th class="px-4 py-3 text-left">最后在线</th><th class="px-4 py-3 text-left">权限</th></tr></thead>
        <tbody class="divide-y divide-dark-border">
          <tr v-for="node in dashboard.nodes" :key="node.nodeId" class="bg-dark-bg-secondary">
            <td class="px-4 py-3"><div class="font-mono text-gray-200">{{ node.nodeId }}</div><div class="text-xs text-gray-600 mt-1">{{ node.endpoint }}</div><div class="flex gap-1 mt-1"><span v-for="tag in node.tags" :key="tag" class="text-[11px] px-1.5 py-0.5 bg-gray-800 text-gray-400 rounded">{{ tag }}</span></div></td>
            <td class="px-4 py-3"><span class="px-2 py-1 text-xs rounded" :class="node.online ? 'bg-emerald-900/50 text-emerald-400' : 'bg-red-950 text-red-400'">{{ node.online ? '在线' : node.stale ? '离线（缓存）' : '离线' }}</span><div v-if="node.error" class="text-xs text-red-400 mt-1 max-w-56 truncate" :title="node.error">{{ node.error }}</div></td>
            <td class="px-4 py-3 font-mono text-xs text-gray-400"><div>{{ node.snapshot?.applicationVersion || '-' }}</div><div class="mt-1">{{ node.snapshot?.configVersionId || '无配置版本' }}</div></td>
            <td class="px-4 py-3 text-gray-400"><div>{{ node.snapshot?.state || '-' }}</div><div class="text-xs text-gray-600 mt-1">{{ formatUptime(node.snapshot?.uptimeSeconds) }} · {{ node.snapshot?.runtimeStateEntries || 0 }} 状态</div></td>
            <td class="px-4 py-3" :class="node.clockSkewWarning ? 'text-amber-400' : 'text-gray-400'">{{ node.clockSkewSeconds == null ? '-' : `${node.clockSkewSeconds.toFixed(1)} 秒` }}<div v-if="node.clockSkewWarning" class="text-xs mt-1">超出允许范围</div></td>
            <td class="px-4 py-3 text-gray-400">{{ formatTime(node.lastSeenAt) }}</td>
            <td class="px-4 py-3"><span :class="node.canOperate ? 'text-emerald-400' : 'text-gray-600'">{{ node.canOperate ? '可操作' : '只读' }}</span></td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-if="dashboard && view === 'upstreams'" class="border border-dark-border rounded overflow-x-auto">
      <table class="w-full text-sm"><thead class="bg-dark-card text-gray-500"><tr><th class="px-4 py-3 text-left">节点 / 集群</th><th class="px-4 py-3 text-left">目标</th><th class="px-4 py-3 text-left">地址</th><th class="px-4 py-3 text-left">主动健康</th><th class="px-4 py-3 text-left">被动健康</th><th class="px-4 py-3 text-right">并发</th></tr></thead>
        <tbody class="divide-y divide-dark-border"><tr v-for="item in upstreams" :key="`${item.node.nodeId}:${item.upstream.clusterId}:${item.upstream.destinationId}`" class="bg-dark-bg-secondary"><td class="px-4 py-3"><div>{{ item.node.nodeId }}</div><div class="text-xs text-gray-600 font-mono">{{ item.upstream.clusterId }}</div></td><td class="px-4 py-3 font-mono text-gray-300">{{ item.upstream.destinationId }}</td><td class="px-4 py-3 font-mono text-xs text-gray-400">{{ item.upstream.address }}</td><td class="px-4 py-3 text-gray-400">{{ item.upstream.activeHealth }}</td><td class="px-4 py-3 text-gray-400">{{ item.upstream.passiveHealth }}</td><td class="px-4 py-3 text-right text-gray-300">{{ item.upstream.concurrentRequests }}</td></tr><tr v-if="!upstreams.length"><td colspan="6" class="px-4 py-10 text-center text-gray-600">暂无上游状态</td></tr></tbody>
      </table>
    </div>

    <div v-if="dashboard && view === 'events'" class="border border-dark-border rounded overflow-x-auto">
      <table class="w-full text-sm"><thead class="bg-dark-card text-gray-500"><tr><th class="px-4 py-3 text-left">来源 IP</th><th class="px-4 py-3 text-left">应用</th><th class="px-4 py-3 text-left">规则</th><th class="px-4 py-3 text-left">类型</th><th class="px-4 py-3 text-right">次数</th><th class="px-4 py-3 text-left">最后发生</th></tr></thead>
        <tbody class="divide-y divide-dark-border"><tr v-for="event in dashboard.recentEvents" :key="event.eventId" class="bg-dark-bg-secondary"><td class="px-4 py-3 font-mono text-gray-300">{{ event.sourceIp }}</td><td class="px-4 py-3 text-gray-400">{{ event.application }}</td><td class="px-4 py-3 text-gray-300">{{ event.ruleName }}</td><td class="px-4 py-3 text-gray-400">{{ event.ruleType }}</td><td class="px-4 py-3 text-right text-gray-300">{{ event.hitCount }}</td><td class="px-4 py-3 text-gray-400">{{ formatTime(event.lastHitAt) }}</td></tr><tr v-if="!dashboard.recentEvents.length"><td colspan="6" class="px-4 py-10 text-center text-gray-600">暂无安全事件</td></tr></tbody>
      </table>
    </div>

    <div v-if="dashboard && view === 'deployments'" class="border border-dark-border rounded overflow-x-auto">
      <div v-if="deployments.some(task => task.taskId)" class="flex flex-wrap gap-3 border-b border-dark-border px-4 py-3">
        <RouterLink v-for="task in deployments.filter(item => item.taskId)" :key="task.id"
          :to="{ path: '/management-tasks', query: { task: task.taskId } }"
          class="text-sm text-primary-400 hover:underline" :title="task.id">
          任务日志 {{ task.id.slice(0, 8) }}
        </RouterLink>
      </div>
      <table class="w-full text-sm"><thead class="bg-dark-card text-gray-500"><tr><th class="px-4 py-3 text-left">任务</th><th class="px-4 py-3 text-left">配置版本</th><th class="px-4 py-3 text-left">节点进度</th><th class="px-4 py-3 text-left">状态</th><th class="px-4 py-3 text-right">操作</th></tr></thead>
        <tbody class="divide-y divide-dark-border"><tr v-for="task in deployments" :key="task.id" class="bg-dark-bg-secondary"><td class="px-4 py-3"><div class="font-mono text-xs text-gray-300">{{ task.id }}</div><div class="text-xs text-gray-600 mt-1">{{ formatTime(task.createdAt) }}</div></td><td class="px-4 py-3 font-mono text-xs text-gray-400">{{ task.versionId }}</td><td class="px-4 py-3"><div class="flex flex-wrap gap-1"><span v-for="target in Object.values(task.targets)" :key="target.nodeId" class="px-1.5 py-0.5 rounded text-xs bg-gray-800 text-gray-400" :title="target.error || ''">{{ target.nodeId }}: {{ target.state }}</span></div></td><td class="px-4 py-3 text-gray-300">{{ task.state }}</td><td class="px-4 py-3"><div class="flex justify-end gap-3"><button v-if="task.state === 'Running' || task.state === 'Pausing'" class="text-amber-400 disabled:text-gray-700" :disabled="!nodeCanOperateTask(task) || !!actionId" @click="runCommand(task, 'pause')">暂停</button><button v-if="task.state === 'Paused' || task.state === 'Pausing'" class="text-primary-400 disabled:text-gray-700" :disabled="!nodeCanOperateTask(task) || !!actionId" @click="runCommand(task, 'resume')">继续</button><button class="text-red-400 disabled:text-gray-700" :disabled="!nodeCanOperateTask(task) || !!actionId" @click="runCommand(task, 'rollback')">回滚</button></div></td></tr><tr v-if="!deployments.length"><td colspan="5" class="px-4 py-10 text-center text-gray-600">暂无分发任务</td></tr></tbody>
      </table>
    </div>

    <div v-if="showCreate" class="fixed inset-0 z-50 bg-black/70 flex items-center justify-center p-4" @click.self="showCreate = false">
      <div class="w-full max-w-xl bg-dark-card border border-dark-border rounded shadow-xl">
        <div class="px-5 py-4 border-b border-dark-border flex justify-between"><h2 class="text-lg text-gray-100">创建配置分发任务</h2><button class="text-gray-500 hover:text-gray-200" title="关闭" @click="showCreate = false">×</button></div>
        <div class="p-5 space-y-4">
          <label class="block"><span class="text-sm text-gray-400">配置版本</span><select v-model="versionId" class="input w-full mt-1 font-mono"><option v-for="version in versions" :key="version.id" :value="version.id">{{ version.id }} · {{ formatTime(version.createdAt) }}</option></select></label>
          <label class="flex items-center gap-2 text-sm text-gray-300"><input v-model="targetAll" type="checkbox" :disabled="!canOperateGlobally">全部节点（需要全局权限）</label>
          <div v-if="!targetAll"><div class="text-sm text-gray-400 mb-2">目标节点</div><div class="grid grid-cols-2 gap-2"><label v-for="node in dashboardNodes" :key="node.nodeId" class="flex items-center gap-2 text-sm" :class="node.canOperate ? 'text-gray-300' : 'text-gray-600'"><input v-model="selectedNodes" type="checkbox" :value="node.nodeId" :disabled="!node.canOperate">{{ node.nodeId }}</label></div></div>
          <div class="grid grid-cols-2 gap-4"><label><span class="text-sm text-gray-400">灰度节点数</span><input v-model.number="canaryCount" type="number" min="1" class="input w-full mt-1"></label><label><span class="text-sm text-gray-400">批次大小</span><input v-model.number="batchSize" type="number" min="1" class="input w-full mt-1"></label></div>
          <label class="flex items-center gap-2 text-sm text-gray-300"><input v-model="pauseAfterEachBatch" type="checkbox">每批完成后暂停</label>
        </div>
        <div class="px-5 py-4 border-t border-dark-border flex justify-end gap-2"><button class="btn btn-secondary" @click="showCreate = false">取消</button><button class="btn btn-primary" :disabled="actionId === 'create'" @click="createDeployment">{{ actionId === 'create' ? '创建中...' : '创建任务' }}</button></div>
      </div>
    </div>
  </div>
</template>
