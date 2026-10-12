<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuth } from '@/composables/useAuth'
import { t, translateText } from '@/i18n'
import LanguageSelect from '@/components/common/LanguageSelect.vue'

const route = useRoute()
const router = useRouter()
const { username, logout } = useAuth()
defineProps<{ navigationOpen?: boolean }>()
defineEmits<{ toggleNavigation: [] }>()

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

const handleLogout = () => {
  logout()
  router.push('/login')
}
</script>

<template>
  <header class="h-14 shrink-0 bg-dark-bg-secondary border-b border-dark-border px-3 md:px-6 flex items-center justify-between gap-2 sticky top-0 z-40 min-w-0">
    <button class="md:hidden shrink-0 w-9 h-9 bg-dark-card border border-dark-border rounded" title="导航菜单" aria-label="导航菜单" :aria-expanded="navigationOpen" @click="$emit('toggleNavigation')">&#9776;</button>
    <!-- 面包屑 -->
    <nav class="flex items-center gap-2 text-sm min-w-0">
      <template v-for="(item, index) in breadcrumbs" :key="item.path">
        <span v-if="index > 0" class="text-gray-600">›</span>
        <span
          v-if="index === breadcrumbs.length - 1"
          class="text-gray-100 font-medium"
        >
          {{ translateText(item.name) }}
        </span>
        <a
          v-else
          @click="goTo(item.path)"
          class="text-gray-400 hover:text-primary-500 cursor-pointer transition-colors"
        >
          {{ translateText(item.name) }}
        </a>
      </template>
    </nav>

    <!-- 右侧状态 -->
    <div class="flex items-center gap-2 md:gap-4 shrink-0">
      <LanguageSelect />
      <span v-if="username" class="hidden md:inline text-sm text-gray-400">{{ username }}</span>
      <button
        @click="handleLogout"
        class="text-xs text-gray-500 hover:text-gray-300 transition-colors px-2 py-1 rounded hover:bg-white/5"
      >
        {{ t('common.logout') }}
      </button>
      <div class="hidden md:flex items-center gap-2 text-sm border-l border-dark-border pl-4">
        <span class="w-2 h-2 rounded-full bg-green-500 animate-pulse-dot"></span>
        <span class="text-gray-300">{{ t('common.running') }}</span>
      </div>
    </div>
  </header>

</template>
