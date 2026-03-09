import { createRouter, createWebHashHistory } from 'vue-router'
import type { RouteRecordRaw } from 'vue-router'

const routes: RouteRecordRaw[] = [
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
    name: 'CcProtection',
    component: () => import('@/views/CcProtection.vue'),
    meta: { title: 'CC 防护', icon: '🛡️' }
  },
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
    path: '/stats-config',
    name: 'StatsConfig',
    component: () => import('@/views/StatsConfig.vue'),
    meta: { title: '统计配置', icon: '📈' }
  },
  {
    path: '/error-templates',
    name: 'ErrorTemplates',
    component: () => import('@/views/ErrorTemplates.vue'),
    meta: { title: '错误模板', icon: '🚫' }
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

router.beforeEach((to, _from, next) => {
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

  next()
})

export default router
