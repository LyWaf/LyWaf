<script setup lang="ts">
import { t } from '@/i18n'
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
  catch { error.value = t('settings.failed.to.load.two.factor.authentication.status') }
})
async function submit() {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try {
    const action = enabled.value ? 'disable' : secret.value ? 'confirm' : 'setup'
    const result = await authApi.mfaAction(action, password.value, code.value.trim())
    if (!result.success) { error.value = result.message || t('settings.operation.failed'); return }
    if (result.enrollment) secret.value = result.enrollment.secret
    else {
      recovery.value = result.recoveryCodes || []
      changed.value = true
      password.value = ''
      code.value = ''
      secret.value = ''
      logout()
    }
  } catch { error.value = t('settings.operation.failed.try.again.later') }
  finally { busy.value = false }
}
</script>

<template>
  <section class="border-t border-dark-border p-5 space-y-4">
    <h3 class="text-gray-200 font-medium">{{ t('settings.two.factor.authentication') }} <span class="text-sm text-gray-400">{{ enabled ? t('common.enabled') : t('settings.not.enabled') }}</span></h3>
    <p v-if="error" role="alert" class="text-red-400">{{ error }}</p>
    <form v-if="!changed" class="max-w-lg space-y-3" @submit.prevent="submit">
      <label class="block text-sm text-gray-300">{{ t('settings.current.password') }}
        <input v-model="password" type="password" required autocomplete="current-password" maxlength="1024"
          class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" />
      </label>
      <div v-if="secret" class="space-y-2">
        <label class="block text-sm text-gray-300">{{ t('settings.authenticator.setup.key') }}</label>
        <code class="block break-all select-all text-gray-200">{{ secret }}</code>
      </div>
      <label v-if="enabled || secret" class="block text-sm text-gray-300">{{ t('settings.authentication.code') }}{{ enabled ? t('settings..recovery.code') : '' }}
        <input v-model="code" required autocomplete="one-time-code" maxlength="64"
          class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" />
      </label>
      <button :disabled="busy" class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2 disabled:opacity-50">
        {{ busy ? t('settings.processing') : enabled ? t('settings.remove.authenticator') : secret ? t('settings.confirm.authenticator') : t('settings.set.up.authenticator') }}
      </button>
    </form>
    <div v-else class="space-y-3">
      <template v-if="recovery.length">
        <h4 class="text-gray-200">{{ t('settings.recovery.codes.shown.once') }}</h4>
        <pre class="text-sm text-gray-300 whitespace-pre-wrap break-all select-all">{{ recovery.join('\n') }}</pre>
      </template>
      <p class="text-gray-400">{{ t('settings.authentication.settings.updated.previous.sessions.are.no.longer.valid') }}</p>
      <button class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2" @click="router.push('/login')">{{ t('settings.sign.in.again') }}</button>
    </div>
  </section>
</template>
