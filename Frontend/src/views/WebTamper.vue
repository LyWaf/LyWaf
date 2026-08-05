<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { webTamperApi, type WebTamperComparison, type WebTamperEvent, type WebTamperResource, type WebTamperWebsite } from '@/api'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()
const resources = ref<WebTamperResource[]>([])
const events = ref<WebTamperEvent[]>([])
const websites = ref<WebTamperWebsite[]>([])
const selectedWebsite = ref('')
const loading = ref(true)
const activeTab = ref<'resources' | 'events'>('resources')
const search = ref('')
const busyId = ref<string | null>(null)
const checkingId = ref<string | null>(null)
const showAdd = ref(false)
const showAddWebsite = ref(false)
const showDeleteWebsite = ref(false)
const showRenameWebsite = ref(false)
const websiteRemark = ref('')
const websiteForm = ref({ name: '', domain: '', port: '' })
const websiteSaving = ref(false)
const addPath = ref('/')
const adding = ref(false)
const comparison = ref<WebTamperComparison | null>(null)
const deleteTarget = ref<WebTamperResource | null>(null)
const expandedPatterns = ref<string[]>([])

interface ResourceTreeRow {
  key: string
  label: string
  depth: number
  kind: 'directory' | 'resource'
  resource?: WebTamperResource
}

const buildResourceTree = (items: WebTamperResource[]): ResourceTreeRow[] => {
  type Node = { children: Map<string, Node>; resources: WebTamperResource[] }
  const root: Node = { children: new Map(), resources: [] }
  for (const resource of items) {
    const pathOnly = resource.path.split('?')[0]
    const segments = pathOnly.split('/').filter(Boolean)
    let node = root
    for (const segment of segments.slice(0, -1)) {
      if (!node.children.has(segment)) node.children.set(segment, { children: new Map(), resources: [] })
      node = node.children.get(segment)!
    }
    node.resources.push(resource)
  }

  const rows: ResourceTreeRow[] = []
  const append = (node: Node, depth: number, prefix: string) => {
    for (const [name, child] of [...node.children.entries()].sort(([a], [b]) => a.localeCompare(b))) {
      const key = `${prefix}/${name}`
      rows.push({ key: `dir:${key}`, label: name, depth, kind: 'directory' })
      append(child, depth + 1, key)
    }
    for (const resource of node.resources.sort((a, b) => a.path.localeCompare(b.path))) {
      const pathOnly = resource.path.split('?')[0]
      const label = pathOnly.split('/').filter(Boolean).at(-1) || '/'
      const query = resource.path.includes('?') ? `?${resource.path.split('?').slice(1).join('?')}` : ''
      rows.push({ key: resource.id, label: label + query, depth, kind: 'resource', resource })
    }
  }
  append(root, 0, '')
  return rows
}

const isPatternExpanded = (id: string) => expandedPatterns.value.includes(id)
const togglePatternDetails = (id: string) => {
  expandedPatterns.value = isPatternExpanded(id)
    ? expandedPatterns.value.filter(item => item !== id)
    : [...expandedPatterns.value, id]
}

const visibleResources = computed(() => {
  const keyword = search.value.trim().toLowerCase()
  const scoped = resources.value.filter(item => item.website === selectedWebsite.value)
  return keyword ? scoped.filter(item => item.path.toLowerCase().includes(keyword)
    || item.capturedResources?.some(child => child.path.toLowerCase().includes(keyword))) : scoped
})
const currentWebsite = computed(() => websites.value.find(item => item.domain === selectedWebsite.value))

const visibleEvents = computed(() => events.value.filter(item => {
  const resource = resources.value.find(candidate => candidate.id === item.resourceId)
  return resource?.website === selectedWebsite.value
}))

const unresolvedCount = computed(() => visibleEvents.value.filter(item => !item.resolved).length)
const resourceForEvent = (event: WebTamperEvent) => {
  const parent = resources.value.find(item => item.id === event.resourceId)
  if (!parent) return undefined
  return parent.isPattern
    ? parent.capturedResources?.find(item => item.path === event.resourceName)
    : parent
}

