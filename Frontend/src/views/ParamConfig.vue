<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
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

const filteredEnvItems = computed(() => {
  if (!envFilter.value) return envItems.value
  const q = envFilter.value.toLowerCase()
  return envItems.value.filter(
    item => item.key.toLowerCase().includes(q) || item.value.toLowerCase().includes(q)
  )
})

const loadConfig = async () => {
  loading.value = true
  try {
    const res = await paramApi.get() as any
    if (res?.success) {
      isFirstServer.value = res.isFirstServer
    }
  } catch {
    showError('加载参数配置失败')
  } finally {
    loading.value = false
  }
}

const loadEnv = async () => {
  envLoading.value = true
  try {
    const res = await paramApi.getEnv() as any
    if (res?.success) {
      envItems.value = res.items
    }
  } catch {
    showError('加载环境变量失败')
  } finally {
    envLoading.value = false
  }
}

const handleSave = async () => {
  saving.value = true
  try {
    const res = await paramApi.update({ isFirstServer: isFirstServer.value }) as any
    if (res?.success) {
      showSuccess(res.message || '参数配置已更新')
    } else {
      showError(res?.message || '更新失败')
    }
  } catch {
    showError('保存参数配置失败')
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
        <h2 class="text-lg font-semibold text-gray-100">参数配置</h2>
        <span class="text-xs text-gray-500">运行时参数调整，部分选项也可在 config.ly 中通过 Param 块配置</span>
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
          <h3 class="text-sm font-medium text-gray-300 mb-4">网络参数</h3>

          <div class="space-y-4">
            <!-- 首台服务器 -->
            <div class="flex items-start justify-between p-4 bg-dark-card rounded-lg border border-dark-border">
              <div class="flex-1 mr-4">
                <div class="text-sm text-gray-200 font-medium mb-1">当前为首台服务器</div>
                <div class="text-xs text-gray-500 leading-relaxed">
                  启用后，获取客户端 IP 时将直接使用连接地址（RemoteIpAddress），忽略 X-Forwarded-For、X-Real-IP 等代理头。
                  适用于 LyWaf 直接面对客户端、前面没有其他反向代理的部署场景。未启用时将优先从代理头中读取客户端真实 IP。
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
            {{ saving ? '保存中...' : '保存配置' }}
          </button>
        </div>
      </div>
    </div>

    <!-- 环境变量 -->
    <div class="card">
      <div class="flex items-center justify-between mb-4">
        <div class="flex items-center gap-3">
          <h3 class="text-sm font-medium text-gray-300">环境变量</h3>
          <span class="text-xs text-gray-500">共 {{ envItems.length }} 个</span>
        </div>
        <div class="flex items-center gap-2">
          <input
            v-model="envFilter"
            type="text"
            class="input text-xs !py-1.5 !px-3 w-56"
            placeholder="搜索变量名或值..."
          />
          <button @click="loadEnv" class="btn btn-sm btn-primary flex items-center gap-1.5">
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
            </svg>
            刷新
          </button>
        </div>
      </div>

      <!-- 加载 -->
      <div v-if="envLoading" class="flex items-center justify-center py-12">
        <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
      </div>

      <!-- 环境变量表格 -->
      <div v-else-if="filteredEnvItems.length > 0" class="overflow-x-auto max-h-[500px] overflow-y-auto">
        <table class="w-full text-sm">
          <thead class="sticky top-0 z-10">
            <tr class="text-left text-gray-400 border-b border-dark-border bg-dark-card">
              <th class="py-2.5 font-medium w-1/3">变量名</th>
              <th class="py-2.5 font-medium">值</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="item in filteredEnvItems"
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
        {{ envFilter ? '没有匹配的环境变量' : '暂无环境变量' }}
      </div>
    </div>

    <!-- config.ly 示例 -->
    <div class="card">
      <h3 class="text-sm font-medium text-gray-300 mb-3">config.ly 配置示例</h3>
      <pre class="bg-dark-card rounded-lg border border-dark-border p-4 text-xs text-gray-400 font-mono leading-relaxed overflow-x-auto">Param {
    IsFirstServer = true
}</pre>
    </div>
  </div>
</template>
