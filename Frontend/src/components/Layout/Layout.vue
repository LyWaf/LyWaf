<script setup lang="ts">
import { t } from '@/i18n'
import Sidebar from './Sidebar.vue'
import Topbar from './Topbar.vue'
import { ref, watch } from 'vue'
import { useRoute } from 'vue-router'
const route = useRoute()
const navigationOpen = ref(false)
watch(() => route.fullPath, () => { navigationOpen.value = false })
</script>

<template>
  <div class="flex h-screen bg-dark-bg overflow-hidden" @keydown.esc="navigationOpen = false">
    <!-- 侧边栏 -->
    <button v-if="navigationOpen" class="fixed inset-0 bg-black/60 z-40 md:hidden" :aria-label="t('common.close.navigation')" @click="navigationOpen = false" />
    <Sidebar :class="navigationOpen ? 'flex' : 'hidden md:flex'" />

    <!-- 主内容区 -->
    <div class="flex-1 md:ml-[220px] flex flex-col min-h-0 min-w-0">
      <!-- 顶部栏 -->
      <Topbar :navigation-open="navigationOpen" @toggle-navigation="navigationOpen = !navigationOpen" />

      <!-- 内容 -->
      <main class="flex-1 p-4 md:p-6 overflow-y-auto min-h-0 min-w-0">
        <slot />
      </main>
      
      <!-- 页脚 -->
      <footer class="text-center py-4 text-gray-500 text-sm border-t border-dark-border">
        <p>{{ t('common.productName') }} · {{ new Date().getFullYear() }}</p>
      </footer>
    </div>
  </div>
</template>
