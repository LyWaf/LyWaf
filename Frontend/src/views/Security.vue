<script setup lang="ts">
import { ref, shallowRef, onMounted, onUnmounted, computed, nextTick, markRaw } from 'vue'
import { Chart, registerables } from 'chart.js'
import StatCard from '@/components/common/StatCard.vue'
import { securityApi } from '@/api'
import { useToast } from '@/composables/useToast'
import type { SecurityStats, SecurityEventType, AttackSourceStat } from '@/types'

Chart.register(...registerables)

const { showSuccess, showError } = useToast()

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
    // 选了 6h / 24h 时查 DuckDB 历史
    if (selectedHours.value > 1) {
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

// 数据
const stats = ref<SecurityStats | null>(null)
const loading = ref(false)
const selectedHours = ref(24)
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

// 加载数据
const loadData = async () => {
  loading.value = true
  try {
    const data = await securityApi.getStats(selectedHours.value)
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
    const labels = history.map(h => h.time)
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

// 切换时间范围
const changeTimeRange = (hours: number) => {
  selectedHours.value = hours
  // 时间范围变了，销毁所有图表实例让其重建
  Object.keys(charts).forEach(k => {
    charts[k]?.destroy()
    charts[k] = null
  })
  // 自动调整 QPS 粒度
  const newG = autoQpsGranularity(hours)
  if (qpsGranularity.value !== newG) {
    qpsGranularity.value = newG
  }
  if (qpsChart.value) {
    qpsChart.value.destroy()
    qpsChart.value = null
  }
  loadData()
  loadQps()
}

// 格式化攻击源列表
const getTopAttackSources = (key: SecurityEventType): AttackSourceStat[] => {
  return stats.value?.topAttackSources?.[key] || []
}

// 自动刷新
let refreshTimer: number | null = null
let qpsTimer: number | null = null

onMounted(() => {
  loadData()
  loadQps()
  refreshTimer = window.setInterval(loadData, 60000)
  qpsTimer = window.setInterval(loadQps, 5000)
})

onUnmounted(() => {
  if (refreshTimer) clearInterval(refreshTimer)
  if (qpsTimer) clearInterval(qpsTimer)
  // 销毁所有图表
  Object.values(charts).forEach(chart => chart?.destroy())
  qpsChart.value?.destroy()
})
</script>

<template>
  <div class="space-y-6">
    <!-- 顶部控制 -->
    <div class="flex items-center justify-between">
      <div class="flex gap-2">
        <button
          v-for="range in timeRanges"
          :key="range.value"
          @click="changeTimeRange(range.value)"
          :class="[
            'btn btn-sm',
            selectedHours === range.value ? 'btn-primary' : 'btn-secondary'
          ]"
        >
          {{ range.label }}
        </button>
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
