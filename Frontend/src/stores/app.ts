import { defineStore } from 'pinia'
import { ref } from 'vue'

export const useAppStore = defineStore('app', () => {
  const sidebarCollapsed = ref(false)
  const loading = ref(false)
  
  const toggleSidebar = () => {
    sidebarCollapsed.value = !sidebarCollapsed.value
  }
  
  const setLoading = (value: boolean) => {
    loading.value = value
  }
  
  return {
    sidebarCollapsed,
    loading,
    toggleSidebar,
    setLoading,
  }
})
