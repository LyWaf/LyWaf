<script setup lang="ts">
import { t } from '@/i18n'
import { ref, onMounted } from 'vue'
import { wafApi, dashboardApi } from '@/api'
import { useToast } from '@/composables/useToast'

interface RuleItem {
  id: string
  pattern: string
  source: 'file' | 'custom'
}

const props = defineProps<{
  ruleType: 'args' | 'post'
  enabled: boolean
  loading: boolean
}>()

const emit = defineEmits<{
  toggle: []
}>()

const { showSuccess, showError } = useToast()

const rules = ref<RuleItem[]>([])
const typeLabel = props.ruleType === 'args' ? 'Args' : 'Post'
const title = t('page.35d3df2f956c', { p0: typeLabel })

onMounted(async () => {
  try {
    const data = await dashboardApi.getData()
    if (data.success && data.wafRules) {
      const key = props.ruleType
      const patterns: string[] = data.wafRules[key] || []
      const fileCount = key === 'args' ? data.wafRules.argsFileCount : data.wafRules.postFileCount
      rules.value = patterns.map((p, i) => ({
        id: `${key[0]}${i}`,
        pattern: p,
        source: i < fileCount ? 'file' as const : 'custom' as const
      }))
    }
  } catch { /* 静默处理 */ }
})

// 弹窗状态
const showDialog = ref(false)
const dialogPattern = ref('')
const dialogLoading = ref(false)

const openAddDialog = () => {
  dialogPattern.value = ''
  showDialog.value = true
}

const submitRule = async () => {
  const pattern = dialogPattern.value.trim()
  if (!pattern) {
    showError(t('page.204801c813fa'))
    return
  }

  dialogLoading.value = true
  try {
    const res = props.ruleType === 'args'
      ? await wafApi.addArgsRule(pattern)
      : await wafApi.addPostRule(pattern)
    if (res.success) {
      rules.value.push({ id: Date.now().toString(), pattern, source: 'custom' })
      showSuccess(t('page.8758c0a28cf4', { p0: typeLabel }))
      showDialog.value = false
    } else {
      showError((res as any).message || t('dashboard.failed.to.add'))
    }
  } catch {
    showError(t('dashboard.failed.to.add'))
  } finally {
    dialogLoading.value = false
  }
}

// 删除弹窗
const showDeleteConfirm = ref(false)
const deleteTarget = ref<RuleItem | null>(null)

const confirmDelete = (rule: RuleItem) => {
  deleteTarget.value = rule
  showDeleteConfirm.value = true
}

const doDelete = async () => {
  if (!deleteTarget.value) return
  try {
    const res = props.ruleType === 'args'
      ? await wafApi.removeArgsRule(deleteTarget.value.pattern)
      : await wafApi.removePostRule(deleteTarget.value.pattern)
    if (res.success) {
      rules.value = rules.value.filter(r => r.id !== deleteTarget.value!.id)
      showSuccess(t('page.04e366e33981'))
    }
  } catch {
    showError(t('settings.deletion.failed'))
  } finally {
    showDeleteConfirm.value = false
    deleteTarget.value = null
  }
}
</script>

