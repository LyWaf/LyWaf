<script setup lang="ts">
import { t } from '@/plugins/analysis/i18n'
import { ref, computed, watch } from 'vue'
import { advancedCcApi, type CreateAdvancedCcRuleRequest } from '@/plugins/analysis/api'
import type { AdvancedCcRule, CcCondition, CcMatchTarget, CcMatchOperator, CcRuleType, CcAction } from '@/types'

interface Props {
  show: boolean
  editRule?: AdvancedCcRule | null
}

const props = defineProps<Props>()
const emit = defineEmits<{
  close: []
  saved: [rule: AdvancedCcRule]
}>()

// 枚举翻译 —— 与 WAF 规则保持一致风格
const targetLabels = computed<Record<CcMatchTarget, string>>(() => ({
  UrlPath: t('page.f3ff6f41a826'),
  FullUrl: t('page.369a6f068cd9'),
  Method: t('page.af1029a24f14'),
  ContentType: 'Content-Type',
  UserAgent: 'User-Agent',
  Referer: 'Referer',
  Header: t('page.cea1aafe9416'),
  QueryParam: t('page.1904a09fab00'),
  Cookie: 'Cookie',
  ClientIp: t('page.2d1cb6792f12'),
  StatusCode: t('page.327607f80ee2'),
}))

const operatorLabels = computed<Record<CcMatchOperator, string>>(() => ({
  Equal: t('page.4bc69a0562ff'),
  NotEqual: t('page.9678e3a7cb3c'),
  Contains: t('page.107319b8b39d'),
  NotContains: t('page.5f60ffcf600a'),
  StartsWith: t('page.6fd076957136'),
  EndsWith: t('page.152a880d3fd0'),
  Regex: t('page.7b1e7037f86f'),
  Exists: t('page.4a558371f938'),
  NotExists: t('page.4e51b67dd32c'),
}))

const ruleTypeLabels = computed<Record<CcRuleType, string>>(() => ({
  FrequentAccess: t('page.38d9f66567c0'),
  FrequentAttack: t('page.b955b87d932c'),
  FrequentError: t('page.1eb791fb8b9d'),
}))

const ruleTypeDescriptions = computed<Record<CcRuleType, string>>(() => ({
  FrequentAccess: t('page.d1fe6f55dc82'),
  FrequentAttack: t('page.f3e27fc75cab'),
  FrequentError: t('page.97dc3d73bedd'),
}))

const actionLabels = computed<Record<CcAction, string>>(() => ({
  Captcha: t('page.36fbc1da5545'),
  Block: t('page.c35813ecc371'),
  Reject: t('page.025a3ebbbc66'),
  RateLimit: t('page.106a487eeea5'),
  LogOnly: t('page.c4ba4276172a'),
}))

// 需要 fieldName 的目标（Header/QueryParam/Cookie 第一个 value 作为 key）
const needsFieldName = (target: CcMatchTarget) =>
  ['Header', 'QueryParam', 'Cookie'].includes(target)

// 不需要匹配值的操作符
const noValueOperators: CcMatchOperator[] = ['Exists', 'NotExists']

// 表单数据
type FormCondition = { target: CcMatchTarget; operator: CcMatchOperator; value: string; fieldName: string }
const groups = ref<Array<{ operator: 'And' | 'Or'; conditions: FormCondition[] }>>([])
const groupOperator = ref<'And' | 'Or'>('Or')
const relationshipClass = (op: string) => op === 'And' ? 'border-blue-400/50 bg-blue-500 text-white hover:bg-blue-400' : 'border-amber-400/50 bg-amber-500 text-gray-950 hover:bg-amber-400'
const form = ref({
  scope: 'SiteGroup' as 'SiteGroup' | 'Node' | 'Cluster',
  useManagedMembership: false,
  expectedNodes: '',
  requireCompleteCoverage: false,
  protectionGroup: '',
  name: '',
  enabled: true,
  type: 'FrequentAccess' as CcRuleType,
  conditions: [] as Array<{
    target: CcMatchTarget
    operator: CcMatchOperator
    value: string
    fieldName: string
  }>,
  period: 10,
  threshold: 100,
  action: 'Captcha' as CcAction,
  actionSeconds: 600,
  priority: 100,
})

