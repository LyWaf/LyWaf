<script setup lang="ts">
import { t } from '@/i18n'
import { ref, computed, onMounted } from 'vue'
import { accessControlApi } from '@/api'
import type { AccessControlData } from '@/api'
import { useToast } from '@/composables/useToast'
import { countryNameMap, provinceNameMap } from '@/utils/geoNameMap'
import { geoLabel } from '@/utils/geoLabel'

const props = defineProps<{
  mode: 'ip' | 'geo'
}>()

const { showSuccess, showError } = useToast()

// 数据
const loading = ref(false)
const data = ref<AccessControlData | null>(null)

// 添加弹窗
const showAddDialog = ref(false)
const addType = ref<'whitelist' | 'blacklist' | 'denyCountry' | 'denyRegion' | 'allowCountry' | 'allowRegion'>('whitelist')
const addValue = ref('')
const adding = ref(false)

// 删除确认
const showDeleteConfirm = ref(false)
const deleteTarget = ref<{ type: string; value: string } | null>(null)
const deleting = ref(false)

// 标题
const title = computed(() => props.mode === 'ip' ? t('page.2316186c99ee') : t('page.70cf33d0ceb9'))

const subtitle = computed(() => {
  if (!data.value) return ''
  if (props.mode === 'ip') {
    const wc = data.value.ipControl.whitelist.length
    const bc = data.value.ipControl.blacklist.length
    return t('page.4759a411f692', { p0: wc, p1: bc, p2: formatHit(data.value.stats?.blacklistBlockCount ?? 0) })
  } else {
    const g = data.value.geoControl
    const count = g.denyCountries.length + g.denyRegions.length + g.allowCountries.length + g.allowRegions.length
    return t('page.009b80a71eb2', { p0: count, p1: data.value.geoControl.mode === 'Deny' ? t('page.b645c326ce3e') : t('page.ce7ef28b670a'), p2: formatHit(data.value.stats?.geoBlockCount ?? 0) })
  }
})

// 是否启用
const isEnabled = computed(() => {
  if (!data.value) return false
  return props.mode === 'ip' ? data.value.ipControl.enabled : data.value.geoControl.enabled
})

// 构建统一的条目列表（按模式过滤）
const entries = computed(() => {
  if (!data.value) return []
  const list: Array<{ type: string; label: string; value: string }> = []

  if (props.mode === 'ip') {
    for (const ip of data.value.ipControl.whitelist) {
      list.push({ type: 'whitelist', label: t('page.27e2d9e2d924'), value: ip })
    }
    for (const ip of data.value.ipControl.blacklist) {
      list.push({ type: 'blacklist', label: t('page.174ec5a4ab6a'), value: ip })
    }
  } else {
    for (const c of data.value.geoControl.denyCountries) {
      list.push({ type: 'denyCountry', label: t('page.284992c00c09'), value: c })
    }
    for (const r of data.value.geoControl.denyRegions) {
      list.push({ type: 'denyRegion', label: t('page.3e13bca057db'), value: r })
    }
    for (const c of data.value.geoControl.allowCountries) {
      list.push({ type: 'allowCountry', label: t('page.d898c561932a'), value: c })
    }
    for (const r of data.value.geoControl.allowRegions) {
      list.push({ type: 'allowRegion', label: t('page.aea75af799f9'), value: r })
    }
  }

  return list
})

// 添加选项
const addMenuOptions = computed(() => {
  if (props.mode === 'ip') {
    return [
      { type: 'whitelist' as const, label: t('page.2b58f787e5af') },
      { type: 'blacklist' as const, label: t('page.be6f2d46d830') },
    ]
  } else {
    return [
      { type: 'denyCountry' as const, label: t('page.284992c00c09') },
      { type: 'denyRegion' as const, label: t('page.3e13bca057db') },
      { type: 'allowCountry' as const, label: t('page.d898c561932a') },
      { type: 'allowRegion' as const, label: t('page.aea75af799f9') },
    ]
  }
})

// 加载
const loadData = async () => {
  loading.value = true
  try {
    const res = await accessControlApi.getAll() as unknown as AccessControlData
    if (res?.success) {
      data.value = res
    }
  } catch {
    showError(t('page.96cca6ab0240'))
  } finally {
    loading.value = false
  }
}

