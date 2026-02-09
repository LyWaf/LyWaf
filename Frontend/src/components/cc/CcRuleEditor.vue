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

// 枚举翻译
const targetLabels: Record<CcMatchTarget, string> = {
  UrlPath: 'URL 路径',
  FullUrl: '完整 URL',
  Method: '请求方法',
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

// 表单数据
const form = ref({
  name: '',
  enabled: true,
  type: 'FrequentAccess' as CcRuleType,
  conditions: [] as CcCondition[],
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

// 标题
const dialogTitle = computed(() => isEditMode.value ? '编辑规则' : '添加规则')

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
      conditions: JSON.parse(JSON.stringify(rule.conditions)),
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

// 监听显示状态
watch(() => props.show, (show) => {
  if (!show) {
    resetForm()
  }
})

// 添加条件
const addCondition = () => {
  form.value.conditions.push({
    target: 'UrlPath',
    operator: 'Equal',
    values: [],
  })
}

// 删除条件
const removeCondition = (index: number) => {
  form.value.conditions.splice(index, 1)
}

// 添加条件值
const addConditionValue = (condition: CcCondition, value: string) => {
  if (value && !condition.values.includes(value)) {
    condition.values.push(value)
  }
}

// 删除条件值
const removeConditionValue = (condition: CcCondition, index: number) => {
  condition.values.splice(index, 1)
}

// 获取目标的占位符提示
const getTargetPlaceholder = (target: CcMatchTarget): string => {
  const placeholders: Record<CcMatchTarget, string> = {
    UrlPath: 'e.g: /index.html',
    FullUrl: 'e.g: https://example.com/page',
    Method: 'e.g: GET, POST',
    ContentType: 'e.g: text/html, application/json',
    UserAgent: 'e.g: Mozilla, curl',
    Referer: 'e.g: https://google.com',
    Header: 'e.g: X-Custom-Header: value',
    QueryParam: 'e.g: id=123',
    Cookie: 'e.g: session=abc',
    ClientIp: 'e.g: 192.168.1.0/24',
    StatusCode: 'e.g: 404, 500',
  }
  return placeholders[target] || ''
}

// 提交保存
const saving = ref(false)
const saveError = ref('')

const handleSubmit = async () => {
  if (!form.value.name.trim()) {
    saveError.value = '请输入规则名称'
    return
  }

  saving.value = true
  saveError.value = ''

  try {
    const request: CreateAdvancedCcRuleRequest = {
      name: form.value.name,
      enabled: form.value.enabled,
      type: form.value.type,
      conditions: form.value.conditions.map(c => ({
        target: c.target,
        operator: c.operator,
        values: c.values,
      })),
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

// 关闭对话框
const handleClose = () => {
  emit('close')
}
</script>

<template>
  <div v-if="show" class="fixed inset-0 z-50 flex items-center justify-center bg-black/50">
    <div class="bg-dark-card rounded-lg w-full max-w-3xl max-h-[90vh] overflow-hidden">
      <!-- 标题栏 -->
      <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border">
        <h2 class="text-lg font-medium text-gray-100">{{ dialogTitle }}</h2>
        <button @click="handleClose" class="text-gray-400 hover:text-gray-200">
          <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12" />
          </svg>
        </button>
      </div>

      <!-- 表单内容 -->
      <div class="px-6 py-4 overflow-y-auto max-h-[calc(90vh-140px)]">
        <!-- 基本信息 -->
        <div class="space-y-4">
          <!-- 规则名称 -->
          <div>
            <label class="block text-sm text-gray-400 mb-1">名称 <span class="text-red-400">*</span></label>
            <input
              v-model="form.name"
              type="text"
              class="input w-full"
              placeholder="输入规则名称"
            />
          </div>

          <!-- 规则类型 -->
          <div>
            <label class="block text-sm text-gray-400 mb-1">规则类型</label>
            <select v-model="form.type" class="input w-full">
              <option v-for="type in ruleTypes" :key="type" :value="type">
                {{ ruleTypeLabels[type] }}
              </option>
            </select>
            <p class="text-xs text-gray-500 mt-1">{{ ruleTypeDescriptions[form.type] }}</p>
          </div>
        </div>

        <!-- 匹配条件 -->
        <div class="mt-6">
          <h3 class="text-sm font-medium text-gray-300 mb-3">触发下列条件认为是 CC 攻击</h3>
          <div class="bg-dark-card-hover rounded-lg p-4 space-y-4">
            <!-- 条件列表 -->
            <div v-for="(condition, index) in form.conditions" :key="index" class="space-y-3">
              <div v-if="index > 0" class="text-xs text-primary-400 font-medium">AND</div>
              <div class="flex items-start gap-3">
                <!-- 匹配目标 -->
                <div class="flex-shrink-0 w-[150px]">
                  <label class="block text-xs text-gray-500 mb-1">匹配目标</label>
                  <select v-model="condition.target" class="input w-full text-sm">
                    <option v-for="target in matchTargets" :key="target" :value="target">
                      {{ targetLabels[target] }}
                    </option>
                  </select>
                </div>

                <!-- 匹配方式 -->
                <div class="flex-shrink-0 w-[130px]">
                  <label class="block text-xs text-gray-500 mb-1">匹配方式 <span class="text-red-400">*</span></label>
                  <select v-model="condition.operator" class="input w-full text-sm">
                    <option v-for="op in matchOperators" :key="op" :value="op">
                      {{ operatorLabels[op] }}
                    </option>
                  </select>
                </div>

                <!-- 匹配内容 -->
                <div class="flex-1">
                  <label class="block text-xs text-gray-500 mb-1">匹配内容 <span class="text-red-400">*</span></label>
                  <div class="space-y-2">
                    <!-- 已添加的值 -->
                    <div v-if="condition.values.length > 0" class="flex flex-wrap gap-1">
                      <span
                        v-for="(val, vIndex) in condition.values"
                        :key="vIndex"
                        class="inline-flex items-center gap-1 px-2 py-0.5 bg-primary-500/20 text-primary-400 text-xs rounded"
                      >
                        {{ val }}
                        <button @click="removeConditionValue(condition, vIndex)" class="hover:text-red-400">×</button>
                      </span>
                    </div>
                    <!-- 输入框 -->
                    <input
                      type="text"
                      class="input w-full text-sm"
                      :placeholder="getTargetPlaceholder(condition.target)"
                      @keydown.enter.prevent="(e) => { addConditionValue(condition, (e.target as HTMLInputElement).value); (e.target as HTMLInputElement).value = '' }"
                    />
                  </div>
                </div>

                <!-- 删除按钮 -->
                <button
                  @click="removeCondition(index)"
                  class="flex-shrink-0 mt-5 p-2 text-gray-400 hover:text-red-400"
                >
                  <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                  </svg>
                </button>
              </div>
            </div>

            <!-- 添加条件按钮 -->
            <button
              @click="addCondition"
              class="flex items-center gap-2 px-4 py-2 text-sm text-primary-400 border border-primary-500/50 rounded-lg hover:bg-primary-500/10"
            >
              添加一个 AND 条件
            </button>
          </div>
        </div>

        <!-- 触发条件 -->
        <div class="mt-6 grid grid-cols-2 md:grid-cols-4 gap-4">
          <div>
            <label class="block text-sm text-gray-400 mb-1">经过时间 <span class="text-red-400">*</span></label>
            <div class="flex items-center gap-2">
              <input v-model.number="form.period" type="number" min="1" class="input flex-1" />
              <span class="text-sm text-gray-500">秒</span>
            </div>
          </div>
          <div>
            <label class="block text-sm text-gray-400 mb-1">请求次数达到 <span class="text-red-400">*</span></label>
            <div class="flex items-center gap-2">
              <input v-model.number="form.threshold" type="number" min="1" class="input flex-1" />
              <span class="text-sm text-gray-500">次</span>
            </div>
          </div>
          <div>
            <label class="block text-sm text-gray-400 mb-1">限制结果</label>
            <select v-model="form.action" class="input w-full">
              <option v-for="action in actions" :key="action" :value="action">
                {{ actionLabels[action] }}
              </option>
            </select>
          </div>
          <div>
            <label class="block text-sm text-gray-400 mb-1">{{ actionLabels[form.action] }} <span class="text-red-400">*</span></label>
            <div class="flex items-center gap-2">
              <input v-model.number="form.actionSeconds" type="number" min="1" class="input flex-1" />
              <span class="text-sm text-gray-500">秒</span>
            </div>
          </div>
        </div>

        <!-- 错误提示 -->
        <div v-if="saveError" class="mt-4 p-3 bg-red-500/10 border border-red-500/30 rounded text-red-400 text-sm">
          {{ saveError }}
        </div>
      </div>

      <!-- 底部按钮 -->
      <div class="flex items-center justify-end gap-3 px-6 py-4 border-t border-dark-border">
        <button @click="handleClose" class="btn btn-secondary">取消</button>
        <button @click="handleSubmit" class="btn btn-primary" :disabled="saving">
          {{ saving ? '保存中...' : '提交' }}
        </button>
      </div>
    </div>
  </div>
</template>
