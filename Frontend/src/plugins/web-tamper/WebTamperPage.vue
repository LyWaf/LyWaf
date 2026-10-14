<script setup lang="ts">
import { locale } from '@/i18n'
import { t } from '@/plugins/web-tamper/i18n'
import { computed, onMounted, ref } from 'vue'
import { webTamperApi, type WebTamperComparison, type WebTamperEvent, type WebTamperResource, type WebTamperWebsite } from './api'
import { createUnifiedDiff } from './diff'
import { useToast } from '@/composables/useToast'
import { useRouter } from 'vue-router'

const { showSuccess, showError } = useToast()
const router = useRouter()
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
const comparisonView = ref<'diff' | 'baseline' | 'current'>('diff')
const expandedDiffSections = ref<string[]>([])
const comparisonDiff = computed(() => comparison.value
  ? createUnifiedDiff(comparison.value.baselineContent, comparison.value.currentContent)
  : [])
const visibleComparisonDiff = computed(() => comparisonDiff.value.flatMap(row =>
  row.kind === 'skip' && row.sectionId && expandedDiffSections.value.includes(row.sectionId)
    ? [row, ...(row.collapsedRows || [])]
    : [row]))
const toggleDiffSection = (sectionId?: string) => {
  if (!sectionId) return
  expandedDiffSections.value = expandedDiffSections.value.includes(sectionId)
    ? expandedDiffSections.value.filter(item => item !== sectionId)
    : [...expandedDiffSections.value, sectionId]
}
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
  if (!resource) return showError(t('page.629973982895'))
  return checkResource(resource)
}
const formatTime = (value?: string) => value ? new Intl.DateTimeFormat(locale.value, {
  year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit'
}).format(new Date(value)) : t('page.cdbc0434f568')
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
  } catch { showError(t('page.b419abc9543a')) }
  finally { loading.value = false }
}

const addResource = async () => {
  if (!selectedWebsite.value) return showError(t('page.c66d3963af13'))
  if (!addPath.value.trim()) return showError(t('page.8d65f5b0504b'))
  adding.value = true
  try {
    const result = await webTamperApi.add(selectedWebsite.value, addPath.value.trim())
    if (!result.success) return showError(result.message || t('page.2ca15f1e2bb7'))
    showSuccess(result.message || t('page.60cf8b7d329c'))
    showAdd.value = false
    addPath.value = '/'
    await loadData()
  } catch { showError(t('page.cd9026a5a6bb')) }
  finally { adding.value = false }
}

const addWebsite = async () => {
  if (!websiteForm.value.name.trim() || !websiteForm.value.domain.trim()) return showError(t('page.7e28f9f8c14f'))
  const port = websiteForm.value.port ? Number(websiteForm.value.port) : undefined
  if (port !== undefined && (!Number.isInteger(port) || port < 1 || port > 65535)) return showError(t('page.41ce32303077'))
  websiteSaving.value = true
  try {
    const result = await webTamperApi.addWebsite(websiteForm.value.name.trim(), websiteForm.value.domain.trim(), port)
    if (!result.success) return showError(result.message || t('page.4a4f0e65630e'))
    const domain = result.website?.domain || `${websiteForm.value.domain.trim().toLowerCase()}${port ? `:${port}` : ''}`
    websiteForm.value = { name: '', domain: '', port: '' }
    showAddWebsite.value = false
    await loadData()
    if (websites.value.some(item => item.domain === domain)) selectedWebsite.value = domain
    showSuccess(t('page.39e637bab52d'))
  } catch { showError(t('page.4a4f0e65630e')) }
  finally { websiteSaving.value = false }
}

const deleteWebsite = async () => {
  if (!currentWebsite.value?.removable) return
  try {
    const result = await webTamperApi.deleteWebsite(currentWebsite.value.id)
    if (!result.success) return showError(result.message || t('page.c34f07802890'))
    selectedWebsite.value = ''
    showDeleteWebsite.value = false
    await loadData()
    showSuccess(t('page.1325d1490641'))
  } catch { showError(t('page.c34f07802890')) }
}

