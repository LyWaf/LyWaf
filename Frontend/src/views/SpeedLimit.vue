<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { throttleApi } from '@/api'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()

// 状态
const loading = ref(false)
const enabled = ref(true)
const toggling = ref(false)
const globalLimit = ref(0)
const pathLimits = ref<{ path: string; limitKbps: number; editing?: boolean; editValue?: string }[]>([])
const ipLimits = ref<{ ip: string; limitKbps: number; editing?: boolean; editValue?: string }[]>([])

// 全局限速编辑
const globalEditing = ref(false)
const globalEditValue = ref('')
const startEditGlobal = () => {
  globalEditing.value = true
  globalEditValue.value = String(globalLimit.value)
}
const saveGlobal = async () => {
  const val = parseInt(globalEditValue.value || '0')
  if (isNaN(val) || val < 0) return showError('请输入有效数值')
  try {
    const res = await throttleApi.setGlobal(val) as any
    if (res.success) {
      globalLimit.value = val
      globalEditing.value = false
      showSuccess(res.message)
      await autoSavePatch()
    } else {
      showError(res.message || '设置失败')
    }
  } catch {
    showError('设置全局限速失败')
  }
}

// 新增表单
const newIp = ref('')
const newIpLimit = ref(100)
const newPath = ref('')
const newPathLimit = ref(1024)

// 自动保存补丁
const autoSavePatch = async () => {
  try {
    await throttleApi.savePatch()
  } catch {
    // 静默失败，主操作已成功
  }
}

// 切换启用状态
const toggleEnabled = async () => {
  toggling.value = true
  try {
    const res = await throttleApi.toggle() as any
    if (res.success) {
      enabled.value = res.enabled
      showSuccess(res.message)
      await autoSavePatch()
    }
  } catch {
    showError('切换失败')
  } finally {
    toggling.value = false
  }
}

// 加载配置
const loadConfig = async () => {
  loading.value = true
  try {
    const res = await throttleApi.getConfig() as any
    if (res.success) {
      enabled.value = res.enabled !== false
      globalLimit.value = res.global || 0
      pathLimits.value = Object.entries(res.pathLimits || {}).map(([path, limitKbps]) => ({
        path,
        limitKbps: limitKbps as number,
      }))
      ipLimits.value = Object.entries(res.ipLimits || {}).map(([ip, limitKbps]) => ({
        ip,
        limitKbps: limitKbps as number,
      }))
    }
  } catch (e) {
    showError('加载限速配置失败')
  } finally {
    loading.value = false
  }
}

// IP 限速操作
const addIpThrottle = async () => {
  const ip = newIp.value.trim()
  if (!ip) return showError('请输入 IP 地址')
  if (newIpLimit.value <= 0) return showError('限速值必须大于 0')
  try {
    const res = await throttleApi.addIpThrottle(ip, newIpLimit.value) as any
    if (res.success) {
      showSuccess(res.message || '添加成功')
      newIp.value = ''
      newIpLimit.value = 100
      await loadConfig()
      await autoSavePatch()
    } else {
      showError(res.message || '添加失败')
    }
  } catch {
    showError('添加 IP 限速失败')
  }
}

const removeIpThrottle = async (ip: string) => {
  try {
    const res = await throttleApi.removeIpThrottle(ip) as any
    if (res.success) {
      showSuccess(res.message || '已移除')
      await loadConfig()
      await autoSavePatch()
    } else {
      showError(res.message || '移除失败')
    }
  } catch {
    showError('移除 IP 限速失败')
  }
}

// 编辑 IP 限速
const startEditIp = (item: { ip: string; limitKbps: number; editing?: boolean; editValue?: string }) => {
  item.editing = true
  item.editValue = String(item.limitKbps)
}

const saveEditIp = async (item: { ip: string; limitKbps: number; editing?: boolean; editValue?: string }) => {
  const val = parseInt(item.editValue || '0')
  if (!val || val <= 0) return showError('限速值必须大于 0')
  try {
    const res = await throttleApi.addIpThrottle(item.ip, val) as any
    if (res.success) {
      item.limitKbps = val
      item.editing = false
      showSuccess('已更新')
      await autoSavePatch()
    } else {
      showError(res.message || '更新失败')
    }
  } catch {
    showError('更新 IP 限速失败')
  }
}

const cancelEditIp = (item: { editing?: boolean }) => {
  item.editing = false
}

// 路径限速操作
const addPathThrottle = async () => {
  const path = newPath.value.trim()
  if (!path) return showError('请输入路径')
  if (newPathLimit.value <= 0) return showError('限速值必须大于 0')
  try {
    const res = await throttleApi.addPathThrottle(path, newPathLimit.value) as any
    if (res.success) {
      showSuccess(res.message || '添加成功')
      newPath.value = ''
      newPathLimit.value = 1024
      await loadConfig()
      await autoSavePatch()
    } else {
      showError(res.message || '添加失败')
    }
  } catch {
    showError('添加路径限速失败')
  }
}

