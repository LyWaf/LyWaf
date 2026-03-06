<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { bwRuleApi } from '@/api'
import type { BwHitEvent, BwRule } from '@/api'
import BwRuleEditor from '@/components/bw/BwRuleEditor.vue'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()

// ===== 检测事件 =====
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

    const res = await bwRuleApi.events(params) as any
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
    const res = await bwRuleApi.clearEvents() as any
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

// ===== 自定义规则面板 =====
const showRulesPanel = ref(false)
const rulesLoading = ref(false)
const rules = ref<BwRule[]>([])
const showEditor = ref(false)
const editingRule = ref<BwRule | null>(null)
const showDeleteConfirm = ref(false)
const deleteTarget = ref<BwRule | null>(null)
const deleting = ref(false)

const loadRules = async () => {
  rulesLoading.value = true
  try {
    const res = await bwRuleApi.list() as any
    if (res?.success) rules.value = res.rules || []
  } catch {
    showError('加载规则失败')
  } finally {
    rulesLoading.value = false
  }
}

const openRulesPanel = () => {
  showRulesPanel.value = true
  loadRules()
}

const openCreate = () => {
  editingRule.value = null
  showEditor.value = true
}

const openEdit = (rule: BwRule) => {
  editingRule.value = rule
  showEditor.value = true
}

const onEditorSaved = () => {
  showEditor.value = false
  editingRule.value = null
  loadRules()
}

const toggleRule = async (rule: BwRule) => {
  try {
    const res = await bwRuleApi.toggle(rule.id) as any
    if (res?.success) {
      rule.enabled = res.enabled
      showSuccess(res.message || (res.enabled ? '已启用' : '已禁用'))
    }
  } catch {
    showError('操作失败')
  }
}

const confirmDelete = (rule: BwRule) => {
  deleteTarget.value = rule
  showDeleteConfirm.value = true
}

const executeDelete = async () => {
  if (!deleteTarget.value) return
  deleting.value = true
  try {
    const res = await bwRuleApi.delete(deleteTarget.value.id) as any
    if (res?.success) {
      showDeleteConfirm.value = false
      deleteTarget.value = null
      loadRules()
      showSuccess('规则已删除')
    } else {
      showError(res?.message || '删除失败')
    }
  } catch {
    showError('删除失败')
  } finally {
    deleting.value = false
  }
}

const resetHits = async () => {
  try {
    const res = await bwRuleApi.resetHits() as any
    if (res?.success) {
      loadRules()
      showSuccess('命中计数已重置')
    }
  } catch {
    showError('重置失败')
  }
}

// ===== 工具函数 =====
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
  if (n >= 1000) return (n / 1000).toFixed(1) + 'k'
  return n.toString()
}

const getFieldShort = (field: string): string => {
  const map: Record<string, string> = {
    ClientIp: 'IP', UriPath: '路径', FullUrl: 'URL', Method: '方法',
    UserAgent: 'UA', Referer: 'Referer', Header: '请求头', Cookie: 'Cookie',
    QueryParam: '参数', QueryString: '查询串', XForwardedFor: 'XFF',
    ContentType: 'CT', ServerPort: '端口',
  }
  return map[field] || field
}

const getOperatorShort = (op: string): string => {
  const map: Record<string, string> = {
    Equal: '=', NotEqual: '!=', Contains: '含', NotContains: '不含',
    StartsWith: '前缀', EndsWith: '后缀', Regex: '正则',
    Exists: '存在', NotExists: '不存在', LengthGreaterThan: '长度>', LengthLessThan: '长度<',
  }
  return map[op] || op
}
</script>

