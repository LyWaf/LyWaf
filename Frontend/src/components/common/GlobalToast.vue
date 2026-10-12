<script setup lang="ts">
import { useToast } from '@/composables/useToast'
import { translateText } from '@/i18n'

const { toasts, remove } = useToast()

const iconMap = {
  success: '✓',
  error: '✕',
  warning: '⚠',
  info: 'ℹ',
}

const colorMap = {
  success: 'bg-green-500/20 border-green-500/30 text-green-400',
  error: 'bg-red-500/20 border-red-500/30 text-red-400',
  warning: 'bg-yellow-500/20 border-yellow-500/30 text-yellow-400',
  info: 'bg-blue-500/20 border-blue-500/30 text-blue-400',
}
</script>

<template>
  <Teleport to="body">
    <div class="fixed top-4 left-4 right-4 sm:left-auto z-[9999] flex flex-col items-end gap-2 pointer-events-none">
      <TransitionGroup name="toast">
        <div
          v-for="toast in toasts"
          :key="toast.id"
          :class="[
            'flex items-start gap-3 px-4 py-3 rounded-lg border shadow-lg w-full sm:w-auto sm:min-w-[280px] sm:max-w-[640px] backdrop-blur-sm pointer-events-auto',
            colorMap[toast.type]
          ]"
        >
          <span class="text-lg leading-5 shrink-0">{{ iconMap[toast.type] }}</span>
          <span class="flex-1 text-sm whitespace-pre-line break-words min-w-0">{{ translateText(toast.message) }}</span>
          <button 
            @click="remove(toast.id)"
            class="text-current opacity-60 hover:opacity-100 transition-opacity shrink-0 leading-5"
          >
            ✕
          </button>
        </div>
      </TransitionGroup>
    </div>
  </Teleport>
</template>

<style scoped>
.toast-enter-active,
.toast-leave-active {
  transition: all 0.3s ease;
}

.toast-enter-from {
  opacity: 0;
  transform: translateX(100%);
}

.toast-leave-to {
  opacity: 0;
  transform: translateX(100%);
}
</style>
