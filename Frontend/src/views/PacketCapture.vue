<script setup lang="ts">
import { t } from '@/i18n'
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { pcapApi } from '@/api'
import type { PcapPortStatus, InterceptLogFile, InterceptLogEntry } from '@/api'
import { useToast } from '@/composables/useToast'
import DateTimePicker from '@/components/common/DateTimePicker.vue'

const { showSuccess, showError } = useToast()

// ==================== Tab ====================
const activeTab = ref<'settings' | 'pcapSettings' | 'details'>('settings')

// ==================== 代理设置 ====================
const loading = ref(false)
const ports = ref<PcapPortStatus[]>([])
const caCertExists = ref(false)
const certDownloadUrl = ref('')
const togglingPort = ref<string | null>(null)
const anyPcapEnabled = computed(() => ports.value.some(p => p.enablePcap))
watch(anyPcapEnabled, (val) => { if (!val && activeTab.value !== 'settings') activeTab.value = 'settings' })

const loadStatus = async () => {
  loading.value = true
  try {
    const res = await pcapApi.getStatus() as any
    if (res?.success) {
      ports.value = res.ports || []
      caCertExists.value = res.caCertExists
      certDownloadUrl.value = res.certDownloadUrl || ''
    }
  } catch {
    showError(t('page.8e9de544ae44'))
  } finally {
    loading.value = false
  }
}

const copyCertUrl = async () => {
  if (!certDownloadUrl.value) return
  try {
    await navigator.clipboard.writeText(certDownloadUrl.value)
    showSuccess(t('page.95158bf052ac'))
  } catch {
    // fallback
    const input = document.createElement('input')
    input.value = certDownloadUrl.value
    document.body.appendChild(input)
    input.select()
    document.execCommand('copy')
    document.body.removeChild(input)
    showSuccess(t('page.95158bf052ac'))
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
    showError(t('page.d08fd4224abc'))
  } finally {
    togglingPort.value = null
  }
}

// ==================== Pcap Hosts 编辑 ====================
const editingHostsPort = ref<string | null>(null)
const editingHostsList = ref<string[]>([])
const newHostInput = ref('')

const startEditHosts = (port: PcapPortStatus) => {
  editingHostsPort.value = port.port
  editingHostsList.value = [...(port.pcapHosts || ['*'])]
  newHostInput.value = ''
}

const cancelEditHosts = () => {
  editingHostsPort.value = null
}

const addHost = () => {
  const val = newHostInput.value.trim()
  if (!val) return
  // 支持逗号/分号分隔的批量添加
  const items = val.split(/[,;]+/).map(h => h.trim()).filter(h => h.length > 0)
  for (const item of items) {
    if (!editingHostsList.value.includes(item)) {
      // 如果添加具体域名，移除 *
      if (item !== '*' && editingHostsList.value.includes('*')) {
        editingHostsList.value = editingHostsList.value.filter(h => h !== '*')
      }
      editingHostsList.value.push(item)
    }
  }
  newHostInput.value = ''
}

const removeHost = (index: number) => {
  editingHostsList.value.splice(index, 1)
}

const saveHosts = async (port: PcapPortStatus) => {
  let hosts = editingHostsList.value.filter(h => h.length > 0)
  if (hosts.length === 0) hosts = ['*']

  togglingPort.value = port.port
  try {
    const res = await pcapApi.toggle(port.port, port.enablePcap, hosts) as any
    if (res?.success) {
      port.pcapHosts = hosts
      editingHostsPort.value = null
      showSuccess(t('page.777b64bf5727'))
    }
  } catch {
    showError(t('ui.save.failed'))
  } finally {
    togglingPort.value = null
  }
}

const downloadCert = async () => {
  try {
    await pcapApi.downloadCaCert()
    showSuccess(t('page.1fb140bb6da8'))
  } catch {
    showError(t('page.a92f3bc2257e'))
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
const logHosts = ref<string[]>([])
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
    showError(t('page.52962f65e2de'))
  }
}

const loadLogHosts = async () => {
  if (!selectedFile.value) { logHosts.value = []; return }
  try {
    const res = await pcapApi.getLogHosts(selectedFile.value) as any
    if (res?.success) logHosts.value = res.hosts || []
  } catch { logHosts.value = [] }
}

