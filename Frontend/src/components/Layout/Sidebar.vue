<script setup lang="ts">
import { useRoute, useRouter } from 'vue-router'

interface MenuItem {
  path?: string
  name: string
  icon?: string
  children?: MenuItem[]
  action?: string
}

const route = useRoute()
const router = useRouter()

const menuItems: MenuItem[] = [
  { path: '/', name: '统计报表', icon: '📊' },
  { path: '/security', name: '安全态势', icon: '🛡️' },
  { name: '防护应用', icon: '⚙️', action: 'feature-status' },
  { name: '攻击防护', icon: '🔒', action: 'waf-rules' },
  { name: '黑白名单', icon: '📋', action: 'ip-control' },
  { path: '/cc-protection', name: 'CC 防护', icon: '⚡' },
  { name: '地理访问', icon: '🌍', action: 'geo-access' },
  { path: '/api-timing', name: 'API 耗时', icon: '⏱️' },
  { path: '/config', name: '配置文件', icon: '📄' },
  { path: '/cluster-config', name: '配置集群', icon: '⚖️' },
]

const isActive = (item: MenuItem) => {
  if (item.path) {
    return route.path === item.path
  }
  return false
}

const handleClick = (item: MenuItem) => {
  if (item.path) {
    router.push(item.path)
  } else if (item.action) {
    // 滚动到指定区域
    if (route.path !== '/') {
      router.push('/').then(() => {
        setTimeout(() => scrollToSection(item.action!), 100)
      })
    } else {
      scrollToSection(item.action)
    }
  }
}

const scrollToSection = (sectionId: string) => {
  const element = document.getElementById(sectionId)
  if (element) {
    element.scrollIntoView({ behavior: 'smooth', block: 'start' })
  }
}
</script>

<template>
  <aside class="fixed left-0 top-0 h-screen w-[220px] bg-dark-sidebar border-r border-dark-border flex flex-col z-50">
    <!-- Logo -->
    <router-link 
      to="/" 
      class="flex items-center gap-3 px-4 py-5 border-b border-dark-border hover:bg-white/5 transition-colors no-underline"
    >
      <div class="w-9 h-9 rounded-lg bg-gradient-to-br from-primary-500 to-primary-700 flex items-center justify-center text-lg font-bold text-gray-900">
        W
      </div>
      <span class="text-lg font-bold text-gray-100">LyWaf</span>
    </router-link>
    
    <!-- 导航菜单 -->
    <nav class="flex-1 py-3 overflow-y-auto">
      <template v-for="item in menuItems" :key="item.name">
        <!-- 带子菜单的项 -->
        <div v-if="item.children" class="mb-1">
          <div 
            class="flex items-center gap-3 px-5 py-3 text-gray-400 cursor-pointer hover:bg-white/5 hover:text-gray-200 transition-colors"
          >
            <span class="w-5 text-center">{{ item.icon }}</span>
            <span class="flex-1 text-sm">{{ item.name }}</span>
            <span class="text-xs">›</span>
          </div>
          <div class="pl-8">
            <div
              v-for="child in item.children"
              :key="child.name"
              @click="handleClick(child)"
              class="flex items-center gap-3 px-5 py-2 text-gray-500 cursor-pointer hover:bg-white/5 hover:text-gray-300 transition-colors text-sm"
            >
              <span>{{ child.name }}</span>
            </div>
          </div>
        </div>
        
        <!-- 普通菜单项 -->
        <div
          v-else
          @click="handleClick(item)"
          :class="[
            'flex items-center gap-3 px-5 py-3 cursor-pointer transition-colors text-sm border-l-[3px]',
            isActive(item) 
              ? 'bg-primary-500/10 text-primary-500 border-l-primary-500' 
              : 'text-gray-400 border-l-transparent hover:bg-white/5 hover:text-gray-200'
          ]"
        >
          <span class="w-5 text-center">{{ item.icon }}</span>
          <span class="flex-1">{{ item.name }}</span>
        </div>
      </template>
    </nav>
    
    <!-- 底部信息 -->
    <div class="p-4 border-t border-dark-border text-xs text-gray-500">
      <div class="flex items-center gap-2">
        <span class="w-2 h-2 rounded-full bg-green-500 animate-pulse-dot"></span>
        <span>服务运行中</span>
      </div>
    </div>
  </aside>
</template>
