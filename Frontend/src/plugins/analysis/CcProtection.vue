<script setup lang="ts">
import { locale } from '@/i18n'
import { t } from '@/plugins/analysis/i18n'
import { computed, ref, onMounted, onUnmounted } from 'vue'
import { refreshPluginMenus } from '@/composables/usePluginMenus'
import { pluginApi, pluginProxyApi } from '@/api'
import { advancedCcApi, statisticApi } from './api'
import ListToolbar from '@/components/common/ListToolbar.vue'
import CcRuleEditor from '@/components/cc/CcRuleEditor.vue'
import { useToast } from '@/composables/useToast'
import type { AdvancedCcRule, CcRuleType } from '@/types'

const { showSuccess, showError } = useToast()
const banLogs = ref<{ id: number; time: string; ip: string; reason: string; durationSeconds: number }[]>([])
const logError = ref('')
const logsLoading = ref(false)
const loadBanLogs = async () => {
  if (logsLoading.value) return
  logsLoading.value = true
  try {
    const result = await pluginProxyApi('analysis').get<{ success: boolean; logs: typeof banLogs.value }>('ban-logs')
    if (result.success) { banLogs.value = result.logs; logError.value = '' }
  } catch { logError.value = t('page.9dc24b0e828f') }
  finally { logsLoading.value = false }
}
let logTimer: ReturnType<typeof setInterval> | undefined
onMounted(() => { void loadBanLogs(); logTimer = setInterval(loadBanLogs, 10000) })
onUnmounted(() => clearInterval(logTimer))

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
      await refreshPluginMenus()
      showSuccess(res.isEnabled ? t('page.5847b4547e31') : t('page.f95ccc399797'))
    }
  } catch {
    showError(t('page.0c3b4cf7aa25'))
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
    console.error(t('page.612d58d1a266'), e)
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
      showSuccess(t('page.919791c13c68'))
    } else {
      showError(res.message || t('page.2ca15f1e2bb7'))
    }
  } catch (e) {
    showError(t('page.2ca15f1e2bb7'))
  } finally {
    whitePathLoading.value = false
  }
}

const removeWhitePath = async (path: string) => {
  try {
    const res = await statisticApi.removeWhitePath(path)
    if (res.success) {
      await loadStatsConfig()
      showSuccess(t('page.174688c755b9'))
    } else {
      showError(res.message || t('page.4757fc1d4709'))
    }
  } catch (e) {
    showError(t('page.4757fc1d4709'))
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
      showSuccess(t('page.0a114f5f3bbc'))
    } else {
      showError(res.message || t('page.2ca15f1e2bb7'))
    }
  } catch (e) {
    showError(t('page.2ca15f1e2bb7'))
  } finally {
    pathStaLoading.value = false
  }
}

const removePathSta = async (path: string) => {
  try {
    const res = await statisticApi.removePathSta(path)
    if (res.success) {
      await loadStatsConfig()
      showSuccess(t('page.174688c755b9'))
    } else {
      showError(res.message || t('page.4757fc1d4709'))
    }
  } catch (e) {
    showError(t('page.4757fc1d4709'))
  }
}

// 枚举翻译
const ruleTypeLabels = computed<Record<CcRuleType, string>>(() => ({
  FrequentAccess: t('page.38d9f66567c0'),
  FrequentAttack: t('page.b955b87d932c'),
  FrequentError: t('page.1eb791fb8b9d'),
}))

// 数据
const loading = ref(false)
const rules = ref<AdvancedCcRule[]>([])
const page = ref(1)
const total = ref(0)
const search = ref('')
let listRequest = 0

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
  const request = ++listRequest
  loading.value = true
  try {
    const response = await advancedCcApi.list(page.value, search.value)
    if (request === listRequest && response.success) {
      rules.value = response.data
      total.value = response.total
    }
  } catch (error) {
    if (request === listRequest) showError(t('page.d1abfa54a4ac'))
  } finally {
    if (request === listRequest) loading.value = false
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
  showSuccess(t('page.c9105d640ece'))
}

// 切换规则启用状态
const toggleRule = async (rule: AdvancedCcRule) => {
  try {
    const response = await advancedCcApi.toggleRule(rule.id)
    if (response.success) {
      rule.enabled = response.enabled
      showSuccess(response.enabled ? t('page.0497be5a2ca1') : t('page.045e0be12313'))
    }
  } catch (error) {
    showError(t('page.0c3b4cf7aa25'))
  }
}

