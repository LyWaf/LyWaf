<script setup lang="ts">
import { locale } from '@/i18n'
import { t } from '@/i18n'
import { computed, ref, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { wafRuleApi } from '@/api'
import WafRuleEditor from '@/components/waf/WafRuleEditor.vue'
import { useToast } from '@/composables/useToast'
import type { WafCustomRule, WafRuleAction } from '@/types'

const { showSuccess, showError } = useToast()
const route = useRoute()

// 来源标签
const sourceLabels = computed<Record<string, string>>(() => ({
  User: t('settings.user'),
  Config: t('ui.configuration'),
  System: t('plugins.system'),
}))

const sourceColors: Record<string, string> = {
  User: 'bg-green-500/15 text-green-400',
  Config: 'bg-purple-500/15 text-purple-400',
  System: 'bg-cyan-500/15 text-cyan-400',
}

// 判断规则是否可编辑（仅用户规则可编辑/删除）
const isEditable = (rule: WafCustomRule) => !rule.source || rule.source === 'User'

// 翻译
const fieldLabels = computed<Record<string, string>>(() => ({
  UriPath: t('page.f3ff6f41a826'), FullUrl: t('page.369a6f068cd9'), QueryString: t('page.9b0340697a07'),
  Method: t('page.af1029a24f14'), ClientIp: t('page.2d1cb6792f12'), XForwardedFor: 'X-Forwarded-For',
  UserAgent: 'User-Agent', Referer: 'Referer', ContentType: 'Content-Type',
  ContentLength: 'Content-Length', Cookie: 'Cookie', Header: t('fields.Headers'),
  QueryParam: t('page.1904a09fab00'), Body: t('dashboard.request.body'), ServerPort: t('page.ff673195f05f'),
}))

const operatorLabels = computed<Record<string, string>>(() => ({
  Equal: t('page.4bc69a0562ff'), NotEqual: t('page.9678e3a7cb3c'), Contains: t('page.107319b8b39d'), NotContains: t('page.5f60ffcf600a'),
  StartsWith: t('page.461920e1ba6e'), EndsWith: t('page.006d3ffb11d2'), Regex: t('page.75b304aa9420'),
  Exists: t('page.4a558371f938'), NotExists: t('page.4e51b67dd32c'),
  LengthGreaterThan: t('page.1b2a948ae150'), LengthLessThan: t('page.10525f421818'),
}))

const actionLabels = computed<Record<WafRuleAction, string>>(() => ({
  Observe: t('page.f262badacb76'),
  Block: t('dashboard.blocked'),
  Reject: t('page.bf33347e3c85'),
  Captcha: t('dashboard.captcha'),
}))

const actionColors: Record<WafRuleAction, string> = {
  Observe: 'bg-blue-500/15 text-blue-400',
  Block: 'bg-red-500/15 text-red-400',
  Reject: 'bg-orange-500/15 text-orange-400',
  Captcha: 'bg-amber-500/15 text-amber-400',
}

// 数据
const loading = ref(false)
const rules = ref<WafCustomRule[]>([])
const page = ref(1)
const total = ref(0)
const search = ref('')
const source = ref('')

// 编辑器状态
const showEditor = ref(false)
const editingRule = ref<WafCustomRule | null>(null)

// 删除确认
const showDeleteConfirm = ref(false)
const deletingRule = ref<WafCustomRule | null>(null)
const deleting = ref(false)

// 加载列表
let listRequest = 0
const loadRules = async () => {
  const request = ++listRequest
  loading.value = true
  try {
    const res = await wafRuleApi.list({ page: page.value, pageSize: 20, search: search.value, source: source.value || undefined }) as unknown as { success: boolean; data: WafCustomRule[]; total: number }
    if (request === listRequest && res?.success) {
      rules.value = res.data
      total.value = res.total
    }
  } catch {
    if (request === listRequest) showError(t('page.da6ec6643a86'))
  } finally {
    if (request === listRequest) loading.value = false
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
  showSuccess(t('page.c9105d640ece'))
}

// 切换启用
const toggleRule = async (rule: WafCustomRule) => {
  try {
    const res = await wafRuleApi.toggle(rule.id) as unknown as { success: boolean; enabled: boolean }
    if (res?.success) {
      rule.enabled = res.enabled
      showSuccess(res.enabled ? t('page.0497be5a2ca1') : t('page.045e0be12313'))
    }
  } catch {
    showError(t('settings.operation.failed'))
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
      showSuccess(t('page.04e366e33981'))
    }
  } catch {
    showError(t('settings.deletion.failed'))
  } finally {
    deleting.value = false
  }
}

// 格式化单个条件
const formatSingleCondition = (c: { field: string; fieldName?: string; operator: string; value: string }): string => {
  const field = fieldLabels.value[c.field] || c.field
  const fieldName = c.fieldName ? `[${c.fieldName}]` : ''
  const op = operatorLabels.value[c.operator] || c.operator
  const noValue = ['Exists', 'NotExists'].includes(c.operator)
  const val = noValue ? '' : ` "${c.value}"`
  return `${field}${fieldName} ${op}${val}`
}

// 格式化条件摘要
const formatConditions = (rule: WafCustomRule): string => {
  if (!rule.conditionGroups?.length) return t('page.318b70d7fc34')
  const groups = rule.conditionGroups.map(group => {
    const separator = group.operator === 'And' ? t('page.a22bdbe928cd') : t('page.60ab743988bb')
    return group.conditions.map(formatSingleCondition).join(separator)
  })
  const separator = rule.groupOperator === 'And' ? t('page.a22bdbe928cd') : t('page.60ab743988bb')
  return groups.map(group => groups.length > 1 ? `(${group})` : group).join(separator)
}

// 格式化时间
const formatTime = (iso: string): string => {
  if (!iso) return ''
  const d = new Date(iso)
  return d.toLocaleString(locale.value, { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })
}

onMounted(async () => {
  await loadRules()
  if (typeof route.query.edit === 'string') {
    try {
      const response: any = await wafRuleApi.get(route.query.edit)
      if (response.success) openEdit(response.rule)
    } catch { showError(t('page.b6a7f187a169')) }
  }
})
</script>

<template>
  <div class="space-y-4">
    <!-- 标题 -->
    <div class="card">
      <div class="flex items-center justify-between">
        <div class="flex items-center gap-3">
          <h2 class="text-lg font-semibold text-gray-100">{{ t('nav.waf') }}</h2>
          <span class="text-xs text-gray-500">{{ t('page.9b548d2d81f9') }}</span>
        </div>
        <button @click="openAdd" class="btn btn-sm btn-primary flex items-center gap-1.5">
          <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
          </svg>
          {{ t('plugins.addRule') }}
        </button>
      </div>
    </div>

    <form class="flex flex-wrap gap-3 items-center" @submit.prevent="page = 1; loadRules()">
      <input v-model="search" maxlength="200" :aria-label="t('page.ff1804ddae6d')" :placeholder="t('page.897bd34b813d')" class="input min-w-0" />
      <select v-model="source" :aria-label="t('page.21acc7496bef')" class="input" @change="page = 1; loadRules()"><option value="">{{ t('page.9f357adb6100') }}</option><option value="User">{{ t('settings.user') }}</option><option value="Config">{{ t('ui.configuration') }}</option><option value="System">{{ t('plugins.system') }}</option></select>
      <button class="btn btn-secondary" :disabled="loading">{{ t('common.search') }}</button>
      <button type="button" class="btn btn-secondary" :disabled="loading || page <= 1" @click="page--; loadRules()">{{ t('common.previous') }}</button>
      <span class="text-sm text-gray-400">{{ page }} / {{ Math.max(1, Math.ceil(total / 20)) }} {{ t('page.c8b281787653') }} {{ total }} {{ t('page.f004f1d84cf9') }}</span>
      <button type="button" class="btn btn-secondary" :disabled="loading || page * 20 >= total" @click="page++; loadRules()">{{ t('common.next') }}</button>
    </form>

    <!-- 提示 -->
    <div class="px-4 py-3 rounded-lg bg-blue-500/10 border border-blue-500/20 text-sm text-blue-300">
      <span class="font-medium">{{ t('page.be8e4dd716fb') }}</span>
      {{ t('page.e5c44a18bdb0') }}
    </div>

    <!-- 加载中 -->
    <div v-if="loading" class="card flex items-center justify-center py-12">
      <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
    </div>

    <!-- 规则列表 -->
    <template v-else>
      <div v-if="rules.length === 0" class="card text-center py-12 text-gray-500">
        <p class="text-lg mb-2">{{ t('page.3d99d7e4c4b9') }}</p>
        <p class="text-sm">{{ t('page.7b8844057036') }}</p>
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
                <span class="text-xs text-gray-600">{{ t('fields.Priority') }} {{ rule.priority }}</span>
              </div>
              <p v-if="rule.description" class="text-xs text-gray-500 mb-1.5">{{ rule.description }}</p>
              <p class="text-xs text-gray-500 font-mono leading-5">{{ formatConditions(rule) }}</p>
              <div class="flex items-center gap-4 mt-2 text-[11px] text-gray-600">
                <span v-if="rule.action === 'Block'">{{ t('dashboard.blocked') }} {{ rule.actionSeconds }}s</span>
                <span v-if="rule.action === 'Reject'">{{ t('page.572cf45ba436') }} {{ rule.responseCode }}</span>
                <span v-if="rule.action === 'Captcha'">{{ t('page.154723a1aa0a') }} {{ rule.actionSeconds }}s</span>
                <span>{{ t('page.dc4d662d071e') }} {{ formatTime(rule.createdAt) }}</span>
                <span v-if="rule.updatedAt">{{ t('page.7153f0918a93') }} {{ formatTime(rule.updatedAt) }}</span>
              </div>
            </div>

            <!-- 操作 -->
            <div class="flex items-center gap-2 flex-shrink-0">
              <button @click="openEdit(rule)"
                class="text-xs text-primary-400 hover:text-primary-300 px-2 py-1 rounded hover:bg-primary-500/10 transition-colors">
                {{ isEditable(rule) ? t('common.edit') : t('page.db8db0530432') }}
              </button>
              <button v-if="isEditable(rule)" @click="confirmDelete(rule)"
                class="text-xs text-red-400 hover:text-red-300 px-2 py-1 rounded hover:bg-red-500/10 transition-colors">
                {{ t('common.delete') }}
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
          <h3 class="text-lg font-semibold text-gray-100 mb-2">{{ t('page.c02fdcf208b3') }}</h3>
          <p class="text-sm text-gray-400 mb-4">
            {{ t('page.9bf833933e5e') }}<span class="text-gray-200">{{ deletingRule?.name }}</span>{{ t('page.342822e51a1b') }}
          </p>
          <p class="text-sm text-red-400 mb-5">{{ t('page.438b9267f6b2') }}</p>
          <div class="flex justify-end gap-3">
            <button @click="showDeleteConfirm = false" class="btn btn-sm btn-secondary">{{ t('common.cancel') }}</button>
            <button @click="executeDelete" :disabled="deleting"
              class="btn btn-sm bg-red-600 hover:bg-red-500 text-white flex items-center gap-1.5">
              <span v-if="deleting" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
              <span>{{ deleting ? t('settings.deleting') : t('settings.confirm.deletion') }}</span>
            </button>
          </div>
        </div>
      </div>
    </Teleport>
  </div>
</template>