const inspectEvent = (event: WebTamperEvent) => {
  const resource = resourceForEvent(event)
  if (!resource) return showError('对应的保护资源已不存在')
  return checkResource(resource)
}
const formatTime = (value?: string) => value ? new Intl.DateTimeFormat('zh-CN', {
  year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit'
}).format(new Date(value)) : '尚未检查'
const shortHash = (value?: string) => value ? value.slice(0, 12) : '-'

const loadData = async () => {
  loading.value = true
  try {
    const [websiteResult, resourceResult, eventResult] = await Promise.all([webTamperApi.getWebsites(), webTamperApi.getResources(), webTamperApi.getEvents()])
    if (websiteResult.success) {
      websites.value = websiteResult.websites
      if (!selectedWebsite.value && websites.value.length) selectedWebsite.value = websites.value[0].domain
    }
    if (resourceResult.success) resources.value = resourceResult.resources
    if (eventResult.success) events.value = eventResult.events
  } catch { showError('加载网页防篡改数据失败') }
  finally { loading.value = false }
}

const addResource = async () => {
  if (!selectedWebsite.value) return showError('请先选择防护网站')
  if (!addPath.value.trim()) return showError('请输入资源路径')
  adding.value = true
  try {
    const result = await webTamperApi.add(selectedWebsite.value, addPath.value.trim())
    if (!result.success) return showError(result.message || '添加失败')
    showSuccess(result.message || '保护资源已添加')
    showAdd.value = false
    addPath.value = '/'
    await loadData()
  } catch { showError('添加资源失败') }
  finally { adding.value = false }
}

const addWebsite = async () => {
  if (!websiteForm.value.name.trim() || !websiteForm.value.domain.trim()) return showError('请填写网站名称和域名')
  const port = websiteForm.value.port ? Number(websiteForm.value.port) : undefined
  if (port !== undefined && (!Number.isInteger(port) || port < 1 || port > 65535)) return showError('端口必须在 1 到 65535 之间')
  websiteSaving.value = true
  try {
    const result = await webTamperApi.addWebsite(websiteForm.value.name.trim(), websiteForm.value.domain.trim(), port)
    if (!result.success) return showError(result.message || '添加网站失败')
    const domain = result.website?.domain || `${websiteForm.value.domain.trim().toLowerCase()}${port ? `:${port}` : ''}`
    websiteForm.value = { name: '', domain: '', port: '' }
    showAddWebsite.value = false
    await loadData()
    if (websites.value.some(item => item.domain === domain)) selectedWebsite.value = domain
    showSuccess('防护网站已添加')
  } catch { showError('添加网站失败') }
  finally { websiteSaving.value = false }
}

const deleteWebsite = async () => {
  if (!currentWebsite.value?.removable) return
  try {
    const result = await webTamperApi.deleteWebsite(currentWebsite.value.id)
    if (!result.success) return showError(result.message || '删除网站失败')
    selectedWebsite.value = ''
    showDeleteWebsite.value = false
    await loadData()
    showSuccess('防护网站已删除')
  } catch { showError('删除网站失败') }
}

const openRenameWebsite = () => {
  if (!currentWebsite.value) return
  websiteRemark.value = currentWebsite.value.name
  showRenameWebsite.value = true
}

const renameWebsite = async () => {
  if (!currentWebsite.value || !websiteRemark.value.trim()) return showError('请输入备注名称')
  websiteSaving.value = true
  try {
    const result = await webTamperApi.renameWebsite(websiteRemark.value.trim(), currentWebsite.value.domain, currentWebsite.value.source)
    if (!result.success) return showError(result.message || '修改备注失败')
    showRenameWebsite.value = false
    await loadData()
    showSuccess('网站备注已更新')
  } catch { showError('修改备注失败') }
  finally { websiteSaving.value = false }
}

const toggleResource = async (resource: WebTamperResource) => {
  busyId.value = resource.id
  const result = await webTamperApi.update(resource.id, resource.name, !resource.enabled)
  if (result.success) {
    resource.enabled = !resource.enabled
    showSuccess(resource.parentResourceId
      ? (resource.enabled ? '该路径已恢复防护' : '该路径已从目录规则中排除')
      : (resource.enabled ? '防护已启用' : '防护已暂停'))
  } else showError(result.message || '操作失败')
  busyId.value = null
}

