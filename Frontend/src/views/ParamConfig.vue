<script setup lang="ts">
import { t } from '@/i18n'
import { ref, onMounted, onBeforeUnmount } from 'vue'
import ListToolbar from '@/components/common/ListToolbar.vue'
import { paramApi } from '@/api'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()

const loading = ref(false)
const saving = ref(false)
const isFirstServer = ref(false)

// 环境变量
const envLoading = ref(false)
const envItems = ref<Array<{ key: string; value: string }>>([])
const envFilter = ref('')

const envPage = ref(1)
const envTotal = ref(0)
let envRequest = 0
onBeforeUnmount(() => { envRequest++ })

const loadConfig = async () => {
  loading.value = true
  try {
    const res = await paramApi.get() as any
    if (res?.success) {
      isFirstServer.value = res.isFirstServer
    }
  } catch {
    showError(t('page.7ba74da2c4af'))
  } finally {
    loading.value = false
  }
}

const loadEnv = async () => {
  const request = ++envRequest
  envLoading.value = true
  try {
    const res = await paramApi.getEnv(envPage.value, envFilter.value)
    if (request !== envRequest) return
    if (res?.success) {
      envItems.value = res.data
      envTotal.value = res.total
      if (!res.data.length && envPage.value > 1) {
        envPage.value = Math.max(1, Math.ceil(res.total / 20))
        await loadEnv()
      }
    }
  } catch {
    if (request === envRequest) showError(t('page.74f59dab7bce'))
  } finally {
    if (request === envRequest) envLoading.value = false
  }
}

const handleSave = async () => {
  saving.value = true
  try {
    const res = await paramApi.update({ isFirstServer: isFirstServer.value }) as any
    if (res?.success) {
      showSuccess(res.message || t('page.f07e7097578c'))
    } else {
      showError(res?.message || t('page.ec99e5c45d64'))
    }
  } catch {
    showError(t('page.237d1c2e13ea'))
  } finally {
    saving.value = false
  }
}

onMounted(() => {
  loadConfig()
  loadEnv()
})
</script>

<template>
  <div class="space-y-4">
    <!-- 页面标题 -->
    <div class="card">
      <div class="flex items-center gap-3">
        <h2 class="text-lg font-semibold text-gray-100">{{ t('nav.parameters') }}</h2>
        <span class="text-xs text-gray-500">{{ t('page.251d8500948b') }}</span>
      </div>
    </div>

    <!-- 配置内容 -->
    <div class="card">
      <!-- 加载 -->
      <div v-if="loading" class="flex items-center justify-center py-12">
        <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
      </div>

      <div v-else class="space-y-6">
        <!-- 网络参数 -->
        <div>
          <h3 class="text-sm font-medium text-gray-300 mb-4">{{ t('page.a71d1f5ac5d1') }}</h3>

          <div class="space-y-4">
            <!-- 首台服务器 -->
            <div class="flex items-start justify-between p-4 bg-dark-card rounded-lg border border-dark-border">
              <div class="flex-1 mr-4">
                <div class="text-sm text-gray-200 font-medium mb-1">{{ t('page.1647bc01c249') }}</div>
                <div class="text-xs text-gray-500 leading-relaxed">
                  {{ t('page.d88fa76aedfb') }}
                </div>
              </div>
              <label class="relative inline-flex items-center cursor-pointer shrink-0 mt-0.5">
                <input type="checkbox" v-model="isFirstServer" class="sr-only peer" />
                <div class="w-10 h-5 bg-gray-600 rounded-full peer peer-checked:bg-primary-500 transition-colors after:content-[''] after:absolute after:top-0.5 after:left-[2px] after:bg-white after:rounded-full after:h-4 after:w-4 after:transition-all peer-checked:after:translate-x-5"></div>
              </label>
            </div>
          </div>
        </div>

        <!-- 保存按钮 -->
        <div class="flex items-center justify-end pt-4 border-t border-dark-border">
          <button
            @click="handleSave"
            :disabled="saving"
            class="btn btn-sm btn-primary flex items-center gap-1.5"
          >
            <svg v-if="!saving" class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M5 13l4 4L19 7" />
            </svg>
            <span v-if="saving" class="w-3.5 h-3.5 border-2 border-gray-900 border-t-transparent rounded-full animate-spin"></span>
            {{ saving ? t('page.917b1c1f18d0') : t('plugins.saveConfig') }}
          </button>
        </div>
      </div>
    </div>

    <!-- 环境变量 -->
    <div class="card">
      <div class="flex items-center justify-between mb-4">
        <div class="flex items-center gap-3">
          <h3 class="text-sm font-medium text-gray-300">{{ t('page.ae27b474ea4d') }}</h3>
          <span class="text-xs text-gray-500">{{ t('page.76e547a8fa54') }} {{ envTotal }} {{ t('page.3a116815bc9d') }}</span>
        </div>
      </div>
      <ListToolbar v-model:page="envPage" v-model:search="envFilter" :total="envTotal" :busy="envLoading" @refresh="loadEnv" />

      <!-- 加载 -->
      <div v-if="envLoading" class="flex items-center justify-center py-12">
        <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
      </div>

      <!-- 环境变量表格 -->
      <div v-else-if="envItems.length > 0" class="overflow-x-auto max-h-[500px] overflow-y-auto">
        <table class="w-full text-sm">
          <thead class="sticky top-0 z-10">
            <tr class="text-left text-gray-400 border-b border-dark-border bg-dark-card">
              <th class="py-2.5 font-medium w-1/3">{{ t('page.9d9d763731b4') }}</th>
              <th class="py-2.5 font-medium">{{ t('common.value') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="item in envItems"
              :key="item.key"
              class="border-b border-dark-border/50 hover:bg-white/[0.02]"
            >
              <td class="py-2 pr-4 text-gray-200 font-mono text-xs break-all">{{ item.key }}</td>
              <td class="py-2 text-gray-400 font-mono text-xs break-all">{{ item.value }}</td>
            </tr>
          </tbody>
        </table>
      </div>

      <!-- 空状态 -->
      <div v-else class="text-center py-8 text-gray-500 text-sm">
        {{ envFilter ? t('page.80aec9cea2b3') : t('page.37021d4e1468') }}
      </div>
    </div>

    <!-- config.ly 示例 -->
    <div class="card">
      <h3 class="text-sm font-medium text-gray-300 mb-3">{{ t('page.0541d0e213bd') }}</h3>
      <pre class="bg-dark-card rounded-lg border border-dark-border p-4 text-xs text-gray-400 font-mono leading-relaxed overflow-x-auto">Param {
    IsFirstServer = true
}</pre>
    </div>
  </div>
</template>
