<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { advancedCcApi, type CreateAdvancedCcRuleRequest } from '@/api'
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
const targetLabels: Record<CcMatchTarget, string> = {
  UrlPath: 'URL 路径',
  FullUrl: '完整 URL',
  Method: 'HTTP 方法',
  ContentType: 'Content-Type',
  UserAgent: 'User-Agent',
  Referer: 'Referer',
  Header: '请求头',
  QueryParam: '查询参数',
  Cookie: 'Cookie',
  ClientIp: '客户端 IP',
  StatusCode: '响应状态码',
}

const operatorLabels: Record<CcMatchOperator, string> = {
  Equal: '等于',
  NotEqual: '不等于',
  Contains: '包含',
  NotContains: '不包含',
  StartsWith: '前缀匹配',
  EndsWith: '后缀匹配',
  Regex: '正则匹配',
  Exists: '存在',
  NotExists: '不存在',
}

const ruleTypeLabels: Record<CcRuleType, string> = {
  FrequentAccess: '高频访问限制',
  FrequentAttack: '高频攻击限制',
  FrequentError: '高频错误限制',
}

const ruleTypeDescriptions: Record<CcRuleType, string> = {
  FrequentAccess: '限制某 IP 在指定时间内的请求次数',
  FrequentAttack: '限制某 IP 触发 WAF 拦截的次数',
  FrequentError: '限制某 IP 触发错误响应的次数',
}

const actionLabels: Record<CcAction, string> = {
  Captcha: '人机验证',
  Block: '封禁 IP',
  Reject: '直接拒绝',
  RateLimit: '限速',
  LogOnly: '仅记录',
}

// 需要 fieldName 的目标（Header/QueryParam/Cookie 第一个 value 作为 key）
const needsFieldName = (target: CcMatchTarget) =>
  ['Header', 'QueryParam', 'Cookie'].includes(target)

// 不需要匹配值的操作符
const noValueOperators: CcMatchOperator[] = ['Exists', 'NotExists']