// 删除规则
const deleteRule = async (rule: AdvancedCcRule) => {
  if (!confirm(t('page.e0dd7146864b', { p0: rule.name }))) return

  try {
    const response = await advancedCcApi.removeRule(rule.id)
    if (response.success) {
      loadData()
      showSuccess(t('page.04e366e33981'))
    }
  } catch (error) {
    showError(t('page.c228558cf257'))
  }
}

// 匹配目标翻译
const targetLabels = computed<Record<string, string>>(() => ({
  UrlPath: t('page.77e1ea5c5688'), FullUrl: 'URL', Method: t('page.cf206666a6a5'),
  ContentType: 'Content-Type', UserAgent: 'User-Agent',
  Referer: 'Referer', Header: t('page.cea1aafe9416'), QueryParam: t('page.1904a09fab00'),
  Cookie: 'Cookie', ClientIp: t('page.2d1cb6792f12'), StatusCode: t('page.06f7d7341212'),
}))

// 匹配操作翻译
const operatorLabels = computed<Record<string, string>>(() => ({
  Equal: t('page.4bc69a0562ff'), NotEqual: t('page.9678e3a7cb3c'), Contains: t('page.107319b8b39d'),
  NotContains: t('page.5f60ffcf600a'), StartsWith: t('page.b35e50b3873d'), EndsWith: t('page.637f4e5ba011'),
  Regex: t('page.ad550e963af3'), Exists: t('page.4a558371f938'), NotExists: t('page.4e51b67dd32c'),
}))

// 动作翻译
const actionLabels = computed<Record<string, string>>(() => ({
  Captcha: t('page.36fbc1da5545'), Block: t('page.6f93ba4c79f8'), Reject: t('page.136de7a8c46f'),
  RateLimit: t('page.106a487eeea5'), LogOnly: t('page.c4ba4276172a'),
}))

// 格式化条件文本
const formatConditions = (rule: AdvancedCcRule): string => {
  if (rule.conditionGroups?.length) return rule.conditionGroups.map(g => '(' + g.conditions.map(c =>
    `${targetLabels.value[c.target] || c.target}${operatorLabels.value[c.operator] || c.operator} [${c.values.join(', ')}]`
  ).join(g.operator === 'And' ? t('page.a22bdbe928cd') : t('page.60ab743988bb')) + ')').join(rule.groupOperator === 'And' ? t('page.a22bdbe928cd') : t('page.60ab743988bb'))
  if (!rule.conditions || rule.conditions.length === 0) return ''
  return rule.conditions.map(c => {
    const target = targetLabels.value[c.target] || c.target
    const op = operatorLabels.value[c.operator] || c.operator
    const vals = c.values.join(', ')
    return `${target}${op} [${vals}]`
  }).join('；')
}

