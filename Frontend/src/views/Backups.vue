<script setup lang="ts">
import { t, formatDateTime } from '@/i18n'
import { onMounted, ref } from 'vue'
import { backupApi } from '@/api'
const rows = ref<Array<{ id: string; size: number; createdAt: string; scope: string }>>([])
const scope = ref('configuration')
const pending = ref(false)
const instancePending = ref(false)
const page = ref(1)
const total = ref(0)
const search = ref('')
const busy = ref(false)
const error = ref('')
const task = ref('')
async function load() {
  const result = await backupApi.list(page.value, search.value)
  if (!result.success) throw new Error(result.message || t('ui.failed.to.load.backups'))
  rows.value = result.data
  pending.value = result.statisticsRestorePending
  instancePending.value = result.instanceStatePending
  total.value = result.total
}
async function perform(action: () => Promise<any>) {
  if (busy.value) return
  busy.value = true; error.value = ''; task.value = ''
  try {
    const result = await action()
    if (result?.success === false) throw new Error(result.message || result.error?.message || t('settings.operation.failed'))
    if (result?.data?.id) task.value = result.data.id
    await load()
  } catch (e: any) { error.value = e.message || t('api.failed') }
  finally { busy.value = false }
}
function restore(id: string, action: 'preflight' | 'restore') {
  if (action === 'restore' && !confirm(id.startsWith('i-') ? t('ui.restore.instance.state.on.the.next.restart.this.overwrites.accounts.certificates.and.plugin.data.and.deletes.state.files.absent.from.the.snapshot') : id.startsWith('s-') ? t('ui.schedule.statistics.restoration.on.the.next.restart.existing.statistics.will.be.overwritten') : t('ui.restore.and.reload.the.backup.overwriting.the.current.configuration'))) return
  perform(() => backupApi.restore(id, action))
}
async function download(id: string) {
  const blob = await backupApi.download(id)
  if (!(blob instanceof Blob) || blob.type.includes('json')) throw new Error(t('ui.backup.download.failed'))
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url; link.download = `${id}.lybackup`; link.click()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}
function upload(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]; input.value = ''
  if (!file) return
  if (file.size > 32 * 1024 * 1024) { error.value = t('ui.configuration.backups.must.not.exceed.32.mib'); return }
  perform(() => backupApi.upload(file, scope.value))
}
function remove(id: string) {
  if (confirm(t('ui.delete.this.backup'))) perform(() => backupApi.remove(id))
}
function create() {
  if (scope.value === 'instance' && !confirm(t('ui.instance.state.backup.runs.before.the.service.starts.on.the.next.restart.schedule.the.task.now'))) return
  perform(() => backupApi.create(scope.value))
}
onMounted(() => perform(async () => {}))
</script>
<template>
  <div class="space-y-5">
    <header class="flex flex-wrap items-center justify-between gap-3"><h1 class="text-xl font-semibold"> {{ t('ui.backup.and.restore') }} </h1>
      <button :disabled="busy" class="bg-dark-card border border-dark-border rounded-lg px-4 py-2" @click="perform(async () => {})"> {{ t('common.refresh') }} </button>
    </header>
    <p v-if="error" role="alert" class="text-red-400 break-words">{{ error }}</p>
    <div v-if="pending" role="status" class="flex flex-wrap items-center gap-4 text-amber-400 border-b border-dark-border pb-3">
      <span> {{ t('ui.statistics.restoration.pending.restart') }} </span><button :disabled="busy" class="text-red-400" @click="perform(backupApi.cancelPending)"> {{ t('ui.cancel.restoration') }} </button>
    </div>
    <RouterLink v-if="task" to="/management-tasks" class="block text-primary-400 break-all"> {{ t('ui.task.created') }} {{ task }}</RouterLink>
    <div v-if="instancePending" role="status" class="flex flex-wrap items-center gap-4 text-amber-400 border-b border-dark-border pb-3">
      <span> {{ t('ui.instance.state.task.pending.restart') }} </span><button :disabled="busy" class="text-red-400" @click="perform(backupApi.cancelInstancePending)"> {{ t('ui.cancel.task') }} </button>
    </div>
    <form class="flex flex-wrap gap-3 items-end border-b border-dark-border pb-5" @submit.prevent="create">
      <label class="text-sm text-gray-400"> {{ t('ui.backup.scope') }} <select v-model="scope" class="block mt-1 bg-dark-card border border-dark-border rounded-lg p-2"><option value="configuration"> {{ t('ui.configuration') }} </option><option value="statistics"> {{ t('ui.statistics.database') }} </option><option value="instance"> {{ t('ui.instance.state.restart.required') }} </option></select></label>
      <button :disabled="busy" class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2 disabled:opacity-50"> {{ t('ui.create.backup') }} </button>
      <label class="block text-sm text-gray-400"> {{ t('ui.upload.backup') }} <input type="file" accept=".lybackup" :disabled="busy" class="block mt-1 max-w-full" @change="upload" /></label>
    </form>
    <form class="flex flex-wrap gap-3" @submit.prevent="page = 1; perform(async () => {})">
      <input v-model="search" :aria-label="t('ui.search.backups')" :placeholder="t('ui.search.backup.id.or.scope')" maxlength="200" class="min-w-0 bg-dark-card border border-dark-border rounded-lg p-2" />
      <button :disabled="busy" class="border border-dark-border rounded-lg px-4 py-2"> {{ t('common.search') }} </button>
    </form>
    <div class="overflow-x-auto"><table class="w-full text-sm text-left">
      <thead class="text-gray-400 border-b border-dark-border"><tr><th class="p-3"> {{ t('ui.backup.id') }} </th><th class="p-3"> {{ t('ui.size') }} </th><th class="p-3"> {{ t('ui.created.at') }} </th><th class="p-3"> {{ t('settings.actions') }} </th></tr></thead>
      <tbody><tr v-for="row in rows" :key="row.id" class="border-b border-dark-border">
        <td class="p-3 font-mono">{{ row.id }}<div class="text-xs text-gray-500 mt-1">{{ row.scope === 'statistics' ? t('ui.statistics.database') : row.scope === 'instance' ? t('ui.instance.state') : t('ui.configuration') }}</div></td><td class="p-3 whitespace-nowrap">{{ (row.size / 1024).toFixed(1) }} KiB</td><td class="p-3 whitespace-nowrap">{{ formatDateTime(row.createdAt) }}</td>
        <td class="p-3"><div class="flex gap-3 whitespace-nowrap">
          <button :disabled="busy" class="text-primary-400" @click="perform(() => download(row.id))"> {{ t('ui.download') }} </button>
          <button :disabled="busy" class="text-primary-400" @click="restore(row.id, 'preflight')"> {{ t('ui.preflight') }} </button>
          <button :disabled="busy" class="text-primary-400" @click="restore(row.id, 'restore')"> {{ t('ui.restore') }} </button>
          <button :disabled="busy" class="text-red-400" @click="remove(row.id)"> {{ t('common.delete') }} </button>
        </div></td>
      </tr><tr v-if="!rows.length"><td colspan="4" class="py-12 text-center text-gray-500"> {{ t('ui.no.configuration.backups') }} </td></tr></tbody>
    </table></div>
    <div class="flex gap-4 items-center text-sm">
      <button :disabled="busy || page <= 1" class="disabled:opacity-40" @click="page--; perform(async () => {})"> {{ t('common.previous') }} </button>
      <span>{{ t('ui.pageSummary', { page, pages: Math.max(1, Math.ceil(total / 20)), count: total }) }}</span>
      <button :disabled="busy || page * 20 >= total" class="disabled:opacity-40" @click="page++; perform(async () => {})"> {{ t('common.next') }} </button>
    </div>
  </div>
</template>