const deleteLogFile = async () => {
  if (!selectedFile.value) return
  if (!confirm(t('page.777f98ccea23', { p0: selectedFile.value }))) return
  try {
    const res = await pcapApi.deleteLogFile(selectedFile.value) as any
    if (res?.success) {
      const idx = logFiles.value.findIndex(f => f.name === selectedFile.value)
      logFiles.value.splice(idx, 1)
      selectedFile.value = logFiles.value.length > 0 ? logFiles.value[0].name : ''
      logEntries.value = []
      logTotal.value = 0
    } else {
      showError(res?.message || t('settings.deletion.failed'))
    }
  } catch {
    showError(t('page.b5e8e7e4140e'))
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
    showError(t('page.fca096011043'))
  } finally {
    logLoading.value = false
  }
}

const onFileChange = () => {
  logOffset.value = 0
  loadLogEntries()
  loadLogHosts()
}

const onSearchEnter = () => {
  logOffset.value = 0
  loadLogEntries()
}

// 防抖自动搜索（域名/客户端IP 输入时自动触发）
let debounceTimer: ReturnType<typeof setTimeout> | null = null
const debouncedSearch = () => {
  if (debounceTimer) clearTimeout(debounceTimer)
  debounceTimer = setTimeout(() => {
    logOffset.value = 0
    loadLogEntries()
  }, 500)
}

