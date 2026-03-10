<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch } from 'vue'
import { interceptEventApi, interceptLogApi } from '@/api'
import type { InterceptEvent, InterceptLogFile, InterceptLogEntry } from '@/api'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()

// ==================== Tab 切换 ====================
const activeTab = ref<'events' | 'details'>('events')

// ==================== 拦截事件（聚合视图） ====================
const loading = ref(false)
const events = ref<InterceptEvent[]>([])

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

    const res = await interceptEventApi.list(params) as any
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
    const res = await interceptEventApi.clear() as any
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

// ==================== 拦截明细（日志查看器） ====================
const logFiles = ref<InterceptLogFile[]>([])
const selectedFile = ref('')
const logEntries = ref<InterceptLogEntry[]>([])
const logTotal = ref(0)
const logOffset = ref(0)
const logLimit = 20
const logSearch = ref('')
const logLoading = ref(false)
const expandedEntries = ref<Set<number>>(new Set())

const loadLogFiles = async () => {
  try {
    const res = await interceptLogApi.listFiles() as any
    if (res?.success) {
      logFiles.value = res.files || []
      // 默认选中最新文件
      if (logFiles.value.length > 0 && !selectedFile.value) {
        selectedFile.value = logFiles.value[0].name
      }
    }
  } catch {
    showError('加载日志文件列表失败')
  }
}

const loadLogEntries = async () => {
  if (!selectedFile.value) return
  logLoading.value = true
  try {
    const res = await interceptLogApi.getEntries({
      file: selectedFile.value,
      offset: logOffset.value,
      limit: logLimit,
      search: logSearch.value || undefined,
    }) as any
    if (res?.success) {
      logEntries.value = res.entries || []
      logTotal.value = res.total || 0
      expandedEntries.value.clear()
    }
  } catch {
    showError('加载日志条目失败')
  } finally {
    logLoading.value = false
  }
}

const onFileChange = () => {
  logOffset.value = 0
  expandedEntries.value.clear()
  loadLogEntries()
}

const onSearchEnter = () => {
  logOffset.value = 0
  loadLogEntries()
}

const toggleEntry = (idx: number) => {
  if (expandedEntries.value.has(idx)) {
    expandedEntries.value.delete(idx)
  } else {
    expandedEntries.value.add(idx)
  }
}

const totalPages = () => Math.ceil(logTotal.value / logLimit) || 1
const currentPage = () => Math.floor(logOffset.value / logLimit) + 1

const goPage = (page: number) => {
  const maxPage = totalPages()
  if (page < 1 || page > maxPage) return
  logOffset.value = (page - 1) * logLimit
  loadLogEntries()
}

// Tab 切换时加载数据
watch(activeTab, (tab) => {
  if (tab === 'details' && logFiles.value.length === 0) {
    loadLogFiles()
  }
})

// 选中文件后加载条目
watch(selectedFile, (f) => {
  if (f) {
    logOffset.value = 0
    loadLogEntries()
  }
})

