<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { advancedCcApi, pluginApi, statisticApi } from '@/api'
import CcRuleEditor from '@/components/cc/CcRuleEditor.vue'
import { useToast } from '@/composables/useToast'
import type { AdvancedCcRule, CcRuleType } from '@/types'

const { showSuccess, showError } = useToast()

// CC 分析插件开关
const analysisEnabled = ref(true)
const analysisToggling = ref(false)

const loadAnalysisState = async () => {
  try {
    const res = await pluginApi.getPlugins()
    if (res.success) {
      const plugin = res.plugins.find((p: any) => p.id === 'analysis')
      if (plugin) analysisEnabled.value = plugin.isEnabled
    }
  } catch {}
}

const toggleAnalysis = async () => {
  analysisToggling.value = true
  try {
    const res = await pluginApi.togglePlugin('analysis')
    if (res.success) {
      analysisEnabled.value = res.isEnabled
      showSuccess(res.isEnabled ? '已开启 CC 分析' : '已关闭 CC 分析')
    }
  } catch {
    showError('操作失败')
  } finally {
    analysisToggling.value = false
  }
}

// ===== 白名单路径 =====
const whitePaths = ref<string[]>([])
const newWhitePath = ref('')
const whitePathLoading = ref(false)

// ===== 路径统计规则 =====
const pathStas = ref<string[]>([])
const newPathSta = ref('')
const pathStaLoading = ref(false)

const loadStatsConfig = async () => {
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

// 白名单操作
const addWhitePath = async () => {
  const path = newWhitePath.value.trim()
  if (!path) return
  whitePathLoading.value = true
  try {
    const res = await statisticApi.addWhitePath(path)
    if (res.success) {
      newWhitePath.value = ''
      await loadStatsConfig()
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
      await loadStatsConfig()
      showSuccess('已移除')
    } else {
      showError(res.message || '移除失败')
    }
  } catch (e) {
    showError('移除失败')
  }
}

// PathStas 操作
const addPathSta = async () => {
  const path = newPathSta.value.trim()
  if (!path) return
  pathStaLoading.value = true
  try {
    const res = await statisticApi.addPathSta(path)
    if (res.success) {
      newPathSta.value = ''
      await loadStatsConfig()
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
      await loadStatsConfig()
      showSuccess('已移除')
    } else {
      showError(res.message || '移除失败')
    }
  } catch (e) {
    showError('移除失败')
  }
}

// 枚举翻译
const ruleTypeLabels: Record<CcRuleType, string> = {
  FrequentAccess: '高频访问限制',
  FrequentAttack: '高频攻击限制',
  FrequentError: '高频错误限制',
}

// 数据
const loading = ref(false)
const rules = ref<AdvancedCcRule[]>([])

// 规则类型样式
const ruleTypeColors: Record<CcRuleType, string> = {
  FrequentAccess: 'bg-blue-500/15 text-blue-400 border-blue-500/30',
  FrequentAttack: 'bg-red-500/15 text-red-400 border-red-500/30',
  FrequentError: 'bg-yellow-500/15 text-yellow-400 border-yellow-500/30',
}

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
  loadAnalysisState()
  loadStatsConfig()
  refreshTimer = window.setInterval(() => {
    loadData()
    loadStatsConfig()
  }, 60000)
})

onUnmounted(() => {
  if (refreshTimer) clearInterval(refreshTimer)
})
</script>

<template>
  <div class="space-y-6">
    <!-- CC 分析开关 -->
    <div class="card">
      <div class="flex items-center justify-between">
        <div>
          <h2 class="text-lg font-medium text-gray-100">CC 流量分析</h2>
          <p class="text-sm text-gray-500 mt-1">开启后将在后台持续分析流量，自动检测 CC 攻击行为</p>
        </div>
        <button
          @click="toggleAnalysis"
          :disabled="analysisToggling"
          :class="[
            'relative w-12 h-6 rounded-full transition-colors flex-shrink-0',
            analysisEnabled ? 'bg-primary-500' : 'bg-gray-600'
          ]"
        >
          <span
            :class="[
              'absolute top-1 w-4 h-4 rounded-full bg-white transition-transform',
              analysisEnabled ? 'left-7' : 'left-1'
            ]"
          />
        </button>
      </div>
    </div>

    <!-- CC 防护规则 -->
    <div class="card">
      <div class="flex items-center justify-between mb-4">
        <h2 class="text-lg font-medium text-gray-100">CC 防护规则</h2>
        <button @click="openAddDialog" class="btn btn-sm btn-primary flex items-center gap-1.5">
          <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
          </svg>
          添加规则
        </button>
      </div>

      <div class="space-y-3">
        <!-- 规则列表 -->
        <div
          v-for="rule in rules"
          :key="rule.id"
          class="flex items-center justify-between p-4 bg-dark-card-hover rounded-lg"
        >
          <div class="flex items-center gap-4">
            <!-- 启用开关 -->
            <button
              @click="toggleRule(rule)"
              :class="[
                'relative w-12 h-6 rounded-full transition-colors flex-shrink-0',
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
              <div class="flex items-center gap-2">
                <h3 class="font-medium text-gray-200">{{ rule.name }}</h3>
                <span :class="['px-2 py-0.5 rounded text-xs border', ruleTypeColors[rule.type]]">
                  {{ ruleTypeLabels[rule.type] }}
                </span>
              </div>
              <p class="text-sm text-gray-500 mt-1">{{ formatRuleDescription(rule) }}</p>
            </div>
          </div>

          <div class="flex items-center gap-3 flex-shrink-0">
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
          v-if="rules.length === 0"
          class="text-center py-8 text-gray-500"
        >
          暂无 CC 防护规则
        </div>
      </div>
    </div>

    <!-- 白名单路径 -->
    <div class="card">
      <div class="flex items-center justify-between mb-3">
        <div>
          <h2 class="text-lg font-medium text-gray-100">白名单路径</h2>
          <p class="text-sm text-gray-500 mt-1">白名单路径将跳过 WAF 和 CC 检测</p>
        </div>
      </div>

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

    <!-- 路径统计规则 -->
    <div class="card">
      <div class="flex items-center justify-between mb-3">
        <div>
          <h2 class="text-lg font-medium text-gray-100">路径统计规则</h2>
          <p class="text-sm text-gray-500 mt-1">用于流量分析的路径匹配规则，支持 {'{**match}'} 通配符</p>
        </div>
      </div>

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

    <!-- 规则编辑器 -->
    <CcRuleEditor
      :show="showEditor"
      :edit-rule="editingRule"
      @close="closeEditor"
      @saved="handleSaved"
    />
  </div>
</template>
