<script setup lang="ts">
import { t, formatDateTime } from '@/i18n'
import { onMounted, ref } from 'vue'
import { notificationApi } from '@/api'
import ListToolbar from '@/components/common/ListToolbar.vue'
const channels = ref<any[]>([])
const deliveries = ref<any[]>([])
const form = ref<any>(null)
const busy = ref(false)
const error = ref('')
const silence = ref('')
const channelPage = ref(1)
const channelSearch = ref('')
const channelTotal = ref(0)
const deliveryPage = ref(1)
const deliverySearch = ref('')
const deliveryTotal = ref(0)
const loading = ref(false)
let listRequest = 0
async function load() {
  const request = ++listRequest
  loading.value = true
  try {
    const [channelResult, deliveryResult] = await Promise.all([
      notificationApi.channels(channelPage.value, channelSearch.value),
      notificationApi.deliveries(deliveryPage.value, deliverySearch.value),
    ])
    if (request !== listRequest) return
    channels.value = channelResult.data; channelTotal.value = channelResult.total
    deliveries.value = deliveryResult.data; deliveryTotal.value = deliveryResult.total
  } catch { if (request === listRequest) error.value = t('ui.failed.to.load.notification.settings') }
  finally { if (request === listRequest) loading.value = false }
}
function edit(channel?: any) {
  form.value = channel ? { ...channel } : { id: '', name: '', kind: 'webhook', destination: '', enabled: true, minCount: 1, mergeSeconds: 60, smtpPort: 587 }
  silence.value = channel?.silencedUntil ? new Date(new Date(channel.silencedUntil).getTime() - new Date().getTimezoneOffset() * 60000).toISOString().slice(0, 16) : ''
}
async function save() {
  busy.value = true; error.value = ''
  try {
    const result = await notificationApi.save({ ...form.value, silencedUntil: silence.value ? new Date(silence.value).toISOString() : null })
    if (!result.success) error.value = result.message || t('ui.save.failed')
    else { form.value = null; await load() }
  } catch (e: any) { error.value = e.message || t('ui.save.failed') }
  finally { busy.value = false }
}
async function remove(id: string) {
  if (!window.confirm(t('ui.delete.this.notification.channel'))) return
  busy.value = true
  try { const result = await notificationApi.remove(id); if (!result.success) error.value = result.message || t('dashboard.delete.failed'); await load() }
  catch (e: any) { error.value = e.message || t('dashboard.delete.failed') }
  finally { busy.value = false }
}
async function retry(id: string) {
  busy.value = true
  try { const result = await notificationApi.retry(id); if (!result.success) error.value = result.message || t('ui.retry.unavailable'); await load() }
  catch (e: any) { error.value = e.message || t('ui.retry.failed') }
  finally { busy.value = false }
}
onMounted(load)
</script>
<template>
  <div class="space-y-5">
    <header class="flex items-center justify-between"><h1 class="text-xl font-semibold"> {{ t('nav.notifications') }} </h1><div class="flex gap-3"><button class="bg-dark-card rounded-lg px-4 py-2" @click="load"> {{ t('common.refresh') }} </button><button class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2" @click="edit()"> {{ t('ui.add.channel') }} </button></div></header>
    <p v-if="error" role="alert" class="text-red-400">{{ error }}</p>
    <ListToolbar v-model:page="channelPage" v-model:search="channelSearch" :total="channelTotal" :busy="loading" @refresh="load" />
    <div class="overflow-auto"><table class="w-full text-left text-sm"><thead class="text-gray-400"><tr><th class="p-3"> {{ t('ui.channel') }} </th><th> {{ t('settings.type') }} </th><th> {{ t('ui.alert.condition') }} </th><th> {{ t('ui.silenced.until') }} </th><th> {{ t('settings.actions') }} </th></tr></thead><tbody>
      <tr v-for="channel in channels" :key="channel.id" class="border-t border-dark-border"><td class="p-3">{{ channel.name }}<span class="text-gray-500"> {{ channel.enabled ? '' : t('common.disabled') }}</span></td><td>{{ channel.kind }}</td><td>{{ t('ui.alertCondition', { count: channel.minCount, seconds: channel.mergeSeconds }) }}</td><td>{{ channel.silencedUntil ? formatDateTime(channel.silencedUntil) : t('ui.none') }}</td><td class="space-x-3"><button class="text-primary-400" @click="edit(channel)"> {{ t('common.edit') }} </button><button :disabled="busy" class="text-red-400" @click="remove(channel.id)"> {{ t('common.delete') }} </button></td></tr>
    </tbody></table></div>
    <h2 class="font-medium"> {{ t('ui.delivery.history') }} </h2>
    <ListToolbar v-model:page="deliveryPage" v-model:search="deliverySearch" :total="deliveryTotal" :busy="loading" @refresh="load" />
    <div class="overflow-auto"><table class="w-full text-left text-sm"><thead class="text-gray-400"><tr><th class="p-3"> {{ t('ui.channel.rule') }} </th><th> {{ t('settings.status') }} </th><th> {{ t('ui.attempts') }} </th><th> {{ t('common.error') }} </th><th> {{ t('settings.actions') }} </th></tr></thead><tbody>
      <tr v-for="delivery in deliveries" :key="delivery.id" class="border-t border-dark-border"><td class="p-3">{{ delivery.channelId }}<div class="text-gray-400">{{ delivery.message.ruleId }}</div></td><td>{{ delivery.state }}</td><td>{{ delivery.attempts }}</td><td>{{ delivery.error || '-' }}</td><td><button v-if="delivery.state === 'failed'" :disabled="busy" class="text-primary-400" @click="retry(delivery.id)"> {{ t('ui.retry') }} </button></td></tr>
      <tr v-if="!deliveries.length"><td colspan="5" class="text-center text-gray-500 py-10"> {{ t('ui.no.deliveries') }} </td></tr>
    </tbody></table></div>
    <div v-if="form" class="fixed inset-0 z-50 bg-black/60 flex items-center justify-center p-4">
      <form role="dialog" aria-modal="true" :aria-label="t('ui.notification.channel')" class="w-full max-w-xl max-h-[90vh] overflow-auto bg-dark-card border border-dark-border rounded-lg p-5 space-y-3" @submit.prevent="save">
        <h2 class="font-semibold"> {{ t('ui.notification.channel') }} </h2><p v-if="error" class="text-red-400" role="alert">{{ error }}</p>
        <label class="block text-sm"> {{ t('ui.channel.id') }} <input v-model="form.id" required maxlength="100" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="block text-sm"> {{ t('fields.Name') }} <input v-model="form.name" required maxlength="100" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="block text-sm"> {{ t('settings.type') }} <select v-model="form.kind" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2"><option value="webhook">Webhook</option><option value="smtp">SMTP</option><option value="plugin"> {{ t('ui.plugin') }} </option></select></label>
        <label class="block text-sm">{{ form.kind === 'webhook' ? t('ui.https.url') : form.kind === 'smtp' ? t('ui.recipient') : t('ui.plugin.id') }}<input v-model="form.destination" required class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <template v-if="form.kind === 'smtp'">
          <label class="block text-sm"> {{ t('ui.smtp.host') }} <input v-model="form.smtpHost" required class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
          <label class="block text-sm"> {{ t('fields.Port') }} <input v-model.number="form.smtpPort" type="number" min="1" max="65535" required class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
          <label class="block text-sm"> {{ t('ui.sender') }} <input v-model="form.sender" type="email" required class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
          <label class="block text-sm"> {{ t('login.username') }} <input v-model="form.smtpUser" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
          <label class="block text-sm"> {{ t('settings.new.password') }} <input v-model="form.password" type="password" autocomplete="new-password" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        </template>
        <div class="grid grid-cols-2 gap-3"><label class="text-sm"> {{ t('ui.count.threshold') }} <input v-model.number="form.minCount" type="number" min="1" required class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label><label class="text-sm"> {{ t('ui.merge.interval.seconds') }} <input v-model.number="form.mergeSeconds" type="number" min="1" max="86400" required class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label></div>
        <label class="block text-sm"> {{ t('ui.site.filter') }} <input v-model="form.site" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="block text-sm"> {{ t('ui.attack.type.filter') }} <input v-model="form.attackType" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="block text-sm"> {{ t('ui.silenced.until') }} <input v-model="silence" type="datetime-local" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="flex gap-2"><input v-model="form.enabled" type="checkbox" /> {{ t('fields.Enabled') }} </label>
        <div class="flex justify-end gap-3"><button type="button" :disabled="busy" @click="form = null"> {{ t('common.cancel') }} </button><button :disabled="busy" class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2"> {{ t('common.save') }} </button></div>
      </form>
    </div>
  </div>
</template>
