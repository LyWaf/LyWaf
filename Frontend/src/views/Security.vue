<script setup lang="ts">
import { ref, shallowRef, onMounted, onUnmounted, computed, nextTick, markRaw } from 'vue'
import { Chart, registerables } from 'chart.js'
import StatCard from '@/components/common/StatCard.vue'
import DateTimePicker from '@/components/common/DateTimePicker.vue'
import { securityApi } from '@/api'
import { useToast } from '@/composables/useToast'
import type { SecurityStats, SecurityEventType, AttackSourceStat } from '@/types'

Chart.register(...registerables)

const { showSuccess, showError } = useToast()

// 时间格式化：MM-DD HH:mm:ss
const formatTime = (t: string) => {
  const d = new Date(t)
  if (isNaN(d.getTime())) return t
  const mm = String(d.getMonth() + 1).padStart(2, '0')
  const dd = String(d.getDate()).padStart(2, '0')
  const HH = String(d.getHours()).padStart(2, '0')
  const MM = String(d.getMinutes()).padStart(2, '0')
  const ss = String(d.getSeconds()).padStart(2, '0')
  return `${mm}-${dd} ${HH}:${MM}:${ss}`
}

// ==================== QPS 统计 ====================
type QpsGranularity = '5s' | '1min' | '1hour'
const qpsGranularity = ref<QpsGranularity>('5s')
const qpsChart = shallowRef<Chart | null>(null)
const qpsLoading = ref(false)
let qpsCountData: number[] = []
let qpsQpsData: number[] = []

const qpsGranularities: { label: string; value: QpsGranularity }[] = [
  { label: '每5秒', value: '5s' },
  { label: '每分钟', value: '1min' },
  { label: '每5分钟', value: '1hour' },
]

const loadQps = async () => {
  qpsLoading.value = true
  try {
    let from: string | undefined
    let to: string | undefined
    if (useCustomTime.value && customStartTime.value && customEndTime.value) {
      // 自定义时间范围
      from = new Date(customStartTime.value).toISOString()
      to = new Date(customEndTime.value).toISOString()
    } else if (selectedHours.value > 1) {
      // 选了 6h / 24h 时查 DuckDB 历史
      const now = new Date()
      to = now.toISOString()
      from = new Date(now.getTime() - selectedHours.value * 3600_000).toISOString()
    }
    const res = await securityApi.getQpsHistory(qpsGranularity.value, from, to)
    const arr = res?.success ? (res.data ?? []) : []
    await nextTick()
    updateQpsChart(arr)
  } catch (e) {
    console.warn('加载 QPS 数据请求失败', e)
  } finally {
    qpsLoading.value = false
  }
}

const updateQpsChart = (data: Array<{ time: string; qps: number; requestCount: number }>) => {
  try {
    const labels = data.map(d => d.time)
    qpsQpsData = data.map(d => d.qps)
    qpsCountData = data.map(d => d.requestCount)

    // 已有图表实例则只更新数据
    if (qpsChart.value) {
      qpsChart.value.data.labels = labels
      qpsChart.value.data.datasets[0].data = qpsQpsData
      qpsChart.value.update()
      return
    }

    // 首次创建
    const canvas = document.getElementById('qps-chart') as HTMLCanvasElement
    if (!canvas || !canvas.getContext('2d')) return

    qpsChart.value = markRaw(new Chart(canvas, {
    type: 'line',
    data: {
      labels,
      datasets: [{
        label: 'QPS',
        data: qpsQpsData,
        borderColor: '#3b82f6',
        backgroundColor: 'rgba(59, 130, 246, 0.15)',
        fill: true,
        tension: 0.3,
        pointRadius: 1,
        pointHoverRadius: 5,
      }]
    },
    options: {
      responsive: true,
      maintainAspectRatio: false,
      animation: false,
      interaction: {
        mode: 'index',
        intersect: false,
      },
      plugins: {
        legend: { display: false },
        tooltip: {
          enabled: true,
          mode: 'index',
          intersect: false,
          callbacks: {
            label: (item: any) => {
              const idx = item.dataIndex
              const qps = qpsQpsData[idx] ?? 0
              const count = qpsCountData[idx] ?? 0
              return `QPS: ${qps}  请求数: ${count}`
            },
          },
        },
      },
      scales: {
        x: {
          grid: { color: 'rgba(255,255,255,0.1)' },
          ticks: { color: '#94a3b8', maxRotation: 0, maxTicksLimit: 10 },
        },
        y: {
          grid: { color: 'rgba(255,255,255,0.1)' },
          ticks: { color: '#94a3b8' },
          beginAtZero: true,
        },
      },
    },
  }))
  } catch (e) {
    console.warn('QPS 图表渲染异常', e)
  }
}

