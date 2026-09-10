<script setup lang="ts">
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

const formatTime = (value: string) => new Date(value).toLocaleString()
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
    showError(error?.response?.data?.message || '加载配置历史失败')
  } finally {
    if (generation === historyGeneration) historyLoading.value = false
  }
}

const viewDiff = async (version: ConfigVersionInfo) => {
  diffLoading.value = true
  try {
    selectedDiff.value = (await configFileApi.getVersionDiff(version.id)).diff
  } catch (error: any) {
    showError(error?.response?.data?.message || '加载配置差异失败')
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
    showSuccess('配置回滚任务已提交')
    await router.push({ path: '/management-tasks', query: { task: result.data.id } })
  } catch (error: any) {
    showError(error?.response?.data?.message || '配置回滚失败')
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
    showError(error?.response?.data?.message || '加载程序版本失败')
  } finally { if (request === upgradeRequest) upgradeLoading.value = false }
}

const stageUpgrade = async () => {
  if (!upgradePackageName.value || !upgradeVersion.value || !/^[a-fA-F0-9]{64}$/.test(upgradeSha256.value)) {
    showError('请填写 ZIP 文件名、版本号和完整 SHA-256')
    return
  }
  upgradeLoading.value = true
  try {
    const response = await upgradeApi.stage(upgradePackageName.value, upgradeVersion.value, upgradeSha256.value)
    showSuccess('升级校验任务已提交')
    upgradePackageName.value = ''; upgradeVersion.value = ''; upgradeSha256.value = ''
    await router.push({ path: '/management-tasks', query: { task: response.data.id } })
  } catch (error: any) {
    showError(error?.response?.data?.message || '候选版本校验失败')
  } finally { upgradeLoading.value = false }
}

const confirmActivateUpgrade = async () => {
  if (!activateTarget.value) return
  activatingUpgrade.value = true
  try {
    const result = await upgradeApi.activate(activateTarget.value.version)
    showSuccess('程序激活任务已提交')
    activateTarget.value = null
    await router.push({ path: '/management-tasks', query: { task: result.data.id } })
  } catch (error: any) {
    showError(error?.response?.data?.message || '激活失败')
  } finally { activatingUpgrade.value = false }
}

const rollbackProgram = async () => {
  activatingUpgrade.value = true
  try {
    const result = await upgradeApi.rollback()
    showSuccess('程序回退任务已提交')
    await router.push({ path: '/management-tasks', query: { task: result.data.id } })
  } catch (error: any) {
    showError(error?.response?.data?.message || '程序版本回退失败')
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
    showError(error?.response?.data?.message || '加载配置文件失败')
  } finally {
    loading.value = false
  }
}

// 恢复为原始文件内容
const restoreOriginal = () => {
  content.value = originalContent.value
  showSuccess('已恢复为原始配置文件内容')
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
      showError(response.message || '转换失败')
    }
  } catch (error: any) {
    showError(error?.response?.data?.message || '转换失败')
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
      showSuccess('配置发布任务已提交')
      await router.push({ path: '/management-tasks', query: { task: response.data.id } })
      return
    }
    if (response.success) {
      lastSavedContent.value = content.value
      hasDraft.value = true
      showSuccess(response.message || '草稿已保存')

      // 检测到端口变更，询问用户是否重启
      if (response.restartRequired || response.portsChanged) {
        showRestartConfirm.value = true
      }
    } else {
      showError(response.message || '保存失败')
    }
  } catch (error: any) {
    showError(error?.response?.data?.message || '保存失败')
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
    showSuccess('服务正在重启，请稍后刷新页面...')
    showRestartConfirm.value = false
  } catch {
    showError('重启请求发送失败')
  } finally {
    restarting.value = false
  }
}

