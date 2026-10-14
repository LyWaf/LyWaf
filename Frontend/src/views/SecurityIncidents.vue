<script setup lang="ts">
import { locale } from '@/i18n'
import { t } from '@/i18n'
import { computed, onMounted, reactive, ref } from 'vue'
import { securityIncidentApi } from '@/api'
import { useAuth } from '@/composables/useAuth'
import DateTimePicker from '@/components/common/DateTimePicker.vue'
const { can } = useAuth()
const filters = reactive({ site: '', rule: '', ip: '', attackType: '', state: '', from: '', to: '' })
const page = ref(1)
const total = ref(0)
const dropped = ref(0)
const events = ref<any[]>([])
const detail = ref<any>(null)
const loading = ref(false)
const busy = ref(false)
const error = ref('')
const state = ref(0)
const note = ref('')
const requests = ref<any[] | null>(null)
const requestsTotal = ref(0)
const requestsDropped = ref(0)
const nextCursor = ref<string | null>(null)
const states = computed(() => ([t('page.999a459c3f3f'), t('page.d48ba64735c0'), t('page.2ca255d63de1'), t('page.c9bb70bcaaa3')]))
async function load() {
  loading.value = true; error.value = ''
  try {
    const params: Record<string, unknown> = { page: page.value, pageSize: 20 }
    for (const [key, value] of Object.entries(filters)) if (value !== '') params[key] = key === 'from' || key === 'to' ? new Date(value.replace(' ', 'T')).toISOString() : value
    const result = await securityIncidentApi.list(params)
    if (!result.success) error.value = result.message || t('page.d1d044826a45')
    else { events.value = result.data; total.value = result.total; dropped.value = result.dropped }
  } catch { error.value = t('page.50152adea5ae') }
  finally { loading.value = false }
}
async function open(id: string) {
  busy.value = true
  try {
    const result = await securityIncidentApi.detail(id)
    if (!result.success) { error.value = result.message || t('page.51da9cfa7104'); return }
    detail.value = result; state.value = result.data.state; note.value = ''
    requests.value = null; nextCursor.value = null
  } catch { error.value = t('page.19a7ca44a09a') }
  finally { busy.value = false }
}
async function disposition() {
  busy.value = true; error.value = ''
  try {
    const result = await securityIncidentApi.disposition(detail.value.data.id, state.value, note.value, detail.value.data.stateVersion)
    if (!result.success) error.value = result.message || t('page.d5eb0ae84c30')
    else { await open(detail.value.data.id); await load() }
  } catch (e: any) { error.value = e.message || t('page.d5eb0ae84c30') }
  finally { busy.value = false }
}
async function loadRequests(cursor?: string | null) {
  if (!detail.value || busy.value) return
  const id = detail.value.data.id
  busy.value = true
  try {
    const result = await securityIncidentApi.requests(id, cursor)
    if (!result.success) throw new Error(result.message || t('page.fb491388c80d'))
    if (detail.value?.data.id !== id) return
    requests.value = result.data; requestsTotal.value = result.total; requestsDropped.value = result.dropped; nextCursor.value = result.nextCursor
  } catch (e: any) { error.value = e.message || t('page.fb491388c80d') }
  finally { busy.value = false }
}
async function act(action: string) {
  if (!window.confirm(`${action === 'allow-ip' ? t('page.558e518d0123') : t('dashboard.blocked.ips')}：${detail.value.data.ip}？`)) return
  busy.value = true
  try { const result = await securityIncidentApi.action(detail.value.data.id, action); if (!result.success) error.value = result.message || t('settings.operation.failed') }
  catch (e: any) { error.value = e.message || t('settings.operation.failed') }
  finally { busy.value = false }
}
onMounted(load)
</script>
<template>
  <div class="space-y-5">
    <header class="flex justify-between items-center"><h1 class="text-xl font-semibold">{{ t('nav.incidents') }}</h1><button :disabled="loading" class="px-4 py-2 bg-dark-card rounded-lg" @click="load">{{ loading ? t('ui.refreshing') : t('common.refresh') }}</button></header>
    <p v-if="error" class="text-red-400" role="alert">{{ error }}</p>
    <p v-if="dropped" class="text-amber-400">{{ t('page.b6a874641775') }} {{ dropped }} {{ t('page.52c808ae91ef') }}</p>
    <form class="grid grid-cols-2 lg:grid-cols-4 gap-3" @submit.prevent="page = 1; load()">
      <input v-model="filters.site" :aria-label="t('page.a59fe62777ff')" :placeholder="t('page.a59fe62777ff')" class="min-w-0 bg-dark-card border border-dark-border rounded-lg p-2" />
      <input v-model="filters.rule" :aria-label="t('fields.Rules')" :placeholder="t('fields.Rules')" class="min-w-0 bg-dark-card border border-dark-border rounded-lg p-2" />
      <input v-model="filters.ip" aria-label="IP" placeholder="IP" class="min-w-0 bg-dark-card border border-dark-border rounded-lg p-2" />
      <input v-model="filters.attackType" :aria-label="t('page.713eb94e2557')" :placeholder="t('page.713eb94e2557')" class="min-w-0 bg-dark-card border border-dark-border rounded-lg p-2" />
      <select v-model="filters.state" :aria-label="t('page.9915bfbab463')" class="min-w-0 bg-dark-card border border-dark-border rounded-lg p-2"><option value="">{{ t('ui.all.states') }}</option><option v-for="(label, i) in states" :key="i" :value="String(i)">{{ label }}</option></select>
      <DateTimePicker v-model="filters.from" :placeholder="t('ui.start.time')" class="min-w-0" />
      <DateTimePicker v-model="filters.to" :placeholder="t('ui.end.time')" class="min-w-0" />
      <button class="bg-primary-500 text-gray-900 rounded-lg p-2">{{ t('ui.query') }}</button>
    </form>
    <div class="overflow-auto"><table class="w-full text-sm text-left"><thead class="text-gray-400"><tr><th class="p-3">{{ t('page.51bd1c52e880') }}</th><th>{{ t('page.bd76983f00aa') }}</th><th>{{ t('page.05c518eb94a0') }}</th><th>{{ t('page.3d8f250d0849') }}</th><th>{{ t('settings.status') }}</th><th>{{ t('settings.actions') }}</th></tr></thead><tbody>
      <tr v-for="event in events" :key="event.id" class="border-t border-dark-border"><td class="p-3">{{ event.site }}<div class="text-gray-400">{{ event.ip }}</div></td><td class="break-all">{{ event.ruleId }}<div class="text-gray-400">{{ event.attackType }}</div></td><td>{{ event.count }}</td><td class="whitespace-nowrap">{{ new Date(event.firstSeen).toLocaleString(locale) }}<div>{{ new Date(event.lastSeen).toLocaleString(locale) }}</div></td><td>{{ states[event.state] }}</td><td><button :disabled="busy" class="text-primary-400" @click="open(event.id)">{{ t('page.ce3a82c3c5ce') }}</button></td></tr>
      <tr v-if="!events.length"><td colspan="6" class="text-center text-gray-500 py-12">{{ t('page.7de14a9bf06d') }}</td></tr>
    </tbody></table></div>
    <footer class="flex justify-end gap-4 text-sm"><span>{{ t('page.76e547a8fa54') }} {{ total }} {{ t('page.49ccde43a154') }}</span><button :disabled="page <= 1 || loading" @click="page--; load()">{{ t('common.previous') }}</button><span>{{ page }}</span><button :disabled="page * 20 >= total || loading" @click="page++; load()">{{ t('common.next') }}</button></footer>
    <div v-if="detail" class="fixed inset-0 z-50 bg-black/60 flex items-center justify-center p-4">
      <section role="dialog" aria-modal="true" :aria-label="t('page.575aa4b86a4f')" class="bg-dark-card border border-dark-border rounded-lg w-full max-w-4xl max-h-[90vh] overflow-auto p-5 space-y-4">
        <header class="flex justify-between gap-3"><h2 class="font-semibold break-all">{{ detail.data.ruleId }}</h2><button :aria-label="t('common.close')" @click="detail = null">{{ t('common.close') }}</button></header>
        <p v-if="error" role="alert" class="text-red-400">{{ error }}</p>
        <div class="flex flex-wrap gap-3 text-sm"><span>{{ detail.data.host }}</span><span>{{ detail.data.ip }}</span><span>{{ states[detail.data.state] }}</span>
          <button v-if="can('system.manage')" :disabled="busy" class="text-green-400" @click="act('allow-ip')">{{ t('page.d11504aa3f98') }}</button><button v-if="can('system.manage')" :disabled="busy" class="text-red-400" @click="act('block-ip')">{{ t('dashboard.blocked.ips') }}</button>
          <RouterLink v-if="detail.explanation && can('write')" class="text-primary-400" :to="{ path: '/waf-rules', query: { edit: detail.data.ruleId } }">{{ t('page.2b4f627c68a1') }}</RouterLink>
        </div>
        <h3 class="font-medium">{{ t('page.bbd811c1082d') }}</h3>
        <div v-for="sample in detail.data.samples" :key="sample.requestId" class="border-t border-dark-border py-3 space-y-1 text-sm break-all">
          <div class="font-mono">{{ sample.method }} {{ sample.path }}</div><div class="text-gray-400">{{ new Date(sample.time).toLocaleString(locale) }} · {{ sample.requestId }}</div>
          <div>{{ t('page.eaba2aa91757') }}{{ sample.matchedFields.join(', ') || t('page.7450cc5b16e2') }}</div><div>{{ t('page.0767918bf65d') }}{{ sample.normalizationSteps.join(' → ') || t('page.9d5be237daa9') }}</div>
          <div v-for="location in sample.matchLocations || []" :key="`${location.group}:${location.condition}`">
            {{ t('page.95ec5352a597') }} {{ location.group }} {{ t('page.74f841d0c59a') }} {{ location.condition }}：{{ location.field }}<span v-if="location.fieldName">[{{ location.fieldName }}]</span> · {{ location.operator }}
          </div>
          <div>{{ t('page.8e02684eeca5') }}{{ sample.queryKeys.join(', ') || t('ui.none') }}</div><div v-if="sample.bodyTruncated" class="text-amber-400">{{ t('page.d2562ba9da11') }}</div>
        </div>
        <div class="border-t border-dark-border pt-3 space-y-3">
          <div class="flex flex-wrap items-center gap-3"><h3 class="font-medium">{{ t('page.29a080d61a10') }}</h3><button :disabled="busy" class="text-primary-400 text-sm" @click="loadRequests()">{{ t('page.4c1061f42eed') }}</button><button v-if="nextCursor" :disabled="busy" class="text-primary-400 text-sm" @click="loadRequests(nextCursor)">{{ t('page.dea367b006bb') }}</button><span v-if="requests" class="text-sm text-gray-400">{{ t('page.670ec25af841') }} {{ requestsTotal }} {{ t('page.f004f1d84cf9') }}</span></div>
          <p v-if="requestsDropped" class="text-sm text-amber-400">{{ t('page.0e52cf9639c3') }} {{ requestsDropped }} {{ t('page.a2e6dacd3286') }}</p>
          <p v-if="requests && !requests.length" class="text-sm text-gray-500">{{ t('page.6bfd0e0b77e8') }}</p>
          <div v-for="record in requests" :key="record.id" class="border-t border-dark-border py-2 text-sm break-all">
            <div class="font-mono">{{ record.request.method }} {{ record.request.path }}</div>
            <div class="text-gray-400">{{ new Date(record.request.time).toLocaleString(locale) }} · {{ record.request.requestId }}</div>
            <div>{{ record.ip }} · {{ record.ruleId }}</div>
            <div>{{ t('page.eaba2aa91757') }}{{ record.request.matchedFields.join(', ') || t('page.7450cc5b16e2') }}</div>
            <div v-for="location in record.request.matchLocations || []" :key="`${location.group}:${location.condition}`">
              {{ t('page.95ec5352a597') }} {{ location.group }} {{ t('page.74f841d0c59a') }} {{ location.condition }}：{{ location.field }}<span v-if="location.fieldName">[{{ location.fieldName }}]</span> · {{ location.operator }}
            </div>
            <div>{{ t('page.0767918bf65d') }}{{ record.request.normalizationSteps.join(' → ') || t('page.9d5be237daa9') }}</div>
            <div>{{ t('page.8e02684eeca5') }}{{ record.request.queryKeys.join(', ') || t('ui.none') }}</div>
          </div>
        </div>
        <details v-if="detail.explanation"><summary class="cursor-pointer text-primary-400">{{ t('page.3d156946d919') }}</summary><pre class="text-xs whitespace-pre-wrap break-all mt-2">{{ JSON.stringify(detail.explanation, null, 2) }}</pre></details>
        <details v-if="detail.related.length"><summary class="cursor-pointer text-primary-400">{{ t('page.c7bd579cf156') }} {{ detail.related.length }}</summary><div v-for="event in detail.related" :key="event.id" class="py-2"><button class="text-primary-400 text-sm" @click="open(event.id)">{{ event.ip }} · {{ event.ruleId }} · {{ event.count }}</button></div></details>
        <form v-if="can('write')" class="border-t border-dark-border pt-4 space-y-3" @submit.prevent="disposition">
          <select v-model="state" :aria-label="t('page.9915bfbab463')" class="bg-dark-sidebar border border-dark-border rounded-lg p-2"><option v-for="(label, i) in states" :key="i" :value="i">{{ label }}</option></select>
          <textarea v-model="note" required maxlength="200" :placeholder="t('page.87efe8a671a0')" class="block w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" />
          <button :disabled="busy" class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2">{{ busy ? t('settings.processing') : t('page.351068bd73ec') }}</button>
        </form>
        <div v-for="(entry, index) in detail.data.history" :key="index" class="border-t border-dark-border pt-2 text-sm"><span class="text-gray-400">{{ new Date(entry.time).toLocaleString(locale) }} · {{ entry.operator }} · {{ states[entry.state] }}</span><p class="break-all">{{ entry.note }}</p></div>
      </section>
    </div>
  </div>
</template>