<template>
  <div class="card">
    <!-- 标题栏 -->
    <div class="flex items-center justify-between mb-4 pb-3 border-b border-dark-border">
      <div class="flex items-center gap-3">
        <h2 class="text-base font-semibold text-gray-100">{{ title }}</h2>
        <span class="text-xs text-gray-500">{{ t('page.76e547a8fa54') }} {{ rules.length }} {{ t('page.65ca3e7654a7') }}</span>
      </div>
      <div class="flex items-center gap-3">
        <button @click="openAddDialog" class="btn btn-sm btn-primary flex items-center gap-1.5">
          <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
          </svg>
          {{ t('common.add') }}
        </button>
        <button
          @click="emit('toggle')"
          :disabled="loading"
          :class="[
            'relative w-12 h-6 rounded-full transition-colors flex-shrink-0',
            enabled ? 'bg-primary-500' : 'bg-gray-600'
          ]"
        >
          <span
            :class="[
              'absolute top-1 w-4 h-4 rounded-full bg-white transition-transform',
              enabled ? 'left-7' : 'left-1'
            ]"
          />
        </button>
      </div>
    </div>

    <!-- 规则列表 -->
    <div class="max-h-[400px] overflow-y-auto -mx-5 -mb-5">
      <table class="w-full text-sm">
        <tbody>
          <tr
            v-for="rule in rules"
            :key="rule.id"
            class="border-t border-dark-border/50 hover:bg-white/[0.03] transition-colors"
          >
            <td class="px-5 py-2 w-14">
              <span v-if="rule.source === 'file'"
                class="px-1.5 py-0.5 text-[10px] font-medium rounded bg-cyan-500/15 text-cyan-400 whitespace-nowrap">
                {{ t('settings.built.in') }}
              </span>
              <span v-else
                class="px-1.5 py-0.5 text-[10px] font-medium rounded bg-green-500/15 text-green-400 whitespace-nowrap">
                {{ t('page.4eafa9e925b3') }}
              </span>
            </td>
            <td class="py-2 pr-4">
              <code class="text-blue-400 text-xs font-mono truncate block max-w-[600px]" :title="rule.pattern">{{ rule.pattern }}</code>
            </td>
            <td class="px-5 py-2 w-16 text-right">
              <button
                v-if="rule.source === 'custom'"
                @click="confirmDelete(rule)"
                class="text-xs text-red-400 hover:text-red-300 px-2 py-0.5 rounded hover:bg-red-500/10 transition-colors"
              >
                {{ t('page.6135d4159e89') }}
              </button>
            </td>
          </tr>
        </tbody>
      </table>
      <div v-if="rules.length === 0" class="text-center py-8 text-gray-500 text-sm">
        {{ t('page.b336a174cd1f') }} {{ typeLabel }} {{ t('page.1288057a5655') }}
      </div>
    </div>
  </div>

  <!-- 添加规则弹窗 -->
  <Teleport to="body">
    <div v-if="showDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="showDialog = false"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-[460px] max-w-[90vw]">
        <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border">
          <h3 class="text-lg font-semibold text-gray-100">{{ t('common.add') }} {{ typeLabel }} {{ t('page.1288057a5655') }}</h3>
          <button @click="showDialog = false" class="text-gray-400 hover:text-gray-200 text-xl leading-none">&times;</button>
        </div>
        <div class="px-6 py-5">
          <label class="block text-sm text-gray-400 mb-1">{{ t('page.af33e62808ca') }} <span class="text-red-400">*</span></label>
          <input
            v-model="dialogPattern"
            type="text"
            class="input font-mono"
            :placeholder="t('page.d7c2556bef63')"
            @keydown.enter="submitRule"
          />
          <p class="text-xs text-gray-500 mt-2">{{ t('waf.inspectSource', { source: ruleType === 'args' ? t('page.7cffb7812fcc') : t('page.e8d40fcde9f1') }) }}</p>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="showDialog = false" class="btn btn-secondary">{{ t('common.cancel') }}</button>
          <button @click="submitRule" :disabled="dialogLoading" class="btn btn-primary">
            {{ dialogLoading ? t('settings.submitting') : t('page.1945909fd7f7') }}
          </button>
        </div>
      </div>
    </div>
  </Teleport>

  <!-- 删除确认弹窗 -->
  <Teleport to="body">
    <div v-if="showDeleteConfirm" class="fixed inset-0 z-[100] flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="showDeleteConfirm = false"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-[400px] max-w-[90vw]">
        <div class="px-6 py-5">
          <h3 class="text-lg font-semibold text-gray-100 mb-2">{{ t('settings.confirm.deletion') }}</h3>
          <p class="text-gray-400">{{ t('page.7cd709e90316') }}</p>
          <code v-if="deleteTarget" class="block mt-2 text-sm text-red-400 bg-dark-card-hover p-2 rounded break-all">{{ deleteTarget.pattern }}</code>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="showDeleteConfirm = false" class="btn btn-secondary">{{ t('common.cancel') }}</button>
          <button @click="doDelete" class="btn btn-danger">{{ t('page.357af24bee32') }}</button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
