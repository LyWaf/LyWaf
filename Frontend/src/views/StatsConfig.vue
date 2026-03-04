<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { statisticApi, advancedCcApi } from '@/api'
import CcRuleEditor from '@/components/cc/CcRuleEditor.vue'
import { useToast } from '@/composables/useToast'
import type { AdvancedCcRule, CcRuleType } from '@/types'

const { showSuccess, showError } = useToast()

// ===== 白名单路径 =====
const whitePaths = ref<string[]>([])
const newWhitePath = ref('')
const whitePathLoading = ref(false)

// ===== 路径统计规则 =====
const pathStas = ref<string[]>([])
const newPathSta = ref('')
const pathStaLoading = ref(false)

// ===== CC 规则 =====
const ccRules = ref<AdvancedCcRule[]>([])
const ccLoading = ref(false)
const showEditor = ref(false)
const editingRule = ref<AdvancedCcRule | null>(null)

// CC 规则按类型分组
const rulesByType = computed(() => {
  const grouped: Record<CcRuleType, AdvancedCcRule[]> = {
    FrequentAccess: [],
    FrequentAttack: [],
    FrequentError: [],
  }
  for (const rule of ccRules.value) {
    if (grouped[rule.type]) {
      grouped[rule.type].push(rule)
    }
  }
  return grouped
})

const ruleTypeLabels: Record<CcRuleType, string> = {
  FrequentAccess: '高频访问限制',
  FrequentAttack: '高频攻击限制',
  FrequentError: '高频错误限制',
}

const actionLabels: Record<string, string> = {
  Captcha: '人机验证', Block: '封禁', Reject: '拒绝',
  RateLimit: '限速', LogOnly: '仅记录',
}

const targetLabels: Record<string, string> = {
  UrlPath: '路径', FullUrl: 'URL', Method: '请求方法',
  ContentType: 'Content-Type', UserAgent: 'User-Agent',
  Referer: 'Referer', Header: '请求头', QueryParam: '查询参数',
  Cookie: 'Cookie', ClientIp: '客户端 IP', StatusCode: '状态码',
}

const operatorLabels: Record<string, string> = {
  Equal: '等于', NotEqual: '不等于', Contains: '包含',
  NotContains: '不包含', StartsWith: '前缀为', EndsWith: '后缀为',
  Regex: '匹配正则', Exists: '存在', NotExists: '不存在',
}

const formatConditions = (rule: AdvancedCcRule): string => {
  if (!rule.conditions || rule.conditions.length === 0) return ''
  return rule.conditions.map(c => {
    const target = targetLabels[c.target] || c.target
    const op = operatorLabels[c.operator] || c.operator
    const vals = c.values.join(', ')
    return `${target}${op} [${vals}]`
  }).join('; ')
}

const formatRuleDesc = (rule: AdvancedCcRule): string => {
  const actionLabel = actionLabels[rule.action] || rule.action
  const condText = formatConditions(rule)
  const base = `${rule.period}s/${rule.threshold}次 -> ${actionLabel} ${rule.actionSeconds}s`
  return condText ? `${condText} | ${base}` : base
}

// ===== 加载数据 =====
const loadConfig = async () => {
  try {
    const res = await statisticApi.getConfig()
    if (res.success) {
      whitePaths.value = res.whitePaths || []
      pathStas.value = res.pathStas || []
    }
  } catch (e) {
    console.error('加载统计配置失败:', e)
  }
}

const loadCcRules = async () => {
  ccLoading.value = true
  try {
    const res = await advancedCcApi.getRules()
    if (res.success) {
      ccRules.value = res.rules
    }
  } catch (e) {
    console.error('加载 CC 规则失败:', e)
  } finally {
    ccLoading.value = false
  }
}

// ===== 白名单操作 =====
const addWhitePath = async () => {
  const path = newWhitePath.value.trim()
  if (!path) return
  whitePathLoading.value = true
  try {
    const res = await statisticApi.addWhitePath(path)
    if (res.success) {
      newWhitePath.value = ''
      await loadConfig()
      showSuccess('白名单路径已添加')
    } else {
      showError(res.message || '添加失败')
    }
  } catch (e) {
    showError('添加失败')
  } finally {
    whitePathLoading.value = false
  }
}

