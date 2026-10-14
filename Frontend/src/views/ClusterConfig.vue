<script setup lang="ts">
import { locale } from '@/i18n'
import { t } from '@/i18n'
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
    if (!res.success) { clusterError.value = res.message || t('page.7e6a71efbf63'); return }
    selectedClusterId.value = clusterForm.value.id
    page.value = 1; search.value = ''
    showClusterDialog.value = false
    await loadData()
    showSuccess(t('page.5c74562600ca'))
  } catch (e) { clusterError.value = getErrorMessage(e, t('page.e0334457c59f')) }
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
  consecutive_connection_failures: t('page.e13640f4294e'),
  consecutive_5xx: t('page.add8103b8ab2'),
  error_rate: t('page.97d53d32adcd'),
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
    if (request === destinationRequest) showError(t('page.8deed7a292f9'))
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
    if (request === configRequest) showError(t('page.96bba618faea'))
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
      showSuccess(t('page.587067f62127'))
      await loadData()
    } else {
      showError(res.message || t('page.db7ac79f328a'))
    }
  } catch (error) {
    showError(getErrorMessage(error, t('page.db7ac79f328a')))
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
    showError(t('page.69e10235488a'))
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
      showSuccess(dialogMode.value === 'add' ? t('page.9e497f1f547f') : t('page.eca0b0f53752'))
      showDialog.value = false
      await loadData()
    } else {
      showError(res.message || t('settings.operation.failed'))
    }
  } catch (error) {
    showError(getErrorMessage(error, t('settings.operation.failed')))
  }
}

// 删除单个目标
const removeDest = async (dest: LbDestination) => {
  if (!confirm(t('page.8d41b00b60e7', { p0: dest.id }))) return
  try {
    const res = await lbApi.removeDestination(selectedClusterId.value, dest.id)
    if (res.success) {
      showSuccess(t('page.e9e677e13451'))
      await loadData()
    } else {
      showError(res.message || t('settings.deletion.failed'))
    }
  } catch (error) {
    showError(getErrorMessage(error, t('settings.deletion.failed')))
  }
}

// 批量删除
const batchRemove = async () => {
  if (selectedDestIds.value.size === 0) {
    showError(t('page.68ec570a51e7'))
    return
  }
  if (!confirm(t('page.befe8f5dcf61', { p0: selectedDestIds.value.size }))) return
  try {
    const res = await lbApi.batchRemoveDestinations(
      selectedClusterId.value,
      Array.from(selectedDestIds.value)
    )
    if (res.success) {
      if (res.allSucceeded) showSuccess(t('page.2a4ac31cfbde', { p0: res.succeeded }))
      else showError(t('page.4223b9212792', { p0: res.succeeded, p1: res.failed, p2: res.results.filter(item => !item.success).map(item => `${item.id}: ${item.message}`).join('；') }))
      await loadData()
    } else {
      showError(res.message || t('page.27b8c99cf572'))
    }
  } catch (error) {
    showError(getErrorMessage(error, t('page.27b8c99cf572')))
  }
}

