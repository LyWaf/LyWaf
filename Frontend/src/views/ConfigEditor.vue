<script setup lang="ts">
import { locale } from '@/i18n'
import { t } from '@/i18n'
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { configFileApi, serviceApi, patchApi, upgradeApi, type ConfigVersionInfo, type ConfigVersionDiff, type UpgradeReleaseInfo, type UpgradePointers } from '@/api'
import { useToast } from '@/composables/useToast'
import ListToolbar from '@/components/common/ListToolbar.vue'

const { showSuccess, showError } = useToast()
const router = useRouter()

// 状态
const loading = ref(false)
const saving = ref(false)
const converting = ref(false)
const fileName = ref('')
const format = ref('ly')
const content = ref('')                // 编辑区域内容（加载时取草稿优先）
const originalContent = ref('')        // 原始配置文件内容（不可变）
const lastSavedContent = ref('')       // 上次保存/加载时的内容
const yamlPreview = ref('')
const activeTab = ref<'source' | 'yaml'>('source')
const hasDraft = ref(false)
const historyLoading = ref(false)
const versions = ref<ConfigVersionInfo[]>([])
const historyPage = ref(1)
const historySearch = ref('')
const historyTotal = ref(0)
let historyGeneration = 0
const selectedDiff = ref<ConfigVersionDiff | null>(null)
const diffLoading = ref(false)
const rollbackTarget = ref<ConfigVersionInfo | null>(null)
const rollingBack = ref(false)
const upgradeLoading = ref(false)
const upgradePackageName = ref('')
const upgradeVersion = ref('')
const upgradeSha256 = ref('')
const upgradeReleases = ref<UpgradeReleaseInfo[]>([])
const upgradePage = ref(1)
const upgradeSearch = ref('')
const upgradeTotal = ref(0)
let upgradeRequest = 0
const upgradePointers = ref<UpgradePointers>({ current: null, previous: null, pending: null })
const activateTarget = ref<UpgradeReleaseInfo | null>(null)
const activatingUpgrade = ref(false)

const isLyFormat = computed(() => format.value === 'ly')
const hasChanges = computed(() => content.value !== lastSavedContent.value)
const isDifferentFromOriginal = computed(() => content.value !== originalContent.value)

const formatTime = (value: string) => new Date(value).toLocaleString(locale.value)
const shortHash = (value: string) => value.slice(0, 12)

const loadHistory = async () => {
  const generation = ++historyGeneration
  historyLoading.value = true
  try {
    const result = await configFileApi.getHistory(historyPage.value, historySearch.value)
    if (generation !== historyGeneration) return
    versions.value = result.data
    historyTotal.value = result.total
  } catch (error: any) {
    showError(error?.response?.data?.message || t('page.cb0d6aac9351'))
  } finally {
    if (generation === historyGeneration) historyLoading.value = false
  }
}

const viewDiff = async (version: ConfigVersionInfo) => {
  diffLoading.value = true
  try {
    selectedDiff.value = (await configFileApi.getVersionDiff(version.id)).diff
  } catch (error: any) {
    showError(error?.response?.data?.message || t('page.4c77b9828818'))
  } finally {
    diffLoading.value = false
  }
}

const confirmRollback = async () => {
  if (!rollbackTarget.value) return
  rollingBack.value = true
  try {
    const result = await configFileApi.rollback(rollbackTarget.value.id)
    rollbackTarget.value = null
    selectedDiff.value = null
    showSuccess(t('page.84ed9ff33b90'))
    await router.push({ path: '/management-tasks', query: { task: result.data.id } })
  } catch (error: any) {
    showError(error?.response?.data?.message || t('page.3249318fe005'))
  } finally {
    rollingBack.value = false
  }
}

const loadUpgrades = async () => {
  const request = ++upgradeRequest
  upgradeLoading.value = true
  try {
    const response = await upgradeApi.list(upgradePage.value, upgradeSearch.value)
    const pointers = await upgradeApi.pointers()
    if (request !== upgradeRequest) return
    upgradeReleases.value = response.data
    upgradeTotal.value = response.total
    upgradePointers.value = pointers
  } catch (error: any) {
    showError(error?.response?.data?.message || t('page.10c2e0b99f22'))
  } finally { if (request === upgradeRequest) upgradeLoading.value = false }
}

