<script setup lang="ts">
import { t } from '@/i18n'
import { ref, onMounted } from 'vue'
import { throttleApi } from '@/api'
import { useToast } from '@/composables/useToast'
import ConnectionLimitPanel from '@/components/dashboard/ConnectionLimitPanel.vue'

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
  if (isNaN(val) || val < 0) return showError(t('page.f9c48451c226'))
  try {
    const res = await throttleApi.setGlobal(val) as any
    if (res.success) {
      globalLimit.value = val
      globalEditing.value = false
      showSuccess(res.message)
      await autoSavePatch()
    } else {
      showError(res.message || t('page.bf76bc819550'))
    }
  } catch {
    showError(t('page.ff3bc79d5cbc'))
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
    showError(t('page.d08fd4224abc'))
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
    showError(t('page.a1792f1fd7dd'))
  } finally {
    loading.value = false
  }
}

// IP 限速操作
const addIpThrottle = async () => {
  const ip = newIp.value.trim()
  if (!ip) return showError(t('dashboard.enter.an.ip.address'))
  if (newIpLimit.value <= 0) return showError(t('page.fe1bd8a37695'))
  try {
    const res = await throttleApi.addIpThrottle(ip, newIpLimit.value) as any
    if (res.success) {
      showSuccess(res.message || t('page.ee1a3c93ab01'))
      newIp.value = ''
      newIpLimit.value = 100
      await loadConfig()
      await autoSavePatch()
    } else {
      showError(res.message || t('dashboard.failed.to.add'))
    }
  } catch {
    showError(t('page.574e298dfb27'))
  }
}

const removeIpThrottle = async (ip: string) => {
  try {
    const res = await throttleApi.removeIpThrottle(ip) as any
    if (res.success) {
      showSuccess(res.message || t('page.174688c755b9'))
      await loadConfig()
      await autoSavePatch()
    } else {
      showError(res.message || t('page.4757fc1d4709'))
    }
  } catch {
    showError(t('page.5feea8bee124'))
  }
}

// 编辑 IP 限速
const startEditIp = (item: { ip: string; limitKbps: number; editing?: boolean; editValue?: string }) => {
  item.editing = true
  item.editValue = String(item.limitKbps)
}

const saveEditIp = async (item: { ip: string; limitKbps: number; editing?: boolean; editValue?: string }) => {
  const val = parseInt(item.editValue || '0')
  if (!val || val <= 0) return showError(t('page.fe1bd8a37695'))
  try {
    const res = await throttleApi.addIpThrottle(item.ip, val) as any
    if (res.success) {
      item.limitKbps = val
      item.editing = false
      showSuccess(t('page.434203a723b4'))
      await autoSavePatch()
    } else {
      showError(res.message || t('page.ec99e5c45d64'))
    }
  } catch {
    showError(t('page.d752c38895ae'))
  }
}

const cancelEditIp = (item: { editing?: boolean }) => {
  item.editing = false
}

// 路径限速操作
const addPathThrottle = async () => {
  const path = newPath.value.trim()
  if (!path) return showError(t('page.13c40c0b2513'))
  if (newPathLimit.value <= 0) return showError(t('page.fe1bd8a37695'))
  try {
    const res = await throttleApi.addPathThrottle(path, newPathLimit.value) as any
    if (res.success) {
      showSuccess(res.message || t('page.ee1a3c93ab01'))
      newPath.value = ''
      newPathLimit.value = 1024
      await loadConfig()
      await autoSavePatch()
    } else {
      showError(res.message || t('dashboard.failed.to.add'))
    }
  } catch {
    showError(t('page.f33e8c28a239'))
  }
}

const removePathThrottle = async (path: string) => {
  try {
    const res = await throttleApi.removePathThrottle(path) as any
    if (res.success) {
      showSuccess(res.message || t('page.174688c755b9'))
      await loadConfig()
      await autoSavePatch()
    } else {
      showError(res.message || t('page.4757fc1d4709'))
    }
  } catch {
    showError(t('page.24611120eed0'))
  }
}

// 编辑路径限速
const startEditPath = (item: { path: string; limitKbps: number; editing?: boolean; editValue?: string }) => {
  item.editing = true
  item.editValue = String(item.limitKbps)
}