// 切换
const toggle = async () => {
  try {
    if (props.mode === 'ip') {
      const res = await accessControlApi.toggleIpControl() as unknown as { success: boolean; enabled: boolean }
      if (res?.success) {
        if (data.value) data.value.ipControl.enabled = res.enabled
        showSuccess(res.enabled ? t('page.aac51b1c8159') : t('page.cc25cbeadb83'))
      }
    } else {
      const res = await accessControlApi.toggleGeoControl() as unknown as { success: boolean; enabled: boolean }
      if (res?.success) {
        if (data.value) data.value.geoControl.enabled = res.enabled
        showSuccess(res.enabled ? t('page.98df973bb4c3') : t('page.9caf21e97293'))
      }
    }
  } catch {
    showError(t('settings.operation.failed'))
  }
}

// 打开添加弹窗
const addTypeLabels = computed<Record<string, string>>(() => ({
  whitelist: t('page.2b58f787e5af'),
  blacklist: t('page.be6f2d46d830'),
  denyCountry: t('page.284992c00c09'),
  denyRegion: t('page.3e13bca057db'),
  allowCountry: t('page.d898c561932a'),
  allowRegion: t('page.aea75af799f9'),
}))

const addTypePlaceholders = computed<Record<string, string>>(() => ({
  whitelist: t('page.2bc7a2a6d976'),
  blacklist: t('page.2bc7a2a6d976'),
  denyCountry: t('page.fa1fd2863dc1'),
  denyRegion: t('page.f5c47a5f7601'),
  allowCountry: t('page.faad22cdbcef'),
  allowRegion: t('page.9cb6ef8bd3a2'),
}))

// 地理类型预设选项
const geoSearch = ref('')
const isGeoType = computed(() => ['denyCountry', 'denyRegion', 'allowCountry', 'allowRegion'].includes(addType.value))
const isCountryType = computed(() => addType.value === 'denyCountry' || addType.value === 'allowCountry')

const geoOptions = computed(() => {
  if (isCountryType.value) return Object.keys(countryNameMap)
  return Object.keys(provinceNameMap)
})

const existingGeoValues = computed(() => {
  if (!data.value) return new Set<string>()
  const map: Record<string, string[]> = {
    denyCountry: data.value.geoControl.denyCountries,
    denyRegion: data.value.geoControl.denyRegions,
    allowCountry: data.value.geoControl.allowCountries,
    allowRegion: data.value.geoControl.allowRegions,
  }
  return new Set(map[addType.value] || [])
})

const filteredGeoOptions = computed(() => {
  const search = geoSearch.value.toLowerCase()
  const existing = existingGeoValues.value
  return geoOptions.value.filter(name =>
    !existing.has(name) && (name.toLowerCase().includes(search) || geoLabel(name).toLowerCase().includes(search))
  )
})

const openAdd = (type: typeof addType.value) => {
  addType.value = type
  addValue.value = ''
  geoSearch.value = ''
  showAddDialog.value = true
}

const executeAdd = async () => {
  if (!addValue.value.trim()) return
  adding.value = true
  try {
    const v = addValue.value.trim()
    const apiMap: Record<string, () => Promise<unknown>> = {
      whitelist: () => accessControlApi.addWhitelist(v),
      blacklist: () => accessControlApi.addBlacklist(v),
      denyCountry: () => accessControlApi.addDenyCountry(v),
      denyRegion: () => accessControlApi.addDenyRegion(v),
      allowCountry: () => accessControlApi.addAllowCountry(v),
      allowRegion: () => accessControlApi.addAllowRegion(v),
    }
    const res = await apiMap[addType.value]() as unknown as { success: boolean; message: string }
    if (res?.success) {
      showAddDialog.value = false
      loadData()
      showSuccess(res.message || t('page.ee1a3c93ab01'))
    } else {
      showError(res?.message || t('dashboard.failed.to.add'))
    }
  } catch {
    showError(t('dashboard.failed.to.add'))
  } finally {
    adding.value = false
  }
}

// 删除
const confirmDelete = (entry: { type: string; value: string }) => {
  deleteTarget.value = entry
  showDeleteConfirm.value = true
}

const executeDelete = async () => {
  if (!deleteTarget.value) return
  deleting.value = true
  try {
    const { type, value } = deleteTarget.value
    const apiMap: Record<string, () => Promise<unknown>> = {
      whitelist: () => accessControlApi.removeWhitelist(value),
      blacklist: () => accessControlApi.removeBlacklist(value),
      denyCountry: () => accessControlApi.removeDenyCountry(value),
      denyRegion: () => accessControlApi.removeDenyRegion(value),
      allowCountry: () => accessControlApi.removeAllowCountry(value),
      allowRegion: () => accessControlApi.removeAllowRegion(value),
    }
    const res = await apiMap[type]() as unknown as { success: boolean; message: string }
    if (res?.success) {
      showDeleteConfirm.value = false
      deleteTarget.value = null
      loadData()
      showSuccess(t('page.174688c755b9'))
    } else {
      showError(res?.message || t('page.4757fc1d4709'))
    }
  } catch {
    showError(t('page.4757fc1d4709'))
  } finally {
    deleting.value = false
  }
}

