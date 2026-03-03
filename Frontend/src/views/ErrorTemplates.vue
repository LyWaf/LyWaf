<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { errorTemplateApi } from '@/api'
import type { ErrorTemplateItem, ErrorTemplateDetail } from '@/api'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()

const STATUS_INFO: Record<number, { en: string; zh: string }> = {
  403: { en: 'Forbidden', zh: '禁止访问' },
  404: { en: 'Not Found', zh: '页面未找到' },
  429: { en: 'Too Many Requests', zh: '请求过多' },
  500: { en: 'Internal Server Error', zh: '服务器内部错误' },
  502: { en: 'Bad Gateway', zh: '网关错误' },
  503: { en: 'Service Unavailable', zh: '服务不可用' },
}

// 状态
const loading = ref(false)
const templates = ref<ErrorTemplateItem[]>([])
const selectedCode = ref<number | null>(null)
const detail = ref<ErrorTemplateDetail | null>(null)
const loadingDetail = ref(false)
const saving = ref(false)
const editContent = ref('')
const showRevertConfirm = ref(false)
const reverting = ref(false)
const activeTab = ref<'source' | 'preview'>('source')

// 是否有未保存的修改
const hasChanges = computed(() => {
  if (!detail.value) return false
  return editContent.value !== detail.value.activeContent
})

// 加载列表
const loadList = async () => {
  loading.value = true
  try {
    const res = await errorTemplateApi.list() as unknown as { success: boolean; templates: ErrorTemplateItem[] }
    if (res?.success) {
      templates.value = res.templates
    }
  } catch {
    showError('加载模板列表失败')
  } finally {
    loading.value = false
  }
}

// 选中模板
const selectTemplate = async (code: number) => {
  if (selectedCode.value === code && detail.value) return
  selectedCode.value = code
  loadingDetail.value = true
  try {
    const res = await errorTemplateApi.get(code) as unknown as ErrorTemplateDetail
    if (res?.success !== false) {
      detail.value = res
      editContent.value = res.activeContent
    }
  } catch {
    showError('加载模板内容失败')
  } finally {
    loadingDetail.value = false
  }
}

// 保存
const saveTemplate = async () => {
  if (!selectedCode.value) return
  saving.value = true
  try {
    const res = await errorTemplateApi.save(selectedCode.value, editContent.value) as unknown as { success: boolean; message?: string }
    if (res?.success) {
      showSuccess('模板已保存')
      // 刷新详情和列表
      await selectTemplate(selectedCode.value)
      await loadList()
    } else {
      showError(res?.message || '保存失败')
    }
  } catch {
    showError('保存失败')
  } finally {
    saving.value = false
  }
}

// 恢复为原始版本
const revertTemplate = async () => {
  if (!selectedCode.value) return
  reverting.value = true
  try {
    const res = await errorTemplateApi.revert(selectedCode.value) as unknown as { success: boolean; message?: string }
    if (res?.success) {
      showSuccess('已恢复为原始模板')
      showRevertConfirm.value = false
      selectedCode.value = selectedCode.value // force re-select
      await selectTemplate(selectedCode.value!)
      await loadList()
    } else {
      showError(res?.message || '恢复失败')
    }
  } catch {
    showError('恢复失败')
  } finally {
    reverting.value = false
  }
}

// 重置编辑内容
const resetContent = () => {
  if (detail.value) {
    editContent.value = detail.value.activeContent
    showSuccess('已撤销修改')
  }
}

// 查看原始版本
const viewOriginal = () => {
  if (detail.value?.originalContent) {
    editContent.value = detail.value.originalContent
  }
}

// 快捷键
const handleKeydown = (e: KeyboardEvent) => {
  if ((e.ctrlKey || e.metaKey) && e.key === 's') {
    e.preventDefault()
    saveTemplate()
    return
  }
  if (e.key === 'Tab') {
    e.preventDefault()
    document.execCommand('insertText', false, '    ')
  }
}

