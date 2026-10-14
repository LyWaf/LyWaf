<script setup lang="ts">
import { t } from '@/i18n'
import { ref, onMounted } from 'vue'
import { connectionLimitApi } from '@/api'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()

const loading = ref(false)
const enabled = ref(false)
const maxPerIp = ref(0)
const maxPerDest = ref(0)
const maxTotal = ref(0)
const pathLimits = ref<Record<string, number>>({})

// 编辑状态
const editingField = ref<string | null>(null)
const editValue = ref('')

// 新增路径限制
const newPath = ref('')
const newPathMax = ref('100')

// 统计
const stats = ref({ totalConnections: 0, connectionsPerIp: 0, connectionsPerDestination: 0 })

const loadConfig = async () => {
  loading.value = true
  try {
    const res = await connectionLimitApi.getConfig() as any
    if (res.success) {
      enabled.value = res.enabled
      maxPerIp.value = res.maxConnectionsPerIp
      maxPerDest.value = res.maxConnectionsPerDestination
      maxTotal.value = res.maxTotalConnections
      pathLimits.value = res.pathLimits || {}
      stats.value = res.stats || stats.value
    }
  } catch (e) {
    console.error(t('page.ee841a2c7f38'), e)
  } finally {
    loading.value = false
  }
}

const toggle = async () => {
  try {
    const res = await connectionLimitApi.toggle() as any
    if (res.success) {
      enabled.value = res.enabled
      showSuccess(res.message)
    }
  } catch {
    showError(t('settings.operation.failed'))
  }
}

const startEdit = (field: string, value: number) => {
  editingField.value = field
  editValue.value = String(value)
}

const cancelEdit = () => {
  editingField.value = null
  editValue.value = ''
}

const saveEdit = async () => {
  const val = parseInt(editValue.value)
  if (isNaN(val) || val < 0) {
    showError(t('page.d1e6c6f01819'))
    return
  }
  const field = editingField.value
  try {
    const config: any = {}
    if (field === 'maxPerIp') config.maxConnectionsPerIp = val
    else if (field === 'maxPerDest') config.maxConnectionsPerDestination = val
    else if (field === 'maxTotal') config.maxTotalConnections = val
    const res = await connectionLimitApi.update(config) as any
    if (res.success) {
      showSuccess(t('page.f3019e95edf6'))
      await loadConfig()
    } else {
      showError(res.message || t('page.ec99e5c45d64'))
    }
  } catch {
    showError(t('page.ec99e5c45d64'))
  } finally {
    cancelEdit()
  }
}

const addPathLimit = async () => {
  const path = newPath.value.trim()
  const max = parseInt(newPathMax.value)
  if (!path || isNaN(max) || max <= 0) {
    showError(t('page.10c9e285ffa8'))
    return
  }
  try {
    const res = await connectionLimitApi.addPathLimit(path, max) as any
    if (res.success) {
      newPath.value = ''
      newPathMax.value = '100'
      showSuccess(res.message)
      await loadConfig()
    } else {
      showError(res.message || t('dashboard.failed.to.add'))
    }
  } catch {
    showError(t('dashboard.failed.to.add'))
  }
}

const removePathLimit = async (path: string) => {
  try {
    const res = await connectionLimitApi.removePathLimit(path) as any
    if (res.success) {
      showSuccess(res.message)
      await loadConfig()
    } else {
      showError(res.message || t('page.4757fc1d4709'))
    }
  } catch {
    showError(t('page.4757fc1d4709'))
  }
}

const formatValue = (val: number) => val === 0 ? t('page.65feea2c2ba2') : String(val)

onMounted(loadConfig)
</script>