const removePathThrottle = async (path: string) => {
  try {
    const res = await throttleApi.removePathThrottle(path) as any
    if (res.success) {
      showSuccess(res.message || '已移除')
      await loadConfig()
      await autoSavePatch()
    } else {
      showError(res.message || '移除失败')
    }
  } catch {
    showError('移除路径限速失败')
  }
}

// 编辑路径限速
const startEditPath = (item: { path: string; limitKbps: number; editing?: boolean; editValue?: string }) => {
  item.editing = true
  item.editValue = String(item.limitKbps)
}

const saveEditPath = async (item: { path: string; limitKbps: number; editing?: boolean; editValue?: string }) => {
  const val = parseInt(item.editValue || '0')
  if (!val || val <= 0) return showError('限速值必须大于 0')
  try {
    const res = await throttleApi.addPathThrottle(item.path, val) as any
    if (res.success) {
      item.limitKbps = val
      item.editing = false
      showSuccess('已更新')
      await autoSavePatch()
    } else {
      showError(res.message || '更新失败')
    }
  } catch {
    showError('更新路径限速失败')
  }
}

const cancelEditPath = (item: { editing?: boolean }) => {
  item.editing = false
}

// 删除补丁
const deletingPatch = ref(false)
const removePatch = async () => {
  deletingPatch.value = true
  try {
    const res = await throttleApi.removePatch() as any
    if (res.success) {
      showSuccess(res.message || '已删除补丁')
      await loadConfig()
    } else {
      showError(res.message || '删除失败')
    }
  } catch {
    showError('删除补丁失败')
  } finally {
    deletingPatch.value = false
  }
}

// 格式化速度
const formatSpeed = (kbps: number) => {
  if (kbps >= 1024) return `${(kbps / 1024).toFixed(1)} MB/s`
  return `${kbps} KB/s`
}

onMounted(loadConfig)
</script>