const changeQpsGranularity = (g: QpsGranularity) => {
  qpsGranularity.value = g
  if (qpsChart.value) {
    qpsChart.value.destroy()
    qpsChart.value = null
  }
  loadQps()
}

// ==================== 带宽统计 ====================
const bwChart = shallowRef<Chart | null>(null)
const bwLoading = ref(false)
let bwInboundData: number[] = []
let bwOutboundData: number[] = []

const formatBytes = (bytes: number): string => {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}

const loadBandwidth = async () => {
  bwLoading.value = true
  try {
    let from: string | undefined
    let to: string | undefined
    if (useCustomTime.value && customStartTime.value && customEndTime.value) {
      from = new Date(customStartTime.value).toISOString()
      to = new Date(customEndTime.value).toISOString()
    } else if (selectedHours.value > 1) {
      const now = new Date()
      to = now.toISOString()
      from = new Date(now.getTime() - selectedHours.value * 3600_000).toISOString()
    }
    const res = await securityApi.getBandwidthHistory(qpsGranularity.value, from, to)
    const arr = res?.success ? (res.data ?? []) : []
    await nextTick()
    updateBwChart(arr)
  } catch (e) {
    console.warn('加载带宽数据失败', e)
  } finally {
    bwLoading.value = false
  }
}

const updateBwChart = (data: Array<{ time: string; inboundBytes: number; outboundBytes: number; inboundRate: number; outboundRate: number }>) => {
  try {
    const labels = data.map(d => d.time)
    bwInboundData = data.map(d => d.inboundRate)
    bwOutboundData = data.map(d => d.outboundRate)

    if (bwChart.value) {
      bwChart.value.data.labels = labels
      bwChart.value.data.datasets[0].data = bwInboundData
      bwChart.value.data.datasets[1].data = bwOutboundData
      bwChart.value.update()
      return
    }

    const canvas = document.getElementById('bw-chart') as HTMLCanvasElement
    if (!canvas || !canvas.getContext('2d')) return

    bwChart.value = markRaw(new Chart(canvas, {
      type: 'line',
      data: {
        labels,
        datasets: [
          {
            label: '入站',
            data: bwInboundData,
            borderColor: '#22c55e',
            backgroundColor: 'rgba(34, 197, 94, 0.15)',
            fill: true,
            tension: 0.3,
            pointRadius: 1,
            pointHoverRadius: 5,
          },
          {
            label: '出站',
            data: bwOutboundData,
            borderColor: '#f59e0b',
            backgroundColor: 'rgba(245, 158, 11, 0.15)',
            fill: true,
            tension: 0.3,
            pointRadius: 1,
            pointHoverRadius: 5,
          },
        ]
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        animation: false,
        interaction: {
          mode: 'index',
          intersect: false,
        },
        plugins: {
          legend: {
            display: true,
            labels: { color: '#94a3b8' },
          },
          tooltip: {
            enabled: true,
            mode: 'index',
            intersect: false,
            callbacks: {
              label: (item: any) => {
                const idx = item.dataIndex
                const inRate = bwInboundData[idx] ?? 0
                const outRate = bwOutboundData[idx] ?? 0
                if (item.datasetIndex === 0)
                  return `入站: ${formatBytes(inRate)}/s`
                return `出站: ${formatBytes(outRate)}/s`
              },
            },
          },
        },
        scales: {
          x: {
            grid: { color: 'rgba(255,255,255,0.1)' },
            ticks: { color: '#94a3b8', maxRotation: 0, maxTicksLimit: 10 },
          },
          y: {
            grid: { color: 'rgba(255,255,255,0.1)' },
            ticks: {
              color: '#94a3b8',
              callback: (value: any) => formatBytes(Number(value)) + '/s',
            },
            beginAtZero: true,
          },
        },
      },
    }))
  } catch (e) {
    console.warn('带宽图表渲染异常', e)
  }
}