// 格式化命中数
const formatHit = (n: number): string => {
  if (n >= 10000) return (n / 10000).toFixed(1) + t('page.fc227df4fc94')
  if (n >= 1000) return (n / 1000).toFixed(1) + 'k'
  return n.toString()
}

// 类型徽章样式
const typeBadgeClass = (type: string): string => {
  switch (type) {
    case 'whitelist': return 'bg-green-500/15 text-green-400'
    case 'blacklist': return 'bg-red-500/15 text-red-400'
    case 'denyCountry': case 'denyRegion': return 'bg-orange-500/15 text-orange-400'
    case 'allowCountry': case 'allowRegion': return 'bg-blue-500/15 text-blue-400'
    default: return 'bg-gray-500/15 text-gray-400'
  }
}

onMounted(() => {
  loadData()
})
</script>

<template>
  <div class="card">
    <!-- 标题栏 -->
    <div class="flex items-center justify-between mb-4 pb-3 border-b border-dark-border">
      <div class="flex items-center gap-3">
        <h2 class="text-base font-semibold text-gray-100">{{ title }}</h2>
        <span class="text-xs text-gray-500">{{ subtitle }}</span>
      </div>
      <div class="flex items-center gap-3">
        <!-- 添加按钮 -->
        <div class="relative group">
          <button class="btn btn-sm btn-primary flex items-center gap-1.5">
            <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
            </svg>
            {{ t('common.add') }}
          </button>
          <div class="absolute right-0 top-full mt-1 w-40 bg-dark-card border border-dark-border rounded-lg shadow-xl opacity-0 invisible group-hover:opacity-100 group-hover:visible transition-all z-10">
            <button
              v-for="(opt, idx) in addMenuOptions"
              :key="opt.type"
              @click="openAdd(opt.type)"
              :class="[
                'w-full text-left px-3 py-2 text-sm text-gray-300 hover:bg-white/5',
                idx === 0 ? 'rounded-t-lg' : '',
                idx === addMenuOptions.length - 1 ? 'rounded-b-lg' : '',
              ]"
            >
              {{ opt.label }}
            </button>
          </div>
        </div>
        <!-- 开关 -->
        <button
          v-if="data"
          @click="toggle"
          :class="[
            'relative w-12 h-6 rounded-full transition-colors flex-shrink-0',
            isEnabled ? 'bg-primary-500' : 'bg-gray-600'
          ]"
        >
          <span
            :class="[
              'absolute top-1 w-4 h-4 rounded-full bg-white transition-transform',
              isEnabled ? 'left-7' : 'left-1'
            ]"
          />
        </button>
      </div>
    </div>

    <!-- 加载中 -->
    <div v-if="loading" class="flex items-center justify-center py-8">
      <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
    </div>

    <!-- 条目列表 -->
    <template v-else-if="data">
      <div v-if="entries.length === 0" class="text-center py-8 text-gray-500">
        <p class="text-sm">{{ t('page.b336a174cd1f') }}{{ mode === 'ip' ? t('page.2316186c99ee') : t('page.8dc9d6d1e78d') }}{{ t('fields.Rules') }}</p>
      </div>

      <div v-else class="overflow-x-auto -mx-5 -mb-5">
        <table class="w-full text-sm">
          <thead>
            <tr class="border-t border-dark-border text-left text-xs text-gray-500">
              <th class="px-5 py-2.5 w-28">{{ t('settings.type') }}</th>
              <th class="px-4 py-2.5">{{ t('common.value') }}</th>
              <th class="px-5 py-2.5 w-20">{{ t('settings.actions') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(entry, idx) in entries" :key="idx"
              class="border-t border-dark-border/50 hover:bg-white/[0.03] transition-colors">
              <td class="px-5 py-2.5">
                <span :class="['px-2 py-0.5 text-[11px] font-medium rounded-full whitespace-nowrap', typeBadgeClass(entry.type)]">
                  {{ entry.label }}
                </span>
              </td>
              <td class="px-4 py-2.5 text-gray-200 font-mono text-sm">{{ mode === 'geo' ? geoLabel(entry.value) : entry.value }}</td>
              <td class="px-5 py-2.5">
                <button @click="confirmDelete(entry)"
                  class="text-xs text-red-400 hover:text-red-300 px-2 py-1 rounded hover:bg-red-500/10 transition-colors">
                  {{ t('page.6135d4159e89') }}
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </template>
  </div>

  <!-- 添加弹窗 -->
  <Teleport to="body">
    <div v-if="showAddDialog" class="fixed inset-0 z-50 flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="showAddDialog = false"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl p-6 max-w-md w-full mx-4">
        <h3 class="text-lg font-semibold text-gray-100 mb-4">{{ t('common.add') }}{{ addTypeLabels[addType] }}</h3>
        <div class="space-y-3">
          <!-- IP 类型：文本输入 -->
          <template v-if="!isGeoType">
            <div>
              <label class="block text-sm text-gray-400 mb-1">{{ t('common.value') }} <span class="text-red-400">*</span></label>
              <input v-model="addValue" type="text" class="input w-full"
                :placeholder="addTypePlaceholders[addType]"
                @keyup.enter="executeAdd" />
            </div>
            <div class="text-xs text-gray-500">
              {{ t('page.ea70d759e386') }}
            </div>
          </template>

          <!-- 地理类型：搜索 + 下拉列表 -->
          <template v-else>
            <input
              v-model="geoSearch"
              type="text"
              class="input w-full"
              :placeholder="isCountryType ? t('page.b0f54c0ebb01') : t('page.bd5a4343bd31')"
            />
            <div class="max-h-[280px] overflow-y-auto space-y-1 border border-dark-border rounded-lg p-2">
              <div
                v-for="name in filteredGeoOptions"
                :key="name"
                @click="addValue = name"
                :class="[
                  'px-3 py-2 rounded-lg cursor-pointer transition-colors text-sm',
                  addValue === name
                    ? 'bg-primary-500/20 text-primary-400 border border-primary-500/30'
                    : 'hover:bg-dark-card-hover text-gray-300'
                ]"
              >
                {{ geoLabel(name) }}
              </div>
              <div v-if="filteredGeoOptions.length === 0" class="text-gray-500 text-sm text-center py-4">
                {{ t('page.336cba9a9241') }}
              </div>
            </div>
            <div class="flex items-center gap-2">
              <span class="text-xs text-gray-500 shrink-0">{{ t('page.8f50913a95d1') }}</span>
              <input
                v-model="addValue"
                type="text"
                class="input text-sm flex-1"
                :placeholder="isCountryType ? t('page.a1d29f2e404d') : t('page.d5520841b358')"
                @keyup.enter="executeAdd"
              />
            </div>
            <div v-if="addValue" class="text-sm text-gray-400">
              {{ t('page.99205dfa26c0') }}<span class="text-primary-400 font-medium">{{ geoLabel(addValue) }}</span>
            </div>
          </template>
        </div>
        <div class="flex justify-end gap-3 mt-5">
          <button @click="showAddDialog = false" class="btn btn-sm btn-secondary">{{ t('common.cancel') }}</button>
          <button @click="executeAdd" :disabled="adding || !addValue.trim()"
            class="btn btn-sm btn-primary flex items-center gap-1.5">
            <span v-if="adding" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
            <span>{{ adding ? t('page.0dae682eddac') : t('common.add') }}</span>
          </button>
        </div>
      </div>
    </div>
  </Teleport>

  <!-- 删除确认 -->
  <Teleport to="body">
    <div v-if="showDeleteConfirm" class="fixed inset-0 z-50 flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="showDeleteConfirm = false"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl p-6 max-w-md w-full mx-4">
        <h3 class="text-lg font-semibold text-gray-100 mb-2">{{ t('page.b24464068814') }}</h3>
        <p class="text-sm text-gray-400 mb-5">
          {{ t('page.30990449e53b') }}<span class="text-gray-200 font-mono">{{ mode === 'geo' ? geoLabel(deleteTarget?.value || '') : deleteTarget?.value }}</span>{{ t('page.342822e51a1b') }}
        </p>
        <div class="flex justify-end gap-3">
          <button @click="showDeleteConfirm = false" class="btn btn-sm btn-secondary">{{ t('common.cancel') }}</button>
          <button @click="executeDelete" :disabled="deleting"
            class="btn btn-sm bg-red-600 hover:bg-red-500 text-white flex items-center gap-1.5">
            <span v-if="deleting" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
            <span>{{ deleting ? t('page.22669d0d7959') : t('page.ee7dea070d88') }}</span>
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
