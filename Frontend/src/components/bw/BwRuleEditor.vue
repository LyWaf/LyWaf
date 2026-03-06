<script setup lang="ts">
import { ref, computed, watch, onMounted } from 'vue'
import { bwRuleApi, wafRuleApi } from '@/api'
import type { BwRule, BwCondition } from '@/api'
import { useToast } from '@/composables/useToast'

const props = defineProps<{
  rule?: BwRule | null
}>()

const emit = defineEmits<{
  close: []
  saved: []
}>()

const { showSuccess, showError } = useToast()

// 表单
const name = ref('')
const type = ref<'White' | 'Black'>('Black')
const conditions = ref<BwCondition[]>([])
const saving = ref(false)

// 枚举选项
const fieldOptions = ref<Array<{ name: string; label: string }>>([])
const operatorOptions = ref<Array<{ name: string; label: string }>>([])

// 需要 FieldName 的字段
const needsFieldName = (field: string) => ['Header', 'Cookie', 'QueryParam'].includes(field)

// 默认常用字段子集
const defaultFields = [
  { name: 'ClientIp', label: '客户端 IP' },
  { name: 'UriPath', label: 'URL 路径' },
  { name: 'FullUrl', label: '完整 URL' },
  { name: 'Method', label: 'HTTP 方法' },
  { name: 'Host', label: 'Host (Header)' },
  { name: 'UserAgent', label: 'User-Agent' },
  { name: 'Referer', label: 'Referer' },
  { name: 'Header', label: '请求头 (指定)' },
  { name: 'Cookie', label: 'Cookie (指定)' },
  { name: 'QueryParam', label: '查询参数 (指定)' },
  { name: 'QueryString', label: '查询字符串' },
  { name: 'XForwardedFor', label: 'X-Forwarded-For' },
  { name: 'ContentType', label: 'Content-Type' },
  { name: 'ServerPort', label: '服务端口' },
]

const defaultOperators = [
  { name: 'Equal', label: '等于' },
  { name: 'NotEqual', label: '不等于' },
  { name: 'Contains', label: '包含' },
  { name: 'NotContains', label: '不包含' },
  { name: 'StartsWith', label: '前缀匹配' },
  { name: 'EndsWith', label: '后缀匹配' },
  { name: 'Regex', label: '正则匹配' },
  { name: 'Exists', label: '存在' },
  { name: 'NotExists', label: '不存在' },
  { name: 'LengthGreaterThan', label: '长度大于' },
  { name: 'LengthLessThan', label: '长度小于' },
]

// 初始化
onMounted(async () => {
  // 尝试从 waf-rules/enums 获取枚举
  try {
    const res = await wafRuleApi.enums() as any
    if (res?.success) {
      fieldOptions.value = res.fields || defaultFields
      operatorOptions.value = res.operators || defaultOperators
    } else {
      fieldOptions.value = defaultFields
      operatorOptions.value = defaultOperators
    }
  } catch {
    fieldOptions.value = defaultFields
    operatorOptions.value = defaultOperators
  }
})

// 编辑模式：加载规则数据
const isEdit = computed(() => !!props.rule)

watch(() => props.rule, (rule) => {
  if (rule) {
    name.value = rule.name
    type.value = rule.type
    conditions.value = rule.conditions.map(c => ({ ...c }))
  } else {
    name.value = ''
    type.value = 'Black'
    conditions.value = [{ field: 'ClientIp', operator: 'Equal', value: '', ignoreCase: true }]
  }
}, { immediate: true })

// 条件操作
const addCondition = () => {
  conditions.value.push({
    field: 'ClientIp',
    operator: 'Equal',
    value: '',
    ignoreCase: true,
  })
}

const removeCondition = (index: number) => {
  if (conditions.value.length <= 1) return
  conditions.value.splice(index, 1)
}

// 不需要输入值的操作符
const noValueOperator = (op: string) => ['Exists', 'NotExists'].includes(op)

