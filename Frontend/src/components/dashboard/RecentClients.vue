<script setup lang="ts">
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
  
  if (diff < 60) return `${diff} 秒前`
  if (diff < 3600) return `${Math.floor(diff / 60)} 分钟前`
  return time.toLocaleTimeString()
}
</script>

<template>
  <Section id="recent-clients" title="最近 5 分钟访问">
    <template #actions>
      <span class="text-sm text-gray-400">{{ clients.length }} 个客户端</span>
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
      
      <div v-if="clients.length === 0" class="text-gray-500 text-center py-8">
        最近 5 分钟内暂无访问记录
      </div>
    </div>
  </Section>
</template>
