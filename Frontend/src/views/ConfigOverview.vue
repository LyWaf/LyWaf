<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { overviewApi, routeApi, simpleResApi, fileServerApi } from '@/api'
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
    showError(error?.response?.data?.message || '加载概览数据失败')
  } finally {
    loading.value = false
  }
}

// ============== 新增对话框 ==============
type ServiceType = 'simpleres' | 'fileserver' | 'proxy'

const showAddDialog = ref(false)
const addStep = ref<'type' | 'form'>('type')
const addPort = ref(0)
const addHost = ref('')
const addServiceType = ref<ServiceType>('simpleres')
const saving = ref(false)

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

const buildHosts = () => {
  const host = addHost.value === '0.0.0.0' || addHost.value === '::' ? '*' : addHost.value
  return [`${host}:${addPort.value}`]
}

const submitAdd = async () => {
  const id = addForm.value.itemId.trim()
  if (!id) { showError('ID 不能为空'); return }

  saving.value = true
  try {
    const hosts = buildHosts()
    const path = addForm.value.path.trim() || '{**catch-all}'

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
      if (!itemRes.success) { showError(itemRes.message || '添加简单响应失败'); saving.value = false; return }
      const routeRes = await routeApi.addRoute({ routeId: id, match: { path, hosts } })
      if (!routeRes.success) { showError(routeRes.message || '添加路由失败'); saving.value = false; return }
      showSuccess('简单响应已添加')
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
      if (!itemRes.success) { showError(itemRes.message || '添加文件服务失败'); saving.value = false; return }
      const routeRes = await routeApi.addRoute({ routeId: id, match: { path, hosts } })
      if (!routeRes.success) { showError(routeRes.message || '添加路由失败'); saving.value = false; return }
      showSuccess('文件服务已添加')
    } else {
      if (!addForm.value.clusterId) { showError('请选择目标集群'); saving.value = false; return }
      const routeRes = await routeApi.addRoute({
        routeId: id,
        clusterId: addForm.value.clusterId,
        match: { path, hosts },
      })
      if (!routeRes.success) { showError(routeRes.message || '添加路由失败'); saving.value = false; return }
      showSuccess('代理路由已添加')
    }

    showAddDialog.value = false
    await loadData()
  } catch (error: any) {
    showError(error?.response?.data?.message || error?.message || '添加失败')
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
})

// 文件服务编辑表单
const editFileServer = ref({
  prefix: '/',
  basePath: './wwwroot',
  browse: false,
  preCompressed: false,
  tryFiles: '',
  defaultFiles: '',
})

// 代理路由编辑表单
const editProxy = ref({
  path: '',
  hosts: '',
  methods: '',
  order: 0,
  clusterId: '',
})

const openEditDialog = async (route: ListenBoundRoute) => {
  editRoute.value = route
  editLoading.value = true
  showEditDialog.value = true

  try {
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
        }
      } else {
        showError(`未找到简单响应配置: ${route.routeId}`)
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
        }
      } else {
        showError(`未找到文件服务配置: ${route.routeId}`)
        showEditDialog.value = false
      }
    } else {
      const res = await routeApi.getRoutes()
      const item = res.routes?.find((r: RouteConfig) => r.routeId === route.routeId)
      if (item) {
        editProxy.value = {
          path: item.match?.path ?? '',
          hosts: item.match?.hosts?.join(', ') ?? '',
          methods: item.match?.methods?.join(', ') ?? '',
          order: item.order ?? 0,
          clusterId: item.clusterId ?? '',
        }
      } else {
        showError(`未找到路由配置: ${route.routeId}`)
        showEditDialog.value = false
      }
    }
  } catch (error: any) {
    showError(error?.message || '加载编辑数据失败')
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
      if (!res.success) { showError(res.message || '更新失败'); return }
      showSuccess('简单响应已更新')

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
      if (!res.success) { showError(res.message || '更新失败'); return }
      showSuccess('文件服务已更新')

    } else {
      const hosts = parseList(editProxy.value.hosts)
      const methods = parseList(editProxy.value.methods).map(m => m.toUpperCase())
      const res = await routeApi.updateRoute({
        routeId: route.routeId,
        clusterId: editProxy.value.clusterId || undefined,
        order: editProxy.value.order,
        match: {
          path: editProxy.value.path || undefined,
          hosts: hosts.length > 0 ? hosts : undefined,
          methods: methods.length > 0 ? methods : undefined,
        },
      })
      if (!res.success) { showError(res.message || '更新失败'); return }
      showSuccess('路由已更新')
    }

    showEditDialog.value = false
    await loadData()
  } catch (error: any) {
    showError(error?.response?.data?.message || error?.message || '更新失败')
  } finally {
    editSaving.value = false
  }
}

