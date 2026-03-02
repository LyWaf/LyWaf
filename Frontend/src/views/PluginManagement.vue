<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import { pluginApi } from '@/api'
import type { PluginItem } from '@/api'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()

const plugins = ref<PluginItem[]>([])
const loading = ref(true)
const toggling = ref<string | null>(null)

// =============== 配置编辑 ===============
const editConfigVisible = ref(false)
const editConfigPlugin = ref<PluginItem | null>(null)
const editConfigLoading = ref(false)
const editConfigSaving = ref(false)
const editConfigItems = ref<Array<{ key: string; value: string; original: string }>>([])

const hasConfigChanges = computed(() => {
  return editConfigItems.value.some(item => item.value !== item.original)
})

const loadPlugins = async () => {
  loading.value = true
  try {
    const res = await pluginApi.getPlugins()
    if (res.success) {
      plugins.value = res.plugins
    }
  } catch {
    showError('获取插件列表失败')
  } finally {
    loading.value = false
  }
}

const togglePlugin = async (plugin: PluginItem) => {
  toggling.value = plugin.id
  try {
    const res = await pluginApi.togglePlugin(plugin.id)
    if (res.success) {
      plugin.isEnabled = res.isEnabled
      plugin.state = res.state
      showSuccess(res.message || '操作成功')
    } else {
      showError(res.message || '操作失败')
    }
  } catch (error: any) {
    showError(error?.response?.data?.message || error?.message || '操作失败')
  } finally {
    toggling.value = null
  }
}

const openEditConfig = async (plugin: PluginItem) => {
  editConfigPlugin.value = plugin
  editConfigVisible.value = true
  editConfigLoading.value = true
  editConfigItems.value = []

  try {
    const res = await pluginApi.getConfig(plugin.id)
    if (res.success) {
      editConfigItems.value = Object.entries(res.config)
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([key, value]) => ({ key, value, original: value }))
    } else {
      showError('获取插件配置失败')
    }
  } catch {
    showError('获取插件配置失败')
  } finally {
    editConfigLoading.value = false
  }
}

const saveConfig = async () => {
  if (!editConfigPlugin.value) return
  editConfigSaving.value = true

  try {
    const configMap: Record<string, string> = {}
    for (const item of editConfigItems.value) {
      configMap[item.key] = item.value
    }

    const res = await pluginApi.saveConfig(editConfigPlugin.value.id, configMap)
    if (res.success) {
      showSuccess(res.message || '插件配置已保存')
      // 更新 original 值
      for (const item of editConfigItems.value) {
        item.original = item.value
      }
      editConfigVisible.value = false
    } else {
      showError(res.message || '保存失败')
    }
  } catch (error: any) {
    showError(error?.response?.data?.message || error?.message || '保存失败')
  } finally {
    editConfigSaving.value = false
  }
}

const stateColor = (state: string) => {
  switch (state) {
    case 'Running': return 'bg-green-500/20 text-green-400 border-green-500/30'
    case 'Initialized': return 'bg-blue-500/20 text-blue-400 border-blue-500/30'
    case 'Loaded': return 'bg-yellow-500/20 text-yellow-400 border-yellow-500/30'
    case 'Stopped': return 'bg-gray-500/20 text-gray-400 border-gray-500/30'
    case 'Error': return 'bg-red-500/20 text-red-400 border-red-500/30'
    default: return 'bg-gray-500/20 text-gray-500 border-gray-500/30'
  }
}

const stateLabel = (state: string) => {
  switch (state) {
    case 'Running': return '运行中'
    case 'Initialized': return '已初始化'
    case 'Loaded': return '已加载'
    case 'Stopped': return '已停止'
    case 'Error': return '错误'
    case 'Unloaded': return '未加载'
    default: return state
  }
}

const priorityLabel = (priority: string) => {
  switch (priority) {
    case 'Highest': return '最高'
    case 'High': return '高'
    case 'Normal': return '普通'
    case 'Low': return '低'
    case 'Lowest': return '最低'
    default: return priority
  }
}

onMounted(loadPlugins)
</script>

