<script setup lang="ts">
import { t } from '@/i18n'
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
      <input v-model="search" class="input min-w-0" maxlength="200" :aria-label="t('settings.search.list')" :placeholder="t('common.search')" />
      <button class="btn btn-secondary shrink-0" :disabled="busy">{{ t('common.search') }}</button>
    </form>
    <nav class="flex items-center justify-end gap-3 text-sm w-full sm:w-auto shrink-0" :aria-label="t('settings.list.pagination')">
      <span class="text-gray-400">{{ t('common.pagination', { count: total, page }) }}</span>
      <button type="button" class="btn btn-secondary w-10 h-10 p-0" :title="t('common.previous')" :aria-label="t('common.previous')" :disabled="busy || page <= 1" @click="navigate(-1)">←</button>
      <button type="button" class="btn btn-secondary w-10 h-10 p-0" :title="t('common.next')" :aria-label="t('common.next')" :disabled="busy || page * pageSize >= total" @click="navigate(1)">→</button>
    </nav>
  </div>
</template>
