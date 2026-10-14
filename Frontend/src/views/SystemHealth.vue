<script setup lang="ts">
import { t, formatDateTime } from '@/i18n'
import { computed, onMounted, ref } from 'vue'
import { managementTaskApi } from '@/api'
const data = ref<any>(null)
const loading = ref(false)
const error = ref('')
interface Upstream {
  cluster: string
  destination: string
  address?: string
  hosts?: string[]
  active: string
  passive: string
  connections: number
}
const upstreamGroups = computed(() => {
  const groups = new Map<string, Upstream[]>()
  for (const upstream of (data.value?.upstreams ?? []) as Upstream[]) {
    const entries = groups.get(upstream.cluster)
    if (entries) entries.push(upstream)
    else groups.set(upstream.cluster, [upstream])
  }
  return Array.from(groups, ([cluster, entries]) => ({ cluster, entries }))
})
const bytes = (value: number | null | undefined) => value == null ? t('ui.unknown') : `${(value / 1024 / 1024).toFixed(1)} MiB`
async function load() {
  loading.value = true
  error.value = ''
  try { const result = await managementTaskApi.health(); if (result.success) data.value = result.data; else error.value = t('ui.unable.to.read.system.health') }
  catch { error.value = t('api.failed') }
  finally { loading.value = false }
}
onMounted(load)
</script>
<template>
  <div class="space-y-5">
    <header class="flex items-center justify-between"><h1 class="text-xl font-semibold"> {{ t('nav.health') }} </h1>
      <button :disabled="loading" class="bg-dark-card rounded-lg px-4 py-2" @click="load">{{ loading ? t('ui.refreshing') : t('common.refresh') }}</button>
    </header>
    <p v-if="error" role="alert" class="text-red-400">{{ error }}</p>
    <template v-if="data">
      <dl class="grid grid-cols-2 lg:grid-cols-4 gap-5 border-y border-dark-border py-5">
        <div><dt class="text-gray-400"> {{ t('ui.process') }} </dt><dd>{{ data.process.id }}</dd></div>
        <div><dt class="text-gray-400"> {{ t('ui.memory') }} </dt><dd>{{ bytes(data.process.memoryBytes) }}</dd></div>
        <div><dt class="text-gray-400"> {{ t('ui.database') }} </dt><dd>{{ data.database }}</dd></div>
        <div><dt class="text-gray-400"> {{ t('ui.disk.available.total') }} </dt><dd>{{ bytes(data.disk.availableBytes) }} / {{ bytes(data.disk.capacityBytes) }}</dd></div>
        <div><dt class="text-gray-400"> {{ t('ui.queued.tasks') }} </dt><dd>{{ data.queue.queued }}</dd></div>
        <div><dt class="text-gray-400"> {{ t('ui.running.tasks') }} </dt><dd>{{ data.queue.running }}</dd></div>
        <div><dt class="text-gray-400"> {{ t('ui.failed.timed.out.tasks') }} </dt><dd>{{ data.queue.failed }}</dd></div>
        <div><dt class="text-gray-400"> {{ t('settings.started.at') }} </dt><dd>{{ formatDateTime(data.process.startedAt) }}</dd></div>
      </dl>
      <section class="space-y-2"><h2 class="font-medium"> {{ t('ui.listening.addresses') }} </h2><div v-for="address in data.listeners" :key="address" class="font-mono text-sm">{{ address }}</div><p v-if="!data.listeners.length" class="text-gray-500"> {{ t('ui.no.listening.addresses.reported') }} </p></section>
      <section class="space-y-2"><h2 class="font-medium"> {{ t('ui.upstreams') }} </h2><div class="overflow-auto"><table class="w-full text-left text-sm"><thead class="text-gray-400"><tr><th class="p-2"> {{ t('ui.cluster') }} </th><th> {{ t('ui.destination') }} </th><th> {{ t('ui.active.check') }} </th><th> {{ t('ui.passive.check') }} </th><th> {{ t('ui.connections') }} </th></tr></thead><tbody>
        <template v-for="group in upstreamGroups" :key="group.cluster">
          <tr v-for="(upstream, index) in group.entries" :key="upstream.destination" class="border-t border-dark-border">
            <th v-if="index === 0" :rowspan="group.entries.length" scope="rowgroup" class="p-2 align-middle font-normal break-all"><div>{{ group.cluster }}</div><div v-for="host in group.entries[0]?.hosts || []" :key="host" class="text-xs text-gray-400">{{ host === '*' ? t('ui.all.hosts') : host }}</div></th>
            <td class="py-2 pr-4"><div>{{ upstream.destination }}</div><div class="text-xs text-gray-400 font-mono break-all">{{ upstream.address || t('ui.no.address.provided') }}</div></td><td>{{ upstream.active }}</td><td>{{ upstream.passive }}</td><td>{{ upstream.connections }}</td>
          </tr>
        </template>
        <tr v-if="!upstreamGroups.length"><td colspan="5" class="py-8 text-center text-gray-500"> {{ t('ui.no.upstreams') }} </td></tr>
      </tbody></table></div></section>
      <section class="space-y-2"><h2 class="font-medium"> {{ t('ui.certificates') }} </h2><div v-for="certificate in data.certificates" :key="certificate.domain" class="flex flex-wrap gap-4 border-b border-dark-border py-2 text-sm"><span>{{ certificate.domain }}</span><span>{{ formatDateTime(certificate.expiresAt) }}</span><span :class="certificate.daysRemaining < 15 ? 'text-red-400' : 'text-green-400'">{{ t('ui.daysRemaining', { count: certificate.daysRemaining }) }}</span></div><p v-if="!data.certificates?.length" class="text-gray-500"> {{ t('ui.no.managed.certificates') }} </p></section>
      <section class="space-y-2"><h2 class="font-medium"> {{ t('ui.rule.version') }} </h2><pre class="text-sm whitespace-pre-wrap break-all text-gray-400">{{ data.rules ? JSON.stringify(data.rules, null, 2) : t('ui.unknown') }}</pre></section>
      <p class="text-xs text-gray-500">{{ formatDateTime(data.capturedAt) }}</p>
    </template>
  </div>
</template>
