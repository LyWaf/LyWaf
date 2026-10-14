<script setup lang="ts">
import { t } from '@/i18n'
import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import StatCard from '@/components/common/StatCard.vue'
import ListToolbar from '@/components/common/ListToolbar.vue'
import { timingApi, type TimingListResponse } from '@/api'
import { useToast } from '@/composables/useToast'
import type { ApiTimingStat, ApiTimingSummary } from '@/types'

const { showSuccess, showError } = useToast()

// 数据
const loading = ref(false)
const summary = ref<ApiTimingSummary | null>(null)
const items = ref<ApiTimingStat[]>([])
const backends = ref<TimingListResponse['backends']>([])
const page = ref(1)
const total = ref(0)

// 筛选和排序
const searchPath = ref('')
const filterMethod = ref('')
const filterBackend = ref('')
const sortField = ref<keyof ApiTimingStat>('requestCount')
const sortOrder = ref<'asc' | 'desc'>('desc')
const filterRequestOp = ref<'>' | '<'>('>')
const filterRequestVal = ref('')
const filterAvgTimeOp = ref<'>' | '<'>('>')
const filterAvgTimeVal = ref('')
const toggleRequestOp = () => { filterRequestOp.value = filterRequestOp.value === '>' ? '<' : '>' }
const toggleAvgTimeOp = () => { filterAvgTimeOp.value = filterAvgTimeOp.value === '>' ? '<' : '>' }

// 方法列表
const methods = ['GET', 'POST', 'PUT', 'DELETE', 'PATCH', 'OPTIONS', 'HEAD']

// 后端列表
const backendList = computed(() => backends.value.map(item => item.backend))

// 过滤后的数据
const filteredItems = computed(() => items.value)

// 加载数据
let listRequest = 0
const loadData = async () => {
  const request = ++listRequest
  loading.value = true
  try {
    const data = await timingApi.getList({ page: page.value, search: searchPath.value,
      method: filterMethod.value || undefined, backend: filterBackend.value || undefined,
      sortBy: sortField.value, sortDirection: sortOrder.value,
      requestCount: filterRequestVal.value || undefined, requestOp: filterRequestOp.value,
      avgTime: filterAvgTimeVal.value || undefined, avgTimeOp: filterAvgTimeOp.value })
    if (request !== listRequest) return
    summary.value = data.summary
    items.value = data.items || []
    backends.value = data.backends || []
    total.value = data.total
    if (page.value > 1 && !items.value.length) {
      page.value = Math.max(1, Math.ceil(data.total / 20))
      return loadData()
    }
  } catch {
    if (request === listRequest) showError(t('ui.failed.to.load.api.timing.statistics'))
  } finally {
    if (request === listRequest) loading.value = false
  }
}

watch([filterMethod, filterBackend, sortField, sortOrder, filterRequestVal, filterRequestOp, filterAvgTimeVal, filterAvgTimeOp], () => {
  page.value = 1
  loadData()
})

// 清空统计
const clearData = async () => {
  if (!confirm(t('ui.clear.all.api.timing.statistics'))) return

  try {
    await timingApi.clear()
    ++listRequest
    page.value = 1
    showSuccess(t('ui.api.timing.statistics.cleared'))
    loadData()
  } catch {
    showError(t('dashboard.failed.to.clear'))
  }
}

// 格式化时间
const formatTime = (ms: number) => {
  if (ms >= 1000) return (ms / 1000).toFixed(2) + 's'
  return ms.toFixed(0) + 'ms'
}

// 格式化数字
const formatNumber = (num: number) => {
  if (num >= 1000000) return (num / 1000000).toFixed(2) + 'M'
  if (num >= 1000) return (num / 1000).toFixed(2) + 'K'
  return num.toString()
}

// 切换排序
const toggleSort = (field: keyof ApiTimingStat) => {
  if (sortField.value === field) {
    sortOrder.value = sortOrder.value === 'desc' ? 'asc' : 'desc'
  } else {
    sortField.value = field
    sortOrder.value = 'desc'
  }
}

// 获取状态码颜色
const getStatusColor = (code: string) => {
  if (code.startsWith('2')) return 'text-green-400'
  if (code.startsWith('3')) return 'text-blue-400'
  if (code.startsWith('4')) return 'text-yellow-400'
  if (code.startsWith('5')) return 'text-red-400'
  return 'text-gray-400'
}

// 获取状态码背景色
const getStatusBgColor = (code: string) => {
  if (code.startsWith('2')) return 'bg-green-500/20'
  if (code.startsWith('3')) return 'bg-blue-500/20'
  if (code.startsWith('4')) return 'bg-yellow-500/20'
  if (code.startsWith('5')) return 'bg-red-500/20'
  return 'bg-gray-500/20'
}

// 格式化后端名称
const formatBackend = (backend: string) => {
  try {
    const url = new URL(backend)
    return `${url.protocol}//${url.host}`
  } catch {
    return backend
  }
}

// 自动刷新
let refreshTimer: number | null = null

onMounted(() => {
  loadData()
  refreshTimer = window.setInterval(loadData, 30000)
})

