<script setup lang="ts">
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
const title = `WAF ${typeLabel} 检测`

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
    showError('请输入规则表达式')
    return
  }

  dialogLoading.value = true
  try {
    const res = props.ruleType === 'args'
      ? await wafApi.addArgsRule(pattern)
      : await wafApi.addPostRule(pattern)
    if (res.success) {
      rules.value.push({ id: Date.now().toString(), pattern, source: 'custom' })
      showSuccess(`${typeLabel} 规则已添加`)
      showDialog.value = false
    } else {
      showError((res as any).message || '添加失败')
    }
  } catch {
    showError('添加失败')
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
      showSuccess('规则已删除')
    }
  } catch {
    showError('删除失败')
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
        <span class="text-xs text-gray-500">共 {{ rules.length }} 条规则</span>
      </div>
      <div class="flex items-center gap-3">
        <button @click="openAddDialog" class="btn btn-sm btn-primary flex items-center gap-1.5">
          <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
          </svg>
          添加
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
                内置
              </span>
              <span v-else
                class="px-1.5 py-0.5 text-[10px] font-medium rounded bg-green-500/15 text-green-400 whitespace-nowrap">
                自定义
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
                移除
              </button>
            </td>
          </tr>
        </tbody>
      </table>
      <div v-if="rules.length === 0" class="text-center py-8 text-gray-500 text-sm">
        暂无 {{ typeLabel }} 检测规则
      </div>
    </div>
  </div>

  <!-- 添加规则弹窗 -->
  <Teleport to="body">
    <div v-if="showDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="showDialog = false"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-[460px] max-w-[90vw]">
        <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border">
          <h3 class="text-lg font-semibold text-gray-100">添加 {{ typeLabel }} 检测规则</h3>
          <button @click="showDialog = false" class="text-gray-400 hover:text-gray-200 text-xl leading-none">&times;</button>
        </div>
        <div class="px-6 py-5">
          <label class="block text-sm text-gray-400 mb-1">正则表达式 <span class="text-red-400">*</span></label>
          <input
            v-model="dialogPattern"
            type="text"
            class="input font-mono"
            placeholder="例如 (union|select|insert|update|delete)"
            @keydown.enter="submitRule"
          />
          <p class="text-xs text-gray-500 mt-2">用于检测请求中 {{ ruleType === 'args' ? 'URL 参数' : 'POST 数据' }} 的恶意内容</p>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="showDialog = false" class="btn btn-secondary">取消</button>
          <button @click="submitRule" :disabled="dialogLoading" class="btn btn-primary">
            {{ dialogLoading ? '提交中...' : '确定添加' }}
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
          <h3 class="text-lg font-semibold text-gray-100 mb-2">确认删除</h3>
          <p class="text-gray-400">确定要删除此规则吗？</p>
          <code v-if="deleteTarget" class="block mt-2 text-sm text-red-400 bg-dark-card-hover p-2 rounded break-all">{{ deleteTarget.pattern }}</code>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="showDeleteConfirm = false" class="btn btn-secondary">取消</button>
          <button @click="doDelete" class="btn btn-danger">确定删除</button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
