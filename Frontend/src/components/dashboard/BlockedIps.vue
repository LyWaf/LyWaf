<script setup lang="ts">
import { ref, watch, computed } from 'vue'
import Section from '@/components/common/Section.vue'
import { ipApi } from '@/api'
import type { IpLogEntry } from '@/api'
import { useToast } from '@/composables/useToast'

interface BlockedIpInfo {
  ip: string
  type: string
  reason: string
  remainingSeconds?: number
}

interface Props {
  initialData?: BlockedIpInfo[]
}

const props = defineProps<Props>()
const { showSuccess, showError } = useToast()

const blockedIps = ref<BlockedIpInfo[]>([])

watch(() => props.initialData, (newData) => {
  if (newData) {
    blockedIps.value = newData
  }
}, { immediate: true })

// 类型标签
const typeLabel = (type: string) => {
  switch (type) {
    case 'blocked': return '封禁'
    case 'captcha': return '验证码'
    case 'throttled': return '限速'
    case 'log': return '日志'
    default: return type
  }
}

// 类型样式
const typeClass = (type: string) => {
  switch (type) {
    case 'blocked': return 'bg-red-500/20 text-red-400'
    case 'captcha': return 'bg-yellow-500/20 text-yellow-400'
    case 'throttled': return 'bg-blue-500/20 text-blue-400'
    case 'log': return 'bg-green-500/20 text-green-400'
    default: return 'bg-gray-500/20 text-gray-400'
  }
}

// 格式化剩余时间
const formatRemainingTime = (seconds?: number) => {
  if (seconds === undefined || seconds === null) return '永久'
  if (seconds <= 0) return '已过期'

  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  const secs = Math.floor(seconds % 60)

  if (days > 0) return `${days}天${hours}时`
  if (hours > 0) return `${hours}时${minutes}分`
  if (minutes > 0) return `${minutes}分${secs}秒`
  return `${secs}秒`
}

// HTTP 方法颜色
const methodColor = (method: string) => {
  switch (method?.toUpperCase()) {
    case 'GET': return 'text-green-400'
    case 'POST': return 'text-blue-400'
    case 'PUT': return 'text-yellow-400'
    case 'DELETE': return 'text-red-400'
    case 'PATCH': return 'text-purple-400'
    case 'OPTIONS': return 'text-gray-400'
    case 'HEAD': return 'text-cyan-400'
    default: return 'text-gray-300'
  }
}

// HTTP 状态码颜色
const statusCodeClass = (code?: number) => {
  if (!code) return 'text-gray-500'
  if (code >= 500) return 'text-red-400'
  if (code >= 400) return 'text-orange-400'
  if (code >= 300) return 'text-yellow-400'
  if (code >= 200) return 'text-green-400'
  return 'text-gray-400'
}

const emit = defineEmits<{
  (e: 'refresh'): void
}>()

// ======================== 手动添加弹窗 ========================
type ActionType = 'blocked' | 'captcha' | 'throttled' | 'log'
const showDialog = ref(false)
const blockForm = ref({
  ip: '',
  actionType: 'blocked' as ActionType,
  reason: '手动封禁',
  duration: 3600,
  speedLimit: 100,
})
const blockLoading = ref(false)

const actionTypeOptions: { value: ActionType; label: string }[] = [
  { value: 'blocked', label: '封禁' },
  { value: 'captcha', label: '验证码' },
  { value: 'throttled', label: '限速' },
  { value: 'log', label: '日志记录' },
]

const dialogTitle = computed(() => {
  switch (blockForm.value.actionType) {
    case 'captcha': return '添加验证码验证'
    case 'throttled': return '添加带宽限速'
    case 'log': return '添加请求日志记录'
    default: return '手动封禁 IP'
  }
})

const submitBtnText = computed(() => {
  if (blockLoading.value) return '提交中...'
  switch (blockForm.value.actionType) {
    case 'captcha': return '确定'
    case 'throttled': return '确定'
    case 'log': return '开始记录'
    default: return '确定封禁'
  }
})