// 数据
const stats = ref<SecurityStats | null>(null)
const loading = ref(false)
const selectedHours = ref(1)
const customStartTime = ref('')
const customEndTime = ref('')
const useCustomTime = computed(() => customStartTime.value !== '' || customEndTime.value !== '')
const charts: Record<string, Chart | null> = {}

// 时间范围选项
const timeRanges = [
  { label: '1小时', value: 1 },
  { label: '6小时', value: 6 },
  { label: '24小时', value: 24 },
]

// 事件类型配置
const eventTypes: { key: SecurityEventType; label: string; color: string; bgColor: string }[] = [
  { key: 'WafIntercept', label: '攻击拦截', color: '#ef4444', bgColor: 'rgba(239, 68, 68, 0.2)' },
  { key: 'CcAttack', label: 'CC攻击', color: '#f59e0b', bgColor: 'rgba(245, 158, 11, 0.2)' },
  { key: 'BlacklistBlock', label: '黑名单拦截', color: '#8b5cf6', bgColor: 'rgba(139, 92, 246, 0.2)' },
  { key: 'GeoBlock', label: '地理拦截', color: '#3b82f6', bgColor: 'rgba(59, 130, 246, 0.2)' },
  { key: 'CrawlerDetect', label: '爬虫检测', color: '#10b981', bgColor: 'rgba(16, 185, 129, 0.2)' },
  { key: 'RateLimit', label: '频率限制', color: '#ec4899', bgColor: 'rgba(236, 72, 153, 0.2)' },
]

// 总计统计
const totalStats = computed(() => {
  if (!stats.value) return []
  return [
    { label: '攻击拦截', value: stats.value.wafIntercepts, color: 'red' as const, icon: '🛡️' },
    { label: 'CC攻击', value: stats.value.ccAttacks, color: 'yellow' as const, icon: '⚡' },
    { label: '黑名单拦截', value: stats.value.blacklistBlocks, color: 'purple' as const, icon: '🚫' },
    { label: '地理拦截', value: stats.value.geoBlocks, color: 'blue' as const, icon: '🌍' },
    { label: '爬虫检测', value: stats.value.crawlerDetects, color: 'green' as const, icon: '🤖' },
    { label: '频率限制', value: stats.value.rateLimits, color: 'default' as const, icon: '⏱️' },
  ]
})

// 计算当前有效的时间范围（小时数）
const getEffectiveHours = () => {
  if (useCustomTime.value && customStartTime.value && customEndTime.value) {
    const start = new Date(customStartTime.value).getTime()
    const end = new Date(customEndTime.value).getTime()
    if (!isNaN(start) && !isNaN(end) && end > start) {
      return Math.ceil((end - start) / 3600_000)
    }
  }
  return selectedHours.value || 24
}

// 加载数据
const loadData = async () => {
  loading.value = true
  try {
    const hours = getEffectiveHours()
    const data = await securityApi.getStats(hours)
    stats.value = data
    await nextTick()
    updateCharts()
  } catch (error) {
    console.error('加载安全统计失败:', error)
  } finally {
    loading.value = false
  }
}

