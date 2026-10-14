<script setup lang="ts">
import { t } from '@/i18n'
import axios from 'axios'
import { ref, watch } from 'vue'
import { stepUpOpen, finishStepUp } from '@/composables/useStepUp'
const password = ref('')
const code = ref('')
const error = ref('')
const busy = ref(false)
watch(stepUpOpen, () => { password.value = ''; code.value = ''; error.value = '' })
async function verify() {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try {
    const { data } = await axios.post('/api/auth/mfa/step-up', { password: password.value, code: code.value.trim() }, {
      timeout: 15000, headers: { Authorization: `Bearer ${localStorage.getItem('lywaf_token')}` }
    })
    if (data.success && data.proof) finishStepUp(data.proof)
    else error.value = data.message || t('page.105f55aa144e')
  } catch (e: any) { error.value = e.response?.data?.message || t('page.cfa466946898') }
  finally { busy.value = false }
}
</script>
<template>
  <div v-if="stepUpOpen" class="fixed inset-0 z-[100] bg-black/60 flex items-center justify-center p-4" @keydown.esc="!busy && finishStepUp()">
    <form role="dialog" aria-modal="true" aria-labelledby="step-up-title" class="w-full max-w-md bg-dark-card border border-dark-border rounded-lg p-5 space-y-4" @submit.prevent="verify">
      <h2 id="step-up-title" class="text-lg text-gray-200">{{ t('page.36fc20640974') }}</h2>
      <p v-if="error" role="alert" class="text-red-400">{{ error }}</p>
      <label class="block text-sm text-gray-300">{{ t('settings.current.password') }}
        <input v-model="password" required type="password" autocomplete="current-password" maxlength="1024" autofocus class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" />
      </label>
      <label class="block text-sm text-gray-300">{{ t('page.8fdc70da03b4') }}
        <input v-model="code" autocomplete="one-time-code" maxlength="64" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" />
      </label>
      <div class="flex justify-end gap-3">
        <button type="button" :disabled="busy" class="px-4 py-2 text-gray-300" @click="finishStepUp()">{{ t('common.cancel') }}</button>
        <button :disabled="busy" class="px-4 py-2 bg-primary-500 text-gray-900 rounded-lg disabled:opacity-50">{{ busy ? t('page.6c128e0ad353') : t('page.64ded447c5bd') }}</button>
      </div>
    </form>
  </div>
</template>