const openBlockDialog = () => {
  blockForm.value = { ip: '', actionType: 'blocked', reason: '手动封禁', duration: 3600, speedLimit: 100 }
  showDialog.value = true
}

const submitBlock = async () => {
  const { ip, actionType, reason, duration, speedLimit } = blockForm.value
  if (!ip.trim()) {
    showError('请输入 IP 地址')
    return
  }

  blockLoading.value = true
  try {
    if (actionType === 'log') {
      const res = await ipApi.addIpLog(ip.trim(), duration > 0 ? duration : undefined)
      if (res.success) {
        blockedIps.value.push({
          ip: ip.trim(),
          type: 'log',
          reason: '请求日志记录中',
          remainingSeconds: duration > 0 ? duration : undefined,
        })
        showSuccess(`已开始记录: ${ip.trim()}`)
        showDialog.value = false
        emit('refresh')
      } else {
        showError((res as any).message || '添加失败')
      }
    } else {
      const res = await ipApi.blockIp(
        ip.trim(),
        reason || undefined,
        duration || undefined,
        actionType,
        actionType === 'throttled' ? speedLimit : undefined,
      )
      if (res.success) {
        let displayReason = reason || '手动封禁'
        if (actionType === 'captcha') displayReason = `验证码待验证: ${reason || '手动验证码'}`
        if (actionType === 'throttled') displayReason = `带宽限速: ${speedLimit}KB/s`
        blockedIps.value.push({
          ip: ip.trim(),
          type: actionType,
          reason: displayReason,
          remainingSeconds: duration || undefined,
        })
        showSuccess((res as any).message || '操作成功')
        showDialog.value = false
        emit('refresh')
      } else {
        showError((res as any).message || '操作失败')
      }
    }
  } catch {
    showError('操作失败')
  } finally {
    blockLoading.value = false
  }
}

// ======================== 日志查看弹窗（列表 + 分页 + 重放） ========================
const showLogViewDialog = ref(false)
const logViewIp = ref('')
const logEntries = ref<IpLogEntry[]>([])
const logViewLoading = ref(false)
const logTotal = ref(0)
const logOffset = ref(0)
const logLimit = ref(20)
const logFileSize = ref(0)
const expandedEntry = ref<number | null>(null) // 展开的条目 index

// 重放状态
const replayLoading = ref<number | null>(null) // 正在重放的条目 index
const replayResult = ref<{ index: number; status: number; statusText: string; body: string } | null>(null)

const totalPages = computed(() => Math.ceil(logTotal.value / logLimit.value) || 1)
const currentPage = computed(() => Math.floor(logOffset.value / logLimit.value) + 1)

const viewLog = async (ip: string, resetOffset = true) => {
  logViewIp.value = ip
  logEntries.value = []
  logViewLoading.value = true
  showLogViewDialog.value = true
  if (resetOffset) {
    logOffset.value = 0
    expandedEntry.value = null
    replayResult.value = null
  }

  try {
    const res = await ipApi.readIpLog(ip, logOffset.value, logLimit.value)
    if (res.success) {
      logEntries.value = res.entries || []
      logTotal.value = res.total || 0
      logFileSize.value = res.fileSize || 0
    } else {
      logEntries.value = []
      logTotal.value = 0
    }
  } catch {
    logEntries.value = []
    logTotal.value = 0
  } finally {
    logViewLoading.value = false
  }
}

const gotoPage = (page: number) => {
  if (page < 1 || page > totalPages.value) return
  logOffset.value = (page - 1) * logLimit.value
  expandedEntry.value = null
  replayResult.value = null
  viewLog(logViewIp.value, false)
}

const toggleExpand = (index: number) => {
  expandedEntry.value = expandedEntry.value === index ? null : index
  replayResult.value = null
}

