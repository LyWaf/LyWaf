<script setup lang="ts">
import { t } from '@/i18n'
import { computed, onMounted, ref } from 'vue'
import { siteApi } from '@/api'
const props = defineProps<{ clusterId: string }>()
const emit = defineEmits<{ close: []; saved: [] }>()
const form = ref<any>(null)
const error = ref('')
const busy = ref(false)
const numbers = computed(() => ([{ key: 'intervalSeconds', label: t('fields.IntervalSeconds'), max: 3600 }, { key: 'timeoutSeconds', label: t('fields.TimeoutSeconds'), max: 300 }, { key: 'passes', label: t('page.873d6dc76750'), max: 100 }, { key: 'fails', label: t('page.4d27335b65ba'), max: 100 }]))
onMounted(async () => {
  try {
    const result = await siteApi.health(props.clusterId)
    if (!result.success) throw new Error(result.message || t('page.6aa27f14bff1'))
    form.value = result.data
  } catch (e: any) { error.value = e.message || t('page.6aa27f14bff1') }
})
async function save() {
  busy.value = true; error.value = ''
  try {
    const result = await siteApi.saveHealth(props.clusterId, form.value)
    if (!result.success) throw new Error(result.message || t('ui.save.failed'))
    emit('saved')
  } catch (e: any) { error.value = e.message || t('ui.save.failed') }
  finally { busy.value = false }
}
</script>
<template>
  <div class="fixed inset-0 z-50 bg-black/60 flex items-center justify-center p-4">
    <form role="dialog" aria-modal="true" :aria-label="t('page.0cd80729c1f6')" class="health-form bg-dark-card rounded-lg border border-dark-border w-full max-w-xl max-h-[90vh] overflow-auto p-5 space-y-4" @submit.prevent="save">
      <h2 class="font-semibold break-all">{{ t('page.10d9bb2542c4') }} {{ clusterId }}{{ t('page.472ed3ee3a9e') }}</h2>
      <p v-if="error" role="alert" class="text-red-400 break-all">{{ error }}</p>
      <p v-if="!form && !error" role="status">{{ t('common.loading') }}</p>
      <template v-if="form">
        <label><input v-model="form.enabled" type="checkbox" /> {{ t('page.072cb5b70d3f') }}</label>
        <label>{{ t('page.fc12218cc64a') }}<input v-model="form.path" required maxlength="2048" placeholder="/health" /></label>
        <label>{{ t('fields.Method') }}<select v-model="form.method"><option v-for="method in ['GET','HEAD','POST','PUT','PATCH','DELETE','OPTIONS']" :key="method">{{ method }}</option></select></label>
        <label>{{ t('page.05e5c3bbcfeb') }}<select v-model="form.contentType"><option>application/json</option><option>application/x-www-form-urlencoded</option><option>text/plain</option></select></label>
        <label>{{ t('page.375324013140') }}<textarea v-model="form.body" rows="4" maxlength="65536" spellcheck="false" /></label>
        <label>{{ t('page.744651a63746') }}<input v-model="form.statusCodes" required maxlength="1024" pattern="[1-5xX][0-9xX]{2}(,[1-5xX][0-9xX]{2})*" placeholder="200,20x,30x" /></label>
        <div class="grid grid-cols-2 gap-3"><label v-for="field in numbers" :key="field.key">{{ field.label }}<input v-model.number="form[field.key]" type="number" required min="1" :max="field.max" /></label></div>
      </template>
      <footer class="flex justify-end gap-3"><button type="button" :disabled="busy" @click="emit('close')">{{ t('common.cancel') }}</button><button :disabled="busy || !form" class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2 disabled:opacity-50">{{ busy ? t('page.917b1c1f18d0') : t('common.save') }}</button></footer>
    </form>
  </div>
</template>
<style scoped>
.health-form label { display: block; font-size: 14px; min-width: 0; }
.health-form input:not([type=checkbox]), .health-form textarea, .health-form select { display: block; width: 100%; margin-top: 4px; padding: 8px; border: 1px solid #374151; border-radius: 8px; background: #141b26; color: #e5e7eb; }
</style>