const stageUpgrade = async () => {
  if (!upgradePackageName.value || !upgradeVersion.value || !/^[a-fA-F0-9]{64}$/.test(upgradeSha256.value)) {
    showError(t('page.ca893740e01a'))
    return
  }
  upgradeLoading.value = true
  try {
    const response = await upgradeApi.stage(upgradePackageName.value, upgradeVersion.value, upgradeSha256.value)
    showSuccess(t('page.8ca94a7faa13'))
    upgradePackageName.value = ''; upgradeVersion.value = ''; upgradeSha256.value = ''
    await router.push({ path: '/management-tasks', query: { task: response.data.id } })
  } catch (error: any) {
    showError(error?.response?.data?.message || t('page.fc881889c5c7'))
  } finally { upgradeLoading.value = false }
}

const confirmActivateUpgrade = async () => {
  if (!activateTarget.value) return
  activatingUpgrade.value = true
  try {
    const result = await upgradeApi.activate(activateTarget.value.version)
    showSuccess(t('page.b9f7824ebb1c'))
    activateTarget.value = null
    await router.push({ path: '/management-tasks', query: { task: result.data.id } })
  } catch (error: any) {
    showError(error?.response?.data?.message || t('page.2907075e503b'))
  } finally { activatingUpgrade.value = false }
}

const rollbackProgram = async () => {
  activatingUpgrade.value = true
  try {
    const result = await upgradeApi.rollback()
    showSuccess(t('page.af84fc87b535'))
    await router.push({ path: '/management-tasks', query: { task: result.data.id } })
  } catch (error: any) {
    showError(error?.response?.data?.message || t('page.db5614e6d4e1'))
  } finally { activatingUpgrade.value = false }
}

// 加载配置文件
const loadFile = async () => {
  loading.value = true
  try {
    const response = await configFileApi.getFile()
    if (response.success) {
      fileName.value = response.fileName
      format.value = response.format
      originalContent.value = response.content
      hasDraft.value = response.hasDraft

      // 当前运行的是草稿配置时，默认显示草稿内容
      if (response.draftLoaded && response.draftContent) {
        content.value = response.draftContent
        lastSavedContent.value = response.draftContent
      } else {
        content.value = response.content
        lastSavedContent.value = response.content
      }
      yamlPreview.value = ''
    }
  } catch (error: any) {
    showError(error?.response?.data?.message || t('page.378051442206'))
  } finally {
    loading.value = false
  }
}

// 恢复为原始文件内容
const restoreOriginal = () => {
  content.value = originalContent.value
  showSuccess(t('page.be1f589136b7'))
}

// 转换为 YAML 预览
const convertToYaml = async () => {
  if (!isLyFormat.value) return
  converting.value = true
  try {
    const response = await configFileApi.convert(content.value)
    if (response.success) {
      yamlPreview.value = response.yaml
      activeTab.value = 'yaml'
    } else {
      showError(response.message || t('page.0567fe6493c4'))
    }
  } catch (error: any) {
    showError(error?.response?.data?.message || t('page.0567fe6493c4'))
  } finally {
    converting.value = false
  }
}

// 保存配置（保存到草稿文件，不覆盖原始配置）
const saveConfig = async (reload: boolean = false) => {
  saving.value = true
  try {
    const response = await configFileApi.saveFile(content.value, reload)
    if (reload && response.data) {
      showSuccess(t('page.3c4d98cf95b5'))
      await router.push({ path: '/management-tasks', query: { task: response.data.id } })
      return
    }
    if (response.success) {
      lastSavedContent.value = content.value
      hasDraft.value = true
      showSuccess(response.message || t('page.ed6341cbd511'))

      // 检测到端口变更，询问用户是否重启
      if (response.restartRequired || response.portsChanged) {
        showRestartConfirm.value = true
      }
    } else {
      showError(response.message || t('ui.save.failed'))
    }
  } catch (error: any) {
    showError(error?.response?.data?.message || t('ui.save.failed'))
  } finally {
    saving.value = false
  }
}

// 重启确认
const showRestartConfirm = ref(false)
const restarting = ref(false)

