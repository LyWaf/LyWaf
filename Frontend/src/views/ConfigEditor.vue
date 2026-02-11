<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { configFileApi } from '@/api'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()

// 状态
const loading = ref(false)
const saving = ref(false)
const converting = ref(false)
const fileName = ref('')
const format = ref('ly')
const content = ref('')                // 编辑区域内容（加载时取草稿优先）
const originalContent = ref('')        // 原始配置文件内容（不可变）
const lastSavedContent = ref('')       // 上次保存/加载时的内容
const yamlPreview = ref('')
const activeTab = ref<'source' | 'yaml'>('source')
const hasDraft = ref(false)

const isLyFormat = computed(() => format.value === 'ly')
const hasChanges = computed(() => content.value !== lastSavedContent.value)
const isDifferentFromOriginal = computed(() => content.value !== originalContent.value)

// 加载配置文件
const loadFile = async () => {
  loading.value = true
  try {
    const response = await configFileApi.getFile()
    if (response.success) {
      fileName.value = response.fileName
      format.value = response.format
      originalContent.value = response.content
      hasDraft.value = response.hasDraft

      // 仅在运行时已加载草稿配置时，默认显示草稿内容
      if (response.draftLoaded && response.draftContent) {
        content.value = response.draftContent
        lastSavedContent.value = response.draftContent
      } else {
        content.value = response.content
        lastSavedContent.value = response.content
      }
      yamlPreview.value = ''
    }
  } catch (error: any) {
    showError(error?.response?.data?.message || '加载配置文件失败')
  } finally {
    loading.value = false
  }
}

// 恢复为原始文件内容
const restoreOriginal = () => {
  content.value = originalContent.value
  showSuccess('已恢复为原始配置文件内容')
}

// 转换为 YAML 预览
const convertToYaml = async () => {
  if (!isLyFormat.value) return
  converting.value = true
  try {
    const response = await configFileApi.convert(content.value)
    if (response.success) {
      yamlPreview.value = response.yaml
      activeTab.value = 'yaml'
    } else {
      showError(response.message || '转换失败')
    }
  } catch (error: any) {
    showError(error?.response?.data?.message || '转换失败')
  } finally {
    converting.value = false
  }
}

// 保存配置（保存到草稿文件，不覆盖原始配置）
const saveConfig = async (reload: boolean = false) => {
  saving.value = true
  try {
    const response = await configFileApi.saveFile(content.value, reload)
    if (response.success) {
      lastSavedContent.value = content.value
      hasDraft.value = true
      showSuccess(response.message || '草稿已保存')
    } else {
      showError(response.message || '保存失败')
    }
  } catch (error: any) {
    showError(error?.response?.data?.message || '保存失败')
  } finally {
    saving.value = false
  }
}

// 重置为上次保存的内容
const resetContent = () => {
  content.value = lastSavedContent.value
  showSuccess('已重置为上次保存的内容')
}

// Tab 切换
const switchTab = (tab: 'source' | 'yaml') => {
  if (tab === 'yaml' && isLyFormat.value) {
    convertToYaml()
  } else {
    activeTab.value = tab
  }
}

// 快捷键
const handleKeydown = (e: KeyboardEvent) => {
  if ((e.ctrlKey || e.metaKey) && e.key === 's') {
    e.preventDefault()
    saveConfig(false)
    return
  }

  // Tab 插入4个空格（使用 execCommand 保留浏览器原生 undo 支持）
  if (e.key === 'Tab') {
    e.preventDefault()
    document.execCommand('insertText', false, '    ')
  }
}

onMounted(() => {
  loadFile()
})
</script>

