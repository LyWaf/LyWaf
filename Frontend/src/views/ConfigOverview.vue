<script setup lang="ts">
import { t } from '@/i18n'
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { overviewApi, routeApi, simpleResApi, fileServerApi, listenApi, serviceApi } from '@/api'
import { useToast } from '@/composables/useToast'
import type { ListenInfo, CertInfo, OverviewCluster, ListenBoundRoute, SimpleResItem, FileServerItem, RouteConfig } from '@/types'

const router = useRouter()
const { showSuccess, showError } = useToast()

const loading = ref(false)
const listens = ref<ListenInfo[]>([])
const controlListen = ref<{ host: string; port: number } | null>(null)
const certs = ref<CertInfo[]>([])
const clusters = ref<OverviewCluster[]>([])

// ============== 加载数据 ==============
const loadData = async () => {
  loading.value = true
  try {
    const response = await overviewApi.getData()
    if (response.success) {
      listens.value = response.listens
      controlListen.value = response.controlListen
      certs.value = response.certs
      clusters.value = response.clusters
    }
  } catch (error: any) {
    showError(error?.response?.data?.message || t('page.9d2ed714c50b'))
  } finally {
    loading.value = false
  }
}

// ============== 新增监听端口 ==============
const showListenDialog = ref(false)
const listenSaving = ref(false)
const listenForm = ref({ host: '0.0.0.0', port: 0, isHttps: false })
const listenHosts = ref<string[]>([])

// IP 地址校验（IPv4 / IPv6）
const isValidIP = (ip: string): boolean => {
  // IPv4
  if (/^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.test(ip)) {
    return ip.split('.').every(n => Number(n) >= 0 && Number(n) <= 255)
  }
  // IPv6 常见简写: ::, ::1, 0:0:0:0:0:0:0:0 等
  if (/^[0-9a-fA-F:]+$/.test(ip) && ip.includes(':')) return true
  return false
}

// 重启确认
const showRestartConfirm = ref(false)
const restarting = ref(false)

const confirmRestart = async () => {
  restarting.value = true
  try {
    await serviceApi.restart()
    showSuccess(t('page.e070b338a9f2'))
    showRestartConfirm.value = false
  } catch (error: any) {
    showError(error?.response?.data?.message || error?.message || t('page.25aa8c9992a0'))
  } finally {
    restarting.value = false
  }
}

const openListenDialog = () => {
  listenForm.value = { host: '0.0.0.0', port: 0, isHttps: false }
  listenHosts.value = ['']
  showListenDialog.value = true
}

const submitListen = async () => {
  const { host, port, isHttps } = listenForm.value
  if (!isValidIP(host)) { showError(t('page.ed4d152b0965')); return }
  if (port <= 0 || port > 65535) { showError(t('page.4eaa9d4ff9b3')); return }
  const hosts = listenHosts.value.filter(h => h.trim())
  listenSaving.value = true
  try {
    const res = await listenApi.add({ host, port, isHttps })
    if (!res.success) { showError(res.message || t('dashboard.failed.to.add')); return }
    // 自动创建默认 SimpleRes 路由
    const routeId = `simpleres_listen_${port}_default`
    const itemRes = await simpleResApi.addItem({
      itemId: routeId,
      body: `<!DOCTYPE html><html><head><meta charset="utf-8"><title>LyWaf - Port ${port}</title></head><body><h1>LyWaf</h1><p>Port ${port} is running.</p></body></html>`,
      contentType: 'text/html',
      statusCode: 200,
    })
    if (!itemRes.success) {
      showError(t('page.a7758d1f2205', { p0: itemRes.message || t('page.13a46616e16f') }))
    } else {
      const routeRes = await routeApi.addRoute({
        routeId,
        clusterId: 'cluster_unuse',
        match: { path: '{**catch-all}', hosts: hosts.length > 0 ? hosts : [`*:${port}`] },
      })
      if (!routeRes.success) {
        showError(t('page.84a4c79e2911', { p0: routeRes.message || t('page.13a46616e16f') }))
      } else {
        showSuccess(t('page.fe4e812c8996'))
      }
    }
    showListenDialog.value = false
    await loadData()
    // 弹出重启确认
    showRestartConfirm.value = true
  } catch (error: any) {
    showError(error?.response?.data?.message || error?.message || t('dashboard.failed.to.add'))
  } finally {
    listenSaving.value = false
  }
}

const confirmDeleteListen = ref<ListenInfo | null>(null)
const deletingListen = ref(false)

const deleteListen = (listen: ListenInfo) => {
  if (listen.source !== 'patch') { showError(t('page.3441ae504fdd')); return }
  confirmDeleteListen.value = listen
}

const executeDeleteListen = async () => {
  const listen = confirmDeleteListen.value
  if (!listen) return
  deletingListen.value = true
  try {
    const res = await listenApi.remove({ host: listen.host, port: listen.port })
    if (!res.success) { showError(res.message || t('settings.deletion.failed')); return }
    showSuccess(t('page.66100f27b84c'))
    confirmDeleteListen.value = null
    await loadData()
    showRestartConfirm.value = true
  } catch (error: any) {
    showError(error?.response?.data?.message || error?.message || t('settings.deletion.failed'))
  } finally {
    deletingListen.value = false
  }
}

// ============== 新增服务对话框 ==============
type ServiceType = 'simpleres' | 'fileserver' | 'proxy'

const showAddDialog = ref(false)
const addStep = ref<'type' | 'form'>('type')
const addPort = ref(0)
const addHost = ref('')
const addServiceType = ref<ServiceType>('simpleres')
const saving = ref(false)
const addHosts = ref<string[]>([])

const addForm = ref({
  itemId: '',
  body: 'Hello World',
  contentType: 'text/plain',
  statusCode: 200,
  headers: [] as { key: string; value: string }[],
  prefix: '/',
  basePath: './wwwroot',
  browse: false,
  preCompressed: false,
  tryFiles: '',
  defaultFiles: '',
  clusterId: '',
  path: '{**catch-all}',
  order: null as number | null,
})

// 常用 Content-Type 选项
const contentTypeOptions = [
  'text/plain',
  'text/html',
  'application/json',
  'application/xml',
  'application/javascript',
  'text/css',
  'text/csv',
  'application/octet-stream',
]

// ContentType 自定义输入模式
const addCtCustom = ref(false)
const editCtCustom = ref(false)

const onAddCtSelect = (e: Event) => {
  const val = (e.target as HTMLSelectElement).value
  if (val === '__custom__') {
    addCtCustom.value = true
    addForm.value.contentType = ''
  } else {
    addForm.value.contentType = val
  }
}

const onEditCtSelect = (e: Event) => {
  const val = (e.target as HTMLSelectElement).value
  if (val === '__custom__') {
    editCtCustom.value = true
    editSimpleRes.value.contentType = ''
  } else {
    editSimpleRes.value.contentType = val
  }
}

const clusterOptions = computed(() => clusters.value.map(c => c.id))

const openAddDialog = (listen: ListenInfo) => {
  addPort.value = listen.port
  addHost.value = listen.host
  addStep.value = 'type'
  addServiceType.value = 'simpleres'
  addCtCustom.value = false
  const host = listen.host === '0.0.0.0' || listen.host === '::' ? '*' : listen.host
  addHosts.value = [`${host}:${listen.port}`]
  addForm.value = {
    itemId: '',
    body: 'Hello World',
    contentType: 'text/plain',
    statusCode: 200,
    headers: [],
    prefix: '/',
    basePath: './wwwroot',
    browse: false,
    preCompressed: false,
    tryFiles: '',
    defaultFiles: '',
    clusterId: clusterOptions.value[0] || '',
    path: '{**catch-all}',
    order: null,
  }
  showAddDialog.value = true
}

