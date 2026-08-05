import type { RouteRecordRaw } from 'vue-router'

export default {
  path: '/plugins/web-tamper',
  name: 'WebTamper',
  component: () => import('./WebTamperPage.vue'),
  meta: { title: '网页防篡改', icon: '🧾' },
} satisfies RouteRecordRaw
