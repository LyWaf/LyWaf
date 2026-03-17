<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch } from 'vue'
import { pcapApi } from '@/api'
import type { PcapPortStatus, InterceptLogFile, InterceptLogEntry } from '@/api'
import { useToast } from '@/composables/useToast'
import DateTimePicker from '@/components/common/DateTimePicker.vue'

const { showSuccess, showError } = useToast()

// ==================== Tab ====================
const activeTab = ref<'settings' | 'details'>('settings')

// ==================== 抓包设置 ====================
const loading = ref(false)
const ports = ref<PcapPortStatus[]>([])
const caCertExists = ref(false)
const togglingPort = ref<string | null>(null)

const loadStatus = async () => {
  loading.value = true
  try {
    const res = await pcapApi.getStatus() as any
    if (res?.success) {
      ports.value = res.ports || []
      caCertExists.value = res.caCertExists
    }
  } catch {
    showError('加载抓包状态失败')
  } finally {
    loading.value = false
  }
}

const togglePcap = async (port: PcapPortStatus) => {
  togglingPort.value = port.port
  try {
    const res = await pcapApi.toggle(port.port, !port.enablePcap) as any
    if (res?.success) {
      port.enablePcap = res.enabled
      caCertExists.value = true
      showSuccess(res.message)
    }
  } catch {
    showError('切换失败')
  } finally {
    togglingPort.value = null
  }
}

const downloadCert = async () => {
  try {
    await pcapApi.downloadCaCert()
    showSuccess('证书已下载')
  } catch {
    showError('证书下载失败')
  }
}

// ==================== 安装说明折叠 ====================
const expandedGuide = ref<string | null>(null)
const toggleGuide = (id: string) => {
  expandedGuide.value = expandedGuide.value === id ? null : id
}

// ==================== 抓包明细 ====================
const logFiles = ref<InterceptLogFile[]>([])
const selectedFile = ref('')
const logEntries = ref<InterceptLogEntry[]>([])
const logTotal = ref(0)
const logOffset = ref(0)
const logLimit = 20
const logSearch = ref('')
const logHost = ref('')
const logClientIp = ref('')
const logStartTime = ref('')
const logEndTime = ref('')
const logLoading = ref(false)

const loadLogFiles = async () => {
  try {
    const res = await pcapApi.listLogFiles() as any
    if (res?.success) {
      logFiles.value = res.files || []
      if (logFiles.value.length > 0 && !selectedFile.value) {
        selectedFile.value = logFiles.value[0].name
      }
    }
  } catch {
    showError('加载日志文件列表失败')
  }
}

const deleteLogFile = async () => {
  if (!selectedFile.value) return
  if (!confirm(`确定要删除日志文件 ${selectedFile.value} 吗？此操作不可恢复。`)) return
  try {
    const res = await pcapApi.deleteLogFile(selectedFile.value) as any
    if (res?.success) {
      const idx = logFiles.value.findIndex(f => f.name === selectedFile.value)
      logFiles.value.splice(idx, 1)
      selectedFile.value = logFiles.value.length > 0 ? logFiles.value[0].name : ''
      logEntries.value = []
      logTotal.value = 0
    } else {
      showError(res?.message || '删除失败')
    }
  } catch {
    showError('删除日志文件失败')
  }
}

