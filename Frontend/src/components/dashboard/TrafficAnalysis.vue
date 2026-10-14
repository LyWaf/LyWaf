<script setup lang="ts">
import { t, formatDateTime } from '@/i18n'
import { computed } from 'vue'
import type { TrafficStats } from '@/types'

interface Props {
  traffic: TrafficStats | null
}

defineProps<Props>()
const emit = defineEmits<{
  (e: 'reset'): void
}>()

const formatNumber = (num: number) => {
  if (num >= 1000000) return (num / 1000000).toFixed(2) + 'M'
  if (num >= 1000) return (num / 1000).toFixed(2) + 'K'
  return num.toString()
}

const formatRate = (rate: number) => {
  return rate.toFixed(2) + '%'
}

const cards = computed(() => [
  { key: 'totalRequests', label: t('dashboard.requests'), icon: '👤', color: 'text-primary-500', tooltip: t('dashboard.total.requests.in.the.reporting.period') },
  { key: 'pageViews', label: t('dashboard.page.views.pv'), icon: '📄', color: 'text-primary-500', tooltip: t('dashboard.page.view.count') },
  { key: 'uniqueVisitors', label: t('dashboard.unique.visitors.uv'), icon: '👥', color: 'text-yellow-500', tooltip: t('dashboard.unique.visitors.identified.by.cookies') },
  { key: 'uniqueIps', label: t('dashboard.unique.ips'), icon: '🌐', color: 'text-blue-500', tooltip: t('dashboard.distinct.source.ip.addresses') },
  { key: 'interceptCount', label: t('dashboard.blocked.requests'), icon: '🛡️', color: 'text-blue-400', tooltip: t('dashboard.requests.blocked.by.protection.rules') },
  { key: 'attackIps', label: t('dashboard.attack.ips'), icon: '⚠️', color: 'text-red-500', tooltip: t('dashboard.distinct.ips.triggering.protection.rules') },
] as const)

const errorCards = computed(() => [
  { key: 'error4xxCount', label: t('dashboard.4xx.errors'), color: 'text-yellow-500', isRate: false, tooltip: t('dashboard.client.error.responses') },
  { key: 'error4xxRate', label: t('dashboard.4xx.error.rate'), color: 'text-yellow-500', isRate: true, tooltip: t('dashboard.4xx.errors.as.a.percentage.of.all.requests') },
  { key: 'intercept4xxCount', label: t('dashboard.4xx.blocks'), color: 'text-yellow-500', isRate: false, tooltip: t('dashboard.blocked.4xx.requests') },
  { key: 'intercept4xxRate', label: t('dashboard.4xx.block.rate'), color: 'text-yellow-500', isRate: true, tooltip: t('dashboard.4xx.blocks.as.a.percentage.of.all.requests') },
  { key: 'error5xxCount', label: t('dashboard.5xx.errors'), color: 'text-red-500', isRate: false, tooltip: t('dashboard.server.error.responses') },
  { key: 'error5xxRate', label: t('dashboard.5xx.error.rate'), color: 'text-red-500', isRate: true, tooltip: t('dashboard.5xx.errors.as.a.percentage.of.all.requests') },
] as const)

const getValue = (traffic: TrafficStats | null, key: string): number => {
  if (!traffic) return 0
  return (traffic as unknown as Record<string, number>)[key] ?? 0
}
</script>

<template>
  <div class="space-y-4">
    <!-- 主要指标 -->
    <div class="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-4">
      <div 
        v-for="card in cards" 
        :key="card.key"
        class="card cursor-help"
        :title="card.tooltip"
      >
        <div class="flex items-center gap-2 text-gray-400 text-sm mb-2">
          <span>{{ card.icon }}</span>
          <span>{{ card.label }}</span>
        </div>
        <div :class="['text-2xl font-bold', card.color]">
          {{ formatNumber(getValue(traffic, card.key)) }}
        </div>
      </div>
    </div>
    
    <!-- 错误统计 -->
    <div class="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-4">
      <div 
        v-for="card in errorCards" 
        :key="card.key"
        class="card cursor-help"
        :title="card.tooltip"
      >
        <div class="flex items-center gap-2 text-gray-400 text-sm mb-2">
          <span class="text-yellow-500">⚠</span>
          <span>{{ card.label }}</span>
        </div>
        <div :class="['text-xl font-bold', card.color]">
          {{ card.isRate 
              ? formatRate(getValue(traffic, card.key)) 
              : formatNumber(getValue(traffic, card.key))
          }}
        </div>
      </div>
    </div>
    
    <!-- 统计时间和重置按钮 -->
    <div class="flex items-center gap-4 text-sm text-gray-500">
      <span> {{ t('dashboard.statistics.since') }} {{ traffic?.startTime ? formatDateTime(traffic.startTime) : '-' }}</span>
      <button @click="emit('reset')" class="btn btn-sm btn-secondary"> {{ t('dashboard.reset.statistics') }} </button>
    </div>
  </div>
</template>
