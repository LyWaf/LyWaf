<script setup lang="ts">
interface Props {
  title: string
  id?: string
}

defineProps<Props>()
</script>

<template>
  <section :id="id" class="card mb-6">
    <div class="flex items-center justify-between mb-4 pb-4 border-b border-dark-border">
      <h2 class="text-lg font-semibold text-gray-100">{{ title }}</h2>
      <slot name="actions" />
    </div>
    <slot />
  </section>
</template>
