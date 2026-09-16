import { createRouter, createWebHashHistory } from 'vue-router'
import type { RouteRecordRaw } from 'vue-router'
import { pluginRoutes } from '@/plugins'
import { pluginMenus, refreshPluginMenus } from '@/composables/usePluginMenus'

const routes: RouteRecordRaw[] = [
  { path: '/notifications', name: 'Notifications', component: () => import('@/views/Notifications.vue'), meta: { title: '通知告警' } },
  { path: '/backups', name: 'Backups', component: () => import('@/views/Backups.vue'), meta: { title: '配置备份' } },
  { path: '/security-incidents', name: 'SecurityIncidents', component: () => import('@/views/SecurityIncidents.vue'), meta: { title: '事件处置' } },
  { path: '/sites', name: 'Sites', component: () => import('@/views/Sites.vue'), meta: { title: '站点管理' } },
  { path: '/system-health', name: 'SystemHealth', component: () => import('@/views/SystemHealth.vue'), meta: { title: '系统健康' } },
  { path: '/management-tasks', name: 'ManagementTasks', component: () => import('@/views/ManagementTasks.vue'), meta: { title: '任务中心' } },
  {
    path: '/login',
    name: 'Login',
    component: () => import('@/views/Login.vue'),
    meta: { title: '登录' }
  },
  {
    path: '/',
    name: 'Dashboard',
    component: () => import('@/views/Dashboard.vue'),
    meta: { title: '统计报表', icon: '📊' }
  },
  {
    path: '/security',
    name: 'Security',
    component: () => import('@/views/Security.vue'),
    meta: { title: '安全态势', icon: '🛡️' }
  },
  {
    path: '/feature-status',
    name: 'FeatureStatus',
    component: () => import('@/views/FeatureStatus.vue'),
    meta: { title: '防护应用', icon: '⚙️' }
  },
  {
    path: '/cc-protection',
    redirect: '/plugins/analysis/cc',
  },
  {
    path: '/ip-control',
    name: 'IpControl',
    component: () => import('@/views/IpControl.vue'),
    meta: { title: 'IP 管理', icon: '🌐' }
  },
  ...pluginRoutes,
  {
    path: '/api-timing',
    name: 'ApiTiming',
    component: () => import('@/views/ApiTiming.vue'),
    meta: { title: 'API 耗时', icon: '⏱️' }
  },
  {
    path: '/intercept-events',
    name: 'InterceptEvents',
    component: () => import('@/views/InterceptEvents.vue'),
    meta: { title: '拦截事件', icon: '📋' }
  },
  {
    path: '/config',
    name: 'ConfigEditor',
    component: () => import('@/views/ConfigEditor.vue'),
    meta: { title: '配置文件', icon: '📄' }
  },
  {
    path: '/config-overview',
    name: 'ConfigOverview',
    component: () => import('@/views/ConfigOverview.vue'),
    meta: { title: '配置概览', icon: '📋' }
  },
  {
    path: '/cluster-config',
    name: 'ClusterConfig',
    component: () => import('@/views/ClusterConfig.vue'),
    meta: { title: '配置集群', icon: '⚖️' }
  },
  {
    path: '/multi-node',
    name: 'MultiNode',
    component: () => import('@/views/MultiNode.vue'),
    meta: { title: '节点管理', icon: '◫' }
  },
  {
    path: '/route-config',
    name: 'RouteConfig',
    component: () => import('@/views/RouteConfig.vue'),
    meta: { title: '配置路由', icon: '🔀' }
  },
  {
    path: '/fileserver-config',
    name: 'FileServerConfig',
    component: () => import('@/views/FileServerConfig.vue'),
    meta: { title: '文件服务', icon: '📁' }
  },
  {
    path: '/simpleres-config',
    name: 'SimpleResConfig',
    component: () => import('@/views/SimpleResConfig.vue'),
    meta: { title: '简单响应', icon: '💬' }
  },
  {
    path: '/plugin-management',
    name: 'PluginManagement',
    component: () => import('@/views/PluginManagement.vue'),
    meta: { title: '插件管理', icon: '🧩' }
  },
  {
    path: '/waf-rules',
    name: 'WafRules',
    component: () => import('@/views/WafRules.vue'),
    meta: { title: 'WAF 规则', icon: '🛡️' }
  },
  {
    path: '/param-config',
    name: 'ParamConfig',
    component: () => import('@/views/ParamConfig.vue'),
    meta: { title: '参数配置', icon: '🔧' }
  },
  {
    path: '/error-templates',
    name: 'ErrorTemplates',
    component: () => import('@/views/ErrorTemplates.vue'),
    meta: { title: '错误模板', icon: '🚫' }
  },
  {
    path: '/speed-limit',
    name: 'SpeedLimit',
    component: () => import('@/views/SpeedLimit.vue'),
    meta: { title: '速度限制', icon: '🚀' }
  },
  {
    path: '/packet-capture',
    name: 'PacketCapture',
    component: () => import('@/views/PacketCapture.vue'),
    meta: { title: '代理管理', icon: '📦' }
  },
  {
    path: '/general-settings',
    name: 'GeneralSettings',
    component: () => import('@/views/GeneralSettings.vue'),
    meta: { title: '通用设置', icon: '⚙️' }
  },
]

const router = createRouter({
  history: createWebHashHistory(), // 使用 hash 模式，支持任意目录部署
  routes,
})

router.beforeEach(async (to, _from, next) => {
  document.title = `${to.meta.title || 'LyWaf'} - LyWaf 控制面板`

  const token = localStorage.getItem('lywaf_token')

  // 登录页：已登录则跳到首页
  if (to.path === '/login') {
    if (token) {
      next('/')
    } else {
      next()
    }
    return
  }

  // 其他页面：未登录则跳转登录
  if (!token) {
    next('/login')
    return
  }

  if (to.path.startsWith('/plugins/')) {
    await refreshPluginMenus()
    if (!pluginMenus.value.some(menu => menu.path === to.path)) {
      next('/plugin-management')
      return
    }
  }
  next()
})

export default router
