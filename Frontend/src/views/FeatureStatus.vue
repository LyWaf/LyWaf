<script setup lang="ts">
import { ref, onMounted } from 'vue'
import AccessControlPanel from '@/components/dashboard/AccessControlPanel.vue'
import WafRules from '@/components/dashboard/WafRules.vue'
import { featureApi, dashboardApi } from '@/api'
import { useToast } from '@/composables/useToast'
import type { FeatureStatus } from '@/types'

const { showSuccess, showError } = useToast()

const features = ref<FeatureStatus>({
  ipControl: false,
  geoControl: false,
  wafArgs: false,
  wafPost: false,
  ccProtection: true,
})
const featureLoading = ref<string | null>(null)

onMounted(async () => {
  try {
    const data = await dashboardApi.getData()
    if (data.success) {
      features.value = data.features
    }
  } catch { /* 静默处理 */ }
})

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
  } catch {
    showError('操作失败，请重试')
  } finally {
    featureLoading.value = null
  }
}

const getFeatureName = (feature: keyof FeatureStatus) => {
  const names: Record<keyof FeatureStatus, string> = {
    ipControl: 'IP 访问控制',
    geoControl: '地理位置控制',
    wafArgs: 'WAF Args检测',
    wafPost: 'WAF Post检测',
    ccProtection: 'CC 防护',
  }
  return names[feature]
}
</script>

<template>
  <div class="space-y-6">
    <!-- IP 访问控制 -->
    <AccessControlPanel mode="ip" />

    <!-- 地理位置访问控制 -->
    <AccessControlPanel mode="geo" />

    <!-- WAF Args 检测 -->
    <WafRules
      rule-type="args"
      :enabled="features.wafArgs"
      :loading="featureLoading === 'wafArgs'"
      @toggle="toggleFeature('wafArgs')"
    />

    <!-- WAF Post 检测 -->
    <WafRules
      rule-type="post"
      :enabled="features.wafPost"
      :loading="featureLoading === 'wafPost'"
      @toggle="toggleFeature('wafPost')"
    />
  </div>
</template>