// 重置为上次保存的内容
const resetContent = () => {
  content.value = lastSavedContent.value
  showSuccess('已重置为上次保存的内容')
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
      showSuccess('所有补丁已清除')
      showClearPatchConfirm.value = false
    } else {
      showError(res?.message || '清除补丁失败')
    }
  } catch {
    showError('清除补丁失败，请检查网络连接')
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
          <h2 class="text-lg font-semibold text-gray-100">配置文件</h2>
          <span v-if="fileName" class="px-2.5 py-0.5 text-xs font-medium rounded-full"
            :class="isLyFormat ? 'bg-purple-500/20 text-purple-400' : 'bg-blue-500/20 text-blue-400'">
            {{ fileName }}
          </span>
          <span v-if="hasDraft"
            class="px-2 py-0.5 text-xs font-medium rounded-full bg-cyan-500/20 text-cyan-400">
            草稿
          </span>
          <span v-if="hasChanges"
            class="px-2 py-0.5 text-xs font-medium rounded-full bg-amber-500/20 text-amber-400">
            未保存
          </span>
        </div>
        <div class="flex items-center gap-2">
          <button @click="showClearPatchConfirm = true"
            class="btn btn-sm text-xs text-red-400 border border-red-500/30 hover:bg-red-500/10 transition-colors">
            清除所有补丁
          </button>
          <button v-if="isDifferentFromOriginal" @click="restoreOriginal"
            class="btn btn-sm btn-secondary text-xs">
            恢复原始
          </button>
          <button @click="loadFile" :disabled="loading"
            class="btn btn-sm btn-secondary flex items-center gap-1.5">
            <span v-if="loading" class="w-3.5 h-3.5 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
            <span>刷新</span>
          </button>
        </div>
      </div>
    </div>

    <!-- 提示信息 -->
    <div class="px-4 py-3 rounded-lg bg-blue-500/10 border border-blue-500/20 text-sm text-blue-300">
      <span class="font-medium">提示：</span>
      编辑内容将保存为草稿文件，不会覆盖原始配置。重载仅更新路由、规则等运行时配置，端口监听变更需要重启服务。
    </div>

    <!-- Tab 切换（仅 .ly 格式） -->
    <div v-if="isLyFormat" class="flex gap-1 bg-dark-card rounded-lg p-1 border border-dark-border">
      <button @click="switchTab('source')"
        :class="['flex-1 px-4 py-2 text-sm font-medium rounded-md transition-colors',
          activeTab === 'source'
            ? 'bg-primary-500/15 text-primary-400'
            : 'text-gray-400 hover:text-gray-200 hover:bg-white/5']">
        📝 源文件 (.ly)
      </button>
      <button @click="switchTab('yaml')"
        :class="['flex-1 px-4 py-2 text-sm font-medium rounded-md transition-colors',
          activeTab === 'yaml'
            ? 'bg-primary-500/15 text-primary-400'
            : 'text-gray-400 hover:text-gray-200 hover:bg-white/5']">
        <span v-if="converting" class="inline-block w-3 h-3 border-2 border-gray-400 border-t-transparent rounded-full animate-spin mr-1.5"></span>
        🔄 YAML 预览
      </button>
    </div>

    <!-- 编辑区域 -->
    <div class="card !p-0 overflow-hidden">
      <!-- 源文件编辑 -->
      <div v-show="activeTab === 'source'">
        <div class="flex items-center justify-between px-4 py-2 bg-dark-sidebar border-b border-dark-border">
          <span class="text-xs text-gray-500">{{ isLyFormat ? 'LyWaf 配置格式 (.ly)' : 'YAML 配置格式' }}</span>
          <span class="text-xs text-gray-500">Ctrl+S 保存草稿</span>
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
          <span class="text-xs text-gray-500">YAML 转换预览（只读）</span>
          <span class="text-xs text-gray-500">由 .ly 文件自动转换</span>
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
          草稿保存不影响原始配置文件，重载后运行时配置立即生效
        </div>
        <div class="flex items-center gap-2">
          <button @click="resetContent" :disabled="!hasChanges || saving"
            class="btn btn-sm btn-secondary">
            撤销修改
          </button>
          <button @click="saveConfig(false)" :disabled="saving || !hasChanges"
            class="btn btn-sm btn-secondary">
            {{ saving ? '保存中...' : '保存草稿' }}
          </button>
          <button @click="saveConfig(true)" :disabled="saving"
            class="btn btn-sm btn-primary flex items-center gap-1.5">
            <span v-if="saving" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
            <span>{{ saving ? '保存中...' : '保存并重载' }}</span>
          </button>
        </div>
      </div>
    </div>

    <div class="card !p-0 overflow-hidden">
      <div class="flex items-center justify-between px-4 py-3 border-b border-dark-border">
        <div>
          <h3 class="text-sm font-semibold text-gray-200">配置历史</h3>
          <p class="mt-1 text-xs text-gray-500">每次有效写入都会生成不可变版本，可查看差异或回滚。</p>
        </div>
        <button class="btn btn-sm btn-secondary" :disabled="historyLoading" @click="loadHistory">
          {{ historyLoading ? '加载中...' : '刷新历史' }}
        </button>
      </div>
      <ListToolbar v-model:page="historyPage" v-model:search="historySearch" :total="historyTotal" :busy="historyLoading" @refresh="loadHistory" />
      <div class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead class="bg-dark-sidebar text-gray-400">
            <tr><th class="px-4 py-3 text-left">时间</th><th class="px-4 py-3 text-left">类型</th><th class="px-4 py-3 text-left">操作者</th><th class="px-4 py-3 text-left">动作</th><th class="px-4 py-3 text-left">摘要</th><th class="px-4 py-3 text-right">操作</th></tr>
          </thead>
          <tbody class="divide-y divide-dark-border">
            <tr v-for="version in versions" :key="version.id">
              <td class="px-4 py-3 whitespace-nowrap text-gray-300">{{ formatTime(version.createdAt) }}</td>
              <td class="px-4 py-3 text-gray-400">{{ version.kind === 'main' ? '主配置' : '运行时补丁' }}</td>
              <td class="px-4 py-3 text-gray-400">{{ version.operator }}</td>
              <td class="px-4 py-3 font-mono text-xs text-gray-400">{{ version.action }}</td>
              <td class="px-4 py-3 font-mono text-xs text-gray-500">{{ shortHash(version.sha256) }}</td>
              <td class="px-4 py-3 text-right whitespace-nowrap">
                <button class="text-primary-400 hover:text-primary-300 mr-4" :disabled="diffLoading" @click="viewDiff(version)">查看差异</button>
                <button class="text-amber-400 hover:text-amber-300" @click="rollbackTarget = version">回滚</button>
              </td>
            </tr>
            <tr v-if="!historyLoading && versions.length === 0"><td colspan="6" class="px-4 py-8 text-center text-gray-500">暂无配置版本</td></tr>
          </tbody>
        </table>
      </div>
    </div>

    <div class="card !p-0 overflow-hidden">
      <div class="flex items-center justify-between px-4 py-3 border-b border-dark-border">
        <div><h3 class="text-sm font-semibold text-gray-200">程序版本</h3><p class="mt-1 text-xs text-gray-500">升级包需先放入配置的收件箱目录，校验通过后才能激活。</p></div>
        <div class="flex gap-2">
          <button v-if="upgradePointers.previous" class="btn btn-sm btn-secondary text-amber-400" :disabled="activatingUpgrade" @click="rollbackProgram">回退到 {{ upgradePointers.previous }}</button>
          <button class="btn btn-sm btn-secondary" :disabled="upgradeLoading" @click="loadUpgrades">刷新版本</button>
        </div>
      </div>
      <div class="px-4 pt-4">
        <ListToolbar v-model:page="upgradePage" v-model:search="upgradeSearch" :total="upgradeTotal" :busy="upgradeLoading" @refresh="loadUpgrades" />
      </div>
      <div class="grid gap-3 border-b border-dark-border p-4 md:grid-cols-[1fr_0.7fr_2fr_auto]">
        <input v-model.trim="upgradePackageName" class="input" placeholder="收件箱 ZIP 文件名" />
        <input v-model.trim="upgradeVersion" class="input" placeholder="版本号" />
        <input v-model.trim="upgradeSha256" class="input font-mono" placeholder="SHA-256" />
        <button class="btn btn-primary whitespace-nowrap" :disabled="upgradeLoading" @click="stageUpgrade">{{ upgradeLoading ? '校验中...' : '校验并入库' }}</button>
      </div>
      <div class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead class="bg-dark-sidebar text-gray-400"><tr><th class="px-4 py-3 text-left">版本</th><th class="px-4 py-3 text-left">入库时间</th><th class="px-4 py-3 text-left">摘要</th><th class="px-4 py-3 text-left">状态</th><th class="px-4 py-3 text-right">操作</th></tr></thead>
          <tbody class="divide-y divide-dark-border">
            <tr v-for="release in upgradeReleases" :key="release.version">
              <td class="px-4 py-3 text-gray-200"><span class="font-medium">{{ release.version }}</span><span v-if="upgradePointers.current === release.version" class="ml-2 text-xs text-emerald-400">当前</span><span v-if="upgradePointers.pending === release.version" class="ml-2 text-xs text-amber-400">待激活</span></td>
              <td class="px-4 py-3 whitespace-nowrap text-gray-400">{{ formatTime(release.stagedAt) }}</td>
              <td class="px-4 py-3 font-mono text-xs text-gray-500">{{ shortHash(release.sha256) }}</td>
              <td class="px-4 py-3 text-emerald-400">已验证</td>
              <td class="px-4 py-3 text-right"><button class="text-primary-400 hover:text-primary-300" :disabled="upgradePointers.current === release.version || activatingUpgrade" @click="activateTarget = release">激活</button></td>
            </tr>
            <tr v-if="!upgradeLoading && upgradeReleases.length === 0"><td colspan="5" class="px-4 py-8 text-center text-gray-500">暂无候选程序版本</td></tr>
          </tbody>
        </table>
      </div>
    </div>

    <!-- 端口变更重启确认对话框 -->
    <Teleport to="body">
      <div v-if="showRestartConfirm" class="fixed inset-0 z-50 flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="showRestartConfirm = false"></div>
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl p-6 max-w-md w-full mx-4">
          <h3 class="text-lg font-semibold text-gray-100 mb-2">检测到端口变更</h3>
          <p class="text-sm text-gray-400 mb-4">
            配置已重新加载，但监听端口发生了变化。端口变更需要重启服务才能生效，重启期间服务将短暂不可用。
          </p>
          <p class="text-sm text-amber-400 mb-5">
            是否立即重启服务以应用新的端口配置？
          </p>
          <div class="flex justify-end gap-3">
            <button @click="showRestartConfirm = false" class="btn btn-sm btn-secondary">
              稍后再说
            </button>
            <button @click="confirmRestart" :disabled="restarting"
              class="btn btn-sm bg-amber-600 hover:bg-amber-500 text-white flex items-center gap-1.5">
              <span v-if="restarting" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
              <span>{{ restarting ? '重启中...' : '立即重启' }}</span>
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
          <h3 class="text-lg font-semibold text-gray-100 mb-2">清除所有补丁</h3>
          <p class="text-sm text-gray-400 mb-4">
            将清除通过控制台界面修改的所有运行时补丁（路由、集群、文件服务器、简单响应、监听端口等），恢复为配置文件中的原始状态。
          </p>
          <p class="text-sm text-red-400 mb-5">
            此操作不可撤销，确定要继续吗？
          </p>
          <div class="flex justify-end gap-3">
            <button @click="showClearPatchConfirm = false" class="btn btn-sm btn-secondary">
              取消
            </button>
            <button @click="confirmClearPatch" :disabled="clearingPatch"
              class="btn btn-sm bg-red-600 hover:bg-red-500 text-white flex items-center gap-1.5">
              <span v-if="clearingPatch" class="w-3.5 h-3.5 border-2 border-white/40 border-t-white rounded-full animate-spin"></span>
              <span>{{ clearingPatch ? '清除中...' : '确认清除' }}</span>
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
            <div><h3 class="font-semibold text-gray-100">配置差异</h3><p class="mt-1 font-mono text-xs text-gray-500">{{ selectedDiff.version.id }}</p></div>
            <button class="text-xl text-gray-400 hover:text-white" title="关闭" @click="selectedDiff = null">×</button>
          </div>
          <div class="overflow-auto bg-[#0d1117] font-mono text-xs leading-5">
            <div v-for="(line, index) in selectedDiff.lines" :key="index" class="grid grid-cols-[4rem_4rem_1.5rem_minmax(0,1fr)] px-3"
              :class="line.kind === 'add' ? 'bg-emerald-950/60 text-emerald-200' : line.kind === 'remove' ? 'bg-red-950/60 text-red-200' : 'text-gray-400'">
              <span class="text-right text-gray-600">{{ line.oldLine ?? '' }}</span><span class="text-right text-gray-600">{{ line.newLine ?? '' }}</span>
              <span class="text-center">{{ line.kind === 'add' ? '+' : line.kind === 'remove' ? '-' : ' ' }}</span><span class="whitespace-pre">{{ line.text }}</span>
            </div>
            <div v-if="selectedDiff.truncated" class="px-4 py-3 text-amber-400">差异过大，仅显示前 5000 行。</div>
          </div>
          <div class="flex justify-end gap-3 border-t border-dark-border px-5 py-4"><button class="btn btn-sm btn-secondary" @click="selectedDiff = null">关闭</button><button class="btn btn-sm btn-primary" @click="rollbackTarget = selectedDiff!.version">回滚到此版本</button></div>
        </div>
      </div>
    </Teleport>
    <Teleport to="body">
      <div v-if="rollbackTarget" class="fixed inset-0 z-[60] flex items-center justify-center p-4">
        <div class="absolute inset-0 bg-black/70" @click="rollbackTarget = null"></div>
        <div class="relative w-full max-w-md rounded-lg border border-dark-border bg-dark-card p-6 shadow-2xl">
          <h3 class="text-lg font-semibold text-gray-100">确认回滚配置</h3>
          <p class="my-4 text-sm text-gray-400">将把 {{ rollbackTarget.kind === 'main' ? '主配置' : '运行时补丁' }} 回滚到 {{ formatTime(rollbackTarget.createdAt) }} 的版本，并立即重新加载。</p>
          <div class="flex justify-end gap-3"><button class="btn btn-sm btn-secondary" @click="rollbackTarget = null">取消</button><button class="btn btn-sm bg-amber-600 text-white hover:bg-amber-500" :disabled="rollingBack" @click="confirmRollback">{{ rollingBack ? '回滚中...' : '确认回滚' }}</button></div>
        </div>
      </div>
    </Teleport>
    <Teleport to="body">
      <div v-if="activateTarget" class="fixed inset-0 z-[60] flex items-center justify-center p-4">
        <div class="absolute inset-0 bg-black/70" @click="activateTarget = null"></div>
        <div class="relative w-full max-w-md rounded-lg border border-dark-border bg-dark-card p-6 shadow-2xl">
          <h3 class="text-lg font-semibold text-gray-100">激活程序版本</h3>
          <p class="my-4 text-sm text-gray-400">实例将停止接收新流量，等待在途请求结束，然后启动版本 {{ activateTarget.version }}。候选未能按时就绪时会自动恢复当前程序。</p>
          <div class="flex justify-end gap-3"><button class="btn btn-sm btn-secondary" @click="activateTarget = null">取消</button><button class="btn btn-sm btn-primary" :disabled="activatingUpgrade" @click="confirmActivateUpgrade">{{ activatingUpgrade ? '处理中...' : '确认激活' }}</button></div>
        </div>
      </div>
    </Teleport>
  </div>
</template>
