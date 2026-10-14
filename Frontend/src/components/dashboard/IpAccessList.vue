<script setup lang="ts">
import { t } from '@/i18n'
import { ref, watch, onBeforeUnmount } from 'vue'
import { ipApi, type IpGroup } from '@/api'
import ListToolbar from '@/components/common/ListToolbar.vue'
import { useToast } from '@/composables/useToast'

const props = defineProps<{ target: 'white' | 'black'; revision: number }>()
const emit = defineEmits<{ add: []; remove: [ip: string]; usage: [group: IpGroup] }>()
const { showError } = useToast()
const page = ref(1)
const search = ref('')
const total = ref(0)
const busy = ref(false)
const items = ref<Array<{ id: string; name: string; ip: string | null; group: IpGroup | null }>>([])
const expanded = ref(new Set<string>())
const memberPages = ref<Record<string, number>>({})
let generation = 0
onBeforeUnmount(() => { generation++ })

async function load() {
  const current = ++generation
  busy.value = true
  try {
    const response = await ipApi.listAccess(props.target === 'white' ? 'whitelist' : 'blacklist', page.value, search.value)
    if (current !== generation) return
    items.value = response.data
    total.value = response.total
    for (const item of response.data) {
      if (item.group) memberPages.value[item.id] = Math.min(memberPages.value[item.id] || 1, Math.max(1, Math.ceil(item.group.entries.length / 100)))
    }
    if (!response.data.length && page.value > 1) {
      page.value = Math.max(1, Math.ceil(response.total / 20))
      await load()
    }
  } catch {
    if (current === generation) showError(t('page.d82de47b2661'))
  } finally {
    if (current === generation) busy.value = false
  }
}
function toggle(id: string) {
  const next = new Set(expanded.value)
  next.has(id) ? next.delete(id) : next.add(id)
  expanded.value = next
}
watch(() => props.revision, load, { immediate: true })
</script>

<template>
  <div class="min-w-0">
    <div class="flex items-center justify-between gap-3 mb-4">
      <h3 class="text-gray-300 font-medium">IP {{ target === 'white' ? t('page.3366645622a7') : t('page.59650f41f74b') }}{{ t('page.af488f5f257b') }} <span class="text-gray-500">{{ total }}</span></h3>
      <button class="btn btn-sm btn-primary" @click="emit('add')">{{ t('page.42bb5c118d25') }}</button>
    </div>
    <ListToolbar v-model:page="page" v-model:search="search" :total="total" :busy="busy" @refresh="load" />
    <div class="divide-y divide-dark-border max-h-96 overflow-y-auto" :aria-busy="busy">
      <div v-for="item in items" :key="item.id" class="py-3">
        <div class="flex items-center justify-between gap-3">
          <button v-if="item.group" class="flex min-w-0 flex-1 items-center gap-2 text-left" :aria-expanded="expanded.has(item.id)" @click="toggle(item.id)">
            <span class="w-4 shrink-0" aria-hidden="true">{{ expanded.has(item.id) ? '-' : '+' }}</span>
            <span class="min-w-0 break-all" :class="target === 'white' ? 'text-green-400' : 'text-red-400'">
              {{ item.name }} <span class="text-xs text-gray-500">{{ t('page.e31c3adefea6') }} {{ item.group.entries.length }} {{ t('page.f004f1d84cf9') }}</span>
            </span>
          </button>
          <code v-else class="min-w-0 break-all text-sm" :class="target === 'white' ? 'text-green-400' : 'text-red-400'">{{ item.ip }}</code>
          <button class="w-8 h-8 shrink-0 text-gray-400 hover:text-red-400" :title="t('page.e4ced2bada48')" :aria-label="t('page.e4ced2bada48')" @click="item.group ? emit('usage', item.group) : emit('remove', item.ip!)">×</button>
        </div>
        <div v-if="item.group && expanded.has(item.id)" class="pl-6 pt-3 space-y-2">
          <code v-for="entry in item.group.entries.slice(((memberPages[item.id] || 1) - 1) * 100, (memberPages[item.id] || 1) * 100)" :key="entry" class="block text-xs text-gray-300 break-all">{{ entry }}</code>
          <div v-if="item.group.entries.length > 100" class="flex items-center justify-end gap-3 text-xs">
            <button :disabled="(memberPages[item.id] || 1) <= 1" class="btn btn-secondary" @click="memberPages[item.id]--">{{ t('common.previous') }}</button>
            <span>{{ memberPages[item.id] || 1 }} / {{ Math.ceil(item.group.entries.length / 100) }}</span>
            <button :disabled="(memberPages[item.id] || 1) * 100 >= item.group.entries.length" class="btn btn-secondary" @click="memberPages[item.id] = (memberPages[item.id] || 1) + 1">{{ t('common.next') }}</button>
          </div>
          <span v-if="!item.group.entries.length" class="text-xs text-gray-500">{{ t('page.604988d80428') }}</span>
        </div>
      </div>
      <div v-if="!items.length" class="py-6 text-center text-gray-500">{{ busy ? t('common.loading') : t('page.8bc40d20c0c3') }}</div>
    </div>
  </div>
</template>
