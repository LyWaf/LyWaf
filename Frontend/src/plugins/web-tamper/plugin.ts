import type { RouteRecordRaw } from 'vue-router'
import { t } from './i18n'

export default {
  path: '/plugins/web-tamper',
  name: 'WebTamper',
  component: () => import('./WebTamperPage.vue'),
  meta: { title: () => t('page.1a18e961c05b'), icon: '🧾' },
} satisfies RouteRecordRaw