const formatFileSize = (bytes: number): string => {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

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
    <!-- 标题栏 + Tab -->
    <div class="card">
      <div class="flex items-center gap-6">
        <button
          @click="activeTab = 'events'"
          :class="[
            'text-base font-semibold pb-1 border-b-2 transition-colors',
            activeTab === 'events'
              ? 'text-gray-100 border-primary-500'
              : 'text-gray-500 border-transparent hover:text-gray-300'
          ]"
        >拦截事件</button>
        <button
          @click="activeTab = 'details'"
          :class="[
            'text-base font-semibold pb-1 border-b-2 transition-colors',
            activeTab === 'details'
              ? 'text-gray-100 border-primary-500'
              : 'text-gray-500 border-transparent hover:text-gray-300'
          ]"
        >拦截明细</button>
      </div>
    </div>

    <!-- ==================== 拦截事件 Tab ==================== -->
    <template v-if="activeTab === 'events'">
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
          <p class="text-sm">拦截事件将在此处显示</p>
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
    </template>

    <!-- ==================== 拦截明细 Tab ==================== -->
    <template v-if="activeTab === 'details'">
      <!-- 工具栏 -->
      <div class="card !py-3">
        <div class="flex items-center gap-3">
          <select v-model="selectedFile" @change="onFileChange" class="input w-56 text-sm">
            <option value="" disabled>选择日志文件</option>
            <option v-for="f in logFiles" :key="f.name" :value="f.name">
              {{ f.name }} ({{ formatFileSize(f.size) }})
            </option>
          </select>
          <input
            v-model="logSearch"
            type="text"
            class="input w-56 text-sm"
            placeholder="搜索关键字..."
            @keydown.enter="onSearchEnter"
          />
          <button @click="onSearchEnter" class="btn btn-sm btn-primary flex items-center gap-1.5">
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
            </svg>
            搜索
          </button>
          <button @click="loadLogFiles" class="btn btn-sm btn-secondary flex items-center gap-1.5">
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
            </svg>
            刷新
          </button>
          <div class="ml-auto text-xs text-gray-500" v-if="logTotal > 0">
            共 {{ logTotal }} 条记录
          </div>
        </div>
      </div>

      <!-- 加载中 -->
      <div v-if="logLoading && logEntries.length === 0" class="card flex items-center justify-center py-12">
        <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
      </div>

      <template v-else>
        <!-- 空状态 -->
        <div v-if="logFiles.length === 0" class="card text-center py-12 text-gray-500">
          <p class="text-lg mb-2">暂无拦截日志</p>
          <p class="text-sm">WAF 主动拦截的请求明细将记录在此</p>
        </div>

        <div v-else-if="logEntries.length === 0 && selectedFile" class="card text-center py-12 text-gray-500">
          <p class="text-lg mb-2">该文件暂无匹配记录</p>
          <p class="text-sm">{{ logSearch ? '尝试更换搜索关键字' : '选择其他日志文件' }}</p>
        </div>

        <!-- 日志条目列表 -->
        <div v-else class="space-y-2">
          <div
            v-for="entry in logEntries"
            :key="entry.index"
            class="card !p-0 overflow-hidden"
          >
            <!-- 摘要行（可点击展开） -->
            <div
              @click="toggleEntry(entry.index)"
              class="flex items-center gap-3 px-4 py-2.5 cursor-pointer hover:bg-white/[0.03] transition-colors"
            >
              <svg
                :class="['w-3.5 h-3.5 text-gray-500 transition-transform flex-shrink-0', expandedEntries.has(entry.index) && 'rotate-90']"
                fill="none" stroke="currentColor" viewBox="0 0 24 24"
              >
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M9 5l7 7-7 7" />
              </svg>
              <span class="text-xs text-gray-500 font-mono w-40 flex-shrink-0">{{ entry.time }}</span>
              <span v-if="entry.statusCode" class="px-1.5 py-0.5 text-[10px] font-medium rounded bg-red-500/15 text-red-400 flex-shrink-0">
                {{ entry.statusCode }}
              </span>
              <span class="text-xs text-gray-400 font-mono">{{ entry.method }}</span>
              <span class="text-xs text-blue-400 font-mono truncate" :title="entry.url">{{ entry.url }}</span>
              <span class="text-xs text-gray-600 ml-auto flex-shrink-0">{{ entry.clientIp }}</span>
            </div>
            <!-- 展开详情 -->
            <div v-if="expandedEntries.has(entry.index)" class="border-t border-dark-border/50">
              <pre class="px-4 py-3 text-xs text-gray-300 font-mono whitespace-pre-wrap break-all bg-dark-card-hover/50 overflow-x-auto max-h-[500px]">{{ entry.raw }}</pre>
            </div>
          </div>
        </div>

        <!-- 分页 -->
        <div v-if="logTotal > logLimit" class="flex items-center justify-center gap-2 py-2">
          <button
            @click="goPage(currentPage() - 1)"
            :disabled="currentPage() <= 1"
            class="btn btn-sm btn-secondary text-xs"
          >上一页</button>
          <span class="text-xs text-gray-400">{{ currentPage() }} / {{ totalPages() }}</span>
          <button
            @click="goPage(currentPage() + 1)"
            :disabled="currentPage() >= totalPages()"
            class="btn btn-sm btn-secondary text-xs"
          >下一页</button>
        </div>
      </template>
    </template>
  </div>
</template>
