<script setup lang="ts">
import { t } from '@/i18n'
import { ref, computed, watch } from 'vue'
import { wafRuleApi } from '@/api'
import type { WafCustomRule, WafCondition, WafConditionGroup, WafLogicalOperator, WafMatchField, WafMatchOperator, WafRuleAction } from '@/types'

interface Props {
  show: boolean
  editRule?: WafCustomRule | null
}

const props = defineProps<Props>()
const emit = defineEmits<{
  close: []
  saved: []
}>()

// 枚举翻译
const fieldLabels = computed<Record<WafMatchField, string>>(() => ({
  UriPath: t('page.f3ff6f41a826'),
  FullUrl: t('page.369a6f068cd9'),
  QueryString: t('page.9b0340697a07'),
  Method: t('page.af1029a24f14'),
  ClientIp: t('page.2d1cb6792f12'),
  XForwardedFor: 'X-Forwarded-For',
  UserAgent: 'User-Agent',
  Referer: 'Referer',
  ContentType: 'Content-Type',
  ContentLength: 'Content-Length',
  Cookie: 'Cookie',
  Header: t('fields.Headers'),
  QueryParam: t('page.1904a09fab00'),
  Body: t('dashboard.request.body'),
  ServerPort: t('page.ff673195f05f'),
}))

const operatorLabels = computed<Record<WafMatchOperator, string>>(() => ({
  Equal: t('page.4bc69a0562ff'),
  NotEqual: t('page.9678e3a7cb3c'),
  Contains: t('page.107319b8b39d'),
  NotContains: t('page.5f60ffcf600a'),
  StartsWith: t('page.6fd076957136'),
  EndsWith: t('page.152a880d3fd0'),
  Regex: t('page.7b1e7037f86f'),
  Exists: t('page.4a558371f938'),
  NotExists: t('page.4e51b67dd32c'),
  LengthGreaterThan: t('page.2b04b1142ee9'),
  LengthLessThan: t('page.1dee359f9f21'),
}))

const actionLabels = computed<Record<WafRuleAction, string>>(() => ({
  Observe: t('page.62a409a6dc1c'),
  Block: t('dashboard.blocked.ips'),
  Reject: t('page.48417544e3c1'),
  Captcha: t('page.36fbc1da5545'),
}))

const actionDescriptions = computed<Record<WafRuleAction, string>>(() => ({
  Observe: t('page.a34f838758cc'),
  Block: t('page.3cacb5f7e8fc'),
  Reject: t('page.f8961afe26b3'),
  Captcha: t('page.309ba1bfec75'),
}))

// 需要 fieldName 的字段
const needsFieldName = (field: WafMatchField) =>
  ['Cookie', 'Header', 'QueryParam'].includes(field)

// 不需要匹配值的操作符
const noValueOperators: WafMatchOperator[] = ['Exists', 'NotExists']

// 枚举键列表
const matchFields = Object.keys(fieldLabels.value) as WafMatchField[]
const matchOperators = Object.keys(operatorLabels.value) as WafMatchOperator[]
const ruleActions = Object.keys(actionLabels.value) as WafRuleAction[]

// 占位提示
const getFieldPlaceholder = (field: WafMatchField): string => {
  const map: Partial<Record<WafMatchField, string>> = {
    UriPath: '/api/users',
    FullUrl: 'https://example.com/page',
    QueryString: 'id=1&type=test',
    Method: 'GET',
    ClientIp: '192.168.1.0/24',
    XForwardedFor: '10.0.0.1',
    UserAgent: 'curl, python-requests',
    Referer: 'https://evil.com',
    ContentType: 'application/json',
    ContentLength: '1048576',
    Cookie: 'session_id=abc123',
    Header: 'X-Custom: value',
    QueryParam: 'page=1',
    Body: 'DROP TABLE',
    ServerPort: '443',
  }
  return map[field] || ''
}

const getFieldNamePlaceholder = (field: WafMatchField): string => {
  const map: Partial<Record<WafMatchField, string>> = {
    Cookie: 'session_id',
    Header: 'X-Forwarded-For',
    QueryParam: 'id',
  }
  return map[field] || t('page.2b018f9b1038')
}

const form = ref({
  name: '',
  description: '',
  enabled: true,
  priority: 100,
  groupOperator: 'Or' as WafLogicalOperator,
  conditionGroups: [] as WafConditionGroup[],
  action: 'Observe' as WafRuleAction,
  actionSeconds: 600,
  responseCode: 403,
})

