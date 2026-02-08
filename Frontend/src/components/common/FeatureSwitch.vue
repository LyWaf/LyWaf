<script setup lang="ts">
interface Props {
  label: string
  enabled: boolean
  loading?: boolean
}

withDefaults(defineProps<Props>(), {
  loading: false,
})

const emit = defineEmits<{
  (e: 'toggle'): void
}>()
</script>

<template>
  <div 
    @click="!loading && emit('toggle')"
    :class="[
      'flex items-center justify-between p-4 rounded-lg border cursor-pointer transition-all',
      enabled 
        ? 'bg-green-500/10 border-green-500/30 hover:bg-green-500/20' 
        : 'bg-dark-card border-dark-border hover:bg-dark-card-hover'
    ]"
  >
    <span class="text-gray-200">{{ label }}</span>
    <div class="flex items-center gap-2">
      <span v-if="loading" class="text-gray-400 text-sm">切换中...</span>
      <span 
        :class="[
          'px-2 py-0.5 rounded text-xs font-medium',
          enabled 
            ? 'bg-green-500/20 text-green-400' 
            : 'bg-gray-500/20 text-gray-400'
        ]"
      >
        {{ enabled ? '已启用' : '已禁用' }}
      </span>
    </div>
  </div>
</template>
