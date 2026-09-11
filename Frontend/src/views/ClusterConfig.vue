<script setup lang="ts">
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import ListToolbar from '@/components/common/ListToolbar.vue'
import { lbApi } from '@/api'
import { useToast } from '@/composables/useToast'
import type { LbCluster, LbDestination, LbPolicy, OutlierDestination } from '@/types'

const { showSuccess, showError } = useToast()

// 从 axios 错误中提取服务端 message
const getErrorMessage = (error: unknown, fallback: string): string => {
  if (error && typeof error === 'object' && 'response' in error) {
    const res = (error as { response?: { data?: { message?: string } } }).response
    if (res?.data?.message) return res.data.message
  }
  return fallback
}

// 数据状态
const loading = ref(false)
const clusters = ref<Array<Omit<LbCluster, 'destinations'> & { hasPatch: boolean }>>([])
const policies = ref<LbPolicy[]>([])
const outliers = ref<OutlierDestination[]>([])
const selectedClusterId = ref('')
const selectedDestIds = ref<Set<string>>(new Set())
const page = ref(1)
const search = ref('')
const total = ref(0)
const destinations = ref<Array<LbDestination & { outlier?: OutlierDestination }>>([])
let destinationRequest = 0
let configRequest = 0
onBeforeUnmount(() => { destinationRequest++; configRequest++ })

// 对话框状态
const showDialog = ref(false)
const dialogMode = ref<'add' | 'edit'>('add')
const saving = ref(false)
const showClusterDialog = ref(false)
const clusterForm = ref({ id: '', policy: 'RoundRobin', address: '' })
const clusterError = ref('')
const createCluster = async () => {
  saving.value = true
  clusterError.value = ''
  try {
    const res = await lbApi.createCluster(clusterForm.value)
    if (!res.success) { clusterError.value = res.message || '创建失败'; return }
    selectedClusterId.value = clusterForm.value.id
    page.value = 1; search.value = ''
    showClusterDialog.value = false
    await loadData()
    showSuccess('集群已创建')
  } catch (e) { clusterError.value = getErrorMessage(e, '创建集群失败') }
  finally { saving.value = false }
}

// 表单数据
const form = ref({
  destinationId: '',
  ip: '',
  port: 8001,
  scheme: 'http',
  host: '*',
  weight: '1',
  maxFails: '2',
  timeout: '180',
})

// 编辑中的原始ID（用于标识编辑时原来的key）
const editingOriginalId = ref('')

// 计算当前选中的集群
const currentCluster = computed(() =>
  clusters.value.find(c => c.id === selectedClusterId.value)
)

const getOutlier = (destinationId: string) => outliers.value.find(
  item => item.clusterId === selectedClusterId.value && item.destinationId === destinationId
)

const outlierReason = (reason?: string) => ({
  consecutive_connection_failures: '连续连接失败',
  consecutive_5xx: '连续 5xx',
  error_rate: '错误率过高',
}[reason || ''] || reason || '')

// 当前集群是否存在补丁数据
const hasPatch = computed(() =>
  currentCluster.value?.hasPatch ?? false
)

// 是否全选
const allSelected = computed(() =>
  destinations.value.length > 0 && selectedDestIds.value.size === destinations.value.length
)

const loadDestinations = async () => {
  const request = ++destinationRequest
  selectedDestIds.value.clear()
  if (!selectedClusterId.value) { destinations.value = []; total.value = 0; return }
  loading.value = true
  try {
    const res = await lbApi.destinations(selectedClusterId.value, page.value, search.value)
    if (request !== destinationRequest) return
    destinations.value = res.data
    total.value = res.total
    outliers.value = res.data.flatMap(item => item.outlier ? [item.outlier] : [])
    if (!res.data.length && page.value > 1) {
      page.value = Math.max(1, Math.ceil(res.total / 20))
      await loadDestinations()
    }
  } catch {
    if (request === destinationRequest) showError('加载目标服务器失败')
  } finally {
    if (request === destinationRequest) loading.value = false
  }
}

