<script setup lang="ts">
import { ref, computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuth } from '@/composables/useAuth'
import { authApi } from '@/api'
import { useToast } from '@/composables/useToast'

const route = useRoute()
const router = useRouter()
const { username, logout } = useAuth()
const { showError, showSuccess } = useToast()

const breadcrumbs = computed(() => {
  const items = [{ name: 'LyWaf', path: '/' }]
  if (route.meta.title) {
    items.push({
      name: route.meta.title as string,
      path: route.path
    })
  }
  return items
})

const goTo = (path: string) => {
  router.push(path)
}

const handleLogout = () => {
  logout()
  router.push('/login')
}

// ========== 修改密码弹窗 ==========
const showPasswordModal = ref(false)
const currentPassword = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const changingPassword = ref(false)

const openPasswordModal = () => {
  currentPassword.value = ''
  newPassword.value = ''
  confirmPassword.value = ''
  showPasswordModal.value = true
}

const closePasswordModal = () => {
  showPasswordModal.value = false
}

const handleChangePassword = async () => {
  if (!currentPassword.value) {
    showError('请输入当前密码')
    return
  }
  if (!newPassword.value || newPassword.value.length < 6) {
    showError('新密码长度至少 6 位')
    return
  }
  if (newPassword.value !== confirmPassword.value) {
    showError('两次输入的新密码不一致')
    return
  }

  changingPassword.value = true
  try {
    // api 拦截器已解包：返回值就是 { success, message }，400 也走 resolve
    const res = await authApi.changePassword(currentPassword.value, newPassword.value) as unknown as { success: boolean; message?: string }
    if (res?.success) {
      showSuccess('密码修改成功，请重新登录')
      closePasswordModal()
      logout()
      router.push('/login')
    } else {
      showError(res?.message || '密码修改失败')
    }
  } catch (e: any) {
    // 仅网络错误或 401（拦截器 reject）会到这里
    showError('密码修改失败，请检查网络连接')
  } finally {
    changingPassword.value = false
  }
}
</script>

<template>
  <header class="h-14 bg-dark-bg-secondary border-b border-dark-border px-6 flex items-center justify-between sticky top-0 z-40">
    <!-- 面包屑 -->
    <nav class="flex items-center gap-2 text-sm">
      <template v-for="(item, index) in breadcrumbs" :key="item.path">
        <span v-if="index > 0" class="text-gray-600">›</span>
        <span
          v-if="index === breadcrumbs.length - 1"
          class="text-gray-100 font-medium"
        >
          {{ item.name }}
        </span>
        <a
          v-else
          @click="goTo(item.path)"
          class="text-gray-400 hover:text-primary-500 cursor-pointer transition-colors"
        >
          {{ item.name }}
        </a>
      </template>
    </nav>

    <!-- 右侧状态 -->
    <div class="flex items-center gap-4">
      <span v-if="username" class="text-sm text-gray-400">{{ username }}</span>
      <button
        @click="openPasswordModal"
        class="text-xs text-gray-500 hover:text-gray-300 transition-colors px-2 py-1 rounded hover:bg-white/5"
      >
        修改密码
      </button>
      <button
        @click="handleLogout"
        class="text-xs text-gray-500 hover:text-gray-300 transition-colors px-2 py-1 rounded hover:bg-white/5"
      >
        退出登录
      </button>
      <div class="flex items-center gap-2 text-sm border-l border-dark-border pl-4">
        <span class="w-2 h-2 rounded-full bg-green-500 animate-pulse-dot"></span>
        <span class="text-gray-300">运行中</span>
      </div>
    </div>
  </header>

  <!-- 修改密码弹窗 -->
  <Teleport to="body">
    <div
      v-if="showPasswordModal"
      class="fixed inset-0 z-50 flex items-center justify-center"
    >
      <!-- 遮罩 -->
      <div class="absolute inset-0 bg-black/60" @click="closePasswordModal"></div>
      <!-- 弹窗 -->
      <div class="relative bg-dark-card border border-dark-border rounded-2xl w-full max-w-sm p-6 shadow-2xl">
        <h3 class="text-lg font-semibold text-gray-100 mb-5">修改密码</h3>

        <form @submit.prevent="handleChangePassword" class="space-y-4">
          <div>
            <label class="block text-sm text-gray-400 mb-1">当前密码</label>
            <input
              v-model="currentPassword"
              type="password"
              autocomplete="current-password"
              class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 placeholder-gray-600 focus:outline-none focus:ring-1 focus:ring-primary-500 focus:border-primary-500 transition-colors"
              placeholder="请输入当前密码"
            />
          </div>
          <div>
            <label class="block text-sm text-gray-400 mb-1">新密码</label>
            <input
              v-model="newPassword"
              type="password"
              autocomplete="new-password"
              class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 placeholder-gray-600 focus:outline-none focus:ring-1 focus:ring-primary-500 focus:border-primary-500 transition-colors"
              placeholder="至少 6 位"
            />
          </div>
          <div>
            <label class="block text-sm text-gray-400 mb-1">确认新密码</label>
            <input
              v-model="confirmPassword"
              type="password"
              autocomplete="new-password"
              class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 placeholder-gray-600 focus:outline-none focus:ring-1 focus:ring-primary-500 focus:border-primary-500 transition-colors"
              placeholder="再次输入新密码"
            />
          </div>

          <div class="flex gap-3 pt-2">
            <button
              type="button"
              @click="closePasswordModal"
              class="flex-1 py-2 rounded-lg text-sm text-gray-400 border border-dark-border hover:bg-white/5 transition-colors"
            >
              取消
            </button>
            <button
              type="submit"
              :disabled="changingPassword"
              :class="[
                'flex-1 py-2 rounded-lg text-sm font-medium transition-all duration-200',
                changingPassword
                  ? 'bg-primary-500/50 text-gray-300 cursor-wait'
                  : 'bg-primary-500 text-gray-900 hover:bg-primary-400'
              ]"
            >
              <span v-if="changingPassword">提交中...</span>
              <span v-else>确认修改</span>
            </button>
          </div>
        </form>
      </div>
    </div>
  </Teleport>
</template>
