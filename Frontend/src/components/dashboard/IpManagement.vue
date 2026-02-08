<script setup lang="ts">
import { ref } from 'vue'
import Section from '@/components/common/Section.vue'
import { ipApi } from '@/api'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()

const whitelist = ref<string[]>([])
const blacklist = ref<string[]>([])

// 白名单操作
const addWhitelistIp = async () => {
  const ip = prompt('请输入要添加到白名单的 IP 或 CIDR:')
  if (!ip) return
  
  try {
    const res = await ipApi.addWhitelist(ip)
    if (res.success) {
      whitelist.value.push(ip)
      showSuccess(`已添加到白名单: ${ip}`)
    } else {
      showError(res.message || '添加失败')
    }
  } catch {
    showError('添加失败')
  }
}

const removeWhitelistIp = async (ip: string) => {
  if (!confirm(`确定要从白名单移除 ${ip} 吗？`)) return
  
  try {
    const res = await ipApi.removeWhitelist(ip)
    if (res.success) {
      whitelist.value = whitelist.value.filter(i => i !== ip)
      showSuccess(`已从白名单移除: ${ip}`)
    } else {
      showError(res.message || '移除失败')
    }
  } catch {
    showError('移除失败')
  }
}

// 黑名单操作
const addBlacklistIp = async () => {
  const ip = prompt('请输入要添加到黑名单的 IP 或 CIDR:')
  if (!ip) return
  
  try {
    const res = await ipApi.addBlacklist(ip)
    if (res.success) {
      blacklist.value.push(ip)
      showSuccess(`已添加到黑名单: ${ip}`)
    } else {
      showError(res.message || '添加失败')
    }
  } catch {
    showError('添加失败')
  }
}

const removeBlacklistIp = async (ip: string) => {
  if (!confirm(`确定要从黑名单移除 ${ip} 吗？`)) return
  
  try {
    const res = await ipApi.removeBlacklist(ip)
    if (res.success) {
      blacklist.value = blacklist.value.filter(i => i !== ip)
      showSuccess(`已从黑名单移除: ${ip}`)
    } else {
      showError(res.message || '移除失败')
    }
  } catch {
    showError('移除失败')
  }
}
</script>

<template>
  <Section id="ip-control" title="IP 访问控制">
    <div class="grid grid-cols-1 lg:grid-cols-2 gap-6">
      <!-- 白名单 -->
      <div>
        <div class="flex items-center justify-between mb-4">
          <h3 class="text-gray-300 font-medium">
            IP 白名单 
            <span class="badge badge-success ml-2">{{ whitelist.length }}</span>
          </h3>
          <button @click="addWhitelistIp" class="btn btn-sm btn-primary">+ 添加</button>
        </div>
        <div class="space-y-2 max-h-[200px] overflow-y-auto">
          <div 
            v-for="ip in whitelist" 
            :key="ip"
            class="flex items-center justify-between p-3 bg-dark-card-hover rounded-lg"
          >
            <code class="text-green-400 text-sm">{{ ip }}</code>
            <button 
              @click="removeWhitelistIp(ip)"
              class="text-gray-500 hover:text-red-400 transition-colors"
            >
              ×
            </button>
          </div>
          <div v-if="whitelist.length === 0" class="text-gray-500 text-sm text-center py-4">
            暂无白名单 IP
          </div>
        </div>
      </div>
      
      <!-- 黑名单 -->
      <div>
        <div class="flex items-center justify-between mb-4">
          <h3 class="text-gray-300 font-medium">
            IP 黑名单 
            <span class="badge badge-danger ml-2">{{ blacklist.length }}</span>
          </h3>
          <button @click="addBlacklistIp" class="btn btn-sm btn-primary">+ 添加</button>
        </div>
        <div class="space-y-2 max-h-[200px] overflow-y-auto">
          <div 
            v-for="ip in blacklist" 
            :key="ip"
            class="flex items-center justify-between p-3 bg-dark-card-hover rounded-lg"
          >
            <code class="text-red-400 text-sm">{{ ip }}</code>
            <button 
              @click="removeBlacklistIp(ip)"
              class="text-gray-500 hover:text-red-400 transition-colors"
            >
              ×
            </button>
          </div>
          <div v-if="blacklist.length === 0" class="text-gray-500 text-sm text-center py-4">
            暂无黑名单 IP
          </div>
        </div>
      </div>
    </div>
  </Section>
</template>