// 更新图表
const updateCharts = () => {
  if (!stats.value) return

  eventTypes.forEach(eventType => {
    const history = stats.value?.history?.[eventType.key] || []
    const labels = history.map(h => formatTime(h.time))
    const data = history.map(h => h.count)

    // 已有实例则复用
    const existing = charts[eventType.key]
    if (existing) {
      existing.data.labels = labels
      existing.data.datasets[0].data = data
      existing.update()
      return
    }

    const canvasId = `chart-${eventType.key}`
    const canvas = document.getElementById(canvasId) as HTMLCanvasElement
    if (!canvas || !canvas.getContext('2d')) return

    charts[eventType.key] = markRaw(new Chart(canvas, {
      type: 'line',
      data: {
        labels,
        datasets: [{
          label: eventType.label,
          data,
          borderColor: eventType.color,
          backgroundColor: eventType.bgColor,
          fill: true,
          tension: 0.4,
          pointRadius: 2,
          pointHoverRadius: 5,
        }]
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        animation: false,
        interaction: {
          mode: 'index',
          intersect: false,
        },
        plugins: {
          legend: { display: false },
          tooltip: {
            enabled: true,
            mode: 'index',
            intersect: false,
          },
        },
        scales: {
          x: {
            grid: { color: 'rgba(255,255,255,0.1)' },
            ticks: { color: '#94a3b8', maxRotation: 0 },
          },
          y: {
            grid: { color: 'rgba(255,255,255,0.1)' },
            ticks: { color: '#94a3b8' },
            beginAtZero: true,
          },
        },
      },
    }))
  })
}

// 重置统计
const resetStats = async () => {
  if (!confirm('确定要重置安全统计吗？')) return
  
  try {
    await securityApi.reset()
    showSuccess('安全统计已重置')
    loadData()
  } catch {
    showError('重置失败')
  }
}

// 根据时间范围自动选择合适的 QPS 粒度
const autoQpsGranularity = (hours: number): QpsGranularity => {
  if (hours <= 1) return '5s'
  if (hours <= 6) return '1min'
  return '1hour'
}

// 自定义时间查询
const searchCustomTime = () => {
  if (!customStartTime.value || !customEndTime.value) return
  // 清除快捷选择高亮
  selectedHours.value = 0
  // 销毁图表重建
  destroyAllCharts()
  // 根据时间跨度自动调整 QPS 粒度
  const hours = getEffectiveHours()
  const newG = autoQpsGranularity(hours)
  if (qpsGranularity.value !== newG) qpsGranularity.value = newG
  loadData()
  loadQps()
  loadBandwidth()
}

// 销毁所有图表实例
const destroyAllCharts = () => {
  Object.keys(charts).forEach(k => {
    charts[k]?.destroy()
    charts[k] = null
  })
  if (qpsChart.value) {
    qpsChart.value.destroy()
    qpsChart.value = null
  }
  if (bwChart.value) {
    bwChart.value.destroy()
    bwChart.value = null
  }
}

// 切换时间范围
const changeTimeRange = (hours: number) => {
  selectedHours.value = hours
  customStartTime.value = ''
  customEndTime.value = ''
  destroyAllCharts()
  const newG = autoQpsGranularity(hours)
  if (qpsGranularity.value !== newG) qpsGranularity.value = newG
  loadData()
  loadQps()
  loadBandwidth()
}

// 格式化攻击源列表
const getTopAttackSources = (key: SecurityEventType): AttackSourceStat[] => {
  return stats.value?.topAttackSources?.[key] || []
}

// 自动刷新
let refreshTimer: number | null = null
let qpsTimer: number | null = null

let bwTimer: number | null = null

onMounted(() => {
  loadData()
  loadQps()
  loadBandwidth()
  refreshTimer = window.setInterval(loadData, 60000)
  qpsTimer = window.setInterval(loadQps, 5000)
  bwTimer = window.setInterval(loadBandwidth, 5000)
})

onUnmounted(() => {
  if (refreshTimer) clearInterval(refreshTimer)
  if (qpsTimer) clearInterval(qpsTimer)
  if (bwTimer) clearInterval(bwTimer)
  // 销毁所有图表
  Object.values(charts).forEach(chart => chart?.destroy())
  qpsChart.value?.destroy()
  bwChart.value?.destroy()
})
</script>

