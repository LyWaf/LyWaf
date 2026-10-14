<script setup lang="ts">
import { fieldLabel, fieldDescription, registerPluginMessages, pluginMessage, t } from '@/i18n'
import { refreshPluginMenus } from '@/composables/usePluginMenus'
import { ref, onMounted, onUnmounted, computed } from 'vue'
import { pluginApi } from '@/api'
import ListToolbar from '@/components/common/ListToolbar.vue'
import type { PluginItem, PluginLogEntry, PluginLogFile, PluginActionItem, PluginConfigurationField } from '@/api'
import { useToast } from '@/composables/useToast'
import { useRouter } from 'vue-router'

const { showSuccess, showError } = useToast()
const router = useRouter()

const plugins = ref<PluginItem[]>([])
const page = ref(1)
const search = ref('')
const total = ref(0)
let listRequest = 0
onUnmounted(() => { listRequest++ })
const loading = ref(true)
const toggling = ref<string | null>(null)
const packageInput = ref<HTMLInputElement | null>(null)
const installingPackage = ref(false)
const rollingBack = ref<string | null>(null)

// =============== 配置编辑数据结构 ===============
interface SimpleItem { type: 'simple'; key: string; value: string; defaultValue: string }
interface ArrayItem  { type: 'array';  key: string; items: string[]; defaultItems: string[] }
interface DictItem   { type: 'dict';   key: string; entries: { key: string; value: string }[]; defaultEntries: { key: string; value: string }[] }
interface ObjArrayItem { type: 'object-array'; key: string; fields: string[]; items: Record<string, string>[]; defaultItems: Record<string, string>[]; fieldDefaultValues: Record<string, string> }
type ConfigItem = SimpleItem | ArrayItem | DictItem | ObjArrayItem

const editConfigVisible = ref(false)
const editConfigPlugin = ref<PluginItem | null>(null)
const editConfigLoading = ref(false)
const editConfigSaving = ref(false)
const editConfigValidating = ref(false)
const editConfigErrors = ref<Record<string, string[]>>({})
const editConfigSchema = ref<Record<string, PluginConfigurationField>>({})
const supportsScopedConfig = ref(false)
const editConfigVersion = ref('')
const effectiveHost = ref('')
const effectivePath = ref('/')
const effectiveLoading = ref(false)
const effectiveResult = ref<{ config: Record<string, string>; sources: Record<string, string>; lastReloadError?: string } | null>(null)

const queryEffectiveConfig = async () => {
  if (!editConfigPlugin.value) return
  effectiveLoading.value = true
  effectiveResult.value = null
  try { effectiveResult.value = await pluginApi.getEffectiveConfig(editConfigPlugin.value.id, effectiveHost.value, effectivePath.value) }
  catch (error: any) { showError(error?.message || t('page.82fc0fe6d08f')) }
  finally { effectiveLoading.value = false }
}
const editConfigItems = ref<ConfigItem[]>([])
const editConfigLabels = ref<Record<string, string>>({})
const editConfigFieldDefaults = ref<Record<string, string>>({})
// 保存打开时的快照用于变更检测
const editConfigSnapshot = ref('')

const hasConfigChanges = computed(() => {
  return JSON.stringify(flattenItems(editConfigItems.value)) !== editConfigSnapshot.value
})