// 是否编辑模式
const isEditMode = computed(() => !!props.editRule)
// 是否只读模式（非用户规则不可编辑）
const readOnly = computed(() =>
  props.editRule?.source != null && props.editRule.source !== 'User'
)
const dialogTitle = computed(() => {
  if (readOnly.value) return t('page.bbae4fac4b87')
  return isEditMode.value ? t('page.efe0feab9fd4') : t('plugins.addRule')
})

// 创建默认条件
const createDefaultCondition = (): WafCondition => ({
  field: 'UriPath',
  operator: 'Contains',
  value: '',
  ignoreCase: true,
})

const createDefaultGroup = (): WafConditionGroup => ({
  operator: 'And',
  conditions: [createDefaultCondition()],
})

// 重置
const resetForm = () => {
  form.value = {
    name: '',
    description: '',
    enabled: true,
    priority: 100,
    groupOperator: 'Or',
    conditionGroups: [createDefaultGroup()],
    action: 'Observe',
    actionSeconds: 600,
    responseCode: 403,
  }
}

watch(() => props.editRule, (rule) => {
  if (rule) {
    form.value = {
      name: rule.name,
      description: rule.description,
      enabled: rule.enabled,
      priority: rule.priority,
      groupOperator: rule.groupOperator,
      conditionGroups: JSON.parse(JSON.stringify(rule.conditionGroups)),
      action: rule.action,
      actionSeconds: rule.actionSeconds,
      responseCode: rule.responseCode,
    }
  } else {
    resetForm()
  }
}, { immediate: true })

watch(() => props.show, (show) => {
  if (!show) resetForm()
})

// 条件操作
const conditionCount = computed(() =>
  form.value.conditionGroups.reduce((count, group) => count + group.conditions.length, 0)
)

const addGroup = () => {
  if (form.value.conditionGroups.length < 5 && conditionCount.value < 20)
    form.value.conditionGroups.push(createDefaultGroup())
}

const removeGroup = (groupIndex: number) => {
  form.value.conditionGroups.splice(groupIndex, 1)
}

const addCondition = (group: WafConditionGroup) => {
  if (conditionCount.value < 20) group.conditions.push(createDefaultCondition())
}

const removeCondition = (group: WafConditionGroup, index: number) => {
  group.conditions.splice(index, 1)
}

// 提交
const saving = ref(false)
const saveError = ref('')

const handleSubmit = async () => {
  if (!form.value.name.trim()) {
    saveError.value = t('page.e2df945bb61d')
    return
  }
  if (form.value.conditionGroups.length === 0) {
    saveError.value = t('page.0567911eff2e')
    return
  }
  for (const group of form.value.conditionGroups) {
    if (group.conditions.length === 0) {
      saveError.value = t('page.d5341864bb94')
      return
    }
    for (const c of group.conditions) {
      if (!noValueOperators.includes(c.operator) && !c.value.trim()) {
        saveError.value = t('page.f4c0db941d0f')
        return
      }
      if (needsFieldName(c.field) && !c.fieldName?.trim()) {
        saveError.value = t('page.929d755a594b')
        return
      }
    }
  }

  saving.value = true
  saveError.value = ''

  try {
    const payload = {
      name: form.value.name,
      description: form.value.description,
      enabled: form.value.enabled,
      priority: form.value.priority,
      groupOperator: form.value.groupOperator,
      conditionGroups: form.value.conditionGroups,
      action: form.value.action,
      actionSeconds: form.value.actionSeconds,
      responseCode: form.value.responseCode,
    }

    if (isEditMode.value && props.editRule) {
      await wafRuleApi.update(props.editRule.id, payload)
    } else {
      await wafRuleApi.create(payload)
    }

    emit('saved')
    emit('close')
  } catch (error: any) {
    saveError.value = error.message || t('ui.save.failed')
  } finally {
    saving.value = false
  }
}

const handleClose = () => {
  emit('close')
}
</script>