const openRenameWebsite = () => {
  if (!currentWebsite.value) return
  websiteRemark.value = currentWebsite.value.name
  showRenameWebsite.value = true
}

const renameWebsite = async () => {
  if (!currentWebsite.value || !websiteRemark.value.trim()) return showError(t('page.72f5c43c953f'))
  websiteSaving.value = true
  try {
    const result = await webTamperApi.renameWebsite(websiteRemark.value.trim(), currentWebsite.value.domain, currentWebsite.value.source)
    if (!result.success) return showError(result.message || t('page.953dac6a67de'))
    showRenameWebsite.value = false
    await loadData()
    showSuccess(t('page.d9ac7be90605'))
  } catch { showError(t('page.953dac6a67de')) }
  finally { websiteSaving.value = false }
}

const toggleResource = async (resource: WebTamperResource) => {
  busyId.value = resource.id
  const result = await webTamperApi.update(resource.id, resource.name, !resource.enabled)
  if (result.success) {
    resource.enabled = !resource.enabled
    showSuccess(resource.parentResourceId
      ? (resource.enabled ? t('page.17034bbfa00a') : t('page.90522697e6ab'))
      : (resource.enabled ? t('page.dff24fbc3c23') : t('page.75e74a9c1074')))
  } else showError(result.message || t('page.0c3b4cf7aa25'))
  busyId.value = null
}

const checkResource = async (resource: WebTamperResource) => {
  busyId.value = resource.id
  checkingId.value = resource.id
  try {
    const result = await webTamperApi.check(resource.id)
    if (!result.success || !result.comparison) {
      const message = result.message || t('page.4f6f06254d16')
      return showError(/超时|timed out|timeout|taskcanceled/i.test(message) ? t('page.7f1f4ddd29c2') : message)
    }
    comparison.value = result.comparison
    comparisonView.value = 'diff'
    expandedDiffSections.value = []
    showSuccess(result.comparison.changed ? t('page.c9f3e0cc03ea') : t('page.383e87f7f2e7'))
    await loadData()
  } catch (error: unknown) {
    const requestError = error as { code?: string; message?: string }
    showError(requestError.code === 'ECONNABORTED' || /timeout/i.test(requestError.message || '')
      ? t('page.7f1f4ddd29c2')
      : t('page.fdd1059f10c4'))
  }
  finally { busyId.value = null; checkingId.value = null }
}

const acceptCurrent = async () => {
  if (!comparison.value) return
  busyId.value = comparison.value.resource.id
  try {
    const result = await webTamperApi.accept(comparison.value.resource.id)
    if (!result.success) return showError(result.message || t('page.5c19d266ea85'))
    comparison.value = null
    showSuccess(t('page.5870412a0096'))
    await loadData()
  } catch { showError(t('page.5c19d266ea85')) }
  finally { busyId.value = null }
}

const removeResource = async () => {
  if (!deleteTarget.value) return
  const result = await webTamperApi.delete(deleteTarget.value.id)
  if (result.success) {
    showSuccess(t('page.488994ec8a97'))
    deleteTarget.value = null
    await loadData()
  } else showError(result.message || t('page.c228558cf257'))
}

onMounted(loadData)
</script>

