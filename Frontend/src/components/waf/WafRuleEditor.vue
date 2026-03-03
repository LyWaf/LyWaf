<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { wafRuleApi } from '@/api'
import type { WafCustomRule, WafCondition, WafConditionGroup, WafMatchField, WafMatchOperator, WafRuleAction } from '@/types'

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
const fieldLabels: Record<WafMatchField, string> = {
  UriPath: 'URL 路径',
  FullUrl: '完整 URL',
  QueryString: '查询字符串',
  Method: 'HTTP 方法',
  ClientIp: '客户端 IP',
  XForwardedFor: 'X-Forwarded-For',
  UserAgent: 'User-Agent',
  Referer: 'Referer',
  ContentType: 'Content-Type',
  ContentLength: 'Content-Length',
  Cookie: 'Cookie',
  Header: '请求头',
  QueryParam: '查询参数',
  Body: '请求体',
  ServerPort: '服务端口',
}

const operatorLabels: Record<WafMatchOperator, string> = {
  Equal: '等于',
  NotEqual: '不等于',
  Contains: '包含',
  NotContains: '不包含',
  StartsWith: '前缀匹配',
  EndsWith: '后缀匹配',
  Regex: '正则匹配',
  Exists: '存在',
  NotExists: '不存在',
  LengthGreaterThan: '长度大于',
  LengthLessThan: '长度小于',
}

const actionLabels: Record<WafRuleAction, string> = {
  Observe: '观察（仅记录）',
  Block: '封禁 IP',
  Reject: '拦截（返回错误码）',
  Captcha: '人机验证',
}

const actionDescriptions: Record<WafRuleAction, string> = {
  Observe: '仅记录日志，不拦截请求',
  Block: '将客户端 IP 封禁指定时间',
  Reject: '立即返回指定 HTTP 状态码',
  Captcha: '要求客户端完成人机验证',
}

// 需要 fieldName 的字段
const needsFieldName = (field: WafMatchField) =>
  ['Cookie', 'Header', 'QueryParam'].includes(field)

// 不需要匹配值的操作符
const noValueOperators: WafMatchOperator[] = ['Exists', 'NotExists']

// 枚举键列表
const matchFields = Object.keys(fieldLabels) as WafMatchField[]
const matchOperators = Object.keys(operatorLabels) as WafMatchOperator[]
const ruleActions = Object.keys(actionLabels) as WafRuleAction[]

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
  return map[field] || '字段名'
}

// 表单数据
const form = ref({
  name: '',
  description: '',
  enabled: true,
  priority: 100,
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
  if (readOnly.value) return '查看规则'
  return isEditMode.value ? '编辑规则' : '添加规则'
})

// 创建默认条件
const createDefaultCondition = (): WafCondition => ({
  field: 'UriPath',
  operator: 'Contains',
  value: '',
  ignoreCase: true,
})

// 重置
const resetForm = () => {
  form.value = {
    name: '',
    description: '',
    enabled: true,
    priority: 100,
    conditionGroups: [{ conditions: [createDefaultCondition()] }],
    action: 'Observe',
    actionSeconds: 600,
    responseCode: 403,
  }
}

// 监听编辑规则
watch(() => props.editRule, (rule) => {
  if (rule) {
    form.value = {
      name: rule.name,
      description: rule.description,
      enabled: rule.enabled,
      priority: rule.priority,
      conditionGroups: JSON.parse(JSON.stringify(rule.conditionGroups)),
      action: rule.action,
      actionSeconds: rule.actionSeconds,
      responseCode: rule.responseCode,
    }
    // 确保至少有一个条件组
    if (form.value.conditionGroups.length === 0) {
      form.value.conditionGroups.push({ conditions: [createDefaultCondition()] })
    }
  } else {
    resetForm()
  }
}, { immediate: true })

watch(() => props.show, (show) => {
  if (!show) resetForm()
})

// ========== 条件组操作 ==========

// 添加 OR 条件组
const addGroup = () => {
  form.value.conditionGroups.push({ conditions: [createDefaultCondition()] })
}

// 删除条件组
const removeGroup = (groupIndex: number) => {
  form.value.conditionGroups.splice(groupIndex, 1)
}

// 在组内添加 AND 条件
const addConditionToGroup = (groupIndex: number) => {
  form.value.conditionGroups[groupIndex].conditions.push(createDefaultCondition())
}

// 删除组内条件
const removeConditionFromGroup = (groupIndex: number, condIndex: number) => {
  const group = form.value.conditionGroups[groupIndex]
  group.conditions.splice(condIndex, 1)
  // 组内条件清空 → 删除整个组
  if (group.conditions.length === 0) {
    form.value.conditionGroups.splice(groupIndex, 1)
  }
}