const loadLogEntries = async () => {
  if (!selectedFile.value) return
  logLoading.value = true
  try {
    const res = await pcapApi.getLogEntries({
      file: selectedFile.value,
      offset: logOffset.value,
      limit: logLimit,
      search: logSearch.value || undefined,
      host: logHost.value || undefined,
      clientIp: logClientIp.value || undefined,
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

// ==================== 分页 ====================
const totalPages = () => Math.ceil(logTotal.value / logLimit) || 1
const currentPage = () => Math.floor(logOffset.value / logLimit) + 1
const jumpPageInput = ref('')

const goPage = (page: number) => {
  const maxPage = totalPages()
  if (page < 1 || page > maxPage) return
  logOffset.value = (page - 1) * logLimit
  loadLogEntries()
}

const handleJumpPage = () => {
  const page = parseInt(jumpPageInput.value)
  if (!isNaN(page)) {
    goPage(page)
    jumpPageInput.value = ''
  }
}

// ==================== 生命周期 ====================
let refreshTimer: ReturnType<typeof setInterval> | null = null

onMounted(() => {
  loadStatus()
  refreshTimer = setInterval(loadStatus, 30000)
})

onUnmounted(() => {
  if (refreshTimer) clearInterval(refreshTimer)
})

watch(activeTab, (tab) => {
  if (tab === 'details' && logFiles.value.length === 0) {
    loadLogFiles()
  }
})

watch(selectedFile, (f) => {
  if (f) {
    logOffset.value = 0
    loadLogEntries()
  }
})

// ==================== 工具函数 ====================
const formatFileSize = (bytes: number): string => {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

const getMethodClass = (method: string) => {
  const map: Record<string, string> = {
    GET: 'bg-green-500/15 text-green-400 border-green-500/30',
    POST: 'bg-blue-500/15 text-blue-400 border-blue-500/30',
    PUT: 'bg-yellow-500/15 text-yellow-400 border-yellow-500/30',
    DELETE: 'bg-red-500/15 text-red-400 border-red-500/30',
  }
  return map[method] || 'bg-gray-500/15 text-gray-400 border-gray-500/30'
}
</script>

<template>
  <div class="space-y-4">
    <!-- Tab -->
    <div class="card">
      <div class="flex items-center gap-6">
        <button
          @click="activeTab = 'settings'"
          :class="[
            'text-base font-semibold pb-1 border-b-2 transition-colors',
            activeTab === 'settings'
              ? 'text-gray-100 border-primary-500'
              : 'text-gray-500 border-transparent hover:text-gray-300'
          ]"
        >抓包设置</button>
        <button
          @click="activeTab = 'details'"
          :class="[
            'text-base font-semibold pb-1 border-b-2 transition-colors',
            activeTab === 'details'
              ? 'text-gray-100 border-primary-500'
              : 'text-gray-500 border-transparent hover:text-gray-300'
          ]"
        >抓包明细</button>
      </div>
    </div>

    <!-- ==================== 抓包设置 Tab ==================== -->
    <template v-if="activeTab === 'settings'">
      <!-- 加载中 -->
      <div v-if="loading && ports.length === 0" class="card flex items-center justify-center py-12">
        <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
      </div>

      <template v-else>
        <!-- 端口抓包开关 -->
        <div class="card">
          <h3 class="text-sm font-semibold text-gray-200 mb-4">端口抓包开关</h3>
          <div v-if="ports.length === 0" class="text-sm text-gray-500 py-4 text-center">
            没有配置代理端口
          </div>
          <div v-else class="space-y-2">
            <div
              v-for="port in ports"
              :key="port.port"
              @click="togglePcap(port)"
              :class="[
                'flex items-center justify-between px-4 py-3 rounded-lg cursor-pointer transition-colors',
                port.enablePcap
                  ? 'bg-green-500/5 border border-green-500/20 hover:bg-green-500/10'
                  : 'bg-dark-card-hover border border-dark-border hover:bg-white/5'
              ]"
            >
              <div>
                <span class="text-gray-200 text-sm font-medium">端口 {{ port.port }}</span>
                <div class="flex items-center gap-2 mt-1">
                  <span v-if="port.enableHttp" class="text-[10px] px-1.5 py-0.5 rounded bg-blue-500/15 text-blue-400 border border-blue-500/30">HTTP</span>
                  <span v-if="port.enableHttps" class="text-[10px] px-1.5 py-0.5 rounded bg-green-500/15 text-green-400 border border-green-500/30">HTTPS</span>
                  <span v-if="port.enableSocks5" class="text-[10px] px-1.5 py-0.5 rounded bg-purple-500/15 text-purple-400 border border-purple-500/30">SOCKS5</span>
                </div>
              </div>
              <div class="flex items-center gap-2">
                <span v-if="togglingPort === port.port" class="text-gray-400 text-sm">切换中...</span>
                <span
                  :class="[
                    'text-xs px-2 py-1 rounded font-medium',
                    port.enablePcap
                      ? 'bg-green-500/20 text-green-400'
                      : 'bg-gray-500/20 text-gray-400'
                  ]"
                >{{ port.enablePcap ? '已启用' : '已禁用' }}</span>
              </div>
            </div>
          </div>
        </div>

        <!-- CA 证书 -->
        <div class="card">
          <h3 class="text-sm font-semibold text-gray-200 mb-4">CA 证书</h3>
          <div class="flex items-center gap-4">
            <div class="flex-1">
              <p class="text-sm text-gray-400">
                HTTPS 抓包需要客户端安装并信任 CA 根证书。点击下载证书后，按照下方说明安装到对应设备。
              </p>
              <div class="flex items-center gap-2 mt-2">
                <span :class="['w-2 h-2 rounded-full', caCertExists ? 'bg-green-500' : 'bg-gray-500']"></span>
                <span class="text-xs" :class="caCertExists ? 'text-green-400' : 'text-gray-500'">
                  {{ caCertExists ? 'CA 证书已生成' : 'CA 证书未生成（启用抓包后自动生成）' }}
                </span>
              </div>
            </div>
            <button
              @click="downloadCert"
              :disabled="!caCertExists"
              class="btn btn-sm btn-primary flex items-center gap-1.5"
            >
              <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 10v6m0 0l-3-3m3 3l3-3m2 8H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
              </svg>
              下载证书
            </button>
          </div>
        </div>

        <!-- 安装说明 -->
        <div class="card">
          <h3 class="text-sm font-semibold text-gray-200 mb-4">证书安装说明</h3>
          <div class="space-y-2">
            <!-- Windows -->
            <div class="border border-dark-border rounded-lg overflow-hidden">
              <div
                @click="toggleGuide('windows')"
                class="flex items-center justify-between px-4 py-3 cursor-pointer hover:bg-white/5 transition-colors"
              >
                <div class="flex items-center gap-3">
                  <span class="text-base">🖥️</span>
                  <span class="text-sm text-gray-200 font-medium">Windows</span>
                </div>
                <span class="text-xs text-gray-500 transition-transform duration-200" :class="expandedGuide === 'windows' ? 'rotate-90' : ''">›</span>
              </div>
              <div v-show="expandedGuide === 'windows'" class="px-4 pb-4 text-sm text-gray-400 space-y-2 border-t border-dark-border/50 pt-3">
                <p>1. 双击下载的 <code class="text-primary-400">proxy-ca.crt</code> 文件</p>
                <p>2. 点击「安装证书」</p>
                <p>3. 选择「本地计算机」，点击下一步</p>
                <p>4. 选择「将所有的证书都放入下列存储」，点击浏览</p>
                <p>5. 选择「受信任的根证书颁发机构」，确定</p>
                <p>6. 完成安装，重启浏览器</p>
              </div>
            </div>

            <!-- Android -->
            <div class="border border-dark-border rounded-lg overflow-hidden">
              <div
                @click="toggleGuide('android')"
                class="flex items-center justify-between px-4 py-3 cursor-pointer hover:bg-white/5 transition-colors"
              >
                <div class="flex items-center gap-3">
                  <span class="text-base">📱</span>
                  <span class="text-sm text-gray-200 font-medium">Android</span>
                </div>
                <span class="text-xs text-gray-500 transition-transform duration-200" :class="expandedGuide === 'android' ? 'rotate-90' : ''">›</span>
              </div>
              <div v-show="expandedGuide === 'android'" class="px-4 pb-4 text-sm text-gray-400 space-y-2 border-t border-dark-border/50 pt-3">
                <p>1. 将 <code class="text-primary-400">proxy-ca.crt</code> 传输到手机</p>
                <p>2. 打开「设置」→「安全」→「加密与凭据」</p>
                <p>3. 点击「安装证书」→「CA 证书」</p>
                <p>4. 选择下载的证书文件，确认安装</p>
                <p>5. 在 WiFi 设置中配置代理服务器地址和端口</p>
                <p class="text-yellow-400/80 text-xs mt-2">注意：Android 7.0+ 用户应用可能不信任用户安装的 CA 证书，部分应用的 HTTPS 流量可能无法抓取</p>
              </div>
            </div>

            <!-- iOS -->
            <div class="border border-dark-border rounded-lg overflow-hidden">
              <div
                @click="toggleGuide('ios')"
                class="flex items-center justify-between px-4 py-3 cursor-pointer hover:bg-white/5 transition-colors"
              >
                <div class="flex items-center gap-3">
                  <span class="text-base">📱</span>
                  <span class="text-sm text-gray-200 font-medium">iOS / iPadOS</span>
                </div>
                <span class="text-xs text-gray-500 transition-transform duration-200" :class="expandedGuide === 'ios' ? 'rotate-90' : ''">›</span>
              </div>
              <div v-show="expandedGuide === 'ios'" class="px-4 pb-4 text-sm text-gray-400 space-y-2 border-t border-dark-border/50 pt-3">
                <p>1. 使用 Safari 浏览器下载 <code class="text-primary-400">proxy-ca.crt</code></p>
                <p>2. 打开「设置」→「已下载描述文件」→ 点击安装</p>
                <p>3. 进入「设置」→「通用」→「关于本机」→「证书信任设置」</p>
                <p>4. 开启「LyWaf Proxy CA」的完全信任</p>
                <p>5. 在 WiFi 设置中配置 HTTP 代理</p>
              </div>
            </div>
          </div>
        </div>
      </template>
    </template>

    <!-- ==================== 抓包明细 Tab ==================== -->
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
            class="input w-44 text-sm"
            placeholder="搜索关键字..."
            @keydown.enter="onSearchEnter"
          />
          <input
            v-model="logHost"
            type="text"
            class="input w-40 text-sm"
            placeholder="过滤域名..."
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
          <input
            v-model="logClientIp"
            type="text"
            class="input w-32 text-sm"
            placeholder="客户端IP..."
            @keydown.enter="onSearchEnter"
          />
          <button @click="loadLogFiles" class="btn btn-sm btn-secondary flex items-center gap-1.5">
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
            </svg>
            刷新
          </button>
          <button v-if="selectedFile" @click="deleteLogFile" class="btn btn-sm flex items-center gap-1.5" style="background: #dc2626; color: white;">
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
            </svg>
            删除
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
          <p class="text-lg mb-2">暂无抓包日志</p>
          <p class="text-sm">启用抓包后，通过代理访问的请求将记录在此</p>
        </div>

        <div v-else-if="logEntries.length === 0 && selectedFile" class="card text-center py-12 text-gray-500">
          <p class="text-lg mb-2">该文件暂无匹配记录</p>
          <p class="text-sm">{{ logSearch ? '尝试更换搜索关键字' : '选择其他日志文件' }}</p>
        </div>

        <!-- 抓包日志表格 -->
        <div v-else class="card !p-0 overflow-hidden">
          <div class="overflow-x-auto">
            <table class="w-full text-sm">
              <thead>
                <tr class="border-b border-dark-border text-left text-xs text-gray-500">
                  <th class="px-4 py-3 w-16">状态</th>
                  <th class="px-4 py-3">请求地址</th>
                  <th class="px-4 py-3 w-36">Host</th>
                  <th class="px-4 py-3 w-28">客户端</th>
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
                    <span
                      class="inline-flex items-center px-1.5 py-0.5 text-[10px] font-semibold rounded border"
                      :class="{
                        'bg-green-500/15 text-green-400 border-green-500/30': entry.statusCode && entry.statusCode < 300,
                        'bg-yellow-500/15 text-yellow-400 border-yellow-500/30': entry.statusCode && entry.statusCode >= 300 && entry.statusCode < 400,
                        'bg-red-500/15 text-red-400 border-red-500/30': entry.statusCode && entry.statusCode >= 400,
                        'bg-gray-500/15 text-gray-400 border-gray-500/30': !entry.statusCode,
                      }"
                    >{{ entry.statusCode || '-' }}</span>
                  </td>
                  <td class="px-4 py-3">
                    <div class="flex items-center gap-2 min-w-0">
                      <span
                        class="inline-flex items-center px-1.5 py-0.5 text-[10px] font-semibold rounded border flex-shrink-0"
                        :class="getMethodClass(entry.method)"
                      >{{ entry.method }}</span>
                      <span class="text-gray-300 text-sm font-mono truncate" :title="(entry.host || '') + entry.url">
                        {{ entry.url }}
                      </span>
                    </div>
                  </td>
                  <td class="px-4 py-3">
                    <span class="text-gray-400 text-xs font-mono">{{ entry.host || '-' }}</span>
                  </td>
                  <td class="px-4 py-3">
                    <span class="text-gray-400 text-xs font-mono">{{ entry.clientIp || '-' }}</span>
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
          <input
            v-model="jumpPageInput"
            @keyup.enter="handleJumpPage"
            type="text" inputmode="numeric"
            placeholder="页码"
            class="w-16 text-center text-xs bg-dark-card-hover border border-dark-border rounded px-1.5 py-1 text-gray-200 placeholder-gray-500 focus:outline-none focus:border-primary-500"
          />
          <button @click="handleJumpPage" class="btn btn-sm btn-secondary text-xs">跳转</button>
        </div>
      </template>
    </template>

    <!-- ==================== 详情弹窗 ==================== -->
    <Teleport to="body">
      <div v-if="detailEntry" class="fixed inset-0 z-50 flex items-center justify-center" @click.self="closeDetail">
        <div class="absolute inset-0 bg-black/60 backdrop-blur-sm" @click="closeDetail"></div>
        <div class="relative w-[800px] max-w-[90vw] max-h-[85vh] flex flex-col bg-dark-card border border-dark-border rounded-lg shadow-2xl overflow-hidden">
          <!-- 头部 -->
          <div class="px-6 pt-5 pb-4 border-b border-dark-border">
            <div class="flex items-center gap-3 mb-4">
              <span
                class="inline-flex items-center px-2.5 py-1 text-xs font-bold rounded border"
                :class="{
                  'bg-green-500/15 text-green-400 border-green-500/30': detailEntry.statusCode && detailEntry.statusCode < 300,
                  'bg-yellow-500/15 text-yellow-400 border-yellow-500/30': detailEntry.statusCode && detailEntry.statusCode >= 300 && detailEntry.statusCode < 400,
                  'bg-red-500/15 text-red-400 border-red-500/30': detailEntry.statusCode && detailEntry.statusCode >= 400,
                  'bg-gray-500/15 text-gray-400 border-gray-500/30': !detailEntry.statusCode,
                }"
              >{{ detailEntry.statusCode || '-' }}</span>
              <span
                class="inline-flex items-center px-1.5 py-0.5 text-[10px] font-semibold rounded border"
                :class="getMethodClass(detailEntry.method)"
              >{{ detailEntry.method }}</span>
              <span class="text-gray-200 text-sm font-mono truncate" :title="(detailEntry.host || '') + detailEntry.url">
                https://{{ detailEntry.host }}{{ detailEntry.url }}
              </span>
            </div>
            <div class="grid grid-cols-[auto_1fr] gap-x-6 gap-y-2 text-sm">
              <span class="text-gray-500">Host</span>
              <span class="text-gray-200 font-mono">{{ detailEntry.host || '-' }}</span>

              <span class="text-gray-500">时间</span>
              <span class="text-gray-200 font-mono">{{ detailEntry.time }}</span>

              <span class="text-gray-500">状态码</span>
              <span class="text-gray-200 font-mono">{{ detailEntry.statusCode || '-' }}</span>
            </div>
          </div>

          <!-- 请求/响应 Tab -->
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