<template>
  <div class="space-y-6">
    <div class="flex items-start justify-between gap-4">
      <div>
        <h1 class="text-xl font-semibold text-gray-100">{{ t('page.1a18e961c05b') }}</h1>
        <p class="text-sm text-gray-500 mt-1">{{ t('page.af08c92e01b4') }}</p>
      </div>
      <button class="btn btn-secondary btn-sm shrink-0" @click="router.push('/plugin-management')">{{ t('page.2a230a75835d') }}</button>
    </div>

    <section class="bg-dark-card border border-dark-border rounded-lg p-4">
      <div class="flex items-center justify-between gap-4 mb-2">
        <label class="block text-xs text-gray-500">{{ t('page.6b5803713d76') }}</label>
        <button class="btn btn-primary btn-sm" @click="showAddWebsite = true">{{ t('page.6181670fef73') }}</button>
      </div>
      <div class="flex flex-col sm:flex-row gap-3">
        <select v-model="selectedWebsite" class="input max-w-lg">
          <option value="" disabled>{{ t('page.ec2157f0082c') }}</option>
          <option v-for="site in websites" :key="site.domain" :value="site.domain">{{ site.name }}（{{ site.domain }}）</option>
        </select>
        <button v-if="currentWebsite" class="btn btn-secondary shrink-0" @click="openRenameWebsite">{{ t('page.7886e13b90d0') }}</button>
        <button v-if="currentWebsite?.removable" class="btn btn-danger shrink-0" :title="t('page.3ab802d8b96e')" @click="showDeleteWebsite = true">{{ t('page.82128d22bad8') }}</button>
      </div>
      <div v-if="currentWebsite" class="flex items-center gap-2 mt-3 text-xs text-gray-500">
        <span :class="currentWebsite.source === 'manual' ? 'badge badge-info' : 'badge badge-success'">{{ currentWebsite.source === 'manual' ? t('page.ddfe2c796c7e') : t('page.ac6c5e186b42') }}</span>
        <span>{{ t('page.912ae8a89aba') }}</span>
      </div>
      <p v-if="!loading && !websites.length" class="text-sm text-yellow-400 mt-3">{{ t('page.9ad8873354f4') }}</p>
    </section>

    <div class="flex items-center justify-between gap-4 border-b border-dark-border">
      <div class="flex">
        <button class="px-5 py-3 text-sm border-b-2" :class="activeTab === 'resources' ? 'text-primary-400 border-primary-500' : 'text-gray-500 border-transparent'" @click="activeTab = 'resources'">{{ t('page.ca028f59737e') }} {{ visibleResources.length }}</button>
        <button class="px-5 py-3 text-sm border-b-2" :class="activeTab === 'events' ? 'text-primary-400 border-primary-500' : 'text-gray-500 border-transparent'" @click="activeTab = 'events'">{{ t('page.9b1334cb3065') }} <span v-if="unresolvedCount" class="ml-1 badge badge-danger">{{ unresolvedCount }}</span></button>
      </div>
      <div class="flex items-center gap-2 mb-2">
        <button class="btn btn-secondary btn-sm shrink-0" :disabled="loading" @click="loadData">{{ loading ? t('page.609140d9c661') : t('page.aee887434131') }}</button>
        <button v-if="activeTab === 'resources'" class="btn btn-primary btn-sm shrink-0" :disabled="!selectedWebsite" @click="showAdd = true">{{ t('page.ba49df54e92a') }}</button>
      </div>
    </div>

    <section v-if="activeTab === 'resources'" class="bg-dark-card border border-dark-border rounded-lg overflow-hidden">
      <div class="p-4 border-b border-dark-border"><input v-model="search" class="input max-w-sm" :placeholder="t('page.03c88397009d')" /></div>
      <div class="overflow-x-auto">
        <table class="w-full min-w-[900px] text-sm">
          <thead class="bg-dark-card-hover text-gray-400"><tr><th class="text-left px-5 py-3 font-medium">{{ t('page.daa6d02a55bc') }}</th><th class="text-left px-5 py-3 font-medium">{{ t('page.6320b4a8722a') }}</th><th class="text-left px-5 py-3 font-medium">{{ t('page.8846cd7c325e') }}</th><th class="text-left px-5 py-3 font-medium">{{ t('page.25910becc7a5') }}</th><th class="text-right px-5 py-3 font-medium">{{ t('page.ed31fbb483ee') }}</th></tr></thead>
          <tbody class="divide-y divide-dark-border">
            <template v-for="item in visibleResources" :key="item.id">
              <tr class="hover:bg-dark-card-hover/60">
                <td class="px-5 py-4">
                  <div class="flex items-center gap-2">
                    <button v-if="item.isPattern" class="w-6 h-6 shrink-0 text-gray-400 hover:text-gray-100" :title="isPatternExpanded(item.id) ? t('page.47c91fadcc3e') : t('page.a49f5287fc9e')" @click="togglePatternDetails(item.id)">{{ isPatternExpanded(item.id) ? '⌄' : '›' }}</button>
                    <span v-else class="w-6 shrink-0"></span>
                    <span class="font-medium font-mono text-gray-200">{{ item.path }}</span>
                    <span v-if="item.isPattern" class="badge badge-info">{{ t('page.ded3763dd8ee') }}</span>
                  </div>
                  <div class="text-xs text-gray-500 mt-1 ml-8 max-w-md truncate">{{ item.website }}<span v-if="item.isPattern"> {{ t('page.85b7f5003f32') }} {{ item.capturedCount }} {{ t('page.6d4f094c5337') }}</span></div>
                </td>
                <td class="px-5 py-4"><span v-if="checkingId === item.id" class="inline-flex items-center gap-2 text-xs text-primary-400"><span class="w-3.5 h-3.5 border-2 border-primary-400/40 border-t-primary-400 rounded-full animate-spin"></span>{{ t('page.d85ff08f9141') }}</span><span v-else-if="item.changed" class="badge badge-danger">{{ t('page.3203b29889e5') }}</span><span v-else-if="item.enabled" class="badge badge-success">{{ t('page.be8f11d5a2ed') }}</span><span v-else class="badge badge-warning">{{ t('page.eb0c326b60ae') }}</span></td>
                <td class="px-5 py-4 font-mono text-xs text-gray-400">{{ item.isPattern ? t('page.9d6f5cf4832f', { p0: item.capturedCount }) : shortHash(item.baselineHash) }}</td>
                <td class="px-5 py-4 text-gray-400 whitespace-nowrap">{{ formatTime(item.lastCheckedAt) }}</td>
                <td class="px-5 py-4 text-right whitespace-nowrap">
                  <button class="inline-flex items-center gap-1.5 text-primary-400 hover:text-primary-300 mr-4 disabled:opacity-70" :disabled="busyId === item.id" @click="checkResource(item)"><span v-if="checkingId === item.id" class="w-3.5 h-3.5 border-2 border-current/40 border-t-current rounded-full animate-spin"></span>{{ checkingId === item.id ? t('page.2a114c7e95b7') : t('page.978e275133d1') }}</button>
                  <button class="text-gray-400 hover:text-gray-200 mr-4" :disabled="busyId === item.id" @click="toggleResource(item)">{{ item.enabled ? t('page.8d12fc0d4eb2') : t('page.f4f0ead1116b') }}</button>
                  <button class="text-red-400 hover:text-red-300" @click="deleteTarget = item">{{ t('page.2f9daa828907') }}</button>
                </td>
              </tr>
              <tr v-if="item.isPattern && isPatternExpanded(item.id)" class="bg-dark-bg/40">
                <td colspan="5" class="p-0">
                  <div v-if="item.capturedResources?.length" class="py-2">
                    <div v-for="row in buildResourceTree(item.capturedResources)" :key="row.key" class="grid grid-cols-[minmax(260px,1fr)_130px_150px_280px] items-center min-h-10 px-5 hover:bg-dark-card-hover/50" :style="{ paddingLeft: `${44 + row.depth * 24}px` }">
                      <div class="flex items-center min-w-0 gap-2">
                        <span class="w-4 text-gray-600">{{ row.kind === 'directory' ? '└' : '·' }}</span>
                        <span v-if="row.kind === 'directory'" class="text-xs text-gray-500">{{ t('page.52daa71ebc31') }}</span>
                        <span class="font-mono text-xs truncate" :class="row.kind === 'directory' ? 'text-gray-400' : 'text-gray-200'">{{ row.label }}{{ row.kind === 'directory' ? '/' : '' }}</span>
                      </div>
                      <div v-if="row.resource"><span v-if="checkingId === row.resource.id" class="inline-flex items-center gap-2 text-xs text-primary-400"><span class="w-3.5 h-3.5 border-2 border-primary-400/40 border-t-primary-400 rounded-full animate-spin"></span>{{ t('page.d85ff08f9141') }}</span><span v-else-if="!row.resource.enabled" class="badge badge-warning">{{ t('page.c6f3c8b82c24') }}</span><span v-else-if="row.resource.changed" class="badge badge-danger">{{ t('page.3203b29889e5') }}</span><span v-else class="badge badge-success">{{ t('page.ddd7edc7b6a7') }}</span></div><div v-else></div>
                      <div class="font-mono text-xs text-gray-500">{{ row.resource ? shortHash(row.resource.baselineHash) : '' }}</div>
                      <div v-if="row.resource" class="flex items-center justify-end gap-4 whitespace-nowrap"><span class="text-xs text-gray-500">{{ formatTime(row.resource.lastCheckedAt) }}</span><button class="inline-flex items-center gap-1.5 text-xs text-primary-400 hover:text-primary-300 disabled:opacity-70" :disabled="busyId === row.resource.id" @click="checkResource(row.resource)"><span v-if="checkingId === row.resource.id" class="w-3 h-3 border-2 border-current/40 border-t-current rounded-full animate-spin"></span>{{ checkingId === row.resource.id ? t('page.2a114c7e95b7') : t('page.978e275133d1') }}</button><button class="text-xs text-gray-400 hover:text-gray-200" :disabled="busyId === row.resource.id" @click="toggleResource(row.resource)">{{ row.resource.enabled ? t('page.ac83d3972fe9') : t('page.e0534b8a4e46') }}</button><button class="text-xs text-red-400 hover:text-red-300" @click="deleteTarget = row.resource">{{ t('page.2f9daa828907') }}</button></div>
                    </div>
                  </div>
                  <div v-else class="px-12 py-6 text-sm text-gray-500">{{ t('page.fcec2274aae8') }}</div>
                </td>
              </tr>
            </template>
            <tr v-if="!loading && !visibleResources.length"><td colspan="5" class="py-14 text-center text-gray-500">{{ t('page.b1aa9b92531c') }}</td></tr>
            <tr v-if="loading"><td colspan="5" class="py-14 text-center text-gray-500">{{ t('page.9dc0825fba54') }}</td></tr>
          </tbody>
        </table>
      </div>
    </section>

    <section v-else class="bg-dark-card border border-dark-border rounded-lg overflow-hidden">
      <div class="overflow-x-auto"><table class="w-full min-w-[780px] text-sm">
        <thead class="bg-dark-card-hover text-gray-400"><tr><th class="text-left px-5 py-3 font-medium">{{ t('page.daa6d02a55bc') }}</th><th class="text-left px-5 py-3 font-medium">{{ t('page.057023df9d31') }}</th><th class="text-left px-5 py-3 font-medium">{{ t('page.48c6d51b653f') }}</th><th class="text-left px-5 py-3 font-medium">{{ t('page.6320b4a8722a') }}</th><th class="text-right px-5 py-3 font-medium">{{ t('page.ed31fbb483ee') }}</th></tr></thead>
        <tbody class="divide-y divide-dark-border">
          <tr v-for="event in visibleEvents" :key="event.id"><td class="px-5 py-4"><div class="text-gray-200 font-mono">{{ event.resourceName }}</div><div class="text-xs text-gray-500 truncate max-w-sm">{{ event.url }}</div></td><td class="px-5 py-4 text-gray-400">{{ formatTime(event.detectedAt) }}</td><td class="px-5 py-4 font-mono text-xs text-gray-500">{{ shortHash(event.baselineHash) }} → {{ shortHash(event.currentHash) }}</td><td class="px-5 py-4"><span :class="event.resolved ? 'badge badge-success' : 'badge badge-danger'">{{ event.resolved ? t('page.59f6c8369293') : t('page.a8b0bd9c65f7') }}</span></td><td class="px-5 py-4 text-right"><button class="inline-flex items-center gap-1.5 text-primary-400 hover:text-primary-300 disabled:opacity-60 whitespace-nowrap" :disabled="busyId === resourceForEvent(event)?.id || !resourceForEvent(event)" @click="inspectEvent(event)"><span v-if="checkingId === resourceForEvent(event)?.id" class="w-3.5 h-3.5 border-2 border-current/40 border-t-current rounded-full animate-spin"></span>{{ checkingId === resourceForEvent(event)?.id ? t('page.54b3eab8deb4') : (event.resolved ? t('page.393dd8a7f29e') : t('page.f7381c80595f')) }}</button></td></tr>
          <tr v-if="!visibleEvents.length"><td colspan="5" class="py-14 text-center text-gray-500">{{ t('page.2f51b2b93362') }}</td></tr>
        </tbody>
      </table></div>
    </section>
  </div>

  <Teleport to="body"><div v-if="showAddWebsite" class="fixed inset-0 z-[100] flex items-center justify-center p-4"><div class="absolute inset-0 bg-black/60" @click="showAddWebsite = false"></div><div class="relative w-[500px] max-w-full bg-dark-card border border-dark-border rounded-lg shadow-2xl"><div class="flex items-center justify-between px-6 py-4 border-b border-dark-border"><h3 class="text-lg font-semibold">{{ t('page.7117ea5ac4de') }}</h3><button class="text-xl text-gray-400" :title="t('page.3fd47edce45b')" @click="showAddWebsite = false">×</button></div><div class="p-6 space-y-4"><div><label class="block text-sm text-gray-400 mb-1">{{ t('page.5a632d88aad2') }}</label><input v-model="websiteForm.name" class="input" :placeholder="t('page.e58cdb148635')" /></div><div class="grid grid-cols-[minmax(0,1fr)_120px] gap-3"><div><label class="block text-sm text-gray-400 mb-1">{{ t('page.222952431147') }}</label><input v-model="websiteForm.domain" class="input" placeholder="www.example.com" /></div><div><label class="block text-sm text-gray-400 mb-1">{{ t('page.e71ac32b544b') }}</label><input v-model="websiteForm.port" class="input" type="number" min="1" max="65535" :placeholder="t('page.537352400c49')" @keydown.enter="addWebsite" /></div></div><p class="text-xs text-gray-500">{{ t('page.cc4d0222b1a4') }}</p></div><div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border"><button class="btn btn-secondary" @click="showAddWebsite = false">{{ t('page.2cd0f3be8738') }}</button><button class="btn btn-primary" :disabled="websiteSaving" @click="addWebsite">{{ websiteSaving ? t('page.0dae682eddac') : t('page.6181670fef73') }}</button></div></div></div></Teleport>

  <Teleport to="body"><div v-if="showRenameWebsite && currentWebsite" class="fixed inset-0 z-[100] flex items-center justify-center p-4"><div class="absolute inset-0 bg-black/60" @click="showRenameWebsite = false"></div><div class="relative w-[460px] max-w-full bg-dark-card border border-dark-border rounded-lg shadow-2xl"><div class="flex items-center justify-between px-6 py-4 border-b border-dark-border"><h3 class="text-lg font-semibold">{{ t('page.fb7101e684c0') }}</h3><button class="text-xl text-gray-400" :title="t('page.3fd47edce45b')" @click="showRenameWebsite = false">×</button></div><div class="p-6 space-y-4"><div><label class="block text-sm text-gray-400 mb-1">{{ t('page.d85326ae1951') }}</label><input v-model="websiteRemark" class="input" maxlength="80" :placeholder="t('page.e58cdb148635')" @keydown.enter="renameWebsite" /></div><div><label class="block text-sm text-gray-400 mb-1">{{ t('page.222952431147') }}</label><div class="px-4 py-2 bg-dark-bg border border-dark-border rounded-lg text-gray-500">{{ currentWebsite.domain }}</div></div></div><div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border"><button class="btn btn-secondary" @click="showRenameWebsite = false">{{ t('page.2cd0f3be8738') }}</button><button class="btn btn-primary" :disabled="websiteSaving" @click="renameWebsite">{{ websiteSaving ? t('page.917b1c1f18d0') : t('page.4f8b0ab088ec') }}</button></div></div></div></Teleport>

  <Teleport to="body"><div v-if="showDeleteWebsite && currentWebsite" class="fixed inset-0 z-[100] flex items-center justify-center p-4"><div class="absolute inset-0 bg-black/60" @click="showDeleteWebsite = false"></div><div class="relative w-[430px] max-w-full bg-dark-card border border-dark-border rounded-lg shadow-2xl"><div class="p-6"><h3 class="text-lg font-semibold mb-2">{{ t('page.ac972534e92f') }}</h3><p class="text-gray-400">{{ t('page.6ec00e356283') }}{{ currentWebsite.name }}{{ t('page.ea7b33ce357d') }}</p></div><div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border"><button class="btn btn-secondary" @click="showDeleteWebsite = false">{{ t('page.2cd0f3be8738') }}</button><button class="btn btn-danger" @click="deleteWebsite">{{ t('page.2f9daa828907') }}</button></div></div></div></Teleport>

  <Teleport to="body"><div v-if="showAdd" class="fixed inset-0 z-[100] flex items-center justify-center p-4"><div class="absolute inset-0 bg-black/60" @click="showAdd = false"></div><div class="relative w-[520px] max-w-full bg-dark-card border border-dark-border rounded-lg shadow-2xl"><div class="flex items-center justify-between px-6 py-4 border-b border-dark-border"><h3 class="text-lg font-semibold">{{ t('page.ba49df54e92a') }}</h3><button class="text-xl text-gray-400" :title="t('page.3fd47edce45b')" @click="showAdd = false">×</button></div><div class="p-6 space-y-4"><div><label class="block text-sm text-gray-400 mb-1">{{ t('page.6b5803713d76') }}</label><div class="px-4 py-2 bg-dark-bg border border-dark-border rounded-lg text-gray-300">{{ selectedWebsite }}</div></div><div><label class="block text-sm text-gray-400 mb-1">{{ t('page.0be00218025d') }}</label><input v-model="addPath" class="input font-mono" :placeholder="t('page.5f82527478dc')" @keydown.enter="addResource" /></div><p class="text-xs text-gray-500">{{ t('page.7f51d315ddfd') }}</p></div><div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border"><button class="btn btn-secondary" @click="showAdd = false">{{ t('page.2cd0f3be8738') }}</button><button class="btn btn-primary" :disabled="adding" @click="addResource">{{ adding ? t('page.c1fd812f81ca') : t('page.ba49df54e92a') }}</button></div></div></div></Teleport>

  <Teleport to="body">
    <div v-if="comparison" class="fixed inset-0 z-[100] flex items-center justify-center p-4">
      <div class="absolute inset-0 bg-black/70" @click="comparison = null"></div>
      <div class="relative w-[1400px] max-w-full max-h-[90vh] flex flex-col bg-dark-card border border-dark-border rounded-lg shadow-2xl">
        <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border">
          <div class="min-w-0">
            <h3 class="text-lg font-semibold">{{ t('page.2fc04feadd06') }}</h3>
            <p class="text-xs text-gray-500 mt-1 truncate">{{ comparison.resource.url }}</p>
          </div>
          <button class="text-xl text-gray-400" :title="t('page.3fd47edce45b')" @click="comparison = null">×</button>
        </div>
        <div class="flex flex-wrap items-center justify-between gap-3 px-5 py-2 border-b border-dark-border bg-dark-bg/40">
          <div class="inline-flex overflow-hidden rounded border border-dark-border text-xs">
            <button class="px-3 py-1.5" :class="comparisonView === 'diff' ? 'bg-primary-500 text-dark-bg' : 'text-gray-400 hover:bg-white/5'" @click="comparisonView = 'diff'">{{ t('page.9032c3c2b7e6') }}</button>
            <button class="border-l border-dark-border px-3 py-1.5" :class="comparisonView === 'baseline' ? 'bg-primary-500 text-dark-bg' : 'text-gray-400 hover:bg-white/5'" @click="comparisonView = 'baseline'">{{ t('page.564ef5abe24d') }}</button>
            <button class="border-l border-dark-border px-3 py-1.5" :class="comparisonView === 'current' ? 'bg-primary-500 text-dark-bg' : 'text-gray-400 hover:bg-white/5'" @click="comparisonView = 'current'">{{ t('page.98bee8c41a5e') }}</button>
          </div>
          <div v-if="comparisonView === 'diff'" class="flex items-center gap-5 text-xs">
            <span class="text-red-400">{{ t('page.5f5ddcc572dc') }}</span>
            <span class="text-green-400">{{ t('page.d86a1eaad6ae') }}</span>
          </div>
        </div>
        <div v-if="comparisonView === 'diff'" class="h-[58vh] min-h-0 overflow-auto bg-[#0d1117] font-mono text-xs leading-5">
          <div v-for="(row, index) in visibleComparisonDiff" :key="`${row.sectionId || 'line'}-${row.oldLine || 0}-${row.newLine || 0}-${index}`"
            class="grid min-w-max grid-cols-[3.5rem_3.5rem_1.5rem_minmax(40rem,1fr)] border-l-2"
            :class="row.kind === 'removed' ? 'bg-red-950/40 border-red-500' : row.kind === 'added' ? 'bg-green-950/40 border-green-500' : row.kind === 'skip' ? 'bg-blue-950/30 border-blue-500' : 'border-transparent hover:bg-white/[0.03]'">
            <template v-if="row.kind === 'skip'">
              <button class="col-span-4 px-4 py-1 text-left text-blue-400 hover:bg-blue-500/10 hover:text-blue-300" @click="toggleDiffSection(row.sectionId)">
                {{ row.sectionId && expandedDiffSections.includes(row.sectionId) ? t('page.afd4b783536b') : t('page.00bd3960fea8') }} {{ row.skipped }} {{ t('page.5dfda3677a3c') }}
              </button>
            </template>
            <template v-else>
              <span class="select-none border-r border-white/5 px-2 text-right text-gray-600">{{ row.oldLine ?? '' }}</span>
              <span class="select-none border-r border-white/5 px-2 text-right text-gray-600">{{ row.newLine ?? '' }}</span>
              <span class="select-none text-center" :class="row.kind === 'removed' ? 'text-red-400' : row.kind === 'added' ? 'text-green-400' : 'text-gray-600'">{{ row.kind === 'removed' ? '−' : row.kind === 'added' ? '+' : ' ' }}</span>
              <code class="pr-5 whitespace-pre text-gray-300">{{ row.text || ' ' }}</code>
            </template>
          </div>
        </div>
        <div v-else class="h-[58vh] min-h-0 overflow-auto bg-[#0d1117]">
          <pre class="min-w-max p-4 font-mono text-xs leading-5 text-gray-300 whitespace-pre">{{ comparisonView === 'baseline' ? comparison.baselineContent : comparison.currentContent }}</pre>
        </div>
        <div class="flex flex-col sm:flex-row justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button class="btn btn-secondary" @click="comparison = null">{{ t('page.d38c3e73182e') }}</button>
          <button v-if="comparison.changed" class="btn btn-warning" @click="acceptCurrent">{{ t('page.e85f04f01e3e') }}</button>
        </div>
      </div>
    </div>
  </Teleport>

  <Teleport to="body"><div v-if="deleteTarget" class="fixed inset-0 z-[100] flex items-center justify-center p-4"><div class="absolute inset-0 bg-black/60" @click="deleteTarget = null"></div><div class="relative w-[440px] max-w-full bg-dark-card border border-dark-border rounded-lg"><div class="p-6"><h3 class="text-lg font-semibold mb-2">{{ t('page.f4a2c466a2bb') }}</h3><p class="text-gray-400">{{ t('page.0ba78097d8ca') }}{{ deleteTarget.name }}{{ t('page.535b86d6b72f') }}</p><p v-if="deleteTarget.parentResourceId" class="text-xs text-yellow-400 mt-3">{{ t('page.de22960041b3') }}</p></div><div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border"><button class="btn btn-secondary" @click="deleteTarget = null">{{ t('page.2cd0f3be8738') }}</button><button class="btn btn-danger" @click="removeResource">{{ t('page.2f9daa828907') }}</button></div></div></div></Teleport>
</template>
