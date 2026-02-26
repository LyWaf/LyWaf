<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import Section from '@/components/common/Section.vue'
import { geoApi, dashboardApi } from '@/api'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()

const allowCountries = ref<string[]>([])
const allowRegions = ref<string[]>([])
const denyCountries = ref<string[]>([])
const denyRegions = ref<string[]>([])

onMounted(async () => {
  try {
    const data = await dashboardApi.getData()
    if (data.success && data.geoAccess) {
      allowCountries.value = data.geoAccess.allowCountries || []
      allowRegions.value = data.geoAccess.allowRegions || []
      denyCountries.value = data.geoAccess.denyCountries || []
      denyRegions.value = data.geoAccess.denyRegions || []
    }
  } catch { /* 静默处理 */ }
})

// ========== 国家/地区预设选项 ==========
const countryOptions = [
  { code: 'CN', label: '中国' },
  { code: 'US', label: '美国' },
  { code: 'JP', label: '日本' },
  { code: 'KR', label: '韩国' },
  { code: 'GB', label: '英国' },
  { code: 'DE', label: '德国' },
  { code: 'FR', label: '法国' },
  { code: 'RU', label: '俄罗斯' },
  { code: 'IN', label: '印度' },
  { code: 'BR', label: '巴西' },
  { code: 'CA', label: '加拿大' },
  { code: 'AU', label: '澳大利亚' },
  { code: 'SG', label: '新加坡' },
  { code: 'TW', label: '中国台湾' },
  { code: 'HK', label: '中国香港' },
  { code: 'MO', label: '中国澳门' },
  { code: 'TH', label: '泰国' },
  { code: 'VN', label: '越南' },
  { code: 'MY', label: '马来西亚' },
  { code: 'ID', label: '印度尼西亚' },
  { code: 'PH', label: '菲律宾' },
  { code: 'NL', label: '荷兰' },
  { code: 'IT', label: '意大利' },
  { code: 'ES', label: '西班牙' },
  { code: 'SE', label: '瑞典' },
  { code: 'CH', label: '瑞士' },
  { code: 'NZ', label: '新西兰' },
  { code: 'ZA', label: '南非' },
  { code: 'MX', label: '墨西哥' },
  { code: 'AR', label: '阿根廷' },
  { code: 'AE', label: '阿联酋' },
  { code: 'SA', label: '沙特阿拉伯' },
  { code: 'IL', label: '以色列' },
  { code: 'TR', label: '土耳其' },
  { code: 'UA', label: '乌克兰' },
  { code: 'PL', label: '波兰' },
  { code: 'NG', label: '尼日利亚' },
  { code: 'EG', label: '埃及' },
  { code: 'PK', label: '巴基斯坦' },
  { code: 'BD', label: '孟加拉国' },
  { code: 'KP', label: '朝鲜' },
  { code: 'IR', label: '伊朗' },
  { code: 'IQ', label: '伊拉克' },
]

const regionOptions = [
  { code: '亚洲', label: '亚洲' },
  { code: '欧洲', label: '欧洲' },
  { code: '北美洲', label: '北美洲' },
  { code: '南美洲', label: '南美洲' },
  { code: '非洲', label: '非洲' },
  { code: '大洋洲', label: '大洋洲' },
  { code: '中东', label: '中东' },
  { code: '东南亚', label: '东南亚' },
  { code: '东亚', label: '东亚' },
  { code: '南亚', label: '南亚' },
  { code: '中亚', label: '中亚' },
  { code: '西欧', label: '西欧' },
  { code: '东欧', label: '东欧' },
  { code: '北欧', label: '北欧' },
]

// 国家代码 → 显示名称
const countryLabel = (code: string) => {
  const found = countryOptions.find(c => c.code === code)
  return found ? `${found.label} (${code})` : code
}

// ========== 弹窗状态 ==========
type DialogKind = 'allowCountry' | 'allowRegion' | 'denyCountry' | 'denyRegion'

const showDialog = ref(false)
const dialogKind = ref<DialogKind>('allowCountry')
const dialogValue = ref('')
const dialogSearch = ref('')
const dialogLoading = ref(false)

const dialogTitle = computed(() => {
  const map: Record<DialogKind, string> = {
    allowCountry: '添加允许国家',
    allowRegion: '添加允许地区',
    denyCountry: '添加禁止国家',
    denyRegion: '添加禁止地区',
  }
  return map[dialogKind.value]
})

const isCountryDialog = computed(() => dialogKind.value === 'allowCountry' || dialogKind.value === 'denyCountry')

// 已存在的列表（用于过滤已添加的选项）
const existingList = computed(() => {
  const map: Record<DialogKind, string[]> = {
    allowCountry: allowCountries.value,
    allowRegion: allowRegions.value,
    denyCountry: denyCountries.value,
    denyRegion: denyRegions.value,
  }
  return map[dialogKind.value]
})