const selectType = (type: ServiceType) => {
  addServiceType.value = type
  const ts = Date.now().toString(36)
  if (type === 'simpleres') {
    addForm.value.itemId = `simpleres_${ts}`
  } else if (type === 'fileserver') {
    addForm.value.itemId = `fileserver_${ts}`
  } else {
    addForm.value.itemId = `route_${ts}`
  }
  addForm.value.path = '{**catch-all}'
  addStep.value = 'form'
}

const parseList = (str: string): string[] =>
  str.split(/[,\s]+/).map(s => s.trim()).filter(s => s.length > 0)

const submitAdd = async () => {
  const id = addForm.value.itemId.trim()
  if (!id) { showError(t('page.a45065ad89c2')); return }

  saving.value = true
  try {
    const hosts = addHosts.value.filter(h => h.trim())
    const path = addForm.value.path.trim() || '{**catch-all}'
    const order = addForm.value.order != null ? addForm.value.order : undefined

    if (addServiceType.value === 'simpleres') {
      const hdrs: Record<string, string> = {}
      for (const h of addForm.value.headers) {
        if (h.key.trim()) hdrs[h.key.trim()] = h.value
      }
      const itemRes = await simpleResApi.addItem({
        itemId: id,
        body: addForm.value.body,
        contentType: addForm.value.contentType,
        statusCode: addForm.value.statusCode,
        headers: Object.keys(hdrs).length > 0 ? hdrs : undefined,
      })
      if (!itemRes.success) { showError(itemRes.message || t('page.fae875b66b98')); saving.value = false; return }
      const routeRes = await routeApi.addRoute({ routeId: id, clusterId: 'cluster_unuse', order, match: { path, hosts } })
      if (!routeRes.success) { showError(routeRes.message || t('page.22872291f5df')); saving.value = false; return }
      showSuccess(t('page.0e5645c63cea'))
    } else if (addServiceType.value === 'fileserver') {
      const tryFiles = parseList(addForm.value.tryFiles)
      const defaultFiles = parseList(addForm.value.defaultFiles)
      const itemRes = await fileServerApi.addItem({
        itemId: id,
        prefix: addForm.value.prefix,
        basePath: addForm.value.basePath,
        browse: addForm.value.browse,
        preCompressed: addForm.value.preCompressed,
        tryFiles: tryFiles.length > 0 ? tryFiles : undefined,
        defaultFiles: defaultFiles.length > 0 ? defaultFiles : undefined,
      })
      if (!itemRes.success) { showError(itemRes.message || t('page.4a803e131b82')); saving.value = false; return }
      const routeRes = await routeApi.addRoute({ routeId: id, clusterId: 'cluster_unuse', order, match: { path, hosts } })
      if (!routeRes.success) { showError(routeRes.message || t('page.22872291f5df')); saving.value = false; return }
      showSuccess(t('page.06e0b688fb85'))
    } else {
      if (!addForm.value.clusterId) { showError(t('page.e2ba56010793')); saving.value = false; return }
      const routeRes = await routeApi.addRoute({
        routeId: id,
        clusterId: addForm.value.clusterId,
        order,
        match: { path, hosts },
      })
      if (!routeRes.success) { showError(routeRes.message || t('page.22872291f5df')); saving.value = false; return }
      showSuccess(t('page.c83b53e5f348'))
    }

    showAddDialog.value = false
    await loadData()
  } catch (error: any) {
    showError(error?.response?.data?.message || error?.message || t('dashboard.failed.to.add'))
  } finally {
    saving.value = false
  }
}

// ============== 编辑对话框 ==============
const showEditDialog = ref(false)
const editLoading = ref(false)
const editSaving = ref(false)
const editRoute = ref<ListenBoundRoute | null>(null)

// 简单响应编辑表单
const editSimpleRes = ref({
  body: '',
  contentType: 'text/plain',
  statusCode: 200,
  charset: 'utf-8',
  showReq: false,
  headers: [] as { key: string; value: string }[],
  path: '{**catch-all}',
  order: 0,
})

// 文件服务编辑表单
const editFileServer = ref({
  prefix: '/',
  basePath: './wwwroot',
  browse: false,
  preCompressed: false,
  tryFiles: '',
  defaultFiles: '',
  path: '{**catch-all}',
  order: 0,
})

// 代理路由编辑表单
const editProxy = ref({
  path: '',
  methods: '',
  order: 0,
  clusterId: '',
})

// 所有类型共用的 hosts 编辑
const editHosts = ref<string[]>([])

const openEditDialog = async (route: ListenBoundRoute) => {
  editRoute.value = route
  editLoading.value = true
  showEditDialog.value = true

  try {
    // 加载路由信息（获取 hosts，所有类型都需要）
    const routeRes = await routeApi.getRoutes()
    const routeItem = routeRes.routes?.find((r: RouteConfig) => r.routeId === route.routeId)
    editHosts.value = routeItem?.match?.hosts?.slice() ?? []

    if (route.serviceType === 'simpleres') {
      const res = await simpleResApi.getItems()
      const item = res.items?.find((it: SimpleResItem) => it.itemId === route.routeId)
      if (item) {
        const hdrs: { key: string; value: string }[] = []
        if (item.headers) {
          for (const [k, v] of Object.entries(item.headers)) {
            hdrs.push({ key: k, value: v })
          }
        }
        editCtCustom.value = !contentTypeOptions.includes(item.contentType ?? 'text/plain')
        editSimpleRes.value = {
          body: item.body ?? '',
          contentType: item.contentType ?? 'text/plain',
          statusCode: item.statusCode ?? 200,
          charset: item.charset ?? 'utf-8',
          showReq: item.showReq ?? false,
          headers: hdrs,
          path: routeItem?.match?.path ?? '{**catch-all}',
          order: routeItem?.order ?? 0,
        }
      } else {
        showError(t('page.db413dfbc04d', { p0: route.routeId }))
        showEditDialog.value = false
      }
    } else if (route.serviceType === 'fileserver') {
      const res = await fileServerApi.getItems()
      const item = res.items?.find((it: FileServerItem) => it.itemId === route.routeId)
      if (item) {
        editFileServer.value = {
          prefix: item.prefix ?? '/',
          basePath: item.basePath ?? './wwwroot',
          browse: item.browse ?? false,
          preCompressed: item.preCompressed ?? false,
          tryFiles: item.tryFiles?.join(', ') ?? '',
          defaultFiles: item.defaultFiles?.join(', ') ?? '',
          path: routeItem?.match?.path ?? '{**catch-all}',
          order: routeItem?.order ?? 0,
        }
      } else {
        showError(t('page.c2ee98fc151e', { p0: route.routeId }))
        showEditDialog.value = false
      }
    } else {
      if (routeItem) {
        editProxy.value = {
          path: routeItem.match?.path ?? '',
          methods: routeItem.match?.methods?.join(', ') ?? '',
          order: routeItem.order ?? 0,
          clusterId: routeItem.clusterId ?? '',
        }
      } else {
        showError(t('page.26862e73d3f0', { p0: route.routeId }))
        showEditDialog.value = false
      }
    }
  } catch (error: any) {
    showError(error?.message || t('page.00b3867f7321'))
    showEditDialog.value = false
  } finally {
    editLoading.value = false
  }
}

