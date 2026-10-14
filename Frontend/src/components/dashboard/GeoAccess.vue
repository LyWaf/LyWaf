<script setup lang="ts">
import { t } from '@/i18n'
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
const countryOptions = computed(() => ([
  { code: 'CN', label: t('dashboard.china') },
  { code: 'US', label: t('page.58857e5318f3') },
  { code: 'JP', label: t('page.cf2abf0c5be3') },
  { code: 'KR', label: t('page.963cd7300454') },
  { code: 'GB', label: t('page.d055c3998f13') },
  { code: 'DE', label: t('page.0a7e404732db') },
  { code: 'FR', label: t('page.81678972842d') },
  { code: 'RU', label: t('page.d8fcbae05f79') },
  { code: 'IN', label: t('page.fd41ccf56290') },
  { code: 'BR', label: t('page.c733bdf60002') },
  { code: 'CA', label: t('page.6930e3f7dd59') },
  { code: 'AU', label: t('page.0b1001a02c97') },
  { code: 'SG', label: t('page.f98b2fc82a45') },
  { code: 'TW', label: t('page.c20756588291') },
  { code: 'HK', label: t('page.ce679b791a3b') },
  { code: 'MO', label: t('page.5574d1dc3ec4') },
  { code: 'TH', label: t('page.8f6ee8c64166') },
  { code: 'VN', label: t('page.193e6ef53d64') },
  { code: 'MY', label: t('page.b3320aa9c094') },
  { code: 'ID', label: t('page.027121b0528b') },
  { code: 'PH', label: t('page.f1e3c16bbd63') },
  { code: 'NL', label: t('page.8e1849a68722') },
  { code: 'IT', label: t('page.7d3566712e91') },
  { code: 'ES', label: t('page.bde5cc5b7e1a') },
  { code: 'SE', label: t('page.af02be73f1a8') },
  { code: 'CH', label: t('page.0344bcb449f5') },
  { code: 'NZ', label: t('page.14c7289f304b') },
  { code: 'ZA', label: t('page.54fc2b6c3b9b') },
  { code: 'MX', label: t('page.f5b984831b2d') },
  { code: 'AR', label: t('page.37eb14e4da69') },
  { code: 'AE', label: t('page.20e2113f5d9b') },
  { code: 'SA', label: t('page.8fa333c1964a') },
  { code: 'IL', label: t('page.294eb34fe0a1') },
  { code: 'TR', label: t('page.f60603b48764') },
  { code: 'UA', label: t('page.2929408da741') },
  { code: 'PL', label: t('page.07d67d424576') },
  { code: 'NG', label: t('page.f29d378e0e56') },
  { code: 'EG', label: t('page.75fb1f4a2a98') },
  { code: 'PK', label: t('page.0ae6df4762bc') },
  { code: 'BD', label: t('page.391c8b512df3') },
  { code: 'KP', label: t('page.8c2021079f63') },
  { code: 'IR', label: t('page.73f571f3c737') },
  { code: 'IQ', label: t('page.3635e474d230') },
]))

const regionOptions = computed(() => ([
  { code: '亚洲', label: t('page.d6b5711fea6d') },
  { code: '欧洲', label: t('page.8ef85828ff9c') },
  { code: '北美洲', label: t('page.29646cf76f9a') },
  { code: '南美洲', label: t('page.356ec8f97f02') },
  { code: '非洲', label: t('page.2d5b98b7b92a') },
  { code: '大洋洲', label: t('page.3fda2b8c1af8') },
  { code: '中东', label: t('page.079b8bfb8d2a') },
  { code: '东南亚', label: t('page.e0ded4e93dd5') },
  { code: '东亚', label: t('page.817aa4a4bba8') },
  { code: '南亚', label: t('page.fbcbb80541c9') },
  { code: '中亚', label: t('page.32738d3a482e') },
  { code: '西欧', label: t('page.fb6f767f85fe') },
  { code: '东欧', label: t('page.a3079ae6d40f') },
  { code: '北欧', label: t('page.bf794fd47005') },
]))

// 国家代码 → 显示名称
const countryLabel = (code: string) => {
  const found = countryOptions.value.find(c => c.code === code)
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
    allowCountry: t('page.3041aef25775'),
    allowRegion: t('page.b600b6e35259'),
    denyCountry: t('page.d8db90f8fe7c'),
    denyRegion: t('page.c99ffec4f27d'),
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
  const options = isCountryDialog.value ? countryOptions.value : regionOptions.value
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
    showError(t('page.ec5702d51a4c'))
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
      showSuccess(t('page.0231cac2f1f5', { p0: isCountryDialog.value ? countryLabel(value) : value }))
      showDialog.value = false
    } else {
      showError((res as any).message || t('dashboard.failed.to.add'))
    }
  } catch {
    showError(t('dashboard.failed.to.add'))
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
      showSuccess(t('page.837f3bbff7f9', { p0: kind.includes('Country') ? countryLabel(value) : value }))
    }
  } catch {
    showError(t('page.4757fc1d4709'))
  } finally {
    showDeleteConfirm.value = false
    deleteInfo.value = null
  }
}
</script>

