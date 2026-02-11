<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { routeApi } from '@/api'
import { useToast } from '@/composables/useToast'
import type { RouteConfig } from '@/types'

const { showSuccess, showError } = useToast()

// 数据状态
const loading = ref(false)
const routes = ref<RouteConfig[]>([])

// 对话框状态
const showDialog = ref(false)
const dialogMode = ref<'add' | 'edit'>('add')
const saving = ref(false)
const editingRoute = ref<RouteConfig | null>(null)
const form = ref({
  routeId: '',
  path: '',
  hosts: '',
  methods: '',
  order: 0,
  clusterId: '',
})

// 加载数据
const loadData = async () => {
  loading.value = true
  try {
    const res = await routeApi.getRoutes()
    if (res.success) {
      routes.value = res.routes
    }
  } catch (error) {
    showError('加载路由配置失败')
  } finally {
    loading.value = false
  }
}

// 打开新增对话框
const openAddDialog = () => {
  dialogMode.value = 'add'
  editingRoute.value = null
  form.value = {
    routeId: '',
    path: '/{**catch-all}',
    hosts: '',
    methods: '',
    order: 0,
    clusterId: '',
  }
  showDialog.value = true
}

// 打开编辑对话框
const openEditDialog = (route: RouteConfig) => {
  dialogMode.value = 'edit'
  editingRoute.value = route
  form.value = {
    routeId: route.routeId,
    path: route.match.path,
    hosts: route.match.hosts.join(', '),
    methods: route.match.methods.join(', '),
    order: route.order,
    clusterId: route.clusterId,
  }
  showDialog.value = true
}

// 保存（新增/编辑）
const handleSave = async () => {
  saving.value = true
  try {
    const hosts = form.value.hosts
      .split(/[,\s]+/)
      .map(h => h.trim())
      .filter(h => h.length > 0)

    const methods = form.value.methods
      .split(/[,\s]+/)
      .map(m => m.trim().toUpperCase())
      .filter(m => m.length > 0)

    if (dialogMode.value === 'add') {
      if (!form.value.routeId.trim()) {
        showError('路由 ID 不能为空')
        saving.value = false
        return
      }
      const res = await routeApi.addRoute({
        routeId: form.value.routeId.trim(),
        clusterId: form.value.clusterId || undefined,
        order: form.value.order,
        match: {
          path: form.value.path || undefined,
          hosts,
          methods,
        },
      })
      if (res.success) {
        showSuccess('路由已新增')
        showDialog.value = false
        await loadData()
      } else {
        showError(res.message || '新增失败')
      }
    } else {
      if (!editingRoute.value) return
      const res = await routeApi.updateRoute({
        routeId: editingRoute.value.routeId,
        clusterId: form.value.clusterId || undefined,
        order: form.value.order,
        match: {
          path: form.value.path || undefined,
          hosts,
          methods,
        },
      })
      if (res.success) {
        showSuccess('路由已更新')
        showDialog.value = false
        await loadData()
      } else {
        showError(res.message || '更新失败')
      }
    }
  } catch (error) {
    showError(getErrorMessage(error, dialogMode.value === 'add' ? '新增失败' : '更新失败'))
  } finally {
    saving.value = false
  }
}

// 从 axios 错误中提取服务端 message
const getErrorMessage = (error: unknown, fallback: string): string => {
  if (error && typeof error === 'object' && 'response' in error) {
    const res = (error as { response?: { data?: { message?: string } } }).response
    if (res?.data?.message) return res.data.message
  }
  return fallback
}

// 删除单个路由（补丁新增的路由）
const removeRoute = async (route: RouteConfig) => {
  if (!confirm(`确定要删除路由 "${route.routeId}" 吗？`)) return
  try {
    const res = await routeApi.removeRoute(route.routeId)
    if (res.success) {
      showSuccess('路由已删除')
      await loadData()
    } else {
      showError(res.message || '删除失败')
    }
  } catch (error) {
    showError(getErrorMessage(error, '删除失败'))
  }
}

