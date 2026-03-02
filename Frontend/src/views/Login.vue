<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { useRouter } from 'vue-router'
import { authApi } from '@/api'
import { useAuth } from '@/composables/useAuth'
import { useToast } from '@/composables/useToast'
import { sha256 } from '@/utils/crypto'

const router = useRouter()
const { setAuth } = useAuth()
const { showError } = useToast()

const username = ref('')
const password = ref('')
const loading = ref(false)

// ========== 服务端时间同步 ==========
const timeOffset = ref(0) // 服务器时间 - 客户端时间（秒）

const syncServerTime = async () => {
  try {
    const before = Date.now()
    const res = await authApi.time()
    const after = Date.now()
    // 取请求中间时刻作为客户端参考时间
    const clientTimeSec = Math.floor((before + after) / 2 / 1000)
    timeOffset.value = res.timestamp - clientTimeSec
  } catch {
    // 时间同步失败，使用本地时间（偏差较小时仍可登录）
    timeOffset.value = 0
  }
}

/** 获取校准后的服务器时间（Unix 秒） */
const getServerTime = () => Math.floor(Date.now() / 1000) + timeOffset.value

onMounted(() => {
  syncServerTime()
})

// ========== 暴力破解冷却倒计时 ==========
const cooldownSeconds = ref(0)
let cooldownTimer: ReturnType<typeof setInterval> | null = null

const startCooldown = (seconds: number) => {
  cooldownSeconds.value = seconds
  if (cooldownTimer) clearInterval(cooldownTimer)
  cooldownTimer = setInterval(() => {
    cooldownSeconds.value--
    if (cooldownSeconds.value <= 0) {
      cooldownSeconds.value = 0
      if (cooldownTimer) {
        clearInterval(cooldownTimer)
        cooldownTimer = null
      }
    }
  }, 1000)
}

onUnmounted(() => {
  if (cooldownTimer) clearInterval(cooldownTimer)
})

// ========== 登录 ==========
const handleLogin = async () => {
  if (!username.value || !password.value) {
    showError('请输入用户名和密码')
    return
  }
  if (cooldownSeconds.value > 0) {
    showError(`请等待 ${cooldownSeconds.value} 秒后再试`)
    return
  }

  loading.value = true
  try {
    // 1. 计算带时间戳的密码哈希: SHA256(SHA256(password) + timestamp)
    const timestamp = getServerTime()
    const pwdHash = await sha256(password.value)
    const loginHash = await sha256(pwdHash + timestamp.toString())

    // 2. 发送登录请求
    const res = await authApi.login(username.value, loginHash, timestamp)
    if (res.success && res.token && res.username) {
      setAuth(res.token, res.username)
      router.push('/')
    } else {
      if (res.retryAfterSeconds && res.retryAfterSeconds > 0) {
        startCooldown(res.retryAfterSeconds)
      }
      showError(res.message || '登录失败')
    }
  } catch (e: any) {
    // axios 将 4xx 作为 error 抛出，从 response.data 中读取
    const data = e?.response?.data
    if (data?.retryAfterSeconds && data.retryAfterSeconds > 0) {
      startCooldown(data.retryAfterSeconds)
      showError(data.message || '登录失败次数过多，请稍后再试')
    } else {
      showError(data?.message || '登录失败，请检查网络连接')
    }
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="min-h-screen bg-dark-bg flex items-center justify-center px-4">
    <div class="w-full max-w-sm">
      <!-- Logo -->
      <div class="flex flex-col items-center mb-8">
        <div class="w-14 h-14 rounded-2xl bg-gradient-to-br from-primary-500 to-primary-700 flex items-center justify-center text-2xl font-bold text-gray-900 mb-4 shadow-lg shadow-primary-500/20">
          W
        </div>
        <h1 class="text-2xl font-bold text-gray-100">LyWaf 控制台</h1>
        <p class="text-sm text-gray-500 mt-1">请登录以继续</p>
      </div>

      <!-- 登录表单 -->
      <form @submit.prevent="handleLogin" class="bg-dark-card border border-dark-border rounded-2xl p-6 space-y-5 shadow-2xl">
        <div>
          <label class="block text-sm font-medium text-gray-300 mb-1.5">用户名</label>
          <input
            v-model="username"
            type="text"
            autocomplete="username"
            autofocus
            class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-4 py-2.5 text-sm text-gray-200 placeholder-gray-600 focus:outline-none focus:ring-1 focus:ring-primary-500 focus:border-primary-500 transition-colors"
            placeholder="请输入用户名"
          />
        </div>
        <div>
          <label class="block text-sm font-medium text-gray-300 mb-1.5">密码</label>
          <input
            v-model="password"
            type="password"
            autocomplete="current-password"
            class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-4 py-2.5 text-sm text-gray-200 placeholder-gray-600 focus:outline-none focus:ring-1 focus:ring-primary-500 focus:border-primary-500 transition-colors"
            placeholder="请输入密码"
          />
        </div>

        <!-- 冷却倒计时提示 -->
        <div
          v-if="cooldownSeconds > 0"
          class="flex items-center gap-2 px-3 py-2 rounded-lg bg-red-500/10 border border-red-500/20 text-red-400 text-sm"
        >
          <span class="w-4 h-4 border-2 border-red-400 border-t-transparent rounded-full animate-spin"></span>
          <span>登录已锁定，请 <strong>{{ cooldownSeconds }}</strong> 秒后再试</span>
        </div>

        <button
          type="submit"
          :disabled="loading || cooldownSeconds > 0"
          :class="[
            'w-full py-2.5 rounded-lg text-sm font-medium transition-all duration-200',
            loading || cooldownSeconds > 0
              ? 'bg-primary-500/50 text-gray-300 cursor-wait'
              : 'bg-primary-500 text-gray-900 hover:bg-primary-400 active:scale-[0.98]'
          ]"
        >
          <span v-if="loading" class="inline-flex items-center gap-2">
            <span class="w-4 h-4 border-2 border-current border-t-transparent rounded-full animate-spin"></span>
            登录中...
          </span>
          <span v-else-if="cooldownSeconds > 0">请等待 {{ cooldownSeconds }}s</span>
          <span v-else>登 录</span>
        </button>
      </form>

      <p class="text-center text-xs text-gray-600 mt-6">LyWaf Web Application Firewall</p>
    </div>
  </div>
</template>
