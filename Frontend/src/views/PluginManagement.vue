<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import { pluginApi } from '@/api'
import type { PluginItem } from '@/api'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()

const plugins = ref<PluginItem[]>([])
const loading = ref(true)
const toggling = ref<string | null>(null)

// =============== 配置编辑数据结构 ===============
interface SimpleItem { type: 'simple'; key: string; value: string; defaultValue: string }
interface ArrayItem  { type: 'array';  key: string; items: string[]; defaultItems: string[] }
interface DictItem   { type: 'dict';   key: string; entries: { key: string; value: string }[]; defaultEntries: { key: string; value: string }[] }
type ConfigItem = SimpleItem | ArrayItem | DictItem

const editConfigVisible = ref(false)
const editConfigPlugin = ref<PluginItem | null>(null)
const editConfigLoading = ref(false)
const editConfigSaving = ref(false)
const editConfigItems = ref<ConfigItem[]>([])
// 保存打开时的快照用于变更检测
const editConfigSnapshot = ref('')

const hasConfigChanges = computed(() => {
  return JSON.stringify(flattenItems(editConfigItems.value)) !== editConfigSnapshot.value
})

// =============== flat → 结构化 ===============
function parseFlatToStructured(flat: Record<string, string>, defaults: Record<string, string>): ConfigItem[] {
  // 按顶层 key 分组
  const groups = new Map<string, { sub: Map<string, string>; defSub: Map<string, string> }>()

  const addToGroup = (source: Record<string, string>, target: 'sub' | 'defSub') => {
    for (const [fullKey, val] of Object.entries(source)) {
      const colonIdx = fullKey.indexOf(':')
      if (colonIdx === -1) {
        // 简单 key
        if (!groups.has(fullKey)) groups.set(fullKey, { sub: new Map(), defSub: new Map() })
        groups.get(fullKey)![target].set('', val)
      } else {
        const topKey = fullKey.substring(0, colonIdx)
        const subKey = fullKey.substring(colonIdx + 1)
        if (!groups.has(topKey)) groups.set(topKey, { sub: new Map(), defSub: new Map() })
        groups.get(topKey)![target].set(subKey, val)
      }
    }
  }

  addToGroup(defaults, 'defSub')
  addToGroup(flat, 'sub')

  const result: ConfigItem[] = []

  // 按 defaults 顺序优先，再加 flat 中新增的
  const orderedKeys: string[] = []
  const seen = new Set<string>()
  for (const key of [...Object.keys(defaults), ...Object.keys(flat)]) {
    const topKey = key.indexOf(':') === -1 ? key : key.substring(0, key.indexOf(':'))
    if (!seen.has(topKey)) { seen.add(topKey); orderedKeys.push(topKey) }
  }

  for (const topKey of orderedKeys) {
    const g = groups.get(topKey)
    if (!g) continue

    // 合并 sub 和 defSub
    const allSub = new Map(g.defSub)
    for (const [k, v] of g.sub) allSub.set(k, v)

    if (allSub.size === 1 && allSub.has('')) {
      // 简单值
      result.push({
        type: 'simple',
        key: topKey,
        value: g.sub.get('') ?? g.defSub.get('') ?? '',
        defaultValue: g.defSub.get('') ?? '',
      })
    } else {
      // 判断是数组还是字典：所有子 key 都是数字→数组
      const subKeys = [...allSub.keys()].filter(k => k !== '')
      const isArray = subKeys.length > 0 && subKeys.every(k => /^\d+$/.test(k))

      if (isArray) {
        const maxIdx = Math.max(...subKeys.map(Number))
        const items: string[] = []
        const defaultItems: string[] = []
        for (let i = 0; i <= maxIdx; i++) {
          items.push(allSub.get(String(i)) ?? '')
          defaultItems.push(g.defSub.get(String(i)) ?? '')
        }
        result.push({ type: 'array', key: topKey, items, defaultItems })
      } else {
        const entries: { key: string; value: string }[] = []
        const defaultEntries: { key: string; value: string }[] = []
        for (const [k, v] of allSub) {
          if (k === '') continue
          entries.push({ key: k, value: v })
        }
        for (const [k, v] of g.defSub) {
          if (k === '') continue
          defaultEntries.push({ key: k, value: v })
        }
        result.push({ type: 'dict', key: topKey, entries, defaultEntries })
      }
    }
  }

  return result
}

// =============== 结构化 → flat ===============
function flattenItems(items: ConfigItem[]): Record<string, string> {
  const result: Record<string, string> = {}
  for (const item of items) {
    if (item.type === 'simple') {
      result[item.key] = item.value
    } else if (item.type === 'array') {
      for (let i = 0; i < item.items.length; i++) {
        result[`${item.key}:${i}`] = item.items[i]
      }
    } else if (item.type === 'dict') {
      for (const entry of item.entries) {
        if (entry.key) result[`${item.key}:${entry.key}`] = entry.value
      }
    }
  }
  return result
}

