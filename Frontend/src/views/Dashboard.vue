<script setup lang="ts">
import { t, formatDuration } from '@/i18n'
import { ref, onMounted, onUnmounted } from 'vue'
import StatCard from '@/components/common/StatCard.vue'
import TrafficAnalysis from '@/components/dashboard/TrafficAnalysis.vue'
import GeoTrafficMap from '@/components/dashboard/GeoTrafficMap.vue'
import BlockedIps from '@/components/dashboard/BlockedIps.vue'
import RecentClients from '@/components/dashboard/RecentClients.vue'
import { trafficApi, dashboardApi } from '@/api'
import { useToast } from '@/composables/useToast'
import type { TrafficStats, SystemStatus, GeoTrafficStats } from '@/types'

const { showSuccess, showError } = useToast()

// 数据
const loading = ref(false)
const traffic = ref<TrafficStats | null>(null)
const geoTraffic = ref<GeoTrafficStats | null>(null)
const system = ref<SystemStatus>({
  uptime: '-',
  memory: 0,
  totalConnections: 0,
  httpConnections: 0,
  wsConnections: 0,
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
  if (loading.value) return
  loading.value = true
  try {
    const data = await dashboardApi.getData()
    if (data.success) {
      // 更新系统状态
      system.value = {
        uptime: data.system.uptime,
        uptimeSeconds: data.system.uptimeSeconds ?? Math.max(0, (Date.now() - new Date(data.system.processStartTime).getTime()) / 1000),
        memory: data.system.memory,
        totalConnections: data.system.totalConnections,
        httpConnections: data.system.httpConnections ?? 0,
        wsConnections: data.system.wsConnections ?? 0,
        blockedIpCount: data.system.blockedIpCount,
        processStartTime: data.system.processStartTime,
      }

      // 更新流量统计
      traffic.value = data.traffic
      geoTraffic.value = data.geoTraffic

      // 更新最近访问的客户端
      recentClients.value = data.recentClients || []

      // 更新封禁的 IP
      blockedIps.value = data.blockedIps || []
    }
  } catch (error) {
    console.error(t('dashboard.failed.to.load.data'), error)
  } finally {
    loading.value = false
  }
}

// 重置流量统计
const resetTraffic = async () => {
  if (!confirm(t('dashboard.reset.traffic.statistics'))) return

  try {
    await trafficApi.reset()
    showSuccess(t('dashboard.traffic.statistics.reset'))
    loadData()
  } catch (error) {
    showError(t('dashboard.reset.failed'))
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
        :label="t('dashboard.uptime')"
        :value="formatDuration(system.uptimeSeconds)"
        icon="⏱️"
        color="blue"
        :tooltip="t('dashboard.time.since.the.service.started')"
      />
      <StatCard
        :label="t('dashboard.memory.usage')"
        :value="`${system.memory} MB`"
        icon="💾"
        color="green"
        :tooltip="t('dashboard.memory.used.by.the.current.process')"
      />
      <div class="card group" :title="t('dashboard.active.http.and.websocket.connections')">
        <div class="flex items-center gap-2 text-gray-400 text-sm mb-3">
          <span class="text-base">🔗</span>
          <span> {{ t('dashboard.active.connections') }} </span>
        </div>
        <div class="flex items-baseline gap-3">
          <div class="flex items-baseline gap-1">
            <span class="text-xs text-gray-500">HTTP</span>
            <span class="text-2xl font-bold text-blue-400">{{ system.httpConnections }}</span>
          </div>
          <span class="text-gray-600">|</span>
          <div class="flex items-baseline gap-1">
            <span class="text-xs text-gray-500">WS</span>
            <span class="text-2xl font-bold text-purple-400">{{ system.wsConnections }}</span>
          </div>
        </div>
      </div>
      <StatCard
        :label="t('dashboard.blocked.ips')"
        :value="system.blockedIpCount"
        icon="🚫"
        color="red"
        :tooltip="t('dashboard.currently.blocked.ip.addresses')"
      />
    </div>

    <!-- 最近访问 -->
    <RecentClients :clients="recentClients" />

    <!-- 地理流量分布 -->
    <GeoTrafficMap :stats="geoTraffic" />

    <!-- 封禁列表 -->
    <BlockedIps :initial-data="blockedIps" @refresh="loadData" />
  </div>
</template>
