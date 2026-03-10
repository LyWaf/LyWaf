<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import StatCard from '@/components/common/StatCard.vue'
import { timingApi, type TimingListResponse } from '@/api'
import { useToast } from '@/composables/useToast'
import type { ApiTimingStat, ApiTimingSummary } from '@/types'

const { showSuccess, showError } = useToast()

// 数据
const loading = ref(false)
const summary = ref<ApiTimingSummary | null>(null)
const items = ref<ApiTimingStat[]>([])
const backends = ref<Record<string, ApiTimingStat[]>>({})

// 筛选和排序
const searchPath = ref('')
const filterMethod = ref('')
const filterBackend = ref('')
const sortField = ref<keyof ApiTimingStat>('requestCount')
const sortOrder = ref<'asc' | 'desc'>('desc')

// 方法列表
const methods = ['GET', 'POST', 'PUT', 'DELETE', 'PATCH', 'OPTIONS', 'HEAD']

// 后端列表
const backendList = computed(() => Object.keys(backends.value))

// 过滤后的数据
const filteredItems = computed(() => {
  let result = [...items.value]
  
  if (searchPath.value) {
    result = result.filter(item => 
      item.path.toLowerCase().includes(searchPath.value.toLowerCase())
    )
  }
  
  if (filterMethod.value) {
    result = result.filter(item => item.method === filterMethod.value)
  }
  
  if (filterBackend.value) {
    result = result.filter(item => item.backend === filterBackend.value)
  }
  
  // 排序
  result.sort((a, b) => {
    const aVal = a[sortField.value] as number
    const bVal = b[sortField.value] as number
    return sortOrder.value === 'desc' ? bVal - aVal : aVal - bVal
  })
  
  return result
})

// 加载数据
const loadData = async () => {
  loading.value = true
  try {
    const data: TimingListResponse = await timingApi.getList()
    summary.value = data.summary
    items.value = data.items || []
    backends.value = data.backends || {}
  } catch (error) {
    console.error('加载 API 耗时统计失败:', error)
  } finally {
    loading.value = false
  }
}

// 清空统计
const clearData = async () => {
  if (!confirm('确定要清空所有 API 耗时统计吗？')) return
  
  try {
    await timingApi.clear()
    showSuccess('API 耗时统计已清空')
    loadData()
  } catch {
    showError('清空失败')
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
  if (refreshTimer) clearInterval(refreshTimer)
})
</script>

