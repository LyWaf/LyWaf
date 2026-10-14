<script setup lang="ts">
import { locale } from '@/i18n'
import { t } from '@/i18n'
import { onMounted, ref } from 'vue'
import { siteApi, wafRuleApi } from '@/api'
import UpstreamHealthDialog from '@/components/UpstreamHealthDialog.vue'
const healthCluster = ref('')
const rows = ref<any[]>([])
const clusters = ref<any[]>([])
const rules = ref<any[]>([])
const listeners = ref<any[]>([])
const total = ref(0)
const page = ref(1)
const search = ref('')
const selected = ref<string[]>([])
const loading = ref(false)
const saving = ref(false)
const message = ref('')
const error = ref('')
const form = ref<any>(null)
const hosts = ref('')
const editing = ref(false)
const bound = ref(false)
const publication = ref<any>(null)
const version = ref('')
const editVersion = ref('')
const batchRules = ref<string[]>([])
function healthLabel(status: string, enabled: boolean | undefined, active: boolean) {
  if (enabled === false) return t('settings.not.enabled')
  if (status === 'Healthy') return t('settings.healthy')
  if (status === 'Unhealthy') return t('page.428fb8bfeecf')
  if (enabled === true) return active ? t('page.3a811788f296') : t('page.966692cbcf9d')
  return t('page.93e474c68ed5')
}
async function load() {
  loading.value = true
  try {
    const result = await siteApi.list(page.value, search.value)
    if (!result.success) { error.value = result.message || t('page.50e145939865'); return }
    rows.value = result.data; clusters.value = result.clusters; listeners.value = result.listeners
    total.value = result.total; publication.value = result.latestPublication; version.value = result.version
    selected.value = selected.value.filter(id => rows.value.some(r => r.site.id === id))
  } catch { error.value = t('page.50e145939865') }
  finally { loading.value = false }
}
function edit(site?: any, clone = false) {
  editVersion.value = version.value
  editing.value = !!site && !clone
  form.value = site ? JSON.parse(JSON.stringify(site)) : { id: '', name: '', enabled: true, route: { routeId: '', clusterId: clusters.value[0]?.id || '', match: { path: '/{**path}', hosts: [] } }, ruleIds: null }
  if (clone) { form.value.id = `${site.id}-copy`; form.value.name = t('page.ef5e52459e97', { p0: site.name }) }
  hosts.value = form.value.route.match.hosts?.join('\n') || ''
  bound.value = form.value.ruleIds != null
  if (!form.value.ruleIds) form.value.ruleIds = []
}
async function save() {
  saving.value = true; error.value = ''; message.value = ''
  try {
    const site = JSON.parse(JSON.stringify(form.value))
    site.route.routeId = site.id
    site.route.match.hosts = hosts.value.split(/[\s,]+/).filter(Boolean)
    if (!bound.value) site.ruleIds = null
    const result = await siteApi.save(site, editVersion.value)
    if (!result.success) { error.value = result.message || t('page.4a699018ae3e'); return }
    message.value = t('page.8de58d69be04', { p0: result.data.id }); form.value = null; await load()
  } catch (e: any) { error.value = e.message || t('ui.save.failed') }
  finally { saving.value = false }
}
async function batch(action: string) {
  if (action === 'delete' && !window.confirm(t('page.be9852368f62', { p0: selected.value.length }))) return
  saving.value = true; error.value = ''; message.value = ''
  try {
    const result = await siteApi.batch(selected.value, action, action === 'bind-rules' ? batchRules.value : undefined)
    if (!result.success) error.value = result.message || t('page.203f0208220c')
    else {
      const failed = result.data.filter((item: any) => !item.success)
      message.value = t('page.a3c329e806f5', { p0: result.data.length - failed.length })
      error.value = failed.map((item: any) => `${item.id}: ${item.message}`).join('\n')
    }
    await load()
  } catch (e: any) { error.value = e.message || t('page.203f0208220c') }
  finally { saving.value = false }
}
async function exportSites() {
  try {
    const result = await siteApi.export()
    if (!result.success) { error.value = t('page.6e4445df6957'); return }
    const url = URL.createObjectURL(new Blob([JSON.stringify(result.data, null, 2)], { type: 'application/json' }))
    const link = document.createElement('a'); link.href = url; link.download = 'lywaf-sites.json'; link.click(); URL.revokeObjectURL(url)
  } catch { error.value = t('page.6e4445df6957') }
}
async function importSites(event: Event) {
  const input = event.target as HTMLInputElement; const file = input.files?.[0]
  if (!file) return
  saving.value = true; error.value = ''
  try {
    if (file.size > 8 * 1024 * 1024) throw new Error(t('page.998039e44a4c'))
    const result = await siteApi.import(JSON.parse(await file.text()))
    if (!result.success) error.value = result.message || t('page.01aaebc96af1')
    else { message.value = t('page.cda590c397b4', { p0: result.data.id }); await load() }
  } catch (e: any) { error.value = e.message || t('page.01aaebc96af1') }
  finally { saving.value = false; input.value = '' }
}
onMounted(async () => {
  await load()
  try {
    const collected: any[] = []
    for (let page = 1; ; page++) {
      const result: any = await wafRuleApi.list({ page, pageSize: 100, source: 'User' })
      if (!result.success) throw new Error(t('page.3cca40011660'))
      if (!Array.isArray(result.data)) throw new Error(t('page.c74acd1b50d2'))
      collected.push(...result.data)
      if (collected.length >= result.total) break
      if (!result.data.length || page >= 100) throw new Error(t('page.0d57796c43d8'))
    }
    rules.value = collected
  } catch (e: any) { error.value = e.message || t('page.363b0f64b071') }
})
</script>
<template>
  <div class="space-y-5">
    <UpstreamHealthDialog v-if="healthCluster" :cluster-id="healthCluster" @close="healthCluster = ''" @saved="healthCluster = ''; load()" />
    <header class="flex flex-wrap items-center justify-between gap-3"><h1 class="text-xl font-semibold">{{ t('nav.sites') }}</h1><div class="flex flex-wrap gap-3">
      <button class="bg-dark-card rounded-lg px-4 py-2" :disabled="loading" @click="load">{{ t('common.refresh') }}</button>
      <button class="bg-dark-card rounded-lg px-4 py-2" @click="exportSites">{{ t('page.76420433f22d') }}</button>
      <label class="bg-dark-card rounded-lg px-4 py-2 cursor-pointer">{{ t('page.576d81bb0631') }}<input type="file" accept="application/json,.json" class="hidden" :disabled="saving" @change="importSites" /></label>
      <button class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2" @click="edit()">{{ t('page.f1c7da9b414c') }}</button>
    </div></header>
    <p v-if="error" role="alert" class="text-red-400 whitespace-pre-wrap">{{ error }}</p>
    <p v-if="message" class="text-green-400 break-all">{{ message }} <RouterLink class="underline" to="/management-tasks">{{ t('page.36550b9c5748') }}</RouterLink></p>
    <div class="flex flex-wrap gap-3 items-center">
      <input v-model="search" :placeholder="t('page.06285c179b50')" class="bg-dark-card border border-dark-border rounded-lg p-2" @keyup.enter="page = 1; load()" />
      <button :disabled="!selected.length || saving" class="px-3 py-2 bg-dark-card rounded-lg disabled:opacity-40" @click="batch('enable')">{{ t('fields.Enabled') }}</button>
      <button :disabled="!selected.length || saving" class="px-3 py-2 bg-dark-card rounded-lg disabled:opacity-40" @click="batch('disable')">{{ t('page.8d12fc0d4eb2') }}</button>
      <button :disabled="!selected.length || saving" class="px-3 py-2 bg-red-500/10 text-red-400 rounded-lg disabled:opacity-40" @click="batch('delete')">{{ t('common.delete') }}</button>
    </div>
    <div v-if="selected.length" class="flex flex-wrap items-end gap-3">
      <label class="text-sm text-gray-400">{{ t('page.28f4bae24118') }}<select v-model="batchRules" multiple class="block mt-1 bg-dark-card border border-dark-border rounded-lg p-2 max-w-full"><option v-for="rule in rules" :key="rule.id" :value="rule.id">{{ rule.name }}</option></select></label>
      <button :disabled="saving" class="bg-dark-card rounded-lg px-3 py-2" @click="batch('bind-rules')">{{ t('page.f2779119ecc0') }}</button>
    </div>
    <p v-if="publication" class="text-sm text-gray-400">{{ t('page.7ec8f6288259') }}{{ [t('ui.queued'), t('common.running'), t('ui.succeeded'), t('ui.failed'), t('common.cancel'), t('ui.timed.out')][publication.state] }} <RouterLink to="/management-tasks" class="text-primary-400">{{ t('dashboard.view.logs') }}</RouterLink></p>
    <div class="overflow-auto"><table class="w-full min-w-[1050px] text-left text-sm"><thead class="text-gray-400"><tr><th class="p-3">{{ t('page.c11330b85234') }}</th><th>{{ t('page.a59fe62777ff') }}</th><th>{{ t('page.aeb1404a9333') }}</th><th>{{ t('page.48c64e98c4e2') }}</th><th>{{ t('settings.status') }}</th><th>{{ t('ui.certificates') }}</th><th>{{ t('settings.actions') }}</th></tr></thead><tbody>
      <tr v-for="row in rows" :key="row.site.id" class="border-t border-dark-border">
        <td class="p-3"><input v-model="selected" type="checkbox" :value="row.site.id" :aria-label="t('page.f0f5c3b93772', { p0: row.site.name })" /></td>
        <td class="py-3"><div>{{ row.site.name }}</div><div class="text-xs text-gray-400 break-all">{{ row.site.route.match.hosts?.join(', ') || '*' }}</div><div class="text-xs text-gray-500">{{ row.site.route.match.path }}</div>
          <div v-for="listener in row.listeners" :key="`${listener.host}:${listener.port}`" class="text-xs text-gray-400">{{ listener.https ? 'HTTPS' : 'HTTP' }} :{{ listener.port }}</div>
        </td>
        <td class="py-3"><div>{{ row.site.route.clusterId }}</div><div v-for="upstream in row.upstreams" :key="upstream.id" class="text-xs text-gray-400 break-all">{{ upstream.address }}</div>
          <div v-for="health in row.upstreamHealth" :key="health.id" class="text-xs" :class="(health.activeEnabled !== false && health.active === 'Unhealthy') || (health.passiveEnabled !== false && health.passive === 'Unhealthy') ? 'text-red-400' : 'text-gray-400'">{{ health.id }}{{ t('page.ac806ff99467') }} {{ healthLabel(health.active, health.activeEnabled, true) }} {{ t('page.c2dad908b3e3') }} {{ healthLabel(health.passive, health.passiveEnabled, false) }}</div>
        </td><td><span>{{ row.traffic?.requests ?? 0 }} / {{ row.traffic?.intercepts ?? 0 }}</span><div v-if="row.traffic?.requests" class="text-xs text-gray-500">{{ t('page.aa0bc35b20a5') }} {{ ((1 - row.traffic.errors / row.traffic.requests) * 100).toFixed(1) }}%<br />{{ t('page.b3e27dd13a7a') }} {{ new Date(row.traffic.observedSince).toLocaleString(locale) }}</div></td>
        <td :class="row.site.enabled ? 'text-green-400' : 'text-gray-500'">{{ row.site.enabled ? t('fields.Enabled') : t('page.8d12fc0d4eb2') }}<div v-if="row.latestPublication" class="text-xs text-gray-400">{{ [t('ui.queued'), t('page.b7a0e0aad12a'), t('page.8d544e154955'), t('page.c8bceff124ed'), t('ui.cancelled'), t('page.605a8846dd6f')][row.latestPublication.state] }}</div></td>
        <td><div v-for="cert in row.certificates" :key="cert.domain" :class="cert.daysRemaining < 15 ? 'text-red-400' : 'text-green-400'">{{ cert.daysRemaining }} {{ t('page.49da61ceeea2') }}</div><span v-if="!row.certificates.length" class="text-gray-500">{{ t('page.11af2d58fbdf') }}</span></td>
        <td class="whitespace-nowrap space-x-3"><button class="text-primary-400" @click="edit(row.site)">{{ t('common.edit') }}</button><button class="text-primary-400" @click="edit(row.site, true)">{{ t('page.63d90d977348') }}</button><button v-if="row.site.route.clusterId !== 'cluster_unuse'" class="text-primary-400" @click="healthCluster = row.site.route.clusterId">{{ t('page.da7d17db5d64') }}</button></td>
      </tr><tr v-if="!rows.length"><td colspan="7" class="text-center text-gray-500 py-12">{{ t('page.8a10712dbd45') }}</td></tr>
    </tbody></table></div>
    <footer class="flex justify-end gap-4 text-sm"><span>{{ t('page.76e547a8fa54') }} {{ total }} {{ t('page.3a116815bc9d') }}</span><button :disabled="page <= 1 || loading" @click="page--; load()">{{ t('common.previous') }}</button><span>{{ page }}</span><button :disabled="page * 20 >= total || loading" @click="page++; load()">{{ t('common.next') }}</button></footer>
    <p class="text-xs text-gray-500 break-all">{{ t('page.c3e31092cbf9') }}{{ listeners.map(l => `${l.host || '*'}:${l.port}`).join(', ') }}</p>
    <div v-if="form" class="fixed inset-0 z-50 bg-black/60 flex items-center justify-center p-4">
      <form role="dialog" aria-modal="true" :aria-label="t('page.354ff094a774')" class="bg-dark-card rounded-lg border border-dark-border w-full max-w-xl max-h-[90vh] overflow-auto p-5 space-y-4" @submit.prevent="save">
        <h2 class="font-semibold">{{ editing ? t('page.354ff094a774') : t('page.f1c7da9b414c') }}</h2>
        <label class="block text-sm">{{ t('page.15a9aec0ea06') }}<input v-model="form.id" required :disabled="editing" maxlength="100" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="block text-sm">{{ t('fields.Name') }}<input v-model="form.name" required maxlength="200" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="block text-sm">{{ t('settings.domain') }}<textarea v-model="hosts" rows="3" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="block text-sm">{{ t('ui.path') }}<input v-model="form.route.match.path" required class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="block text-sm">{{ t('page.aeb1404a9333') }}<select v-model="form.route.clusterId" required class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2"><option v-for="cluster in clusters" :key="cluster.id" :value="cluster.id">{{ cluster.id }}</option></select></label>
        <label class="flex items-center gap-2 text-sm"><input v-model="form.enabled" type="checkbox" />{{ t('page.a22623d22a3e') }}</label>
        <label class="flex items-center gap-2 text-sm"><input v-model="bound" type="checkbox" />{{ t('page.ce16a4a1d26e') }}</label>
        <div v-if="bound" class="max-h-40 overflow-auto space-y-2"><label v-for="rule in rules" :key="rule.id" class="flex items-center gap-2 text-sm"><input v-model="form.ruleIds" type="checkbox" :value="rule.id" />{{ rule.name }}</label></div>
        <div class="flex justify-end gap-3"><button type="button" :disabled="saving" @click="form = null">{{ t('common.cancel') }}</button><button :disabled="saving" class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2">{{ saving ? t('settings.submitting') : t('page.b61f333b91b2') }}</button></div>
      </form>
    </div>
  </div>
</template>
