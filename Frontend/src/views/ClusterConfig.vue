<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { lbApi } from '@/api'
import { useToast } from '@/composables/useToast'
import type { LbCluster, LbDestination, LbPolicy } from '@/types'

const { showSuccess, showError } = useToast()

// 数据状态
const loading = ref(false)
const clusters = ref<LbCluster[]>([])
const policies = ref<LbPolicy[]>([])
const selectedClusterId = ref('')
const selectedDestIds = ref<Set<string>>(new Set())

// 对话框状态
const showDialog = ref(false)
const dialogMode = ref<'add' | 'edit'>('add')
const saving = ref(false)

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

// 计算当前集群的目标列表
const destinations = computed(() =>
  currentCluster.value?.destinations ?? []
)

// 是否全选
const allSelected = computed(() =>
  destinations.value.length > 0 && selectedDestIds.value.size === destinations.value.length
)

// 加载数据
const loadData = async () => {
  loading.value = true
  try {
    const [clustersRes, policiesRes] = await Promise.all([
      lbApi.getClusters(),
      lbApi.getPolicies(),
    ])
    clusters.value = clustersRes.clusters
    policies.value = policiesRes.policies
    if (!selectedClusterId.value && clusters.value.length > 0) {
      selectedClusterId.value = clusters.value[0].id
    }
    selectedDestIds.value.clear()
  } catch (error) {
    showError('加载集群配置失败')
  } finally {
    loading.value = false
  }
}

// 切换集群
const switchCluster = (clusterId: string) => {
  selectedClusterId.value = clusterId
  selectedDestIds.value.clear()
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
    showError('更新策略失败')
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
    showError('操作失败')
  } finally {
    saving.value = false
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
    showError('删除失败')
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
      showSuccess('批量删除成功')
      await loadData()
    } else {
      showError(res.message || '批量删除失败')
    }
  } catch (error) {
    showError('批量删除失败')
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
    showError('删除补丁失败')
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
    <!-- 集群选择 & 策略配置 -->
    <div class="card">
      <div class="flex items-center justify-between mb-4">
        <h2 class="text-lg font-medium text-gray-100">配置集群</h2>
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
        <div class="flex items-center gap-2" v-if="currentCluster">
          <span class="text-sm text-gray-400">负载均衡策略:</span>
          <select
            :value="currentCluster.loadBalancingPolicy"
            @change="updatePolicy(($event.target as HTMLSelectElement).value)"
            class="bg-dark-card border border-dark-border rounded-lg px-3 py-1.5 text-sm text-gray-200 focus:outline-none focus:border-primary-500"
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
      <div class="flex items-center justify-between mb-4">
        <h2 class="text-lg font-medium text-gray-100">
          目标服务器
          <span class="text-sm text-gray-500 ml-2">({{ destinations.length }})</span>
        </h2>
        <div class="flex items-center gap-2">
          <button @click="removeClusterPatch" class="btn btn-sm btn-warning flex items-center gap-1">
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
      <div class="overflow-x-auto">
        <table class="w-full text-sm">
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
              <th class="py-3 px-3 text-left">IP</th>
              <th class="py-3 px-3 text-left">端口</th>
              <th class="py-3 px-3 text-left">权重</th>
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
              <td class="py-3 px-3 text-gray-300 font-mono">{{ dest.host }}</td>
              <td class="py-3 px-3 text-gray-300">{{ dest.port }}</td>
              <td class="py-3 px-3 text-gray-300">{{ dest.metadata?.Weight || '1' }}</td>
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
