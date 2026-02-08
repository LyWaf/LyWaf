<script setup lang="ts">
import { ref, watch } from 'vue'
import Section from '@/components/common/Section.vue'
import { ipApi } from '@/api'
import { useToast } from '@/composables/useToast'

interface BlockedIpInfo {
  ip: string
  reason: string
  remainingSeconds?: number
}

interface Props {
  initialData?: BlockedIpInfo[]
}

const props = defineProps<Props>()
const { showSuccess, showError } = useToast()

const blockedIps = ref<BlockedIpInfo[]>([])

// 监听父组件传入的数据
watch(() => props.initialData, (newData) => {
  if (newData) {
    blockedIps.value = newData
  }
}, { immediate: true })

// 格式化剩余时间
const formatRemainingTime = (seconds?: number) => {
  if (seconds === undefined || seconds === null) return '永久'
  if (seconds <= 0) return '已过期'
  
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  const secs = Math.floor(seconds % 60)
  
  if (days > 0) return `${days}天${hours}时`
  if (hours > 0) return `${hours}时${minutes}分`
  if (minutes > 0) return `${minutes}分${secs}秒`
  return `${secs}秒`
}

const emit = defineEmits<{
  (e: 'refresh'): void
}>()

const blockIp = async () => {
  const ip = prompt('请输入要封禁的 IP:')
  if (!ip) return
  
  const reason = prompt('请输入封禁原因:', '手动封禁')
  const durationStr = prompt('请输入封禁时长（秒，0表示永久）:', '3600')
  const duration = parseInt(durationStr || '3600')
  
  try {
    const res = await ipApi.blockIp(ip, reason || undefined, duration || undefined)
    if (res.success) {
      blockedIps.value.push({
        ip,
        reason: reason || '手动封禁',
        remainingSeconds: duration || undefined,
      })
      showSuccess(`已封禁: ${ip}`)
      emit('refresh')
    }
  } catch {
    showError('封禁失败')
  }
}

const unblockIp = async (ip: string) => {
  if (!confirm(`确定要解封 ${ip} 吗？`)) return
  
  try {
    const res = await ipApi.unblockIp(ip)
    if (res.success) {
      blockedIps.value = blockedIps.value.filter(i => i.ip !== ip)
      showSuccess(`已解封: ${ip}`)
      emit('refresh')
    }
  } catch {
    showError('解封失败')
  }
}

const clearAll = async () => {
  if (!confirm('确定要清空所有封禁 IP 吗？')) return
  
  try {
    const res = await ipApi.clearBlockedIps()
    if (res.success) {
      blockedIps.value = []
      showSuccess('已清空封禁列表')
      emit('refresh')
    }
  } catch {
    showError('清空失败')
  }
}
</script>

<template>
  <Section id="blocked-ips" title="当前封禁的 IP">
    <template #actions>
      <div class="flex gap-2">
        <button @click="blockIp" class="btn btn-sm btn-primary">+ 手动封禁</button>
        <button @click="clearAll" class="btn btn-sm btn-danger">清空全部</button>
      </div>
    </template>
    
    <div class="space-y-2 max-h-[400px] overflow-y-auto">
      <div 
        v-for="item in blockedIps" 
        :key="item.ip"
        class="flex items-center justify-between p-4 bg-dark-card-hover rounded-lg"
      >
        <div class="flex items-center gap-6">
          <div>
            <span class="text-gray-400 text-sm">IP 地址</span>
            <div class="text-red-400 font-mono">{{ item.ip }}</div>
          </div>
          <div v-if="item.reason">
            <span class="text-gray-400 text-sm">原因</span>
            <div class="text-gray-300">{{ item.reason }}</div>
          </div>
          <div>
            <span class="text-gray-400 text-sm">剩余时间</span>
            <div class="text-yellow-400">{{ formatRemainingTime(item.remainingSeconds) }}</div>
          </div>
        </div>
        <button 
          @click="unblockIp(item.ip)"
          class="btn btn-sm btn-secondary"
        >
          解封
        </button>
      </div>
      
      <div v-if="blockedIps.length === 0" class="text-gray-500 text-center py-8">
        暂无封禁的 IP
      </div>
    </div>
  </Section>
</template>