<template>
  <Teleport to="body">
    <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="handleClose"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-full max-w-5xl max-h-[90vh] overflow-hidden">
        <!-- 标题 -->
        <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border">
          <h2 class="text-lg font-semibold text-gray-100">{{ dialogTitle }}</h2>
          <button @click="handleClose" class="text-gray-400 hover:text-gray-200 transition-colors">
            <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        <!-- 表单 -->
        <div class="px-6 py-4 overflow-y-auto max-h-[calc(90vh-140px)] space-y-5">
          <!-- 基本信息 -->
          <div class="grid grid-cols-2 gap-4">
            <div class="col-span-2 sm:col-span-1">
              <label class="block text-sm text-gray-400 mb-1">{{ t('dashboard.rule.name') }} <span v-if="!readOnly" class="text-red-400">*</span></label>
              <input v-model="form.name" type="text" class="input w-full" :placeholder="t('page.c6c9d649111d')" :disabled="readOnly" />
            </div>
            <div class="col-span-2 sm:col-span-1">
              <label class="block text-sm text-gray-400 mb-1">{{ t('fields.Priority') }}</label>
              <input v-model.number="form.priority" type="number" min="1" max="9999" class="input w-full" :disabled="readOnly" />
              <p class="text-xs text-gray-600 mt-1">{{ t('page.1247450bebef') }}</p>
            </div>
            <div class="col-span-2">
              <label class="block text-sm text-gray-400 mb-1">{{ t('page.dc2ba467fc7a') }}</label>
              <input v-model="form.description" type="text" class="input w-full" :placeholder="t('page.564b681911d7')" :disabled="readOnly" />
            </div>
          </div>

          <!-- 分组匹配条件 -->
          <div>
            <div class="flex flex-wrap items-center justify-between gap-3 mb-3">
              <h3 class="text-sm font-medium text-gray-300">{{ t('page.cbdfb8401c2e') }}</h3>
              <div class="flex items-center gap-2 text-xs text-gray-500">
                <button type="button"
                  class="inline-flex h-9 min-w-14 items-center justify-center rounded-md border px-4 text-sm font-medium shadow-sm transition-colors active:translate-y-px disabled:cursor-default disabled:opacity-60"
                  :class="form.groupOperator === 'And'
                    ? 'border-blue-400/50 bg-blue-500 text-white hover:bg-blue-400'
                    : 'border-amber-400/50 bg-amber-500 text-gray-950 hover:bg-amber-400'"
                  :disabled="readOnly" @click="form.groupOperator = form.groupOperator === 'And' ? 'Or' : 'And'">
                  {{ form.groupOperator === 'And' ? t('page.9f00f6770f8c') : t('page.a2123d69506f') }}
                </button>
                <span>{{ t('page.4dfe980d0e70') }}</span>
              </div>
            </div>

            <div class="space-y-4">
              <section v-for="(conditionGroup, groupIndex) in form.conditionGroups" :key="groupIndex"
                class="border border-dark-border bg-dark-card-hover overflow-hidden">
                <header class="flex items-center justify-between border-b border-dark-border px-4 py-3">
                  <strong class="text-sm text-gray-200">{{ t('page.95ec5352a597') }} {{ groupIndex + 1 }}</strong>
                  <button v-if="!readOnly && form.conditionGroups.length > 1" type="button"
                    class="text-xs text-red-400 hover:text-red-300" @click="removeGroup(groupIndex)">{{ t('page.7181350804ed') }}</button>
                </header>

                <div class="p-4 space-y-3">
                  <div v-for="(condition, conditionIndex) in conditionGroup.conditions" :key="conditionIndex">
                    <div v-if="conditionIndex > 0" class="flex items-center gap-3 py-2">
                      <button type="button"
                        class="inline-flex h-9 min-w-14 items-center justify-center rounded-md border px-4 text-sm font-medium shadow-sm transition-colors active:translate-y-px disabled:cursor-default disabled:opacity-60"
                        :class="conditionGroup.operator === 'And'
                          ? 'border-blue-400/50 bg-blue-500 text-white hover:bg-blue-400'
                          : 'border-amber-400/50 bg-amber-500 text-gray-950 hover:bg-amber-400'" :disabled="readOnly"
                        @click="conditionGroup.operator = conditionGroup.operator === 'And' ? 'Or' : 'And'">
                        {{ conditionGroup.operator === 'And' ? t('page.9f00f6770f8c') : t('page.a2123d69506f') }}
                      </button>
                      <span class="h-px flex-1 bg-dark-border"></span>
                    </div>

                    <div class="grid grid-cols-1 md:grid-cols-[28px_130px_110px_minmax(180px,1fr)_auto] gap-2 items-center">
                      <button v-if="!readOnly" type="button"
                        class="w-7 h-7 flex items-center justify-center rounded-full border border-red-500/40 text-red-400 hover:bg-red-500/20"
                        :title="t('page.3312fb0423e1')" @click="removeCondition(conditionGroup, conditionIndex)">−</button>
                      <span v-else></span>
                      <select v-model="condition.field" class="input w-full text-sm" :disabled="readOnly">
                        <option v-for="field in matchFields" :key="field" :value="field">{{ fieldLabels[field] }}</option>
                      </select>
                      <select v-model="condition.operator" class="input w-full text-sm" :disabled="readOnly">
                        <option v-for="operator in matchOperators" :key="operator" :value="operator">{{ operatorLabels[operator] }}</option>
                      </select>
                      <div class="flex gap-2 min-w-0">
                        <input v-if="needsFieldName(condition.field)" v-model="condition.fieldName" class="input w-32 text-sm"
                          :placeholder="getFieldNamePlaceholder(condition.field)" :disabled="readOnly" />
                        <input v-if="!noValueOperators.includes(condition.operator)" v-model="condition.value"
                          class="input min-w-0 flex-1 text-sm" :placeholder="getFieldPlaceholder(condition.field)" :disabled="readOnly" />
                        <span v-else class="text-xs text-gray-500 self-center">{{ t('page.f0f654116fcb') }}</span>
                      </div>
                      <label class="flex items-center gap-1.5 text-xs text-gray-500 whitespace-nowrap">
                        <input v-model="condition.ignoreCase" type="checkbox" :disabled="readOnly" />{{ t('page.4c8f72724877') }}
                      </label>
                    </div>
                  </div>

                  <div v-if="conditionGroup.conditions.length === 0" class="py-4 text-center text-sm text-red-400">{{ t('page.d5341864bb94') }}</div>
                  <button v-if="!readOnly" type="button" class="text-sm text-primary-400 hover:text-primary-300 disabled:text-gray-600"
                    :disabled="conditionCount >= 20" @click="addCondition(conditionGroup)">{{ t('page.1dbd0bf0fae8') }}</button>
                </div>
              </section>

              <div v-if="form.conditionGroups.length === 0" class="border border-dark-border py-8 text-center text-sm text-gray-500">{{ t('page.a2ed6cad6545') }}</div>
              <div v-if="!readOnly" class="flex items-center gap-3">
                <button type="button" class="btn btn-secondary" :disabled="form.conditionGroups.length >= 5 || conditionCount >= 20"
                  @click="addGroup">{{ t('page.9f00002b6002') }}</button>
                <span class="text-xs text-gray-500">{{ t('page.2bd5d5fda21e') }} {{ conditionCount }} {{ t('page.22ba276c22bf') }}</span>
              </div>
            </div>
          </div>


          <!-- 执行动作 -->
          <div>
            <h3 class="text-sm font-medium text-gray-300 mb-3">{{ t('fields.Action') }}</h3>
            <div class="grid grid-cols-2 gap-4">
              <div>
                <label class="block text-sm text-gray-400 mb-1">{{ t('page.301c53dd5e30') }}</label>
                <select v-model="form.action" class="input w-full" :disabled="readOnly">
                  <option v-for="a in ruleActions" :key="a" :value="a">{{ actionLabels[a] }}</option>
                </select>
                <p class="text-xs text-gray-600 mt-1">{{ actionDescriptions[form.action] }}</p>
              </div>

              <!-- Block / Captcha 持续时间 -->
              <div v-if="form.action === 'Block' || form.action === 'Captcha'">
                <label class="block text-sm text-gray-400 mb-1">{{ t('page.cc758c333732') }}</label>
                <div class="flex items-center gap-2">
                  <input v-model.number="form.actionSeconds" type="number" min="1" class="input flex-1" :disabled="readOnly" />
                  <span class="text-sm text-gray-500">{{ t('page.9dcdc2b289b9') }}</span>
                </div>
              </div>

              <!-- Reject 响应码 -->
              <div v-if="form.action === 'Reject'">
                <label class="block text-sm text-gray-400 mb-1">{{ t('page.327607f80ee2') }}</label>
                <select v-model.number="form.responseCode" class="input w-full" :disabled="readOnly">
                  <option :value="403">403 Forbidden</option>
                  <option :value="404">404 Not Found</option>
                  <option :value="429">429 Too Many Requests</option>
                  <option :value="500">500 Internal Server Error</option>
                  <option :value="502">502 Bad Gateway</option>
                  <option :value="503">503 Service Unavailable</option>
                </select>
              </div>
            </div>
          </div>

          <!-- 错误提示 -->
          <div v-if="saveError" class="p-3 bg-red-500/10 border border-red-500/30 rounded-lg text-red-400 text-sm">
            {{ saveError }}
          </div>
        </div>

        <!-- 底部 -->
        <div class="flex items-center justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="handleClose" class="btn btn-sm btn-secondary">
            {{ readOnly ? t('common.close') : t('common.cancel') }}
          </button>
          <button v-if="!readOnly" @click="handleSubmit" :disabled="saving"
            class="btn btn-sm btn-primary flex items-center gap-1.5">
            <span v-if="saving" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
            <span>{{ saving ? t('page.917b1c1f18d0') : t('common.save') }}</span>
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