// 格式化文件大小
const formatFileSize = (bytes: number) => {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

// 重放请求
const replayRequest = async (entry: IpLogEntry) => {
  replayLoading.value = entry.index
  replayResult.value = null

  try {
    // 从 entry 解析出请求信息
    const method = entry.method || 'GET'
    const host = entry.host || ''
    const url = entry.url || '/'

    // 构建完整 URL
    const fullUrl = host ? `${host.includes('://') ? '' : 'http://'}${host}${url}` : url

    // 解析 headers
    const headers: Record<string, string> = {}
    if (entry.headers) {
      for (const line of entry.headers.split('\n')) {
        const colonIdx = line.indexOf(':')
        if (colonIdx > 0) {
          const key = line.substring(0, colonIdx).trim().toLowerCase()
          const value = line.substring(colonIdx + 1).trim()
          // 跳过不能手动设置的头
          if (!['host', 'content-length', 'transfer-encoding', 'connection'].includes(key)) {
            headers[key] = value
          }
        }
      }
    }

    const fetchOptions: RequestInit = {
      method: method,
      headers: headers,
    }

    // POST/PUT/PATCH 带 body
    if (['POST', 'PUT', 'PATCH'].includes(method.toUpperCase()) && entry.requestBody) {
      fetchOptions.body = entry.requestBody
    }

    const response = await fetch(fullUrl, fetchOptions)
    const responseBody = await response.text()

    replayResult.value = {
      index: entry.index,
      status: response.status,
      statusText: response.statusText,
      body: responseBody.length > 10000 ? responseBody.substring(0, 10000) + '\n[... 截断 ...]' : responseBody,
    }
  } catch (err: any) {
    replayResult.value = {
      index: entry.index,
      status: 0,
      statusText: 'Error',
      body: `请求失败: ${err.message || '网络错误'}`,
    }
  } finally {
    replayLoading.value = null
  }
}

// ======================== 其他操作 ========================

const deleteLogFile = async (ip: string) => {
  if (!confirm(`确定要删除 ${ip} 的日志文件吗？`)) return

  try {
    const res = await ipApi.deleteIpLogFile(ip)
    if (res.success) {
      showSuccess(`已删除 ${ip} 的日志文件`)
      emit('refresh')
    }
  } catch {
    showError('删除失败')
  }
}

const unblockIp = async (ip: string, type: string) => {
  if (type === 'log') {
    if (!confirm(`确定要停止记录 ${ip} 的请求日志吗？`)) return

    try {
      const res = await ipApi.removeIpLog(ip)
      if (res.success) {
        blockedIps.value = blockedIps.value.filter(i => !(i.ip === ip && i.type === 'log'))
        showSuccess(`已停止记录: ${ip}`)
        emit('refresh')
      }
    } catch {
      showError('停止失败')
    }
    return
  }

  if (!confirm(`确定要解封 ${ip} 吗？`)) return

  try {
    const res = await ipApi.unblockIp(ip)
    if (res.success) {
      blockedIps.value = blockedIps.value.filter(i => i.ip !== ip)
      showSuccess(`已解封: ${ip}`)
      emit('refresh')
    }
  } catch {
    showError('解封失败')
  }
}

const clearAll = async () => {
  if (!confirm('确定要清空所有封禁/监控的 IP 吗？')) return

  try {
    const res = await ipApi.clearBlockedIps()
    if (res.success) {
      blockedIps.value = []
      showSuccess('已清空列表')
      emit('refresh')
    }
  } catch {
    showError('清空失败')
  }
}
</script>

<template>
  <Section id="blocked-ips" :title="`当前封禁/受限的 IP (${blockedIps.length})`">
    <template #actions>
      <div class="flex gap-2">
        <button @click="openBlockDialog" class="btn btn-sm btn-primary">+ 手动添加</button>
        <button @click="clearAll" class="btn btn-sm btn-danger">清空全部</button>
      </div>
    </template>

    <div class="space-y-2 max-h-[400px] overflow-y-auto">
      <div
        v-for="item in blockedIps"
        :key="`${item.type}-${item.ip}`"
        class="flex items-center justify-between p-4 bg-dark-card-hover rounded-lg"
      >
        <div class="flex items-center gap-6">
          <div>
            <span class="text-gray-400 text-sm">IP 地址</span>
            <div class="text-red-400 font-mono">{{ item.ip }}</div>
          </div>
          <div>
            <span class="text-gray-400 text-sm">类型</span>
            <div>
              <span :class="typeClass(item.type)" class="inline-block px-2 py-0.5 rounded text-xs font-medium">
                {{ typeLabel(item.type) }}
              </span>
            </div>
          </div>
          <div v-if="item.reason">
            <span class="text-gray-400 text-sm">原因</span>
            <div class="text-gray-300 text-sm">{{ item.reason }}</div>
          </div>
          <div>
            <span class="text-gray-400 text-sm">剩余时间</span>
            <div class="text-yellow-400">{{ formatRemainingTime(item.remainingSeconds) }}</div>
          </div>
        </div>
        <div class="flex items-center gap-2">
          <template v-if="item.type === 'log'">
            <button @click="viewLog(item.ip)" class="btn btn-sm btn-secondary">查看日志</button>
            <button @click="deleteLogFile(item.ip)" class="btn btn-sm btn-secondary">清空日志</button>
          </template>
          <button
            @click="unblockIp(item.ip, item.type)"
            class="btn btn-sm btn-secondary"
          >
            {{ item.type === 'log' ? '停止' : '解封' }}
          </button>
        </div>
      </div>

      <div v-if="blockedIps.length === 0" class="text-gray-500 text-center py-8">
        暂无封禁/监控的 IP
      </div>
    </div>
  </Section>

  <!-- 手动添加弹窗 -->
  <Teleport to="body">
    <div v-if="showDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="showDialog = false"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-[420px] max-w-[90vw]">
        <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border">
          <h3 class="text-lg font-semibold text-gray-100">{{ dialogTitle }}</h3>
          <button @click="showDialog = false" class="text-gray-400 hover:text-gray-200 text-xl leading-none">&times;</button>
        </div>
        <div class="px-6 py-5 space-y-4">
          <!-- 操作类型下拉 -->
          <div>
            <label class="block text-sm text-gray-400 mb-1">操作类型</label>
            <select v-model="blockForm.actionType" class="input">
              <option v-for="opt in actionTypeOptions" :key="opt.value" :value="opt.value">
                {{ opt.label }}
              </option>
            </select>
          </div>
          <!-- IP 地址 -->
          <div>
            <label class="block text-sm text-gray-400 mb-1">IP 地址 <span class="text-red-400">*</span></label>
            <input
              v-model="blockForm.ip"
              type="text"
              class="input"
              placeholder="例如 192.168.1.100"
              @keydown.enter="submitBlock"
            />
          </div>
          <!-- 封禁/验证码: 原因 -->
          <div v-if="blockForm.actionType === 'blocked' || blockForm.actionType === 'captcha'">
            <label class="block text-sm text-gray-400 mb-1">
              {{ blockForm.actionType === 'captcha' ? '规则名称' : '封禁原因' }}
            </label>
            <input
              v-model="blockForm.reason"
              type="text"
              class="input"
              :placeholder="blockForm.actionType === 'captcha' ? '手动验证码' : '手动封禁'"
            />
          </div>
          <!-- 限速: 速度 -->
          <div v-if="blockForm.actionType === 'throttled'">
            <label class="block text-sm text-gray-400 mb-1">限速速度（KB/s）</label>
            <input
              v-model.number="blockForm.speedLimit"
              type="number"
              class="input"
              min="1"
              placeholder="100"
            />
          </div>
          <!-- 时长（所有类型都显示） -->
          <div>
            <label class="block text-sm text-gray-400 mb-1">时长（秒，0 表示永久）</label>
            <input
              v-model.number="blockForm.duration"
              type="number"
              class="input"
              min="0"
              placeholder="3600"
            />
            <span class="text-xs text-gray-500 mt-1 block">
              {{ blockForm.duration > 0 ? formatRemainingTime(blockForm.duration) : '永久' }}
            </span>
          </div>
          <!-- 日志模式提示 -->
          <p v-if="blockForm.actionType === 'log'" class="text-xs text-gray-500">
            该 IP 的所有请求内容（请求头、请求体）将被完整记录到文件中。每个请求最多记录 200KB 数据。
          </p>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="showDialog = false" class="btn btn-secondary">取消</button>
          <button @click="submitBlock" :disabled="blockLoading" class="btn btn-primary">
            {{ submitBtnText }}
          </button>
        </div>
      </div>
    </div>
  </Teleport>

  <!-- 日志查看弹窗（列表 + 分页 + 重放） -->
  <Teleport to="body">
    <div v-if="showLogViewDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="showLogViewDialog = false"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-[900px] max-w-[95vw] max-h-[85vh] flex flex-col">
        <!-- 头部 -->
        <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border shrink-0">
          <div class="flex items-center gap-4">
            <h3 class="text-lg font-semibold text-gray-100">
              请求日志 - <span class="text-blue-400 font-mono">{{ logViewIp }}</span>
            </h3>
            <span v-if="logTotal > 0" class="text-xs text-gray-500">
              共 {{ logTotal }} 条 · {{ formatFileSize(logFileSize) }}
            </span>
          </div>
          <button @click="showLogViewDialog = false" class="text-gray-400 hover:text-gray-200 text-xl leading-none">&times;</button>
        </div>

        <!-- 内容区 -->
        <div class="flex-1 overflow-auto">
          <!-- 加载中 -->
          <div v-if="logViewLoading" class="flex items-center justify-center py-12">
            <div class="animate-spin w-6 h-6 border-2 border-gray-500 border-t-primary-400 rounded-full"></div>
            <span class="ml-3 text-gray-400 text-sm">加载中...</span>
          </div>

          <!-- 无数据 -->
          <div v-else-if="logEntries.length === 0" class="text-gray-500 text-center py-12">
            暂无日志条目
          </div>

          <!-- 日志条目列表 -->
          <div v-else class="divide-y divide-dark-border">
            <div v-for="entry in logEntries" :key="entry.index" class="hover:bg-dark-card-hover transition-colors">
              <!-- 条目摘要行 -->
              <div
                class="flex items-center gap-4 px-5 py-3 cursor-pointer select-none"
                @click="toggleExpand(entry.index)"
              >
                <!-- 展开图标 -->
                <span class="text-gray-500 text-xs shrink-0 w-4 text-center transition-transform" :class="{ 'rotate-90': expandedEntry === entry.index }">▶</span>
                <!-- 序号 -->
                <span class="text-gray-500 text-xs font-mono w-8 shrink-0 text-right">#{{ entry.index + 1 }}</span>
                <!-- 时间 -->
                <span class="text-gray-400 text-xs font-mono shrink-0 w-[170px]">{{ entry.time }}</span>
                <!-- 方法 -->
                <span :class="methodColor(entry.method)" class="text-xs font-bold font-mono shrink-0 w-16">{{ entry.method }}</span>
                <!-- URL -->
                <span class="text-gray-200 text-xs font-mono truncate flex-1" :title="entry.url">{{ entry.url }}</span>
                <!-- 状态码 -->
                <span
                  v-if="entry.statusCode"
                  :class="statusCodeClass(entry.statusCode)"
                  class="text-xs font-mono font-bold shrink-0 w-8 text-center"
                >{{ entry.statusCode }}</span>
                <span v-else class="text-gray-600 text-xs font-mono shrink-0 w-8 text-center">—</span>
                <!-- 耗时 -->
                <span v-if="entry.duration" class="text-gray-500 text-xs font-mono shrink-0 w-16 text-right">{{ entry.duration }}</span>
                <span v-else class="shrink-0 w-16"></span>
                <!-- 重放按钮 -->
                <button
                  @click.stop="replayRequest(entry)"
                  :disabled="replayLoading === entry.index"
                  class="btn btn-sm text-xs px-2 py-1 shrink-0"
                  :class="replayLoading === entry.index ? 'bg-gray-600 text-gray-400 cursor-wait' : 'bg-purple-500/20 text-purple-400 hover:bg-purple-500/30'"
                >
                  {{ replayLoading === entry.index ? '重放中...' : '重放' }}
                </button>
              </div>

              <!-- 展开的详细内容 -->
              <div v-if="expandedEntry === entry.index" class="px-5 pb-4 space-y-3">
                <!-- 请求行 + 响应概览 -->
                <div class="bg-dark-bg rounded-lg p-3">
                  <div class="text-xs text-gray-200 font-mono">{{ entry.requestLine }}</div>
                  <div v-if="entry.host" class="text-xs text-gray-400 font-mono mt-1">Host: {{ entry.host }}</div>
                  <div v-if="entry.statusCode || entry.duration" class="flex items-center gap-3 mt-2 pt-2 border-t border-dark-border">
                    <span v-if="entry.statusCode" class="text-xs font-mono">
                      状态码: <span :class="statusCodeClass(entry.statusCode)" class="font-bold">{{ entry.statusCode }}</span>
                    </span>
                    <span v-if="entry.duration" class="text-xs font-mono text-gray-400">
                      耗时: <span class="text-gray-300">{{ entry.duration }}</span>
                    </span>
                  </div>
                </div>

                <!-- 请求头 -->
                <div v-if="entry.headers" class="bg-dark-bg rounded-lg p-3">
                  <div class="text-xs text-gray-500 mb-1">请求头</div>
                  <pre class="text-xs text-gray-300 font-mono whitespace-pre-wrap break-all max-h-[200px] overflow-auto">{{ entry.headers }}</pre>
                </div>

                <!-- 请求体 -->
                <div v-if="entry.requestBody" class="bg-dark-bg rounded-lg p-3">
                  <div class="text-xs text-gray-500 mb-1">请求体</div>
                  <pre class="text-xs text-gray-300 font-mono whitespace-pre-wrap break-all max-h-[300px] overflow-auto">{{ entry.requestBody }}</pre>
                </div>

                <!-- 重放结果 -->
                <div v-if="replayResult && replayResult.index === entry.index" class="bg-dark-bg rounded-lg p-3 border border-purple-500/30">
                  <div class="flex items-center gap-2 mb-2">
                    <span class="text-xs text-purple-400 font-semibold">重放响应</span>
                    <span
                      class="text-xs font-mono px-1.5 py-0.5 rounded"
                      :class="replayResult.status >= 200 && replayResult.status < 300 ? 'bg-green-500/20 text-green-400' : replayResult.status >= 400 ? 'bg-red-500/20 text-red-400' : 'bg-yellow-500/20 text-yellow-400'"
                    >
                      {{ replayResult.status }} {{ replayResult.statusText }}
                    </span>
                  </div>
                  <pre class="text-xs text-gray-300 font-mono whitespace-pre-wrap break-all max-h-[300px] overflow-auto">{{ replayResult.body }}</pre>
                </div>
              </div>
            </div>
          </div>
        </div>

        <!-- 底部: 分页 + 操作 -->
        <div class="flex items-center justify-between px-6 py-3 border-t border-dark-border shrink-0">
          <div class="flex items-center gap-2">
            <!-- 分页控件 -->
            <template v-if="totalPages > 1">
              <button @click="gotoPage(1)" :disabled="currentPage === 1" class="btn btn-sm btn-secondary text-xs px-2">首页</button>
              <button @click="gotoPage(currentPage - 1)" :disabled="currentPage === 1" class="btn btn-sm btn-secondary text-xs px-2">上一页</button>
              <span class="text-xs text-gray-400 px-2">
                第 {{ currentPage }} / {{ totalPages }} 页
              </span>
              <button @click="gotoPage(currentPage + 1)" :disabled="currentPage === totalPages" class="btn btn-sm btn-secondary text-xs px-2">下一页</button>
              <button @click="gotoPage(totalPages)" :disabled="currentPage === totalPages" class="btn btn-sm btn-secondary text-xs px-2">末页</button>
            </template>
            <span v-else-if="logTotal > 0" class="text-xs text-gray-500">共 {{ logTotal }} 条</span>
          </div>
          <div class="flex items-center gap-2">
            <button @click="viewLog(logViewIp)" class="btn btn-secondary btn-sm">刷新</button>
            <button @click="showLogViewDialog = false" class="btn btn-secondary btn-sm">关闭</button>
          </div>
        </div>
      </div>
    </div>
  </Teleport>
</template>