<template>
  <div class="space-y-6">
    <!-- 汇总卡片 -->
    <div class="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-4">
      <StatCard 
        label="总请求数" 
        :value="formatNumber(summary?.totalRequests || 0)" 
        icon="📊"
        tooltip="统计周期内的 API 总请求数"
      />
      <StatCard 
        label="平均总耗时" 
        :value="formatTime(summary?.avgTotalTime || 0)" 
        icon="⏱️"
        color="blue"
        tooltip="从接收请求到响应的平均时间"
      />
      <StatCard 
        label="平均后端耗时" 
        :value="formatTime(summary?.avgBackendTime || 0)" 
        icon="🔄"
        color="green"
        tooltip="后端服务处理的平均时间"
      />
      <StatCard 
        label="平均网关耗时" 
        :value="formatTime(summary?.avgGatewayTime || 0)" 
        icon="🚀"
        color="yellow"
        tooltip="网关处理的平均时间"
      />
      <StatCard 
        label="错误率" 
        :value="((summary?.errorRate || 0) * 100).toFixed(2) + '%'" 
        icon="⚠️"
        color="red"
        tooltip="返回错误状态码的请求占比"
      />
      <StatCard 
        label="API 数量" 
        :value="summary?.apiCount || 0" 
        icon="📋"
        tooltip="已统计的不同 API 数量"
      />
    </div>
    
    <!-- 后端稳定性 -->
    <div class="card" v-if="Object.keys(backends).length > 0">
      <h3 class="font-medium text-gray-200 mb-4">后端稳定性</h3>
      <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
        <div 
          v-for="(backendStats, backendName) in backends" 
          :key="backendName"
          class="p-4 bg-dark-card-hover rounded-lg cursor-pointer hover:bg-dark-card transition-colors"
          @click="filterBackend = filterBackend === backendName ? '' : backendName"
          :class="{ 'ring-2 ring-primary-500': filterBackend === backendName }"
        >
          <div class="flex items-center justify-between mb-2">
            <span class="text-sm text-gray-400 truncate" :title="String(backendName)">
              {{ formatBackend(String(backendName)) }}
            </span>
            <span class="badge badge-info">{{ backendStats.length }} APIs</span>
          </div>
          <div class="grid grid-cols-2 gap-2 text-sm">
            <div>
              <span class="text-gray-500">平均耗时</span>
              <div class="text-blue-400">
                {{ formatTime(backendStats.reduce((sum, s) => sum + s.avgBackendTime, 0) / backendStats.length) }}
              </div>
            </div>
            <div>
              <span class="text-gray-500">请求数</span>
              <div class="text-gray-200">
                {{ formatNumber(backendStats.reduce((sum, s) => sum + s.requestCount, 0)) }}
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
    
    <!-- 筛选和操作 -->
    <div class="flex flex-wrap items-center gap-4">
      <input
        v-model="searchPath"
        type="text"
        placeholder="搜索路径..."
        class="input w-[200px]"
      />
      <select v-model="filterMethod" class="input w-[120px]">
        <option value="">全部方法</option>
        <option v-for="method in methods" :key="method" :value="method">{{ method }}</option>
      </select>
      <select v-model="filterBackend" class="input w-[200px]">
        <option value="">全部后端</option>
        <option v-for="backend in backendList" :key="backend" :value="backend">
          {{ formatBackend(backend) }}
        </option>
      </select>
      <div class="flex-1"></div>
      <button @click="loadData" class="btn btn-sm btn-secondary" :disabled="loading">
        {{ loading ? '加载中...' : '刷新' }}
      </button>
      <button @click="clearData" class="btn btn-sm btn-danger">
        清空统计
      </button>
    </div>
    
    <!-- 数据表格 -->
    <div class="card overflow-hidden">
      <div class="overflow-x-auto">
        <table class="w-full min-w-[900px]">
          <thead>
            <tr class="border-b border-dark-border">
              <th class="px-4 py-3 text-left text-sm font-medium text-gray-400 whitespace-nowrap">方法</th>
              <th class="px-4 py-3 text-left text-sm font-medium text-gray-400 whitespace-nowrap">路径</th>
              <th class="px-4 py-3 text-left text-sm font-medium text-gray-400 whitespace-nowrap">后端</th>
              <th
                @click="toggleSort('requestCount')"
                class="px-4 py-3 text-right text-sm font-medium text-gray-400 whitespace-nowrap cursor-pointer hover:text-primary-500"
              >
                请求数 {{ sortField === 'requestCount' ? (sortOrder === 'desc' ? '↓' : '↑') : '' }}
              </th>
              <th
                @click="toggleSort('avgTotalTime')"
                class="px-4 py-3 text-right text-sm font-medium text-gray-400 whitespace-nowrap cursor-pointer hover:text-primary-500"
              >
                平均总耗时 {{ sortField === 'avgTotalTime' ? (sortOrder === 'desc' ? '↓' : '↑') : '' }}
              </th>
              <th
                @click="toggleSort('avgBackendTime')"
                class="px-4 py-3 text-right text-sm font-medium text-gray-400 whitespace-nowrap cursor-pointer hover:text-primary-500"
              >
                后端耗时 {{ sortField === 'avgBackendTime' ? (sortOrder === 'desc' ? '↓' : '↑') : '' }}
              </th>
              <th class="px-4 py-3 text-right text-sm font-medium text-gray-400 whitespace-nowrap">最小/最大</th>
              <th class="px-4 py-3 text-center text-sm font-medium text-gray-400 whitespace-nowrap">状态码分布</th>
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
                     title="在新窗口中打开原始请求"
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
                {{ loading ? '加载中...' : '暂无数据' }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  </div>
</template>