// 枚举选项
const matchTargets = Object.keys(targetLabels.value) as CcMatchTarget[]
const matchOperators = Object.keys(operatorLabels.value) as CcMatchOperator[]
const ruleTypes = Object.keys(ruleTypeLabels.value) as CcRuleType[]
const actions = Object.keys(actionLabels.value) as CcAction[]

// 是否为编辑模式
const isEditMode = computed(() => !!props.editRule)
const dialogTitle = computed(() => isEditMode.value ? t('page.efe0feab9fd4') : t('page.c9f9e18b0623'))

// 占位提示
const getTargetPlaceholder = (target: CcMatchTarget): string => {
  const map: Record<CcMatchTarget, string> = {
    UrlPath: '/api/users',
    FullUrl: 'https://example.com/page',
    Method: 'GET',
    ContentType: 'application/json',
    UserAgent: 'curl, python-requests',
    Referer: 'https://evil.com',
    Header: 'X-Custom: value',
    QueryParam: 'id=123',
    Cookie: 'session=abc',
    ClientIp: '192.168.1.0/24',
    StatusCode: '404',
  }
  return map[target] || ''
}

const getFieldNamePlaceholder = (target: CcMatchTarget): string => {
  const map: Partial<Record<CcMatchTarget, string>> = {
    Cookie: 'session_id',
    Header: 'X-Forwarded-For',
    QueryParam: 'id',
  }
  return map[target] || t('page.2b018f9b1038')
}

// 将后端 CcCondition（target, operator, values[]）转为表单格式
const conditionsToForm = (conditions: CcCondition[]) => {
  return conditions.map(c => {
    const isKeyed = needsFieldName(c.target)
    return {
      target: c.target,
      operator: c.operator,
      fieldName: isKeyed && c.values.length > 0 ? c.values[0] : '',
      value: isKeyed ? (c.values.length > 1 ? c.values.slice(1).join(', ') : '') : c.values.join(', '),
    }
  })
}

// 将表单格式转回后端 CcCondition
const formToConditions = (conditions: FormCondition[]): CcCondition[] => {
  return conditions.map(c => {
    const isKeyed = needsFieldName(c.target)
    const rawValues = c.value.split(',').map(v => v.trim()).filter(Boolean)
    return {
      target: c.target,
      operator: c.operator,
      values: isKeyed ? [c.fieldName.trim(), ...rawValues] : rawValues,
    }
  })
}

// 重置表单
const resetForm = () => {
  groups.value = []
  groupOperator.value = 'Or'
  form.value = {
    scope: 'SiteGroup',
    useManagedMembership: false,
    expectedNodes: '',
    requireCompleteCoverage: false,
    protectionGroup: '',
    name: '',
    enabled: true,
    type: 'FrequentAccess',
    conditions: [],
    period: 10,
    threshold: 100,
    action: 'Captcha',
    actionSeconds: 600,
    priority: 100,
  }
}

// 监听编辑规则变化
watch(() => props.editRule, (rule) => {
  if (rule) {
    groupOperator.value = rule.groupOperator || 'Or'
    groups.value = rule.conditionGroups?.map(g => ({ operator: g.operator, conditions: conditionsToForm(g.conditions) })) ||
      (rule.conditions?.length ? [{ operator: 'And', conditions: conditionsToForm(rule.conditions) }] : [])
    form.value = {
      scope: rule.scope || 'SiteGroup',
      useManagedMembership: rule.useManagedMembership || false,
      expectedNodes: (rule.expectedNodes || []).join(', '),
      requireCompleteCoverage: rule.requireCompleteCoverage || false,
      protectionGroup: rule.protectionGroup || '',
      name: rule.name,
      enabled: rule.enabled,
      type: rule.type,
      conditions: conditionsToForm(JSON.parse(JSON.stringify(rule.conditions))),
      period: rule.period,
      threshold: rule.threshold,
      action: rule.action,
      actionSeconds: rule.actionSeconds,
      priority: rule.priority,
    }
  } else {
    resetForm()
  }
}, { immediate: true })

watch(() => props.show, (show) => {
  if (!show) resetForm()
})

// 创建默认条件
const createDefaultCondition = () => ({
  target: 'UrlPath' as CcMatchTarget,
  operator: 'Contains' as CcMatchOperator,
  value: '',
  fieldName: '',
})

