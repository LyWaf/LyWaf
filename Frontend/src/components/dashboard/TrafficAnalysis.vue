<script setup lang="ts">
import type { TrafficStats } from '@/types'

interface Props {
  traffic: TrafficStats | null
}

const props = defineProps<Props>()
const emit = defineEmits<{
  (e: 'reset'): void
}>()

const formatNumber = (num: number) => {
  if (num >= 1000000) return (num / 1000000).toFixed(2) + 'M'
  if (num >= 1000) return (num / 1000).toFixed(2) + 'K'
  return num.toString()
}

const formatRate = (rate: number) => {
  return (rate * 100).toFixed(2) + '%'
}

const cards = [
  { key: 'totalRequests', label: '请求次数', icon: '👤', color: 'text-primary-500', tooltip: '统计周期内的总请求次数' },
  { key: 'pageViews', label: '访问次数（PV）', icon: '📄', color: 'text-primary-500', tooltip: '页面浏览量统计' },
  { key: 'uniqueVisitors', label: '独立访客（UV）', icon: '👥', color: 'text-yellow-500', tooltip: '根据 Cookie 统计的独立访客数' },
  { key: 'uniqueIps', label: '独立 IP', icon: '🌐', color: 'text-blue-500', tooltip: '不同来源 IP 地址数量' },
  { key: 'interceptCount', label: '拦截次数', icon: '🛡️', color: 'text-blue-400', tooltip: '被防护规则拦截的请求数' },
  { key: 'attackIps', label: '攻击 IP', icon: '⚠️', color: 'text-red-500', tooltip: '触发防护规则的独立 IP 数' },
]

const errorCards = [
  { key: 'error4xxCount', label: '4xx 错误数', color: 'text-yellow-500', tooltip: '客户端错误响应数量' },
  { key: 'error4xxRate', label: '4xx 错误率', color: 'text-yellow-500', isRate: true, tooltip: '4xx 错误占总请求的比例' },
  { key: 'intercept4xxCount', label: '4xx 拦截数', color: 'text-yellow-500', tooltip: '被拦截的 4xx 请求数量' },
  { key: 'intercept4xxRate', label: '4xx 拦截率', color: 'text-yellow-500', isRate: true, tooltip: '4xx 拦截占总请求的比例' },
  { key: 'error5xxCount', label: '5xx 错误数', color: 'text-red-500', tooltip: '服务端错误响应数量' },
  { key: 'error5xxRate', label: '5xx 错误率', color: 'text-red-500', isRate: true, tooltip: '5xx 错误占总请求的比例' },
]
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
          {{ traffic ? formatNumber((traffic as Record<string, number>)[card.key]) : '0' }}
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
          {{ traffic 
            ? (card.isRate 
                ? formatRate((traffic as Record<string, number>)[card.key]) 
                : formatNumber((traffic as Record<string, number>)[card.key]))
            : (card.isRate ? '0.00%' : '0') 
          }}
        </div>
      </div>
    </div>
    
    <!-- 统计时间和重置按钮 -->
    <div class="flex items-center gap-4 text-sm text-gray-500">
      <span>统计开始: {{ traffic?.startTime || '-' }}</span>
      <button @click="emit('reset')" class="btn btn-sm btn-secondary">
        重置统计
      </button>
    </div>
  </div>
</template>
