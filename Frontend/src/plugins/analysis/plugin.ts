import type { RouteRecordRaw } from 'vue-router'

export default {
  path: '/plugins/analysis/cc',
  name: 'CcProtection',
  component: () => import('./CcProtection.vue'),
  meta: { title: 'CC 防护', pluginId: 'analysis' },
} satisfies RouteRecordRaw
