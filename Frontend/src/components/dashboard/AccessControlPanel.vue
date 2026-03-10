<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { accessControlApi } from '@/api'
import type { AccessControlData } from '@/api'
import { useToast } from '@/composables/useToast'

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
const title = computed(() => props.mode === 'ip' ? 'IP 访问控制' : '地理位置访问控制')

const subtitle = computed(() => {
  if (!data.value) return ''
  if (props.mode === 'ip') {
    const wc = data.value.ipControl.whitelist.length
    const bc = data.value.ipControl.blacklist.length
    return `白名单 ${wc} / 黑名单 ${bc} / 累计拦截 ${formatHit(data.value.stats?.blacklistBlockCount ?? 0)}`
  } else {
    const g = data.value.geoControl
    const count = g.denyCountries.length + g.denyRegions.length + g.allowCountries.length + g.allowRegions.length
    return `地理规则 ${count} / 模式: ${data.value.geoControl.mode === 'Deny' ? '禁止' : '允许'} / 累计拦截 ${formatHit(data.value.stats?.geoBlockCount ?? 0)}`
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
      list.push({ type: 'whitelist', label: '白名单', value: ip })
    }
    for (const ip of data.value.ipControl.blacklist) {
      list.push({ type: 'blacklist', label: '黑名单', value: ip })
    }
  } else {
    for (const c of data.value.geoControl.denyCountries) {
      list.push({ type: 'denyCountry', label: '禁止国家', value: c })
    }
    for (const r of data.value.geoControl.denyRegions) {
      list.push({ type: 'denyRegion', label: '禁止省份', value: r })
    }
    for (const c of data.value.geoControl.allowCountries) {
      list.push({ type: 'allowCountry', label: '允许国家', value: c })
    }
    for (const r of data.value.geoControl.allowRegions) {
      list.push({ type: 'allowRegion', label: '允许省份', value: r })
    }
  }

  return list
})

// 添加选项
const addMenuOptions = computed(() => {
  if (props.mode === 'ip') {
    return [
      { type: 'whitelist' as const, label: 'IP 白名单' },
      { type: 'blacklist' as const, label: 'IP 黑名单' },
    ]
  } else {
    return [
      { type: 'denyCountry' as const, label: '禁止国家' },
      { type: 'denyRegion' as const, label: '禁止省份' },
      { type: 'allowCountry' as const, label: '允许国家' },
      { type: 'allowRegion' as const, label: '允许省份' },
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
    showError('加载访问控制数据失败')
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
        showSuccess(res.enabled ? 'IP 访问控制已启用' : 'IP 访问控制已禁用')
      }
    } else {
      const res = await accessControlApi.toggleGeoControl() as unknown as { success: boolean; enabled: boolean }
      if (res?.success) {
        if (data.value) data.value.geoControl.enabled = res.enabled
        showSuccess(res.enabled ? '地理位置访问控制已启用' : '地理位置访问控制已禁用')
      }
    }
  } catch {
    showError('操作失败')
  }
}

// 打开添加弹窗
const addTypeLabels: Record<string, string> = {
  whitelist: 'IP 白名单',
  blacklist: 'IP 黑名单',
  denyCountry: '禁止国家',
  denyRegion: '禁止省份',
  allowCountry: '允许国家',
  allowRegion: '允许省份',
}

const addTypePlaceholders: Record<string, string> = {
  whitelist: '192.168.1.0/24 或 10.0.0.1',
  blacklist: '192.168.1.0/24 或 10.0.0.1',
  denyCountry: '如：美国',
  denyRegion: '如：广东省',
  allowCountry: '如：中国',
  allowRegion: '如：北京',
}

const openAdd = (type: typeof addType.value) => {
  addType.value = type
  addValue.value = ''
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
      showSuccess(res.message || '添加成功')
    } else {
      showError(res?.message || '添加失败')
    }
  } catch {
    showError('添加失败')
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
      showSuccess('已移除')
    } else {
      showError(res?.message || '移除失败')
    }
  } catch {
    showError('移除失败')
  } finally {
    deleting.value = false
  }
}

// 格式化命中数
const formatHit = (n: number): string => {
  if (n >= 10000) return (n / 10000).toFixed(1) + '万'
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
            添加
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
        <p class="text-sm">暂无{{ mode === 'ip' ? 'IP 访问控制' : '地理位置' }}规则</p>
      </div>

      <div v-else class="overflow-x-auto -mx-5 -mb-5">
        <table class="w-full text-sm">
          <thead>
            <tr class="border-t border-dark-border text-left text-xs text-gray-500">
              <th class="px-5 py-2.5 w-24">类型</th>
              <th class="px-4 py-2.5">值</th>
              <th class="px-5 py-2.5 w-20">操作</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(entry, idx) in entries" :key="idx"
              class="border-t border-dark-border/50 hover:bg-white/[0.03] transition-colors">
              <td class="px-5 py-2.5">
                <span :class="['px-2 py-0.5 text-[11px] font-medium rounded-full', typeBadgeClass(entry.type)]">
                  {{ entry.label }}
                </span>
              </td>
              <td class="px-4 py-2.5 text-gray-200 font-mono text-sm">{{ entry.value }}</td>
              <td class="px-5 py-2.5">
                <button @click="confirmDelete(entry)"
                  class="text-xs text-red-400 hover:text-red-300 px-2 py-1 rounded hover:bg-red-500/10 transition-colors">
                  移除
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
        <h3 class="text-lg font-semibold text-gray-100 mb-4">添加{{ addTypeLabels[addType] }}</h3>
        <div class="space-y-3">
          <div>
            <label class="block text-sm text-gray-400 mb-1">值 <span class="text-red-400">*</span></label>
            <input v-model="addValue" type="text" class="input w-full"
              :placeholder="addTypePlaceholders[addType]"
              @keyup.enter="executeAdd" />
          </div>
          <div class="text-xs text-gray-500">
            <template v-if="addType === 'whitelist' || addType === 'blacklist'">
              支持单个 IP 或 CIDR 格式（如 192.168.1.0/24）
            </template>
            <template v-else>
              输入国家或省份名称，需与 IP2Region 数据库中的名称一致
            </template>
          </div>
        </div>
        <div class="flex justify-end gap-3 mt-5">
          <button @click="showAddDialog = false" class="btn btn-sm btn-secondary">取消</button>
          <button @click="executeAdd" :disabled="adding || !addValue.trim()"
            class="btn btn-sm btn-primary flex items-center gap-1.5">
            <span v-if="adding" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
            <span>{{ adding ? '添加中...' : '添加' }}</span>
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
        <h3 class="text-lg font-semibold text-gray-100 mb-2">移除规则</h3>
        <p class="text-sm text-gray-400 mb-5">
          确定要移除「<span class="text-gray-200 font-mono">{{ deleteTarget?.value }}</span>」吗？
        </p>
        <div class="flex justify-end gap-3">
          <button @click="showDeleteConfirm = false" class="btn btn-sm btn-secondary">取消</button>
          <button @click="executeDelete" :disabled="deleting"
            class="btn btn-sm bg-red-600 hover:bg-red-500 text-white flex items-center gap-1.5">
            <span v-if="deleting" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
            <span>{{ deleting ? '移除中...' : '确认移除' }}</span>
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
