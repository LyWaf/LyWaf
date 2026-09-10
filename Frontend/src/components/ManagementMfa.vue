<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { authApi } from '@/api'
import { useAuth } from '@/composables/useAuth'

const enabled = ref(false)
const busy = ref(false)
const password = ref('')
const code = ref('')
const secret = ref('')
const recovery = ref<string[]>([])
const error = ref('')
const changed = ref(false)
const router = useRouter()
const { logout } = useAuth()
onMounted(async () => {
  try { enabled.value = (await authApi.mfaStatus()).enabled }
  catch { error.value = '无法读取双因素认证状态' }
})
async function submit() {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try {
    const action = enabled.value ? 'disable' : secret.value ? 'confirm' : 'setup'
    const result = await authApi.mfaAction(action, password.value, code.value.trim())
    if (!result.success) { error.value = result.message || '操作失败'; return }
    if (result.enrollment) secret.value = result.enrollment.secret
    else {
      recovery.value = result.recoveryCodes || []
      changed.value = true
      password.value = ''
      code.value = ''
      secret.value = ''
      logout()
    }
  } catch { error.value = '操作失败，请稍后重试' }
  finally { busy.value = false }
}
</script>

<template>
  <section class="border-t border-dark-border p-5 space-y-4">
    <h3 class="text-gray-200 font-medium">双因素认证 <span class="text-sm text-gray-400">{{ enabled ? '已启用' : '未启用' }}</span></h3>
    <p v-if="error" role="alert" class="text-red-400">{{ error }}</p>
    <form v-if="!changed" class="max-w-lg space-y-3" @submit.prevent="submit">
      <label class="block text-sm text-gray-300">当前密码
        <input v-model="password" type="password" required autocomplete="current-password" maxlength="1024"
          class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" />
      </label>
      <div v-if="secret" class="space-y-2">
        <label class="block text-sm text-gray-300">验证器绑定密钥</label>
        <code class="block break-all select-all text-gray-200">{{ secret }}</code>
      </div>
      <label v-if="enabled || secret" class="block text-sm text-gray-300">动态码{{ enabled ? ' / 恢复码' : '' }}
        <input v-model="code" required autocomplete="one-time-code" maxlength="64"
          class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" />
      </label>
      <button :disabled="busy" class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2 disabled:opacity-50">
        {{ busy ? '处理中...' : enabled ? '解绑验证器' : secret ? '确认绑定' : '绑定验证器' }}
      </button>
    </form>
    <div v-else class="space-y-3">
      <template v-if="recovery.length">
        <h4 class="text-gray-200">恢复码（仅显示一次）</h4>
        <pre class="text-sm text-gray-300 whitespace-pre-wrap break-all select-all">{{ recovery.join('\n') }}</pre>
      </template>
      <p class="text-gray-400">认证设置已更新，旧会话已失效。</p>
      <button class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2" @click="router.push('/login')">重新登录</button>
    </div>
  </section>
</template>
