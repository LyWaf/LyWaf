<script setup lang="ts">
import { t } from '@/i18n'
import { ref, computed, onMounted } from 'vue'
import { errorTemplateApi } from '@/api'
import type { ErrorTemplateItem, ErrorTemplateDetail } from '@/api'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()

const STATUS_INFO = computed<Record<number, { en: string; zh: string }>>(() => ({
  403: { en: 'Forbidden', zh: t('api.forbidden') },
  404: { en: 'Not Found', zh: t('page.8e5568258ee0') },
  429: { en: 'Too Many Requests', zh: t('page.a7942bc6d6d7') },
  500: { en: 'Internal Server Error', zh: t('page.036549074827') },
  502: { en: 'Bad Gateway', zh: t('page.20edb0eb0d18') },
  503: { en: 'Service Unavailable', zh: t('page.c5798f0b3110') },
}))

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
    showError(t('page.2a7a6978460b'))
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
    showError(t('page.6b5775a59018'))
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
      showSuccess(t('page.11cd7a79f4da'))
      // 刷新详情和列表
      await selectTemplate(selectedCode.value)
      await loadList()
    } else {
      showError(res?.message || t('ui.save.failed'))
    }
  } catch {
    showError(t('ui.save.failed'))
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
      showSuccess(t('page.70217866ec64'))
      showRevertConfirm.value = false
      selectedCode.value = selectedCode.value // force re-select
      await selectTemplate(selectedCode.value!)
      await loadList()
    } else {
      showError(res?.message || t('page.89281a45e04f'))
    }
  } catch {
    showError(t('page.89281a45e04f'))
  } finally {
    reverting.value = false
  }
}

// 重置编辑内容
const resetContent = () => {
  if (detail.value) {
    editContent.value = detail.value.activeContent
    showSuccess(t('page.d9eb707cf247'))
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
          <h2 class="text-lg font-semibold text-gray-100">{{ t('nav.errorTemplates') }}</h2>
          <span class="text-xs text-gray-500">{{ t('page.4d2777b5a590') }}</span>
        </div>
      </div>
    </div>

    <!-- 提示 -->
    <div class="px-4 py-3 rounded-lg bg-blue-500/10 border border-blue-500/20 text-sm text-blue-300">
      <span class="font-medium">{{ t('page.be8e4dd716fb') }}</span>
      {{ t('page.5054e27de803') }} <code class="bg-white/10 px-1 rounded">.lywaf.403.html</code>{{ t('page.11ea5238804a') }}
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
            <span v-if="tpl.hasEdit" class="w-1.5 h-1.5 rounded-full bg-cyan-400 shrink-0" :title="t('page.d0c9d92453ab')"></span>
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
              <span v-if="detail.hasEdit" class="px-1.5 py-0.5 text-[10px] rounded bg-cyan-500/20 text-cyan-400">{{ t('page.d0c9d92453ab') }}</span>
              <span v-if="hasChanges" class="px-1.5 py-0.5 text-[10px] rounded bg-amber-500/20 text-amber-400">{{ t('page.6a1be012c99c') }}</span>
            </div>
            <div class="flex items-center gap-2">
              <span class="text-xs text-gray-500 mr-2">{{ t('page.1da37f8d35e3') }}</span>
              <button v-if="detail.hasEdit && detail.hasOriginal" @click="viewOriginal"
                class="text-xs text-gray-500 hover:text-gray-300 transition-colors px-2 py-1 rounded hover:bg-white/5">
                {{ t('page.3b9f1e3ddd11') }}
              </button>
              <button v-if="detail.hasEdit" @click="showRevertConfirm = true"
                class="text-xs text-red-400 hover:text-red-300 transition-colors px-2 py-1 rounded hover:bg-red-500/10">
                {{ t('page.2d41469ba61a') }}
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
              {{ t('page.41b50a9198f4') }}
            </button>
            <button @click="activeTab = 'preview'"
              :class="['px-3 py-1 text-xs font-medium rounded transition-colors',
                activeTab === 'preview'
                  ? 'bg-primary-500/15 text-primary-400'
                  : 'text-gray-500 hover:text-gray-300 hover:bg-white/5']">
              {{ t('page.13d61fea9f17') }}
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
              {{ detail.hasEdit ? t('page.14f3481090a7', { p0: selectedCode }) : t('page.1b4690b0dc2f', { p0: selectedCode }) }}
            </div>
            <div class="flex items-center gap-2">
              <button @click="resetContent" :disabled="!hasChanges || saving"
                class="btn btn-sm btn-secondary text-xs">
                {{ t('page.b5a929dae5ba') }}
              </button>
              <button @click="saveTemplate" :disabled="saving || !hasChanges"
                class="btn btn-sm btn-primary text-xs flex items-center gap-1.5">
                <span v-if="saving" class="w-3 h-3 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
                <span>{{ saving ? t('page.917b1c1f18d0') : t('common.save') }}</span>
              </button>
            </div>
          </div>
        </template>

        <!-- 未选中状态 -->
        <div v-else class="flex-1 flex items-center justify-center text-gray-500 text-sm">
          {{ t('page.7b5b8edec475') }}
        </div>
      </div>
    </div>

    <!-- 恢复确认对话框 -->
    <Teleport to="body">
      <div v-if="showRevertConfirm" class="fixed inset-0 z-50 flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="showRevertConfirm = false"></div>
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl p-6 max-w-md w-full mx-4">
          <h3 class="text-lg font-semibold text-gray-100 mb-2">{{ t('page.82d20f9e1a44') }}</h3>
          <p class="text-sm text-gray-400 mb-4">
            {{ t('page.8bee9be79dcd') }} <code class="bg-white/10 px-1 rounded">.lywaf.{{ selectedCode }}.html</code> {{ t('page.125d21e67b19') }}
          </p>
          <p class="text-sm text-red-400 mb-5">{{ t('page.a13c91288e4c') }}</p>
          <div class="flex justify-end gap-3">
            <button @click="showRevertConfirm = false" class="btn btn-sm btn-secondary">{{ t('common.cancel') }}</button>
            <button @click="revertTemplate" :disabled="reverting"
              class="btn btn-sm bg-red-600 hover:bg-red-500 text-white flex items-center gap-1.5">
              <span v-if="reverting" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
              <span>{{ reverting ? t('page.c93c8b1cadcc') : t('page.51e44d56ec90') }}</span>
            </button>
          </div>
        </div>
      </div>
    </Teleport>
  </div>
</template>
