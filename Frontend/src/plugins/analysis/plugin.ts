import type { RouteRecordRaw } from 'vue-router'
import { t } from './i18n'

export default {
  path: '/plugins/analysis/cc',
  name: 'CcProtection',
  component: () => import('./CcProtection.vue'),
  meta: { title: () => t('title'), pluginId: 'analysis' },
} satisfies RouteRecordRaw
