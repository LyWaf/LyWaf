<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch } from 'vue'
import { interceptEventApi, interceptLogApi } from '@/api'
import type { InterceptEvent, InterceptLogFile, InterceptLogEntry } from '@/api'
import { useToast } from '@/composables/useToast'
import DateTimePicker from '@/components/common/DateTimePicker.vue'

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
const logStartTime = ref('')
const logEndTime = ref('')
const logLoading = ref(false)

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
      startTime: logStartTime.value || undefined,
      endTime: logEndTime.value || undefined,
    }) as any
    if (res?.success) {
      logEntries.value = res.entries || []
      logTotal.value = res.total || 0
    }
  } catch {
    showError('加载日志条目失败')
  } finally {
    logLoading.value = false
  }
}

const onFileChange = () => {
  logOffset.value = 0
  loadLogEntries()
}

const onSearchEnter = () => {
  logOffset.value = 0
  loadLogEntries()
}

// ==================== 详情弹窗 ====================
const detailEntry = ref<InterceptLogEntry | null>(null)
const detailTab = ref<'request' | 'response'>('request')

const openDetail = (entry: InterceptLogEntry) => {
  detailEntry.value = entry
  detailTab.value = 'request'
}

const closeDetail = () => {
  detailEntry.value = null
  curlMenuOpen.value = false
}

const getActionLabel = (entry: InterceptLogEntry) => {
  if (entry.statusCode === 403) return '已拦截'
  if (entry.statusCode && entry.statusCode >= 400) return `${entry.statusCode}`
  return '观察'
}

const getActionClass = (entry: InterceptLogEntry) => {
  if (entry.statusCode === 403) return 'bg-red-500/20 text-red-400 border border-red-500/30'
  if (entry.statusCode && entry.statusCode >= 400) return 'bg-orange-500/20 text-orange-400 border border-orange-500/30'
  return 'bg-blue-500/20 text-blue-400 border border-blue-500/30'
}

const getRequestContent = (entry: InterceptLogEntry): string => {
  const lines: string[] = []
  lines.push(`${entry.method} ${entry.url} HTTP/1.1`)
  if (entry.host) lines.push(`Host: ${entry.host}`)
  if (entry.headers) lines.push(entry.headers)
  if (entry.requestBody) {
    lines.push('')
    lines.push(entry.requestBody)
  }
  return lines.join('\n')
}

const getResponseContent = (entry: InterceptLogEntry): string => {
  if (entry.responseBody) return entry.responseBody
  return '(无响应体数据)'
}

type CurlFormat = 'bash' | 'cmd' | 'powershell'
const curlMenuOpen = ref(false)