// =============== flat → 结构化 ===============
function parseFlatToStructured(flat: Record<string, string>, defaults: Record<string, string>, labels: Record<string, string>, fieldDefaults: Record<string, string>, schema: Record<string, PluginConfigurationField> = {}): ConfigItem[] {
  // 按顶层 key 分组
  const groups = new Map<string, { sub: Map<string, string>; defSub: Map<string, string> }>()

  const addToGroup = (source: Record<string, string>, target: 'sub' | 'defSub') => {
    for (const [fullKey, val] of Object.entries(source)) {
      const colonIdx = fullKey.indexOf(':')
      if (colonIdx === -1) {
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

  // 按 defaults 顺序优先，再加 flat 中新增的，最后从 labels 补充（保持声明顺序）
  const orderedKeys: string[] = []
  const seen = new Set<string>()
  for (const key of [...Object.keys(defaults), ...Object.keys(flat)]) {
    const topKey = key.indexOf(':') === -1 ? key : key.substring(0, key.indexOf(':'))
    if (!seen.has(topKey)) { seen.add(topKey); orderedKeys.push(topKey) }
  }
  for (const key of Object.keys(labels)) {
    const topKey = key.indexOf(':') === -1 ? key : key.substring(0, key.indexOf(':'))
    if (!seen.has(topKey)) { seen.add(topKey); orderedKeys.push(topKey) }
  }

  for (const topKey of orderedKeys) {
    const g = groups.get(topKey)
    if (!g) {
      // 无数据，检查 labels 是否暗示对象数组（如空的 List<MatchRule>）
      const childFields: string[] = []
      for (const labelKey of Object.keys(labels)) {
        if (labelKey.startsWith(topKey + ':')) {
          const field = labelKey.substring(topKey.length + 1)
          if (!field.includes(':')) childFields.push(field)
        }
      }
      if (childFields.length > 0 && labels[topKey]) {
        // 从 fieldDefaults 提取此对象数组的字段默认值
        const fDefs: Record<string, string> = {}
        for (const f of childFields) {
          const fdKey = `${topKey}:${f}`
          if (fdKey in fieldDefaults) fDefs[f] = fieldDefaults[fdKey]
        }
        result.push({ type: 'object-array', key: topKey, fields: childFields, items: [], defaultItems: [], fieldDefaultValues: fDefs })
      }
      continue
    }

    const allSub = new Map(g.sub.size ? g.sub : g.defSub)
    const shape = new Map([...g.defSub, ...g.sub])
    const type = schema[topKey]?.type || ''
    const listType = type.startsWith('List') || type.endsWith('[]')
    const dictionaryType = type.startsWith('Dictionary')
    const objectFields = Object.keys(labels).some(key => key.startsWith(topKey + ':'))

    if (shape.size === 1 && shape.has('') && !listType && !dictionaryType && !objectFields) {
      result.push({
        type: 'simple',
        key: topKey,
        value: g.sub.get('') ?? g.defSub.get('') ?? '',
        defaultValue: g.defSub.get('') ?? '',
      })
    } else {
      const subKeys = [...shape.keys()].filter(k => k !== '')

      // 对象数组：子 key 格式为 "N:field"
      const isObjectArray = subKeys.length > 0 ? subKeys.every(k => /^\d+:.+/.test(k)) : objectFields && !dictionaryType
      // 简单数组：子 key 全是数字
      const isArray = !isObjectArray && (listType || subKeys.length > 0 && subKeys.every(k => /^\d+$/.test(k)))

      if (isObjectArray) {
        result.push(parseObjectArray(topKey, allSub, g.defSub, labels, fieldDefaults))
      } else if (isArray) {
        const arrayValues = (values: Map<string, string>) => [...values].filter(([key]) => /^\d+$/.test(key))
          .sort(([a], [b]) => Number(a) - Number(b)).map(([, value]) => value)
        const items = arrayValues(allSub)
        const defaultItems = arrayValues(g.defSub)
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

// =============== 解析对象数组 ===============
function parseObjectArray(topKey: string, allSub: Map<string, string>, defSub: Map<string, string>, labels: Record<string, string>, fieldDefaults: Record<string, string>): ObjArrayItem {
  const itemMap = new Map<number, Record<string, string>>()
  const defaultItemMap = new Map<number, Record<string, string>>()
  const fieldSet = new Set<string>()

  for (const [subKey, val] of allSub) {
    if (subKey === '') continue
    const match = subKey.match(/^(\d+):(.+)$/)
    if (!match) continue
    const idx = parseInt(match[1])
    const field = match[2]
    fieldSet.add(field)
    if (!itemMap.has(idx)) itemMap.set(idx, {})
    itemMap.get(idx)![field] = val
  }

  for (const [subKey, val] of defSub) {
    if (subKey === '') continue
    const match = subKey.match(/^(\d+):(.+)$/)
    if (!match) continue
    const idx = parseInt(match[1])
    const field = match[2]
    fieldSet.add(field)
    if (!defaultItemMap.has(idx)) defaultItemMap.set(idx, {})
    defaultItemMap.get(idx)![field] = val
  }

  // 也从 labels 中推断字段（标签中有 "TopKey:Field" 格式）
  for (const labelKey of Object.keys(labels)) {
    if (labelKey.startsWith(topKey + ':')) {
      const field = labelKey.substring(topKey.length + 1)
      if (!field.includes(':')) fieldSet.add(field)
    }
  }

  const fields = [...fieldSet]
  const items = [...itemMap.entries()].sort((a, b) => a[0] - b[0]).map(([_, obj]) => {
    const full: Record<string, string> = {}
    for (const f of fields) full[f] = obj[f] ?? ''
    return full
  })
  const defaultItems = [...defaultItemMap.entries()].sort((a, b) => a[0] - b[0]).map(([_, obj]) => {
    const full: Record<string, string> = {}
    for (const f of fields) full[f] = obj[f] ?? ''
    return full
  })

  // 提取字段默认值
  const fDefs: Record<string, string> = {}
  for (const f of fields) {
    const fdKey = `${topKey}:${f}`
    if (fdKey in fieldDefaults) fDefs[f] = fieldDefaults[fdKey]
  }

  return { type: 'object-array', key: topKey, fields, items, defaultItems, fieldDefaultValues: fDefs }
}

// =============== 结构化 → flat ===============
function flattenItems(items: ConfigItem[]): Record<string, string> {
  const result: Record<string, string> = {}
  for (const item of items) {
    if (item.type === 'simple') {
      result[item.key] = item.value
    } else if (item.type === 'array') {
      if (item.items.length === 0) result[item.key] = ''
      for (let i = 0; i < item.items.length; i++) {
        result[`${item.key}:${i}`] = item.items[i]
      }
    } else if (item.type === 'dict') {
      if (!item.entries.some(entry => entry.key)) result[item.key] = ''
      for (const entry of item.entries) {
        if (entry.key) result[`${item.key}:${entry.key}`] = entry.value
      }
    } else if (item.type === 'object-array') {
      let idx = 0
      for (const obj of item.items) {
        // 跳过全空规则：只看非 bool 字段是否为空
        const hasNonEmptyCondition = Object.entries(obj).some(
          ([f, v]) => v.trim() && !isBoolValue(item.fieldDefaultValues[f] ?? '')
        )
        if (!hasNonEmptyCondition) continue
        for (const [field, value] of Object.entries(obj)) {
          result[`${item.key}:${idx}:${field}`] = value
        }
        idx++
      }
      if (idx === 0) result[item.key] = ''
    }
  }
  return result
}

// =============== 插件列表 ===============
const loadPlugins = async () => {
  const request = ++listRequest
  loading.value = true
  try {
    const res = await pluginApi.list(page.value, search.value)
    if (request !== listRequest) return
    if (res.success) {
      plugins.value = res.data
      for (const plugin of res.data) registerPluginMessages(plugin.id, plugin.localization)
      total.value = res.total
      if (!res.data.length && page.value > 1) {
        page.value = Math.max(1, Math.ceil(res.total / 20))
        await loadPlugins()
      }
    }
  } catch {
    if (request === listRequest) showError(t('page.20ac1210598c'))
  } finally {
    if (request === listRequest) loading.value = false
  }
}

const togglePlugin = async (plugin: PluginItem) => {
  toggling.value = plugin.id
  try {
    const res = await pluginApi.togglePlugin(plugin.id)
    if (res.success) {
      plugin.isEnabled = res.isEnabled
        plugin.state = res.state
        await refreshPluginMenus()
      showSuccess(res.message || t('dashboard.operation.successful'))
    } else {
      showError(res.message || t('settings.operation.failed'))
    }
  } catch (error: any) {
    showError(error?.response?.data?.message || error?.message || t('settings.operation.failed'))
  } finally {
    toggling.value = null
  }
}

const installPackage = async (event: Event) => {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) return
  installingPackage.value = true
  try {
    const result = await pluginApi.installPackage(file)
    result.success ? showSuccess(result.message || t('page.bc801c898b36')) : showError(result.message || t('page.a8581cc00ff3'))
  } catch (error: any) {
    showError(error?.response?.data?.message || error?.message || t('page.a8581cc00ff3'))
  } finally {
    installingPackage.value = false
    input.value = ''
  }
}

const rollbackPlugin = async (plugin: PluginItem) => {
  rollingBack.value = plugin.id
  try {
    const result = await pluginApi.rollback(plugin.id)
    result.success ? showSuccess(result.message || t('page.25a352766461')) : showError(result.message || t('page.c859b98a0150'))
  } catch (error: any) {
    showError(error?.response?.data?.message || error?.message || t('page.c859b98a0150'))
  } finally {
    rollingBack.value = null
  }
}

// =============== 配置编辑 ===============
const openEditConfig = async (plugin: PluginItem) => {
  editConfigPlugin.value = plugin
  editConfigVisible.value = true
  editConfigLoading.value = true
  editConfigItems.value = []
  editConfigErrors.value = {}
  editConfigSchema.value = {}
  supportsScopedConfig.value = false
  editConfigVersion.value = ''
  effectiveResult.value = null

  try {
    const res = await pluginApi.getConfig(plugin.id)
    if (res.success) {
      supportsScopedConfig.value = res.supportsScopedConfiguration === true
      editConfigVersion.value = res.version
      const lbls = res.labels || {}
      registerPluginMessages(plugin.id, res.localization)
      const fDefs = res.fieldDefaults || {}
      editConfigLabels.value = lbls
      editConfigFieldDefaults.value = fDefs
      editConfigSchema.value = Object.fromEntries((res.schema || []).map(field => [field.path, field]))
      const visibleConfig = { ...(res.config || {}) }
      if (plugin.id === 'http-upload') visibleConfig.Key = ''
      const items = parseFlatToStructured(visibleConfig, res.defaults || {}, lbls, fDefs, editConfigSchema.value)
      editConfigItems.value = items
      editConfigSnapshot.value = JSON.stringify(flattenItems(items))
    } else {
      showError(t('page.04773368668f'))
    }
  } catch {
    showError(t('page.04773368668f'))
  } finally {
    editConfigLoading.value = false
  }
}

const saveConfig = async () => {
  if (!editConfigPlugin.value) return
  editConfigSaving.value = true
  editConfigErrors.value = {}
  try {
    const configMap = flattenItems(editConfigItems.value)
    const res = await pluginApi.saveConfig(editConfigPlugin.value.id, configMap, editConfigVersion.value)
    if (res.success) {
      showSuccess(res.message || t('page.866cc0a9f3e3'))
      editConfigSnapshot.value = JSON.stringify(configMap)
      editConfigVisible.value = false
    } else {
      showError(res.message || t('ui.save.failed'))
    }
  } catch (error: any) {
    editConfigErrors.value = error?.response?.data?.error?.details || {}
    showError(error?.response?.data?.message || error?.message || t('ui.save.failed'))
  } finally {
    editConfigSaving.value = false
  }
}

const validateConfig = async () => {
  if (!editConfigPlugin.value) return
  editConfigValidating.value = true
  editConfigErrors.value = {}
  try {
    await pluginApi.validateConfig(editConfigPlugin.value.id, flattenItems(editConfigItems.value))
    showSuccess(t('page.49ba85832c76'))
  } catch (error: any) {
    editConfigErrors.value = error?.response?.data?.error?.details || {}
    showError(error?.response?.data?.message || error?.message || t('page.5659eb393c4d'))
  } finally { editConfigValidating.value = false }
}

// =============== 数组操作 ===============
const addArrayItem = (item: ArrayItem) => { item.items.push('') }
const removeArrayItem = (item: ArrayItem, idx: number) => { item.items.splice(idx, 1) }

// =============== 字典操作 ===============
const addDictEntry = (item: DictItem) => { item.entries.push({ key: '', value: '' }) }
const removeDictEntry = (item: DictItem, idx: number) => { item.entries.splice(idx, 1) }

// =============== 对象数组操作 ===============
const addObjArrayItem = (item: ObjArrayItem) => {
  const obj: Record<string, string> = {}
  for (const f of item.fields) obj[f] = item.fieldDefaultValues[f] ?? ''
  item.items.push(obj)
}
const removeObjArrayItem = (item: ObjArrayItem, idx: number) => { item.items.splice(idx, 1) }
const moveObjArrayItem = (item: ObjArrayItem, idx: number, dir: -1 | 1) => {
  const target = idx + dir
  if (target < 0 || target >= item.items.length) return
  const tmp = item.items[idx]
  item.items[idx] = item.items[target]
  item.items[target] = tmp
}

// =============== 重置 ===============
const resetSimple = (item: SimpleItem) => { item.value = item.defaultValue }
const resetArray = (item: ArrayItem) => { item.items = [...item.defaultItems] }
const resetDict = (item: DictItem) => { item.entries = item.defaultEntries.map(e => ({ ...e })) }
const resetObjArray = (item: ObjArrayItem) => { item.items = item.defaultItems.map(obj => ({ ...obj })) }

const isSimpleModified = (item: SimpleItem) => item.defaultValue !== '' && item.value !== item.defaultValue
const isArrayModified = (item: ArrayItem) => JSON.stringify(item.items) !== JSON.stringify(item.defaultItems)
const isDictModified = (item: DictItem) => JSON.stringify(item.entries) !== JSON.stringify(item.defaultEntries)
const isObjArrayModified = (item: ObjArrayItem) => JSON.stringify(item.items) !== JSON.stringify(item.defaultItems)

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
    case 'Running': return t('common.running')
    case 'Initialized': return t('common.initialized')
    case 'Loaded': return t('page.6bdcbfd2f640')
    case 'Stopped': return t('common.stopped')
    case 'Error': return t('common.error')
    case 'Unloaded': return t('common.notLoaded')
    default: return state
  }
}
const priorityLabel = (priority: string) => {
  switch (priority) {
    case 'Highest': return t('page.8672864e9041')
    case 'High': return t('dashboard.high')
    case 'Normal': return t('page.de907d10df98')
    case 'Low': return t('dashboard.low')
    case 'Lowest': return t('page.8b5f2a61b797')
    default: return priority
  }
}

// =============== 插件操作 — 日志查看器 ===============
const logViewerVisible = ref(false)
const logViewerPlugin = ref<PluginItem | null>(null)
const logViewerAction = ref<PluginActionItem | null>(null)
const logViewerLoading = ref(false)

// 文件列表
const logFiles = ref<PluginLogFile[]>([])
const logSelectedFile = ref('')

// 日志条目
const logEntries = ref<PluginLogEntry[]>([])
const logTotal = ref(0)
const logOffset = ref(0)
const logLimit = ref(20)
const logFileSize = ref(0)
const logSearch = ref('')
const logExpandedEntry = ref<number | null>(null)

const logTotalPages = computed(() => Math.ceil(logTotal.value / logLimit.value) || 1)
const logCurrentPage = computed(() => Math.floor(logOffset.value / logLimit.value) + 1)

const openLogViewer = async (plugin: PluginItem, action: PluginActionItem) => {
  logViewerPlugin.value = plugin
  logViewerAction.value = action
  logViewerVisible.value = true
  logFiles.value = []
  logEntries.value = []
  logSelectedFile.value = ''
  logTotal.value = 0
  logSearch.value = ''
  logExpandedEntry.value = null
  await loadLogFiles()
}

const loadLogFiles = async () => {
  if (!logViewerPlugin.value || !logViewerAction.value) return
  logViewerLoading.value = true
  try {
    const res = await pluginApi.executeAction(logViewerPlugin.value.id, logViewerAction.value.id, { action: 'list-files' })
    if (res.success && res.data?.files) {
      logFiles.value = res.data.files
      // 自动选中第一个文件
      if (logFiles.value.length > 0 && !logSelectedFile.value) {
        logSelectedFile.value = logFiles.value[0].name
        await loadLogEntries()
      }
    }
  } catch {
    showError(t('page.58c3a0232798'))
  } finally {
    logViewerLoading.value = false
  }
}

const loadLogEntries = async (resetOffset = true) => {
  if (!logViewerPlugin.value || !logViewerAction.value || !logSelectedFile.value) return
  logViewerLoading.value = true
  if (resetOffset) {
    logOffset.value = 0
    logExpandedEntry.value = null
  }
  try {
    const params: Record<string, string> = {
      action: 'read-entries',
      file: logSelectedFile.value,
      offset: String(logOffset.value),
      limit: String(logLimit.value),
    }
    if (logSearch.value.trim()) params.search = logSearch.value.trim()

    const res = await pluginApi.executeAction(logViewerPlugin.value.id, logViewerAction.value.id, params)
    if (res.success && res.data) {
      logEntries.value = res.data.entries || []
      logTotal.value = res.data.total || 0
      logFileSize.value = res.data.fileSize || 0
    } else {
      logEntries.value = []
      logTotal.value = 0
      showError(res.message || t('page.c9d4f6cd74ec'))
    }
  } catch {
    logEntries.value = []
    logTotal.value = 0
  } finally {
    logViewerLoading.value = false
  }
}

const logGotoPage = (page: number) => {
  if (page < 1 || page > logTotalPages.value) return
  logOffset.value = (page - 1) * logLimit.value
  logExpandedEntry.value = null
  loadLogEntries(false)
}

const logToggleExpand = (index: number) => {
  logExpandedEntry.value = logExpandedEntry.value === index ? null : index
}

const logSelectFile = (fileName: string) => {
  logSelectedFile.value = fileName
  loadLogEntries()
}

const deleteLogFile = async (fileName: string) => {
  if (!logViewerPlugin.value || !logViewerAction.value) return
  if (!confirm(t('page.798001116e08', { p0: fileName }))) return
  try {
    const res = await pluginApi.executeAction(logViewerPlugin.value.id, logViewerAction.value.id, { action: 'delete-file', file: fileName })
    if (res.success) {
      showSuccess(res.message || t('page.077a6d37719a'))
      logFiles.value = logFiles.value.filter(f => f.name !== fileName)
      if (logSelectedFile.value === fileName) {
        logSelectedFile.value = logFiles.value.length > 0 ? logFiles.value[0].name : ''
        if (logSelectedFile.value) loadLogEntries()
        else { logEntries.value = []; logTotal.value = 0 }
      }
    } else {
      showError(res.message || t('settings.deletion.failed'))
    }
  } catch {
    showError(t('settings.deletion.failed'))
  }
}

const clearLogFile = async (fileName: string) => {
  if (!logViewerPlugin.value || !logViewerAction.value) return
  if (!confirm(t('page.59cb6bce95db', { p0: fileName }))) return
  try {
    const res = await pluginApi.executeAction(logViewerPlugin.value.id, logViewerAction.value.id, { action: 'clear-file', file: fileName })
    if (res.success) {
      showSuccess(res.message || t('page.9790c6e72a16'))
      if (logSelectedFile.value === fileName) {
        logEntries.value = []
        logTotal.value = 0
        logFileSize.value = 0
      }
      // 刷新文件列表以更新大小
      await loadLogFiles()
    } else {
      showError(res.message || t('dashboard.failed.to.clear'))
    }
  } catch {
    showError(t('dashboard.failed.to.clear'))
  }
}

const deleteLogEntry = async (entry: PluginLogEntry) => {
  if (!logViewerPlugin.value || !logViewerAction.value || !logSelectedFile.value) return
  try {
    const res = await pluginApi.executeAction(logViewerPlugin.value.id, logViewerAction.value.id, {
      action: 'delete-entry',
      file: logSelectedFile.value,
      index: String(entry.index),
    })
    if (res.success) {
      showSuccess(res.message || t('page.077a6d37719a'))
      // 重新加载当前页
      await loadLogEntries(false)
      // 刷新文件列表以更新大小
      const fileIdx = logFiles.value.findIndex(f => f.name === logSelectedFile.value)
      if (fileIdx >= 0 && logFileSize.value > 0) {
        logFiles.value[fileIdx].size = logFileSize.value
      }
    } else {
      showError(res.message || t('settings.deletion.failed'))
    }
  } catch {
    showError(t('page.7ff505f9d1c5'))
  }
}

const deleteAllLogFiles = async () => {
  if (!logViewerPlugin.value || !logViewerAction.value) return
  if (!confirm(t('page.ca696c7ff162', { p0: logFiles.value.length }))) return
  try {
    for (const file of [...logFiles.value]) {
      await pluginApi.executeAction(logViewerPlugin.value.id, logViewerAction.value.id, { action: 'delete-file', file: file.name })
    }
    showSuccess(t('page.b7175a2477f5'))
    logFiles.value = []
    logSelectedFile.value = ''
    logEntries.value = []
    logTotal.value = 0
    logFileSize.value = 0
  } catch {
    showError(t('settings.deletion.failed'))
    await loadLogFiles()
  }
}

const formatFileSize = (bytes: number) => {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

const methodColor = (method: string) => {
  switch (method?.toUpperCase()) {
    case 'GET': return 'text-green-400'
    case 'POST': return 'text-blue-400'
    case 'PUT': return 'text-yellow-400'
    case 'DELETE': return 'text-red-400'
    case 'PATCH': return 'text-purple-400'
    case 'OPTIONS': return 'text-gray-400'
    case 'HEAD': return 'text-cyan-400'
    default: return 'text-gray-300'
  }
}

// =============== 日志查看器 — 右键菜单：复制为 cURL ===============
const logContextMenu = ref<{ show: boolean; x: number; y: number; entry: PluginLogEntry | null }>({
  show: false, x: 0, y: 0, entry: null,
})

const onLogEntryContextMenu = (e: MouseEvent, entry: PluginLogEntry) => {
  e.preventDefault()
  logContextMenu.value = { show: true, x: e.clientX, y: e.clientY, entry }
}

const closeLogContextMenu = () => {
  logContextMenu.value.show = false
}

type CurlFormat = 'bash' | 'cmd' | 'powershell'

const buildCurl = (entry: PluginLogEntry, format: CurlFormat): string => {
  const method = entry.method || 'GET'
  const host = entry.host || 'localhost'
  const url = entry.url || '/'
  const scheme = entry.scheme || (host.includes('://') ? '' : 'http')
  const fullUrl = host.includes('://') ? `${host}${url}` : `${scheme}://${host}${url}`

  const q = format === 'bash' ? "'" : '"'
  const cont = format === 'bash' ? ' \\' : format === 'cmd' ? ' ^' : ' `'
  const exe = format === 'powershell' ? 'curl.exe' : 'curl'

  const parts: string[] = [`${exe} -X ${method} ${q}${fullUrl}${q}`]

  if (entry.headers) {
    for (const line of entry.headers.split('\n')) {
      const colonIdx = line.indexOf(':')
      if (colonIdx > 0) {
        const key = line.substring(0, colonIdx).trim()
        const value = line.substring(colonIdx + 1).trim()
        if (!['host', 'content-length', 'transfer-encoding', 'connection'].some(
          h => key.toLowerCase() === h
        )) {
          const escaped = format === 'bash' ? value.replace(/'/g, "'\\''") : value.replace(/"/g, '\\"')
          parts.push(`  -H ${q}${key}: ${escaped}${q}`)
        }
      }
    }
  }

  if (['POST', 'PUT', 'PATCH'].includes(method.toUpperCase()) && entry.requestBody) {
    const body = format === 'bash'
      ? entry.requestBody.replace(/'/g, "'\\''")
      : entry.requestBody.replace(/"/g, '\\"')
    parts.push(`  -d ${q}${body}${q}`)
  }

  return parts.join(cont + '\n')
}

const copyLogCurl = async (format: CurlFormat) => {
  const entry = logContextMenu.value.entry
  if (!entry) return
  const curl = buildCurl(entry, format)
  try {
    await navigator.clipboard.writeText(curl)
    showSuccess(t('page.f57d3587f369', { p0: format === 'bash' ? 'Bash' : format === 'cmd' ? 'CMD' : 'PowerShell' }))
  } catch {
    showError(t('dashboard.copy.failed'))
  }
  closeLogContextMenu()
}

const onLogDocClick = () => closeLogContextMenu()
onMounted(() => document.addEventListener('click', onLogDocClick))
onUnmounted(() => document.removeEventListener('click', onLogDocClick))

// =============== 通用插件操作（button 类型） ===============
const executePluginAction = async (plugin: PluginItem, action: PluginActionItem) => {
  if (action.type === 'page-link' && action.path) {
    await router.push(action.path)
    return
  }
  if (action.type === 'log-viewer') {
    openLogViewer(plugin, action)
    return
  }
  // button 类型：直接执行
  try {
    const res = await pluginApi.executeAction(plugin.id, action.id)
    if (res.success) {
      showSuccess(res.message || t('dashboard.operation.successful'))
    } else {
      showError(res.message || t('settings.operation.failed'))
    }
  } catch {
    showError(t('settings.operation.failed'))
  }
}

onMounted(loadPlugins)
</script>

<template>
  <div class="space-y-6">
    <!-- 页面标题 -->
    <div class="flex items-center justify-between">
      <div>
        <h1 class="text-2xl font-bold text-gray-100">{{ t('nav.pluginManagement') }}</h1>
        <p class="text-sm text-gray-500 mt-1">{{ t('page.b9eb1e40c92b') }}</p>
      </div>
      <div class="flex items-center gap-2">
        <input ref="packageInput" type="file" accept=".lyplugin" class="hidden" @change="installPackage" />
        <button class="btn btn-sm btn-primary" :disabled="installingPackage" @click="packageInput?.click()">
          {{ installingPackage ? t('page.0c82e67ec5fc') : t('plugins.install') }}
        </button>
        <button @click="loadPlugins" :disabled="loading"
          class="btn btn-sm btn-secondary flex items-center gap-1.5">
          <span v-if="loading" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin"></span>
          <span>{{ loading ? t('common.loading') : t('common.refresh') }}</span>
        </button>
      </div>
    </div>

    <!-- 加载中 -->
    <ListToolbar v-model:page="page" v-model:search="search" :total="total" :busy="loading" @refresh="loadPlugins" />
    <div v-if="loading && plugins.length === 0" class="flex items-center justify-center py-20">
      <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
      <span class="ml-3 text-gray-400">{{ t('page.21fe13ab0638') }}</span>
    </div>

    <!-- 空状态 -->
    <div v-else-if="plugins.length === 0" class="text-center py-20">
      <div class="text-4xl mb-3">🧩</div>
      <p class="text-gray-400">{{ t('page.d812732d943b') }}</p>
      <p class="text-sm text-gray-600 mt-1">{{ t('page.39ee2dbbf287') }}</p>
    </div>

    <!-- 插件列表 -->
    <div v-else class="grid gap-4">
      <div v-for="plugin in plugins" :key="plugin.id"
        class="bg-dark-card border border-dark-border rounded-xl p-5 hover:border-gray-600 transition-colors">
        <div class="flex items-start justify-between gap-4">
          <div class="flex-1 min-w-0">
            <div class="flex items-center gap-2 flex-wrap">
              <h3 class="text-base font-semibold text-gray-100">{{ pluginMessage(plugin.id, 'metadata.name', plugin.name) }}</h3>
              <span class="text-xs font-mono px-1.5 py-0.5 rounded bg-dark-sidebar text-gray-400 border border-dark-border">
                v{{ plugin.version }}
              </span>
              <span v-if="plugin.isSystem"
                class="text-xs px-1.5 py-0.5 rounded bg-primary-500/15 text-primary-400 border border-primary-500/30">
                {{ t('plugins.system') }}
              </span>
              <span :class="['text-xs px-1.5 py-0.5 rounded border', stateColor(plugin.state)]">
                {{ stateLabel(plugin.state) }}
              </span>
            </div>
            <p v-if="plugin.description" class="text-sm text-gray-400 mt-1.5">{{ pluginMessage(plugin.id, 'metadata.description', plugin.description) }}</p>
            <div class="flex items-center gap-4 mt-2 text-xs text-gray-500">
              <span v-if="plugin.author">{{ t('page.d40e1a908e0e') }} {{ plugin.author }}</span>
              <span>{{ t('page.490391ce6680') }} {{ priorityLabel(plugin.priority) }}</span>
              <span class="font-mono text-gray-600">{{ plugin.id }}</span>
            </div>
          </div>
          <div class="flex items-center gap-3 flex-shrink-0 pt-1">
            <button v-if="!plugin.isSystem" @click="rollbackPlugin(plugin)" :disabled="rollingBack === plugin.id"
              class="text-xs text-gray-400 hover:text-primary-400 disabled:opacity-50" :title="t('page.e2e7ba78885f')">
              {{ rollingBack === plugin.id ? t('page.7cc758a5d3f8') : t('plugins.rollback') }}
            </button>
            <!-- 插件操作按钮 -->
            <button v-for="action in (plugin.actions || [])" :key="action.id"
              @click="executePluginAction(plugin, action)"
              class="text-gray-400 hover:text-primary-400 transition-colors p-1"
              :title="pluginMessage(plugin.id, `actions.${action.id}.name`, action.name)">
              <span v-if="action.type === 'page-link'" class="text-xs px-1">{{ t('plugins.manage') }}</span>
              <!-- 日志查看器图标 -->
              <svg v-else-if="action.type === 'log-viewer'" class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
              </svg>
              <!-- 通用操作图标 -->
              <svg v-else class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13 10V3L4 14h7v7l9-11h-7z" />
              </svg>
            </button>
            <button v-if="plugin.hasOptions" @click="openEditConfig(plugin)"
              class="text-gray-400 hover:text-primary-400 transition-colors p-1" :title="t('page.b2b5b9fafa31')">
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
            <h2 class="text-lg font-semibold text-gray-100">{{ t('plugins.editConfig') }}</h2>
            <p class="text-xs text-gray-500 mt-0.5">{{ pluginMessage(editConfigPlugin?.id, 'metadata.name', editConfigPlugin?.name) }} ({{ editConfigPlugin?.id }})</p>
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
            <span class="ml-3 text-gray-400">{{ t('plugins.loadingConfig') }}</span>
          </div>

          <div v-else-if="editConfigItems.length === 0" class="text-center py-12">
            <p class="text-gray-500">{{ t('plugins.noConfig') }}</p>
          </div>

          <template v-else v-for="item in editConfigItems" :key="item.key">
            <!-- ====== 简单值 ====== -->
            <div v-if="item.type === 'simple'" class="flex flex-col sm:flex-row sm:items-center gap-2 sm:gap-3">
              <label class="w-full sm:w-2/5 text-sm text-gray-300 whitespace-normal break-words flex-shrink-0" :title="item.key">
                {{ fieldLabel(item.key, editConfigLabels[item.key], editConfigPlugin?.id, editConfigSchema[item.key]?.labelKey) }}
                <span v-if="editConfigSchema[item.key]?.descriptionKey" class="block text-xs text-gray-500 whitespace-normal mt-1">{{ fieldDescription(editConfigSchema[item.key]?.descriptionKey, editConfigPlugin?.id) }}</span>
              </label>
              <div class="w-full sm:w-auto min-w-0 flex-1 flex items-center gap-2">
                <button v-if="isBoolValue((item as SimpleItem).defaultValue || (item as SimpleItem).value)"
                  @click="(item as SimpleItem).value = (item as SimpleItem).value === 'true' ? 'false' : 'true'"
                  :class="[
                    'relative inline-flex h-6 w-11 items-center rounded-full transition-colors duration-200 focus:outline-none flex-shrink-0',
                    (item as SimpleItem).value === 'true' ? 'bg-primary-500' : 'bg-gray-600'
                  ]">
                  <span :class="[
                    'inline-block h-4 w-4 transform rounded-full bg-white shadow transition-transform duration-200',
                    (item as SimpleItem).value === 'true' ? 'translate-x-6' : 'translate-x-1'
                  ]" />
                </button>
                <input v-else :value="(item as SimpleItem).value"
                  @input="(item as SimpleItem).value = ($event.target as HTMLInputElement).value"
                  :type="editConfigSchema[item.key]?.sensitive ? 'password' : ['Int32', 'Int64', 'Double', 'Single', 'Decimal'].includes(editConfigSchema[item.key]?.type || '') ? 'number' : 'text'"
                  autocomplete="new-password"
                  step="any"
                  :min="editConfigSchema[item.key]?.minimum" :max="editConfigSchema[item.key]?.maximum"
                  :class="[
                    'w-full bg-dark-sidebar border rounded-lg px-3 py-1.5 text-sm text-gray-200 focus:outline-none focus:ring-1 focus:ring-primary-500 focus:border-primary-500',
                    isSimpleModified(item as SimpleItem) ? 'border-yellow-500/40' : 'border-dark-border'
                  ]"
                  :placeholder="editConfigSchema[item.key]?.sensitive ? t('plugins.keepSecret') : (item as SimpleItem).defaultValue || ''" />
                <button v-if="isSimpleModified(item as SimpleItem)" @click="resetSimple(item as SimpleItem)"
                  class="flex-shrink-0 text-gray-500 hover:text-yellow-400 transition-colors p-1"
                  :title="t('common.resetValue', { value: (item as SimpleItem).defaultValue })">
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
                <span class="text-sm text-gray-300" :class="{ 'font-mono': !editConfigLabels[item.key] }">{{ fieldLabel(item.key, editConfigLabels[item.key], editConfigPlugin?.id) }}
                  <span class="text-xs text-gray-600 ml-1">{{ t('common.arrayCount', { count: (item as ArrayItem).items.length }) }}</span>
                </span>
                <div class="flex items-center gap-2">
                  <button v-if="isArrayModified(item as ArrayItem)" @click="resetArray(item as ArrayItem)"
                    class="text-gray-500 hover:text-yellow-400 transition-colors p-0.5" :title="t('common.reset')">
                    <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2"
                        d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                    </svg>
                  </button>
                  <button @click="addArrayItem(item as ArrayItem)"
                    class="text-xs text-primary-400 hover:text-primary-300 transition-colors">+ {{ t('common.add') }}</button>
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
              <div v-if="(item as ArrayItem).items.length === 0" class="text-xs text-gray-600 text-center py-1">{{ t('common.emptyArray') }}</div>
            </div>

            <!-- ====== 字典 ====== -->
            <div v-else-if="item.type === 'dict'"
              class="border border-dark-border/60 rounded-lg p-3 space-y-2">
              <div class="flex items-center justify-between">
                <span class="text-sm text-gray-300" :class="{ 'font-mono': !editConfigLabels[item.key] }">{{ fieldLabel(item.key, editConfigLabels[item.key], editConfigPlugin?.id) }}
                  <span class="text-xs text-gray-600 ml-1">{{ t('common.dictionaryCount', { count: (item as DictItem).entries.length }) }}</span>
                </span>
                <div class="flex items-center gap-2">
                  <button v-if="isDictModified(item as DictItem)" @click="resetDict(item as DictItem)"
                    class="text-gray-500 hover:text-yellow-400 transition-colors p-0.5" :title="t('common.reset')">
                    <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2"
                        d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                    </svg>
                  </button>
                  <button @click="addDictEntry(item as DictItem)"
                    class="text-xs text-primary-400 hover:text-primary-300 transition-colors">+ {{ t('common.add') }}</button>
                </div>
              </div>
              <div v-for="(entry, idx) in (item as DictItem).entries" :key="idx"
                class="flex items-center gap-2">
                <span v-if="editConfigSchema[`${item.key}:${entry.key}`]" class="w-2/5 text-sm text-gray-300 break-words shrink-0" :title="`${item.key}:${entry.key}`">
                  {{ fieldLabel(`${item.key}:${entry.key}`, editConfigLabels[`${item.key}:${entry.key}`], editConfigPlugin?.id, editConfigSchema[`${item.key}:${entry.key}`]?.labelKey) }}
                </span>
                <input v-else v-model="entry.key" type="text" :placeholder="t('common.key')"
                  class="w-2/5 bg-dark-sidebar border border-dark-border rounded-lg px-3 py-1 text-sm text-gray-200 focus:outline-none focus:ring-1 focus:ring-primary-500 focus:border-primary-500 font-mono" />
                <input v-model="entry.value" :type="editConfigSchema[`${item.key}:${entry.key}`]?.sensitive ? 'password' : 'text'" :placeholder="t('common.value')"
                  class="flex-1 min-w-0 bg-dark-sidebar border border-dark-border rounded-lg px-3 py-1 text-sm text-gray-200 focus:outline-none focus:ring-1 focus:ring-primary-500 focus:border-primary-500" />
                <button @click="removeDictEntry(item as DictItem, idx)"
                  class="text-gray-600 hover:text-red-400 transition-colors p-0.5 flex-shrink-0">
                  <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                  </svg>
                </button>
              </div>
              <div v-if="(item as DictItem).entries.length === 0" class="text-xs text-gray-600 text-center py-1">{{ t('common.emptyDictionary') }}</div>
            </div>

            <!-- ====== 对象数组 ====== -->
            <div v-else-if="item.type === 'object-array'"
              class="border border-dark-border/60 rounded-lg p-3 space-y-3">
              <div class="flex items-center justify-between">
                <span class="text-sm text-gray-300" :class="{ 'font-mono': !editConfigLabels[item.key] }">{{ fieldLabel(item.key, editConfigLabels[item.key], editConfigPlugin?.id) }}
                  <span class="text-xs text-gray-600 ml-1">[{{ (item as ObjArrayItem).items.length }}]</span>
                </span>
                <div class="flex items-center gap-2">
                  <button v-if="isObjArrayModified(item as ObjArrayItem)" @click="resetObjArray(item as ObjArrayItem)"
                    class="text-gray-500 hover:text-yellow-400 transition-colors p-0.5" :title="t('common.reset')">
                    <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2"
                        d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                    </svg>
                  </button>
                  <button @click="addObjArrayItem(item as ObjArrayItem)"
                    class="text-xs text-primary-400 hover:text-primary-300 transition-colors">{{ t('page.fbf5151f677c') }}</button>
                </div>
              </div>
              <!-- 每条规则卡片 -->
              <div v-for="(obj, idx) in (item as ObjArrayItem).items" :key="idx"
                class="bg-dark-sidebar/50 border border-dark-border/40 rounded-lg p-3 space-y-2">
                <div class="flex items-center justify-between mb-1">
                  <span class="text-xs text-gray-500">{{ t('page.71a68f78c9e6') }}{{ idx + 1 }}</span>
                  <div class="flex items-center gap-1">
                    <button @click="moveObjArrayItem(item as ObjArrayItem, idx, -1)"
                      :disabled="idx === 0"
                      :class="['transition-colors p-0.5', idx === 0 ? 'text-gray-700 cursor-not-allowed' : 'text-gray-500 hover:text-primary-400']"
                      :title="t('common.moveUp')">
                      <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M5 15l7-7 7 7" />
                      </svg>
                    </button>
                    <button @click="moveObjArrayItem(item as ObjArrayItem, idx, 1)"
                      :disabled="idx === (item as ObjArrayItem).items.length - 1"
                      :class="['transition-colors p-0.5', idx === (item as ObjArrayItem).items.length - 1 ? 'text-gray-700 cursor-not-allowed' : 'text-gray-500 hover:text-primary-400']"
                      :title="t('common.moveDown')">
                      <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 9l-7 7-7-7" />
                      </svg>
                    </button>
                    <button @click="removeObjArrayItem(item as ObjArrayItem, idx)"
                      class="text-gray-600 hover:text-red-400 transition-colors p-0.5" :title="t('common.delete')">
                      <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                      </svg>
                    </button>
                  </div>
                </div>
                <div v-for="field in (item as ObjArrayItem).fields" :key="field"
                  class="flex items-center gap-2">
                  <label class="w-1/4 text-xs text-gray-400 truncate flex-shrink-0" :title="field">
                    {{ fieldLabel(`${item.key}:${field}`, editConfigLabels[`${item.key}:${field}`], editConfigPlugin?.id) }}
                  </label>
                  <button v-if="isBoolValue((item as ObjArrayItem).fieldDefaultValues[field] ?? '')"
                    @click="obj[field] = obj[field] === 'true' ? 'false' : 'true'"
                    :class="[
                      'relative inline-flex h-5 w-9 items-center rounded-full transition-colors duration-200 focus:outline-none flex-shrink-0',
                      obj[field] === 'true' ? 'bg-primary-500' : 'bg-gray-600'
                    ]">
                    <span :class="[
                      'inline-block h-3.5 w-3.5 transform rounded-full bg-white shadow transition-transform duration-200',
                      obj[field] === 'true' ? 'translate-x-4' : 'translate-x-0.5'
                    ]" />
                  </button>
                  <input v-else v-model="obj[field]" type="text"
                    :placeholder="fieldLabel(`${item.key}:${field}`, editConfigLabels[`${item.key}:${field}`], editConfigPlugin?.id)"
                    class="flex-1 bg-dark-sidebar border border-dark-border rounded-lg px-2.5 py-1 text-sm text-gray-200 focus:outline-none focus:ring-1 focus:ring-primary-500 focus:border-primary-500" />
                </div>
              </div>
              <div v-if="(item as ObjArrayItem).items.length === 0"
                class="text-xs text-gray-600 text-center py-2">{{ t('page.fcd8ed2e793f') }}</div>
            </div>
          </template>
        </div>

        <!-- 底部按钮 -->
        <section v-if="supportsScopedConfig" class="px-6 py-3 border-t border-gray-700 max-h-64 overflow-auto">
          <h3 class="text-sm font-medium mb-2">{{ t('page.b97e9cb78e75') }}</h3>
          <div class="flex flex-wrap gap-2">
            <input v-model="effectiveHost" :aria-label="t('page.1b0bd09c7cc4')" placeholder="img.example.com" class="input min-w-0 flex-1" />
            <input v-model="effectivePath" :aria-label="t('fields.Path')" placeholder="/thumb/a.png" class="input min-w-0 flex-1" />
            <button class="btn btn-sm btn-secondary" :disabled="effectiveLoading" @click="queryEffectiveConfig">{{ effectiveLoading ? t('page.192533042f7f') : t('ui.query') }}</button>
          </div>
          <p v-if="effectiveResult?.lastReloadError" role="alert" class="text-sm text-red-400 mt-2">{{ effectiveResult.lastReloadError }}</p>
          <table v-if="effectiveResult" class="w-full text-xs mt-3 table-fixed">
            <thead><tr><th class="text-left">{{ t('page.49ecd0e342d6') }}</th><th class="text-left">{{ t('common.value') }}</th><th class="text-left">{{ t('settings.source') }}</th></tr></thead>
            <tbody><tr v-for="(value, key) in effectiveResult.config" :key="key">
              <td class="break-all py-1">{{ key }}</td><td class="break-all">{{ value }}</td><td class="break-all">{{ effectiveResult.sources[key] }}</td>
            </tr></tbody>
          </table>
        </section>
        <div v-if="Object.keys(editConfigErrors).length" role="alert" class="px-6 py-3 text-sm text-red-400 max-h-32 overflow-auto">
          <p v-for="(messages, field) in editConfigErrors" :key="field">{{ field }}: {{ messages.join('；') }}</p>
        </div>
        <div class="flex items-center justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="editConfigVisible = false" class="btn btn-sm btn-secondary">{{ t('common.cancel') }}</button>
          <button @click="validateConfig" :disabled="editConfigSaving || editConfigValidating || editConfigLoading" class="btn btn-sm btn-secondary">
            {{ editConfigValidating ? t('common.loading') : t('plugins.validateConfig') }}
          </button>
          <button @click="saveConfig"
            :disabled="editConfigSaving || !hasConfigChanges"
            :class="['btn btn-sm', hasConfigChanges ? 'btn-primary' : 'btn-secondary opacity-50 cursor-not-allowed']">
            <span v-if="editConfigSaving" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin mr-1.5"></span>
            {{ editConfigSaving ? t('common.loading') : t('plugins.saveConfig') }}
          </button>
        </div>
      </div>
    </div>

    <!-- 日志查看器弹窗 -->
    <div v-if="logViewerVisible" class="fixed inset-0 z-50 flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="logViewerVisible = false"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-[1100px] max-w-[95vw] max-h-[85vh] flex flex-col">
        <!-- 头部 -->
        <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border shrink-0">
          <div class="flex items-center gap-4">
            <h3 class="text-lg font-semibold text-gray-100">
              {{ logViewerAction?.name || t('dashboard.view.logs') }}
              <span class="text-sm font-normal text-gray-500 ml-2">{{ pluginMessage(logViewerPlugin?.id, 'metadata.name', logViewerPlugin?.name) }}</span>
            </h3>
            <span v-if="logTotal > 0" class="text-xs text-gray-500">
              {{ t('page.76e547a8fa54') }} {{ logTotal }} {{ t('page.4bb170bf439d') }} {{ formatFileSize(logFileSize) }}
            </span>
          </div>
          <button @click="logViewerVisible = false" class="text-gray-400 hover:text-gray-200 text-xl leading-none">&times;</button>
        </div>

        <!-- 内容区 -->
        <div class="flex-1 flex overflow-hidden">
          <!-- 左侧文件列表 -->
          <div class="w-56 shrink-0 border-r border-dark-border flex flex-col">
            <div class="px-3 py-2 border-b border-dark-border/50 flex items-center justify-between">
              <span class="text-xs text-gray-500 font-semibold">{{ t('page.f88d392b86ea') }}</span>
              <div class="flex items-center gap-2">
                <button v-if="logFiles.length > 0" @click="deleteAllLogFiles" class="text-[10px] text-gray-600 hover:text-red-400 transition-colors" :title="t('page.47b6ebb7072c')">{{ t('page.9b2ccbb48e31') }}</button>
                <button @click="loadLogFiles" class="text-xs text-gray-500 hover:text-gray-300 transition-colors">{{ t('common.refresh') }}</button>
              </div>
            </div>
            <div class="flex-1 overflow-y-auto">
              <div v-if="logFiles.length === 0 && !logViewerLoading" class="text-xs text-gray-600 text-center py-8">{{ t('page.edf2a6a09da3') }}</div>
              <div v-for="file in logFiles" :key="file.name"
                @click="logSelectFile(file.name)"
                :class="[
                  'px-3 py-2.5 cursor-pointer border-b border-dark-border/30 transition-colors group',
                  logSelectedFile === file.name ? 'bg-primary-500/10 border-l-2 border-l-primary-500' : 'hover:bg-dark-card-hover border-l-2 border-l-transparent'
                ]">
                <div class="text-xs text-gray-200 font-mono truncate" :title="file.name">{{ file.name }}</div>
                <div class="flex items-center justify-between mt-1">
                  <span class="text-[10px] text-gray-500">{{ formatFileSize(file.size) }}</span>
                  <span class="text-[10px] text-gray-600">{{ file.lastModified }}</span>
                </div>
                <div class="flex items-center gap-2 mt-1 hidden group-hover:flex">
                  <button @click.stop="clearLogFile(file.name)"
                    class="text-[10px] text-gray-600 hover:text-yellow-400 transition-colors">
                    {{ t('page.1ef3de06b32e') }}
                  </button>
                  <button @click.stop="deleteLogFile(file.name)"
                    class="text-[10px] text-gray-600 hover:text-red-400 transition-colors">
                    {{ t('page.88f47860e77d') }}
                  </button>
                </div>
              </div>
            </div>
          </div>

          <!-- 右侧日志条目 -->
          <div class="flex-1 flex flex-col overflow-hidden">
            <!-- 搜索栏 -->
            <div class="px-4 py-2 border-b border-dark-border/50 flex items-center gap-2">
              <input v-model="logSearch" type="text" :placeholder="t('page.0080a0856da2')"
                class="flex-1 bg-dark-sidebar border border-dark-border rounded-lg px-3 py-1.5 text-sm text-gray-200 focus:outline-none focus:ring-1 focus:ring-primary-500 focus:border-primary-500"
                @keydown.enter="loadLogEntries()" />
              <button @click="loadLogEntries()" class="btn btn-sm btn-secondary text-xs">{{ t('common.search') }}</button>
            </div>

            <!-- 加载中 -->
            <div v-if="logViewerLoading" class="flex-1 flex items-center justify-center">
              <div class="animate-spin w-6 h-6 border-2 border-gray-500 border-t-primary-400 rounded-full"></div>
              <span class="ml-3 text-gray-400 text-sm">{{ t('common.loading') }}</span>
            </div>

            <!-- 无数据 -->
            <div v-else-if="logEntries.length === 0" class="flex-1 flex items-center justify-center">
              <span class="text-gray-500 text-sm">{{ logSelectedFile ? t('dashboard.no.log.entries') : t('page.805a9989bb6c') }}</span>
            </div>

            <!-- 条目列表 -->
            <div v-else class="flex-1 overflow-y-auto divide-y divide-dark-border">
              <div v-for="entry in logEntries" :key="entry.index" class="hover:bg-dark-card-hover transition-colors group/entry" @contextmenu="onLogEntryContextMenu($event, entry)">
                <!-- 摘要行 -->
                <div class="flex items-center gap-4 px-5 py-3 cursor-pointer select-none"
                  @click="logToggleExpand(entry.index)">
                  <span class="text-gray-500 text-xs shrink-0 w-4 text-center transition-transform"
                    :class="{ 'rotate-90': logExpandedEntry === entry.index }">▶</span>
                  <span class="text-gray-500 text-xs font-mono w-8 shrink-0 text-right">#{{ entry.index + 1 }}</span>
                  <span class="text-gray-400 text-xs font-mono shrink-0 w-[170px]">{{ entry.time }}</span>
                  <span :class="methodColor(entry.method)" class="text-xs font-bold font-mono shrink-0 w-16">{{ entry.method }}</span>
                  <span class="text-gray-200 text-xs font-mono truncate flex-1" :title="entry.url">{{ entry.url }}</span>
                  <!-- 状态码 -->
                  <span v-if="entry.statusCode"
                    :class="[
                      'text-xs font-mono px-1.5 py-0.5 rounded shrink-0',
                      entry.statusCode >= 200 && entry.statusCode < 300 ? 'bg-green-500/20 text-green-400' :
                      entry.statusCode >= 300 && entry.statusCode < 400 ? 'bg-blue-500/20 text-blue-400' :
                      entry.statusCode >= 400 && entry.statusCode < 500 ? 'bg-yellow-500/20 text-yellow-400' :
                      entry.statusCode >= 500 ? 'bg-red-500/20 text-red-400' :
                      'bg-gray-500/20 text-gray-400'
                    ]">{{ entry.statusCode }}</span>
                  <!-- 响应时间 -->
                  <span v-if="entry.duration" class="text-xs font-mono text-gray-500 shrink-0 w-20 text-right">{{ entry.duration }}</span>
                  <!-- 删除条目 -->
                  <button @click.stop="deleteLogEntry(entry)"
                    class="text-gray-600 hover:text-red-400 transition-colors shrink-0 opacity-0 group-hover/entry:opacity-100 ml-2"
                    :title="t('page.c747887833b3')">
                    <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                    </svg>
                  </button>
                </div>

                <!-- 展开的详情 -->
                <div v-if="logExpandedEntry === entry.index" class="px-5 pb-4 space-y-3">
                  <!-- 请求行 -->
                  <div class="bg-dark-bg rounded-lg p-3">
                    <div class="text-xs text-gray-500 mb-1">{{ t('page.c82d19cd1137') }}</div>
                    <div class="text-xs text-gray-200 font-mono">{{ entry.requestLine }}</div>
                    <div v-if="entry.host" class="text-xs text-gray-400 font-mono mt-1">Host: {{ entry.host }}</div>
                    <div v-if="entry.clientIp" class="text-xs text-gray-400 font-mono mt-1">Client-IP: {{ entry.clientIp }}</div>
                  </div>

                  <!-- 请求头 -->
                  <div v-if="entry.headers" class="bg-dark-bg rounded-lg p-3">
                    <div class="text-xs text-gray-500 mb-1">{{ t('fields.Headers') }}</div>
                    <pre class="text-xs text-gray-300 font-mono whitespace-pre-wrap break-all max-h-[200px] overflow-auto">{{ entry.headers }}</pre>
                  </div>

                  <!-- 请求体 -->
                  <div v-if="entry.requestBody" class="bg-dark-bg rounded-lg p-3">
                    <div class="text-xs text-gray-500 mb-1">{{ t('dashboard.request.body') }}</div>
                    <pre class="text-xs text-gray-300 font-mono whitespace-pre-wrap break-all max-h-[300px] overflow-auto">{{ entry.requestBody }}</pre>
                  </div>

                  <!-- 响应体 -->
                  <div v-if="entry.responseBody" class="bg-dark-bg rounded-lg p-3">
                    <div class="text-xs text-gray-500 mb-1">{{ t('page.4d008af0350e') }}</div>
                    <pre class="text-xs text-gray-300 font-mono whitespace-pre-wrap break-all max-h-[300px] overflow-auto">{{ entry.responseBody }}</pre>
                  </div>
                </div>
              </div>
            </div>

            <!-- 底部分页 -->
            <div class="flex items-center justify-between px-6 py-3 border-t border-dark-border shrink-0">
              <div class="flex items-center gap-2">
                <template v-if="logTotalPages > 1">
                  <button @click="logGotoPage(1)" :disabled="logCurrentPage === 1" class="btn btn-sm btn-secondary text-xs px-2">{{ t('dashboard.first') }}</button>
                  <button @click="logGotoPage(logCurrentPage - 1)" :disabled="logCurrentPage === 1" class="btn btn-sm btn-secondary text-xs px-2">{{ t('common.previous') }}</button>
                  <span class="text-xs text-gray-400 px-2">{{ t('ui.pageOf', { page: logCurrentPage, pages: logTotalPages }) }}</span>
                  <button @click="logGotoPage(logCurrentPage + 1)" :disabled="logCurrentPage === logTotalPages" class="btn btn-sm btn-secondary text-xs px-2">{{ t('common.next') }}</button>
                  <button @click="logGotoPage(logTotalPages)" :disabled="logCurrentPage === logTotalPages" class="btn btn-sm btn-secondary text-xs px-2">{{ t('dashboard.last') }}</button>
                </template>
                <span v-else-if="logTotal > 0" class="text-xs text-gray-500">{{ t('page.76e547a8fa54') }} {{ logTotal }} {{ t('page.f004f1d84cf9') }}</span>
              </div>
              <div class="flex items-center gap-2">
                <button v-if="logSelectedFile && logTotal > 0" @click="clearLogFile(logSelectedFile)" class="btn btn-sm text-xs text-red-400 hover:bg-red-500/10 border border-red-500/20">{{ t('page.ead678844b79') }}</button>
                <button @click="loadLogEntries()" class="btn btn-secondary btn-sm">{{ t('common.refresh') }}</button>
                <button @click="logViewerVisible = false" class="btn btn-secondary btn-sm">{{ t('common.close') }}</button>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>

  <!-- 右键菜单：复制为 cURL -->
  <Teleport to="body">
    <div
      v-if="logContextMenu.show"
      class="fixed z-[200] bg-dark-card border border-dark-border rounded-lg shadow-2xl py-1 min-w-[200px]"
      :style="{ left: logContextMenu.x + 'px', top: logContextMenu.y + 'px' }"
      @click.stop
    >
      <div class="px-3 py-1.5 text-xs text-gray-500 select-none">{{ t('dashboard.copy.as.curl') }}</div>
      <button
        @click="copyLogCurl('bash')"
        class="w-full text-left px-3 py-2 text-sm text-gray-200 hover:bg-dark-card-hover flex items-center gap-2"
      >
        <span class="text-green-400 text-xs font-mono w-14">Bash</span>
        <span class="text-gray-400 text-xs">Linux / macOS</span>
      </button>
      <button
        @click="copyLogCurl('cmd')"
        class="w-full text-left px-3 py-2 text-sm text-gray-200 hover:bg-dark-card-hover flex items-center gap-2"
      >
        <span class="text-blue-400 text-xs font-mono w-14">CMD</span>
        <span class="text-gray-400 text-xs">Windows</span>
      </button>
      <button
        @click="copyLogCurl('powershell')"
        class="w-full text-left px-3 py-2 text-sm text-gray-200 hover:bg-dark-card-hover flex items-center gap-2"
      >
        <span class="text-purple-400 text-xs font-mono w-14">PS</span>
        <span class="text-gray-400 text-xs">PowerShell</span>
      </button>
    </div>
  </Teleport>
</template>
