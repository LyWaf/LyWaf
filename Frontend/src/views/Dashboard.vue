<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import StatCard from '@/components/common/StatCard.vue'
import Section from '@/components/common/Section.vue'
import FeatureSwitch from '@/components/common/FeatureSwitch.vue'
import TrafficAnalysis from '@/components/dashboard/TrafficAnalysis.vue'
import IpManagement from '@/components/dashboard/IpManagement.vue'
import GeoAccess from '@/components/dashboard/GeoAccess.vue'
import WafRules from '@/components/dashboard/WafRules.vue'
import CcProtection from '@/components/dashboard/CcProtection.vue'
import BlockedIps from '@/components/dashboard/BlockedIps.vue'
import { featureApi, trafficApi } from '@/api'
import { useToast } from '@/composables/useToast'
import type { FeatureStatus, TrafficStats, SystemStatus } from '@/types'

const { showSuccess, showError } = useToast()

// 数据
const loading = ref(false)
const features = ref<FeatureStatus>({
  ipControl: false,
  geoControl: false,
  wafArgs: false,
  wafPost: false,
  ccProtection: true,
})
const traffic = ref<TrafficStats | null>(null)
const system = ref<SystemStatus>({
  uptime: '-',
  memory: 0,
  totalConnections: 0,
  blockedIpCount: 0,
  processStartTime: '-',
})

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
    // 这里应该调用后端 API 获取数据
    // 目前使用模拟数据
  } catch (error) {
    console.error('加载数据失败:', error)
  } finally {
    loading.value = false
  }
}

// 功能开关
const featureLoading = ref<string | null>(null)

const toggleFeature = async (feature: keyof FeatureStatus) => {
  featureLoading.value = feature
  const newValue = !features.value[feature]
  
  try {
    const apiMap = {
      ipControl: featureApi.toggleIpControl,
      geoControl: featureApi.toggleGeoControl,
      wafArgs: featureApi.toggleWafArgs,
      wafPost: featureApi.toggleWafPost,
      ccProtection: () => Promise.resolve({ success: true }),
    }
    
    const result = await apiMap[feature](newValue) as { success: boolean }
    if (result.success) {
      features.value[feature] = newValue
      showSuccess(`${getFeatureName(feature)}已${newValue ? '启用' : '禁用'}`)
    }
  } catch (error) {
    showError('操作失败，请重试')
  } finally {
    featureLoading.value = null
  }
}

const getFeatureName = (feature: keyof FeatureStatus) => {
  const names = {
    ipControl: 'IP 访问控制',
    geoControl: '地理位置控制',
    wafArgs: 'WAF Args检测',
    wafPost: 'WAF Post检测',
    ccProtection: 'CC 防护',
  }
  return names[feature]
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
    <!-- 标签切换 -->
    <div class="flex gap-2 border-b border-dark-border pb-4">
      <button class="btn btn-primary">流量分析</button>
      <router-link to="/security" class="btn btn-secondary">安全态势</router-link>
    </div>
    
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
    
    <!-- 功能状态 -->
    <Section id="feature-status" title="功能状态">
      <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
        <FeatureSwitch
          v-for="(enabled, key) in features"
          :key="key"
          :label="getFeatureName(key as keyof FeatureStatus)"
          :enabled="enabled"
          :loading="featureLoading === key"
          @toggle="toggleFeature(key as keyof FeatureStatus)"
        />
      </div>
    </Section>
    
    <!-- IP 管理 -->
    <IpManagement />
    
    <!-- 地理访问 -->
    <GeoAccess />
    
    <!-- WAF 规则 -->
    <WafRules />
    
    <!-- CC 防护 -->
    <CcProtection />
    
    <!-- 封禁列表 -->
    <BlockedIps />
  </div>
</template>