<template>
  <div class="space-y-6">
    <!-- 顶部控制 -->
    <div class="flex items-center justify-between">
      <div class="flex items-center gap-2 flex-wrap">
        <button
          v-for="range in timeRanges"
          :key="range.value"
          @click="changeTimeRange(range.value)"
          :class="[
            'btn btn-sm',
            selectedHours === range.value && !useCustomTime ? 'btn-primary' : 'btn-secondary'
          ]"
        >
          {{ range.label }}
        </button>
        <DateTimePicker v-model="customStartTime" placeholder="开始时间" />
        <DateTimePicker v-model="customEndTime" placeholder="结束时间" />
        <button @click="searchCustomTime" class="btn btn-sm btn-primary">查询</button>
      </div>
      <button @click="resetStats" class="btn btn-sm btn-danger">
        重置统计
      </button>
    </div>
    
    <!-- 统计卡片 -->
    <div class="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-4">
      <StatCard
        v-for="stat in totalStats"
        :key="stat.label"
        :label="stat.label"
        :value="stat.value"
        :icon="stat.icon"
        :color="stat.color"
      />
    </div>
    
    <!-- QPS 统计 -->
    <div class="card">
      <div class="flex items-center justify-between mb-4">
        <h3 class="font-medium text-gray-200">QPS 统计</h3>
        <div class="flex gap-1.5">
          <button
            v-for="g in qpsGranularities"
            :key="g.value"
            @click="changeQpsGranularity(g.value)"
            :class="[
              'px-2.5 py-1 text-xs rounded transition-colors',
              qpsGranularity === g.value
                ? 'bg-blue-500/20 text-blue-400 border border-blue-500/30'
                : 'text-gray-500 hover:text-gray-300'
            ]"
          >
            {{ g.label }}
          </button>
        </div>
      </div>
      <div class="h-[220px]">
        <canvas id="qps-chart"></canvas>
      </div>
    </div>

    <!-- 带宽统计 -->
    <div class="card">
      <div class="flex items-center justify-between mb-4">
        <h3 class="font-medium text-gray-200">流量统计</h3>
        <div class="flex items-center gap-3 text-xs text-gray-400">
          <span class="flex items-center gap-1"><span class="w-3 h-0.5 bg-green-500 inline-block rounded"></span> 入站</span>
          <span class="flex items-center gap-1"><span class="w-3 h-0.5 bg-amber-500 inline-block rounded"></span> 出站</span>
        </div>
      </div>
      <div class="h-[220px]">
        <canvas id="bw-chart"></canvas>
      </div>
    </div>

    <!-- 图表网格 -->
    <div class="grid grid-cols-1 lg:grid-cols-2 gap-6">
      <div 
        v-for="eventType in eventTypes" 
        :key="eventType.key"
        class="card"
      >
        <div class="flex items-center justify-between mb-4">
          <h3 class="font-medium text-gray-200">{{ eventType.label }}趋势</h3>
          <span 
            class="badge"
            :style="{ backgroundColor: eventType.bgColor, color: eventType.color }"
          >
            {{ stats?.history?.[eventType.key]?.reduce((sum, h) => sum + h.count, 0) || 0 }}
          </span>
        </div>
        
        <div class="flex gap-4">
          <!-- 图表 -->
          <div class="flex-1 h-[200px]">
            <canvas :id="`chart-${eventType.key}`"></canvas>
          </div>
          
          <!-- 攻击源列表 -->
          <div class="w-[180px] border-l border-dark-border pl-4">
            <div class="text-sm text-gray-400 mb-2">Top 攻击源</div>
            <div class="space-y-2 max-h-[180px] overflow-y-auto">
              <div 
                v-for="(source, index) in getTopAttackSources(eventType.key).slice(0, 5)" 
                :key="source.ip"
                class="flex items-center justify-between text-sm"
              >
                <span class="text-gray-300 truncate">
                  <span class="text-gray-500 mr-1">{{ index + 1 }}.</span>
                  {{ source.ip }}
                </span>
                <span :style="{ color: eventType.color }">{{ source.count }}</span>
              </div>
              <div 
                v-if="getTopAttackSources(eventType.key).length === 0"
                class="text-gray-500 text-sm"
              >
                暂无数据
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