onUnmounted(() => {
  ++listRequest
  if (refreshTimer) clearInterval(refreshTimer)
})
</script>

<template>
  <div class="space-y-6">
    <!-- 汇总卡片 -->
    <div class="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-4">
      <StatCard
        :label="t('ui.total.requests')"
        :value="formatNumber(summary?.totalRequests || 0)"
        icon="📊"
        :tooltip="t('ui.total.api.requests.in.the.reporting.period')"
      />
      <StatCard
        :label="t('ui.average.total.duration')"
        :value="formatTime(summary?.avgTotalTime || 0)"
        icon="⏱️"
        color="blue"
        :tooltip="t('ui.average.time.from.request.to.response')"
      />
      <StatCard
        :label="t('ui.average.backend.duration')"
        :value="formatTime(summary?.avgBackendTime || 0)"
        icon="🔄"
        color="green"
        :tooltip="t('ui.average.backend.processing.time')"
      />
      <StatCard
        :label="t('ui.average.gateway.duration')"
        :value="formatTime(summary?.avgGatewayTime || 0)"
        icon="🚀"
        color="yellow"
        :tooltip="t('ui.average.gateway.processing.time')"
      />
      <StatCard
        :label="t('ui.error.rate')"
        :value="((summary?.errorRate || 0) * 100).toFixed(2) + '%'"
        icon="⚠️"
        color="red"
        :tooltip="t('ui.percentage.of.requests.returning.errors')"
      />
      <StatCard
        :label="t('ui.api.count')"
        :value="summary?.apiCount || 0"
        icon="📋"
        :tooltip="t('ui.number.of.distinct.apis.recorded')"
      />
    </div>

    <!-- 后端稳定性 -->
    <div class="card" v-if="backends.length > 0">
      <h3 class="font-medium text-gray-200 mb-4"> {{ t('ui.backend.stability') }} </h3>
      <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
        <div
          v-for="backendStats in backends"
          :key="backendStats.backend"
          class="p-4 bg-dark-card-hover rounded-lg cursor-pointer hover:bg-dark-card transition-colors"
          @click="filterBackend = filterBackend === backendStats.backend ? '' : backendStats.backend"
          :class="{ 'ring-2 ring-primary-500': filterBackend === backendStats.backend }"
        >
          <div class="flex items-center justify-between mb-2">
            <span class="text-sm text-gray-400 truncate" :title="backendStats.backend">
              {{ formatBackend(backendStats.backend) }}
            </span>
            <span class="badge badge-info">{{ backendStats.apiCount }} APIs</span>
          </div>
          <div class="grid grid-cols-2 gap-2 text-sm">
            <div>
              <span class="text-gray-500"> {{ t('ui.average.duration') }} </span>
              <div class="text-blue-400">
                {{ formatTime(backendStats.avgBackendTime) }}
              </div>
            </div>
            <div>
              <span class="text-gray-500"> {{ t('ui.request.count') }} </span>
              <div class="text-gray-200">
                {{ formatNumber(backendStats.totalRequests) }}
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- 筛选和操作 -->
    <ListToolbar v-model:page="page" v-model:search="searchPath" :total="total" :busy="loading" @refresh="loadData" />
    <div class="flex flex-wrap items-center gap-4">
      <select v-model="filterMethod" class="input w-[120px]">
        <option value=""> {{ t('ui.all.methods') }} </option>
        <option v-for="method in methods" :key="method" :value="method">{{ method }}</option>
      </select>
      <select v-model="filterBackend" class="input w-[200px]">
        <option value=""> {{ t('ui.all.backends') }} </option>
        <option v-for="backend in backendList" :key="backend" :value="backend">
          {{ formatBackend(backend) }}
        </option>
      </select>
      <div class="flex items-center gap-1.5">
        <span class="text-xs text-gray-500 whitespace-nowrap"> {{ t('ui.request.count') }} </span>
        <button @click="toggleRequestOp" class="btn btn-sm btn-secondary w-8 text-center !px-0 font-mono">{{ filterRequestOp }}</button>
        <input v-model="filterRequestVal" type="text" inputmode="numeric" class="input w-[72px] text-center" :placeholder="t('ui.count')" />
      </div>
      <div class="flex items-center gap-1.5">
        <span class="text-xs text-gray-500 whitespace-nowrap"> {{ t('ui.duration.ms') }} </span>
        <button @click="toggleAvgTimeOp" class="btn btn-sm btn-secondary w-8 text-center !px-0 font-mono">{{ filterAvgTimeOp }}</button>
        <input v-model="filterAvgTimeVal" type="text" inputmode="numeric" class="input w-[72px] text-center" :placeholder="t('ui.milliseconds')" />
      </div>
      <div class="flex-1"></div>
      <button @click="loadData" class="btn btn-sm btn-secondary" :disabled="loading">
        {{ loading ? t('common.loading') : t('common.refresh') }}
      </button>
      <button @click="clearData" class="btn btn-sm btn-danger"> {{ t('ui.clear.statistics') }} </button>
    </div>

    <!-- 数据表格 -->
    <div class="card overflow-hidden">
      <div class="overflow-x-auto">
        <table class="w-full min-w-[900px]">
          <thead>
            <tr class="border-b border-dark-border">
              <th class="px-4 py-3 text-left text-sm font-medium text-gray-400 whitespace-nowrap"> {{ t('ui.method') }} </th>
              <th class="px-4 py-3 text-left text-sm font-medium text-gray-400 whitespace-nowrap"> {{ t('ui.path') }} </th>
              <th class="px-4 py-3 text-left text-sm font-medium text-gray-400 whitespace-nowrap"> {{ t('ui.backend') }} </th>
              <th
                @click="toggleSort('requestCount')"
                class="px-4 py-3 text-right text-sm font-medium text-gray-400 whitespace-nowrap cursor-pointer hover:text-primary-500"
              > {{ t('ui.request.count') }} {{ sortField === 'requestCount' ? (sortOrder === 'desc' ? '↓' : '↑') : '' }}
              </th>
              <th
                @click="toggleSort('avgTotalTime')"
                class="px-4 py-3 text-right text-sm font-medium text-gray-400 whitespace-nowrap cursor-pointer hover:text-primary-500"
              > {{ t('ui.average.total.duration') }} {{ sortField === 'avgTotalTime' ? (sortOrder === 'desc' ? '↓' : '↑') : '' }}
              </th>
              <th
                @click="toggleSort('avgBackendTime')"
                class="px-4 py-3 text-right text-sm font-medium text-gray-400 whitespace-nowrap cursor-pointer hover:text-primary-500"
              > {{ t('ui.backend.duration') }} {{ sortField === 'avgBackendTime' ? (sortOrder === 'desc' ? '↓' : '↑') : '' }}
              </th>
              <th class="px-4 py-3 text-right text-sm font-medium text-gray-400 whitespace-nowrap"> {{ t('ui.min.max') }} </th>
              <th class="px-4 py-3 text-center text-sm font-medium text-gray-400 whitespace-nowrap"> {{ t('ui.status.code.distribution') }} </th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="item in filteredItems"
              :key="`${item.method}-${item.path}-${item.backend}`"
              class="border-b border-dark-border/50 hover:bg-dark-card-hover transition-colors"
            >
              <td class="px-4 py-3">
                <span
                  :class="[
                    'px-2 py-0.5 rounded text-xs font-medium',
                    item.method === 'GET' ? 'bg-green-500/20 text-green-400' :
                    item.method === 'POST' ? 'bg-blue-500/20 text-blue-400' :
                    item.method === 'PUT' ? 'bg-yellow-500/20 text-yellow-400' :
                    item.method === 'DELETE' ? 'bg-red-500/20 text-red-400' :
                    'bg-gray-500/20 text-gray-400'
                  ]"
                >
                  {{ item.method }}
                </span>
              </td>
              <td class="px-4 py-3 text-gray-300 font-mono text-sm max-w-[360px]" :title="item.path">
                <div class="flex items-center">
                  <span class="truncate">{{ item.path }}</span>
                  <a
                     v-if="item.method === 'GET' && item.originalHost"
                     :href="item.originalHost + item.path"
                     target="_blank"
                     rel="noopener noreferrer"
                     class="ml-1.5 flex-shrink-0 inline-flex items-center text-gray-500 hover:text-primary-400 transition-colors"
                     :title="t('ui.open.original.request.in.a.new.window')"
                     @click.stop
                   >
                    <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14" />
                    </svg>
                  </a>
                </div>
              </td>
              <td class="px-4 py-3 text-gray-400 text-sm truncate max-w-[150px]" :title="item.backend">
                {{ formatBackend(item.backend) }}
              </td>
              <td class="px-4 py-3 text-right text-gray-200 whitespace-nowrap">{{ formatNumber(item.requestCount) }}</td>
              <td class="px-4 py-3 text-right text-blue-400 whitespace-nowrap">{{ formatTime(item.avgTotalTime) }}</td>
              <td class="px-4 py-3 text-right text-green-400 whitespace-nowrap">{{ formatTime(item.avgBackendTime) }}</td>
              <td class="px-4 py-3 text-right text-sm whitespace-nowrap">
                <span class="text-gray-500">{{ formatTime(item.minTotalTime) }}</span>
                <span class="text-gray-600 mx-1">/</span>
                <span class="text-yellow-400">{{ formatTime(item.maxTotalTime) }}</span>
              </td>
              <td class="px-4 py-3 text-center">
                <div class="flex justify-center gap-1 flex-wrap">
                  <span
                    v-for="(count, code) in item.statusCodes"
                    :key="String(code)"
                    :class="['px-1.5 py-0.5 rounded text-xs', getStatusColor(String(code)), getStatusBgColor(String(code))]"
                  >
                    {{ code }}: {{ count }}
                  </span>
                </div>
              </td>
            </tr>
            <tr v-if="filteredItems.length === 0">
              <td colspan="8" class="px-4 py-8 text-center text-gray-500">
                {{ loading ? t('common.loading') : t('common.noData') }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  </div>
</template>