// 表单数据
const form = ref({
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
const matchTargets = Object.keys(targetLabels) as CcMatchTarget[]
const matchOperators = Object.keys(operatorLabels) as CcMatchOperator[]
const ruleTypes = Object.keys(ruleTypeLabels) as CcRuleType[]
const actions = Object.keys(actionLabels) as CcAction[]

// 是否为编辑模式
const isEditMode = computed(() => !!props.editRule)
const dialogTitle = computed(() => isEditMode.value ? '编辑规则' : '添加规则')

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
  return map[target] || '字段名'
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
const formToConditions = (): CcCondition[] => {
  return form.value.conditions.map(c => {
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
  form.value = {
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
    form.value = {
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
const addCondition = () => {
  form.value.conditions.push(createDefaultCondition())
}

// 删除条件
const removeCondition = (index: number) => {
  form.value.conditions.splice(index, 1)
}

// 提交保存
const saving = ref(false)
const saveError = ref('')

const handleSubmit = async () => {
  if (!form.value.name.trim()) {
    saveError.value = '请输入规则名称'
    return
  }

  // 检查条件值
  for (const c of form.value.conditions) {
    if (!noValueOperators.includes(c.operator) && !c.value.trim()) {
      saveError.value = '请填写所有条件的匹配值'
      return
    }
    if (needsFieldName(c.target) && !c.fieldName.trim()) {
      saveError.value = '请填写指定字段的名称'
      return
    }
  }

  saving.value = true
  saveError.value = ''

  try {
    const request: CreateAdvancedCcRuleRequest = {
      name: form.value.name,
      enabled: form.value.enabled,
      type: form.value.type,
      conditions: formToConditions(),
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
    saveError.value = error.message || '保存失败'
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
              <label class="block text-sm text-gray-400 mb-1">规则名称 <span class="text-red-400">*</span></label>
              <input v-model="form.name" type="text" class="input w-full" placeholder="输入规则名称" />
            </div>
            <div>
              <label class="block text-sm text-gray-400 mb-1">规则类型</label>
              <select v-model="form.type" class="input w-full">
                <option v-for="type in ruleTypes" :key="type" :value="type">
                  {{ ruleTypeLabels[type] }}
                </option>
              </select>
              <p class="text-xs text-gray-600 mt-1">{{ ruleTypeDescriptions[form.type] }}</p>
            </div>
          </div>

          <!-- 匹配条件（WAF 风格） -->
          <div>
            <h3 class="text-sm font-medium text-gray-300 mb-3">
              匹配条件
              <span class="text-xs text-gray-500 ml-1">满足全部条件时触发（AND 关系）</span>
            </h3>

            <div class="bg-dark-card-hover rounded-lg border border-dark-border p-4 space-y-3">
              <!-- 列标题 -->
              <div v-if="form.conditions.length > 0" class="flex items-center gap-2 text-xs text-gray-500 pl-10">
                <div class="w-[130px]">匹配字段</div>
                <div class="w-[110px]">匹配方式</div>
                <div class="flex-1">匹配值</div>
              </div>

              <!-- 条件列表 -->
              <div v-for="(condition, idx) in form.conditions" :key="idx" class="space-y-2">
                <!-- AND 分隔 -->
                <div v-if="idx > 0" class="text-xs text-primary-400 font-medium py-0.5 pl-10">AND</div>

                <div class="flex items-center gap-2">
                  <!-- 删除按钮（红色减号） -->
                  <button type="button" @click="removeCondition(idx)"
                    class="flex-shrink-0 w-7 h-7 flex items-center justify-center rounded-full border border-red-500/40 text-red-400 hover:bg-red-500/20 hover:border-red-400 transition-colors"
                    title="删除此条件">
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
              <div v-if="form.conditions.length === 0"
                class="text-center py-4 text-gray-600 text-sm">
                暂无条件，不添加条件将匹配所有请求
              </div>

              <!-- 添加 AND 条件按钮 -->
              <button @click="addCondition"
                class="flex items-center gap-1.5 px-3 py-1.5 text-xs text-primary-400 border border-primary-500/30 rounded hover:bg-primary-500/10 transition-colors">
                <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
                </svg>
                添加 AND 条件
              </button>
            </div>
          </div>

          <!-- 触发条件 -->
          <div>
            <h3 class="text-sm font-medium text-gray-300 mb-3">触发阈值</h3>
            <div class="grid grid-cols-2 md:grid-cols-4 gap-4">
              <div>
                <label class="block text-sm text-gray-400 mb-1">统计周期 <span class="text-red-400">*</span></label>
                <div class="flex items-center gap-2">
                  <input v-model.number="form.period" type="number" min="1" class="input flex-1" />
                  <span class="text-sm text-gray-500">秒</span>
                </div>
              </div>
              <div>
                <label class="block text-sm text-gray-400 mb-1">请求次数 <span class="text-red-400">*</span></label>
                <div class="flex items-center gap-2">
                  <input v-model.number="form.threshold" type="number" min="1" class="input flex-1" />
                  <span class="text-sm text-gray-500">次</span>
                </div>
              </div>
              <div>
                <label class="block text-sm text-gray-400 mb-1">执行动作</label>
                <select v-model="form.action" class="input w-full">
                  <option v-for="action in actions" :key="action" :value="action">
                    {{ actionLabels[action] }}
                  </option>
                </select>
              </div>
              <div>
                <label class="block text-sm text-gray-400 mb-1">持续时间 <span class="text-red-400">*</span></label>
                <div class="flex items-center gap-2">
                  <input v-model.number="form.actionSeconds" type="number" min="1" class="input flex-1" />
                  <span class="text-sm text-gray-500">秒</span>
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
          <button @click="handleClose" class="btn btn-sm btn-secondary">取消</button>
          <button @click="handleSubmit" :disabled="saving"
            class="btn btn-sm btn-primary flex items-center gap-1.5">
            <span v-if="saving" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
            <span>{{ saving ? '保存中...' : '保存' }}</span>
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
