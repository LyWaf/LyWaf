<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { wafRuleApi } from '@/api'
import WafRuleEditor from '@/components/waf/WafRuleEditor.vue'
import { useToast } from '@/composables/useToast'
import type { WafCustomRule, WafRuleAction } from '@/types'

const { showSuccess, showError } = useToast()

// 来源标签
const sourceLabels: Record<string, string> = {
  User: '用户',
  Config: '配置',
  System: '系统',
}

const sourceColors: Record<string, string> = {
  User: 'bg-green-500/15 text-green-400',
  Config: 'bg-purple-500/15 text-purple-400',
  System: 'bg-cyan-500/15 text-cyan-400',
}

// 判断规则是否可编辑（仅用户规则可编辑/删除）
const isEditable = (rule: WafCustomRule) => !rule.source || rule.source === 'User'

// 翻译
const fieldLabels: Record<string, string> = {
  UriPath: 'URL 路径', FullUrl: '完整 URL', QueryString: '查询字符串',
  Method: 'HTTP 方法', ClientIp: '客户端 IP', XForwardedFor: 'X-Forwarded-For',
  UserAgent: 'User-Agent', Referer: 'Referer', ContentType: 'Content-Type',
  ContentLength: 'Content-Length', Cookie: 'Cookie', Header: '请求头',
  QueryParam: '查询参数', Body: '请求体', ServerPort: '服务端口',
}

const operatorLabels: Record<string, string> = {
  Equal: '等于', NotEqual: '不等于', Contains: '包含', NotContains: '不包含',
  StartsWith: '前缀', EndsWith: '后缀', Regex: '正则',
  Exists: '存在', NotExists: '不存在',
  LengthGreaterThan: '长度>', LengthLessThan: '长度<',
}

const actionLabels: Record<WafRuleAction, string> = {
  Observe: '观察',
  Block: '封禁',
  Reject: '拦截',
  Captcha: '验证码',
}

const actionColors: Record<WafRuleAction, string> = {
  Observe: 'bg-blue-500/15 text-blue-400',
  Block: 'bg-red-500/15 text-red-400',
  Reject: 'bg-orange-500/15 text-orange-400',
  Captcha: 'bg-amber-500/15 text-amber-400',
}

// 数据
const loading = ref(false)
const rules = ref<WafCustomRule[]>([])

// 编辑器状态
const showEditor = ref(false)
const editingRule = ref<WafCustomRule | null>(null)

// 删除确认
const showDeleteConfirm = ref(false)
const deletingRule = ref<WafCustomRule | null>(null)
const deleting = ref(false)

// 加载列表
const loadRules = async () => {
  loading.value = true
  try {
    const res = await wafRuleApi.list() as unknown as { success: boolean; rules: WafCustomRule[] }
    if (res?.success) {
      rules.value = res.rules
    }
  } catch {
    showError('加载规则列表失败')
  } finally {
    loading.value = false
  }
}

// 打开添加
const openAdd = () => {
  editingRule.value = null
  showEditor.value = true
}

// 打开编辑
const openEdit = (rule: WafCustomRule) => {
  editingRule.value = rule
  showEditor.value = true
}

// 关闭编辑器
const closeEditor = () => {
  showEditor.value = false
  editingRule.value = null
}

// 保存成功
const handleSaved = () => {
  loadRules()
  showSuccess('规则已保存')
}

// 切换启用
const toggleRule = async (rule: WafCustomRule) => {
  try {
    const res = await wafRuleApi.toggle(rule.id) as unknown as { success: boolean; enabled: boolean }
    if (res?.success) {
      rule.enabled = res.enabled
      showSuccess(res.enabled ? '已启用规则' : '已禁用规则')
    }
  } catch {
    showError('操作失败')
  }
}

// 删除
const confirmDelete = (rule: WafCustomRule) => {
  deletingRule.value = rule
  showDeleteConfirm.value = true
}

const executeDelete = async () => {
  if (!deletingRule.value) return
  deleting.value = true
  try {
    const res = await wafRuleApi.delete(deletingRule.value.id) as unknown as { success: boolean }
    if (res?.success) {
      showDeleteConfirm.value = false
      deletingRule.value = null
      loadRules()
      showSuccess('规则已删除')
    }
  } catch {
    showError('删除失败')
  } finally {
    deleting.value = false
  }
}

// 格式化单个条件
const formatSingleCondition = (c: { field: string; fieldName?: string; operator: string; value: string }): string => {
  const field = fieldLabels[c.field] || c.field
  const fieldName = c.fieldName ? `[${c.fieldName}]` : ''
  const op = operatorLabels[c.operator] || c.operator
  const noValue = ['Exists', 'NotExists'].includes(c.operator)
  const val = noValue ? '' : ` "${c.value}"`
  return `${field}${fieldName} ${op}${val}`
}

// 格式化条件摘要（支持 OR 分组）
const formatConditions = (rule: WafCustomRule): string => {
  if (!rule.conditionGroups || rule.conditionGroups.length === 0) return '无条件'
  const groups = rule.conditionGroups
    .filter(g => g.conditions && g.conditions.length > 0)
    .map(g => {
      const parts = g.conditions.map(formatSingleCondition)
      return parts.length > 1 ? `(${parts.join(' AND ')})` : parts[0]
    })
  if (groups.length === 0) return '无条件'
  return groups.join(' OR ')
}