<template>
  <div class="card">
    <!-- 标题栏 -->
    <div class="flex items-center justify-between mb-4">
      <div class="flex items-center gap-3">
        <h2 class="text-lg font-bold text-gray-100">{{ t('page.9c02c8961755') }}</h2>
        <span class="text-sm text-gray-500">
          {{ t('dashboard.active.connections') }} {{ stats.totalConnections }}
        </span>
      </div>
      <button
        @click="toggle"
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
    </div>

    <!-- 全局配置 -->
    <div class="grid grid-cols-3 gap-4 mb-5">
      <!-- 单 IP 最大连接数 -->
      <div class="bg-dark-card-hover rounded-lg p-3">
        <div class="text-xs text-gray-500 mb-1">{{ t('page.9e2c059d6cc5') }}</div>
        <template v-if="editingField === 'maxPerIp'">
          <div class="flex items-center gap-1">
            <input
              v-model="editValue"
              type="text" inputmode="numeric"
              class="input w-20 text-sm text-right"
              @keyup.enter="saveEdit"
              @keyup.escape="cancelEdit"
              autofocus
            />
            <button @click="saveEdit" class="text-xs text-primary-400 hover:text-primary-300">{{ t('common.save') }}</button>
            <button @click="cancelEdit" class="text-xs text-gray-400 hover:text-gray-300">{{ t('common.cancel') }}</button>
          </div>
        </template>
        <span
          v-else
          class="text-lg font-bold text-blue-400 cursor-pointer hover:underline"
          @click="startEdit('maxPerIp', maxPerIp)"
        >{{ formatValue(maxPerIp) }}</span>
      </div>

      <!-- 单后端最大连接数 -->
      <div class="bg-dark-card-hover rounded-lg p-3">
        <div class="text-xs text-gray-500 mb-1">{{ t('page.e1585839b904') }}</div>
        <template v-if="editingField === 'maxPerDest'">
          <div class="flex items-center gap-1">
            <input
              v-model="editValue"
              type="text" inputmode="numeric"
              class="input w-20 text-sm text-right"
              @keyup.enter="saveEdit"
              @keyup.escape="cancelEdit"
              autofocus
            />
            <button @click="saveEdit" class="text-xs text-primary-400 hover:text-primary-300">{{ t('common.save') }}</button>
            <button @click="cancelEdit" class="text-xs text-gray-400 hover:text-gray-300">{{ t('common.cancel') }}</button>
          </div>
        </template>
        <span
          v-else
          class="text-lg font-bold text-blue-400 cursor-pointer hover:underline"
          @click="startEdit('maxPerDest', maxPerDest)"
        >{{ formatValue(maxPerDest) }}</span>
      </div>

      <!-- 全局最大连接数 -->
      <div class="bg-dark-card-hover rounded-lg p-3">
        <div class="text-xs text-gray-500 mb-1">{{ t('page.e5373dd47d4c') }}</div>
        <template v-if="editingField === 'maxTotal'">
          <div class="flex items-center gap-1">
            <input
              v-model="editValue"
              type="text" inputmode="numeric"
              class="input w-20 text-sm text-right"
              @keyup.enter="saveEdit"
              @keyup.escape="cancelEdit"
              autofocus
            />
            <button @click="saveEdit" class="text-xs text-primary-400 hover:text-primary-300">{{ t('common.save') }}</button>
            <button @click="cancelEdit" class="text-xs text-gray-400 hover:text-gray-300">{{ t('common.cancel') }}</button>
          </div>
        </template>
        <span
          v-else
          class="text-lg font-bold text-blue-400 cursor-pointer hover:underline"
          @click="startEdit('maxTotal', maxTotal)"
        >{{ formatValue(maxTotal) }}</span>
      </div>
    </div>

    <!-- 路径限制 -->
    <div>
      <h3 class="text-sm font-medium text-gray-400 mb-2">{{ t('page.7d15b43d3f73') }}</h3>
      <p class="text-xs text-gray-500 mb-3">{{ t('page.4d2818ceeb99') }}</p>

      <!-- 添加 -->
      <div class="flex items-center gap-2 mb-3">
        <input
          v-model="newPath"
          type="text"
          :placeholder="t('page.3ee4b3452967')"
          class="flex-1 input text-sm"
          @keyup.enter="addPathLimit"
        />
        <input
          v-model="newPathMax"
          type="text" inputmode="numeric"
          placeholder="100"
          class="w-20 input text-sm text-right"
          @keyup.enter="addPathLimit"
        />
        <button
          @click="addPathLimit"
          :disabled="!newPath.trim()"
          class="btn btn-sm btn-primary"
        >{{ t('common.add') }}</button>
      </div>

      <!-- 列表 -->
      <div v-if="Object.keys(pathLimits).length > 0" class="space-y-1">
        <div
          v-for="(max, path) in pathLimits"
          :key="path"
          class="flex items-center justify-between px-3 py-2 bg-dark-card-hover rounded-lg text-sm"
        >
          <code class="font-mono text-gray-300">{{ path }}</code>
          <div class="flex items-center gap-3">
            <span class="text-blue-400">{{ max }}</span>
            <button @click="removePathLimit(String(path))" class="text-xs text-red-400 hover:text-red-300">{{ t('page.6135d4159e89') }}</button>
          </div>
        </div>
      </div>
      <div v-else class="text-gray-500 text-sm text-center py-3">{{ t('page.7469456badd6') }}</div>
    </div>
  </div>
</template>
