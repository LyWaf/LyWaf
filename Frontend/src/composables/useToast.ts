import { ref } from 'vue'

interface Toast {
  id: number
  type: 'success' | 'error' | 'warning' | 'info'
  message: string
}

const toasts = ref<Toast[]>([])
let toastId = 0

export function useToast() {
  const show = (type: Toast['type'], message: string, duration = 3000) => {
    const id = ++toastId
    toasts.value.push({ id, type, message })
    
    setTimeout(() => {
      remove(id)
    }, duration)
    
    return id
  }
  
  const remove = (id: number) => {
    const index = toasts.value.findIndex(t => t.id === id)
    if (index > -1) {
      toasts.value.splice(index, 1)
    }
  }
  
  const showSuccess = (message: string) => show('success', message)
  const showError = (message: string) => show('error', message)
  const showWarning = (message: string) => show('warning', message)
  const showInfo = (message: string) => show('info', message)
  
  return {
    toasts,
    show,
    remove,
    showSuccess,
    showError,
    showWarning,
    showInfo,
  }
}