const checkResource = async (resource: WebTamperResource) => {
  busyId.value = resource.id
  checkingId.value = resource.id
  try {
    const result = await webTamperApi.check(resource.id)
    if (!result.success || !result.comparison) {
      const message = result.message || '检查失败'
      return showError(/超时|timed out|timeout|taskcanceled/i.test(message) ? '检查超时，请确认上游网站可访问后重试' : message)
    }
    comparison.value = result.comparison
    showSuccess(result.comparison.changed ? '检测到内容变更，访客仍在使用安全基线' : '内容与安全基线一致')
    await loadData()
  } catch (error: unknown) {
    const requestError = error as { code?: string; message?: string }
    showError(requestError.code === 'ECONNABORTED' || /timeout/i.test(requestError.message || '')
      ? '检查超时，请确认上游网站可访问后重试'
      : '检查失败，请稍后重试')
  }
  finally { busyId.value = null; checkingId.value = null }
}

const acceptCurrent = async () => {
  if (!comparison.value) return
  busyId.value = comparison.value.resource.id
  try {
    const result = await webTamperApi.accept(comparison.value.resource.id)
    if (!result.success) return showError(result.message || '更新基线失败')
    comparison.value = null
    showSuccess('已确认业务变更并更新安全基线')
    await loadData()
  } catch { showError('更新基线失败') }
  finally { busyId.value = null }
}

const removeResource = async () => {
  if (!deleteTarget.value) return
  const result = await webTamperApi.delete(deleteTarget.value.id)
  if (result.success) {
    showSuccess('保护资源已删除')
    deleteTarget.value = null
    await loadData()
  } else showError(result.message || '删除失败')
}

onMounted(loadData)
</script>