// 加载数据
const loadData = async () => {
  const request = ++configRequest
  loading.value = true
  try {
    const [clustersRes, policiesRes] = await Promise.all([
      lbApi.getClusterOptions(),
      lbApi.getPolicies(),
    ])
    if (request !== configRequest) return
    clusters.value = clustersRes
    policies.value = policiesRes.policies
    if (!clusters.value.some(item => item.id === selectedClusterId.value)) {
      selectedClusterId.value = clusters.value[0]?.id || ''
      page.value = 1
    }
    await loadDestinations()
  } catch (error) {
    if (request === configRequest) showError('加载集群配置失败')
  } finally {
    if (request === configRequest) loading.value = false
  }
}

// 切换集群
const switchCluster = (clusterId: string) => {
  selectedClusterId.value = clusterId
  page.value = 1
  search.value = ''
  destinations.value = []
  loadDestinations()
}

// 更新策略
const updatePolicy = async (policy: string) => {
  if (!selectedClusterId.value) return
  try {
    const res = await lbApi.updatePolicy(selectedClusterId.value, policy)
    if (res.success) {
      showSuccess('集群策略已更新')
      await loadData()
    } else {
      showError(res.message || '更新策略失败')
    }
  } catch (error) {
    showError(getErrorMessage(error, '更新策略失败'))
  }
}

// 打开新增对话框
const openAddDialog = () => {
  dialogMode.value = 'add'
  form.value = {
    destinationId: '',
    ip: '',
    port: 8001,
    scheme: 'http',
    host: '*',
    weight: '1',
    maxFails: '2',
    timeout: '180',
  }
  editingOriginalId.value = ''
  showDialog.value = true
}

// 打开编辑对话框
const openEditDialog = (dest: LbDestination) => {
  dialogMode.value = 'edit'
  editingOriginalId.value = dest.id
  form.value = {
    destinationId: dest.id,
    ip: dest.host,
    port: dest.port || 80,
    scheme: dest.scheme || 'http',
    host: dest.metadata?.Host || '*',
    weight: dest.metadata?.Weight || '1',
    maxFails: dest.metadata?.MaxFails || '2',
    timeout: dest.metadata?.Timeout || '180',
  }
  showDialog.value = true
}

// 保存（新增/编辑）
const handleSave = async () => {
  if (!selectedClusterId.value) return
  if (!form.value.destinationId || !form.value.ip) {
    showError('名字和 IP 不能为空')
    return
  }

  saving.value = true
  try {
    const address = `${form.value.scheme}://${form.value.ip}:${form.value.port}`
    const metadata: Record<string, string> = {}
    if (form.value.weight) metadata.Weight = form.value.weight
    if (form.value.maxFails) metadata.MaxFails = form.value.maxFails
    if (form.value.timeout) metadata.Timeout = form.value.timeout
    if (form.value.host && form.value.host !== '*') metadata.Host = form.value.host

    let res
    if (dialogMode.value === 'add') {
      res = await lbApi.addDestination({
        clusterId: selectedClusterId.value,
        destinationId: form.value.destinationId,
        address,
        metadata: Object.keys(metadata).length > 0 ? metadata : undefined,
      })
    } else {
      res = await lbApi.updateDestination({
        clusterId: selectedClusterId.value,
        destinationId: editingOriginalId.value,
        address,
        metadata: Object.keys(metadata).length > 0 ? metadata : undefined,
      })
    }

    if (res.success) {
      showSuccess(dialogMode.value === 'add' ? '目标已添加' : '目标已更新')
      showDialog.value = false
      await loadData()
    } else {
      showError(res.message || '操作失败')
    }
  } catch (error) {
    showError(getErrorMessage(error, '操作失败'))
  }
}

// 删除单个目标
const removeDest = async (dest: LbDestination) => {
  if (!confirm(`确定要删除目标 "${dest.id}" 吗？`)) return
  try {
    const res = await lbApi.removeDestination(selectedClusterId.value, dest.id)
    if (res.success) {
      showSuccess('目标已删除')
      await loadData()
    } else {
      showError(res.message || '删除失败')
    }
  } catch (error) {
    showError(getErrorMessage(error, '删除失败'))
  }
}