// =============== 插件列表 ===============
const loadPlugins = async () => {
  loading.value = true
  try {
    const res = await pluginApi.getPlugins()
    if (res.success) plugins.value = res.plugins
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

// =============== 配置编辑 ===============
const openEditConfig = async (plugin: PluginItem) => {
  editConfigPlugin.value = plugin
  editConfigVisible.value = true
  editConfigLoading.value = true
  editConfigItems.value = []

  try {
    const res = await pluginApi.getConfig(plugin.id)
    if (res.success) {
      const items = parseFlatToStructured(res.config || {}, res.defaults || {})
      editConfigItems.value = items
      editConfigSnapshot.value = JSON.stringify(flattenItems(items))
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
    const configMap = flattenItems(editConfigItems.value)
    const res = await pluginApi.saveConfig(editConfigPlugin.value.id, configMap)
    if (res.success) {
      showSuccess(res.message || '插件配置已保存')
      editConfigSnapshot.value = JSON.stringify(configMap)
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

// =============== 数组操作 ===============
const addArrayItem = (item: ArrayItem) => { item.items.push('') }
const removeArrayItem = (item: ArrayItem, idx: number) => { item.items.splice(idx, 1) }

// =============== 字典操作 ===============
const addDictEntry = (item: DictItem) => { item.entries.push({ key: '', value: '' }) }
const removeDictEntry = (item: DictItem, idx: number) => { item.entries.splice(idx, 1) }

// =============== 重置 ===============
const resetSimple = (item: SimpleItem) => { item.value = item.defaultValue }
const resetArray = (item: ArrayItem) => { item.items = [...item.defaultItems] }
const resetDict = (item: DictItem) => { item.entries = item.defaultEntries.map(e => ({ ...e })) }

const isSimpleModified = (item: SimpleItem) => item.defaultValue !== '' && item.value !== item.defaultValue
const isArrayModified = (item: ArrayItem) => JSON.stringify(item.items) !== JSON.stringify(item.defaultItems)
const isDictModified = (item: DictItem) => JSON.stringify(item.entries) !== JSON.stringify(item.defaultEntries)

// =============== 布尔检测 ===============
const isBoolValue = (val: string) => val === 'true' || val === 'false'

// =============== 显示辅助 ===============
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
          <div class="flex items-center gap-3 flex-shrink-0 pt-1">
            <button v-if="plugin.hasOptions" @click="openEditConfig(plugin)"
              class="text-gray-400 hover:text-primary-400 transition-colors p-1" title="编辑配置">
              <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2"
                  d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.066 2.573c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.573 1.066c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.066-2.573c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
              </svg>
            </button>
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
        <!-- 标题 -->
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
        <div class="flex-1 overflow-y-auto px-6 py-4 space-y-4">
          <div v-if="editConfigLoading" class="flex items-center justify-center py-12">
            <span class="w-5 h-5 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
            <span class="ml-3 text-gray-400">加载配置...</span>
          </div>

          <div v-else-if="editConfigItems.length === 0" class="text-center py-12">
            <p class="text-gray-500">该插件暂无可编辑的配置项</p>
          </div>

          <template v-else v-for="item in editConfigItems" :key="item.key">
            <!-- ====== 简单值 ====== -->
            <div v-if="item.type === 'simple'" class="flex items-center gap-3">
              <label class="w-2/5 text-sm text-gray-300 font-mono truncate flex-shrink-0" :title="item.key">
                {{ item.key }}
              </label>
              <div class="flex-1 flex items-center gap-2">
                <!-- 布尔值：下拉选择 -->
                <select v-if="isBoolValue((item as SimpleItem).defaultValue || (item as SimpleItem).value)"
                  v-model="(item as SimpleItem).value"
                  :class="[
                    'w-full bg-dark-sidebar border rounded-lg px-3 py-1.5 text-sm text-gray-200 focus:outline-none focus:ring-1 focus:ring-primary-500 focus:border-primary-500',
                    isSimpleModified(item as SimpleItem) ? 'border-yellow-500/40' : 'border-dark-border'
                  ]">
                  <option value="true">true</option>
                  <option value="false">false</option>
                </select>
                <!-- 普通值：文本输入 -->
                <input v-else v-model="(item as SimpleItem).value" type="text"
                  :class="[
                    'w-full bg-dark-sidebar border rounded-lg px-3 py-1.5 text-sm text-gray-200 focus:outline-none focus:ring-1 focus:ring-primary-500 focus:border-primary-500',
                    isSimpleModified(item as SimpleItem) ? 'border-yellow-500/40' : 'border-dark-border'
                  ]"
                  :placeholder="(item as SimpleItem).defaultValue || ''" />
                <button v-if="isSimpleModified(item as SimpleItem)" @click="resetSimple(item as SimpleItem)"
                  class="flex-shrink-0 text-gray-500 hover:text-yellow-400 transition-colors p-1"
                  :title="`重置为默认值: ${(item as SimpleItem).defaultValue}`">
                  <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2"
                      d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                  </svg>
                </button>
              </div>
            </div>

            <!-- ====== 数组 ====== -->
            <div v-else-if="item.type === 'array'"
              class="border border-dark-border/60 rounded-lg p-3 space-y-2">
              <div class="flex items-center justify-between">
                <span class="text-sm text-gray-300 font-mono">{{ item.key }}
                  <span class="text-xs text-gray-600 ml-1">Array[{{ (item as ArrayItem).items.length }}]</span>
                </span>
                <div class="flex items-center gap-2">
                  <button v-if="isArrayModified(item as ArrayItem)" @click="resetArray(item as ArrayItem)"
                    class="text-gray-500 hover:text-yellow-400 transition-colors p-0.5" title="重置为默认值">
                    <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2"
                        d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                    </svg>
                  </button>
                  <button @click="addArrayItem(item as ArrayItem)"
                    class="text-xs text-primary-400 hover:text-primary-300 transition-colors">+ 添加</button>
                </div>
              </div>
              <div v-for="(_val, idx) in (item as ArrayItem).items" :key="idx"
                class="flex items-center gap-2">
                <span class="text-xs text-gray-600 w-6 text-right flex-shrink-0">{{ idx }}</span>
                <input v-model="(item as ArrayItem).items[idx]" type="text"
                  class="flex-1 bg-dark-sidebar border border-dark-border rounded-lg px-3 py-1 text-sm text-gray-200 focus:outline-none focus:ring-1 focus:ring-primary-500 focus:border-primary-500" />
                <button @click="removeArrayItem(item as ArrayItem, idx)"
                  class="text-gray-600 hover:text-red-400 transition-colors p-0.5 flex-shrink-0">
                  <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                  </svg>
                </button>
              </div>
              <div v-if="(item as ArrayItem).items.length === 0" class="text-xs text-gray-600 text-center py-1">空数组</div>
            </div>

            <!-- ====== 字典 ====== -->
            <div v-else-if="item.type === 'dict'"
              class="border border-dark-border/60 rounded-lg p-3 space-y-2">
              <div class="flex items-center justify-between">
                <span class="text-sm text-gray-300 font-mono">{{ item.key }}
                  <span class="text-xs text-gray-600 ml-1">Dict[{{ (item as DictItem).entries.length }}]</span>
                </span>
                <div class="flex items-center gap-2">
                  <button v-if="isDictModified(item as DictItem)" @click="resetDict(item as DictItem)"
                    class="text-gray-500 hover:text-yellow-400 transition-colors p-0.5" title="重置为默认值">
                    <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2"
                        d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                    </svg>
                  </button>
                  <button @click="addDictEntry(item as DictItem)"
                    class="text-xs text-primary-400 hover:text-primary-300 transition-colors">+ 添加</button>
                </div>
              </div>
              <div v-for="(entry, idx) in (item as DictItem).entries" :key="idx"
                class="flex items-center gap-2">
                <input v-model="entry.key" type="text" placeholder="Key"
                  class="w-2/5 bg-dark-sidebar border border-dark-border rounded-lg px-3 py-1 text-sm text-gray-200 focus:outline-none focus:ring-1 focus:ring-primary-500 focus:border-primary-500 font-mono" />
                <input v-model="entry.value" type="text" placeholder="Value"
                  class="flex-1 bg-dark-sidebar border border-dark-border rounded-lg px-3 py-1 text-sm text-gray-200 focus:outline-none focus:ring-1 focus:ring-primary-500 focus:border-primary-500" />
                <button @click="removeDictEntry(item as DictItem, idx)"
                  class="text-gray-600 hover:text-red-400 transition-colors p-0.5 flex-shrink-0">
                  <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                  </svg>
                </button>
              </div>
              <div v-if="(item as DictItem).entries.length === 0" class="text-xs text-gray-600 text-center py-1">空字典</div>
            </div>
          </template>
        </div>

        <!-- 底部按钮 -->
        <div class="flex items-center justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="editConfigVisible = false" class="btn btn-sm btn-secondary">取消</button>
          <button @click="saveConfig"
            :disabled="editConfigSaving || !hasConfigChanges"
            :class="['btn btn-sm', hasConfigChanges ? 'btn-primary' : 'btn-secondary opacity-50 cursor-not-allowed']">
            <span v-if="editConfigSaving" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin mr-1.5"></span>
            {{ editConfigSaving ? '保存中...' : '保存配置' }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