<template>
  <div class="space-y-6">
    <!-- 顶部操作 -->
    <div class="flex items-center justify-between">
      <div class="flex items-center gap-4">
        <h1 class="text-xl font-bold">速度限制</h1>
        <button
          @click="toggleEnabled"
          :disabled="toggling"
          :class="[
            'relative w-12 h-6 rounded-full transition-colors flex-shrink-0',
            enabled ? 'bg-primary-500' : 'bg-gray-600'
          ]"
        >
          <span
            :class="[
              'absolute top-1 w-4 h-4 rounded-full bg-white transition-transform',
              enabled ? 'left-7' : 'left-1'
            ]"
          />
        </button>
        <span class="text-sm" :class="enabled ? 'text-green-400' : 'text-gray-500'">
          {{ enabled ? '已启用' : '已禁用' }}
        </span>
      </div>
      <button @click="removePatch" :disabled="deletingPatch" class="btn btn-sm btn-danger">
        {{ deletingPatch ? '删除中...' : '删除补丁' }}
      </button>
    </div>

    <!-- 全局限速 -->
    <div class="card">
      <div class="flex items-center justify-between mb-4">
        <h2 class="text-lg font-bold">全局带宽限速</h2>
      </div>
      <p class="text-sm text-gray-400 mb-3">对所有响应进行带宽限制，设为 0 表示不限制</p>
      <div class="flex items-center gap-3">
        <template v-if="globalEditing">
          <input
            v-model="globalEditValue"
            type="text" inputmode="numeric"
            class="input w-32 text-right"
            @keyup.enter="saveGlobal"
            @keyup.escape="globalEditing = false"
          />
          <span class="text-sm text-gray-400">KB/s</span>
          <button @click="saveGlobal" class="btn btn-sm btn-primary">保存</button>
          <button @click="globalEditing = false" class="btn btn-sm btn-secondary">取消</button>
        </template>
        <template v-else>
          <span class="font-mono text-blue-400 text-lg cursor-pointer hover:underline" @click="startEditGlobal">
            {{ globalLimit > 0 ? formatSpeed(globalLimit) : '未限制' }}
          </span>
          <button @click="startEditGlobal" class="btn btn-sm btn-secondary">修改</button>
        </template>
      </div>
    </div>

    <!-- IP 限速 -->
    <div class="card">
      <h2 class="text-lg font-bold mb-4">IP 带宽限速</h2>
      <p class="text-sm text-gray-400 mb-4">对指定 IP 地址进行带宽限制，限制该 IP 的响应速度</p>

      <!-- 新增 -->
      <div class="flex items-center gap-3 mb-4">
        <input
          v-model="newIp"
          type="text"
          placeholder="IP 地址，如 192.168.1.100"
          class="input flex-1"
          @keyup.enter="addIpThrottle"
        />
        <div class="flex items-center gap-1">
          <input
            v-model.number="newIpLimit"
            type="text" inputmode="numeric"
            class="input w-28 text-right"
          />
          <span class="text-sm text-gray-400 whitespace-nowrap">KB/s</span>
        </div>
        <button @click="addIpThrottle" class="btn btn-sm btn-primary whitespace-nowrap">添加</button>
      </div>

      <!-- 列表 -->
      <div v-if="loading" class="text-center py-4 text-gray-400">加载中...</div>
      <div v-else-if="ipLimits.length === 0" class="text-center py-6 text-gray-500">暂无 IP 限速规则</div>
      <table v-else class="w-full">
        <thead>
          <tr class="text-left text-sm text-gray-400 border-b border-gray-700">
            <th class="pb-2">IP 地址</th>
            <th class="pb-2">限速</th>
            <th class="pb-2 text-right">操作</th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="item in ipLimits"
            :key="item.ip"
            class="border-b border-gray-700/50 hover:bg-white/5"
          >
            <td class="py-2 font-mono text-sm">{{ item.ip }}</td>
            <td class="py-2 text-sm">
              <template v-if="item.editing">
                <div class="flex items-center gap-1">
                  <input
                    v-model="item.editValue"
                    type="text" inputmode="numeric"
                    class="input w-24 text-right text-sm"
                    @keyup.enter="saveEditIp(item)"
                    @keyup.escape="cancelEditIp(item)"
                  />
                  <span class="text-gray-400 text-xs">KB/s</span>
                </div>
              </template>
              <span v-else class="text-blue-400 cursor-pointer hover:underline" @click="startEditIp(item)">
                {{ formatSpeed(item.limitKbps) }}
              </span>
            </td>
            <td class="py-2 text-right">
              <div class="flex items-center justify-end gap-2">
                <template v-if="item.editing">
                  <button @click="saveEditIp(item)" class="btn btn-sm btn-primary">保存</button>
                  <button @click="cancelEditIp(item)" class="btn btn-sm btn-secondary">取消</button>
                </template>
                <button
                  v-else
                  @click="removeIpThrottle(item.ip)"
                  class="btn btn-sm btn-danger"
                >
                  移除
                </button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- 路径限速 -->
    <div class="card">
      <h2 class="text-lg font-bold mb-4">路径带宽限速</h2>
      <p class="text-sm text-gray-400 mb-4">对指定 URL 路径进行带宽限制，路径末尾加 * 支持前缀匹配</p>

      <!-- 新增 -->
      <div class="flex items-center gap-3 mb-4">
        <input
          v-model="newPath"
          type="text"
          placeholder="URL 路径，如 /download/* 或 /api/export"
          class="input flex-1"
          @keyup.enter="addPathThrottle"
        />
        <div class="flex items-center gap-1">
          <input
            v-model.number="newPathLimit"
            type="text" inputmode="numeric"
            class="input w-28 text-right"
          />
          <span class="text-sm text-gray-400 whitespace-nowrap">KB/s</span>
        </div>
        <button @click="addPathThrottle" class="btn btn-sm btn-primary whitespace-nowrap">添加</button>
      </div>

      <!-- 列表 -->
      <div v-if="loading" class="text-center py-4 text-gray-400">加载中...</div>
      <div v-else-if="pathLimits.length === 0" class="text-center py-6 text-gray-500">暂无路径限速规则</div>
      <table v-else class="w-full">
        <thead>
          <tr class="text-left text-sm text-gray-400 border-b border-gray-700">
            <th class="pb-2">路径</th>
            <th class="pb-2">限速</th>
            <th class="pb-2 text-right">操作</th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="item in pathLimits"
            :key="item.path"
            class="border-b border-gray-700/50 hover:bg-white/5"
          >
            <td class="py-2 font-mono text-sm">{{ item.path }}</td>
            <td class="py-2 text-sm">
              <template v-if="item.editing">
                <div class="flex items-center gap-1">
                  <input
                    v-model="item.editValue"
                    type="text" inputmode="numeric"
                    class="input w-24 text-right text-sm"
                    @keyup.enter="saveEditPath(item)"
                    @keyup.escape="cancelEditPath(item)"
                  />
                  <span class="text-gray-400 text-xs">KB/s</span>
                </div>
              </template>
              <span v-else class="text-blue-400 cursor-pointer hover:underline" @click="startEditPath(item)">
                {{ formatSpeed(item.limitKbps) }}
              </span>
            </td>
            <td class="py-2 text-right">
              <div class="flex items-center justify-end gap-2">
                <template v-if="item.editing">
                  <button @click="saveEditPath(item)" class="btn btn-sm btn-primary">保存</button>
                  <button @click="cancelEditPath(item)" class="btn btn-sm btn-secondary">取消</button>
                </template>
                <button
                  v-else
                  @click="removePathThrottle(item.path)"
                  class="btn btn-sm btn-danger"
                >
                  移除
                </button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