// ==================== 域名下拉组合框 ====================
const hostDropdownOpen = ref(false)
const filteredHosts = computed(() => {
  if (!logHost.value) return logHosts.value
  return logHosts.value.filter(h => h.toLowerCase().includes(logHost.value.toLowerCase()))
})
const selectHost = (host: string) => {
  logHost.value = host
  hostDropdownOpen.value = false
  logOffset.value = 0
  loadLogEntries()
}
const clearHost = () => {
  logHost.value = ''
  hostDropdownOpen.value = false
  logOffset.value = 0
  loadLogEntries()
}
const onHostInput = () => {
  hostDropdownOpen.value = true
  debouncedSearch()
}
// 点击外部关闭
const onHostBlur = () => {
  setTimeout(() => { hostDropdownOpen.value = false }, 200)
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
  return t('page.b647ecb27e1f')
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
    loadLogHosts()
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
        >{{ t('page.48b970b568a7') }}</button>
        <button
          v-if="anyPcapEnabled"
          @click="activeTab = 'pcapSettings'"
          :class="[
            'text-base font-semibold pb-1 border-b-2 transition-colors',
            activeTab === 'pcapSettings'
              ? 'text-gray-100 border-primary-500'
              : 'text-gray-500 border-transparent hover:text-gray-300'
          ]"
        >{{ t('page.03d61f288e74') }}</button>
        <button
          v-if="anyPcapEnabled"
          @click="activeTab = 'details'"
          :class="[
            'text-base font-semibold pb-1 border-b-2 transition-colors',
            activeTab === 'details'
              ? 'text-gray-100 border-primary-500'
              : 'text-gray-500 border-transparent hover:text-gray-300'
          ]"
        >{{ t('page.a6b2b496d3bf') }}</button>
      </div>
    </div>

    <!-- ==================== 代理设置 Tab ==================== -->
    <template v-if="activeTab === 'settings'">
      <!-- 加载中 -->
      <div v-if="loading && ports.length === 0" class="card flex items-center justify-center py-12">
        <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
      </div>

      <template v-else>
        <!-- 端口抓包开关 -->
        <div class="card">
          <h3 class="text-sm font-semibold text-gray-200 mb-4">{{ t('page.6671d283dabe') }}</h3>
          <div v-if="ports.length === 0" class="text-sm text-gray-500 py-4 text-center">
            {{ t('page.9ba9682a2be6') }}
          </div>
          <div v-else class="space-y-2">
            <div
              v-for="port in ports"
              :key="port.port"
              :class="[
                'rounded-lg transition-colors',
                port.enablePcap
                  ? 'bg-green-500/5 border border-green-500/20'
                  : 'bg-dark-card-hover border border-dark-border'
              ]"
            >
              <!-- 端口行：点击切换 -->
              <div
                @click="togglePcap(port)"
                class="flex items-center justify-between px-4 py-3 cursor-pointer hover:bg-white/5 transition-colors rounded-t-lg"
              >
                <div>
                  <span class="text-gray-200 text-sm font-medium">{{ t('fields.Port') }} {{ port.port }}</span>
                  <div class="flex items-center gap-2 mt-1">
                    <span v-if="port.enableHttp" class="text-[10px] px-1.5 py-0.5 rounded bg-blue-500/15 text-blue-400 border border-blue-500/30">HTTP</span>
                    <span v-if="port.enableHttps" class="text-[10px] px-1.5 py-0.5 rounded bg-green-500/15 text-green-400 border border-green-500/30">HTTPS</span>
                    <span v-if="port.enableSocks5" class="text-[10px] px-1.5 py-0.5 rounded bg-purple-500/15 text-purple-400 border border-purple-500/30">SOCKS5</span>
                  </div>
                </div>
                <div class="flex items-center gap-2">
                  <span v-if="togglingPort === port.port" class="text-gray-400 text-sm">{{ t('common.switching') }}</span>
                  <span
                    :class="[
                      'text-xs px-2 py-1 rounded font-medium',
                      port.enablePcap
                        ? 'bg-green-500/20 text-green-400'
                        : 'bg-gray-500/20 text-gray-400'
                    ]"
                  >{{ port.enablePcap ? t('common.enabled') : t('common.disabled') }}</span>
                </div>
              </div>
              <!-- 域名配置（启用时显示） -->
              <div v-if="port.enablePcap" class="px-4 pb-3 border-t border-dark-border/30">
                <div class="mt-2">
                  <!-- 查看模式 -->
                  <template v-if="editingHostsPort !== port.port">
                    <div class="flex items-center gap-2">
                      <span class="text-xs text-gray-500 flex-shrink-0">{{ t('page.3cfb16a4e352') }}</span>
                      <div class="flex items-center gap-1.5 flex-wrap flex-1 min-w-0">
                        <span
                          v-for="h in (port.pcapHosts || ['*'])"
                          :key="h"
                          class="text-[10px] px-1.5 py-0.5 rounded bg-primary-500/10 text-primary-400 border border-primary-500/20 font-mono"
                        >{{ h }}</span>
                      </div>
                      <button
                        @click.stop="startEditHosts(port)"
                        class="text-xs text-gray-400 hover:text-primary-400 transition-colors flex-shrink-0"
                      >{{ t('common.edit') }}</button>
                    </div>
                  </template>
                  <!-- 编辑模式 -->
                  <template v-else>
                    <div class="space-y-2" @click.stop>
                      <div class="flex items-center gap-2">
                        <span class="text-xs text-gray-500 flex-shrink-0">{{ t('page.3cfb16a4e352') }}</span>
                        <span class="text-[10px] text-gray-500">{{ t('page.2d3108af243c') }}</span>
                      </div>
                      <!-- 已添加的域名标签 -->
                      <div class="flex items-center gap-1.5 flex-wrap">
                        <span
                          v-for="(h, idx) in editingHostsList"
                          :key="idx"
                          class="inline-flex items-center gap-1 text-xs px-2 py-1 rounded bg-primary-500/10 text-primary-400 border border-primary-500/20 font-mono"
                        >
                          {{ h }}
                          <button
                            @click="removeHost(idx)"
                            class="hover:text-red-400 transition-colors"
                          >
                            <svg class="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12" />
                            </svg>
                          </button>
                        </span>
                      </div>
                      <!-- 添加输入 -->
                      <div class="flex items-center gap-2">
                        <input
                          v-model="newHostInput"
                          type="text"
                          class="input flex-1 text-xs font-mono"
                          :placeholder="t('page.7427d9443840')"
                          @keydown.enter.prevent="addHost"
                        />
                        <button
                          @click="addHost"
                          class="btn btn-sm btn-secondary text-xs !py-1"
                        >{{ t('common.add') }}</button>
                        <button
                          @click="saveHosts(port)"
                          class="btn btn-sm btn-primary text-xs !py-1"
                          :disabled="togglingPort === port.port"
                        >{{ t('common.save') }}</button>
                        <button
                          @click="cancelEditHosts"
                          class="btn btn-sm btn-secondary text-xs !py-1"
                        >{{ t('common.cancel') }}</button>
                      </div>
                    </div>
                  </template>
                </div>
              </div>
            </div>
          </div>
        </div>

      </template>
    </template>

    <!-- ==================== 抓包设置 Tab ==================== -->
    <template v-if="activeTab === 'pcapSettings'">
      <!-- CA 证书 -->
      <div class="card">
        <h3 class="text-sm font-semibold text-gray-200 mb-4">{{ t('page.7f42e66b9eff') }}</h3>
        <div class="flex items-center gap-4">
          <div class="flex-1">
            <p class="text-sm text-gray-400">
              {{ t('page.4a53870638fa') }}
            </p>
            <div class="flex items-center gap-2 mt-2">
              <span :class="['w-2 h-2 rounded-full', caCertExists ? 'bg-green-500' : 'bg-gray-500']"></span>
              <span class="text-xs" :class="caCertExists ? 'text-green-400' : 'text-gray-500'">
                {{ caCertExists ? t('page.1beab6437127') : t('page.65d6789c0a42') }}
              </span>
            </div>
          </div>
          <div class="flex items-center gap-2 flex-shrink-0">
            <button
              @click="downloadCert"
              :disabled="!caCertExists"
              class="btn btn-sm btn-primary flex items-center gap-1.5"
            >
              <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 10v6m0 0l-3-3m3 3l3-3m2 8H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
              </svg>
              {{ t('page.82441da63f7c') }}
            </button>
            <button
              v-if="certDownloadUrl"
              @click="copyCertUrl"
              class="btn btn-sm btn-secondary flex items-center gap-1.5"
              :title="certDownloadUrl"
            >
              <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13.828 10.172a4 4 0 00-5.656 0l-4 4a4 4 0 105.656 5.656l1.102-1.101m-.758-4.899a4 4 0 005.656 0l4-4a4 4 0 00-5.656-5.656l-1.1 1.1" />
              </svg>
              {{ t('page.b525d731a0db') }}
            </button>
          </div>
        </div>
      </div>

      <!-- 安装说明 -->
      <div class="card">
        <h3 class="text-sm font-semibold text-gray-200 mb-4">{{ t('page.dd3c00b5ab50') }}</h3>
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
              <p>{{ t('page.c9088b740a05') }} <code class="text-primary-400">proxy-ca.crt</code> {{ t('settings.file') }}</p>
              <p>{{ t('page.1d2840fc985e') }}</p>
              <p>{{ t('page.9c0a976d8340') }}</p>
              <p>{{ t('page.667554e22aef') }}</p>
              <p>{{ t('page.708f91fd8867') }}</p>
              <p>{{ t('page.654b7047f57d') }}</p>
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
              <p>{{ t('page.a1e08c37bab7') }} <code class="text-primary-400">proxy-ca.crt</code> {{ t('page.5586debd1261') }}</p>
              <p>{{ t('page.da57844f1c66') }}</p>
              <p>{{ t('page.7155f2d577a6') }}</p>
              <p>{{ t('page.dc1c6a81e336') }}</p>
              <p>{{ t('page.f3ae66d5d57a') }}</p>
              <p class="text-yellow-400/80 text-xs mt-2">{{ t('page.cf5927f081a2') }}</p>
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
              <p>{{ t('page.0a8c00f7e2f2') }} <code class="text-primary-400">proxy-ca.crt</code></p>
              <p>{{ t('page.79bb1b5068fc') }}</p>
              <p>{{ t('page.aa96a018d84e') }}</p>
              <p>{{ t('page.6e7e3a6c7514') }}</p>
              <p>{{ t('page.57978cb50761') }}</p>
            </div>
          </div>
        </div>
      </div>
    </template>

    <!-- ==================== 抓包明细 Tab ==================== -->
    <template v-if="activeTab === 'details'">
      <!-- 工具栏 -->
      <div class="card !py-3 space-y-3">
        <!-- 第一行：文件选择 + 过滤条件 -->
        <div class="flex items-center gap-3 flex-wrap">
          <select v-model="selectedFile" @change="onFileChange" class="input w-56 text-sm">
            <option value="" disabled>{{ t('page.7d1521995f82') }}</option>
            <option v-for="f in logFiles" :key="f.name" :value="f.name">
              {{ f.name }} ({{ formatFileSize(f.size) }})
            </option>
          </select>
          <input
            v-model="logSearch"
            type="text"
            class="input w-40 text-sm"
            :placeholder="t('page.7455dcc01fe4')"
            @keydown.enter="onSearchEnter"
          />
          <div class="relative w-44">
            <div class="flex items-center">
              <input
                v-model="logHost"
                type="text"
                class="input w-full text-sm !pr-12"
                :placeholder="t('page.d8681a6ff22c')"
                @input="onHostInput"
                @focus="hostDropdownOpen = true"
                @blur="onHostBlur"
                @keydown.enter="onSearchEnter"
              />
              <div class="absolute right-1 flex items-center gap-0.5">
                <button
                  v-if="logHost"
                  @mousedown.prevent="clearHost"
                  class="p-0.5 text-gray-500 hover:text-gray-300"
                  :title="t('common.clear')"
                >
                  <svg class="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12" />
                  </svg>
                </button>
                <button
                  @mousedown.prevent="hostDropdownOpen = !hostDropdownOpen"
                  class="p-0.5 text-gray-500 hover:text-gray-300"
                >
                  <svg class="w-3.5 h-3.5 transition-transform" :class="hostDropdownOpen ? 'rotate-180' : ''" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 9l-7 7-7-7" />
                  </svg>
                </button>
              </div>
            </div>
            <div
              v-show="hostDropdownOpen && filteredHosts.length > 0"
              class="absolute z-50 mt-1 w-full max-h-48 overflow-y-auto bg-dark-card border border-dark-border rounded-lg shadow-xl"
            >
              <div
                v-for="h in filteredHosts"
                :key="h"
                @mousedown.prevent="selectHost(h)"
                class="px-3 py-2 text-sm text-gray-300 cursor-pointer hover:bg-primary-500/15 hover:text-primary-400 transition-colors"
                :class="{ 'bg-primary-500/10 text-primary-400': logHost === h }"
              >{{ h }}</div>
            </div>
          </div>
          <input
            v-model="logClientIp"
            type="text"
            class="input w-32 text-sm"
            :placeholder="t('page.857cede2b6a9')"
            @input="debouncedSearch"
            @keydown.enter="onSearchEnter"
          />
          <div class="ml-auto text-xs text-gray-500" v-if="logTotal > 0">
            {{ t('page.76e547a8fa54') }} {{ logTotal }} {{ t('page.6016bf63b0ff') }}
          </div>
        </div>
        <!-- 第二行：时间过滤 + 操作按钮 -->
        <div class="flex items-center gap-3">
          <DateTimePicker v-model="logStartTime" :placeholder="t('ui.start.time')" />
          <DateTimePicker v-model="logEndTime" :placeholder="t('ui.end.time')" />
          <button @click="onSearchEnter" class="btn btn-sm btn-primary flex items-center gap-1.5">
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
            </svg>
            {{ t('common.search') }}
          </button>
          <button @click="loadLogFiles" class="btn btn-sm btn-secondary flex items-center gap-1.5">
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
            </svg>
            {{ t('common.refresh') }}
          </button>
          <button v-if="selectedFile" @click="deleteLogFile" class="btn btn-sm flex items-center gap-1.5" style="background: #dc2626; color: white;">
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
            </svg>
            {{ t('common.delete') }}
          </button>
        </div>
      </div>

      <!-- 加载中 -->
      <div v-if="logLoading && logEntries.length === 0" class="card flex items-center justify-center py-12">
        <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
      </div>

      <template v-else>
        <!-- 空状态 -->
        <div v-if="logFiles.length === 0" class="card text-center py-12 text-gray-500">
          <p class="text-lg mb-2">{{ t('page.628b7ba35925') }}</p>
          <p class="text-sm">{{ t('page.229c7f1920c7') }}</p>
        </div>

        <div v-else-if="logEntries.length === 0 && selectedFile" class="card text-center py-12 text-gray-500">
          <p class="text-lg mb-2">{{ t('page.cc0adfa62aa4') }}</p>
          <p class="text-sm">{{ logSearch ? t('page.c864a4683021') : t('page.49d4d99cfb53') }}</p>
        </div>

        <!-- 抓包日志表格 -->
        <div v-else class="card !p-0 overflow-hidden">
          <div class="overflow-x-auto">
            <table class="w-full text-sm table-fixed">
              <colgroup>
                <col style="width: 60px" />
                <col />
                <col style="width: 140px" />
                <col style="width: 110px" />
                <col style="width: 180px" />
                <col style="width: 60px" />
              </colgroup>
              <thead>
                <tr class="border-b border-dark-border text-left text-xs text-gray-500">
                  <th class="px-4 py-3">{{ t('settings.status') }}</th>
                  <th class="px-4 py-3">{{ t('page.65a1f5468eed') }}</th>
                  <th class="px-4 py-3">Host</th>
                  <th class="px-4 py-3">{{ t('page.477581b84e50') }}</th>
                  <th class="px-4 py-3">{{ t('settings.time') }}</th>
                  <th class="px-4 py-3 text-center">{{ t('page.979a332955c8') }}</th>
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
                  <td class="px-4 py-3 overflow-hidden">
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
                  <td class="px-4 py-3 overflow-hidden">
                    <span class="text-gray-400 text-xs font-mono truncate block">{{ entry.host || '-' }}</span>
                  </td>
                  <td class="px-4 py-3 overflow-hidden">
                    <span class="text-gray-400 text-xs font-mono truncate block">{{ entry.clientIp || '-' }}</span>
                  </td>
                  <td class="px-4 py-3 text-gray-400 text-xs font-mono whitespace-nowrap">
                    {{ entry.time }}
                  </td>
                  <td class="px-4 py-3 text-center">
                    <button @click="openDetail(entry)" class="text-primary-400 hover:text-primary-300 text-xs transition-colors">
                      {{ t('page.979a332955c8') }}
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
          >{{ t('common.previous') }}</button>
          <span class="text-xs text-gray-400">{{ currentPage() }} / {{ totalPages() }}</span>
          <button
            @click="goPage(currentPage() + 1)"
            :disabled="currentPage() >= totalPages()"
            class="btn btn-sm btn-secondary text-xs"
          >{{ t('common.next') }}</button>
          <input
            v-model="jumpPageInput"
            @keyup.enter="handleJumpPage"
            type="text" inputmode="numeric"
            :placeholder="t('page.0c0c4dcb33a3')"
            class="w-16 text-center text-xs bg-dark-card-hover border border-dark-border rounded px-1.5 py-1 text-gray-200 placeholder-gray-500 focus:outline-none focus:border-primary-500"
          />
          <button @click="handleJumpPage" class="btn btn-sm btn-secondary text-xs">{{ t('page.afd0b9482dfa') }}</button>
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

              <span class="text-gray-500">{{ t('settings.time') }}</span>
              <span class="text-gray-200 font-mono">{{ detailEntry.time }}</span>

              <span class="text-gray-500">{{ t('page.06f7d7341212') }}</span>
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
            >{{ t('page.81e4e1d3bc7a') }}</button>
            <button
              @click="detailTab = 'response'"
              :class="[
                'px-3 py-1.5 text-sm font-medium rounded transition-colors',
                detailTab === 'response'
                  ? 'bg-primary-500/20 text-primary-400 border border-primary-500/40'
                  : 'text-gray-400 hover:text-gray-200'
              ]"
            >{{ t('page.6f6c7fb96ae0') }}</button>
          </div>

          <!-- 报文内容 -->
          <div class="flex-1 overflow-auto">
            <pre class="px-6 py-4 text-xs text-gray-300 font-mono whitespace-pre-wrap break-all leading-relaxed">{{ detailTab === 'request' ? getRequestContent(detailEntry) : getResponseContent(detailEntry) }}</pre>
          </div>

          <!-- 底部 -->
          <div class="flex items-center justify-end px-6 py-3 border-t border-dark-border">
            <button @click="closeDetail" class="btn btn-sm btn-secondary">{{ t('common.close') }}</button>
          </div>
        </div>
      </div>
    </Teleport>
  </div>
</template>
