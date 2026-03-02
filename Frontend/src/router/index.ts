import { createRouter, createWebHashHistory } from 'vue-router'
import type { RouteRecordRaw } from 'vue-router'

const routes: RouteRecordRaw[] = [
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
]

const router = createRouter({
  history: createWebHashHistory(), // 使用 hash 模式，支持任意目录部署
  routes,
})

router.beforeEach((to, _from, next) => {
  document.title = `${to.meta.title || 'LyWaf'} - LyWaf 控制面板`
  next()
})

export default router