// 格式化规则描述
const formatRuleDescription = (rule: AdvancedCcRule): string => {
  const actionLabel = actionLabels.value[rule.action] || rule.action
  const condText = formatConditions(rule)
  const base = t('page.f51ec17aa469', { p0: rule.period, p1: rule.threshold, p2: rule.actionSeconds, p3: actionLabel })
  if (condText) {
    return t('page.1022baaeca1a', { p0: condText, p1: base })
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
          <h2 class="text-lg font-medium text-gray-100">{{ t('page.4aedc48c69ca') }}</h2>
          <p class="text-sm text-gray-500 mt-1">{{ t('page.dc38fecbb50d') }}</p>
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
        <h2 class="text-lg font-medium text-gray-100">{{ t('page.54ad621bd04c') }}</h2>
        <button @click="openAddDialog" class="btn btn-sm btn-primary flex items-center gap-1.5">
          <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
          </svg>
          {{ t('page.c9f9e18b0623') }}
        </button>
      </div>

      <div class="space-y-3">
        <!-- 规则列表 -->
        <ListToolbar v-model:page="page" v-model:search="search" :total="total" :busy="loading" @refresh="loadData" />
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
              {{ t('page.051836569928') }}
            </button>
            <button
              @click="deleteRule(rule)"
              class="text-sm text-red-400 hover:text-red-300"
            >
              {{ t('page.2f9daa828907') }}
            </button>
          </div>
        </div>

        <!-- 空状态 -->
        <div
          v-if="rules.length === 0"
          class="text-center py-8 text-gray-500"
        >
          {{ t('page.a3d955c29e10') }}
        </div>
      </div>
    </div>

    <!-- 白名单路径 -->
    <div class="card">
      <div class="flex items-center justify-between mb-3">
        <div>
          <h2 class="text-lg font-medium text-gray-100">{{ t('page.e15c3f4cac20') }}</h2>
          <p class="text-sm text-gray-500 mt-1">{{ t('page.604a94afc8dd') }}</p>
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
            :title="t('page.6135d4159e89')"
          >
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12"/>
            </svg>
          </button>
        </span>
      </div>
      <div v-else class="text-gray-500 text-sm mb-4">{{ t('page.4dfca1441dcc') }}</div>

      <div class="flex gap-2">
        <input
          v-model="newWhitePath"
          @keyup.enter="addWhitePath"
          type="text"
          :placeholder="t('page.d836ba330759')"
          class="flex-1 bg-dark-card-hover border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 placeholder-gray-500 focus:outline-none focus:border-primary-500"
        />
        <button
          @click="addWhitePath"
          :disabled="!newWhitePath.trim() || whitePathLoading"
          class="btn btn-sm btn-primary"
        >
          {{ t('page.7a8a11ead507') }}
        </button>
      </div>
    </div>

    <!-- 路径统计规则 -->
    <div class="card">
      <div class="flex items-center justify-between mb-3">
        <div>
          <h2 class="text-lg font-medium text-gray-100">{{ t('page.cae86dc7ee54') }}</h2>
          <p class="text-sm text-gray-500 mt-1">{{ t('page.ab5d8fff96d4') }}</p>
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
            :title="t('page.6135d4159e89')"
          >
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12"/>
            </svg>
          </button>
        </span>
      </div>
      <div v-else class="text-gray-500 text-sm mb-4">{{ t('page.9aad63a0ab8d') }}</div>

      <div class="flex gap-2">
        <input
          v-model="newPathSta"
          @keyup.enter="addPathSta"
          type="text"
          :placeholder="t('page.8138ac06bd82')"
          class="flex-1 bg-dark-card-hover border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 placeholder-gray-500 focus:outline-none focus:border-primary-500"
        />
        <button
          @click="addPathSta"
          :disabled="!newPathSta.trim() || pathStaLoading"
          class="btn btn-sm btn-primary"
        >
          {{ t('page.7a8a11ead507') }}
        </button>
      </div>
    </div>

    <!-- 规则编辑器 -->
    <section class="border-t border-dark-border pt-6">
      <div class="flex items-center justify-between mb-4">
        <h2 class="text-lg font-semibold text-gray-100">{{ t('page.973461c40252') }}{{ banLogs.length }}/1000)</h2>
        <button class="btn btn-sm btn-secondary" :disabled="logsLoading" @click="loadBanLogs">{{ t('page.aee887434131') }}</button>
      </div>
      <p v-if="logError" role="alert" class="text-red-400 mb-3">{{ logError }}</p>
      <div class="max-h-96 overflow-auto border border-dark-border rounded-md" tabindex="0" :aria-label="t('page.ba88b90f2f6a')">
        <table class="w-full text-sm text-left">
          <thead class="sticky top-0 bg-dark-card text-gray-400"><tr>
            <th class="p-3 whitespace-nowrap">{{ t('page.8b6ff498515b') }}</th><th class="p-3">IP</th><th class="p-3">{{ t('page.39a7481a7d30') }}</th><th class="p-3 whitespace-nowrap">{{ t('page.efaa6864377d') }}</th>
          </tr></thead>
          <tbody><tr v-for="log in banLogs" :key="log.id" class="border-t border-dark-border">
            <td class="p-3 whitespace-nowrap">{{ new Date(log.time).toLocaleString(locale) }}</td>
            <td class="p-3 font-mono">{{ log.ip }}</td>
            <td class="p-3 break-all min-w-48">{{ log.reason }}</td><td class="p-3">{{ log.durationSeconds }}</td>
          </tr><tr v-if="!banLogs.length"><td colspan="4" class="p-8 text-center text-gray-500">{{ t('page.93ede994e535') }}</td></tr></tbody>
        </table>
      </div>
    </section>

    <CcRuleEditor
      :show="showEditor"
      :edit-rule="editingRule"
      @close="closeEditor"
      @saved="handleSaved"
    />
  </div>
</template>