<template>
  <div class="space-y-6">
    <!-- 页面标题 -->
    <div class="flex items-center justify-between">
      <div>
        <h1 class="text-2xl font-bold text-gray-100">插件管理</h1>
        <p class="text-sm text-gray-500 mt-1">管理已加载的插件，控制启用和禁用状态</p>
      </div>
      <button @click="loadPlugins" :disabled="loading"
        class="btn btn-sm btn-secondary flex items-center gap-1.5">
        <span v-if="loading" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin"></span>
        <span>{{ loading ? '加载中...' : '刷新' }}</span>
      </button>
    </div>

    <!-- 加载中 -->
    <div v-if="loading && plugins.length === 0" class="flex items-center justify-center py-20">
      <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
      <span class="ml-3 text-gray-400">加载插件列表...</span>
    </div>

    <!-- 空状态 -->
    <div v-else-if="plugins.length === 0" class="text-center py-20">
      <div class="text-4xl mb-3">🧩</div>
      <p class="text-gray-400">暂无已加载的插件</p>
      <p class="text-sm text-gray-600 mt-1">将插件 DLL 放入 plugins 目录后重启服务</p>
    </div>

    <!-- 插件列表 -->
    <div v-else class="grid gap-4">
      <div v-for="plugin in plugins" :key="plugin.id"
        class="bg-dark-card border border-dark-border rounded-xl p-5 hover:border-gray-600 transition-colors">
        <div class="flex items-start justify-between gap-4">
          <!-- 左侧信息 -->
          <div class="flex-1 min-w-0">
            <div class="flex items-center gap-2 flex-wrap">
              <h3 class="text-base font-semibold text-gray-100">{{ plugin.name }}</h3>
              <span class="text-xs font-mono px-1.5 py-0.5 rounded bg-dark-sidebar text-gray-400 border border-dark-border">
                v{{ plugin.version }}
              </span>
              <span v-if="plugin.isSystem"
                class="text-xs px-1.5 py-0.5 rounded bg-primary-500/15 text-primary-400 border border-primary-500/30">
                系统
              </span>
              <span :class="['text-xs px-1.5 py-0.5 rounded border', stateColor(plugin.state)]">
                {{ stateLabel(plugin.state) }}
              </span>
            </div>
            <p v-if="plugin.description" class="text-sm text-gray-400 mt-1.5">{{ plugin.description }}</p>
            <div class="flex items-center gap-4 mt-2 text-xs text-gray-500">
              <span v-if="plugin.author">作者: {{ plugin.author }}</span>
              <span>优先级: {{ priorityLabel(plugin.priority) }}</span>
              <span class="font-mono text-gray-600">{{ plugin.id }}</span>
            </div>
          </div>

          <!-- 右侧操作 -->
          <div class="flex items-center gap-3 flex-shrink-0 pt-1">
            <!-- 编辑配置按钮 -->
            <button @click="openEditConfig(plugin)"
              class="text-gray-400 hover:text-primary-400 transition-colors p-1" title="编辑配置">
              <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2"
                  d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.066 2.573c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.573 1.066c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.066-2.573c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
              </svg>
            </button>

            <!-- 开关 -->
            <button @click="togglePlugin(plugin)" :disabled="toggling === plugin.id"
              :class="[
                'relative inline-flex h-6 w-11 items-center rounded-full transition-colors duration-200 focus:outline-none',
                plugin.isEnabled ? 'bg-primary-500' : 'bg-gray-600',
                toggling === plugin.id ? 'opacity-50 cursor-wait' : 'cursor-pointer'
              ]">
              <span v-if="toggling === plugin.id"
                class="absolute inset-0 flex items-center justify-center">
                <span class="w-3 h-3 border-2 border-white border-t-transparent rounded-full animate-spin"></span>
              </span>
              <span v-else
                :class="[
                  'inline-block h-4 w-4 transform rounded-full bg-white shadow transition-transform duration-200',
                  plugin.isEnabled ? 'translate-x-6' : 'translate-x-1'
                ]" />
            </button>
          </div>
        </div>
      </div>
    </div>

    <!-- 配置编辑弹窗 -->
    <div v-if="editConfigVisible" class="fixed inset-0 z-50 flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="editConfigVisible = false"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-2xl shadow-2xl w-full max-w-2xl max-h-[80vh] flex flex-col">
        <!-- 弹窗标题 -->
        <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border">
          <div>
            <h2 class="text-lg font-semibold text-gray-100">编辑插件配置</h2>
            <p class="text-xs text-gray-500 mt-0.5">{{ editConfigPlugin?.name }} ({{ editConfigPlugin?.id }})</p>
          </div>
          <button @click="editConfigVisible = false" class="text-gray-400 hover:text-gray-200 transition-colors">
            <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        <!-- 配置列表 -->
        <div class="flex-1 overflow-y-auto px-6 py-4 space-y-3">
          <!-- 加载中 -->
          <div v-if="editConfigLoading" class="flex items-center justify-center py-12">
            <span class="w-5 h-5 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
            <span class="ml-3 text-gray-400">加载配置...</span>
          </div>

          <!-- 无配置 -->
          <div v-else-if="editConfigItems.length === 0" class="text-center py-12">
            <p class="text-gray-500">该插件暂无可编辑的配置项</p>
          </div>

          <!-- 配置项 -->
          <div v-else>
            <div v-for="item in editConfigItems" :key="item.key"
              class="flex items-center gap-3 py-2 border-b border-dark-border/50 last:border-0">
              <label class="w-2/5 text-sm text-gray-300 font-mono truncate flex-shrink-0" :title="item.key">
                {{ item.key }}
              </label>
              <div class="flex-1">
                <input v-model="item.value" type="text"
                  :class="[
                    'w-full bg-dark-sidebar border rounded-lg px-3 py-1.5 text-sm text-gray-200 focus:outline-none focus:ring-1 focus:ring-primary-500 focus:border-primary-500',
                    item.value !== item.original ? 'border-primary-500/50' : 'border-dark-border'
                  ]"
                  :placeholder="item.original" />
              </div>
            </div>
          </div>
        </div>

        <!-- 底部按钮 -->
        <div class="flex items-center justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="editConfigVisible = false"
            class="btn btn-sm btn-secondary">
            取消
          </button>
          <button @click="saveConfig"
            :disabled="editConfigSaving || !hasConfigChanges"
            :class="[
              'btn btn-sm',
              hasConfigChanges ? 'btn-primary' : 'btn-secondary opacity-50 cursor-not-allowed'
            ]">
            <span v-if="editConfigSaving" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin mr-1.5"></span>
            {{ editConfigSaving ? '保存中...' : '保存配置' }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
