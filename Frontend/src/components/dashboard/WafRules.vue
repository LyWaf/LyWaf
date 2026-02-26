<script setup lang="ts">
import { ref, onMounted } from 'vue'
import Section from '@/components/common/Section.vue'
import { wafApi, dashboardApi } from '@/api'
import { useToast } from '@/composables/useToast'
import type { WafRule } from '@/types'

const { showSuccess, showError } = useToast()

const argsRules = ref<WafRule[]>([])
const postRules = ref<WafRule[]>([])

onMounted(async () => {
  try {
    const data = await dashboardApi.getData()
    if (data.success && data.wafRules) {
      argsRules.value = (data.wafRules.args || []).map((p, i) => ({ id: `a${i}`, pattern: p, type: 'args' as const }))
      postRules.value = (data.wafRules.post || []).map((p, i) => ({ id: `p${i}`, pattern: p, type: 'post' as const }))
    }
  } catch { /* 静默处理 */ }
})

// 弹窗状态
const showDialog = ref(false)
const dialogType = ref<'args' | 'post'>('args')
const dialogPattern = ref('')
const dialogLoading = ref(false)

const openAddDialog = (type: 'args' | 'post') => {
  dialogType.value = type
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
    if (dialogType.value === 'args') {
      const res = await wafApi.addArgsRule(pattern)
      if (res.success) {
        argsRules.value.push({ id: Date.now().toString(), pattern, type: 'args' })
        showSuccess('Args 规则已添加')
        showDialog.value = false
      } else {
        showError((res as any).message || '添加失败')
      }
    } else {
      const res = await wafApi.addPostRule(pattern)
      if (res.success) {
        postRules.value.push({ id: Date.now().toString(), pattern, type: 'post' })
        showSuccess('Post 规则已添加')
        showDialog.value = false
      } else {
        showError((res as any).message || '添加失败')
      }
    }
  } catch {
    showError('添加失败')
  } finally {
    dialogLoading.value = false
  }
}

// 删除弹窗
const showDeleteConfirm = ref(false)
const deleteTarget = ref<{ rule: WafRule; type: 'args' | 'post' } | null>(null)

const confirmDelete = (rule: WafRule, type: 'args' | 'post') => {
  deleteTarget.value = { rule, type }
  showDeleteConfirm.value = true
}

const doDelete = async () => {
  if (!deleteTarget.value) return
  const { rule, type } = deleteTarget.value
  try {
    const res = type === 'args'
      ? await wafApi.removeArgsRule(rule.pattern)
      : await wafApi.removePostRule(rule.pattern)
    if (res.success) {
      if (type === 'args') {
        argsRules.value = argsRules.value.filter(r => r.id !== rule.id)
      } else {
        postRules.value = postRules.value.filter(r => r.id !== rule.id)
      }
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
  <Section id="waf-rules" title="WAF 规则">
    <div class="grid grid-cols-1 lg:grid-cols-2 gap-6">
      <!-- Args 规则 -->
      <div>
        <div class="flex items-center justify-between mb-4">
          <h3 class="text-gray-300 font-medium">
            Args 检测规则
            <span class="badge badge-info ml-2">{{ argsRules.length }}</span>
          </h3>
          <button @click="openAddDialog('args')" class="btn btn-sm btn-primary">+ 添加</button>
        </div>
        <div class="space-y-2 max-h-[300px] overflow-y-auto">
          <div
            v-for="rule in argsRules"
            :key="rule.id"
            class="flex items-center justify-between p-3 bg-dark-card-hover rounded-lg"
          >
            <code class="text-blue-400 text-sm truncate flex-1 mr-2">{{ rule.pattern }}</code>
            <button
              @click="confirmDelete(rule, 'args')"
              class="text-gray-500 hover:text-red-400 transition-colors shrink-0"
            >
              ×
            </button>
          </div>
          <div v-if="argsRules.length === 0" class="text-gray-500 text-sm text-center py-4">
            暂无 Args 检测规则
          </div>
        </div>
      </div>

      <!-- Post 规则 -->
      <div>
        <div class="flex items-center justify-between mb-4">
          <h3 class="text-gray-300 font-medium">
            Post 检测规则
            <span class="badge badge-info ml-2">{{ postRules.length }}</span>
          </h3>
          <button @click="openAddDialog('post')" class="btn btn-sm btn-primary">+ 添加</button>
        </div>
        <div class="space-y-2 max-h-[300px] overflow-y-auto">
          <div
            v-for="rule in postRules"
            :key="rule.id"
            class="flex items-center justify-between p-3 bg-dark-card-hover rounded-lg"
          >
            <code class="text-blue-400 text-sm truncate flex-1 mr-2">{{ rule.pattern }}</code>
            <button
              @click="confirmDelete(rule, 'post')"
              class="text-gray-500 hover:text-red-400 transition-colors shrink-0"
            >
              ×
            </button>
          </div>
          <div v-if="postRules.length === 0" class="text-gray-500 text-sm text-center py-4">
            暂无 Post 检测规则
          </div>
        </div>
      </div>
    </div>
  </Section>

  <!-- 添加规则弹窗 -->
  <Teleport to="body">
    <div v-if="showDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="showDialog = false"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-[460px] max-w-[90vw]">
        <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border">
          <h3 class="text-lg font-semibold text-gray-100">添加 {{ dialogType === 'args' ? 'Args' : 'Post' }} 检测规则</h3>
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
          <p class="text-xs text-gray-500 mt-2">用于检测请求中 {{ dialogType === 'args' ? 'URL 参数' : 'POST 数据' }} 的恶意内容</p>
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
          <code v-if="deleteTarget" class="block mt-2 text-sm text-red-400 bg-dark-card-hover p-2 rounded break-all">{{ deleteTarget.rule.pattern }}</code>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="showDeleteConfirm = false" class="btn btn-secondary">取消</button>
          <button @click="doDelete" class="btn btn-danger">确定删除</button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
