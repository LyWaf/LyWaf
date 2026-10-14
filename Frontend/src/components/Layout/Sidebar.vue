<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { pluginMenus, refreshPluginMenus } from '@/composables/usePluginMenus'
import { useRoute, useRouter } from 'vue-router'
import { pcapApi } from '@/api'
import { useAuth } from '@/composables/useAuth'
import { translateText, translateTitle, t } from '@/i18n'

interface MenuItem {
  path?: string
  name: string
  icon?: string
  children?: MenuItem[]
  action?: string
  condition?: () => boolean
}

const route = useRoute()
const router = useRouter()
const { can } = useAuth()

const proxyEnabled = ref(false)
let menuTimer: ReturnType<typeof setInterval> | undefined
onMounted(() => {
  void refreshPluginMenus()
  menuTimer = setInterval(refreshPluginMenus, 15000)
})
onUnmounted(() => clearInterval(menuTimer))

onMounted(async () => {
  try {
    const res = await pcapApi.getStatus() as any
    if (res?.success) {
      proxyEnabled.value = res.proxyEnabled
    }
  } catch { /* 静默处理 */ }
})

const staticMenuItems: MenuItem[] = [
  { path: '/', name: '统计报表', icon: '📊' },
  { path: '/security', name: '安全态势', icon: '🛡️' },
  { path: '/feature-status', name: '防护应用', icon: '⚙️' },
  { path: '/intercept-events', name: '拦截事件', icon: '📋' },
  {
    name: '规则管理', icon: '📐', children: [
      { path: '/ip-control', name: 'IP 管理' },
      { path: '/waf-rules', name: 'WAF 规则' },
      { path: '/speed-limit', name: '速度限制' },
      { path: '/packet-capture', name: '代理管理', condition: () => proxyEnabled.value },
    ]
  },
  { path: '/api-timing', name: 'API 耗时', icon: '⏱️' },
  {
    name: '配置管理', icon: '📄', children: [
      { path: '/sites', name: '站点管理', condition: () => can('system.manage') },
      { path: '/system-health', name: '系统健康', condition: () => can('system.manage') },
      { path: '/config', name: '配置文件' },
      { path: '/config-overview', name: '配置概览' },
      { path: '/cluster-config', name: '配置集群' },
      { path: '/multi-node', name: '节点管理' },
      { path: '/route-config', name: '配置路由' },
      { path: '/fileserver-config', name: '文件服务' },
      { path: '/simpleres-config', name: '简单响应' },
      { path: '/param-config', name: '参数配置' },
      { path: '/error-templates', name: '错误模板' },
    ]
  },
  {
    name: '系统设置', icon: '⚙️', children: [
      { path: '/general-settings', name: '通用设置' },
      { path: '/notifications', name: '通知告警', condition: () => can('system.manage') },
      { path: '/backups', name: '配置备份', condition: () => can('system.manage') },
      { path: '/security-incidents', name: '事件处置' },
      { path: '/management-tasks', name: '任务中心', condition: () => can('system.manage') },
    ]
  },
]
const menuItems = computed<MenuItem[]>(() => [
  ...staticMenuItems,
  {
    name: '插件', icon: '▦', children: [
      { path: '/plugin-management', name: '插件管理' },
      ...pluginMenus.value.map(menu => ({ name: translateTitle(router.resolve(menu.path).meta.title || menu.name), path: menu.path })),
    ]
  },
])

// 展开状态：默认不展开，但当前路由所在分组自动展开
const expandedGroups = ref<Set<string>>(new Set(
  menuItems.value.filter(m => m.children?.some(c => c.path && route.path === c.path)).map(m => m.name)
))
watch(() => route.path, path => {
  void refreshPluginMenus()
  for (const item of menuItems.value) {
    if (item.children?.some(child => child.path === path)) expandedGroups.value.add(item.name)
  }
})
watch(pluginMenus, menus => {
  if (menus.some(menu => menu.path === route.path)) expandedGroups.value.add('插件')
  if (route.path.startsWith('/plugins/') && !menus.some(menu => menu.path === route.path)) {
    void router.replace('/plugin-management')
  }
})

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
        <template v-if="!item.condition || item.condition()">
        <!-- 带子菜单的项 -->
        <div v-if="item.children" class="mb-1">
          <button type="button" :aria-expanded="expandedGroups.has(item.name)"
            @click="toggleGroup(item)"
            :class="[
              'w-full text-left flex items-center gap-3 px-5 py-3 cursor-pointer transition-colors text-sm border-l-[3px]',
              isGroupActive(item)
                ? 'bg-primary-500/5 text-primary-400 border-l-primary-500/50'
                : 'text-gray-400 border-l-transparent hover:bg-white/5 hover:text-gray-200'
            ]"
          >
            <span class="w-5 text-center">{{ item.icon }}</span>
            <span class="flex-1">{{ translateText(item.name) }}</span>
            <span
              class="text-xs transition-transform duration-200"
              :class="expandedGroups.has(item.name) ? 'rotate-90' : ''"
            >›</span>
          </button>
          <div v-show="expandedGroups.has(item.name)" class="overflow-hidden">
            <template
              v-for="child in item.children"
              :key="child.name"
            >
            <button type="button"
              v-if="!child.condition || child.condition()"
              @click="handleClick(child)"
              :class="[
                'w-full text-left flex items-center gap-3 pl-12 pr-5 py-2 cursor-pointer transition-colors text-sm border-l-[3px]',
                isActive(child)
                  ? 'bg-primary-500/10 text-primary-500 border-l-primary-500'
                  : 'text-gray-500 border-l-transparent hover:bg-white/5 hover:text-gray-300'
              ]"
            >
              <span>{{ translateText(child.name) }}</span>
            </button>
            </template>
          </div>
        </div>
        
        <!-- 普通菜单项 -->
        <button type="button"
          v-else
          @click="handleClick(item)"
          :class="[
            'w-full text-left flex items-center gap-3 px-5 py-3 cursor-pointer transition-colors text-sm border-l-[3px]',
            isActive(item) 
              ? 'bg-primary-500/10 text-primary-500 border-l-primary-500' 
              : 'text-gray-400 border-l-transparent hover:bg-white/5 hover:text-gray-200'
          ]"
        >
          <span class="w-5 text-center">{{ item.icon }}</span>
          <span class="flex-1">{{ translateText(item.name) }}</span>
        </button>
        </template>
      </template>
    </nav>
    
    <!-- 底部信息 -->
    <div class="p-4 border-t border-dark-border text-xs text-gray-500">
      <div class="flex items-center gap-2">
        <span class="w-2 h-2 rounded-full bg-green-500 animate-pulse-dot"></span>
        <span>{{ t('common.serviceRunning') }}</span>
      </div>
    </div>
  </aside>
</template>