// 保存
const save = async () => {
  if (!name.value.trim()) {
    showError('请输入规则名称')
    return
  }
  if (conditions.value.length === 0) {
    showError('至少需要一个匹配条件')
    return
  }
  // 检查条件是否完整
  for (const c of conditions.value) {
    if (!noValueOperator(c.operator) && !c.value.trim()) {
      showError('请填写所有条件的匹配值')
      return
    }
    if (needsFieldName(c.field) && !c.fieldName?.trim()) {
      showError('请指定字段名称（Header/Cookie/QueryParam）')
      return
    }
  }

  saving.value = true
  try {
    const payload = {
      name: name.value.trim(),
      type: type.value,
      enabled: props.rule?.enabled ?? true,
      conditions: conditions.value.map(c => ({
        field: c.field,
        fieldName: needsFieldName(c.field) ? c.fieldName : undefined,
        operator: c.operator,
        value: c.value,
        ignoreCase: c.ignoreCase,
      })),
    }

    let res: any
    if (isEdit.value && props.rule) {
      res = await bwRuleApi.update(props.rule.id, payload)
    } else {
      res = await bwRuleApi.create(payload)
    }

    if (res?.success) {
      showSuccess(isEdit.value ? '规则已更新' : '规则已创建')
      emit('saved')
    } else {
      showError(res?.message || '保存失败')
    }
  } catch {
    showError('保存失败')
  } finally {
    saving.value = false
  }
}

const getFieldLabel = (name: string) => {
  const found = fieldOptions.value.find(f => f.name === name)
  return found?.label || name
}

const getOperatorLabel = (name: string) => {
  const found = operatorOptions.value.find(o => o.name === name)
  return found?.label || name
}
</script>