onMounted(() => {
  loadList()
})
</script>

<template>
  <div class="space-y-4">
    <!-- 顶部标题栏 -->
    <div class="card">
      <div class="flex items-center justify-between">
        <div class="flex items-center gap-3">
          <h2 class="text-lg font-semibold text-gray-100">错误模板</h2>
          <span class="text-xs text-gray-500">自定义 HTTP 错误页面</span>
        </div>
      </div>
    </div>

    <!-- 提示 -->
    <div class="px-4 py-3 rounded-lg bg-blue-500/10 border border-blue-500/20 text-sm text-blue-300">
      <span class="font-medium">提示：</span>
      编辑后保存为独立文件（如 <code class="bg-white/10 px-1 rounded">.lywaf.403.html</code>），不会覆盖原始模板。可随时恢复为原始版本。
    </div>

    <div class="flex gap-4" style="min-height: 600px;">
      <!-- 左侧列表 -->
      <div class="w-48 shrink-0 card !p-2 space-y-1">
        <div v-if="loading" class="flex items-center justify-center py-8">
          <span class="w-5 h-5 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
        </div>
        <button
          v-for="tpl in templates" :key="tpl.statusCode"
          @click="selectTemplate(tpl.statusCode)"
          :class="[
            'w-full text-left px-3 py-2 rounded-lg text-sm transition-colors',
            selectedCode === tpl.statusCode
              ? 'bg-primary-500/15 text-primary-400'
              : 'text-gray-300 hover:bg-white/5 hover:text-gray-100'
          ]"
        >
          <div class="flex items-center justify-between gap-1">
            <span class="font-mono">{{ tpl.statusCode }}</span>
            <span v-if="tpl.hasEdit" class="w-1.5 h-1.5 rounded-full bg-cyan-400 shrink-0" title="已编辑"></span>
          </div>
          <div v-if="STATUS_INFO[tpl.statusCode]" class="text-[11px] mt-0.5 opacity-60 truncate">
            {{ STATUS_INFO[tpl.statusCode].en }}
          </div>
        </button>
      </div>

      <!-- 右侧编辑区 -->
      <div class="flex-1 card !p-0 overflow-hidden flex flex-col">
        <template v-if="selectedCode && detail">
          <!-- 编辑器头部 -->
          <div class="flex items-center justify-between px-4 py-2 bg-dark-sidebar border-b border-dark-border">
            <div class="flex items-center gap-2">
              <span class="text-sm font-medium text-gray-200">{{ selectedCode }} {{ STATUS_INFO[selectedCode]?.en }}</span>
              <span v-if="STATUS_INFO[selectedCode]?.zh" class="text-xs text-gray-500">{{ STATUS_INFO[selectedCode].zh }}</span>
              <span v-if="detail.hasEdit" class="px-1.5 py-0.5 text-[10px] rounded bg-cyan-500/20 text-cyan-400">已编辑</span>
              <span v-if="hasChanges" class="px-1.5 py-0.5 text-[10px] rounded bg-amber-500/20 text-amber-400">未保存</span>
            </div>
            <div class="flex items-center gap-2">
              <span class="text-xs text-gray-500 mr-2">Ctrl+S 保存</span>
              <button v-if="detail.hasEdit && detail.hasOriginal" @click="viewOriginal"
                class="text-xs text-gray-500 hover:text-gray-300 transition-colors px-2 py-1 rounded hover:bg-white/5">
                查看原始
              </button>
              <button v-if="detail.hasEdit" @click="showRevertConfirm = true"
                class="text-xs text-red-400 hover:text-red-300 transition-colors px-2 py-1 rounded hover:bg-red-500/10">
                恢复原始
              </button>
            </div>
          </div>

          <!-- Tab 切换 -->
          <div class="flex gap-1 px-4 py-1.5 bg-dark-sidebar border-b border-dark-border">
            <button @click="activeTab = 'source'"
              :class="['px-3 py-1 text-xs font-medium rounded transition-colors',
                activeTab === 'source'
                  ? 'bg-primary-500/15 text-primary-400'
                  : 'text-gray-500 hover:text-gray-300 hover:bg-white/5']">
              源码
            </button>
            <button @click="activeTab = 'preview'"
              :class="['px-3 py-1 text-xs font-medium rounded transition-colors',
                activeTab === 'preview'
                  ? 'bg-primary-500/15 text-primary-400'
                  : 'text-gray-500 hover:text-gray-300 hover:bg-white/5']">
              预览
            </button>
          </div>

          <!-- 编辑/预览区域 -->
          <div class="flex-1 relative">
            <div v-if="loadingDetail" class="absolute inset-0 flex items-center justify-center bg-dark-bg/80 z-10">
              <span class="w-5 h-5 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
            </div>
            <!-- 源码编辑 -->
            <textarea
              v-show="activeTab === 'source'"
              v-model="editContent"
              @keydown="handleKeydown"
              class="w-full h-full bg-[#0d1117] text-[#e6edf3] font-mono text-sm leading-6 p-4 resize-none outline-none border-none"
              style="min-height: 500px;"
              spellcheck="false"
              autocomplete="off"
            ></textarea>
            <!-- HTML 预览 -->
            <iframe
              v-show="activeTab === 'preview'"
              :srcdoc="editContent"
              class="w-full border-none bg-white"
              style="min-height: 500px; height: 100%;"
              sandbox="allow-same-origin"
            ></iframe>
          </div>

          <!-- 底部操作栏 -->
          <div class="flex items-center justify-between px-4 py-3 bg-dark-sidebar border-t border-dark-border">
            <div class="text-xs text-gray-500">
              {{ detail.hasEdit ? `文件: .lywaf.${selectedCode}.html` : `文件: ${selectedCode}.html` }}
            </div>
            <div class="flex items-center gap-2">
              <button @click="resetContent" :disabled="!hasChanges || saving"
                class="btn btn-sm btn-secondary text-xs">
                撤销修改
              </button>
              <button @click="saveTemplate" :disabled="saving || !hasChanges"
                class="btn btn-sm btn-primary text-xs flex items-center gap-1.5">
                <span v-if="saving" class="w-3 h-3 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
                <span>{{ saving ? '保存中...' : '保存' }}</span>
              </button>
            </div>
          </div>
        </template>

        <!-- 未选中状态 -->
        <div v-else class="flex-1 flex items-center justify-center text-gray-500 text-sm">
          请从左侧选择一个状态码进行编辑
        </div>
      </div>
    </div>

    <!-- 恢复确认对话框 -->
    <Teleport to="body">
      <div v-if="showRevertConfirm" class="fixed inset-0 z-50 flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="showRevertConfirm = false"></div>
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl p-6 max-w-md w-full mx-4">
          <h3 class="text-lg font-semibold text-gray-100 mb-2">恢复为原始模板</h3>
          <p class="text-sm text-gray-400 mb-4">
            将删除 <code class="bg-white/10 px-1 rounded">.lywaf.{{ selectedCode }}.html</code> 编辑文件，恢复使用原始模板。
          </p>
          <p class="text-sm text-red-400 mb-5">此操作不可撤销，确定要继续吗？</p>
          <div class="flex justify-end gap-3">
            <button @click="showRevertConfirm = false" class="btn btn-sm btn-secondary">取消</button>
            <button @click="revertTemplate" :disabled="reverting"
              class="btn btn-sm bg-red-600 hover:bg-red-500 text-white flex items-center gap-1.5">
              <span v-if="reverting" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
              <span>{{ reverting ? '恢复中...' : '确认恢复' }}</span>
            </button>
          </div>
        </div>
      </div>
    </Teleport>
  </div>
</template>