const removeWhitePath = async (path: string) => {
  try {
    const res = await statisticApi.removeWhitePath(path)
    if (res.success) {
      await loadConfig()
      showSuccess('已移除')
    } else {
      showError(res.message || '移除失败')
    }
  } catch (e) {
    showError('移除失败')
  }
}

// ===== PathStas 操作 =====
const addPathSta = async () => {
  const path = newPathSta.value.trim()
  if (!path) return
  pathStaLoading.value = true
  try {
    const res = await statisticApi.addPathSta(path)
    if (res.success) {
      newPathSta.value = ''
      await loadConfig()
      showSuccess('路径统计规则已添加')
    } else {
      showError(res.message || '添加失败')
    }
  } catch (e) {
    showError('添加失败')
  } finally {
    pathStaLoading.value = false
  }
}

const removePathSta = async (path: string) => {
  try {
    const res = await statisticApi.removePathSta(path)
    if (res.success) {
      await loadConfig()
      showSuccess('已移除')
    } else {
      showError(res.message || '移除失败')
    }
  } catch (e) {
    showError('移除失败')
  }
}

// ===== CC 规则操作 =====
const openAddCcRule = () => {
  editingRule.value = null
  showEditor.value = true
}

const openEditCcRule = (rule: AdvancedCcRule) => {
  editingRule.value = rule
  showEditor.value = true
}

const closeEditor = () => {
  showEditor.value = false
  editingRule.value = null
}

const handleCcSaved = () => {
  loadCcRules()
  showSuccess('规则已保存')
}

const toggleCcRule = async (rule: AdvancedCcRule) => {
  try {
    const res = await advancedCcApi.toggleRule(rule.id)
    if (res.success) {
      rule.enabled = res.enabled
      showSuccess(res.enabled ? '已启用' : '已禁用')
    }
  } catch (e) {
    showError('操作失败')
  }
}

const deleteCcRule = async (rule: AdvancedCcRule) => {
  if (!confirm(`确定要删除规则"${rule.name}"吗?`)) return
  try {
    const res = await advancedCcApi.removeRule(rule.id)
    if (res.success) {
      loadCcRules()
      showSuccess('已删除')
    }
  } catch (e) {
    showError('删除失败')
  }
}

// ===== 生命周期 =====
let refreshTimer: number | null = null

onMounted(() => {
  loadConfig()
  loadCcRules()
  refreshTimer = window.setInterval(() => {
    loadConfig()
    loadCcRules()
  }, 60000)
})

onUnmounted(() => {
  if (refreshTimer) clearInterval(refreshTimer)
})
</script>