<template>
  <Teleport to="body">
    <div class="fixed inset-0 z-50 flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="emit('close')"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-[640px] max-w-[95vw] max-h-[90vh] flex flex-col">
        <!-- 标题 -->
        <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border shrink-0">
          <h3 class="text-lg font-semibold text-gray-100">
            {{ isEdit ? '编辑规则' : '添加规则' }}
          </h3>
          <button @click="emit('close')" class="text-gray-400 hover:text-gray-200 text-xl leading-none">&times;</button>
        </div>

        <!-- 表单 -->
        <div class="px-6 py-5 space-y-5 overflow-y-auto flex-1">
          <!-- 规则名称 -->
          <div>
            <label class="block text-sm text-gray-400 mb-1">规则名称 <span class="text-red-400">*</span></label>
            <input v-model="name" type="text" class="input w-full" placeholder="如：封禁恶意爬虫" />
          </div>

          <!-- 规则类型 -->
          <div>
            <label class="block text-sm text-gray-400 mb-2">规则类型</label>
            <div class="flex gap-3">
              <label
                :class="['flex items-center gap-2 px-4 py-2.5 rounded-lg border cursor-pointer transition-colors',
                  type === 'Black'
                    ? 'border-red-500/50 bg-red-500/10 text-red-400'
                    : 'border-dark-border bg-dark-bg text-gray-400 hover:text-gray-200']"
              >
                <input type="radio" v-model="type" value="Black" class="hidden" />
                <span class="w-2 h-2 rounded-full" :class="type === 'Black' ? 'bg-red-400' : 'bg-gray-600'"></span>
                <span class="text-sm font-medium">黑名单</span>
                <span class="text-xs opacity-60">命中时拦截</span>
              </label>
              <label
                :class="['flex items-center gap-2 px-4 py-2.5 rounded-lg border cursor-pointer transition-colors',
                  type === 'White'
                    ? 'border-green-500/50 bg-green-500/10 text-green-400'
                    : 'border-dark-border bg-dark-bg text-gray-400 hover:text-gray-200']"
              >
                <input type="radio" v-model="type" value="White" class="hidden" />
                <span class="w-2 h-2 rounded-full" :class="type === 'White' ? 'bg-green-400' : 'bg-gray-600'"></span>
                <span class="text-sm font-medium">白名单</span>
                <span class="text-xs opacity-60">命中时放行</span>
              </label>
            </div>
          </div>

          <!-- 匹配条件 -->
          <div>
            <div class="flex items-center justify-between mb-2">
              <label class="text-sm text-gray-400">
                匹配条件 <span class="text-xs text-gray-500">（全部满足时触发 AND）</span>
              </label>
              <button @click="addCondition" class="text-xs text-primary-400 hover:text-primary-300 flex items-center gap-1">
                <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v12m6-6H6" />
                </svg>
                添加条件
              </button>
            </div>

            <div class="space-y-3">
              <div
                v-for="(cond, idx) in conditions"
                :key="idx"
                class="p-3 rounded-lg border border-dark-border bg-dark-bg space-y-2"
              >
                <!-- 行1：字段 + 操作符 -->
                <div class="flex gap-2">
                  <select v-model="cond.field" class="input flex-1 text-sm">
                    <option v-for="f in fieldOptions" :key="f.name" :value="f.name">{{ f.label }}</option>
                  </select>
                  <select v-model="cond.operator" class="input w-36 text-sm">
                    <option v-for="o in operatorOptions" :key="o.name" :value="o.name">{{ o.label }}</option>
                  </select>
                  <button
                    v-if="conditions.length > 1"
                    @click="removeCondition(idx)"
                    class="text-gray-500 hover:text-red-400 px-1.5 transition-colors shrink-0"
                    title="删除条件"
                  >
                    <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                    </svg>
                  </button>
                </div>

                <!-- 行2：字段名（Header/Cookie/QueryParam 时显示） -->
                <div v-if="needsFieldName(cond.field)" class="flex gap-2">
                  <input
                    v-model="cond.fieldName"
                    type="text"
                    class="input flex-1 text-sm"
                    :placeholder="cond.field === 'Header' ? '请求头名称，如 X-Real-IP' : cond.field === 'Cookie' ? 'Cookie 名称' : '参数名称'"
                  />
                </div>

                <!-- 行3：值 + 大小写 -->
                <div v-if="!noValueOperator(cond.operator)" class="flex gap-2 items-center">
                  <input
                    v-model="cond.value"
                    type="text"
                    class="input flex-1 text-sm"
                    placeholder="匹配值"
                  />
                  <label class="flex items-center gap-1.5 text-xs text-gray-500 shrink-0 cursor-pointer select-none">
                    <input type="checkbox" v-model="cond.ignoreCase" class="w-3.5 h-3.5 rounded border-gray-600 bg-dark-bg text-primary-500 focus:ring-primary-500/30" />
                    忽略大小写
                  </label>
                </div>

                <!-- 条件摘要 -->
                <div class="text-[11px] text-gray-500 pt-1 border-t border-dark-border/50">
                  <span class="text-gray-400">{{ getFieldLabel(cond.field) }}</span>
                  <template v-if="needsFieldName(cond.field) && cond.fieldName">
                    (<span class="text-blue-400">{{ cond.fieldName }}</span>)
                  </template>
                  <span class="mx-1 text-gray-600">{{ getOperatorLabel(cond.operator) }}</span>
                  <template v-if="!noValueOperator(cond.operator)">
                    <span class="text-yellow-400/80 font-mono">{{ cond.value || '...' }}</span>
                  </template>
                </div>
              </div>
            </div>
          </div>
        </div>

        <!-- 底部按钮 -->
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border shrink-0">
          <button @click="emit('close')" class="btn btn-secondary">取消</button>
          <button @click="save" :disabled="saving" class="btn btn-primary flex items-center gap-1.5">
            <span v-if="saving" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
            <span>{{ saving ? '保存中...' : '保存' }}</span>
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