// 批量删除
const batchRemove = async () => {
  if (selectedDestIds.value.size === 0) {
    showError('请先选择要删除的目标')
    return
  }
  if (!confirm(`确定要删除选中的 ${selectedDestIds.value.size} 个目标吗？`)) return
  try {
    const res = await lbApi.batchRemoveDestinations(
      selectedClusterId.value,
      Array.from(selectedDestIds.value)
    )
    if (res.success) {
      if (res.allSucceeded) showSuccess(`已删除 ${res.succeeded} 个目标`)
      else showError(`已删除 ${res.succeeded} 个，失败 ${res.failed} 个：${res.results.filter(item => !item.success).map(item => `${item.id}: ${item.message}`).join('；')}`)
      await loadData()
    } else {
      showError(res.message || '批量删除失败')
    }
  } catch (error) {
    showError(getErrorMessage(error, '批量删除失败'))
  }
}

// 删除集群补丁
const removeClusterPatch = async () => {
  if (!selectedClusterId.value) return
  if (!confirm(`确定要删除集群 "${selectedClusterId.value}" 的所有补丁数据吗？这将恢复为原始配置。`)) return
  try {
    const res = await lbApi.removeClusterPatch(selectedClusterId.value)
    if (res.success) {
      showSuccess('补丁已删除，已恢复原始配置')
      await loadData()
    } else {
      showError(res.message || '删除补丁失败')
    }
  } catch (error) {
    showError(getErrorMessage(error, '删除补丁失败'))
  }
}

// 切换选择
const toggleSelect = (destId: string) => {
  if (selectedDestIds.value.has(destId)) {
    selectedDestIds.value.delete(destId)
  } else {
    selectedDestIds.value.add(destId)
  }
}

// 全选/取消全选
const toggleSelectAll = () => {
  if (allSelected.value) {
    selectedDestIds.value.clear()
  } else {
    selectedDestIds.value = new Set(destinations.value.map(d => d.id))
  }
}

onMounted(loadData)
</script>

