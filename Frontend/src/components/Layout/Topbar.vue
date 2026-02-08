<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'

const route = useRoute()
const router = useRouter()

const breadcrumbs = computed(() => {
  const items = [{ name: 'LyWaf', path: '/' }]
  
  if (route.meta.title) {
    items.push({ 
      name: route.meta.title as string, 
      path: route.path 
    })
  }
  
  return items
})

const goTo = (path: string) => {
  router.push(path)
}
</script>

<template>
  <header class="h-14 bg-dark-bg-secondary border-b border-dark-border px-6 flex items-center justify-between sticky top-0 z-40">
    <!-- 面包屑 -->
    <nav class="flex items-center gap-2 text-sm">
      <template v-for="(item, index) in breadcrumbs" :key="item.path">
        <span v-if="index > 0" class="text-gray-600">›</span>
        <span 
          v-if="index === breadcrumbs.length - 1"
          class="text-gray-100 font-medium"
        >
          {{ item.name }}
        </span>
        <a 
          v-else
          @click="goTo(item.path)"
          class="text-gray-400 hover:text-primary-500 cursor-pointer transition-colors"
        >
          {{ item.name }}
        </a>
      </template>
    </nav>
    
    <!-- 右侧状态 -->
    <div class="flex items-center gap-4">
      <div class="flex items-center gap-2 text-sm">
        <span class="w-2 h-2 rounded-full bg-green-500 animate-pulse-dot"></span>
        <span class="text-gray-300">运行中</span>
      </div>
    </div>
  </header>
</template>
