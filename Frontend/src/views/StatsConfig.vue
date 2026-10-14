<script setup lang="ts">
import { t } from '@/plugins/analysis/i18n'
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { statisticApi, advancedCcApi } from '@/plugins/analysis/api'
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

const ruleTypeLabels = computed<Record<CcRuleType, string>>(() => ({
  FrequentAccess: t('page.38d9f66567c0'),
  FrequentAttack: t('page.b955b87d932c'),
  FrequentError: t('page.1eb791fb8b9d'),
}))

const actionLabels = computed<Record<string, string>>(() => ({
  Captcha: t('page.36fbc1da5545'), Block: t('page.6f93ba4c79f8'), Reject: t('page.136de7a8c46f'),
  RateLimit: t('page.106a487eeea5'), LogOnly: t('page.c4ba4276172a'),
}))

const targetLabels = computed<Record<string, string>>(() => ({
  UrlPath: t('page.77e1ea5c5688'), FullUrl: 'URL', Method: t('page.cf206666a6a5'),
  ContentType: 'Content-Type', UserAgent: 'User-Agent',
  Referer: 'Referer', Header: t('page.cea1aafe9416'), QueryParam: t('page.1904a09fab00'),
  Cookie: 'Cookie', ClientIp: t('page.2d1cb6792f12'), StatusCode: t('page.06f7d7341212'),
}))

const operatorLabels = computed<Record<string, string>>(() => ({
  Equal: t('page.4bc69a0562ff'), NotEqual: t('page.9678e3a7cb3c'), Contains: t('page.107319b8b39d'),
  NotContains: t('page.5f60ffcf600a'), StartsWith: t('page.b35e50b3873d'), EndsWith: t('page.637f4e5ba011'),
  Regex: t('page.ad550e963af3'), Exists: t('page.4a558371f938'), NotExists: t('page.4e51b67dd32c'),
}))

const formatConditions = (rule: AdvancedCcRule): string => {
  if (!rule.conditions || rule.conditions.length === 0) return ''
  return rule.conditions.map(c => {
    const target = targetLabels.value[c.target] || c.target
    const op = operatorLabels.value[c.operator] || c.operator
    const vals = c.values.join(', ')
    return `${target}${op} [${vals}]`
  }).join('; ')
}

const formatRuleDesc = (rule: AdvancedCcRule): string => {
  const actionLabel = actionLabels.value[rule.action] || rule.action
  const condText = formatConditions(rule)
  const base = t('page.7596206d2fd6', { p0: rule.period, p1: rule.threshold, p2: actionLabel, p3: rule.actionSeconds })
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
    console.error(t('page.612d58d1a266'), e)
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
    console.error(t('page.66f7906c05b4'), e)
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
      await loadConfig()
      showSuccess(t('page.174688c755b9'))
    } else {
      showError(res.message || t('page.4757fc1d4709'))
    }
  } catch (e) {
    showError(t('page.4757fc1d4709'))
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
      await loadConfig()
      showSuccess(t('page.174688c755b9'))
    } else {
      showError(res.message || t('page.4757fc1d4709'))
    }
  } catch (e) {
    showError(t('page.4757fc1d4709'))
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
  showSuccess(t('page.c9105d640ece'))
}

const toggleCcRule = async (rule: AdvancedCcRule) => {
  try {
    const res = await advancedCcApi.toggleRule(rule.id)
    if (res.success) {
      rule.enabled = res.enabled
      showSuccess(res.enabled ? t('page.dfb802238b38') : t('page.bc5a87a757a5'))
    }
  } catch (e) {
    showError(t('page.0c3b4cf7aa25'))
  }
}

const deleteCcRule = async (rule: AdvancedCcRule) => {
  if (!confirm(t('page.9c812be7aa18', { p0: rule.name }))) return
  try {
    const res = await advancedCcApi.removeRule(rule.id)
    if (res.success) {
      loadCcRules()
      showSuccess(t('page.077a6d37719a'))
    }
  } catch (e) {
    showError(t('page.c228558cf257'))
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
          <h2 class="text-lg font-medium text-gray-100">{{ t('page.e15c3f4cac20') }}</h2>
          <p class="text-sm text-gray-500 mt-1">{{ t('page.604a94afc8dd') }}</p>
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
            :title="t('page.6135d4159e89')"
          >
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12"/>
            </svg>
          </button>
        </span>
      </div>
      <div v-else class="text-gray-500 text-sm mb-4">{{ t('page.4dfca1441dcc') }}</div>

      <!-- 添加输入 -->
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

    <!-- ========== Section 2: 路径统计规则 ========== -->
    <div class="card">
      <div class="flex items-center justify-between mb-3">
        <div>
          <h2 class="text-lg font-medium text-gray-100">{{ t('page.cae86dc7ee54') }}</h2>
          <p class="text-sm text-gray-500 mt-1">{{ t('page.ab5d8fff96d4') }}</p>
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
            :title="t('page.6135d4159e89')"
          >
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12"/>
            </svg>
          </button>
        </span>
      </div>
      <div v-else class="text-gray-500 text-sm mb-4">{{ t('page.9aad63a0ab8d') }}</div>

      <!-- 添加输入 -->
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

    <!-- ========== Section 3: CC 防护规则 ========== -->
    <div class="card">
      <div class="flex items-center justify-between mb-4">
        <div>
          <h2 class="text-lg font-medium text-gray-100">{{ t('page.54ad621bd04c') }}</h2>
          <p class="text-sm text-gray-500 mt-1">{{ t('page.d8b97e5f470e') }}</p>
        </div>
        <button @click="openAddCcRule" class="btn btn-sm btn-secondary flex items-center gap-1">
          {{ t('page.c9f9e18b0623') }}
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
                    {{ t('page.051836569928') }}
                  </button>
                  <button @click="deleteCcRule(rule)" class="text-xs text-red-400 hover:text-red-300">
                    {{ t('page.2f9daa828907') }}
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
          {{ t('page.a3d955c29e10') }}
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