const confirmRestart = async () => {
  restarting.value = true
  try {
    await serviceApi.restart()
    showSuccess(t('page.8d89dd9927fb'))
    showRestartConfirm.value = false
  } catch {
    showError(t('page.8b206a8f786b'))
  } finally {
    restarting.value = false
  }
}

// 重置为上次保存的内容
const resetContent = () => {
  content.value = lastSavedContent.value
  showSuccess(t('page.60066bedd540'))
}

// Tab 切换
const switchTab = (tab: 'source' | 'yaml') => {
  if (tab === 'yaml' && isLyFormat.value) {
    convertToYaml()
  } else {
    activeTab.value = tab
  }
}

// 快捷键
const handleKeydown = (e: KeyboardEvent) => {
  if ((e.ctrlKey || e.metaKey) && e.key === 's') {
    e.preventDefault()
    saveConfig(false)
    return
  }

  // Tab 插入4个空格（使用 execCommand 保留浏览器原生 undo 支持）
  if (e.key === 'Tab') {
    e.preventDefault()
    document.execCommand('insertText', false, '    ')
  }
}

// 清除所有补丁
const showClearPatchConfirm = ref(false)
const clearingPatch = ref(false)

const confirmClearPatch = async () => {
  clearingPatch.value = true
  try {
    const res = await patchApi.clearAll() as unknown as { success: boolean; message?: string }
    if (res?.success) {
      showSuccess(t('page.3d8e71dce4f8'))
      showClearPatchConfirm.value = false
    } else {
      showError(res?.message || t('page.2a8a17c1094d'))
    }
  } catch {
    showError(t('page.0c03fa8cde43'))
  } finally {
    clearingPatch.value = false
  }
}

onMounted(() => {
  loadFile()
  loadHistory()
  loadUpgrades()
})
</script>