// 格式化时间
const formatTime = (iso: string): string => {
  if (!iso) return ''
  const d = new Date(iso)
  return d.toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })
}

onMounted(() => {
  loadRules()
})
</script>

<template>
  <div class="space-y-4">
    <!-- 标题 -->
    <div class="card">
      <div class="flex items-center justify-between">
        <div class="flex items-center gap-3">
          <h2 class="text-lg font-semibold text-gray-100">WAF 规则</h2>
          <span class="text-xs text-gray-500">包含用户规则、配置规则和系统内置规则</span>
        </div>
        <button @click="openAdd" class="btn btn-sm btn-primary flex items-center gap-1.5">
          <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
          </svg>
          添加规则
        </button>
      </div>
    </div>

    <!-- 提示 -->
    <div class="px-4 py-3 rounded-lg bg-blue-500/10 border border-blue-500/20 text-sm text-blue-300">
      <span class="font-medium">提示：</span>
      规则按优先级分层执行：用户规则 > 配置规则 > 系统规则。支持多条件组合：组内 AND，组间 OR。配置和系统规则仅支持开关操作。
    </div>

    <!-- 加载中 -->
    <div v-if="loading" class="card flex items-center justify-center py-12">
      <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
    </div>

    <!-- 规则列表 -->
    <template v-else>
      <div v-if="rules.length === 0" class="card text-center py-12 text-gray-500">
        <p class="text-lg mb-2">暂无 WAF 规则</p>
        <p class="text-sm">点击「添加规则」创建第一条用户规则</p>
      </div>

      <div v-else class="space-y-3">
        <div
          v-for="rule in rules" :key="rule.id"
          :class="[
            'card !p-0 overflow-hidden transition-opacity',
            !rule.enabled && 'opacity-60'
          ]"
        >
          <div class="flex items-start gap-4 p-4">
            <!-- 启用开关 -->
            <button
              @click="toggleRule(rule)"
              :class="[
                'relative w-11 h-6 rounded-full transition-colors flex-shrink-0 mt-0.5',
                rule.enabled ? 'bg-primary-500' : 'bg-gray-600'
              ]"
            >
              <span
                :class="[
                  'absolute top-1 w-4 h-4 rounded-full bg-white transition-transform',
                  rule.enabled ? 'left-6' : 'left-1'
                ]"
              />
            </button>

            <!-- 规则信息 -->
            <div class="flex-1 min-w-0">
              <div class="flex items-center gap-2 mb-1">
                <span class="font-medium text-gray-200 truncate">{{ rule.name }}</span>
                <span v-if="rule.source && rule.source !== 'User'"
                  :class="['px-2 py-0.5 text-[11px] font-medium rounded-full', sourceColors[rule.source] || '']">
                  {{ sourceLabels[rule.source] || rule.source }}
                </span>
                <span :class="['px-2 py-0.5 text-[11px] font-medium rounded-full', actionColors[rule.action]]">
                  {{ actionLabels[rule.action] }}
                </span>
                <span class="text-xs text-gray-600">优先级 {{ rule.priority }}</span>
              </div>
              <p v-if="rule.description" class="text-xs text-gray-500 mb-1.5">{{ rule.description }}</p>
              <p class="text-xs text-gray-500 font-mono leading-5">{{ formatConditions(rule) }}</p>
              <div class="flex items-center gap-4 mt-2 text-[11px] text-gray-600">
                <span v-if="rule.action === 'Block'">封禁 {{ rule.actionSeconds }}s</span>
                <span v-if="rule.action === 'Reject'">返回 {{ rule.responseCode }}</span>
                <span v-if="rule.action === 'Captcha'">验证 {{ rule.actionSeconds }}s</span>
                <span>创建于 {{ formatTime(rule.createdAt) }}</span>
                <span v-if="rule.updatedAt">更新于 {{ formatTime(rule.updatedAt) }}</span>
              </div>
            </div>

            <!-- 操作 -->
            <div class="flex items-center gap-2 flex-shrink-0">
              <button @click="openEdit(rule)"
                class="text-xs text-primary-400 hover:text-primary-300 px-2 py-1 rounded hover:bg-primary-500/10 transition-colors">
                {{ isEditable(rule) ? '编辑' : '查看' }}
              </button>
              <button v-if="isEditable(rule)" @click="confirmDelete(rule)"
                class="text-xs text-red-400 hover:text-red-300 px-2 py-1 rounded hover:bg-red-500/10 transition-colors">
                删除
              </button>
            </div>
          </div>
        </div>
      </div>
    </template>

    <!-- 规则编辑器 -->
    <WafRuleEditor
      :show="showEditor"
      :edit-rule="editingRule"
      @close="closeEditor"
      @saved="handleSaved"
    />

    <!-- 删除确认 -->
    <Teleport to="body">
      <div v-if="showDeleteConfirm" class="fixed inset-0 z-50 flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="showDeleteConfirm = false"></div>
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl p-6 max-w-md w-full mx-4">
          <h3 class="text-lg font-semibold text-gray-100 mb-2">删除规则</h3>
          <p class="text-sm text-gray-400 mb-4">
            确定要删除规则「<span class="text-gray-200">{{ deletingRule?.name }}</span>」吗？
          </p>
          <p class="text-sm text-red-400 mb-5">此操作不可撤销。</p>
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