const buildCurl = (entry: InterceptLogEntry, format: CurlFormat): string => {
  const method = entry.method || 'GET'
  const host = entry.host || 'localhost'
  const url = entry.url || '/'
  const scheme = entry.scheme || 'http'
  const fullUrl = `${scheme}://${host}${url}`

  const q = format === 'bash' ? "'" : '"'
  const cont = format === 'bash' ? ' \\' : format === 'cmd' ? ' ^' : ' `'
  const exe = format === 'powershell' ? 'curl.exe' : 'curl'

  const parts: string[] = [`${exe} -X ${method} ${q}${fullUrl}${q}`]

  if (entry.headers) {
    for (const line of entry.headers.split('\n')) {
      const colonIdx = line.indexOf(':')
      if (colonIdx > 0) {
        const key = line.substring(0, colonIdx).trim()
        const value = line.substring(colonIdx + 1).trim()
        if (!['host', 'content-length', 'transfer-encoding', 'connection'].some(h => key.toLowerCase() === h)) {
          const escaped = format === 'bash' ? value.replace(/'/g, "'\\''") : value.replace(/"/g, '\\"')
          parts.push(`  -H ${q}${key}: ${escaped}${q}`)
        }
      }
    }
  }

  if (['POST', 'PUT', 'PATCH'].includes(method.toUpperCase()) && entry.requestBody) {
    const body = format === 'bash'
      ? entry.requestBody.replace(/'/g, "'\\''")
      : entry.requestBody.replace(/"/g, '\\"')
    parts.push(`  -d ${q}${body}${q}`)
  }

  return parts.join(cont + '\n')
}

const copyCurl = async (entry: InterceptLogEntry, format: CurlFormat) => {
  const curl = buildCurl(entry, format)
  try {
    await navigator.clipboard.writeText(curl)
    showSuccess(`已复制 cURL（${format === 'bash' ? 'Bash' : format === 'cmd' ? 'CMD' : 'PowerShell'}）`)
  } catch {
    showError('复制失败')
  }
  curlMenuOpen.value = false
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
          <DateTimePicker v-model="filterStartTime" placeholder="开始时间" />
          <DateTimePicker v-model="filterEndTime" placeholder="结束时间" />
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
          <DateTimePicker v-model="logStartTime" placeholder="开始时间" />
          <DateTimePicker v-model="logEndTime" placeholder="结束时间" />
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

        <!-- 拦截日志表格 -->
        <div v-else class="card !p-0 overflow-hidden">
          <div class="overflow-x-auto">
            <table class="w-full text-sm">
              <thead>
                <tr class="border-b border-dark-border text-left text-xs text-gray-500">
                  <th class="px-4 py-3 w-20">动作</th>
                  <th class="px-4 py-3">请求地址</th>
                  <th class="px-4 py-3 w-36">拦截原因</th>
                  <th class="px-4 py-3 w-36">源 IP</th>
                  <th class="px-4 py-3 w-44">时间</th>
                  <th class="px-4 py-3 w-16 text-center">详情</th>
                </tr>
              </thead>
              <tbody>
                <tr
                  v-for="entry in logEntries"
                  :key="entry.index"
                  class="border-b border-dark-border/50 hover:bg-white/[0.03] transition-colors"
                >
                  <td class="px-4 py-3">
                    <span class="inline-flex items-center px-2 py-0.5 text-xs font-medium rounded"
                      :class="getActionClass(entry)"
                    >
                      {{ getActionLabel(entry) }}
                    </span>
                  </td>
                  <td class="px-4 py-3">
                    <div class="flex items-center gap-2 min-w-0">
                      <span class="inline-flex items-center px-1.5 py-0.5 text-[10px] font-semibold rounded border flex-shrink-0"
                        :class="{
                          'bg-green-500/15 text-green-400 border-green-500/30': entry.method === 'GET',
                          'bg-blue-500/15 text-blue-400 border-blue-500/30': entry.method === 'POST',
                          'bg-yellow-500/15 text-yellow-400 border-yellow-500/30': entry.method === 'PUT',
                          'bg-red-500/15 text-red-400 border-red-500/30': entry.method === 'DELETE',
                          'bg-gray-500/15 text-gray-400 border-gray-500/30': !['GET','POST','PUT','DELETE'].includes(entry.method),
                        }"
                      >{{ entry.method }}</span>
                      <span class="text-gray-300 text-sm font-mono truncate" :title="(entry.host || '') + entry.url">
                        {{ entry.url }}
                      </span>
                    </div>
                  </td>
                  <td class="px-4 py-3">
                    <span class="text-gray-400 text-xs">{{ entry.reason || '-' }}</span>
                  </td>
                  <td class="px-4 py-3">
                    <span class="text-gray-300 font-mono text-xs">{{ entry.clientIp || '-' }}</span>
                  </td>
                  <td class="px-4 py-3 text-gray-400 text-xs font-mono whitespace-nowrap">
                    {{ entry.time }}
                  </td>
                  <td class="px-4 py-3 text-center">
                    <button @click="openDetail(entry)" class="text-primary-400 hover:text-primary-300 text-xs transition-colors">
                      详情
                    </button>
                  </td>
                </tr>
              </tbody>
            </table>
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

    <!-- ==================== 详情弹窗 ==================== -->
    <Teleport to="body">
      <div v-if="detailEntry" class="fixed inset-0 z-50 flex items-center justify-center" @click.self="closeDetail">
        <!-- 遮罩 -->
        <div class="absolute inset-0 bg-black/60 backdrop-blur-sm" @click="closeDetail"></div>
        <!-- 弹窗内容 -->
        <div class="relative w-[800px] max-w-[90vw] max-h-[85vh] flex flex-col bg-dark-card border border-dark-border rounded-lg shadow-2xl overflow-hidden" @click="curlMenuOpen = false">
          <!-- 头部：动作标签 + URL -->
          <div class="px-6 pt-5 pb-4 border-b border-dark-border">
            <div class="flex items-center gap-3 mb-4">
              <span class="inline-flex items-center px-2.5 py-1 text-xs font-bold rounded"
                :class="getActionClass(detailEntry)">
                {{ getActionLabel(detailEntry) }}
              </span>
              <span class="text-gray-200 text-sm font-mono truncate" :title="(detailEntry.host || '') + detailEntry.url">
                {{ detailEntry.scheme || 'http' }}://{{ detailEntry.host }}{{ detailEntry.url }}
              </span>
            </div>
            <!-- 信息网格 -->
            <div class="grid grid-cols-[auto_1fr] gap-x-6 gap-y-2 text-sm">
              <span class="text-gray-500">源 IP</span>
              <span class="text-gray-200 font-mono">{{ detailEntry.clientIp || '-' }}</span>

              <span class="text-gray-500">拦截原因</span>
              <span class="text-gray-200 font-semibold">{{ detailEntry.reason || '-' }}</span>

              <span class="text-gray-500">时间</span>
              <span class="text-gray-200 font-mono">{{ detailEntry.time }}</span>

              <template v-if="detailEntry.duration">
                <span class="text-gray-500">耗时</span>
                <span class="text-gray-200 font-mono">{{ detailEntry.duration }}</span>
              </template>

              <span class="text-gray-500">状态码</span>
              <span class="text-gray-200 font-mono">{{ detailEntry.statusCode || '-' }}</span>
            </div>
          </div>

          <!-- 请求/响应 Tab 切换 -->
          <div class="flex items-center gap-4 px-6 py-3 border-b border-dark-border">
            <button
              @click="detailTab = 'request'"
              :class="[
                'px-3 py-1.5 text-sm font-medium rounded transition-colors',
                detailTab === 'request'
                  ? 'bg-primary-500/20 text-primary-400 border border-primary-500/40'
                  : 'text-gray-400 hover:text-gray-200'
              ]"
            >请求报文</button>
            <button
              @click="detailTab = 'response'"
              :class="[
                'px-3 py-1.5 text-sm font-medium rounded transition-colors',
                detailTab === 'response'
                  ? 'bg-primary-500/20 text-primary-400 border border-primary-500/40'
                  : 'text-gray-400 hover:text-gray-200'
              ]"
            >响应报文</button>
            <div class="flex-1"></div>
            <div class="relative">
              <button @click.stop="curlMenuOpen = !curlMenuOpen" class="text-xs text-gray-400 hover:text-gray-200 transition-colors flex items-center gap-1">
                复制 cURL
                <svg class="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 9l-7 7-7-7" /></svg>
              </button>
              <div v-if="curlMenuOpen" class="absolute right-0 top-full mt-1 w-48 bg-dark-card border border-dark-border rounded-lg shadow-xl overflow-hidden z-10">
                <button
                  @click="copyCurl(detailEntry!, 'bash')"
                  class="w-full text-left px-3 py-2 text-sm text-gray-200 hover:bg-dark-card-hover flex items-center gap-2"
                >
                  <span class="text-green-400 text-xs font-mono w-10">Bash</span>
                  <span class="text-gray-400 text-xs">Linux / macOS</span>
                </button>
                <button
                  @click="copyCurl(detailEntry!, 'cmd')"
                  class="w-full text-left px-3 py-2 text-sm text-gray-200 hover:bg-dark-card-hover flex items-center gap-2"
                >
                  <span class="text-blue-400 text-xs font-mono w-10">CMD</span>
                  <span class="text-gray-400 text-xs">Windows</span>
                </button>
                <button
                  @click="copyCurl(detailEntry!, 'powershell')"
                  class="w-full text-left px-3 py-2 text-sm text-gray-200 hover:bg-dark-card-hover flex items-center gap-2"
                >
                  <span class="text-purple-400 text-xs font-mono w-10">PS</span>
                  <span class="text-gray-400 text-xs">PowerShell</span>
                </button>
              </div>
            </div>
          </div>

          <!-- 报文内容 -->
          <div class="flex-1 overflow-auto">
            <pre class="px-6 py-4 text-xs text-gray-300 font-mono whitespace-pre-wrap break-all leading-relaxed">{{ detailTab === 'request' ? getRequestContent(detailEntry) : getResponseContent(detailEntry) }}</pre>
          </div>

          <!-- 底部 -->
          <div class="flex items-center justify-end px-6 py-3 border-t border-dark-border">
            <button @click="closeDetail" class="btn btn-sm btn-secondary">关闭</button>
          </div>
        </div>
      </div>
    </Teleport>
  </div>
</template>