// 添加条件
const addCondition = (index: number) => {
  groups.value[index].conditions.push(createDefaultCondition())
}
const addGroup = () => groups.value.push({ operator: 'And', conditions: [createDefaultCondition()] })

// 删除条件
const removeCondition = (group: number, index: number) => {
  groups.value[group].conditions.splice(index, 1)
}

// 提交保存
const saving = ref(false)
const saveError = ref('')

const handleSubmit = async () => {
  if (!form.value.name.trim()) {
    saveError.value = t('page.e2df945bb61d')
    return
  }

  // 检查条件值
  if (groups.value.some(g => !g.conditions.length)) { saveError.value = t('page.d5341864bb94'); return }
  for (const c of groups.value.flatMap(g => g.conditions)) {
    if (!noValueOperators.includes(c.operator) && !c.value.trim()) {
      saveError.value = t('page.f4c0db941d0f')
      return
    }
    if (needsFieldName(c.target) && !c.fieldName.trim()) {
      saveError.value = t('page.929d755a594b')
      return
    }
  }

  saving.value = true
  saveError.value = ''

  try {
    const request: CreateAdvancedCcRuleRequest = {
      scope: form.value.scope,
      useManagedMembership: form.value.scope !== 'Node' && form.value.useManagedMembership,
      expectedNodes: form.value.scope === 'Node' || form.value.useManagedMembership ? []
        : form.value.expectedNodes.split(',').map(value => value.trim()).filter(Boolean),
      requireCompleteCoverage: form.value.scope !== 'Node' && (form.value.useManagedMembership || form.value.requireCompleteCoverage),
      protectionGroup: form.value.scope === 'SiteGroup' ? form.value.protectionGroup.trim() : '',
      name: form.value.name,
      enabled: form.value.enabled,
      type: form.value.type,
      conditions: [],
      groupOperator: groupOperator.value,
      conditionGroups: groups.value.length ? groups.value.map(g => ({ operator: g.operator, conditions: formToConditions(g.conditions) })) : undefined,
      period: form.value.period,
      threshold: form.value.threshold,
      action: form.value.action,
      actionSeconds: form.value.actionSeconds,
      priority: form.value.priority,
    }

    if (isEditMode.value && props.editRule) {
      await advancedCcApi.updateRule({ ...request, id: props.editRule.id })
    } else {
      await advancedCcApi.addRule(request)
    }

    emit('saved', form.value as unknown as AdvancedCcRule)
    emit('close')
  } catch (error: any) {
    saveError.value = error.message || t('page.6309a3bb5ba4')
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
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-full max-w-3xl max-h-[90vh] overflow-hidden">
        <!-- 标题栏 -->
        <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border">
          <h2 class="text-lg font-semibold text-gray-100">{{ dialogTitle }}</h2>
          <button @click="handleClose" class="text-gray-400 hover:text-gray-200 transition-colors">
            <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        <!-- 表单内容 -->
        <div class="px-6 py-4 overflow-y-auto max-h-[calc(90vh-140px)] space-y-5">
          <!-- 基本信息 -->
          <div class="grid grid-cols-2 gap-4">
            <div>
              <label class="block text-sm text-gray-400 mb-1">{{ t('page.18dba51b47c8') }} <span class="text-red-400">*</span></label>
              <input v-model="form.name" type="text" class="input w-full" :placeholder="t('page.1094c4a67890')" />
            </div>
            <div>
              <label class="block text-sm text-gray-400 mb-1">{{ t('page.962032f50fea') }}</label>
              <select v-model="form.type" class="input w-full">
                <option v-for="type in ruleTypes" :key="type" :value="type">
                  {{ ruleTypeLabels[type] }}
                </option>
              </select>
              <p class="text-xs text-gray-600 mt-1">{{ ruleTypeDescriptions[form.type] }}</p>
            </div>
          </div>

          <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <label class="block text-sm text-gray-400">{{ t('cc.scope') }}
              <select v-model="form.scope" class="input w-full mt-1" :aria-label="t('cc.scope')">
                <option value="SiteGroup">{{ t('cc.scope.siteGroup') }}</option>
                <option value="Node">{{ t('cc.scope.node') }}</option>
                <option value="Cluster">{{ t('cc.scope.cluster') }}</option>
              </select>
            </label>
            <label v-if="form.scope !== 'Node'" class="block text-sm text-gray-400">{{ t('cc.membership') }}
              <select v-model="form.useManagedMembership" class="input w-full mt-1" :aria-label="t('cc.membership')">
                <option :value="false">{{ t('cc.membership.explicit') }}</option>
                <option :value="true">{{ t('cc.membership.managed') }}</option>
              </select>
            </label>
            <label v-if="form.scope !== 'Node' && !form.useManagedMembership" class="block text-sm text-gray-400">{{ t('cc.expectedNodes') }}
              <input v-model="form.expectedNodes" type="text" maxlength="33024" class="input w-full mt-1" />
            </label>
            <label v-if="form.scope !== 'Node'" class="flex items-center gap-2 text-sm text-gray-400">
              <input type="checkbox" :disabled="form.useManagedMembership" :checked="form.useManagedMembership || form.requireCompleteCoverage"
                @change="form.requireCompleteCoverage = ($event.target as HTMLInputElement).checked" />
              {{ t('cc.requireCompleteCoverage') }}
            </label>
          </div>
          <div v-if="form.scope === 'SiteGroup'">
            <label for="cc-protection-group" class="block text-sm text-gray-400 mb-1">{{ t('cc.protectionGroup') }}</label>
            <input id="cc-protection-group" v-model="form.protectionGroup" type="text" maxlength="128" class="input w-full" />
          </div>

          <!-- 匹配条件（WAF 风格） -->
          <div>
            <h3 class="text-sm font-medium text-gray-300 mb-3">
              {{ t('page.cbdfb8401c2e') }}
              <button type="button" class="ml-3 px-3 py-1 border rounded" :class="relationshipClass(groupOperator)" :title="t('page.f634ae18fd65')" @click="groupOperator = groupOperator === 'And' ? 'Or' : 'And'">{{ groupOperator === 'And' ? t('page.9f00f6770f8c') : t('page.a2123d69506f') }} ▾</button>
            </h3>

            <div v-for="(group, groupIndex) in groups" :key="groupIndex" class="bg-dark-card-hover rounded-lg border border-dark-border p-4 space-y-3 mb-3">
              <div class="flex justify-between items-center"><h4>{{ t('page.95ec5352a597') }} {{ groupIndex + 1 }}</h4><button type="button" class="text-red-400" @click="groups.splice(groupIndex, 1)">{{ t('page.7181350804ed') }}</button></div>
              <!-- 列标题 -->
              <div v-if="group.conditions.length > 0" class="flex items-center gap-2 text-xs text-gray-500 pl-10">
                <div class="w-[130px]">{{ t('page.55fde6c2daae') }}</div>
                <div class="w-[110px]">{{ t('page.e1f0f1cb1ab4') }}</div>
                <div class="flex-1">{{ t('page.8c876267e7d1') }}</div>
              </div>

              <!-- 条件列表 -->
              <div v-for="(condition, idx) in group.conditions" :key="idx" class="space-y-2">
                <!-- AND 分隔 -->
                <button v-if="idx > 0" type="button" class="px-3 py-1 border rounded" :class="relationshipClass(group.operator)" :title="t('page.10b3c5263e0c')" @click="group.operator = group.operator === 'And' ? 'Or' : 'And'">{{ group.operator === 'And' ? t('page.9f00f6770f8c') : t('page.a2123d69506f') }} ▾</button>

                <div class="flex flex-wrap items-center gap-2">
                  <!-- 删除按钮（红色减号） -->
                  <button type="button" @click="removeCondition(groupIndex, idx)"
                    class="flex-shrink-0 w-7 h-7 flex items-center justify-center rounded-full border border-red-500/40 text-red-400 hover:bg-red-500/20 hover:border-red-400 transition-colors"
                    :title="t('page.3312fb0423e1')">
                    <svg class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2.5" viewBox="0 0 24 24">
                      <path stroke-linecap="round" d="M5 12h14" />
                    </svg>
                  </button>

                  <!-- 匹配字段 -->
                  <div class="flex-shrink-0 w-[130px]">
                    <select v-model="condition.target" class="input w-full text-sm">
                      <option v-for="t in matchTargets" :key="t" :value="t">{{ targetLabels[t] }}</option>
                    </select>
                  </div>

                  <!-- 字段名（仅 Cookie/Header/QueryParam） -->
                  <div v-if="needsFieldName(condition.target)" class="flex-shrink-0 w-[100px]">
                    <input v-model="condition.fieldName" type="text" class="input w-full text-sm"
                      :placeholder="getFieldNamePlaceholder(condition.target)" />
                  </div>

                  <!-- 操作符 -->
                  <div class="flex-shrink-0 w-[110px]">
                    <select v-model="condition.operator" class="input w-full text-sm">
                      <option v-for="op in matchOperators" :key="op" :value="op">{{ operatorLabels[op] }}</option>
                    </select>
                  </div>

                  <!-- 匹配值 -->
                  <div class="flex-1" v-if="!noValueOperators.includes(condition.operator)">
                    <input v-model="condition.value" type="text" class="input w-full text-sm"
                      :placeholder="getTargetPlaceholder(condition.target)" />
                  </div>
                </div>
              </div>

              <!-- 空状态 -->
              <div v-if="group.conditions.length === 0"
                class="text-center py-4 text-gray-600 text-sm">
                {{ t('page.d5341864bb94') }}
              </div>

              <!-- 添加 AND 条件按钮 -->
              <button type="button" @click="addCondition(groupIndex)" :disabled="groups.reduce((n, g) => n + g.conditions.length, 0) >= 20"
                class="flex items-center gap-1.5 px-3 py-1.5 text-xs text-primary-400 border border-primary-500/30 rounded hover:bg-primary-500/10 transition-colors">
                <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
                </svg>
                {{ t('page.236878c0b380') }}
              </button>
            </div>
            <button type="button" :disabled="groups.length >= 5 || groups.reduce((n, g) => n + g.conditions.length, 0) >= 20" class="px-3 py-2 border border-primary-500/30 text-primary-400 rounded" @click="addGroup">{{ t('page.cb35cc5b434f') }}</button>
          </div>

          <!-- 触发条件 -->
          <div>
            <h3 class="text-sm font-medium text-gray-300 mb-3">{{ t('page.aaae142015b7') }}</h3>
            <div class="grid grid-cols-2 md:grid-cols-4 gap-4">
              <div>
                <label class="block text-sm text-gray-400 mb-1">{{ t('page.f4f09df9e160') }} <span class="text-red-400">*</span></label>
                <div class="flex items-center gap-2">
                  <input v-model.number="form.period" type="number" min="1" class="input flex-1" />
                  <span class="text-sm text-gray-500">{{ t('page.9dcdc2b289b9') }}</span>
                </div>
              </div>
              <div>
                <label class="block text-sm text-gray-400 mb-1">{{ t('page.a2e2ce97f2e2') }} <span class="text-red-400">*</span></label>
                <div class="flex items-center gap-2">
                  <input v-model.number="form.threshold" type="number" min="1" class="input flex-1" />
                  <span class="text-sm text-gray-500">{{ t('page.172fb7e67b9b') }}</span>
                </div>
              </div>
              <div>
                <label class="block text-sm text-gray-400 mb-1">{{ t('page.45f83590f4b8') }}</label>
                <select v-model="form.action" class="input w-full">
                  <option v-for="action in actions" :key="action" :value="action">
                    {{ actionLabels[action] }}
                  </option>
                </select>
              </div>
              <div>
                <label class="block text-sm text-gray-400 mb-1">{{ t('page.cc758c333732') }} <span class="text-red-400">*</span></label>
                <div class="flex items-center gap-2">
                  <input v-model.number="form.actionSeconds" type="number" min="1" class="input flex-1" />
                  <span class="text-sm text-gray-500">{{ t('page.9dcdc2b289b9') }}</span>
                </div>
              </div>
            </div>
          </div>

          <!-- 错误提示 -->
          <div v-if="saveError" class="p-3 bg-red-500/10 border border-red-500/30 rounded-lg text-red-400 text-sm">
            {{ saveError }}
          </div>
        </div>

        <!-- 底部按钮 -->
        <div class="flex items-center justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="handleClose" class="btn btn-sm btn-secondary">{{ t('page.2cd0f3be8738') }}</button>
          <button @click="handleSubmit" :disabled="saving"
            class="btn btn-sm btn-primary flex items-center gap-1.5">
            <span v-if="saving" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
            <span>{{ saving ? t('page.917b1c1f18d0') : t('page.a3030bf8f16d') }}</span>
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
