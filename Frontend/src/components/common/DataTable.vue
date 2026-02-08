<script setup lang="ts">
interface Column {
  key: string
  label: string
  width?: string
  align?: 'left' | 'center' | 'right'
}

interface Props {
  columns: Column[]
  data: Record<string, unknown>[]
  emptyText?: string
}

withDefaults(defineProps<Props>(), {
  emptyText: '暂无数据',
})
</script>

<template>
  <div class="overflow-x-auto">
    <table class="w-full">
      <thead>
        <tr class="border-b border-dark-border">
          <th 
            v-for="col in columns" 
            :key="col.key"
            :style="{ width: col.width }"
            :class="[
              'px-4 py-3 text-sm font-medium text-gray-400',
              col.align === 'center' ? 'text-center' : col.align === 'right' ? 'text-right' : 'text-left'
            ]"
          >
            {{ col.label }}
          </th>
        </tr>
      </thead>
      <tbody>
        <tr 
          v-for="(row, index) in data" 
          :key="index"
          class="border-b border-dark-border/50 hover:bg-dark-card-hover transition-colors"
        >
          <td 
            v-for="col in columns" 
            :key="col.key"
            :class="[
              'px-4 py-3 text-sm text-gray-300',
              col.align === 'center' ? 'text-center' : col.align === 'right' ? 'text-right' : 'text-left'
            ]"
          >
            <slot :name="col.key" :row="row" :value="row[col.key]">
              {{ row[col.key] }}
            </slot>
          </td>
        </tr>
        <tr v-if="data.length === 0">
          <td :colspan="columns.length" class="px-4 py-8 text-center text-gray-500">
            {{ emptyText }}
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