// ============== 删除 ==============
const deleting = ref<string | null>(null)
const confirmDeleteRoute = ref<ListenBoundRoute | null>(null)

const requestDelete = (route: ListenBoundRoute) => {
  if (route.source !== 'patch') {
    showError('原始配置中的路由不能在此删除，请修改配置文件')
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
    if (!routeRes.success) { showError(routeRes.message || '删除路由失败'); return }

    if (route.serviceType === 'simpleres') {
      await simpleResApi.removeItem(route.routeId).catch(() => {})
    } else if (route.serviceType === 'fileserver') {
      await fileServerApi.removeItem(route.routeId).catch(() => {})
    }

    showSuccess('已删除')
    confirmDeleteRoute.value = null
    await loadData()
  } catch (error: any) {
    showError(error?.response?.data?.message || error?.message || '删除失败')
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
    case 'proxy': return '反向代理'
    case 'fileserver': return '文件服务'
    case 'simpleres': return '简单响应'
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
          <h2 class="text-lg font-semibold text-gray-100">配置概览</h2>
        </div>
        <button @click="loadData" :disabled="loading"
          class="btn btn-sm btn-secondary flex items-center gap-1.5">
          <span v-if="loading" class="w-3.5 h-3.5 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
          <span>刷新</span>
        </button>
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
                  自动跳转 → :{{ listen.autoHttpsPort }}
                </span>
              </div>
              <div class="text-xs text-gray-500 mt-0.5">
                {{ listen.routes.length }} 个服务绑定
              </div>
            </div>
            <!-- 新增按钮 -->
            <button @click="openAddDialog(listen)"
              class="btn btn-sm btn-primary flex items-center gap-1 text-xs">
              <span>+ 新增</span>
            </button>
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
                  <span v-if="route.source === 'patch'" class="text-xs px-1 py-0.5 rounded bg-amber-500/10 text-amber-400">补丁</span>
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
                      <span>集群</span>
                      <span class="font-mono text-primary-400">{{ route.clusterId }}</span>
                      <span class="text-gray-600">·</span>
                      <span>{{ getClusterInfo(route.clusterId)!.policy }}</span>
                      <span class="text-gray-600">·</span>
                      <span>{{ getClusterInfo(route.clusterId)!.destinationCount }} 节点</span>
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
                    集群 <span class="font-mono text-gray-400">{{ route.clusterId }}</span>
                  </div>
                </div>
              </div>

              <!-- 操作按钮组 -->
              <div class="flex items-center gap-1 flex-shrink-0 opacity-0 group-hover:opacity-100 transition-opacity mt-0.5">
                <button @click.stop="openEditDialog(route)"
                  class="text-xs text-gray-500 hover:text-primary-400 transition-colors px-1.5 py-0.5">
                  编辑
                </button>
                <button @click.stop="requestDelete(route)"
                  :disabled="deleting === route.routeId"
                  class="text-xs transition-colors px-1.5 py-0.5"
                  :class="route.source === 'patch'
                    ? 'text-gray-500 hover:text-red-400'
                    : 'text-gray-700 cursor-not-allowed'"
                  :title="route.source !== 'patch' ? '原始配置不可删除' : '删除'">
                  <span v-if="deleting === route.routeId" class="w-3 h-3 border border-gray-400 border-t-transparent rounded-full animate-spin inline-block"></span>
                  <span v-else>删除</span>
                </button>
              </div>
            </div>
          </div>

          <!-- 无绑定服务 -->
          <div v-else class="ml-[52px] text-sm text-gray-600 py-2">
            暂无绑定的路由或服务
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
                <span class="text-xs px-1.5 py-0.5 rounded bg-amber-500/15 text-amber-400">控制台</span>
              </div>
              <div class="text-xs text-gray-500 mt-0.5">
                管理面板和 API 接口
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- 证书信息 -->
      <div v-if="certs.length > 0" class="card">
        <div class="flex items-center justify-between mb-4">
          <h3 class="text-base font-medium text-gray-200 flex items-center gap-2">
            <span class="text-lg">🔒</span> SSL 证书
          </h3>
          <span class="text-xs text-gray-500">{{ certs.length }} 个证书</span>
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
            <span class="text-lg">⚖️</span> 转发集群
          </h3>
          <div class="flex items-center gap-2">
            <span class="text-xs text-gray-500">{{ clusters.length }} 个集群</span>
            <button @click="navigateTo('/cluster-config')"
              class="btn btn-sm btn-secondary text-xs">
              管理 →
            </button>
          </div>
        </div>

        <div v-if="clusters.length === 0" class="text-sm text-gray-500 py-4 text-center">
          暂无转发集群配置
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
              {{ cluster.destinationCount }} 个目标节点
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
              {{ addStep === 'type' ? '选择服务类型' : `新增${serviceTypeLabel(addServiceType)}` }}
            </h3>
            <div class="flex items-center gap-2 text-xs text-gray-500">
              <span class="font-mono">端口 {{ addPort }}</span>
              <button @click="showAddDialog = false" class="text-gray-400 hover:text-gray-200 text-lg ml-2">×</button>
            </div>
          </div>

          <!-- 步骤 1: 选择类型 -->
          <div v-if="addStep === 'type'" class="p-5 space-y-3">
            <button @click="selectType('simpleres')"
              class="w-full flex items-center gap-4 p-4 rounded-lg bg-dark-sidebar border border-dark-border hover:border-violet-500/40 transition-colors text-left">
              <div class="w-10 h-10 rounded-lg bg-violet-500/15 text-violet-400 flex items-center justify-center text-lg font-bold">R</div>
              <div>
                <div class="text-sm font-medium text-gray-200">简单响应</div>
                <div class="text-xs text-gray-500 mt-0.5">返回固定内容，可用于健康检查、接口 mock 等</div>
              </div>
            </button>
            <button @click="selectType('fileserver')"
              class="w-full flex items-center gap-4 p-4 rounded-lg bg-dark-sidebar border border-dark-border hover:border-emerald-500/40 transition-colors text-left">
              <div class="w-10 h-10 rounded-lg bg-emerald-500/15 text-emerald-400 flex items-center justify-center text-lg font-bold">F</div>
              <div>
                <div class="text-sm font-medium text-gray-200">文件服务</div>
                <div class="text-xs text-gray-500 mt-0.5">提供静态文件访问，支持目录浏览</div>
              </div>
            </button>
            <button @click="selectType('proxy')"
              class="w-full flex items-center gap-4 p-4 rounded-lg bg-dark-sidebar border border-dark-border hover:border-blue-500/40 transition-colors text-left">
              <div class="w-10 h-10 rounded-lg bg-blue-500/15 text-blue-400 flex items-center justify-center text-lg font-bold">P</div>
              <div>
                <div class="text-sm font-medium text-gray-200">后台代理</div>
                <div class="text-xs text-gray-500 mt-0.5">将请求转发到后端集群</div>
              </div>
            </button>
          </div>

          <!-- 步骤 2: 填写表单 -->
          <div v-else class="p-5 space-y-4">
            <button @click="addStep = 'type'" class="text-xs text-gray-500 hover:text-gray-300 transition-colors">
              ← 选择其他类型
            </button>
            <div>
              <label class="block text-xs text-gray-400 mb-1">ID <span class="text-gray-600">(唯一标识)</span></label>
              <input v-model="addForm.itemId" type="text"
                class="input w-full font-mono text-sm" placeholder="如: simpleres_health" />
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">匹配路径</label>
              <input v-model="addForm.path" type="text"
                class="input w-full font-mono text-sm" placeholder="{**catch-all}" />
            </div>
            <template v-if="addServiceType === 'simpleres'">
              <div>
                <label class="block text-xs text-gray-400 mb-1">响应内容</label>
                <textarea v-model="addForm.body" rows="3"
                  class="input w-full font-mono text-sm resize-y" placeholder="Hello World"></textarea>
              </div>
              <div class="grid grid-cols-2 gap-3">
                <div>
                  <label class="block text-xs text-gray-400 mb-1">Content-Type</label>
                  <select v-if="!addCtCustom" :value="addForm.contentType" @change="onAddCtSelect"
                    class="input w-full font-mono text-sm">
                    <option v-for="ct in contentTypeOptions" :key="ct" :value="ct">{{ ct }}</option>
                    <option value="__custom__">自定义...</option>
                  </select>
                  <div v-else class="flex items-center gap-2">
                    <input v-model="addForm.contentType" type="text"
                      class="input flex-1 font-mono text-sm" placeholder="输入 Content-Type" />
                    <button type="button" @click="addCtCustom = false; addForm.contentType = contentTypeOptions[0]"
                      class="text-xs text-primary-400 hover:text-primary-300 transition-colors whitespace-nowrap">预设</button>
                  </div>
                </div>
                <div>
                  <label class="block text-xs text-gray-400 mb-1">状态码</label>
                  <input v-model.number="addForm.statusCode" type="number" min="100" max="599"
                    class="input w-full text-sm" />
                </div>
              </div>
              <!-- 自定义 Headers -->
              <div>
                <div class="flex items-center justify-between mb-1">
                  <label class="text-xs text-gray-400">自定义 Headers</label>
                  <button type="button" @click="addForm.headers.push({ key: '', value: '' })"
                    class="text-xs text-primary-400 hover:text-primary-300 transition-colors">+ 添加</button>
                </div>
                <div v-if="addForm.headers.length === 0" class="text-xs text-gray-600 py-1">
                  暂无自定义 Header
                </div>
                <div v-else class="space-y-2">
                  <div v-for="(h, hi) in addForm.headers" :key="hi" class="flex items-center gap-2">
                    <input v-model="h.key" type="text" placeholder="Header 名"
                      class="input flex-1 font-mono text-sm" />
                    <input v-model="h.value" type="text" placeholder="值"
                      class="input flex-1 font-mono text-sm" />
                    <button type="button" @click="addForm.headers.splice(hi, 1)"
                      class="text-gray-500 hover:text-red-400 transition-colors text-sm px-1 flex-shrink-0">×</button>
                  </div>
                </div>
              </div>
            </template>
            <template v-if="addServiceType === 'fileserver'">
              <div>
                <label class="block text-xs text-gray-400 mb-1">目录路径 (BasePath)</label>
                <input v-model="addForm.basePath" type="text"
                  class="input w-full font-mono text-sm" placeholder="./wwwroot" />
              </div>
              <div>
                <label class="block text-xs text-gray-400 mb-1">URL 前缀 (Prefix)</label>
                <input v-model="addForm.prefix" type="text"
                  class="input w-full font-mono text-sm" placeholder="/" />
              </div>
              <div class="grid grid-cols-2 gap-3">
                <label class="flex items-center gap-2 text-sm text-gray-300 cursor-pointer">
                  <input type="checkbox" v-model="addForm.browse" class="rounded border-gray-600" />
                  <span>允许浏览目录</span>
                </label>
                <label class="flex items-center gap-2 text-sm text-gray-300 cursor-pointer">
                  <input type="checkbox" v-model="addForm.preCompressed" class="rounded border-gray-600" />
                  <span>预压缩文件</span>
                </label>
              </div>
              <div>
                <label class="block text-xs text-gray-400 mb-1">TryFiles <span class="text-gray-600">(逗号分隔)</span></label>
                <input v-model="addForm.tryFiles" type="text"
                  class="input w-full font-mono text-sm" placeholder="$path, index.html" />
              </div>
              <div>
                <label class="block text-xs text-gray-400 mb-1">DefaultFiles <span class="text-gray-600">(逗号分隔)</span></label>
                <input v-model="addForm.defaultFiles" type="text"
                  class="input w-full font-mono text-sm" placeholder="index.html, index.htm" />
              </div>
            </template>
            <template v-if="addServiceType === 'proxy'">
              <div>
                <label class="block text-xs text-gray-400 mb-1">目标集群</label>
                <select v-model="addForm.clusterId" class="input w-full text-sm">
                  <option v-if="clusterOptions.length === 0" disabled value="">暂无集群</option>
                  <option v-for="cid in clusterOptions" :key="cid" :value="cid">{{ cid }}</option>
                </select>
                <p v-if="clusterOptions.length === 0" class="text-xs text-amber-400 mt-1">
                  请先在集群管理页面创建集群
                </p>
              </div>
            </template>
            <div class="pt-2 border-t border-dark-border/50">
              <div class="text-xs text-gray-500 mb-3">
                将绑定到 <span class="font-mono text-gray-400">{{ buildHosts()[0] }}</span>
              </div>
              <div class="flex justify-end gap-2">
                <button @click="showAddDialog = false" class="btn btn-sm btn-secondary">取消</button>
                <button @click="submitAdd" :disabled="saving" class="btn btn-sm btn-primary flex items-center gap-1.5">
                  <span v-if="saving" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin"></span>
                  <span>确认添加</span>
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
              编辑{{ serviceTypeLabel(editRoute.serviceType) }}
              <span class="font-mono text-sm text-gray-400 ml-2">{{ editRoute.routeId }}</span>
            </h3>
            <button @click="showEditDialog = false" class="text-gray-400 hover:text-gray-200 text-lg">×</button>
          </div>

          <!-- 加载中 -->
          <div v-if="editLoading" class="p-8 flex items-center justify-center">
            <span class="w-5 h-5 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
            <span class="ml-2 text-sm text-gray-400">加载配置...</span>
          </div>

          <!-- 简单响应编辑表单 -->
          <div v-else-if="editRoute.serviceType === 'simpleres'" class="p-5 space-y-4">
            <div>
              <label class="block text-xs text-gray-400 mb-1">
                Body <span class="text-gray-600">(支持占位符: {PORT}, {HOST}, {PATH} 等)</span>
              </label>
              <textarea v-model="editSimpleRes.body" rows="4"
                class="input w-full font-mono text-sm resize-y" placeholder="响应内容"></textarea>
            </div>
            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="block text-xs text-gray-400 mb-1">ContentType</label>
                <select v-if="!editCtCustom" :value="editSimpleRes.contentType" @change="onEditCtSelect"
                  class="input w-full font-mono text-sm">
                  <option v-for="ct in contentTypeOptions" :key="ct" :value="ct">{{ ct }}</option>
                  <option value="__custom__">自定义...</option>
                </select>
                <div v-else class="flex items-center gap-2">
                  <input v-model="editSimpleRes.contentType" type="text"
                    class="input flex-1 font-mono text-sm" placeholder="输入 Content-Type" />
                  <button type="button" @click="editCtCustom = false; editSimpleRes.contentType = contentTypeOptions[0]"
                    class="text-xs text-primary-400 hover:text-primary-300 transition-colors whitespace-nowrap">预设</button>
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
                <label class="text-xs text-gray-400">自定义 Headers</label>
                <button type="button" @click="editSimpleRes.headers.push({ key: '', value: '' })"
                  class="text-xs text-primary-400 hover:text-primary-300 transition-colors">+ 添加</button>
              </div>
              <div v-if="editSimpleRes.headers.length === 0" class="text-xs text-gray-600 py-1">
                暂无自定义 Header
              </div>
              <div v-else class="space-y-2">
                <div v-for="(h, hi) in editSimpleRes.headers" :key="hi" class="flex items-center gap-2">
                  <input v-model="h.key" type="text" placeholder="Header 名"
                    class="input flex-1 font-mono text-sm" />
                  <input v-model="h.value" type="text" placeholder="值"
                    class="input flex-1 font-mono text-sm" />
                  <button type="button" @click="editSimpleRes.headers.splice(hi, 1)"
                    class="text-gray-500 hover:text-red-400 transition-colors text-sm px-1 flex-shrink-0">×</button>
                </div>
              </div>
            </div>
            <div class="flex justify-end gap-2 pt-2 border-t border-dark-border/50">
              <button @click="showEditDialog = false" class="btn btn-sm btn-secondary">取消</button>
              <button @click="submitEdit" :disabled="editSaving" class="btn btn-sm btn-primary flex items-center gap-1.5">
                <span v-if="editSaving" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin"></span>
                <span>确定</span>
              </button>
            </div>
          </div>

          <!-- 文件服务编辑表单 -->
          <div v-else-if="editRoute.serviceType === 'fileserver'" class="p-5 space-y-4">
            <div>
              <label class="block text-xs text-gray-400 mb-1">目录路径 (BasePath)</label>
              <input v-model="editFileServer.basePath" type="text"
                class="input w-full font-mono text-sm" placeholder="./wwwroot" />
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">URL 前缀 (Prefix)</label>
              <input v-model="editFileServer.prefix" type="text"
                class="input w-full font-mono text-sm" placeholder="/" />
            </div>
            <div class="grid grid-cols-2 gap-3">
              <label class="flex items-center gap-2 text-sm text-gray-300 cursor-pointer">
                <input type="checkbox" v-model="editFileServer.browse" class="rounded border-gray-600" />
                <span>允许浏览目录</span>
              </label>
              <label class="flex items-center gap-2 text-sm text-gray-300 cursor-pointer">
                <input type="checkbox" v-model="editFileServer.preCompressed" class="rounded border-gray-600" />
                <span>预压缩文件</span>
              </label>
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">TryFiles <span class="text-gray-600">(逗号分隔)</span></label>
              <input v-model="editFileServer.tryFiles" type="text"
                class="input w-full font-mono text-sm" placeholder="$path, index.html" />
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">DefaultFiles <span class="text-gray-600">(逗号分隔)</span></label>
              <input v-model="editFileServer.defaultFiles" type="text"
                class="input w-full font-mono text-sm" placeholder="index.html, index.htm" />
            </div>
            <div class="flex justify-end gap-2 pt-2 border-t border-dark-border/50">
              <button @click="showEditDialog = false" class="btn btn-sm btn-secondary">取消</button>
              <button @click="submitEdit" :disabled="editSaving" class="btn btn-sm btn-primary flex items-center gap-1.5">
                <span v-if="editSaving" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin"></span>
                <span>确定</span>
              </button>
            </div>
          </div>

          <!-- 代理路由编辑表单 -->
          <div v-else class="p-5 space-y-4">
            <div>
              <label class="block text-xs text-gray-400 mb-1">目标集群 (ClusterId)</label>
              <select v-model="editProxy.clusterId" class="input w-full text-sm">
                <option v-for="cid in clusterOptions" :key="cid" :value="cid">{{ cid }}</option>
              </select>
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">匹配路径 (Path)</label>
              <input v-model="editProxy.path" type="text"
                class="input w-full font-mono text-sm" placeholder="{**catch-all}" />
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">Hosts <span class="text-gray-600">(逗号分隔)</span></label>
              <input v-model="editProxy.hosts" type="text"
                class="input w-full font-mono text-sm" placeholder="localhost:5002, *.example.com" />
            </div>
            <div class="grid grid-cols-2 gap-3">
              <div>
                <label class="block text-xs text-gray-400 mb-1">Methods <span class="text-gray-600">(逗号分隔)</span></label>
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
              <button @click="showEditDialog = false" class="btn btn-sm btn-secondary">取消</button>
              <button @click="submitEdit" :disabled="editSaving" class="btn btn-sm btn-primary flex items-center gap-1.5">
                <span v-if="editSaving" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin"></span>
                <span>确定</span>
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
            <h3 class="text-base font-semibold text-gray-100 mb-3">确认删除</h3>
            <p class="text-sm text-gray-400 mb-1">
              确定要删除以下{{ serviceTypeLabel(confirmDeleteRoute.serviceType) }}吗？
            </p>
            <p class="font-mono text-sm text-gray-300 mb-4 bg-dark-sidebar rounded px-3 py-2 border border-dark-border">
              {{ confirmDeleteRoute.routeId }}
            </p>
            <div class="flex justify-end gap-2">
              <button @click="confirmDeleteRoute = null" class="btn btn-sm btn-secondary">取消</button>
              <button @click="executeDelete" :disabled="!!deleting" class="btn btn-sm bg-red-600 hover:bg-red-500 text-white flex items-center gap-1.5">
                <span v-if="deleting" class="w-3 h-3 border-2 border-current border-t-transparent rounded-full animate-spin"></span>
                <span>确认删除</span>
              </button>
            </div>
          </div>
        </div>
      </div>
    </Teleport>
  </div>
</template>