// 删除集群补丁
const removeClusterPatch = async () => {
  if (!selectedClusterId.value) return
  if (!confirm(t('page.1648ca1aaaa7', { p0: selectedClusterId.value }))) return
  try {
    const res = await lbApi.removeClusterPatch(selectedClusterId.value)
    if (res.success) {
      showSuccess(t('page.0622d73af96d'))
      await loadData()
    } else {
      showError(res.message || t('page.2f476ccaec39'))
    }
  } catch (error) {
    showError(getErrorMessage(error, t('page.2f476ccaec39')))
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
      <form role="dialog" aria-modal="true" :aria-label="t('page.adac2b426b5b')" class="bg-dark-card border border-dark-border rounded-lg p-5 w-full max-w-lg space-y-4" @submit.prevent="createCluster">
        <h2 class="font-medium">{{ t('page.adac2b426b5b') }}</h2>
        <p v-if="clusterError" role="alert" class="text-red-400 break-all">{{ clusterError }}</p>
        <label class="block">{{ t('page.b53c01010381') }}<input v-model="clusterForm.id" required pattern="[a-zA-Z0-9_-]{1,100}" maxlength="100" class="input w-full mt-1" /></label>
        <label class="block">{{ t('page.61df9d00ded4') }}<select v-model="clusterForm.policy" class="input w-full mt-1"><option value="RoundRobin">RoundRobin</option><option v-for="p in policies.filter(p => p.name !== 'RoundRobin')" :key="p.name" :value="p.name">{{ p.name }}</option></select></label>
        <label class="block">{{ t('page.217aa1ddb160') }}<input v-model="clusterForm.address" type="text" required maxlength="2048" placeholder="127.0.0.1:8080" class="input w-full mt-1" /></label>
        <div class="flex justify-end gap-3"><button type="button" :disabled="saving" @click="showClusterDialog = false">{{ t('common.cancel') }}</button><button :disabled="saving" class="btn btn-primary">{{ saving ? t('page.687d8f3ba99e') : t('page.cde2cd071d25') }}</button></div>
      </form>
    </div>
    <!-- 集群选择 & 策略配置 -->
    <div class="card">
      <div class="flex items-center justify-between mb-4">
        <h2 class="text-lg font-medium text-gray-100">{{ t('nav.clusters') }}</h2>
        <button class="btn btn-primary" @click="clusterForm = { id: '', policy: 'RoundRobin', address: '' }; clusterError = ''; showClusterDialog = true">{{ t('page.adac2b426b5b') }}</button>
      </div>

      <div class="flex flex-wrap items-center gap-4">
        <!-- 集群选择 -->
        <div class="flex items-center gap-2">
          <span class="text-sm text-gray-400">{{ t('page.8c6115b59cf6') }}</span>
          <select
            v-model="selectedClusterId"
            @change="switchCluster(selectedClusterId)"
            class="bg-dark-card border border-dark-border rounded-lg px-3 py-1.5 text-sm text-gray-200 focus:outline-none focus:border-primary-500"
          >
            <option v-for="c in clusters" :key="c.id" :value="c.id">
              {{ c.id }} ({{ c.destinationCount }} {{ t('page.31763e60fa1c') }}
            </option>
          </select>
        </div>

        <!-- 策略选择 -->
        <div class="flex flex-wrap sm:flex-nowrap items-center gap-2 min-w-0 max-w-full" v-if="currentCluster">
          <span class="text-sm text-gray-400 shrink-0">{{ t('page.e3cad51c3f6a') }}</span>
          <select
            :value="currentCluster.loadBalancingPolicy"
            @change="updatePolicy(($event.target as HTMLSelectElement).value)"
            class="bg-dark-card border border-dark-border rounded-lg px-3 py-1.5 text-sm text-gray-200 focus:outline-none focus:border-primary-500 min-w-0 max-w-full"
          >
            <option v-for="p in policies" :key="p.name" :value="p.name">
              {{ t(`lb.policy.${p.name}`) === `lb.policy.${p.name}` ? p.label : t(`lb.policy.${p.name}`) }} ({{ p.name }})
            </option>
          </select>
        </div>
      </div>
    </div>

    <!-- 目标服务器列表 -->
    <div class="card" v-if="currentCluster">
      <div class="flex flex-wrap items-center justify-between gap-3 mb-4">
        <h2 class="text-lg font-medium text-gray-100">
          {{ t('page.9d39d0195c94') }}
          <span class="text-sm text-gray-500 ml-2">({{ total }})</span>
        </h2>
        <div class="flex flex-wrap items-center gap-2">
          <button
            @click="removeClusterPatch"
            :disabled="!hasPatch"
            class="btn btn-sm btn-warning flex items-center gap-1"
            :class="{ 'opacity-50 cursor-not-allowed': !hasPatch }"
          >
            {{ t('page.c9fee9110ba0') }}
          </button>
          <button @click="openAddDialog" class="btn btn-sm btn-primary flex items-center gap-1">
            <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
            </svg>
            {{ t('page.0006d696d8e1') }}
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
            {{ t('page.ddae7a0fc554') }}
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
              <th class="py-3 px-3 text-left">{{ t('page.6773310492a9') }}</th>
              <th class="py-3 px-3 text-left">{{ t('settings.source') }}</th>
              <th class="py-3 px-3 text-left">IP</th>
              <th class="py-3 px-3 text-left">{{ t('fields.Port') }}</th>
              <th class="py-3 px-3 text-left">{{ t('page.db18831a0457') }}</th>
              <th class="py-3 px-3 text-left">{{ t('page.e46b0272bd64') }}</th>
              <th class="py-3 px-3 text-left">{{ t('settings.actions') }}</th>
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
                >{{ t('page.70f565337ffd') }}</span>
                <span
                  v-else
                  class="inline-block px-1.5 py-0.5 text-xs bg-gray-500/20 text-gray-400 rounded"
                >{{ t('page.844b8cc8dff7') }}</span>
              </td>
              <td class="py-3 px-3 text-gray-300 font-mono">{{ dest.host }}</td>
              <td class="py-3 px-3 text-gray-300">{{ dest.port }}</td>
              <td class="py-3 px-3 text-gray-300">{{ dest.metadata?.Weight || '1' }}</td>
              <td class="py-3 px-3">
                <div v-if="getOutlier(dest.id)?.state === 'ejected'" class="space-y-1">
                  <span class="inline-block px-1.5 py-0.5 text-xs bg-red-500/20 text-red-400 rounded">{{ t('page.b3467ac04553') }}</span>
                  <div class="text-xs text-gray-500">{{ outlierReason(getOutlier(dest.id)?.reason) }}</div>
                  <div class="text-xs text-gray-500">{{ t('page.1319e8efdddc') }} {{ new Date(getOutlier(dest.id)!.ejectedUntil!).toLocaleString(locale) }}</div>
                </div>
                <span v-else class="inline-block px-1.5 py-0.5 text-xs bg-green-500/20 text-green-400 rounded">{{ t('page.4d99c976beb8') }}</span>
              </td>
              <td class="py-3 px-3">
                <div class="flex items-center gap-2">
                  <button
                    @click="openEditDialog(dest)"
                    class="px-3 py-1 text-xs border border-primary-500 text-primary-400 rounded hover:bg-primary-500/10 transition-colors"
                  >
                    {{ t('common.edit') }}
                  </button>
                  <button
                    @click="removeDest(dest)"
                    class="px-3 py-1 text-xs border border-red-500 text-red-400 rounded hover:bg-red-500/10 transition-colors"
                  >
                    {{ t('common.delete') }}
                  </button>
                </div>
              </td>
            </tr>
          </tbody>
        </table>

        <!-- 空状态 -->
        <div v-if="destinations.length === 0" class="text-center py-12 text-gray-500">
          {{ t('page.b8935f01ff85') }}
        </div>
      </div>
    </div>

    <!-- 加载中 -->
    <div v-if="loading" class="text-center py-12 text-gray-500">
      {{ t('common.loading') }}
    </div>

    <!-- 无集群 -->
    <div v-if="!loading && clusters.length === 0" class="card text-center py-12 text-gray-500">
      {{ t('page.c2ac8686b5fd') }}
    </div>

    <!-- 新增/编辑对话框 -->
    <Teleport to="body">
      <div v-if="showDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
        <!-- 遮罩 -->
        <div class="absolute inset-0 bg-black/60" @click="showDialog = false"></div>

        <!-- 对话框 -->
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-full max-w-lg mx-4 p-6">
          <h3 class="text-lg font-medium text-gray-100 mb-4">
            {{ dialogMode === 'add' ? t('page.377f3f87fa28') : t('page.b892f5ddb3fb') }}
          </h3>

          <div class="space-y-4">
            <!-- 名字 -->
            <div>
              <label class="block text-sm text-gray-400 mb-1">{{ t('page.90975a437c0e') }}</label>
              <input
                v-model="form.destinationId"
                type="text"
                :disabled="dialogMode === 'edit'"
                :placeholder="t('page.83a0c3da9488')"
                class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 focus:outline-none focus:border-primary-500 disabled:opacity-50"
              />
            </div>

            <!-- 协议 + IP + 端口 -->
            <div class="flex gap-3">
              <div class="w-28">
                <label class="block text-sm text-gray-400 mb-1">{{ t('page.ab2f31f30acf') }}</label>
                <select
                  v-model="form.scheme"
                  class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 focus:outline-none focus:border-primary-500"
                >
                  <option value="http">http</option>
                  <option value="https">https</option>
                </select>
              </div>
              <div class="flex-1">
                <label class="block text-sm text-gray-400 mb-1">{{ t('settings.ip.address') }}</label>
                <input
                  v-model="form.ip"
                  type="text"
                  :placeholder="t('page.3cfd948ececa')"
                  class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 focus:outline-none focus:border-primary-500"
                />
              </div>
              <div class="w-24">
                <label class="block text-sm text-gray-400 mb-1">{{ t('fields.Port') }}</label>
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
              <label class="block text-sm text-gray-400 mb-1">{{ t('page.db18831a0457') }}</label>
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
              {{ t('common.cancel') }}
            </button>
            <button
              @click="handleSave"
              :disabled="saving"
              class="px-4 py-2 text-sm bg-primary-500 text-gray-900 rounded-lg hover:bg-primary-400 transition-colors disabled:opacity-50"
            >
              {{ saving ? t('page.917b1c1f18d0') : t('common.confirm') }}
            </button>
          </div>
        </div>
      </div>
    </Teleport>
  </div>
</template>