<template>
  <div class="space-y-6">

    <!-- ========== Section 1: 白名单路径 ========== -->
    <div class="card">
      <div class="flex items-center justify-between mb-3">
        <div>
          <h2 class="text-lg font-medium text-gray-100">白名单路径</h2>
          <p class="text-sm text-gray-500 mt-1">白名单路径将跳过 WAF 和 CC 检测</p>
        </div>
      </div>

      <!-- 路径标签列表 -->
      <div class="flex flex-wrap gap-2 mb-4" v-if="whitePaths.length > 0">
        <span
          v-for="path in whitePaths"
          :key="path"
          class="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-md bg-primary-500/10 text-primary-400 text-sm border border-primary-500/20"
        >
          <code class="font-mono">{{ path }}</code>
          <button
            @click="removeWhitePath(path)"
            class="ml-0.5 text-primary-400/60 hover:text-red-400 transition-colors"
            title="移除"
          >
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12"/>
            </svg>
          </button>
        </span>
      </div>
      <div v-else class="text-gray-500 text-sm mb-4">暂无白名单路径</div>

      <!-- 添加输入 -->
      <div class="flex gap-2">
        <input
          v-model="newWhitePath"
          @keyup.enter="addWhitePath"
          type="text"
          placeholder="输入路径，例如 /health"
          class="flex-1 bg-dark-card-hover border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 placeholder-gray-500 focus:outline-none focus:border-primary-500"
        />
        <button
          @click="addWhitePath"
          :disabled="!newWhitePath.trim() || whitePathLoading"
          class="btn btn-sm btn-primary"
        >
          添加
        </button>
      </div>
    </div>

    <!-- ========== Section 2: 路径统计规则 ========== -->
    <div class="card">
      <div class="flex items-center justify-between mb-3">
        <div>
          <h2 class="text-lg font-medium text-gray-100">路径统计规则</h2>
          <p class="text-sm text-gray-500 mt-1">用于流量分析的路径匹配规则，支持 {'{**match}'} 通配符</p>
        </div>
      </div>

      <!-- 路径标签列表 -->
      <div class="flex flex-wrap gap-2 mb-4" v-if="pathStas.length > 0">
        <span
          v-for="path in pathStas"
          :key="path"
          class="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-md bg-blue-500/10 text-blue-400 text-sm border border-blue-500/20"
        >
          <code class="font-mono">{{ path }}</code>
          <button
            @click="removePathSta(path)"
            class="ml-0.5 text-blue-400/60 hover:text-red-400 transition-colors"
            title="移除"
          >
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12"/>
            </svg>
          </button>
        </span>
      </div>
      <div v-else class="text-gray-500 text-sm mb-4">暂无路径统计规则</div>

      <!-- 添加输入 -->
      <div class="flex gap-2">
        <input
          v-model="newPathSta"
          @keyup.enter="addPathSta"
          type="text"
          placeholder="输入路径，例如 /api/{**match}"
          class="flex-1 bg-dark-card-hover border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 placeholder-gray-500 focus:outline-none focus:border-primary-500"
        />
        <button
          @click="addPathSta"
          :disabled="!newPathSta.trim() || pathStaLoading"
          class="btn btn-sm btn-primary"
        >
          添加
        </button>
      </div>
    </div>

    <!-- ========== Section 3: CC 防护规则 ========== -->
    <div class="card">
      <div class="flex items-center justify-between mb-4">
        <div>
          <h2 class="text-lg font-medium text-gray-100">CC 防护规则</h2>
          <p class="text-sm text-gray-500 mt-1">基于频率检测的自动防护规则</p>
        </div>
        <button @click="openAddCcRule" class="btn btn-sm btn-secondary flex items-center gap-1">
          添加规则
          <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
          </svg>
        </button>
      </div>

      <!-- 按类型显示 -->
      <div class="space-y-4">
        <template v-for="type in (Object.keys(rulesByType) as CcRuleType[])" :key="type">
          <div v-if="rulesByType[type].length > 0">
            <h3 class="text-sm font-medium text-gray-400 mb-2">{{ ruleTypeLabels[type] }}</h3>
            <div class="space-y-2">
              <div
                v-for="rule in rulesByType[type]"
                :key="rule.id"
                class="flex items-center justify-between p-3 bg-dark-card-hover rounded-lg"
              >
                <div class="flex items-center gap-3 min-w-0">
                  <!-- 开关 -->
                  <button
                    @click="toggleCcRule(rule)"
                    :class="[
                      'relative flex-shrink-0 w-10 h-5 rounded-full transition-colors',
                      rule.enabled ? 'bg-primary-500' : 'bg-gray-600'
                    ]"
                  >
                    <span
                      :class="[
                        'absolute top-0.5 w-4 h-4 rounded-full bg-white transition-transform',
                        rule.enabled ? 'left-5' : 'left-0.5'
                      ]"
                    />
                  </button>

                  <div class="min-w-0">
                    <div class="flex items-center gap-2">
                      <span class="font-medium text-gray-200 text-sm">{{ rule.name }}</span>
                      <span class="text-xs px-1.5 py-0.5 rounded bg-gray-700 text-gray-400">
                        {{ actionLabels[rule.action] || rule.action }}
                      </span>
                    </div>
                    <p class="text-xs text-gray-500 mt-0.5 truncate">{{ formatRuleDesc(rule) }}</p>
                  </div>
                </div>

                <div class="flex items-center gap-2 flex-shrink-0">
                  <button @click="openEditCcRule(rule)" class="text-xs text-primary-400 hover:text-primary-300">
                    编辑
                  </button>
                  <button @click="deleteCcRule(rule)" class="text-xs text-red-400 hover:text-red-300">
                    删除
                  </button>
                </div>
              </div>
            </div>
          </div>
        </template>

        <div
          v-if="ccRules.length === 0 && !ccLoading"
          class="text-center py-8 text-gray-500"
        >
          暂无 CC 防护规则
        </div>
      </div>
    </div>

    <!-- CC 规则编辑器 -->
    <CcRuleEditor
      :show="showEditor"
      :edit-rule="editingRule"
      @close="closeEditor"
      @saved="handleCcSaved"
    />
  </div>
</template>
