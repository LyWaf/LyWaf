<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import StatCard from '@/components/common/StatCard.vue'
import TrafficAnalysis from '@/components/dashboard/TrafficAnalysis.vue'
import BlockedIps from '@/components/dashboard/BlockedIps.vue'
import RecentClients from '@/components/dashboard/RecentClients.vue'
import { trafficApi, dashboardApi } from '@/api'
import { useToast } from '@/composables/useToast'
import type { TrafficStats, SystemStatus } from '@/types'

const { showSuccess, showError } = useToast()

// 数据
const loading = ref(false)
const traffic = ref<TrafficStats | null>(null)
const system = ref<SystemStatus>({
  uptime: '-',
  memory: 0,
  totalConnections: 0,
  blockedIpCount: 0,
  processStartTime: '-',
})

// 最近访问的客户端
const recentClients = ref<Array<{ ip: string; lastAccessTime: string }>>([])

// 当前封禁的 IP
const blockedIps = ref<Array<{ ip: string; type: string; reason: string; remainingSeconds?: number }>>([])

// 自动刷新
let refreshTimer: number | null = null

onMounted(() => {
  loadData()
  refreshTimer = window.setInterval(loadData, 60000) // 每分钟刷新
})

onUnmounted(() => {
  if (refreshTimer) {
    clearInterval(refreshTimer)
  }
})

const loadData = async () => {
  loading.value = true
  try {
    const data = await dashboardApi.getData()
    if (data.success) {
      // 更新系统状态
      system.value = {
        uptime: data.system.uptime,
        memory: data.system.memory,
        totalConnections: data.system.totalConnections,
        blockedIpCount: data.system.blockedIpCount,
        processStartTime: data.system.processStartTime,
      }

      // 更新流量统计
      traffic.value = data.traffic

      // 更新最近访问的客户端
      recentClients.value = data.recentClients || []

      // 更新封禁的 IP
      blockedIps.value = data.blockedIps || []
    }
  } catch (error) {
    console.error('加载数据失败:', error)
  } finally {
    loading.value = false
  }
}

// 重置流量统计
const resetTraffic = async () => {
  if (!confirm('确定要重置流量统计吗？')) return

  try {
    await trafficApi.reset()
    showSuccess('流量统计已重置')
    loadData()
  } catch (error) {
    showError('重置失败')
  }
}
</script>

<template>
  <div class="space-y-6">
    <!-- 流量分析 -->
    <TrafficAnalysis :traffic="traffic" @reset="resetTraffic" />

    <!-- 概览卡片 -->
    <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
      <StatCard
        label="运行时间"
        :value="system.uptime"
        icon="⏱️"
        color="blue"
        tooltip="服务启动后的运行时长"
      />
      <StatCard
        label="内存使用"
        :value="`${system.memory} MB`"
        icon="💾"
        color="green"
        tooltip="当前进程内存占用"
      />
      <StatCard
        label="当前连接"
        :value="system.totalConnections"
        icon="🔗"
        tooltip="当前活跃连接数"
      />
      <StatCard
        label="封禁 IP"
        :value="system.blockedIpCount"
        icon="🚫"
        color="red"
        tooltip="当前被封禁的 IP 数量"
      />
    </div>

    <!-- 最近访问 -->
    <RecentClients :clients="recentClients" />

    <!-- 封禁列表 -->
    <BlockedIps :initial-data="blockedIps" @refresh="loadData" />
  </div>
</template>