const submitEdit = async () => {
  const route = editRoute.value
  if (!route) return

  editSaving.value = true
  try {
    // 更新路由 hosts（所有类型共用）
    const newHosts = editHosts.value.filter(h => h.trim())

    if (route.serviceType === 'simpleres') {
      const hdrs: Record<string, string> = {}
      for (const h of editSimpleRes.value.headers) {
        if (h.key.trim()) hdrs[h.key.trim()] = h.value
      }
      const res = await simpleResApi.updateItem({
        itemId: route.routeId,
        body: editSimpleRes.value.body,
        contentType: editSimpleRes.value.contentType,
        statusCode: editSimpleRes.value.statusCode,
        charset: editSimpleRes.value.charset,
        showReq: editSimpleRes.value.showReq,
        headers: hdrs,
      })
      if (!res.success) { showError(res.message || t('page.ec99e5c45d64')); return }
      // 同步更新路由 hosts、path、order
      await routeApi.updateRoute({
        routeId: route.routeId,
        order: editSimpleRes.value.order,
        match: {
          path: editSimpleRes.value.path || undefined,
          hosts: newHosts.length > 0 ? newHosts : undefined,
        },
      })
      showSuccess(t('page.6c7e0a27dd54'))

    } else if (route.serviceType === 'fileserver') {
      const tryFiles = parseList(editFileServer.value.tryFiles)
      const defaultFiles = parseList(editFileServer.value.defaultFiles)
      const res = await fileServerApi.updateItem({
        itemId: route.routeId,
        prefix: editFileServer.value.prefix,
        basePath: editFileServer.value.basePath,
        browse: editFileServer.value.browse,
        preCompressed: editFileServer.value.preCompressed,
        tryFiles,
        defaultFiles,
      })
      if (!res.success) { showError(res.message || t('page.ec99e5c45d64')); return }
      // 同步更新路由 hosts、path、order
      await routeApi.updateRoute({
        routeId: route.routeId,
        order: editFileServer.value.order,
        match: {
          path: editFileServer.value.path || undefined,
          hosts: newHosts.length > 0 ? newHosts : undefined,
        },
      })
      showSuccess(t('page.575b777a757c'))

    } else {
      const methods = parseList(editProxy.value.methods).map(m => m.toUpperCase())
      const res = await routeApi.updateRoute({
        routeId: route.routeId,
        clusterId: editProxy.value.clusterId || undefined,
        order: editProxy.value.order,
        match: {
          path: editProxy.value.path || undefined,
          hosts: newHosts.length > 0 ? newHosts : undefined,
          methods: methods.length > 0 ? methods : undefined,
        },
      })
      if (!res.success) { showError(res.message || t('page.ec99e5c45d64')); return }
      showSuccess(t('page.f8f23707c6e5'))
    }

    showEditDialog.value = false
    await loadData()
  } catch (error: any) {
    showError(error?.response?.data?.message || error?.message || t('page.ec99e5c45d64'))
  } finally {
    editSaving.value = false
  }
}

// ============== 删除 ==============
const deleting = ref<string | null>(null)
const confirmDeleteRoute = ref<ListenBoundRoute | null>(null)

const requestDelete = (route: ListenBoundRoute) => {
  if (route.source !== 'patch') {
    showError(t('page.d4571d4804d2'))
    return
  }
  confirmDeleteRoute.value = route
}

const executeDelete = async () => {
  const route = confirmDeleteRoute.value
  if (!route) return

  deleting.value = route.routeId
  try {
    const routeRes = await routeApi.removeRoute(route.routeId)
    if (!routeRes.success) { showError(routeRes.message || t('page.e2e089545fbd')); return }

    if (route.serviceType === 'simpleres') {
      await simpleResApi.removeItem(route.routeId).catch(() => {})
    } else if (route.serviceType === 'fileserver') {
      await fileServerApi.removeItem(route.routeId).catch(() => {})
    }

    showSuccess(t('page.077a6d37719a'))
    confirmDeleteRoute.value = null
    await loadData()
  } catch (error: any) {
    showError(error?.response?.data?.message || error?.message || t('settings.deletion.failed'))
  } finally {
    deleting.value = null
  }
}

// ============== 辅助函数 ==============
const navigateTo = (path: string) => {
  router.push(path)
}

const formatAddress = (listen: ListenInfo) => {
  const host = listen.host === '0.0.0.0' || listen.host === '::' ? '*' : listen.host
  return `${host}:${listen.port}`
}

const serviceTypeLabel = (type: string) => {
  switch (type) {
    case 'proxy': return t('page.7b3f1a698530')
    case 'fileserver': return t('nav.files')
    case 'simpleres': return t('nav.responses')
    default: return type
  }
}

const serviceTypeClass = (type: string) => {
  switch (type) {
    case 'proxy': return 'bg-blue-500/15 text-blue-400'
    case 'fileserver': return 'bg-emerald-500/15 text-emerald-400'
    case 'simpleres': return 'bg-violet-500/15 text-violet-400'
    default: return 'bg-gray-500/15 text-gray-400'
  }
}

const getClusterInfo = (clusterId: string) => {
  return clusters.value.find(c => c.id === clusterId)
}

onMounted(() => {
  loadData()
})
</script>

