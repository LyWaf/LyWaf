<script setup lang="ts">
import { t, locale, formatDateTime } from '@/i18n'
import Section from '@/components/common/Section.vue'

interface RecentClient {
  ip: string
  lastAccessTime: string
}

interface Props {
  clients: RecentClient[]
}

defineProps<Props>()

// 计算相对时间
const getRelativeTime = (timeStr: string) => {
  const time = new Date(timeStr)
  const now = new Date()
  const diff = Math.floor((now.getTime() - time.getTime()) / 1000)
  
  if (!Number.isFinite(diff)) return '-'
  const relative = new Intl.RelativeTimeFormat(locale.value, { numeric: 'auto' })
  if (diff < 60) return relative.format(-Math.max(0, diff), 'second')
  if (diff < 3600) return relative.format(-Math.floor(diff / 60), 'minute')
  return formatDateTime(time)
}
</script>

<template>
  <Section id="recent-clients" :title="t('dashboard.visits.in.the.last.5.minutes')">
    <template #actions>
      <span class="text-sm text-gray-400">{{ t('dashboard.clients', { count: clients.length }) }}</span>
    </template>
    
    <div class="space-y-2 max-h-[300px] overflow-y-auto">
      <div 
        v-for="client in clients" 
        :key="client.ip"
        class="flex items-center justify-between p-3 bg-dark-card-hover rounded-lg"
      >
        <div class="flex items-center gap-4">
          <span class="text-green-400">●</span>
          <span class="font-mono text-gray-200">{{ client.ip }}</span>
        </div>
        <span class="text-gray-400 text-sm">{{ getRelativeTime(client.lastAccessTime) }}</span>
      </div>
      
      <div v-if="clients.length === 0" class="text-gray-500 text-center py-8"> {{ t('dashboard.no.visits.in.the.last.5.minutes') }} </div>
    </div>
  </Section>
</template>