<template>
  <div class="space-y-6">
    <div>
      <div>
        <h1 class="text-xl font-semibold text-gray-100">网页防篡改</h1>
        <p class="text-sm text-gray-500 mt-1">按网站管理保护资源，检测变更后持续向访客返回已确认的安全基线。</p>
      </div>
    </div>

    <section class="bg-dark-card border border-dark-border rounded-lg p-4">
      <div class="flex items-center justify-between gap-4 mb-2">
        <label class="block text-xs text-gray-500">防护网站</label>
        <button class="btn btn-primary btn-sm" @click="showAddWebsite = true">添加网站</button>
      </div>
      <div class="flex flex-col sm:flex-row gap-3">
        <select v-model="selectedWebsite" class="input max-w-lg">
          <option value="" disabled>请选择防护网站</option>
          <option v-for="site in websites" :key="site.domain" :value="site.domain">{{ site.name }}（{{ site.domain }}）</option>
        </select>
        <button v-if="currentWebsite" class="btn btn-secondary shrink-0" @click="openRenameWebsite">修改备注</button>
        <button v-if="currentWebsite?.removable" class="btn btn-danger shrink-0" title="网站存在保护资源时不可删除" @click="showDeleteWebsite = true">删除网站</button>
      </div>
      <div v-if="currentWebsite" class="flex items-center gap-2 mt-3 text-xs text-gray-500">
        <span :class="currentWebsite.source === 'manual' ? 'badge badge-info' : 'badge badge-success'">{{ currentWebsite.source === 'manual' ? '手动添加' : '配置发现' }}</span>
        <span>该网站拥有独立的保护路径和变更事件。</span>
      </div>
      <p v-if="!loading && !websites.length" class="text-sm text-yellow-400 mt-3">暂无防护网站，可点击“添加网站”手动添加。</p>
    </section>

    <div class="flex items-center justify-between gap-4 border-b border-dark-border">
      <div class="flex">
        <button class="px-5 py-3 text-sm border-b-2" :class="activeTab === 'resources' ? 'text-primary-400 border-primary-500' : 'text-gray-500 border-transparent'" @click="activeTab = 'resources'">保护资源 {{ visibleResources.length }}</button>
        <button class="px-5 py-3 text-sm border-b-2" :class="activeTab === 'events' ? 'text-primary-400 border-primary-500' : 'text-gray-500 border-transparent'" @click="activeTab = 'events'">变更事件 <span v-if="unresolvedCount" class="ml-1 badge badge-danger">{{ unresolvedCount }}</span></button>
      </div>
      <div class="flex items-center gap-2 mb-2">
        <button class="btn btn-secondary btn-sm shrink-0" :disabled="loading" @click="loadData">{{ loading ? '刷新中...' : '刷新' }}</button>
        <button v-if="activeTab === 'resources'" class="btn btn-primary btn-sm shrink-0" :disabled="!selectedWebsite" @click="showAdd = true">添加保护资源</button>
      </div>
    </div>

    <section v-if="activeTab === 'resources'" class="bg-dark-card border border-dark-border rounded-lg overflow-hidden">
      <div class="p-4 border-b border-dark-border"><input v-model="search" class="input max-w-sm" placeholder="搜索资源路径" /></div>
      <div class="overflow-x-auto">
        <table class="w-full min-w-[900px] text-sm">
          <thead class="bg-dark-card-hover text-gray-400"><tr><th class="text-left px-5 py-3 font-medium">资源</th><th class="text-left px-5 py-3 font-medium">状态</th><th class="text-left px-5 py-3 font-medium">内容指纹</th><th class="text-left px-5 py-3 font-medium">最后检查</th><th class="text-right px-5 py-3 font-medium">操作</th></tr></thead>
          <tbody class="divide-y divide-dark-border">
            <template v-for="item in visibleResources" :key="item.id">
              <tr class="hover:bg-dark-card-hover/60">
                <td class="px-5 py-4">
                  <div class="flex items-center gap-2">
                    <button v-if="item.isPattern" class="w-6 h-6 shrink-0 text-gray-400 hover:text-gray-100" :title="isPatternExpanded(item.id) ? '收起目录' : '展开目录'" @click="togglePatternDetails(item.id)">{{ isPatternExpanded(item.id) ? '⌄' : '›' }}</button>
                    <span v-else class="w-6 shrink-0"></span>
                    <span class="font-medium font-mono text-gray-200">{{ item.path }}</span>
                    <span v-if="item.isPattern" class="badge badge-info">目录规则</span>
                  </div>
                  <div class="text-xs text-gray-500 mt-1 ml-8 max-w-md truncate">{{ item.website }}<span v-if="item.isPattern"> · 已收集 {{ item.capturedCount }} 个 API/资源路径</span></div>
                </td>
                <td class="px-5 py-4"><span v-if="checkingId === item.id" class="inline-flex items-center gap-2 text-xs text-primary-400"><span class="w-3.5 h-3.5 border-2 border-primary-400/40 border-t-primary-400 rounded-full animate-spin"></span>正在检查</span><span v-else-if="item.changed" class="badge badge-danger">发现变更</span><span v-else-if="item.enabled" class="badge badge-success">防护中</span><span v-else class="badge badge-warning">已暂停</span></td>
                <td class="px-5 py-4 font-mono text-xs text-gray-400">{{ item.isPattern ? `${item.capturedCount} 份独立基线` : shortHash(item.baselineHash) }}</td>
                <td class="px-5 py-4 text-gray-400 whitespace-nowrap">{{ formatTime(item.lastCheckedAt) }}</td>
                <td class="px-5 py-4 text-right whitespace-nowrap">
                  <button class="inline-flex items-center gap-1.5 text-primary-400 hover:text-primary-300 mr-4 disabled:opacity-70" :disabled="busyId === item.id" @click="checkResource(item)"><span v-if="checkingId === item.id" class="w-3.5 h-3.5 border-2 border-current/40 border-t-current rounded-full animate-spin"></span>{{ checkingId === item.id ? '检查中...' : '检查' }}</button>
                  <button class="text-gray-400 hover:text-gray-200 mr-4" :disabled="busyId === item.id" @click="toggleResource(item)">{{ item.enabled ? '暂停' : '启用' }}</button>
                  <button class="text-red-400 hover:text-red-300" @click="deleteTarget = item">删除</button>
                </td>
              </tr>
              <tr v-if="item.isPattern && isPatternExpanded(item.id)" class="bg-dark-bg/40">
                <td colspan="5" class="p-0">
                  <div v-if="item.capturedResources?.length" class="py-2">
                    <div v-for="row in buildResourceTree(item.capturedResources)" :key="row.key" class="grid grid-cols-[minmax(260px,1fr)_130px_150px_280px] items-center min-h-10 px-5 hover:bg-dark-card-hover/50" :style="{ paddingLeft: `${44 + row.depth * 24}px` }">
                      <div class="flex items-center min-w-0 gap-2">
                        <span class="w-4 text-gray-600">{{ row.kind === 'directory' ? '└' : '·' }}</span>
                        <span v-if="row.kind === 'directory'" class="text-xs text-gray-500">目录</span>
                        <span class="font-mono text-xs truncate" :class="row.kind === 'directory' ? 'text-gray-400' : 'text-gray-200'">{{ row.label }}{{ row.kind === 'directory' ? '/' : '' }}</span>
                      </div>
                      <div v-if="row.resource"><span v-if="checkingId === row.resource.id" class="inline-flex items-center gap-2 text-xs text-primary-400"><span class="w-3.5 h-3.5 border-2 border-primary-400/40 border-t-primary-400 rounded-full animate-spin"></span>正在检查</span><span v-else-if="!row.resource.enabled" class="badge badge-warning">已排除</span><span v-else-if="row.resource.changed" class="badge badge-danger">发现变更</span><span v-else class="badge badge-success">基线正常</span></div><div v-else></div>
                      <div class="font-mono text-xs text-gray-500">{{ row.resource ? shortHash(row.resource.baselineHash) : '' }}</div>
                      <div v-if="row.resource" class="flex items-center justify-end gap-4 whitespace-nowrap"><span class="text-xs text-gray-500">{{ formatTime(row.resource.lastCheckedAt) }}</span><button class="inline-flex items-center gap-1.5 text-xs text-primary-400 hover:text-primary-300 disabled:opacity-70" :disabled="busyId === row.resource.id" @click="checkResource(row.resource)"><span v-if="checkingId === row.resource.id" class="w-3 h-3 border-2 border-current/40 border-t-current rounded-full animate-spin"></span>{{ checkingId === row.resource.id ? '检查中...' : '检查' }}</button><button class="text-xs text-gray-400 hover:text-gray-200" :disabled="busyId === row.resource.id" @click="toggleResource(row.resource)">{{ row.resource.enabled ? '排除' : '恢复' }}</button><button class="text-xs text-red-400 hover:text-red-300" @click="deleteTarget = row.resource">删除</button></div>
                    </div>
                  </div>
                  <div v-else class="px-12 py-6 text-sm text-gray-500">尚未收集到实际路径。匹配该规则的请求首次经过 WAF 后会自动出现在这里。</div>
                </td>
              </tr>
            </template>
            <tr v-if="!loading && !visibleResources.length"><td colspan="5" class="py-14 text-center text-gray-500">暂无保护资源</td></tr>
            <tr v-if="loading"><td colspan="5" class="py-14 text-center text-gray-500">加载中...</td></tr>
          </tbody>
        </table>
      </div>
    </section>

    <section v-else class="bg-dark-card border border-dark-border rounded-lg overflow-hidden">
      <div class="overflow-x-auto"><table class="w-full min-w-[780px] text-sm">
        <thead class="bg-dark-card-hover text-gray-400"><tr><th class="text-left px-5 py-3 font-medium">资源</th><th class="text-left px-5 py-3 font-medium">检测时间</th><th class="text-left px-5 py-3 font-medium">指纹变化</th><th class="text-left px-5 py-3 font-medium">状态</th><th class="text-right px-5 py-3 font-medium">操作</th></tr></thead>
        <tbody class="divide-y divide-dark-border">
          <tr v-for="event in visibleEvents" :key="event.id"><td class="px-5 py-4"><div class="text-gray-200 font-mono">{{ event.resourceName }}</div><div class="text-xs text-gray-500 truncate max-w-sm">{{ event.url }}</div></td><td class="px-5 py-4 text-gray-400">{{ formatTime(event.detectedAt) }}</td><td class="px-5 py-4 font-mono text-xs text-gray-500">{{ shortHash(event.baselineHash) }} → {{ shortHash(event.currentHash) }}</td><td class="px-5 py-4"><span :class="event.resolved ? 'badge badge-success' : 'badge badge-danger'">{{ event.resolved ? '已处理' : '待确认' }}</span></td><td class="px-5 py-4 text-right"><button class="inline-flex items-center gap-1.5 text-primary-400 hover:text-primary-300 disabled:opacity-60 whitespace-nowrap" :disabled="busyId === resourceForEvent(event)?.id || !resourceForEvent(event)" @click="inspectEvent(event)"><span v-if="checkingId === resourceForEvent(event)?.id" class="w-3.5 h-3.5 border-2 border-current/40 border-t-current rounded-full animate-spin"></span>{{ checkingId === resourceForEvent(event)?.id ? '查看中...' : (event.resolved ? '查看内容' : '查看状态') }}</button></td></tr>
          <tr v-if="!visibleEvents.length"><td colspan="5" class="py-14 text-center text-gray-500">当前网站暂无变更事件</td></tr>
        </tbody>
      </table></div>
    </section>
  </div>

  <Teleport to="body"><div v-if="showAddWebsite" class="fixed inset-0 z-[100] flex items-center justify-center p-4"><div class="absolute inset-0 bg-black/60" @click="showAddWebsite = false"></div><div class="relative w-[500px] max-w-full bg-dark-card border border-dark-border rounded-lg shadow-2xl"><div class="flex items-center justify-between px-6 py-4 border-b border-dark-border"><h3 class="text-lg font-semibold">添加防护网站</h3><button class="text-xl text-gray-400" title="关闭" @click="showAddWebsite = false">×</button></div><div class="p-6 space-y-4"><div><label class="block text-sm text-gray-400 mb-1">网站名称</label><input v-model="websiteForm.name" class="input" placeholder="例如：企业官网" /></div><div class="grid grid-cols-[minmax(0,1fr)_120px] gap-3"><div><label class="block text-sm text-gray-400 mb-1">域名</label><input v-model="websiteForm.domain" class="input" placeholder="www.example.com" /></div><div><label class="block text-sm text-gray-400 mb-1">端口</label><input v-model="websiteForm.port" class="input" type="number" min="1" max="65535" placeholder="可选" @keydown.enter="addWebsite" /></div></div><p class="text-xs text-gray-500">无需填写 Scheme。检查时使用指定端口优先访问 HTTPS，失败后自动尝试 HTTP。</p></div><div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border"><button class="btn btn-secondary" @click="showAddWebsite = false">取消</button><button class="btn btn-primary" :disabled="websiteSaving" @click="addWebsite">{{ websiteSaving ? '添加中...' : '添加网站' }}</button></div></div></div></Teleport>

  <Teleport to="body"><div v-if="showRenameWebsite && currentWebsite" class="fixed inset-0 z-[100] flex items-center justify-center p-4"><div class="absolute inset-0 bg-black/60" @click="showRenameWebsite = false"></div><div class="relative w-[460px] max-w-full bg-dark-card border border-dark-border rounded-lg shadow-2xl"><div class="flex items-center justify-between px-6 py-4 border-b border-dark-border"><h3 class="text-lg font-semibold">修改网站备注</h3><button class="text-xl text-gray-400" title="关闭" @click="showRenameWebsite = false">×</button></div><div class="p-6 space-y-4"><div><label class="block text-sm text-gray-400 mb-1">备注名称</label><input v-model="websiteRemark" class="input" maxlength="80" placeholder="例如：企业官网" @keydown.enter="renameWebsite" /></div><div><label class="block text-sm text-gray-400 mb-1">域名</label><div class="px-4 py-2 bg-dark-bg border border-dark-border rounded-lg text-gray-500">{{ currentWebsite.domain }}</div></div></div><div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border"><button class="btn btn-secondary" @click="showRenameWebsite = false">取消</button><button class="btn btn-primary" :disabled="websiteSaving" @click="renameWebsite">{{ websiteSaving ? '保存中...' : '保存备注' }}</button></div></div></div></Teleport>

  <Teleport to="body"><div v-if="showDeleteWebsite && currentWebsite" class="fixed inset-0 z-[100] flex items-center justify-center p-4"><div class="absolute inset-0 bg-black/60" @click="showDeleteWebsite = false"></div><div class="relative w-[430px] max-w-full bg-dark-card border border-dark-border rounded-lg shadow-2xl"><div class="p-6"><h3 class="text-lg font-semibold mb-2">删除防护网站</h3><p class="text-gray-400">确定删除“{{ currentWebsite.name }}”吗？存在保护资源时将无法删除。</p></div><div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border"><button class="btn btn-secondary" @click="showDeleteWebsite = false">取消</button><button class="btn btn-danger" @click="deleteWebsite">删除</button></div></div></div></Teleport>

  <Teleport to="body"><div v-if="showAdd" class="fixed inset-0 z-[100] flex items-center justify-center p-4"><div class="absolute inset-0 bg-black/60" @click="showAdd = false"></div><div class="relative w-[520px] max-w-full bg-dark-card border border-dark-border rounded-lg shadow-2xl"><div class="flex items-center justify-between px-6 py-4 border-b border-dark-border"><h3 class="text-lg font-semibold">添加保护资源</h3><button class="text-xl text-gray-400" title="关闭" @click="showAdd = false">×</button></div><div class="p-6 space-y-4"><div><label class="block text-sm text-gray-400 mb-1">防护网站</label><div class="px-4 py-2 bg-dark-bg border border-dark-border rounded-lg text-gray-300">{{ selectedWebsite }}</div></div><div><label class="block text-sm text-gray-400 mb-1">资源路径或规则</label><input v-model="addPath" class="input font-mono" placeholder="/api/* 或 /static/{**match}" @keydown.enter="addResource" /></div><p class="text-xs text-gray-500">支持精确 Path、/*、/static/*、/static/{**match} 和 /static/{**match**}。通配规则会在实际路径首次访问时分别建立基线。</p></div><div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border"><button class="btn btn-secondary" @click="showAdd = false">取消</button><button class="btn btn-primary" :disabled="adding" @click="addResource">{{ adding ? '处理中...' : '添加保护资源' }}</button></div></div></div></Teleport>

  <Teleport to="body"><div v-if="comparison" class="fixed inset-0 z-[100] flex items-center justify-center p-4"><div class="absolute inset-0 bg-black/70" @click="comparison = null"></div><div class="relative w-[1100px] max-w-full max-h-[90vh] flex flex-col bg-dark-card border border-dark-border rounded-lg shadow-2xl"><div class="flex items-center justify-between px-6 py-4 border-b border-dark-border"><div><h3 class="text-lg font-semibold">内容对比</h3><p class="text-xs text-gray-500 mt-1">{{ comparison.resource.url }}</p></div><button class="text-xl text-gray-400" title="关闭" @click="comparison = null">×</button></div><div class="grid grid-cols-1 md:grid-cols-2 gap-px bg-dark-border min-h-0 overflow-hidden"><div class="bg-dark-card p-4 min-h-0"><div class="text-sm text-green-400 mb-2">已确认的安全基线</div><pre class="h-[55vh] overflow-auto p-3 bg-dark-bg text-xs text-gray-300 whitespace-pre-wrap break-all">{{ comparison.baselineContent }}</pre></div><div class="bg-dark-card p-4 min-h-0"><div class="text-sm" :class="comparison.changed ? 'text-red-400' : 'text-green-400'">当前上游内容</div><pre class="h-[55vh] overflow-auto p-3 mt-2 bg-dark-bg text-xs text-gray-300 whitespace-pre-wrap break-all">{{ comparison.currentContent }}</pre></div></div><div class="flex flex-col sm:flex-row justify-end gap-3 px-6 py-4 border-t border-dark-border"><button class="btn btn-secondary" @click="comparison = null">继续使用安全基线</button><button v-if="comparison.changed" class="btn btn-warning" @click="acceptCurrent">确认正常，更新基线</button></div></div></div></Teleport>

  <Teleport to="body"><div v-if="deleteTarget" class="fixed inset-0 z-[100] flex items-center justify-center p-4"><div class="absolute inset-0 bg-black/60" @click="deleteTarget = null"></div><div class="relative w-[440px] max-w-full bg-dark-card border border-dark-border rounded-lg"><div class="p-6"><h3 class="text-lg font-semibold mb-2">删除保护资源</h3><p class="text-gray-400">删除“{{ deleteTarget.name }}”及其安全基线和事件记录？</p><p v-if="deleteTarget.parentResourceId" class="text-xs text-yellow-400 mt-3">该路径仍匹配上级目录规则，下次收到请求时会重新收集并建立基线。需要长期忽略时请使用“排除”。</p></div><div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border"><button class="btn btn-secondary" @click="deleteTarget = null">取消</button><button class="btn btn-danger" @click="removeResource">删除</button></div></div></div></Teleport>
</template>
