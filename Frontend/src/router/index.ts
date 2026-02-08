import { createRouter, createWebHistory } from 'vue-router'
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
    path: '/api-timing',
    name: 'ApiTiming',
    component: () => import('@/views/ApiTiming.vue'),
    meta: { title: 'API 耗时', icon: '⏱️' }
  },
]

const router = createRouter({
  history: createWebHistory(),
  routes,
})

router.beforeEach((to, _from, next) => {
  document.title = `${to.meta.title || 'LyWaf'} - LyWaf 控制面板`
  next()
})

export default router