// 过滤后的可选项
const filteredOptions = computed(() => {
  const options = isCountryDialog.value ? countryOptions : regionOptions
  const existing = new Set(existingList.value)
  const search = dialogSearch.value.toLowerCase()
  return options.filter(o =>
    !existing.has(o.code) &&
    (o.code.toLowerCase().includes(search) || o.label.toLowerCase().includes(search))
  )
})

const openDialog = (kind: DialogKind) => {
  dialogKind.value = kind
  dialogValue.value = ''
  dialogSearch.value = ''
  showDialog.value = true
}

const selectOption = (code: string) => {
  dialogValue.value = code
}

const submitAdd = async () => {
  const value = dialogValue.value.trim()
  if (!value) {
    showError('请选择或输入一项')
    return
  }

  dialogLoading.value = true
  try {
    const apiMap: Record<DialogKind, (v: string) => Promise<any>> = {
      allowCountry: geoApi.addAllowCountry,
      allowRegion: geoApi.addAllowRegion,
      denyCountry: geoApi.addDenyCountry,
      denyRegion: geoApi.addDenyRegion,
    }
    const listMap: Record<DialogKind, typeof allowCountries> = {
      allowCountry: allowCountries,
      allowRegion: allowRegions,
      denyCountry: denyCountries,
      denyRegion: denyRegions,
    }

    const res = await apiMap[dialogKind.value](value)
    if (res.success) {
      listMap[dialogKind.value].value.push(value)
      showSuccess(`已添加: ${isCountryDialog.value ? countryLabel(value) : value}`)
      showDialog.value = false
    } else {
      showError((res as any).message || '添加失败')
    }
  } catch {
    showError('添加失败')
  } finally {
    dialogLoading.value = false
  }
}

// ========== 删除确认弹窗 ==========
const showDeleteConfirm = ref(false)
const deleteInfo = ref<{ value: string; kind: DialogKind } | null>(null)

const confirmRemove = (value: string, kind: DialogKind) => {
  deleteInfo.value = { value, kind }
  showDeleteConfirm.value = true
}

const doRemove = async () => {
  if (!deleteInfo.value) return
  const { value, kind } = deleteInfo.value
  try {
    const apiMap: Record<DialogKind, (v: string) => Promise<any>> = {
      allowCountry: geoApi.removeAllowCountry,
      allowRegion: geoApi.removeAllowRegion,
      denyCountry: geoApi.removeDenyCountry,
      denyRegion: geoApi.removeDenyRegion,
    }
    const listMap: Record<DialogKind, typeof allowCountries> = {
      allowCountry: allowCountries,
      allowRegion: allowRegions,
      denyCountry: denyCountries,
      denyRegion: denyRegions,
    }

    const res = await apiMap[kind](value)
    if (res.success) {
      listMap[kind].value = listMap[kind].value.filter(i => i !== value)
      showSuccess(`已移除: ${kind.includes('Country') ? countryLabel(value) : value}`)
    }
  } catch {
    showError('移除失败')
  } finally {
    showDeleteConfirm.value = false
    deleteInfo.value = null
  }
}
</script>