<template>
  <div class="space-y-4">
    <!-- 标题栏 -->
    <div class="card">
      <div class="flex items-center justify-between">
        <div class="flex items-center gap-3">
          <h2 class="text-lg font-semibold text-gray-100">黑白名单</h2>
          <!-- 标签切换 -->
          <div class="flex items-center bg-dark-bg rounded-lg p-0.5 border border-dark-border">
            <button class="px-3 py-1 text-xs font-medium rounded-md bg-primary-500/20 text-primary-400 border border-primary-500/30">
              检测事件
            </button>
          </div>
        </div>
        <button @click="openRulesPanel" class="btn btn-sm btn-secondary flex items-center gap-1.5">
          <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.066 2.573c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.573 1.066c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.066-2.573c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
          </svg>
          自定义规则
        </button>
      </div>
    </div>

    <!-- 过滤栏 -->
    <div class="card !py-3">
      <div class="flex items-center gap-3">
        <input
          v-model="filterIp"
          type="text"
          class="input w-40 text-sm"
          placeholder="源 IP"
        />
        <input
          v-model="filterDomain"
          type="text"
          class="input w-48 text-sm"
          placeholder="域名"
        />
        <input
          v-model="filterStartTime"
          type="datetime-local"
          class="input w-44 text-sm"
        />
        <input
          v-model="filterEndTime"
          type="datetime-local"
          class="input w-44 text-sm"
        />
        <div class="flex items-center gap-2 ml-auto">
          <button
            v-if="events.length > 0"
            @click="clearEvents"
            class="btn btn-sm btn-secondary text-xs"
          >
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
                <!-- 源 IP -->
                <td class="px-4 py-3">
                  <div>
                    <span class="text-gray-200 font-mono text-sm">{{ event.sourceIp }}</span>
                  </div>
                  <div v-if="event.region || event.city" class="text-xs text-gray-500 mt-0.5">
                    {{ event.region }}<template v-if="event.region && event.city"> - </template>{{ event.city }}
                  </div>
                </td>

                <!-- 应用 -->
                <td class="px-4 py-3">
                  <span class="text-gray-300 text-sm">{{ event.application }}</span>
                </td>

                <!-- 命中次数 -->
                <td class="px-4 py-3 text-center">
                  <span class="inline-flex items-center justify-center min-w-[28px] px-2 py-0.5 text-xs font-medium rounded bg-red-500/20 text-red-400 border border-red-500/30">
                    {{ formatNum(event.hitCount) }}
                  </span>
                </td>

                <!-- 持续时间 -->
                <td class="px-4 py-3 text-gray-400 text-sm">
                  {{ formatDuration(event.duration) }}
                </td>

                <!-- 开始时间 -->
                <td class="px-4 py-3 text-gray-400 text-sm font-mono">
                  {{ formatDateTime(event.firstHitTime) }}
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </template>

    <!-- ========== 自定义规则面板（侧滑） ========== -->
    <Teleport to="body">
      <div v-if="showRulesPanel" class="fixed inset-0 z-50 flex">
        <div class="absolute inset-0 bg-black/60" @click="showRulesPanel = false"></div>
        <div class="relative ml-auto w-[720px] max-w-[95vw] h-full bg-dark-card border-l border-dark-border shadow-2xl flex flex-col">
          <!-- 面板头部 -->
          <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border shrink-0">
            <h3 class="text-lg font-semibold text-gray-100">自定义规则</h3>
            <div class="flex items-center gap-2">
              <button
                v-if="rules.length > 0"
                @click="resetHits"
                class="btn btn-sm btn-secondary text-xs"
              >
                重置命中
              </button>
              <button @click="openCreate" class="btn btn-sm btn-primary flex items-center gap-1.5">
                <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
                </svg>
                添加规则
              </button>
              <button @click="showRulesPanel = false" class="text-gray-400 hover:text-gray-200 text-xl leading-none ml-2">&times;</button>
            </div>
          </div>

          <!-- 规则列表 -->
          <div class="flex-1 overflow-y-auto px-6 py-4">
            <div v-if="rulesLoading" class="flex items-center justify-center py-12">
              <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
            </div>

            <div v-else-if="rules.length === 0" class="text-center py-12 text-gray-500">
              <p class="text-lg mb-2">暂无规则</p>
              <p class="text-sm">点击「添加规则」创建自定义匹配规则</p>
            </div>

            <div v-else class="space-y-3">
              <div
                v-for="rule in rules"
                :key="rule.id"
                class="p-4 rounded-lg border border-dark-border bg-dark-bg"
              >
                <!-- 规则头 -->
                <div class="flex items-center justify-between mb-2">
                  <div class="flex items-center gap-2">
                    <button @click="toggleRule(rule)"
                      :class="['relative w-8 h-4 rounded-full transition-colors', rule.enabled ? 'bg-primary-500' : 'bg-gray-600']">
                      <span :class="['absolute top-[2px] w-3 h-3 rounded-full bg-white transition-transform', rule.enabled ? 'left-[18px]' : 'left-[1px]']" />
                    </button>
                    <span :class="['px-2 py-0.5 text-[11px] font-medium rounded-full',
                      rule.type === 'White'
                        ? 'bg-green-500/15 text-green-400'
                        : 'bg-red-500/15 text-red-400']">
                      {{ rule.type === 'White' ? '白名单' : '黑名单' }}
                    </span>
                    <span class="text-gray-200 font-medium text-sm">{{ rule.name }}</span>
                  </div>
                  <div class="flex items-center gap-1">
                    <span class="text-xs text-gray-500 font-mono mr-2">
                      {{ formatNum(rule.todayHitCount) }}/{{ formatNum(rule.hitCount) }}
                    </span>
                    <button @click="openEdit(rule)"
                      class="text-xs text-primary-400 hover:text-primary-300 px-2 py-1 rounded hover:bg-primary-500/10 transition-colors">
                      编辑
                    </button>
                    <button @click="confirmDelete(rule)"
                      class="text-xs text-red-400 hover:text-red-300 px-2 py-1 rounded hover:bg-red-500/10 transition-colors">
                      删除
                    </button>
                  </div>
                </div>

                <!-- 条件标签 -->
                <div class="flex flex-wrap gap-1">
                  <span
                    v-for="(cond, idx) in rule.conditions"
                    :key="idx"
                    class="inline-flex items-center gap-1 px-1.5 py-0.5 text-[11px] rounded bg-dark-card text-gray-400 border border-dark-border/50"
                  >
                    <span class="text-blue-400">{{ getFieldShort(cond.field) }}</span>
                    <span v-if="cond.fieldName" class="text-gray-500">({{ cond.fieldName }})</span>
                    <span class="text-gray-500">{{ getOperatorShort(cond.operator) }}</span>
                    <span v-if="!['Exists', 'NotExists'].includes(cond.operator)" class="text-yellow-400/80 font-mono max-w-[120px] truncate">{{ cond.value }}</span>
                  </span>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </Teleport>

    <!-- 编辑器 -->
    <BwRuleEditor
      v-if="showEditor"
      :rule="editingRule"
      @close="showEditor = false; editingRule = null"
      @saved="onEditorSaved"
    />

    <!-- 删除确认 -->
    <Teleport to="body">
      <div v-if="showDeleteConfirm" class="fixed inset-0 z-[60] flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="showDeleteConfirm = false"></div>
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl p-6 max-w-md w-full mx-4">
          <h3 class="text-lg font-semibold text-gray-100 mb-2">删除规则</h3>
          <p class="text-sm text-gray-400 mb-5">
            确定要删除规则「<span class="text-gray-200">{{ deleteTarget?.name }}</span>」吗？此操作不可恢复。
          </p>
          <div class="flex justify-end gap-3">
            <button @click="showDeleteConfirm = false" class="btn btn-sm btn-secondary">取消</button>
            <button @click="executeDelete" :disabled="deleting"
              class="btn btn-sm bg-red-600 hover:bg-red-500 text-white flex items-center gap-1.5">
              <span v-if="deleting" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
              <span>{{ deleting ? '删除中...' : '确认删除' }}</span>
            </button>
          </div>
        </div>
      </div>
    </Teleport>
  </div>
</template>
