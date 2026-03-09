<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { bwEventApi } from '@/api'
import type { BwHitEvent } from '@/api'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()

const loading = ref(false)
const events = ref<BwHitEvent[]>([])

// 过滤
const filterIp = ref('')
const filterDomain = ref('')
const filterStartTime = ref('')
const filterEndTime = ref('')

const loadEvents = async () => {
  loading.value = true
  try {
    const params: Record<string, string> = {}
    if (filterIp.value.trim()) params.ip = filterIp.value.trim()
    if (filterDomain.value.trim()) params.domain = filterDomain.value.trim()
    if (filterStartTime.value) params.startTime = filterStartTime.value
    if (filterEndTime.value) params.endTime = filterEndTime.value

    const res = await bwEventApi.list(params) as any
    if (res?.success) {
      events.value = res.events || []
    }
  } catch {
    showError('加载检测事件失败')
  } finally {
    loading.value = false
  }
}

const clearEvents = async () => {
  try {
    const res = await bwEventApi.clear() as any
    if (res?.success) {
      events.value = []
      showSuccess('检测事件已清除')
    }
  } catch {
    showError('清除失败')
  }
}

// 自动刷新
let refreshTimer: ReturnType<typeof setInterval> | null = null

onMounted(() => {
  loadEvents()
  refreshTimer = setInterval(loadEvents, 15000)
})

onUnmounted(() => {
  if (refreshTimer) clearInterval(refreshTimer)
})

// 工具函数
const formatDateTime = (dateStr: string): string => {
  if (!dateStr) return '-'
  const d = new Date(dateStr)
  if (isNaN(d.getTime())) return '-'
  const pad = (n: number) => n.toString().padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
}

const formatDuration = (minutes: number): string => {
  if (minutes < 1) return '0 分钟'
  if (minutes < 60) return `${minutes} 分钟`
  const h = Math.floor(minutes / 60)
  const m = minutes % 60
  return m > 0 ? `${h} 小时 ${m} 分钟` : `${h} 小时`
}

const formatNum = (n: number): string => {
  if (n >= 10000) return (n / 10000).toFixed(1) + '万'
  return n.toString()
}
</script>

<template>
  <div class="space-y-4">
    <!-- 标题栏 -->
    <div class="card">
      <div class="flex items-center gap-3">
        <h2 class="text-lg font-semibold text-gray-100">黑白名单</h2>
        <div class="flex items-center bg-dark-bg rounded-lg p-0.5 border border-dark-border">
          <button class="px-3 py-1 text-xs font-medium rounded-md bg-primary-500/20 text-primary-400 border border-primary-500/30">
            检测事件
          </button>
        </div>
      </div>
    </div>

    <!-- 过滤栏 -->
    <div class="card !py-3">
      <div class="flex items-center gap-3">
        <input v-model="filterIp" type="text" class="input w-40 text-sm" placeholder="源 IP" />
        <input v-model="filterDomain" type="text" class="input w-48 text-sm" placeholder="域名" />
        <input v-model="filterStartTime" type="datetime-local" class="input w-44 text-sm" />
        <input v-model="filterEndTime" type="datetime-local" class="input w-44 text-sm" />
        <div class="flex items-center gap-2 ml-auto">
          <button v-if="events.length > 0" @click="clearEvents" class="btn btn-sm btn-secondary text-xs">
            清除事件
          </button>
          <button @click="loadEvents" class="btn btn-sm btn-primary flex items-center gap-1.5">
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
            </svg>
            刷新
          </button>
        </div>
      </div>
    </div>

    <!-- 加载中 -->
    <div v-if="loading && events.length === 0" class="card flex items-center justify-center py-12">
      <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
    </div>

    <!-- 检测事件表格 -->
    <template v-else>
      <div v-if="events.length === 0" class="card text-center py-12 text-gray-500">
        <p class="text-lg mb-2">暂无检测事件</p>
        <p class="text-sm">黑白名单规则命中后，检测事件将在此处显示</p>
      </div>

      <div v-else class="card !p-0 overflow-hidden">
        <div class="overflow-x-auto">
          <table class="w-full text-sm">
            <thead>
              <tr class="border-b border-dark-border text-left text-xs text-gray-500">
                <th class="px-4 py-3">源 IP</th>
                <th class="px-4 py-3">应用</th>
                <th class="px-4 py-3 w-28 text-center">命中次数</th>
                <th class="px-4 py-3 w-28">持续时间</th>
                <th class="px-4 py-3 w-40">开始时间</th>
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="(event, idx) in events"
                :key="idx"
                class="border-b border-dark-border/50 hover:bg-white/[0.03] transition-colors"
              >
                <td class="px-4 py-3">
                  <div>
                    <span class="text-gray-200 font-mono text-sm">{{ event.sourceIp }}</span>
                  </div>
                  <div v-if="event.region || event.city" class="text-xs text-gray-500 mt-0.5">
                    {{ event.region }}<template v-if="event.region && event.city"> - </template>{{ event.city }}
                  </div>
                </td>
                <td class="px-4 py-3">
                  <span class="text-gray-300 text-sm">{{ event.application }}</span>
                </td>
                <td class="px-4 py-3 text-center">
                  <span class="inline-flex items-center justify-center min-w-[28px] px-2 py-0.5 text-xs font-medium rounded bg-red-500/20 text-red-400 border border-red-500/30">
                    {{ formatNum(event.hitCount) }}
                  </span>
                </td>
                <td class="px-4 py-3 text-gray-400 text-sm">
                  {{ formatDuration(event.duration) }}
                </td>
                <td class="px-4 py-3 text-gray-400 text-sm font-mono">
                  {{ formatDateTime(event.firstHitTime) }}
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </template>
  </div>
</template>
