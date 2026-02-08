<script setup lang="ts">
import { ref } from 'vue'
import Section from '@/components/common/Section.vue'
import { geoApi } from '@/api'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()

const allowCountries = ref<string[]>([])
const allowRegions = ref<string[]>([])
const denyCountries = ref<string[]>([])
const denyRegions = ref<string[]>([])

// 添加/移除操作
const operations = {
  addAllowCountry: async () => {
    const value = prompt('请输入允许访问的国家代码:')
    if (!value) return
    try {
      const res = await geoApi.addAllowCountry(value)
      if (res.success) {
        allowCountries.value.push(value)
        showSuccess(`已添加允许国家: ${value}`)
      }
    } catch { showError('添加失败') }
  },
  removeAllowCountry: async (value: string) => {
    if (!confirm(`确定要移除 ${value} 吗？`)) return
    try {
      const res = await geoApi.removeAllowCountry(value)
      if (res.success) {
        allowCountries.value = allowCountries.value.filter(i => i !== value)
        showSuccess(`已移除: ${value}`)
      }
    } catch { showError('移除失败') }
  },
  addAllowRegion: async () => {
    const value = prompt('请输入允许访问的地区:')
    if (!value) return
    try {
      const res = await geoApi.addAllowRegion(value)
      if (res.success) {
        allowRegions.value.push(value)
        showSuccess(`已添加允许地区: ${value}`)
      }
    } catch { showError('添加失败') }
  },
  removeAllowRegion: async (value: string) => {
    if (!confirm(`确定要移除 ${value} 吗？`)) return
    try {
      const res = await geoApi.removeAllowRegion(value)
      if (res.success) {
        allowRegions.value = allowRegions.value.filter(i => i !== value)
        showSuccess(`已移除: ${value}`)
      }
    } catch { showError('移除失败') }
  },
  addDenyCountry: async () => {
    const value = prompt('请输入禁止访问的国家代码:')
    if (!value) return
    try {
      const res = await geoApi.addDenyCountry(value)
      if (res.success) {
        denyCountries.value.push(value)
        showSuccess(`已添加禁止国家: ${value}`)
      }
    } catch { showError('添加失败') }
  },
  removeDenyCountry: async (value: string) => {
    if (!confirm(`确定要移除 ${value} 吗？`)) return
    try {
      const res = await geoApi.removeDenyCountry(value)
      if (res.success) {
        denyCountries.value = denyCountries.value.filter(i => i !== value)
        showSuccess(`已移除: ${value}`)
      }
    } catch { showError('移除失败') }
  },
  addDenyRegion: async () => {
    const value = prompt('请输入禁止访问的地区:')
    if (!value) return
    try {
      const res = await geoApi.addDenyRegion(value)
      if (res.success) {
        denyRegions.value.push(value)
        showSuccess(`已添加禁止地区: ${value}`)
      }
    } catch { showError('添加失败') }
  },
  removeDenyRegion: async (value: string) => {
    if (!confirm(`确定要移除 ${value} 吗？`)) return
    try {
      const res = await geoApi.removeDenyRegion(value)
      if (res.success) {
        denyRegions.value = denyRegions.value.filter(i => i !== value)
        showSuccess(`已移除: ${value}`)
      }
    } catch { showError('移除失败') }
  },
}
</script>

<template>
  <Section id="geo-access" title="地理访问控制">
    <div class="grid grid-cols-1 lg:grid-cols-2 gap-6">
      <!-- 允许列表 -->
      <div class="space-y-4">
        <h3 class="text-green-400 font-medium">允许访问</h3>
        
        <!-- 允许国家 -->
        <div class="p-4 bg-dark-card-hover rounded-lg">
          <div class="flex items-center justify-between mb-3">
            <span class="text-gray-400 text-sm">允许国家</span>
            <button @click="operations.addAllowCountry" class="btn btn-sm btn-primary">+ 添加</button>
          </div>
          <div class="flex flex-wrap gap-2">
            <span 
              v-for="item in allowCountries" 
              :key="item"
              class="inline-flex items-center gap-1 px-2 py-1 bg-green-500/20 text-green-400 rounded text-sm"
            >
              {{ item }}
              <button @click="operations.removeAllowCountry(item)" class="hover:text-red-400">×</button>
            </span>
            <span v-if="allowCountries.length === 0" class="text-gray-500 text-sm">无</span>
          </div>
        </div>
        
        <!-- 允许地区 -->
        <div class="p-4 bg-dark-card-hover rounded-lg">
          <div class="flex items-center justify-between mb-3">
            <span class="text-gray-400 text-sm">允许地区</span>
            <button @click="operations.addAllowRegion" class="btn btn-sm btn-primary">+ 添加</button>
          </div>
          <div class="flex flex-wrap gap-2">
            <span 
              v-for="item in allowRegions" 
              :key="item"
              class="inline-flex items-center gap-1 px-2 py-1 bg-green-500/20 text-green-400 rounded text-sm"
            >
              {{ item }}
              <button @click="operations.removeAllowRegion(item)" class="hover:text-red-400">×</button>
            </span>
            <span v-if="allowRegions.length === 0" class="text-gray-500 text-sm">无</span>
          </div>
        </div>
      </div>
      
      <!-- 禁止列表 -->
      <div class="space-y-4">
        <h3 class="text-red-400 font-medium">禁止访问</h3>
        
        <!-- 禁止国家 -->
        <div class="p-4 bg-dark-card-hover rounded-lg">
          <div class="flex items-center justify-between mb-3">
            <span class="text-gray-400 text-sm">禁止国家</span>
            <button @click="operations.addDenyCountry" class="btn btn-sm btn-primary">+ 添加</button>
          </div>
          <div class="flex flex-wrap gap-2">
            <span 
              v-for="item in denyCountries" 
              :key="item"
              class="inline-flex items-center gap-1 px-2 py-1 bg-red-500/20 text-red-400 rounded text-sm"
            >
              {{ item }}
              <button @click="operations.removeDenyCountry(item)" class="hover:text-white">×</button>
            </span>
            <span v-if="denyCountries.length === 0" class="text-gray-500 text-sm">无</span>
          </div>
        </div>
        
        <!-- 禁止地区 -->
        <div class="p-4 bg-dark-card-hover rounded-lg">
          <div class="flex items-center justify-between mb-3">
            <span class="text-gray-400 text-sm">禁止地区</span>
            <button @click="operations.addDenyRegion" class="btn btn-sm btn-primary">+ 添加</button>
          </div>
          <div class="flex flex-wrap gap-2">
            <span 
              v-for="item in denyRegions" 
              :key="item"
              class="inline-flex items-center gap-1 px-2 py-1 bg-red-500/20 text-red-400 rounded text-sm"
            >
              {{ item }}
              <button @click="operations.removeDenyRegion(item)" class="hover:text-white">×</button>
            </span>
            <span v-if="denyRegions.length === 0" class="text-gray-500 text-sm">无</span>
          </div>
        </div>
      </div>
    </div>
  </Section>
</template>