// ========== 提交 ==========

const saving = ref(false)
const saveError = ref('')

const handleSubmit = async () => {
  if (!form.value.name.trim()) {
    saveError.value = '请输入规则名称'
    return
  }
  if (form.value.conditionGroups.length === 0 ||
      form.value.conditionGroups.every(g => g.conditions.length === 0)) {
    saveError.value = '请至少添加一个匹配条件'
    return
  }
  // 检查条件值
  for (const group of form.value.conditionGroups) {
    for (const c of group.conditions) {
      if (!noValueOperators.includes(c.operator) && !c.value.trim()) {
        saveError.value = '请填写所有条件的匹配值'
        return
      }
      if (needsFieldName(c.field) && !c.fieldName?.trim()) {
        saveError.value = '请填写指定字段的名称'
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
              <label class="block text-sm text-gray-400 mb-1">规则名称 <span v-if="!readOnly" class="text-red-400">*</span></label>
              <input v-model="form.name" type="text" class="input w-full" placeholder="如：拦截恶意爬虫" :disabled="readOnly" />
            </div>
            <div class="col-span-2 sm:col-span-1">
              <label class="block text-sm text-gray-400 mb-1">优先级</label>
              <input v-model.number="form.priority" type="number" min="1" max="9999" class="input w-full" :disabled="readOnly" />
              <p class="text-xs text-gray-600 mt-1">数值越小优先级越高</p>
            </div>
            <div class="col-span-2">
              <label class="block text-sm text-gray-400 mb-1">描述</label>
              <input v-model="form.description" type="text" class="input w-full" placeholder="可选，描述规则用途" :disabled="readOnly" />
            </div>
          </div>

          <!-- 匹配条件（分组） -->
          <div>
            <h3 class="text-sm font-medium text-gray-300 mb-3">
              匹配条件
              <span class="text-xs text-gray-500 ml-1">组内 AND（全部满足），组间 OR（任一匹配）</span>
            </h3>

            <div class="space-y-3">
              <!-- 每个条件组 -->
              <template v-for="(group, gIdx) in form.conditionGroups" :key="gIdx">
                <!-- OR 分隔符 -->
                <div v-if="gIdx > 0" class="flex items-center gap-3 py-1">
                  <div class="flex-1 border-t border-orange-500/30"></div>
                  <span class="text-xs font-bold text-orange-400 px-3 py-1 bg-orange-500/10 rounded-full">OR</span>
                  <div class="flex-1 border-t border-orange-500/30"></div>
                </div>

                <!-- 条件组卡片 -->
                <div class="bg-dark-card-hover rounded-lg border border-dark-border p-4 space-y-3">
                  <!-- 组标题 -->
                  <div class="flex items-center justify-between">
                    <span class="text-xs text-gray-500">条件组 {{ gIdx + 1 }}</span>
                    <button v-if="!readOnly" @click="removeGroup(gIdx)"
                      class="text-xs text-gray-500 hover:text-red-400 transition-colors">
                      删除此组
                    </button>
                  </div>

                  <!-- 组内条件列表 -->
                  <!-- 列标题（仅一次） -->
                  <div v-if="group.conditions.length > 0" class="flex items-center gap-2 text-xs text-gray-500 pl-10">
                    <div class="w-[130px]">匹配字段</div>
                    <div class="w-[110px]">匹配方式</div>
                    <div class="flex-1">匹配值</div>
                  </div>

                  <div v-for="(condition, cIdx) in group.conditions" :key="cIdx" class="space-y-2">
                    <!-- AND 分隔 -->
                    <div v-if="cIdx > 0" class="text-xs text-primary-400 font-medium py-0.5 pl-10">AND</div>

                    <div class="flex items-center gap-2">
                      <!-- 删除条件（红色减号，放在最前面） -->
                      <button v-if="!readOnly" type="button" @click="removeConditionFromGroup(gIdx, cIdx)"
                        class="flex-shrink-0 w-7 h-7 flex items-center justify-center rounded-full border border-red-500/40 text-red-400 hover:bg-red-500/20 hover:border-red-400 transition-colors relative z-10"
                        title="删除此条件">
                        <svg class="w-4 h-4" fill="none" stroke="currentColor" stroke-width="2.5" viewBox="0 0 24 24">
                          <path stroke-linecap="round" d="M5 12h14" />
                        </svg>
                      </button>

                      <!-- 匹配字段 -->
                      <div class="flex-shrink-0 w-[130px]">
                        <select v-model="condition.field" class="input w-full text-sm" :disabled="readOnly">
                          <option v-for="f in matchFields" :key="f" :value="f">{{ fieldLabels[f] }}</option>
                        </select>
                      </div>

                      <!-- 字段名（仅 Cookie/Header/QueryParam） -->
                      <div v-if="needsFieldName(condition.field)" class="flex-shrink-0 w-[100px]">
                        <input v-model="condition.fieldName" type="text" class="input w-full text-sm"
                          :placeholder="getFieldNamePlaceholder(condition.field)" :disabled="readOnly" />
                      </div>

                      <!-- 操作符 -->
                      <div class="flex-shrink-0 w-[110px]">
                        <select v-model="condition.operator" class="input w-full text-sm" :disabled="readOnly">
                          <option v-for="op in matchOperators" :key="op" :value="op">{{ operatorLabels[op] }}</option>
                        </select>
                      </div>

                      <!-- 匹配值 -->
                      <div class="flex-1" v-if="!noValueOperators.includes(condition.operator)">
                        <input v-model="condition.value" type="text" class="input w-full text-sm"
                          :placeholder="getFieldPlaceholder(condition.field)" :disabled="readOnly" />
                      </div>

                      <!-- 忽略大小写 -->
                      <label class="flex-shrink-0 flex items-center gap-1 text-xs text-gray-500 whitespace-nowrap"
                        :class="readOnly ? '' : 'cursor-pointer hover:text-gray-300'">
                        <input type="checkbox" v-model="condition.ignoreCase"
                          class="w-3.5 h-3.5 rounded border-gray-600 text-primary-500 focus:ring-primary-500/30" :disabled="readOnly" />
                        忽略大小写
                      </label>
                    </div>
                  </div>

                  <!-- 添加 AND 条件 -->
                  <button v-if="!readOnly" @click="addConditionToGroup(gIdx)"
                    class="flex items-center gap-1.5 px-3 py-1.5 text-xs text-primary-400 border border-primary-500/30 rounded hover:bg-primary-500/10 transition-colors">
                    <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
                    </svg>
                    添加 AND 条件
                  </button>
                </div>
              </template>

              <!-- 空状态 -->
              <div v-if="form.conditionGroups.length === 0"
                class="text-center py-6 text-gray-600 text-sm bg-dark-card-hover rounded-lg border border-dark-border">
                {{ readOnly ? '无匹配条件' : '暂无条件，点击下方按钮添加' }}
              </div>

              <!-- 添加 OR 条件组 -->
              <button v-if="!readOnly" @click="addGroup"
                class="flex items-center gap-2 w-full px-4 py-2.5 text-sm text-orange-400 border border-dashed border-orange-500/40 rounded-lg hover:bg-orange-500/5 transition-colors justify-center">
                <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
                </svg>
                {{ form.conditionGroups.length === 0 ? '添加条件组' : '添加 OR 条件组' }}
              </button>
            </div>
          </div>

          <!-- 执行动作 -->
          <div>
            <h3 class="text-sm font-medium text-gray-300 mb-3">执行动作</h3>
            <div class="grid grid-cols-2 gap-4">
              <div>
                <label class="block text-sm text-gray-400 mb-1">动作类型</label>
                <select v-model="form.action" class="input w-full" :disabled="readOnly">
                  <option v-for="a in ruleActions" :key="a" :value="a">{{ actionLabels[a] }}</option>
                </select>
                <p class="text-xs text-gray-600 mt-1">{{ actionDescriptions[form.action] }}</p>
              </div>

              <!-- Block / Captcha 持续时间 -->
              <div v-if="form.action === 'Block' || form.action === 'Captcha'">
                <label class="block text-sm text-gray-400 mb-1">持续时间</label>
                <div class="flex items-center gap-2">
                  <input v-model.number="form.actionSeconds" type="number" min="1" class="input flex-1" :disabled="readOnly" />
                  <span class="text-sm text-gray-500">秒</span>
                </div>
              </div>

              <!-- Reject 响应码 -->
              <div v-if="form.action === 'Reject'">
                <label class="block text-sm text-gray-400 mb-1">响应状态码</label>
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
            {{ readOnly ? '关闭' : '取消' }}
          </button>
          <button v-if="!readOnly" @click="handleSubmit" :disabled="saving"
            class="btn btn-sm btn-primary flex items-center gap-1.5">
            <span v-if="saving" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
            <span>{{ saving ? '保存中...' : '保存' }}</span>
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
