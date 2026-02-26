<script setup lang="ts">
import { ref } from 'vue'
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
  { path: '/feature-status', name: '防护应用', icon: '⚙️' },
  { path: '/cc-protection', name: 'CC 防护', icon: '⚡' },
  { path: '/api-timing', name: 'API 耗时', icon: '⏱️' },
  {
    name: '配置管理', icon: '📄', children: [
      { path: '/config', name: '配置文件' },
      { path: '/cluster-config', name: '配置集群' },
      { path: '/route-config', name: '配置路由' },
      { path: '/fileserver-config', name: '文件服务' },
      { path: '/simpleres-config', name: '简单响应' },
    ]
  },
]

// 展开状态：默认展开「配置管理」
const expandedGroups = ref<Set<string>>(new Set(['配置管理']))

const isActive = (item: MenuItem) => {
  if (item.path) {
    return route.path === item.path
  }
  return false
}

const isGroupActive = (item: MenuItem) => {
  return item.children?.some(c => c.path && route.path === c.path)
}

const toggleGroup = (item: MenuItem) => {
  if (expandedGroups.value.has(item.name)) {
    expandedGroups.value.delete(item.name)
  } else {
    expandedGroups.value.add(item.name)
  }
}

const handleClick = (item: MenuItem) => {
  if (item.path) {
    router.push(item.path)
  } else if (item.action) {
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
            @click="toggleGroup(item)"
            :class="[
              'flex items-center gap-3 px-5 py-3 cursor-pointer transition-colors text-sm border-l-[3px]',
              isGroupActive(item)
                ? 'bg-primary-500/5 text-primary-400 border-l-primary-500/50'
                : 'text-gray-400 border-l-transparent hover:bg-white/5 hover:text-gray-200'
            ]"
          >
            <span class="w-5 text-center">{{ item.icon }}</span>
            <span class="flex-1">{{ item.name }}</span>
            <span
              class="text-xs transition-transform duration-200"
              :class="expandedGroups.has(item.name) ? 'rotate-90' : ''"
            >›</span>
          </div>
          <div v-show="expandedGroups.has(item.name)" class="overflow-hidden">
            <div
              v-for="child in item.children"
              :key="child.name"
              @click="handleClick(child)"
              :class="[
                'flex items-center gap-3 pl-12 pr-5 py-2 cursor-pointer transition-colors text-sm border-l-[3px]',
                isActive(child)
                  ? 'bg-primary-500/10 text-primary-500 border-l-primary-500'
                  : 'text-gray-500 border-l-transparent hover:bg-white/5 hover:text-gray-300'
              ]"
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
