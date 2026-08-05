import type { RouteRecordRaw } from 'vue-router'

type PluginRouteModule = { default: RouteRecordRaw }

const modules = import.meta.glob<PluginRouteModule>('./*/plugin.ts', { eager: true })

export const pluginRoutes: RouteRecordRaw[] = Object.values(modules).map(module => module.default)