<template>
  <div class="space-y-4">
    <!-- 顶部标题栏 -->
    <div class="card">
      <div class="flex items-center justify-between">
        <div class="flex items-center gap-3">
          <h2 class="text-lg font-semibold text-gray-100">{{ t('nav.configFile') }}</h2>
          <span v-if="fileName" class="px-2.5 py-0.5 text-xs font-medium rounded-full"
            :class="isLyFormat ? 'bg-purple-500/20 text-purple-400' : 'bg-blue-500/20 text-blue-400'">
            {{ fileName }}
          </span>
          <span v-if="hasDraft"
            class="px-2 py-0.5 text-xs font-medium rounded-full bg-cyan-500/20 text-cyan-400">
            {{ t('page.2a2fd29bd27a') }}
          </span>
          <span v-if="hasChanges"
            class="px-2 py-0.5 text-xs font-medium rounded-full bg-amber-500/20 text-amber-400">
            {{ t('page.6a1be012c99c') }}
          </span>
        </div>
        <div class="flex items-center gap-2">
          <button @click="showClearPatchConfirm = true"
            class="btn btn-sm text-xs text-red-400 border border-red-500/30 hover:bg-red-500/10 transition-colors">
            {{ t('page.22579af1d9ef') }}
          </button>
          <button v-if="isDifferentFromOriginal" @click="restoreOriginal"
            class="btn btn-sm btn-secondary text-xs">
            {{ t('page.2d41469ba61a') }}
          </button>
          <button @click="loadFile" :disabled="loading"
            class="btn btn-sm btn-secondary flex items-center gap-1.5">
            <span v-if="loading" class="w-3.5 h-3.5 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
            <span>{{ t('common.refresh') }}</span>
          </button>
        </div>
      </div>
    </div>

    <!-- 提示信息 -->
    <div class="px-4 py-3 rounded-lg bg-blue-500/10 border border-blue-500/20 text-sm text-blue-300">
      <span class="font-medium">{{ t('page.be8e4dd716fb') }}</span>
      {{ t('page.61ab4df82424') }}
    </div>

    <!-- Tab 切换（仅 .ly 格式） -->
    <div v-if="isLyFormat" class="flex gap-1 bg-dark-card rounded-lg p-1 border border-dark-border">
      <button @click="switchTab('source')"
        :class="['flex-1 px-4 py-2 text-sm font-medium rounded-md transition-colors',
          activeTab === 'source'
            ? 'bg-primary-500/15 text-primary-400'
            : 'text-gray-400 hover:text-gray-200 hover:bg-white/5']">
        {{ t('page.91d87247cf53') }}
      </button>
      <button @click="switchTab('yaml')"
        :class="['flex-1 px-4 py-2 text-sm font-medium rounded-md transition-colors',
          activeTab === 'yaml'
            ? 'bg-primary-500/15 text-primary-400'
            : 'text-gray-400 hover:text-gray-200 hover:bg-white/5']">
        <span v-if="converting" class="inline-block w-3 h-3 border-2 border-gray-400 border-t-transparent rounded-full animate-spin mr-1.5"></span>
        {{ t('page.c479b3c23b91') }}
      </button>
    </div>

    <!-- 编辑区域 -->
    <div class="card !p-0 overflow-hidden">
      <!-- 源文件编辑 -->
      <div v-show="activeTab === 'source'">
        <div class="flex items-center justify-between px-4 py-2 bg-dark-sidebar border-b border-dark-border">
          <span class="text-xs text-gray-500">{{ isLyFormat ? t('page.b53d7d012e58') : t('page.cbdd09fe4a5d') }}</span>
          <span class="text-xs text-gray-500">{{ t('page.f7841398222d') }}</span>
        </div>
        <textarea
          v-model="content"
          @keydown="handleKeydown"
          class="w-full bg-[#0d1117] text-[#e6edf3] font-mono text-sm leading-6 p-4 resize-y outline-none border-none"
          :style="{ minHeight: '500px', tabSize: 4 }"
          spellcheck="false"
          autocomplete="off"
          autocorrect="off"
          autocapitalize="off"
        ></textarea>
      </div>

      <!-- YAML 预览（只读） -->
      <div v-show="activeTab === 'yaml'">
        <div class="flex items-center justify-between px-4 py-2 bg-dark-sidebar border-b border-dark-border">
          <span class="text-xs text-gray-500">{{ t('page.76366a89cda4') }}</span>
          <span class="text-xs text-gray-500">{{ t('page.65e0f42704e3') }}</span>
        </div>
        <textarea
          :value="yamlPreview"
          readonly
          class="w-full bg-[#0d1117] text-[#8b949e] font-mono text-sm leading-6 p-4 resize-y outline-none border-none cursor-default"
          :style="{ minHeight: '500px', tabSize: 2 }"
          spellcheck="false"
        ></textarea>
      </div>
    </div>

    <!-- 底部操作栏 -->
    <div class="card">
      <div class="flex items-center justify-between">
        <div class="text-xs text-gray-500">
          {{ t('page.099aeb0c92f3') }}
        </div>
        <div class="flex items-center gap-2">
          <button @click="resetContent" :disabled="!hasChanges || saving"
            class="btn btn-sm btn-secondary">
            {{ t('page.b5a929dae5ba') }}
          </button>
          <button @click="saveConfig(false)" :disabled="saving || !hasChanges"
            class="btn btn-sm btn-secondary">
            {{ saving ? t('page.917b1c1f18d0') : t('page.5329e3f0ee46') }}
          </button>
          <button @click="saveConfig(true)" :disabled="saving"
            class="btn btn-sm btn-primary flex items-center gap-1.5">
            <span v-if="saving" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
            <span>{{ saving ? t('page.917b1c1f18d0') : t('page.5d594fbb069d') }}</span>
          </button>
        </div>
      </div>
    </div>

    <div class="card !p-0 overflow-hidden">
      <div class="flex items-center justify-between px-4 py-3 border-b border-dark-border">
        <div>
          <h3 class="text-sm font-semibold text-gray-200">{{ t('page.72da49a8e2ac') }}</h3>
          <p class="mt-1 text-xs text-gray-500">{{ t('page.6327307b165d') }}</p>
        </div>
        <button class="btn btn-sm btn-secondary" :disabled="historyLoading" @click="loadHistory">
          {{ historyLoading ? t('common.loading') : t('page.a2e016a613db') }}
        </button>
      </div>
      <ListToolbar v-model:page="historyPage" v-model:search="historySearch" :total="historyTotal" :busy="historyLoading" @refresh="loadHistory" />
      <div class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead class="bg-dark-sidebar text-gray-400">
            <tr><th class="px-4 py-3 text-left">{{ t('settings.time') }}</th><th class="px-4 py-3 text-left">{{ t('settings.type') }}</th><th class="px-4 py-3 text-left">{{ t('page.e18e3f8ab246') }}</th><th class="px-4 py-3 text-left">{{ t('page.be37d84119d6') }}</th><th class="px-4 py-3 text-left">{{ t('page.21c04b2eeeb4') }}</th><th class="px-4 py-3 text-right">{{ t('settings.actions') }}</th></tr>
          </thead>
          <tbody class="divide-y divide-dark-border">
            <tr v-for="version in versions" :key="version.id">
              <td class="px-4 py-3 whitespace-nowrap text-gray-300">{{ formatTime(version.createdAt) }}</td>
              <td class="px-4 py-3 text-gray-400">{{ version.kind === 'main' ? t('page.8c223ee94a13') : t('page.900fb3c97098') }}</td>
              <td class="px-4 py-3 text-gray-400">{{ version.operator }}</td>
              <td class="px-4 py-3 font-mono text-xs text-gray-400">{{ version.action }}</td>
              <td class="px-4 py-3 font-mono text-xs text-gray-500">{{ shortHash(version.sha256) }}</td>
              <td class="px-4 py-3 text-right whitespace-nowrap">
                <button class="text-primary-400 hover:text-primary-300 mr-4" :disabled="diffLoading" @click="viewDiff(version)">{{ t('page.b35001374ea9') }}</button>
                <button class="text-amber-400 hover:text-amber-300" @click="rollbackTarget = version">{{ t('plugins.rollback') }}</button>
              </td>
            </tr>
            <tr v-if="!historyLoading && versions.length === 0"><td colspan="6" class="px-4 py-8 text-center text-gray-500">{{ t('page.f7cd99e6008a') }}</td></tr>
          </tbody>
        </table>
      </div>
    </div>

    <div class="card !p-0 overflow-hidden">
      <div class="flex items-center justify-between px-4 py-3 border-b border-dark-border">
        <div><h3 class="text-sm font-semibold text-gray-200">{{ t('page.ec5658d4a5d2') }}</h3><p class="mt-1 text-xs text-gray-500">{{ t('page.4f48d8ae4fbd') }}</p></div>
        <div class="flex gap-2">
          <button v-if="upgradePointers.previous" class="btn btn-sm btn-secondary text-amber-400" :disabled="activatingUpgrade" @click="rollbackProgram">{{ t('page.2050c8a8c9e1') }} {{ upgradePointers.previous }}</button>
          <button class="btn btn-sm btn-secondary" :disabled="upgradeLoading" @click="loadUpgrades">{{ t('page.9b56266bdb85') }}</button>
        </div>
      </div>
      <div class="px-4 pt-4">
        <ListToolbar v-model:page="upgradePage" v-model:search="upgradeSearch" :total="upgradeTotal" :busy="upgradeLoading" @refresh="loadUpgrades" />
      </div>
      <div class="grid gap-3 border-b border-dark-border p-4 md:grid-cols-[1fr_0.7fr_2fr_auto]">
        <input v-model.trim="upgradePackageName" class="input" :placeholder="t('page.781b4c02f1bb')" />
        <input v-model.trim="upgradeVersion" class="input" :placeholder="t('page.a98030a9974c')" />
        <input v-model.trim="upgradeSha256" class="input font-mono" placeholder="SHA-256" />
        <button class="btn btn-primary whitespace-nowrap" :disabled="upgradeLoading" @click="stageUpgrade">{{ upgradeLoading ? t('page.4abfc9142b24') : t('page.097cbc4ee1b5') }}</button>
      </div>
      <div class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead class="bg-dark-sidebar text-gray-400"><tr><th class="px-4 py-3 text-left">{{ t('ui.version') }}</th><th class="px-4 py-3 text-left">{{ t('page.fd184c2ce046') }}</th><th class="px-4 py-3 text-left">{{ t('page.21c04b2eeeb4') }}</th><th class="px-4 py-3 text-left">{{ t('settings.status') }}</th><th class="px-4 py-3 text-right">{{ t('settings.actions') }}</th></tr></thead>
          <tbody class="divide-y divide-dark-border">
            <tr v-for="release in upgradeReleases" :key="release.version">
              <td class="px-4 py-3 text-gray-200"><span class="font-medium">{{ release.version }}</span><span v-if="upgradePointers.current === release.version" class="ml-2 text-xs text-emerald-400">{{ t('page.cb62ebd689ee') }}</span><span v-if="upgradePointers.pending === release.version" class="ml-2 text-xs text-amber-400">{{ t('page.c263dac32667') }}</span></td>
              <td class="px-4 py-3 whitespace-nowrap text-gray-400">{{ formatTime(release.stagedAt) }}</td>
              <td class="px-4 py-3 font-mono text-xs text-gray-500">{{ shortHash(release.sha256) }}</td>
              <td class="px-4 py-3 text-emerald-400">{{ t('page.0a1b6f1b57f5') }}</td>
              <td class="px-4 py-3 text-right"><button class="text-primary-400 hover:text-primary-300" :disabled="upgradePointers.current === release.version || activatingUpgrade" @click="activateTarget = release">{{ t('page.dd1286c29e9b') }}</button></td>
            </tr>
            <tr v-if="!upgradeLoading && upgradeReleases.length === 0"><td colspan="5" class="px-4 py-8 text-center text-gray-500">{{ t('page.2a23f6aa281b') }}</td></tr>
          </tbody>
        </table>
      </div>
    </div>

    <!-- 端口变更重启确认对话框 -->
    <Teleport to="body">
      <div v-if="showRestartConfirm" class="fixed inset-0 z-50 flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="showRestartConfirm = false"></div>
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl p-6 max-w-md w-full mx-4">
          <h3 class="text-lg font-semibold text-gray-100 mb-2">{{ t('page.b6034510c5cd') }}</h3>
          <p class="text-sm text-gray-400 mb-4">
            {{ t('page.e9a0cb6a8ee2') }}
          </p>
          <p class="text-sm text-amber-400 mb-5">
            {{ t('page.f9389419a2a7') }}
          </p>
          <div class="flex justify-end gap-3">
            <button @click="showRestartConfirm = false" class="btn btn-sm btn-secondary">
              {{ t('page.99845832ed7f') }}
            </button>
            <button @click="confirmRestart" :disabled="restarting"
              class="btn btn-sm bg-amber-600 hover:bg-amber-500 text-white flex items-center gap-1.5">
              <span v-if="restarting" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
              <span>{{ restarting ? t('page.f0bc9c012964') : t('page.700b6c80d954') }}</span>
            </button>
          </div>
        </div>
      </div>
    </Teleport>
    <!-- 清除补丁确认对话框 -->
    <Teleport to="body">
      <div v-if="showClearPatchConfirm" class="fixed inset-0 z-50 flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="showClearPatchConfirm = false"></div>
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl p-6 max-w-md w-full mx-4">
          <h3 class="text-lg font-semibold text-gray-100 mb-2">{{ t('page.22579af1d9ef') }}</h3>
          <p class="text-sm text-gray-400 mb-4">
            {{ t('page.ad963fc5a53b') }}
          </p>
          <p class="text-sm text-red-400 mb-5">
            {{ t('page.a13c91288e4c') }}
          </p>
          <div class="flex justify-end gap-3">
            <button @click="showClearPatchConfirm = false" class="btn btn-sm btn-secondary">
              {{ t('common.cancel') }}
            </button>
            <button @click="confirmClearPatch" :disabled="clearingPatch"
              class="btn btn-sm bg-red-600 hover:bg-red-500 text-white flex items-center gap-1.5">
              <span v-if="clearingPatch" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
              <span>{{ clearingPatch ? t('page.30cf331f0b1f') : t('page.a5da1dd8fd92') }}</span>
            </button>
          </div>
        </div>
      </div>
    </Teleport>
    <Teleport to="body">
      <div v-if="selectedDiff" class="fixed inset-0 z-50 flex items-center justify-center p-4">
        <div class="absolute inset-0 bg-black/70" @click="selectedDiff = null"></div>
        <div class="relative flex max-h-[88vh] w-full max-w-6xl flex-col overflow-hidden rounded-lg border border-dark-border bg-dark-card shadow-2xl">
          <div class="flex items-center justify-between border-b border-dark-border px-5 py-4">
            <div><h3 class="font-semibold text-gray-100">{{ t('page.43164224cdd9') }}</h3><p class="mt-1 font-mono text-xs text-gray-500">{{ selectedDiff.version.id }}</p></div>
            <button class="text-xl text-gray-400 hover:text-white" :title="t('common.close')" @click="selectedDiff = null">×</button>
          </div>
          <div class="overflow-auto bg-[#0d1117] font-mono text-xs leading-5">
            <div v-for="(line, index) in selectedDiff.lines" :key="index" class="grid grid-cols-[4rem_4rem_1.5rem_minmax(0,1fr)] px-3"
              :class="line.kind === 'add' ? 'bg-emerald-950/60 text-emerald-200' : line.kind === 'remove' ? 'bg-red-950/60 text-red-200' : 'text-gray-400'">
              <span class="text-right text-gray-600">{{ line.oldLine ?? '' }}</span><span class="text-right text-gray-600">{{ line.newLine ?? '' }}</span>
              <span class="text-center">{{ line.kind === 'add' ? '+' : line.kind === 'remove' ? '-' : ' ' }}</span><span class="whitespace-pre">{{ line.text }}</span>
            </div>
            <div v-if="selectedDiff.truncated" class="px-4 py-3 text-amber-400">{{ t('page.ba91bcd9de57') }}</div>
          </div>
          <div class="flex justify-end gap-3 border-t border-dark-border px-5 py-4"><button class="btn btn-sm btn-secondary" @click="selectedDiff = null">{{ t('common.close') }}</button><button class="btn btn-sm btn-primary" @click="rollbackTarget = selectedDiff!.version">{{ t('page.fd640d4abd9f') }}</button></div>
        </div>
      </div>
    </Teleport>
    <Teleport to="body">
      <div v-if="rollbackTarget" class="fixed inset-0 z-[60] flex items-center justify-center p-4">
        <div class="absolute inset-0 bg-black/70" @click="rollbackTarget = null"></div>
        <div class="relative w-full max-w-md rounded-lg border border-dark-border bg-dark-card p-6 shadow-2xl">
          <h3 class="text-lg font-semibold text-gray-100">{{ t('page.e7f82beaab05') }}</h3>
          <p class="my-4 text-sm text-gray-400">{{ t('page.6ffa118ccfa6') }} {{ rollbackTarget.kind === 'main' ? t('page.8c223ee94a13') : t('page.900fb3c97098') }} {{ t('page.6ea925956fab') }} {{ formatTime(rollbackTarget.createdAt) }} {{ t('page.45106a0eda8e') }}</p>
          <div class="flex justify-end gap-3"><button class="btn btn-sm btn-secondary" @click="rollbackTarget = null">{{ t('common.cancel') }}</button><button class="btn btn-sm bg-amber-600 text-white hover:bg-amber-500" :disabled="rollingBack" @click="confirmRollback">{{ rollingBack ? t('page.7cc758a5d3f8') : t('page.eb32e19bdb15') }}</button></div>
        </div>
      </div>
    </Teleport>
    <Teleport to="body">
      <div v-if="activateTarget" class="fixed inset-0 z-[60] flex items-center justify-center p-4">
        <div class="absolute inset-0 bg-black/70" @click="activateTarget = null"></div>
        <div class="relative w-full max-w-md rounded-lg border border-dark-border bg-dark-card p-6 shadow-2xl">
          <h3 class="text-lg font-semibold text-gray-100">{{ t('page.65a3909559eb') }}</h3>
          <p class="my-4 text-sm text-gray-400">{{ t('page.b64986dbf40f') }} {{ activateTarget.version }}{{ t('page.de5e6436aaed') }}</p>
          <div class="flex justify-end gap-3"><button class="btn btn-sm btn-secondary" @click="activateTarget = null">{{ t('common.cancel') }}</button><button class="btn btn-sm btn-primary" :disabled="activatingUpgrade" @click="confirmActivateUpgrade">{{ activatingUpgrade ? t('settings.processing') : t('page.406e8c3b8f3b') }}</button></div>
        </div>
      </div>
    </Teleport>
  </div>
</template>