// 删除所有路由补丁
const removeRoutePatch = async () => {
  if (!confirm('确定要删除所有路由的补丁数据吗？这将恢复为原始配置。')) return
  try {
    const res = await routeApi.removeRoutePatch('')
    if (res.success) {
      showSuccess('路由补丁已删除，已恢复原始配置')
      await loadData()
    } else {
      showError(res.message || '删除补丁失败')
    }
  } catch (error) {
    showError(getErrorMessage(error, '删除补丁失败'))
  }
}

onMounted(loadData)
</script>

<template>
  <div class="space-y-6">
    <!-- 路由列表 -->
    <div class="card">
      <div class="flex items-center justify-between mb-4">
        <h2 class="text-lg font-medium text-gray-100">配置路由</h2>
        <div class="flex items-center gap-2">
          <button @click="removeRoutePatch" class="btn btn-sm btn-warning flex items-center gap-1">
            删除补丁
          </button>
          <button @click="openAddDialog" class="btn btn-sm btn-primary flex items-center gap-1">
            <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
            </svg>
            新增
          </button>
        </div>
      </div>

      <!-- 表格 -->
      <div class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead>
            <tr class="border-b border-dark-border text-gray-400">
              <th class="py-3 px-3 text-left">路由 ID</th>
              <th class="py-3 px-3 text-left">来源</th>
              <th class="py-3 px-3 text-left">Order</th>
              <th class="py-3 px-3 text-left">Path</th>
              <th class="py-3 px-3 text-left">Hosts</th>
              <th class="py-3 px-3 text-left">Methods</th>
              <th class="py-3 px-3 text-left">集群</th>
              <th class="py-3 px-3 text-left">操作</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="route in routes"
              :key="route.routeId"
              class="border-b border-dark-border/50 hover:bg-white/5 transition-colors"
            >
              <td class="py-3 px-3 text-gray-200 font-mono">{{ route.routeId }}</td>
              <td class="py-3 px-3">
                <span
                  v-if="route.source === 'patch'"
                  class="inline-block px-1.5 py-0.5 text-xs bg-yellow-500/20 text-yellow-400 rounded"
                >补丁</span>
                <span
                  v-else
                  class="inline-block px-1.5 py-0.5 text-xs bg-gray-500/20 text-gray-400 rounded"
                >默认</span>
              </td>
              <td class="py-3 px-3 text-gray-300">{{ route.order }}</td>
              <td class="py-3 px-3 text-primary-400 font-mono">{{ route.match.path || '-' }}</td>
              <td class="py-3 px-3 text-gray-300">
                <div v-if="route.match.hosts.length > 0" class="flex flex-wrap gap-1">
                  <span
                    v-for="host in route.match.hosts"
                    :key="host"
                    class="inline-block px-1.5 py-0.5 text-xs bg-blue-500/20 text-blue-400 rounded"
                  >{{ host }}</span>
                </div>
                <span v-else class="text-gray-500">*</span>
              </td>
              <td class="py-3 px-3 text-gray-300">
                <div v-if="route.match.methods.length > 0" class="flex flex-wrap gap-1">
                  <span
                    v-for="method in route.match.methods"
                    :key="method"
                    class="inline-block px-1.5 py-0.5 text-xs bg-green-500/20 text-green-400 rounded"
                  >{{ method }}</span>
                </div>
                <span v-else class="text-gray-500">ALL</span>
              </td>
              <td class="py-3 px-3 text-gray-300">{{ route.clusterId }}</td>
              <td class="py-3 px-3">
                <div class="flex items-center gap-2">
                  <button
                    @click="openEditDialog(route)"
                    class="px-3 py-1 text-xs border border-primary-500 text-primary-400 rounded hover:bg-primary-500/10 transition-colors"
                  >
                    编辑
                  </button>
                  <button
                    @click="removeRoute(route)"
                    class="px-3 py-1 text-xs border border-red-500 text-red-400 rounded hover:bg-red-500/10 transition-colors"
                  >
                    删除
                  </button>
                </div>
              </td>
            </tr>
          </tbody>
        </table>

        <!-- 空状态 -->
        <div v-if="!loading && routes.length === 0" class="text-center py-12 text-gray-500">
          暂无路由配置
        </div>
      </div>
    </div>

    <!-- 加载中 -->
    <div v-if="loading" class="text-center py-12 text-gray-500">
      加载中...
    </div>

    <!-- 新增/编辑对话框 -->
    <Teleport to="body">
      <div v-if="showDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
        <!-- 遮罩 -->
        <div class="absolute inset-0 bg-black/60" @click="showDialog = false"></div>

        <!-- 对话框 -->
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-full max-w-lg mx-4 p-6">
          <h3 class="text-lg font-medium text-gray-100 mb-4">
            {{ dialogMode === 'add' ? '新增路由' : '编辑路由' }}
            <span v-if="dialogMode === 'edit'" class="text-primary-400 font-mono">{{ editingRoute?.routeId }}</span>
          </h3>

          <div class="space-y-4">
            <!-- Route ID (仅新增时) -->
            <div v-if="dialogMode === 'add'">
              <label class="block text-sm text-gray-400 mb-1">路由 ID</label>
              <input
                v-model="form.routeId"
                type="text"
                placeholder="如: route1"
                class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 font-mono focus:outline-none focus:border-primary-500"
              />
            </div>

            <!-- Order + ClusterId -->
            <div class="flex gap-3">
              <div class="w-24">
                <label class="block text-sm text-gray-400 mb-1">Order</label>
                <input
                  v-model.number="form.order"
                  type="number"
                  class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 focus:outline-none focus:border-primary-500"
                />
              </div>
              <div class="flex-1">
                <label class="block text-sm text-gray-400 mb-1">集群 ID</label>
                <input
                  v-model="form.clusterId"
                  type="text"
                  placeholder="cluster1"
                  class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 focus:outline-none focus:border-primary-500"
                />
              </div>
            </div>

            <!-- Path -->
            <div>
              <label class="block text-sm text-gray-400 mb-1">Path</label>
              <input
                v-model="form.path"
                type="text"
                placeholder="如: /{**catch-all}"
                class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 font-mono focus:outline-none focus:border-primary-500"
              />
            </div>

            <!-- Hosts -->
            <div>
              <label class="block text-sm text-gray-400 mb-1">Hosts <span class="text-gray-600">(多个用逗号分隔，留空匹配所有)</span></label>
              <input
                v-model="form.hosts"
                type="text"
                placeholder="如: example.com, *.example.com"
                class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 font-mono focus:outline-none focus:border-primary-500"
              />
            </div>

            <!-- Methods -->
            <div>
              <label class="block text-sm text-gray-400 mb-1">Methods <span class="text-gray-600">(多个用逗号分隔，留空匹配所有)</span></label>
              <input
                v-model="form.methods"
                type="text"
                placeholder="如: GET, POST"
                class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 font-mono focus:outline-none focus:border-primary-500"
              />
            </div>
          </div>

          <!-- 按钮 -->
          <div class="flex justify-end gap-3 mt-6">
            <button
              @click="showDialog = false"
              class="px-4 py-2 text-sm text-gray-400 border border-dark-border rounded-lg hover:bg-white/5 transition-colors"
            >
              取消
            </button>
            <button
              @click="handleSave"
              :disabled="saving"
              class="px-4 py-2 text-sm bg-primary-500 text-gray-900 rounded-lg hover:bg-primary-400 transition-colors disabled:opacity-50"
            >
              {{ saving ? '保存中...' : '确定' }}
            </button>
          </div>
        </div>
      </div>
    </Teleport>
  </div>
</template>