<template>
  <div class="space-y-6">
    <div v-if="showClusterDialog" class="fixed inset-0 z-50 bg-black/60 flex items-center justify-center p-4">
      <form role="dialog" aria-modal="true" aria-label="添加集群" class="bg-dark-card border border-dark-border rounded-lg p-5 w-full max-w-lg space-y-4" @submit.prevent="createCluster">
        <h2 class="font-medium">添加集群</h2>
        <p v-if="clusterError" role="alert" class="text-red-400 break-all">{{ clusterError }}</p>
        <label class="block">集群 ID<input v-model="clusterForm.id" required pattern="[a-zA-Z0-9_-]{1,100}" maxlength="100" class="input w-full mt-1" /></label>
        <label class="block">负载均衡策略<select v-model="clusterForm.policy" class="input w-full mt-1"><option value="RoundRobin">RoundRobin</option><option v-for="p in policies.filter(p => p.name !== 'RoundRobin')" :key="p.name" :value="p.name">{{ p.name }}</option></select></label>
        <label class="block">首个上游地址<input v-model="clusterForm.address" type="text" required maxlength="2048" placeholder="127.0.0.1:8080" class="input w-full mt-1" /></label>
        <div class="flex justify-end gap-3"><button type="button" :disabled="saving" @click="showClusterDialog = false">取消</button><button :disabled="saving" class="btn btn-primary">{{ saving ? '创建中...' : '创建' }}</button></div>
      </form>
    </div>
    <!-- 集群选择 & 策略配置 -->
    <div class="card">
      <div class="flex items-center justify-between mb-4">
        <h2 class="text-lg font-medium text-gray-100">配置集群</h2>
        <button class="btn btn-primary" @click="clusterForm = { id: '', policy: 'RoundRobin', address: '' }; clusterError = ''; showClusterDialog = true">添加集群</button>
      </div>

      <div class="flex flex-wrap items-center gap-4">
        <!-- 集群选择 -->
        <div class="flex items-center gap-2">
          <span class="text-sm text-gray-400">集群:</span>
          <select
            v-model="selectedClusterId"
            @change="switchCluster(selectedClusterId)"
            class="bg-dark-card border border-dark-border rounded-lg px-3 py-1.5 text-sm text-gray-200 focus:outline-none focus:border-primary-500"
          >
            <option v-for="c in clusters" :key="c.id" :value="c.id">
              {{ c.id }} ({{ c.destinationCount }} 个目标)
            </option>
          </select>
        </div>

        <!-- 策略选择 -->
        <div class="flex flex-wrap sm:flex-nowrap items-center gap-2 min-w-0 max-w-full" v-if="currentCluster">
          <span class="text-sm text-gray-400 shrink-0">负载均衡策略:</span>
          <select
            :value="currentCluster.loadBalancingPolicy"
            @change="updatePolicy(($event.target as HTMLSelectElement).value)"
            class="bg-dark-card border border-dark-border rounded-lg px-3 py-1.5 text-sm text-gray-200 focus:outline-none focus:border-primary-500 min-w-0 max-w-full"
          >
            <option v-for="p in policies" :key="p.name" :value="p.name">
              {{ p.label }} ({{ p.name }})
            </option>
          </select>
        </div>
      </div>
    </div>

    <!-- 目标服务器列表 -->
    <div class="card" v-if="currentCluster">
      <div class="flex flex-wrap items-center justify-between gap-3 mb-4">
        <h2 class="text-lg font-medium text-gray-100">
          目标服务器
          <span class="text-sm text-gray-500 ml-2">({{ total }})</span>
        </h2>
        <div class="flex flex-wrap items-center gap-2">
          <button
            @click="removeClusterPatch"
            :disabled="!hasPatch"
            class="btn btn-sm btn-warning flex items-center gap-1"
            :class="{ 'opacity-50 cursor-not-allowed': !hasPatch }"
          >
            删除补丁
          </button>
          <button @click="openAddDialog" class="btn btn-sm btn-primary flex items-center gap-1">
            <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
            </svg>
            新增
          </button>
          <button
            @click="batchRemove"
            :disabled="selectedDestIds.size === 0"
            class="btn btn-sm btn-danger flex items-center gap-1"
            :class="{ 'opacity-50 cursor-not-allowed': selectedDestIds.size === 0 }"
          >
            <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
            </svg>
            批量删除
          </button>
        </div>
      </div>

      <!-- 表格 -->
      <ListToolbar v-model:page="page" v-model:search="search" :total="total" :busy="loading" @refresh="loadDestinations" />
      <div class="overflow-x-auto">
        <table class="w-full min-w-[640px] text-sm">
          <thead>
            <tr class="border-b border-dark-border text-gray-400">
              <th class="py-3 px-3 text-left w-10">
                <input
                  type="checkbox"
                  :checked="allSelected"
                  @change="toggleSelectAll"
                  class="rounded border-gray-500"
                />
              </th>
              <th class="py-3 px-3 text-left">名字</th>
              <th class="py-3 px-3 text-left">来源</th>
              <th class="py-3 px-3 text-left">IP</th>
              <th class="py-3 px-3 text-left">端口</th>
              <th class="py-3 px-3 text-left">权重</th>
              <th class="py-3 px-3 text-left">运行状态</th>
              <th class="py-3 px-3 text-left">操作</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="dest in destinations"
              :key="dest.id"
              class="border-b border-dark-border/50 hover:bg-white/5 transition-colors"
            >
              <td class="py-3 px-3">
                <input
                  type="checkbox"
                  :checked="selectedDestIds.has(dest.id)"
                  @change="toggleSelect(dest.id)"
                  class="rounded border-gray-500"
                />
              </td>
              <td class="py-3 px-3 text-gray-200">{{ dest.id }}</td>
              <td class="py-3 px-3">
                <span
                  v-if="dest.source === 'patch'"
                  class="inline-block px-1.5 py-0.5 text-xs bg-yellow-500/20 text-yellow-400 rounded"
                >补丁</span>
                <span
                  v-else
                  class="inline-block px-1.5 py-0.5 text-xs bg-gray-500/20 text-gray-400 rounded"
                >默认</span>
              </td>
              <td class="py-3 px-3 text-gray-300 font-mono">{{ dest.host }}</td>
              <td class="py-3 px-3 text-gray-300">{{ dest.port }}</td>
              <td class="py-3 px-3 text-gray-300">{{ dest.metadata?.Weight || '1' }}</td>
              <td class="py-3 px-3">
                <div v-if="getOutlier(dest.id)?.state === 'ejected'" class="space-y-1">
                  <span class="inline-block px-1.5 py-0.5 text-xs bg-red-500/20 text-red-400 rounded">已驱逐</span>
                  <div class="text-xs text-gray-500">{{ outlierReason(getOutlier(dest.id)?.reason) }}</div>
                  <div class="text-xs text-gray-500">恢复于 {{ new Date(getOutlier(dest.id)!.ejectedUntil!).toLocaleString() }}</div>
                </div>
                <span v-else class="inline-block px-1.5 py-0.5 text-xs bg-green-500/20 text-green-400 rounded">可用</span>
              </td>
              <td class="py-3 px-3">
                <div class="flex items-center gap-2">
                  <button
                    @click="openEditDialog(dest)"
                    class="px-3 py-1 text-xs border border-primary-500 text-primary-400 rounded hover:bg-primary-500/10 transition-colors"
                  >
                    编辑
                  </button>
                  <button
                    @click="removeDest(dest)"
                    class="px-3 py-1 text-xs border border-red-500 text-red-400 rounded hover:bg-red-500/10 transition-colors"
                  >
                    删除
                  </button>
                </div>
              </td>
            </tr>
          </tbody>
        </table>

        <!-- 空状态 -->
        <div v-if="destinations.length === 0" class="text-center py-12 text-gray-500">
          暂无目标服务器
        </div>
      </div>
    </div>

    <!-- 加载中 -->
    <div v-if="loading" class="text-center py-12 text-gray-500">
      加载中...
    </div>

    <!-- 无集群 -->
    <div v-if="!loading && clusters.length === 0" class="card text-center py-12 text-gray-500">
      暂无集群配置
    </div>

    <!-- 新增/编辑对话框 -->
    <Teleport to="body">
      <div v-if="showDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
        <!-- 遮罩 -->
        <div class="absolute inset-0 bg-black/60" @click="showDialog = false"></div>

        <!-- 对话框 -->
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-full max-w-lg mx-4 p-6">
          <h3 class="text-lg font-medium text-gray-100 mb-4">
            {{ dialogMode === 'add' ? '新增目标服务器' : '编辑目标服务器' }}
          </h3>

          <div class="space-y-4">
            <!-- 名字 -->
            <div>
              <label class="block text-sm text-gray-400 mb-1">名字 (ID)</label>
              <input
                v-model="form.destinationId"
                type="text"
                :disabled="dialogMode === 'edit'"
                placeholder="如: web后台1"
                class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 focus:outline-none focus:border-primary-500 disabled:opacity-50"
              />
            </div>

            <!-- 协议 + IP + 端口 -->
            <div class="flex gap-3">
              <div class="w-28">
                <label class="block text-sm text-gray-400 mb-1">协议</label>
                <select
                  v-model="form.scheme"
                  class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 focus:outline-none focus:border-primary-500"
                >
                  <option value="http">http</option>
                  <option value="https">https</option>
                </select>
              </div>
              <div class="flex-1">
                <label class="block text-sm text-gray-400 mb-1">IP 地址</label>
                <input
                  v-model="form.ip"
                  type="text"
                  placeholder="如: 172.30.53.74"
                  class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 focus:outline-none focus:border-primary-500"
                />
              </div>
              <div class="w-24">
                <label class="block text-sm text-gray-400 mb-1">端口</label>
                <input
                  v-model.number="form.port"
                  type="number"
                  min="1"
                  max="65535"
                  class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 focus:outline-none focus:border-primary-500"
                />
              </div>
            </div>

            <!-- 权重 -->
            <div class="w-32">
              <label class="block text-sm text-gray-400 mb-1">权重</label>
              <input
                v-model="form.weight"
                type="text"
                placeholder="1"
                class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 focus:outline-none focus:border-primary-500"
              />
            </div>
          </div>

          <!-- 按钮 -->
          <div class="flex justify-end gap-3 mt-6">
            <button
              @click="showDialog = false"
              class="px-4 py-2 text-sm text-gray-400 border border-dark-border rounded-lg hover:bg-white/5 transition-colors"
            >
              取消
            </button>
            <button
              @click="handleSave"
              :disabled="saving"
              class="px-4 py-2 text-sm bg-primary-500 text-gray-900 rounded-lg hover:bg-primary-400 transition-colors disabled:opacity-50"
            >
              {{ saving ? '保存中...' : '确定' }}
            </button>
          </div>
        </div>
      </div>
    </Teleport>
  </div>
</template>
