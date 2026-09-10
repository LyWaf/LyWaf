<script setup lang="ts">
const page = defineModel<number>('page', { required: true })
const search = defineModel<string>('search', { required: true })
const props = withDefaults(defineProps<{ total: number; busy?: boolean; pageSize?: number }>(), { pageSize: 20 })
const emit = defineEmits<{ refresh: [] }>()
function navigate(delta: number) {
  if (props.busy) return
  page.value += delta
  emit('refresh')
}
function submit() {
  page.value = 1
  emit('refresh')
}
</script>
<template>
  <div class="flex flex-wrap items-center justify-between gap-3 mb-4">
    <form class="flex gap-2 w-full sm:flex-1 min-w-0 sm:max-w-md" @submit.prevent="submit">
      <input v-model="search" class="input min-w-0" maxlength="200" aria-label="搜索列表" placeholder="搜索" />
      <button class="btn btn-secondary shrink-0" :disabled="busy">搜索</button>
    </form>
    <nav class="flex items-center justify-end gap-3 text-sm w-full sm:w-auto shrink-0" aria-label="列表分页">
      <span class="text-gray-400">共 {{ total }} 条 · 第 {{ page }} 页</span>
      <button type="button" class="btn btn-secondary w-10 h-10 p-0" title="上一页" aria-label="上一页" :disabled="busy || page <= 1" @click="navigate(-1)">←</button>
      <button type="button" class="btn btn-secondary w-10 h-10 p-0" title="下一页" aria-label="下一页" :disabled="busy || page * pageSize >= total" @click="navigate(1)">→</button>
    </nav>
  </div>
</template>
