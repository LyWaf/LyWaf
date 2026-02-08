<script setup lang="ts">
interface Props {
  label: string
  value: string | number
  icon?: string
  color?: 'default' | 'blue' | 'green' | 'red' | 'yellow' | 'purple'
  tooltip?: string
}

withDefaults(defineProps<Props>(), {
  color: 'default',
  icon: '',
  tooltip: '',
})

const colorClasses: Record<string, string> = {
  default: 'text-gray-100',
  blue: 'text-blue-400',
  green: 'text-green-400',
  red: 'text-red-400',
  yellow: 'text-yellow-400',
  purple: 'text-purple-400',
}
</script>

<template>
  <div 
    class="card group"
    :title="tooltip"
  >
    <div class="flex items-center gap-2 text-gray-400 text-sm mb-3">
      <span v-if="icon" class="text-base">{{ icon }}</span>
      <span>{{ label }}</span>
    </div>
    <div 
      :class="['text-2xl font-bold transition-transform group-hover:scale-105', colorClasses[color]]"
    >
      {{ value }}
    </div>
  </div>
</template>