<template>
  <div class="space-y-4">
    <!-- 顶部标题栏 -->
    <div class="card">
      <div class="flex items-center justify-between">
        <div class="flex items-center gap-3">
          <h2 class="text-lg font-semibold text-gray-100">{{ t('nav.configOverview') }}</h2>
        </div>
        <div class="flex items-center gap-2">
          <button @click="openListenDialog"
            class="btn btn-sm btn-primary flex items-center gap-1 text-xs">
            <span>{{ t('page.b65b642e5e7d') }}</span>
          </button>
          <button @click="loadData" :disabled="loading"
            class="btn btn-sm btn-secondary flex items-center gap-1.5">
            <span v-if="loading" class="w-3.5 h-3.5 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
            <span>{{ t('common.refresh') }}</span>
          </button>
        </div>
      </div>
    </div>

    <!-- 加载骨架屏 -->
    <div v-if="loading && listens.length === 0" class="space-y-4">
      <div class="card animate-pulse">
        <div class="h-4 bg-gray-700/50 rounded w-32 mb-4"></div>
        <div class="space-y-3">
          <div class="h-20 bg-gray-700/30 rounded"></div>
          <div class="h-20 bg-gray-700/30 rounded"></div>
        </div>
      </div>
    </div>

    <template v-else>
      <!-- 监听端口及服务关联 -->
      <div class="space-y-3">
        <div v-for="(listen, i) in listens" :key="i" class="card">
          <!-- 端口头部 -->
          <div class="flex items-center gap-3 mb-3">
            <div class="flex-shrink-0 w-10 h-10 rounded-lg flex items-center justify-center text-sm font-bold"
              :class="listen.isHttps
                ? 'bg-green-500/15 text-green-400'
                : 'bg-blue-500/15 text-blue-400'">
              {{ listen.isHttps ? 'S' : 'H' }}
            </div>
            <div class="flex-1 min-w-0">
              <div class="flex items-center gap-2">
                <span class="text-base font-mono font-semibold text-gray-100">{{ formatAddress(listen) }}</span>
                <span class="text-xs px-1.5 py-0.5 rounded"
                  :class="listen.isHttps
                    ? 'bg-green-500/15 text-green-400'
                    : 'bg-blue-500/15 text-blue-400'">
                  {{ listen.isHttps ? 'HTTPS' : 'HTTP' }}
                </span>
                <span v-if="listen.autoHttpsPort" class="text-xs text-gray-500">
                  {{ t('page.7ffb2efd448f') }}{{ listen.autoHttpsPort }}
                </span>
              </div>
              <div class="text-xs text-gray-500 mt-0.5">
                {{ listen.routes.length }} {{ t('page.e7049bec6873') }}
              </div>
            </div>
            <!-- 操作按钮 -->
            <div class="flex items-center gap-2">
              <button @click="openAddDialog(listen)"
                class="btn btn-sm btn-primary flex items-center gap-1 text-xs">
                <span>{{ t('page.a91e87b292cc') }}</span>
              </button>
              <button v-if="listen.source === 'patch'" @click="deleteListen(listen)"
                class="btn btn-sm text-xs bg-red-600/20 text-red-400 hover:bg-red-600/30 border border-red-500/30">
                {{ t('page.89d2f906ce47') }}
              </button>
            </div>
          </div>

          <!-- 绑定的路由/服务列表 -->
          <div v-if="listen.routes.length > 0" class="space-y-2 ml-[52px]">
            <div v-for="route in listen.routes" :key="route.routeId"
              class="flex items-start gap-3 px-3 py-2.5 rounded-lg bg-dark-sidebar border border-dark-border hover:border-dark-border/80 transition-colors group">
              <!-- 服务类型标签 -->
              <span class="flex-shrink-0 text-xs px-2 py-0.5 rounded mt-0.5"
                :class="serviceTypeClass(route.serviceType)">
                {{ serviceTypeLabel(route.serviceType) }}
              </span>

              <!-- 路由详情 -->
              <div class="flex-1 min-w-0">
                <div class="flex items-center gap-2 flex-wrap">
                  <span class="font-mono text-sm text-gray-300">{{ route.routeId }}</span>
                  <span v-if="route.path" class="font-mono text-xs text-gray-500">{{ route.path }}</span>
                  <span v-if="route.source === 'patch'" class="text-xs px-1 py-0.5 rounded bg-amber-500/10 text-amber-400">{{ t('page.70f565337ffd') }}</span>
                </div>
                <!-- 域名标签 -->
                <div v-if="route.hosts.length > 0" class="flex flex-wrap gap-1 mt-1">
                  <span v-for="host in route.hosts" :key="host"
                    class="px-1.5 py-0.5 text-xs rounded bg-purple-500/10 text-purple-400 font-mono">
                    {{ host }}
                  </span>
                </div>
                <!-- 集群目标展示（仅代理类型） -->
                <div v-if="route.serviceType === 'proxy' && route.clusterId" class="mt-1.5">
                  <template v-if="getClusterInfo(route.clusterId)">
                    <div class="flex items-center gap-2 text-xs text-gray-500">
                      <span>{{ t('ui.cluster') }}</span>
                      <span class="font-mono text-primary-400">{{ route.clusterId }}</span>
                      <span class="text-gray-600">·</span>
                      <span>{{ getClusterInfo(route.clusterId)!.policy }}</span>
                      <span class="text-gray-600">·</span>
                      <span>{{ getClusterInfo(route.clusterId)!.destinationCount }} {{ t('page.2410d860f093') }}</span>
                    </div>
                    <div class="flex flex-wrap gap-x-3 gap-y-1 mt-1">
                      <div v-for="dest in getClusterInfo(route.clusterId)!.destinations" :key="dest.id"
                        class="flex items-center gap-1.5 text-xs">
                        <span class="w-1.5 h-1.5 rounded-full bg-green-400 flex-shrink-0"></span>
                        <span class="font-mono text-gray-400">{{ dest.address }}</span>
                      </div>
                    </div>
                  </template>
                  <div v-else class="text-xs text-gray-500">
                    {{ t('ui.cluster') }} <span class="font-mono text-gray-400">{{ route.clusterId }}</span>
                  </div>
                </div>
              </div>

              <!-- 操作按钮组 -->
              <div class="flex items-center gap-1 flex-shrink-0 opacity-0 group-hover:opacity-100 transition-opacity mt-0.5">
                <button @click.stop="openEditDialog(route)"
                  class="text-xs text-gray-500 hover:text-primary-400 transition-colors px-1.5 py-0.5">
                  {{ t('common.edit') }}
                </button>
                <button @click.stop="requestDelete(route)"
                  :disabled="deleting === route.routeId"
                  class="text-xs transition-colors px-1.5 py-0.5"
                  :class="route.source === 'patch'
                    ? 'text-gray-500 hover:text-red-400'
                    : 'text-gray-700 cursor-not-allowed'"
                  :title="route.source !== 'patch' ? t('page.1d22be24dc41') : t('common.delete')">
                  <span v-if="deleting === route.routeId" class="w-3 h-3 border border-gray-400 border-t-transparent rounded-full animate-spin inline-block"></span>
                  <span v-else>{{ t('common.delete') }}</span>
                </button>
              </div>
            </div>
          </div>

          <!-- 无绑定服务 -->
          <div v-else class="ml-[52px] text-sm text-gray-600 py-2">
            {{ t('page.f4e5b6cc5501') }}
          </div>
        </div>

        <!-- 控制台端口 -->
        <div v-if="controlListen" class="card">
          <div class="flex items-center gap-3">
            <div class="flex-shrink-0 w-10 h-10 rounded-lg flex items-center justify-center text-sm font-bold bg-amber-500/15 text-amber-400">
              C
            </div>
            <div class="flex-1 min-w-0">
              <div class="flex items-center gap-2">
                <span class="text-base font-mono font-semibold text-gray-100">{{ controlListen.host }}:{{ controlListen.port }}</span>
                <span class="text-xs px-1.5 py-0.5 rounded bg-amber-500/15 text-amber-400">{{ t('page.d0d07d153161') }}</span>
              </div>
              <div class="text-xs text-gray-500 mt-0.5">
                {{ t('page.5bc237ecc128') }}
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- 证书信息 -->
      <div v-if="certs.length > 0" class="card">
        <div class="flex items-center justify-between mb-4">
          <h3 class="text-base font-medium text-gray-200 flex items-center gap-2">
            <span class="text-lg">🔒</span> {{ t('page.afac7703f1fb') }}
          </h3>
          <span class="text-xs text-gray-500">{{ certs.length }} {{ t('page.92c59766f25e') }}</span>
        </div>
        <div class="space-y-2">
          <div v-for="(cert, ci) in certs" :key="ci"
            class="flex items-center justify-between px-4 py-2.5 rounded-lg bg-dark-sidebar border border-dark-border">
            <div class="flex items-center gap-3">
              <span class="text-green-400 text-sm">🔐</span>
              <span class="text-sm font-mono text-gray-300">{{ cert.host }}</span>
            </div>
            <div class="flex items-center gap-2 text-xs text-gray-500">
              <span>{{ cert.pemFile }}</span>
              <span v-if="cert.hasKey" class="px-1.5 py-0.5 rounded bg-green-500/10 text-green-400">KEY</span>
            </div>
          </div>
        </div>
      </div>

      <!-- 集群总览 -->
      <div class="card">
        <div class="flex items-center justify-between mb-4">
          <h3 class="text-base font-medium text-gray-200 flex items-center gap-2">
            <span class="text-lg">⚖️</span> {{ t('page.4b0bdb2a5104') }}
          </h3>
          <div class="flex items-center gap-2">
            <span class="text-xs text-gray-500">{{ clusters.length }} {{ t('page.c3a5459903f2') }}</span>
            <button @click="navigateTo('/cluster-config')"
              class="btn btn-sm btn-secondary text-xs">
              {{ t('page.d2257df342ab') }}
            </button>
          </div>
        </div>

        <div v-if="clusters.length === 0" class="text-sm text-gray-500 py-4 text-center">
          {{ t('page.1a3f279e11eb') }}
        </div>

        <div v-else class="grid gap-3 sm:grid-cols-2">
          <div v-for="cluster in clusters" :key="cluster.id"
            class="rounded-lg bg-dark-sidebar border border-dark-border p-4 hover:border-dark-border/80 transition-colors cursor-pointer"
            @click="navigateTo('/cluster-config')">
            <div class="flex items-center justify-between mb-3">
              <span class="text-sm font-medium text-gray-200 font-mono">{{ cluster.id }}</span>
              <span class="text-xs px-2 py-0.5 rounded-full bg-primary-500/15 text-primary-400">
                {{ cluster.policy }}
              </span>
            </div>
            <div class="space-y-1.5">
              <div v-for="dest in cluster.destinations" :key="dest.id"
                class="flex items-center gap-2 text-xs">
                <span class="w-1.5 h-1.5 rounded-full bg-green-400 flex-shrink-0"></span>
                <span class="font-mono text-gray-400 truncate">{{ dest.id }}</span>
                <span class="text-gray-600 mx-1">→</span>
                <span class="font-mono text-gray-300 truncate">{{ dest.address }}</span>
              </div>
            </div>
            <div class="mt-2 pt-2 border-t border-dark-border/50 text-xs text-gray-500">
              {{ cluster.destinationCount }} {{ t('page.a7500576c662') }}
            </div>
          </div>
        </div>
      </div>
    </template>

    <!-- ============== 新增服务对话框 ============== -->
    <Teleport to="body">
      <div v-if="showAddDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="showAddDialog = false"></div>
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-full max-w-lg mx-4 max-h-[80vh] overflow-y-auto">
          <div class="flex items-center justify-between px-5 py-4 border-b border-dark-border">
            <h3 class="text-base font-semibold text-gray-100">
              {{ addStep === 'type' ? t('page.adfc5ace573e') : t('page.798b4f3bb40b', { p0: serviceTypeLabel(addServiceType) }) }}
            </h3>
            <div class="flex items-center gap-2 text-xs text-gray-500">
              <span class="font-mono">{{ t('fields.Port') }} {{ addPort }}</span>
              <button @click="showAddDialog = false" class="text-gray-400 hover:text-gray-200 text-lg ml-2">×</button>
            </div>
          </div>

          <!-- 步骤 1: 选择类型 -->
          <div v-if="addStep === 'type'" class="p-5 space-y-3">
            <button @click="selectType('simpleres')"
              class="w-full flex items-center gap-4 p-4 rounded-lg bg-dark-sidebar border border-dark-border hover:border-violet-500/40 transition-colors text-left">
              <div class="w-10 h-10 rounded-lg bg-violet-500/15 text-violet-400 flex items-center justify-center text-lg font-bold">R</div>
              <div>
                <div class="text-sm font-medium text-gray-200">{{ t('nav.responses') }}</div>
                <div class="text-xs text-gray-500 mt-0.5">{{ t('page.a237b8a0e4a4') }}</div>
              </div>
            </button>
            <button @click="selectType('fileserver')"
              class="w-full flex items-center gap-4 p-4 rounded-lg bg-dark-sidebar border border-dark-border hover:border-emerald-500/40 transition-colors text-left">
              <div class="w-10 h-10 rounded-lg bg-emerald-500/15 text-emerald-400 flex items-center justify-center text-lg font-bold">F</div>
              <div>
                <div class="text-sm font-medium text-gray-200">{{ t('nav.files') }}</div>
                <div class="text-xs text-gray-500 mt-0.5">{{ t('page.24de4e283a98') }}</div>
              </div>
            </button>
            <button @click="selectType('proxy')"
              class="w-full flex items-center gap-4 p-4 rounded-lg bg-dark-sidebar border border-dark-border hover:border-blue-500/40 transition-colors text-left">
              <div class="w-10 h-10 rounded-lg bg-blue-500/15 text-blue-400 flex items-center justify-center text-lg font-bold">P</div>
              <div>
                <div class="text-sm font-medium text-gray-200">{{ t('page.a2b10f6cd3d7') }}</div>
                <div class="text-xs text-gray-500 mt-0.5">{{ t('page.ee95b386f119') }}</div>
              </div>
            </button>
          </div>

          <!-- 步骤 2: 填写表单 -->
          <div v-else class="p-5 space-y-4">
            <button @click="addStep = 'type'" class="text-xs text-gray-500 hover:text-gray-300 transition-colors">
              {{ t('page.ac0bd6b60998') }}
            </button>
            <div>
              <label class="block text-xs text-gray-400 mb-1">ID <span class="text-gray-600">{{ t('page.9369671e2469') }}</span></label>
              <input v-model="addForm.itemId" type="text"
                class="input w-full font-mono text-sm" :placeholder="t('page.6e9fc0e932c5')" />
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">{{ t('page.4695bbcd03e4') }}</label>
              <input v-model="addForm.path" type="text"
                class="input w-full font-mono text-sm" placeholder="{**catch-all}" />
            </div>
            <!-- Hosts 动态数组 -->
            <div>
              <div class="flex items-center justify-between mb-1">
                <label class="text-xs text-gray-400">Hosts <span class="text-gray-600">{{ t('page.b0ccc48d53ee') }}</span></label>
                <button type="button" @click="addHosts.push('')"
                  class="text-xs text-primary-400 hover:text-primary-300 transition-colors">{{ t('page.42bb5c118d25') }}</button>
              </div>
              <div class="space-y-2">
                <div v-for="(_h, hi) in addHosts" :key="hi" class="flex items-center gap-2">
                  <input v-model="addHosts[hi]" type="text"
                    class="input flex-1 font-mono text-sm" placeholder="localhost:5002" />
                  <button v-if="addHosts.length > 1" type="button" @click="addHosts.splice(hi, 1)"
                    class="text-gray-500 hover:text-red-400 transition-colors text-sm px-1 flex-shrink-0">×</button>
                </div>
              </div>
            </div>
            <!-- Order 优先级 -->
            <div>
              <label class="block text-xs text-gray-400 mb-1">{{ t('page.867fb5b1c3e1') }} <span class="text-gray-600">{{ t('page.1ddf0a3405d8') }}</span></label>
              <input v-model.number="addForm.order" type="number"
                class="input w-full font-mono text-sm" :placeholder="t('page.c876d248ef7f')" />
            </div>
            <template v-if="addServiceType === 'simpleres'">
              <div>
                <label class="block text-xs text-gray-400 mb-1">{{ t('dashboard.response.body') }}</label>
                <textarea v-model="addForm.body" rows="3"
                  class="input w-full font-mono text-sm resize-y" placeholder="Hello World"></textarea>
              </div>
              <div class="grid grid-cols-2 gap-3">
                <div>
                  <label class="block text-xs text-gray-400 mb-1">Content-Type</label>
                  <select v-if="!addCtCustom" :value="addForm.contentType" @change="onAddCtSelect"
                    class="input w-full font-mono text-sm">
                    <option v-for="ct in contentTypeOptions" :key="ct" :value="ct">{{ ct }}</option>
                    <option value="__custom__">{{ t('page.f95208bf93e5') }}</option>
                  </select>
                  <div v-else class="flex items-center gap-2">
                    <input v-model="addForm.contentType" type="text"
                      class="input flex-1 font-mono text-sm" :placeholder="t('page.d4554b494a59')" />
                    <button type="button" @click="addCtCustom = false; addForm.contentType = contentTypeOptions[0]"
                      class="text-xs text-primary-400 hover:text-primary-300 transition-colors whitespace-nowrap">{{ t('page.f1de1e2621de') }}</button>
                  </div>
                </div>
                <div>
                  <label class="block text-xs text-gray-400 mb-1">{{ t('page.06f7d7341212') }}</label>
                  <input v-model.number="addForm.statusCode" type="number" min="100" max="599"
                    class="input w-full text-sm" />
                </div>
              </div>
              <!-- 自定义 Headers -->
              <div>
                <div class="flex items-center justify-between mb-1">
                  <label class="text-xs text-gray-400">{{ t('page.12ae77e6202d') }}</label>
                  <button type="button" @click="addForm.headers.push({ key: '', value: '' })"
                    class="text-xs text-primary-400 hover:text-primary-300 transition-colors">{{ t('page.42bb5c118d25') }}</button>
                </div>
                <div v-if="addForm.headers.length === 0" class="text-xs text-gray-600 py-1">
                  {{ t('page.71d8fb6df466') }}
                </div>
                <div v-else class="space-y-2">
                  <div v-for="(h, hi) in addForm.headers" :key="hi" class="flex items-center gap-2">
                    <input v-model="h.key" type="text" :placeholder="t('page.5bc55cef48e0')"
                      class="input flex-1 font-mono text-sm" />
                    <input v-model="h.value" type="text" :placeholder="t('common.value')"
                      class="input flex-1 font-mono text-sm" />
                    <button type="button" @click="addForm.headers.splice(hi, 1)"
                      class="text-gray-500 hover:text-red-400 transition-colors text-sm px-1 flex-shrink-0">×</button>
                  </div>
                </div>
              </div>
            </template>
            <template v-if="addServiceType === 'fileserver'">
              <div>
                <label class="block text-xs text-gray-400 mb-1">{{ t('page.f33b8f56b366') }}</label>
                <input v-model="addForm.basePath" type="text"
                  class="input w-full font-mono text-sm" placeholder="./wwwroot" />
              </div>
              <div>
                <label class="block text-xs text-gray-400 mb-1">{{ t('page.78cd447f6f06') }}</label>
                <input v-model="addForm.prefix" type="text"
                  class="input w-full font-mono text-sm" placeholder="/" />
              </div>
              <div class="grid grid-cols-2 gap-3">
                <label class="flex items-center gap-2 text-sm text-gray-300 cursor-pointer">
                  <input type="checkbox" v-model="addForm.browse" class="rounded border-gray-600" />
                  <span>{{ t('page.f8af9c5e626f') }}</span>
                </label>
                <label class="flex items-center gap-2 text-sm text-gray-300 cursor-pointer">
                  <input type="checkbox" v-model="addForm.preCompressed" class="rounded border-gray-600" />
                  <span>{{ t('page.d36e4e4adc22') }}</span>
                </label>
              </div>
              <div>
                <label class="block text-xs text-gray-400 mb-1">TryFiles <span class="text-gray-600">{{ t('page.ed111dcc5927') }}</span></label>
                <input v-model="addForm.tryFiles" type="text"
                  class="input w-full font-mono text-sm" placeholder="$path, index.html" />
              </div>
              <div>
                <label class="block text-xs text-gray-400 mb-1">DefaultFiles <span class="text-gray-600">{{ t('page.ed111dcc5927') }}</span></label>
                <input v-model="addForm.defaultFiles" type="text"
                  class="input w-full font-mono text-sm" placeholder="index.html, index.htm" />
              </div>
            </template>
            <template v-if="addServiceType === 'proxy'">
              <div>
                <label class="block text-xs text-gray-400 mb-1">{{ t('page.409565081c33') }}</label>
                <select v-model="addForm.clusterId" class="input w-full text-sm">
                  <option v-if="clusterOptions.length === 0" disabled value="">{{ t('page.25b2d08665bf') }}</option>
                  <option v-for="cid in clusterOptions" :key="cid" :value="cid">{{ cid }}</option>
                </select>
                <p v-if="clusterOptions.length === 0" class="text-xs text-amber-400 mt-1">
                  {{ t('page.6f7d1ec50362') }}
                </p>
              </div>
            </template>
            <div class="pt-2 border-t border-dark-border/50">
              <div class="flex justify-end gap-2">
                <button @click="showAddDialog = false" class="btn btn-sm btn-secondary">{{ t('common.cancel') }}</button>
                <button @click="submitAdd" :disabled="saving" class="btn btn-sm btn-primary flex items-center gap-1.5">
                  <span v-if="saving" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin"></span>
                  <span>{{ t('page.72dde8dfef88') }}</span>
                </button>
              </div>
            </div>
          </div>
        </div>
      </div>
    </Teleport>

    <!-- ============== 编辑对话框 ============== -->
    <Teleport to="body">
      <div v-if="showEditDialog && editRoute" class="fixed inset-0 z-[100] flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="showEditDialog = false"></div>
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-full max-w-lg mx-4 max-h-[80vh] overflow-y-auto">
          <!-- 头部 -->
          <div class="flex items-center justify-between px-5 py-4 border-b border-dark-border">
            <h3 class="text-base font-semibold text-gray-100">
              {{ t('common.edit') }}{{ serviceTypeLabel(editRoute.serviceType) }}
              <span class="font-mono text-sm text-gray-400 ml-2">{{ editRoute.routeId }}</span>
            </h3>
            <button @click="showEditDialog = false" class="text-gray-400 hover:text-gray-200 text-lg">×</button>
          </div>

          <!-- 加载中 -->
          <div v-if="editLoading" class="p-8 flex items-center justify-center">
            <span class="w-5 h-5 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
            <span class="ml-2 text-sm text-gray-400">{{ t('plugins.loadingConfig') }}</span>
          </div>

          <!-- 简单响应编辑表单 -->
          <div v-else-if="editRoute.serviceType === 'simpleres'" class="p-5 space-y-4">
            <div>
              <label class="block text-xs text-gray-400 mb-1">
                Body <span class="text-gray-600">{{ t('page.a5eaa7bfed47') }}</span>
              </label>
              <textarea v-model="editSimpleRes.body" rows="4"
                class="input w-full font-mono text-sm resize-y" :placeholder="t('dashboard.response.body')"></textarea>
            </div>
            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="block text-xs text-gray-400 mb-1">ContentType</label>
                <select v-if="!editCtCustom" :value="editSimpleRes.contentType" @change="onEditCtSelect"
                  class="input w-full font-mono text-sm">
                  <option v-for="ct in contentTypeOptions" :key="ct" :value="ct">{{ ct }}</option>
                  <option value="__custom__">{{ t('page.f95208bf93e5') }}</option>
                </select>
                <div v-else class="flex items-center gap-2">
                  <input v-model="editSimpleRes.contentType" type="text"
                    class="input flex-1 font-mono text-sm" :placeholder="t('page.d4554b494a59')" />
                  <button type="button" @click="editCtCustom = false; editSimpleRes.contentType = contentTypeOptions[0]"
                    class="text-xs text-primary-400 hover:text-primary-300 transition-colors whitespace-nowrap">{{ t('page.f1de1e2621de') }}</button>
                </div>
              </div>
              <div>
                <label class="block text-xs text-gray-400 mb-1">StatusCode</label>
                <input v-model.number="editSimpleRes.statusCode" type="number" min="100" max="599"
                  class="input w-full text-sm" />
              </div>
            </div>
            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="block text-xs text-gray-400 mb-1">Charset</label>
                <select v-model="editSimpleRes.charset" class="input w-full text-sm">
                  <option value="utf-8">utf-8</option>
                  <option value="gbk">gbk</option>
                  <option value="gb2312">gb2312</option>
                  <option value="ascii">ascii</option>
                  <option value="iso-8859-1">iso-8859-1</option>
                </select>
              </div>
              <div class="flex items-end">
                <label class="flex items-center gap-2 text-sm text-gray-300 cursor-pointer pb-2">
                  <input type="checkbox" v-model="editSimpleRes.showReq" class="rounded border-gray-600" />
                  <span>ShowReq</span>
                </label>
              </div>
            </div>
            <!-- 自定义 Headers -->
            <div>
              <div class="flex items-center justify-between mb-1">
                <label class="text-xs text-gray-400">{{ t('page.12ae77e6202d') }}</label>
                <button type="button" @click="editSimpleRes.headers.push({ key: '', value: '' })"
                  class="text-xs text-primary-400 hover:text-primary-300 transition-colors">{{ t('page.42bb5c118d25') }}</button>
              </div>
              <div v-if="editSimpleRes.headers.length === 0" class="text-xs text-gray-600 py-1">
                {{ t('page.71d8fb6df466') }}
              </div>
              <div v-else class="space-y-2">
                <div v-for="(h, hi) in editSimpleRes.headers" :key="hi" class="flex items-center gap-2">
                  <input v-model="h.key" type="text" :placeholder="t('page.5bc55cef48e0')"
                    class="input flex-1 font-mono text-sm" />
                  <input v-model="h.value" type="text" :placeholder="t('common.value')"
                    class="input flex-1 font-mono text-sm" />
                  <button type="button" @click="editSimpleRes.headers.splice(hi, 1)"
                    class="text-gray-500 hover:text-red-400 transition-colors text-sm px-1 flex-shrink-0">×</button>
                </div>
              </div>
            </div>
            <!-- 路由配置：Path / Order -->
            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="block text-xs text-gray-400 mb-1">{{ t('page.0d321c95a8e5') }}</label>
                <input v-model="editSimpleRes.path" type="text"
                  class="input w-full font-mono text-sm" placeholder="{**catch-all}" />
              </div>
              <div>
                <label class="block text-xs text-gray-400 mb-1">Order</label>
                <input v-model.number="editSimpleRes.order" type="number"
                  class="input w-full text-sm" />
              </div>
            </div>
            <!-- Hosts 编辑 -->
            <div>
              <div class="flex items-center justify-between mb-1">
                <label class="text-xs text-gray-400">Hosts <span class="text-gray-600">{{ t('page.b0ccc48d53ee') }}</span></label>
                <button type="button" @click="editHosts.push('')"
                  class="text-xs text-primary-400 hover:text-primary-300 transition-colors">{{ t('page.42bb5c118d25') }}</button>
              </div>
              <div v-if="editHosts.length === 0" class="text-xs text-gray-600 py-1">{{ t('page.83c6e34d9977') }}</div>
              <div v-else class="space-y-2">
                <div v-for="(_h, hi) in editHosts" :key="hi" class="flex items-center gap-2">
                  <input v-model="editHosts[hi]" type="text"
                    class="input flex-1 font-mono text-sm" placeholder="localhost:5002" />
                  <button type="button" @click="editHosts.splice(hi, 1)"
                    class="text-gray-500 hover:text-red-400 transition-colors text-sm px-1 flex-shrink-0">×</button>
                </div>
              </div>
            </div>
            <div class="flex justify-end gap-2 pt-2 border-t border-dark-border/50">
              <button @click="showEditDialog = false" class="btn btn-sm btn-secondary">{{ t('common.cancel') }}</button>
              <button @click="submitEdit" :disabled="editSaving" class="btn btn-sm btn-primary flex items-center gap-1.5">
                <span v-if="editSaving" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin"></span>
                <span>{{ t('common.confirm') }}</span>
              </button>
            </div>
          </div>

          <!-- 文件服务编辑表单 -->
          <div v-else-if="editRoute.serviceType === 'fileserver'" class="p-5 space-y-4">
            <div>
              <label class="block text-xs text-gray-400 mb-1">{{ t('page.f33b8f56b366') }}</label>
              <input v-model="editFileServer.basePath" type="text"
                class="input w-full font-mono text-sm" placeholder="./wwwroot" />
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">{{ t('page.78cd447f6f06') }}</label>
              <input v-model="editFileServer.prefix" type="text"
                class="input w-full font-mono text-sm" placeholder="/" />
            </div>
            <div class="grid grid-cols-2 gap-3">
              <label class="flex items-center gap-2 text-sm text-gray-300 cursor-pointer">
                <input type="checkbox" v-model="editFileServer.browse" class="rounded border-gray-600" />
                <span>{{ t('page.f8af9c5e626f') }}</span>
              </label>
              <label class="flex items-center gap-2 text-sm text-gray-300 cursor-pointer">
                <input type="checkbox" v-model="editFileServer.preCompressed" class="rounded border-gray-600" />
                <span>{{ t('page.d36e4e4adc22') }}</span>
              </label>
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">TryFiles <span class="text-gray-600">{{ t('page.ed111dcc5927') }}</span></label>
              <input v-model="editFileServer.tryFiles" type="text"
                class="input w-full font-mono text-sm" placeholder="$path, index.html" />
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">DefaultFiles <span class="text-gray-600">{{ t('page.ed111dcc5927') }}</span></label>
              <input v-model="editFileServer.defaultFiles" type="text"
                class="input w-full font-mono text-sm" placeholder="index.html, index.htm" />
            </div>
            <!-- 路由配置：Path / Order -->
            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="block text-xs text-gray-400 mb-1">{{ t('page.0d321c95a8e5') }}</label>
                <input v-model="editFileServer.path" type="text"
                  class="input w-full font-mono text-sm" placeholder="{**catch-all}" />
              </div>
              <div>
                <label class="block text-xs text-gray-400 mb-1">Order</label>
                <input v-model.number="editFileServer.order" type="number"
                  class="input w-full text-sm" />
              </div>
            </div>
            <!-- Hosts 编辑 -->
            <div>
              <div class="flex items-center justify-between mb-1">
                <label class="text-xs text-gray-400">Hosts <span class="text-gray-600">{{ t('page.b0ccc48d53ee') }}</span></label>
                <button type="button" @click="editHosts.push('')"
                  class="text-xs text-primary-400 hover:text-primary-300 transition-colors">{{ t('page.42bb5c118d25') }}</button>
              </div>
              <div v-if="editHosts.length === 0" class="text-xs text-gray-600 py-1">{{ t('page.83c6e34d9977') }}</div>
              <div v-else class="space-y-2">
                <div v-for="(_h, hi) in editHosts" :key="hi" class="flex items-center gap-2">
                  <input v-model="editHosts[hi]" type="text"
                    class="input flex-1 font-mono text-sm" placeholder="localhost:5002" />
                  <button type="button" @click="editHosts.splice(hi, 1)"
                    class="text-gray-500 hover:text-red-400 transition-colors text-sm px-1 flex-shrink-0">×</button>
                </div>
              </div>
            </div>
            <div class="flex justify-end gap-2 pt-2 border-t border-dark-border/50">
              <button @click="showEditDialog = false" class="btn btn-sm btn-secondary">{{ t('common.cancel') }}</button>
              <button @click="submitEdit" :disabled="editSaving" class="btn btn-sm btn-primary flex items-center gap-1.5">
                <span v-if="editSaving" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin"></span>
                <span>{{ t('common.confirm') }}</span>
              </button>
            </div>
          </div>

          <!-- 代理路由编辑表单 -->
          <div v-else class="p-5 space-y-4">
            <div>
              <label class="block text-xs text-gray-400 mb-1">{{ t('page.1f05c33b3667') }}</label>
              <select v-model="editProxy.clusterId" class="input w-full text-sm">
                <option v-for="cid in clusterOptions" :key="cid" :value="cid">{{ cid }}</option>
              </select>
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">{{ t('page.0d321c95a8e5') }}</label>
              <input v-model="editProxy.path" type="text"
                class="input w-full font-mono text-sm" placeholder="{**catch-all}" />
            </div>
            <!-- Hosts 编辑 -->
            <div>
              <div class="flex items-center justify-between mb-1">
                <label class="text-xs text-gray-400">Hosts <span class="text-gray-600">{{ t('page.b0ccc48d53ee') }}</span></label>
                <button type="button" @click="editHosts.push('')"
                  class="text-xs text-primary-400 hover:text-primary-300 transition-colors">{{ t('page.42bb5c118d25') }}</button>
              </div>
              <div v-if="editHosts.length === 0" class="text-xs text-gray-600 py-1">{{ t('page.83c6e34d9977') }}</div>
              <div v-else class="space-y-2">
                <div v-for="(_h, hi) in editHosts" :key="hi" class="flex items-center gap-2">
                  <input v-model="editHosts[hi]" type="text"
                    class="input flex-1 font-mono text-sm" placeholder="localhost:5002" />
                  <button type="button" @click="editHosts.splice(hi, 1)"
                    class="text-gray-500 hover:text-red-400 transition-colors text-sm px-1 flex-shrink-0">×</button>
                </div>
              </div>
            </div>
            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="block text-xs text-gray-400 mb-1">Methods <span class="text-gray-600">{{ t('page.ed111dcc5927') }}</span></label>
                <input v-model="editProxy.methods" type="text"
                  class="input w-full font-mono text-sm" placeholder="GET, POST" />
              </div>
              <div>
                <label class="block text-xs text-gray-400 mb-1">Order</label>
                <input v-model.number="editProxy.order" type="number"
                  class="input w-full text-sm" />
              </div>
            </div>
            <div class="flex justify-end gap-2 pt-2 border-t border-dark-border/50">
              <button @click="showEditDialog = false" class="btn btn-sm btn-secondary">{{ t('common.cancel') }}</button>
              <button @click="submitEdit" :disabled="editSaving" class="btn btn-sm btn-primary flex items-center gap-1.5">
                <span v-if="editSaving" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin"></span>
                <span>{{ t('common.confirm') }}</span>
              </button>
            </div>
          </div>
        </div>
      </div>
    </Teleport>

    <!-- ============== 删除确认对话框 ============== -->
    <Teleport to="body">
      <div v-if="confirmDeleteRoute" class="fixed inset-0 z-[100] flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="confirmDeleteRoute = null"></div>
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-full max-w-sm mx-4">
          <div class="p-5">
            <h3 class="text-base font-semibold text-gray-100 mb-3">{{ t('settings.confirm.deletion') }}</h3>
            <p class="text-sm text-gray-400 mb-1">
              {{ t('page.d2f629cc5722') }}{{ serviceTypeLabel(confirmDeleteRoute.serviceType) }}{{ t('page.9d45d8943988') }}
            </p>
            <p class="font-mono text-sm text-gray-300 mb-4 bg-dark-sidebar rounded px-3 py-2 border border-dark-border">
              {{ confirmDeleteRoute.routeId }}
            </p>
            <div class="flex justify-end gap-2">
              <button @click="confirmDeleteRoute = null" class="btn btn-sm btn-secondary">{{ t('common.cancel') }}</button>
              <button @click="executeDelete" :disabled="!!deleting" class="btn btn-sm bg-red-600 hover:bg-red-500 text-white flex items-center gap-1.5">
                <span v-if="deleting" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin"></span>
                <span>{{ t('settings.confirm.deletion') }}</span>
              </button>
            </div>
          </div>
        </div>
      </div>
    </Teleport>

    <!-- ============== 删除端口确认对话框 ============== -->
    <Teleport to="body">
      <div v-if="confirmDeleteListen" class="fixed inset-0 z-[100] flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="confirmDeleteListen = null"></div>
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-full max-w-sm mx-4">
          <div class="p-5">
            <h3 class="text-base font-semibold text-gray-100 mb-3">{{ t('page.37797d3dfe28') }}</h3>
            <p class="text-sm text-gray-400 mb-1">{{ t('page.65a2b420f448') }}</p>
            <p class="font-mono text-sm text-gray-300 mb-4 bg-dark-sidebar rounded px-3 py-2 border border-dark-border">
              {{ confirmDeleteListen.host }}:{{ confirmDeleteListen.port }}
              <span v-if="confirmDeleteListen.isHttps" class="text-green-400 ml-1">(HTTPS)</span>
            </p>
            <div class="flex justify-end gap-2">
              <button @click="confirmDeleteListen = null" class="btn btn-sm btn-secondary">{{ t('common.cancel') }}</button>
              <button @click="executeDeleteListen" :disabled="deletingListen" class="btn btn-sm bg-red-600 hover:bg-red-500 text-white flex items-center gap-1.5">
                <span v-if="deletingListen" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin"></span>
                <span>{{ t('settings.confirm.deletion') }}</span>
              </button>
            </div>
          </div>
        </div>
      </div>
    </Teleport>

    <!-- ============== 新增监听端口对话框 ============== -->
    <Teleport to="body">
      <div v-if="showListenDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="showListenDialog = false"></div>
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-full max-w-sm mx-4">
          <div class="flex items-center justify-between px-5 py-4 border-b border-dark-border">
            <h3 class="text-base font-semibold text-gray-100">{{ t('page.e3039d65951e') }}</h3>
            <button @click="showListenDialog = false" class="text-gray-400 hover:text-gray-200 text-lg">×</button>
          </div>
          <div class="p-5 space-y-4">
            <div>
              <label class="block text-xs text-gray-400 mb-1">{{ t('ui.listening.addresses') }} <span class="text-gray-600">{{ t('page.f7d7202d0ff5') }}</span></label>
              <input v-model="listenForm.host" type="text"
                class="input w-full font-mono text-sm" placeholder="0.0.0.0" />
              <p v-if="listenForm.host && !isValidIP(listenForm.host)" class="text-xs text-red-400 mt-1">
                {{ t('page.6a730e19448f') }}
              </p>
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">{{ t('page.7830f101b9ac') }}</label>
              <input v-model.number="listenForm.port" type="number" min="1" max="65535"
                class="input w-full text-sm" placeholder="5080" />
            </div>
            <label class="flex items-center gap-2 text-sm text-gray-300 cursor-pointer">
              <input type="checkbox" v-model="listenForm.isHttps" class="rounded border-gray-600" />
              <span>{{ t('page.30294cdffec7') }}</span>
            </label>
            <!-- Hosts 绑定地址 -->
            <div>
              <div class="flex items-center justify-between mb-1">
                <label class="text-xs text-gray-400">{{ t('page.5c24e897781f') }} <span class="text-gray-600">{{ t('page.173c70b97740') }}</span></label>
                <button type="button" @click="listenHosts.push('')"
                  class="text-xs text-primary-400 hover:text-primary-300 transition-colors">{{ t('page.42bb5c118d25') }}</button>
              </div>
              <div class="space-y-2">
                <div v-for="(_h, hi) in listenHosts" :key="hi" class="flex items-center gap-2">
                  <input v-model="listenHosts[hi]" type="text"
                    class="input flex-1 font-mono text-sm" placeholder="*:5080" />
                  <button v-if="listenHosts.length > 1" type="button" @click="listenHosts.splice(hi, 1)"
                    class="text-gray-500 hover:text-red-400 transition-colors text-sm px-1 flex-shrink-0">×</button>
                </div>
              </div>
              <p class="text-xs text-gray-600 mt-1">{{ t('page.71cde0218095') }}</p>
            </div>
            <div class="flex justify-end gap-2 pt-2 border-t border-dark-border/50">
              <button @click="showListenDialog = false" class="btn btn-sm btn-secondary">{{ t('common.cancel') }}</button>
              <button @click="submitListen" :disabled="listenSaving" class="btn btn-sm btn-primary flex items-center gap-1.5">
                <span v-if="listenSaving" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin"></span>
                <span>{{ t('page.72dde8dfef88') }}</span>
              </button>
            </div>
          </div>
        </div>
      </div>
    </Teleport>

    <!-- ============== 重启确认对话框 ============== -->
    <Teleport to="body">
      <div v-if="showRestartConfirm" class="fixed inset-0 z-[100] flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="showRestartConfirm = false"></div>
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-full max-w-sm mx-4">
          <div class="p-5">
            <h3 class="text-base font-semibold text-gray-100 mb-3">{{ t('page.95aa1f40bc8e') }}</h3>
            <p class="text-sm text-gray-400 mb-4">
              {{ t('page.521dc0a949a2') }}
            </p>
            <div class="text-xs text-amber-400/80 bg-amber-500/10 border border-amber-500/20 rounded-lg px-3 py-2 mb-4">
              {{ t('page.2cd99849dc68') }}
            </div>
            <div class="flex justify-end gap-2">
              <button @click="showRestartConfirm = false" class="btn btn-sm btn-secondary">{{ t('page.5b31f09799a8') }}</button>
              <button @click="confirmRestart" :disabled="restarting" class="btn btn-sm bg-amber-600 hover:bg-amber-500 text-white flex items-center gap-1.5">
                <span v-if="restarting" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin"></span>
                <span>{{ t('page.700b6c80d954') }}</span>
              </button>
            </div>
          </div>
        </div>
      </div>
    </Teleport>
  </div>
</template>