const saveEditPath = async (item: { path: string; limitKbps: number; editing?: boolean; editValue?: string }) => {
  const val = parseInt(item.editValue || '0')
  if (!val || val <= 0) return showError(t('page.fe1bd8a37695'))
  try {
    const res = await throttleApi.addPathThrottle(item.path, val) as any
    if (res.success) {
      item.limitKbps = val
      item.editing = false
      showSuccess(t('page.434203a723b4'))
      await autoSavePatch()
    } else {
      showError(res.message || t('page.ec99e5c45d64'))
    }
  } catch {
    showError(t('page.7a0f921d9a81'))
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
      showSuccess(res.message || t('page.25daa3a60a35'))
      await loadConfig()
    } else {
      showError(res.message || t('settings.deletion.failed'))
    }
  } catch {
    showError(t('page.2f476ccaec39'))
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
        <h1 class="text-xl font-bold">{{ t('nav.speed') }}</h1>
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
          {{ enabled ? t('common.enabled') : t('common.disabled') }}
        </span>
      </div>
      <button @click="removePatch" :disabled="deletingPatch" class="btn btn-sm btn-danger">
        {{ deletingPatch ? t('settings.deleting') : t('page.c9fee9110ba0') }}
      </button>
    </div>

    <!-- 全局限速 -->
    <div class="card">
      <div class="flex items-center justify-between mb-4">
        <h2 class="text-lg font-bold">{{ t('page.8619ab93dbbe') }}</h2>
      </div>
      <p class="text-sm text-gray-400 mb-3">{{ t('page.e1dcf179ae81') }}</p>
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
          <button @click="saveGlobal" class="btn btn-sm btn-primary">{{ t('common.save') }}</button>
          <button @click="globalEditing = false" class="btn btn-sm btn-secondary">{{ t('common.cancel') }}</button>
        </template>
        <template v-else>
          <span class="font-mono text-blue-400 text-lg cursor-pointer hover:underline" @click="startEditGlobal">
            {{ globalLimit > 0 ? formatSpeed(globalLimit) : t('page.67a4d26098f4') }}
          </span>
          <button @click="startEditGlobal" class="btn btn-sm btn-secondary">{{ t('page.37090f456516') }}</button>
        </template>
      </div>
    </div>

    <!-- IP 限速 -->
    <div class="card">
      <h2 class="text-lg font-bold mb-4">{{ t('page.43d49aecfab7') }}</h2>
      <p class="text-sm text-gray-400 mb-4">{{ t('page.d1024f385935') }}</p>

      <!-- 新增 -->
      <div class="flex items-center gap-3 mb-4">
        <input
          v-model="newIp"
          type="text"
          :placeholder="t('page.700a69ecd25b')"
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
        <button @click="addIpThrottle" class="btn btn-sm btn-primary whitespace-nowrap">{{ t('common.add') }}</button>
      </div>

      <!-- 列表 -->
      <div v-if="loading" class="text-center py-4 text-gray-400">{{ t('common.loading') }}</div>
      <div v-else-if="ipLimits.length === 0" class="text-center py-6 text-gray-500">{{ t('page.87efbedca0d7') }}</div>
      <table v-else class="w-full">
        <thead>
          <tr class="text-left text-sm text-gray-400 border-b border-gray-700">
            <th class="pb-2">{{ t('settings.ip.address') }}</th>
            <th class="pb-2">{{ t('dashboard.throttled') }}</th>
            <th class="pb-2 text-right">{{ t('settings.actions') }}</th>
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
                  <button @click="saveEditIp(item)" class="btn btn-sm btn-primary">{{ t('common.save') }}</button>
                  <button @click="cancelEditIp(item)" class="btn btn-sm btn-secondary">{{ t('common.cancel') }}</button>
                </template>
                <template v-else>
                  <button @click="startEditIp(item)" class="btn btn-sm btn-primary">{{ t('common.edit') }}</button>
                  <button @click="removeIpThrottle(item.ip)" class="btn btn-sm btn-danger">{{ t('page.6135d4159e89') }}</button>
                </template>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- 路径限速 -->
    <div class="card">
      <h2 class="text-lg font-bold mb-4">{{ t('page.cd4b279dbc8e') }}</h2>
      <p class="text-sm text-gray-400 mb-4">{{ t('page.d49704ce424b') }}</p>

      <!-- 新增 -->
      <div class="flex items-center gap-3 mb-4">
        <input
          v-model="newPath"
          type="text"
          :placeholder="t('page.922a870f158e')"
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
        <button @click="addPathThrottle" class="btn btn-sm btn-primary whitespace-nowrap">{{ t('common.add') }}</button>
      </div>

      <!-- 列表 -->
      <div v-if="loading" class="text-center py-4 text-gray-400">{{ t('common.loading') }}</div>
      <div v-else-if="pathLimits.length === 0" class="text-center py-6 text-gray-500">{{ t('page.6dfc476dab10') }}</div>
      <table v-else class="w-full">
        <thead>
          <tr class="text-left text-sm text-gray-400 border-b border-gray-700">
            <th class="pb-2">{{ t('ui.path') }}</th>
            <th class="pb-2">{{ t('dashboard.throttled') }}</th>
            <th class="pb-2 text-right">{{ t('settings.actions') }}</th>
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
                  <button @click="saveEditPath(item)" class="btn btn-sm btn-primary">{{ t('common.save') }}</button>
                  <button @click="cancelEditPath(item)" class="btn btn-sm btn-secondary">{{ t('common.cancel') }}</button>
                </template>
                <template v-else>
                  <button @click="startEditPath(item)" class="btn btn-sm btn-primary">{{ t('common.edit') }}</button>
                  <button @click="removePathThrottle(item.path)" class="btn btn-sm btn-danger">{{ t('page.6135d4159e89') }}</button>
                </template>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- 并发连接限制 -->
    <ConnectionLimitPanel />
  </div>
</template>