<template>
  <Section id="geo-access" :title="t('page.d50b801e1094')">
    <div class="grid grid-cols-1 lg:grid-cols-2 gap-6">
      <!-- 允许列表 -->
      <div class="space-y-4">
        <h3 class="text-green-400 font-medium">{{ t('page.54536b96aee9') }}</h3>

        <!-- 允许国家 -->
        <div class="p-4 bg-dark-card-hover rounded-lg">
          <div class="flex items-center justify-between mb-3">
            <span class="text-gray-400 text-sm">{{ t('page.d898c561932a') }}</span>
            <button @click="openDialog('allowCountry')" class="btn btn-sm btn-primary">{{ t('page.42bb5c118d25') }}</button>
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
            <span v-if="allowCountries.length === 0" class="text-gray-500 text-sm">{{ t('ui.none') }}</span>
          </div>
        </div>

        <!-- 允许地区 -->
        <div class="p-4 bg-dark-card-hover rounded-lg">
          <div class="flex items-center justify-between mb-3">
            <span class="text-gray-400 text-sm">{{ t('page.52dbcdffcb70') }}</span>
            <button @click="openDialog('allowRegion')" class="btn btn-sm btn-primary">{{ t('page.42bb5c118d25') }}</button>
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
            <span v-if="allowRegions.length === 0" class="text-gray-500 text-sm">{{ t('ui.none') }}</span>
          </div>
        </div>
      </div>

      <!-- 禁止列表 -->
      <div class="space-y-4">
        <h3 class="text-red-400 font-medium">{{ t('api.forbidden') }}</h3>

        <!-- 禁止国家 -->
        <div class="p-4 bg-dark-card-hover rounded-lg">
          <div class="flex items-center justify-between mb-3">
            <span class="text-gray-400 text-sm">{{ t('page.284992c00c09') }}</span>
            <button @click="openDialog('denyCountry')" class="btn btn-sm btn-primary">{{ t('page.42bb5c118d25') }}</button>
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
            <span v-if="denyCountries.length === 0" class="text-gray-500 text-sm">{{ t('ui.none') }}</span>
          </div>
        </div>

        <!-- 禁止地区 -->
        <div class="p-4 bg-dark-card-hover rounded-lg">
          <div class="flex items-center justify-between mb-3">
            <span class="text-gray-400 text-sm">{{ t('page.a1e50150690a') }}</span>
            <button @click="openDialog('denyRegion')" class="btn btn-sm btn-primary">{{ t('page.42bb5c118d25') }}</button>
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
            <span v-if="denyRegions.length === 0" class="text-gray-500 text-sm">{{ t('ui.none') }}</span>
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
            :placeholder="isCountryDialog ? t('page.e64a53eac1a9') : t('page.a7b32afebbe1')"
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
              {{ t('page.336cba9a9241') }}
            </div>
          </div>

          <!-- 手动输入 -->
          <div class="flex items-center gap-2">
            <span class="text-xs text-gray-500 shrink-0">{{ t('page.8f50913a95d1') }}</span>
            <input
              v-model="dialogValue"
              type="text"
              class="input text-sm"
              :placeholder="isCountryDialog ? t('page.73d64d2571b4') : t('page.06a411543dee')"
            />
          </div>

          <div v-if="dialogValue" class="text-sm text-gray-400">
            {{ t('page.99205dfa26c0') }}<span class="text-primary-400 font-medium">{{ isCountryDialog ? countryLabel(dialogValue) : dialogValue }}</span>
          </div>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="showDialog = false" class="btn btn-secondary">{{ t('common.cancel') }}</button>
          <button @click="submitAdd" :disabled="dialogLoading || !dialogValue" class="btn btn-primary">
            {{ dialogLoading ? t('settings.submitting') : t('page.1945909fd7f7') }}
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
          <h3 class="text-lg font-semibold text-gray-100 mb-2">{{ t('page.ee7dea070d88') }}</h3>
          <p class="text-gray-400">
            {{ t('page.37251437194a') }}
            <span class="text-red-400 font-medium">
              {{ deleteInfo?.kind.includes('Country') ? countryLabel(deleteInfo?.value || '') : deleteInfo?.value }}
            </span>
            {{ t('page.9d45d8943988') }}
          </p>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="showDeleteConfirm = false" class="btn btn-secondary">{{ t('common.cancel') }}</button>
          <button @click="doRemove" class="btn btn-danger">{{ t('page.4948800ce892') }}</button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