<template>
  <div class="space-y-4">
    <!-- 顶部标题栏 -->
    <div class="card">
      <div class="flex items-center justify-between">
        <div class="flex items-center gap-3">
          <h2 class="text-lg font-semibold text-gray-100">配置文件</h2>
          <span v-if="fileName" class="px-2.5 py-0.5 text-xs font-medium rounded-full"
            :class="isLyFormat ? 'bg-purple-500/20 text-purple-400' : 'bg-blue-500/20 text-blue-400'">
            {{ fileName }}
          </span>
          <span v-if="hasDraft"
            class="px-2 py-0.5 text-xs font-medium rounded-full bg-cyan-500/20 text-cyan-400">
            草稿
          </span>
          <span v-if="hasChanges"
            class="px-2 py-0.5 text-xs font-medium rounded-full bg-amber-500/20 text-amber-400">
            未保存
          </span>
        </div>
        <div class="flex items-center gap-2">
          <button v-if="isDifferentFromOriginal" @click="restoreOriginal"
            class="btn btn-sm btn-secondary text-xs">
            恢复原始
          </button>
          <button @click="loadFile" :disabled="loading"
            class="btn btn-sm btn-secondary flex items-center gap-1.5">
            <span v-if="loading" class="w-3.5 h-3.5 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
            <span>刷新</span>
          </button>
        </div>
      </div>
    </div>

    <!-- 提示信息 -->
    <div class="px-4 py-3 rounded-lg bg-blue-500/10 border border-blue-500/20 text-sm text-blue-300">
      <span class="font-medium">提示：</span>
      编辑内容将保存为草稿文件，不会覆盖原始配置。重载仅更新路由、规则等运行时配置，端口监听变更需要重启服务。
    </div>

    <!-- Tab 切换（仅 .ly 格式） -->
    <div v-if="isLyFormat" class="flex gap-1 bg-dark-card rounded-lg p-1 border border-dark-border">
      <button @click="switchTab('source')"
        :class="['flex-1 px-4 py-2 text-sm font-medium rounded-md transition-colors',
          activeTab === 'source'
            ? 'bg-primary-500/15 text-primary-400'
            : 'text-gray-400 hover:text-gray-200 hover:bg-white/5']">
        📝 源文件 (.ly)
      </button>
      <button @click="switchTab('yaml')"
        :class="['flex-1 px-4 py-2 text-sm font-medium rounded-md transition-colors',
          activeTab === 'yaml'
            ? 'bg-primary-500/15 text-primary-400'
            : 'text-gray-400 hover:text-gray-200 hover:bg-white/5']">
        <span v-if="converting" class="inline-block w-3 h-3 border-2 border-gray-400 border-t-transparent rounded-full animate-spin mr-1.5"></span>
        🔄 YAML 预览
      </button>
    </div>

    <!-- 编辑区域 -->
    <div class="card !p-0 overflow-hidden">
      <!-- 源文件编辑 -->
      <div v-show="activeTab === 'source'">
        <div class="flex items-center justify-between px-4 py-2 bg-dark-sidebar border-b border-dark-border">
          <span class="text-xs text-gray-500">{{ isLyFormat ? 'LyWaf 配置格式 (.ly)' : 'YAML 配置格式' }}</span>
          <span class="text-xs text-gray-500">Ctrl+S 保存草稿</span>
        </div>
        <textarea
          v-model="content"
          @keydown="handleKeydown"
          class="w-full bg-[#0d1117] text-[#e6edf3] font-mono text-sm leading-6 p-4 resize-y outline-none border-none"
          :style="{ minHeight: '500px', tabSize: 4 }"
          spellcheck="false"
          autocomplete="off"
          autocorrect="off"
          autocapitalize="off"
        ></textarea>
      </div>

      <!-- YAML 预览（只读） -->
      <div v-show="activeTab === 'yaml'">
        <div class="flex items-center justify-between px-4 py-2 bg-dark-sidebar border-b border-dark-border">
          <span class="text-xs text-gray-500">YAML 转换预览（只读）</span>
          <span class="text-xs text-gray-500">由 .ly 文件自动转换</span>
        </div>
        <textarea
          :value="yamlPreview"
          readonly
          class="w-full bg-[#0d1117] text-[#8b949e] font-mono text-sm leading-6 p-4 resize-y outline-none border-none cursor-default"
          :style="{ minHeight: '500px', tabSize: 2 }"
          spellcheck="false"
        ></textarea>
      </div>
    </div>

    <!-- 底部操作栏 -->
    <div class="card">
      <div class="flex items-center justify-between">
        <div class="text-xs text-gray-500">
          草稿保存不影响原始配置文件，重载后运行时配置立即生效
        </div>
        <div class="flex items-center gap-2">
          <button @click="resetContent" :disabled="!hasChanges || saving"
            class="btn btn-sm btn-secondary">
            撤销修改
          </button>
          <button @click="saveConfig(false)" :disabled="saving || !hasChanges"
            class="btn btn-sm btn-secondary">
            {{ saving ? '保存中...' : '保存草稿' }}
          </button>
          <button @click="saveConfig(true)" :disabled="saving"
            class="btn btn-sm btn-primary flex items-center gap-1.5">
            <span v-if="saving" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
            <span>{{ saving ? '保存中...' : '保存并重载' }}</span>
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
