<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { advancedCcApi } from '@/api'
import CcRuleEditor from '@/components/cc/CcRuleEditor.vue'
import { useToast } from '@/composables/useToast'
import type { AdvancedCcRule, CcRuleType } from '@/types'

const { showSuccess, showError } = useToast()

// 枚举翻译
const ruleTypeLabels: Record<CcRuleType, string> = {
  FrequentAccess: '高频访问限制',
  FrequentAttack: '高频攻击限制',
  FrequentError: '高频错误限制',
}

// 数据
const loading = ref(false)
const rules = ref<AdvancedCcRule[]>([])

// 按类型分组的规则
const rulesByType = computed(() => {
  const grouped: Record<CcRuleType, AdvancedCcRule[]> = {
    FrequentAccess: [],
    FrequentAttack: [],
    FrequentError: [],
  }
  for (const rule of rules.value) {
    if (grouped[rule.type]) {
      grouped[rule.type].push(rule)
    }
  }
  return grouped
})

// 编辑器状态
const showEditor = ref(false)
const editingRule = ref<AdvancedCcRule | null>(null)

// 加载数据
const loadData = async () => {
  loading.value = true
  try {
    const response = await advancedCcApi.getRules()
    if (response.success) {
      rules.value = response.rules
    }
  } catch (error) {
    console.error('加载 CC 规则失败:', error)
    showError('加载数据失败')
  } finally {
    loading.value = false
  }
}

// 打开添加规则对话框
const openAddDialog = () => {
  editingRule.value = null
  showEditor.value = true
}

// 打开编辑规则对话框
const openEditDialog = (rule: AdvancedCcRule) => {
  editingRule.value = rule
  showEditor.value = true
}

// 关闭编辑器
const closeEditor = () => {
  showEditor.value = false
  editingRule.value = null
}

// 保存规则成功
const handleSaved = () => {
  loadData()
  showSuccess('规则已保存')
}

// 切换规则启用状态
const toggleRule = async (rule: AdvancedCcRule) => {
  try {
    const response = await advancedCcApi.toggleRule(rule.id)
    if (response.success) {
      rule.enabled = response.enabled
      showSuccess(response.enabled ? '已启用规则' : '已禁用规则')
    }
  } catch (error) {
    showError('操作失败')
  }
}

// 删除规则
const deleteRule = async (rule: AdvancedCcRule) => {
  if (!confirm(`确定要删除规则"${rule.name}"吗？`)) return
  
  try {
    const response = await advancedCcApi.removeRule(rule.id)
    if (response.success) {
      loadData()
      showSuccess('规则已删除')
    }
  } catch (error) {
    showError('删除失败')
  }
}

// 匹配目标翻译
const targetLabels: Record<string, string> = {
  UrlPath: '路径', FullUrl: 'URL', Method: '请求方法',
  ContentType: 'Content-Type', UserAgent: 'User-Agent',
  Referer: 'Referer', Header: '请求头', QueryParam: '查询参数',
  Cookie: 'Cookie', ClientIp: '客户端 IP', StatusCode: '状态码',
}

// 匹配操作翻译
const operatorLabels: Record<string, string> = {
  Equal: '等于', NotEqual: '不等于', Contains: '包含',
  NotContains: '不包含', StartsWith: '前缀为', EndsWith: '后缀为',
  Regex: '匹配正则', Exists: '存在', NotExists: '不存在',
}

// 动作翻译
const actionLabels: Record<string, string> = {
  Captcha: '人机验证', Block: '封禁', Reject: '拒绝',
  RateLimit: '限速', LogOnly: '仅记录',
}

// 格式化条件文本
const formatConditions = (rule: AdvancedCcRule): string => {
  if (!rule.conditions || rule.conditions.length === 0) return ''
  return rule.conditions.map(c => {
    const target = targetLabels[c.target] || c.target
    const op = operatorLabels[c.operator] || c.operator
    const vals = c.values.join(', ')
    return `${target}${op} [${vals}]`
  }).join('；')
}

// 格式化规则描述
const formatRuleDescription = (rule: AdvancedCcRule): string => {
  const actionLabel = actionLabels[rule.action] || rule.action
  const condText = formatConditions(rule)
  const base = `某 IP 在 ${rule.period} 秒内请求达到 ${rule.threshold} 次，${rule.actionSeconds} 秒内执行「${actionLabel}」`
  if (condText) {
    return `匹配条件：${condText}。${base}`
  }
  return base
}

// 自动刷新
let refreshTimer: number | null = null

onMounted(() => {
  loadData()
  refreshTimer = window.setInterval(loadData, 60000)
})

onUnmounted(() => {
  if (refreshTimer) clearInterval(refreshTimer)
})
</script>

<template>
  <div class="space-y-6">
    <!-- 各类型规则列表 -->
    <template v-for="type in Object.keys(rulesByType) as CcRuleType[]" :key="type">
      <div class="card">
        <div class="flex items-center justify-between mb-4">
          <h2 class="text-lg font-medium text-gray-100">{{ ruleTypeLabels[type] }}</h2>
          <button @click="openAddDialog" class="btn btn-sm btn-secondary flex items-center gap-1">
            添加规则 
            <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
            </svg>
          </button>
        </div>

        <div class="space-y-3">
          <!-- 规则列表 -->
          <div
            v-for="rule in rulesByType[type]"
            :key="rule.id"
            class="flex items-center justify-between p-4 bg-dark-card-hover rounded-lg"
          >
            <div class="flex items-center gap-4">
              <!-- 启用开关 -->
              <button
                @click="toggleRule(rule)"
                :class="[
                  'relative w-12 h-6 rounded-full transition-colors',
                  rule.enabled ? 'bg-primary-500' : 'bg-gray-600'
                ]"
              >
                <span
                  :class="[
                    'absolute top-1 w-4 h-4 rounded-full bg-white transition-transform',
                    rule.enabled ? 'left-7' : 'left-1'
                  ]"
                />
              </button>

              <div>
                <h3 class="font-medium text-gray-200">{{ rule.name }}</h3>
                <p class="text-sm text-gray-500 mt-1">{{ formatRuleDescription(rule) }}</p>
              </div>
            </div>

            <div class="flex items-center gap-3">
              <button
                @click="openEditDialog(rule)"
                class="text-sm text-primary-400 hover:text-primary-300"
              >
                编辑
              </button>
              <button
                @click="deleteRule(rule)"
                class="text-sm text-red-400 hover:text-red-300"
              >
                删除
              </button>
            </div>
          </div>

          <!-- 空状态 -->
          <div
            v-if="rulesByType[type].length === 0"
            class="text-center py-8 text-gray-500"
          >
            暂无 {{ ruleTypeLabels[type] }} 规则
          </div>
        </div>
      </div>
    </template>

    <!-- 规则编辑器 -->
    <CcRuleEditor
      :show="showEditor"
      :edit-rule="editingRule"
      @close="closeEditor"
      @saved="handleSaved"
    />
  </div>
</template>
