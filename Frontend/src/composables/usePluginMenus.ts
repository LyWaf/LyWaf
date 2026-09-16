import { ref } from 'vue'
import { pluginApi } from '@/api'

export const pluginMenus = ref<{ pluginId: string; id: string; name: string; path: string }[]>([])
let revision = 0
export async function refreshPluginMenus() {
  const request = ++revision
  try {
    const result = await pluginApi.getMenus()
    if (request === revision) pluginMenus.value = result.success ? result.menus : []
  } catch {
    if (request === revision) pluginMenus.value = []
  }
}