<template>
  <Section id="geo-access" title="地理访问控制">
    <div class="grid grid-cols-1 lg:grid-cols-2 gap-6">
      <!-- 允许列表 -->
      <div class="space-y-4">
        <h3 class="text-green-400 font-medium">允许访问</h3>

        <!-- 允许国家 -->
        <div class="p-4 bg-dark-card-hover rounded-lg">
          <div class="flex items-center justify-between mb-3">
            <span class="text-gray-400 text-sm">允许国家</span>
            <button @click="openDialog('allowCountry')" class="btn btn-sm btn-primary">+ 添加</button>
          </div>
          <div class="flex flex-wrap gap-2">
            <span
              v-for="item in allowCountries"
              :key="item"
              class="inline-flex items-center gap-1 px-2 py-1 bg-green-500/20 text-green-400 rounded text-sm"
            >
              {{ countryLabel(item) }}
              <button @click="confirmRemove(item, 'allowCountry')" class="hover:text-red-400">×</button>
            </span>
            <span v-if="allowCountries.length === 0" class="text-gray-500 text-sm">无</span>
          </div>
        </div>

        <!-- 允许地区 -->
        <div class="p-4 bg-dark-card-hover rounded-lg">
          <div class="flex items-center justify-between mb-3">
            <span class="text-gray-400 text-sm">允许地区</span>
            <button @click="openDialog('allowRegion')" class="btn btn-sm btn-primary">+ 添加</button>
          </div>
          <div class="flex flex-wrap gap-2">
            <span
              v-for="item in allowRegions"
              :key="item"
              class="inline-flex items-center gap-1 px-2 py-1 bg-green-500/20 text-green-400 rounded text-sm"
            >
              {{ item }}
              <button @click="confirmRemove(item, 'allowRegion')" class="hover:text-red-400">×</button>
            </span>
            <span v-if="allowRegions.length === 0" class="text-gray-500 text-sm">无</span>
          </div>
        </div>
      </div>

      <!-- 禁止列表 -->
      <div class="space-y-4">
        <h3 class="text-red-400 font-medium">禁止访问</h3>

        <!-- 禁止国家 -->
        <div class="p-4 bg-dark-card-hover rounded-lg">
          <div class="flex items-center justify-between mb-3">
            <span class="text-gray-400 text-sm">禁止国家</span>
            <button @click="openDialog('denyCountry')" class="btn btn-sm btn-primary">+ 添加</button>
          </div>
          <div class="flex flex-wrap gap-2">
            <span
              v-for="item in denyCountries"
              :key="item"
              class="inline-flex items-center gap-1 px-2 py-1 bg-red-500/20 text-red-400 rounded text-sm"
            >
              {{ countryLabel(item) }}
              <button @click="confirmRemove(item, 'denyCountry')" class="hover:text-white">×</button>
            </span>
            <span v-if="denyCountries.length === 0" class="text-gray-500 text-sm">无</span>
          </div>
        </div>

        <!-- 禁止地区 -->
        <div class="p-4 bg-dark-card-hover rounded-lg">
          <div class="flex items-center justify-between mb-3">
            <span class="text-gray-400 text-sm">禁止地区</span>
            <button @click="openDialog('denyRegion')" class="btn btn-sm btn-primary">+ 添加</button>
          </div>
          <div class="flex flex-wrap gap-2">
            <span
              v-for="item in denyRegions"
              :key="item"
              class="inline-flex items-center gap-1 px-2 py-1 bg-red-500/20 text-red-400 rounded text-sm"
            >
              {{ item }}
              <button @click="confirmRemove(item, 'denyRegion')" class="hover:text-white">×</button>
            </span>
            <span v-if="denyRegions.length === 0" class="text-gray-500 text-sm">无</span>
          </div>
        </div>
      </div>
    </div>
  </Section>

  <!-- 添加国家/地区弹窗 -->
  <Teleport to="body">
    <div v-if="showDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="showDialog = false"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-[460px] max-w-[90vw]">
        <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border">
          <h3 class="text-lg font-semibold text-gray-100">{{ dialogTitle }}</h3>
          <button @click="showDialog = false" class="text-gray-400 hover:text-gray-200 text-xl leading-none">&times;</button>
        </div>
        <div class="px-6 py-5 space-y-3">
          <!-- 搜索框 -->
          <input
            v-model="dialogSearch"
            type="text"
            class="input"
            :placeholder="isCountryDialog ? '搜索国家名称或代码...' : '搜索地区名称...'"
          />

          <!-- 选项列表 -->
          <div class="max-h-[240px] overflow-y-auto space-y-1 border border-dark-border rounded-lg p-2">
            <div
              v-for="opt in filteredOptions"
              :key="opt.code"
              @click="selectOption(opt.code)"
              :class="[
                'flex items-center justify-between px-3 py-2 rounded-lg cursor-pointer transition-colors text-sm',
                dialogValue === opt.code
                  ? 'bg-primary-500/20 text-primary-400 border border-primary-500/30'
                  : 'hover:bg-dark-card-hover text-gray-300'
              ]"
            >
              <span>{{ opt.label }}</span>
              <span class="text-gray-500 text-xs font-mono">{{ opt.code }}</span>
            </div>
            <div v-if="filteredOptions.length === 0" class="text-gray-500 text-sm text-center py-4">
              无匹配项
            </div>
          </div>

          <!-- 手动输入 -->
          <div class="flex items-center gap-2">
            <span class="text-xs text-gray-500 shrink-0">或手动输入：</span>
            <input
              v-model="dialogValue"
              type="text"
              class="input text-sm"
              :placeholder="isCountryDialog ? '国家代码，如 CN' : '地区名称'"
            />
          </div>

          <div v-if="dialogValue" class="text-sm text-gray-400">
            当前选择：<span class="text-primary-400 font-medium">{{ isCountryDialog ? countryLabel(dialogValue) : dialogValue }}</span>
          </div>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="showDialog = false" class="btn btn-secondary">取消</button>
          <button @click="submitAdd" :disabled="dialogLoading || !dialogValue" class="btn btn-primary">
            {{ dialogLoading ? '提交中...' : '确定添加' }}
          </button>
        </div>
      </div>
    </div>
  </Teleport>

  <!-- 删除确认弹窗 -->
  <Teleport to="body">
    <div v-if="showDeleteConfirm" class="fixed inset-0 z-[100] flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="showDeleteConfirm = false"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-[400px] max-w-[90vw]">
        <div class="px-6 py-5">
          <h3 class="text-lg font-semibold text-gray-100 mb-2">确认移除</h3>
          <p class="text-gray-400">
            确定要移除
            <span class="text-red-400 font-medium">
              {{ deleteInfo?.kind.includes('Country') ? countryLabel(deleteInfo?.value || '') : deleteInfo?.value }}
            </span>
            吗？
          </p>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="showDeleteConfirm = false" class="btn btn-secondary">取消</button>
          <button @click="doRemove" class="btn btn-danger">确定移除</button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
