<script setup lang="ts">
import { RouterView, useRoute } from 'vue-router'
import Layout from '@/components/Layout/Layout.vue'
import GlobalToast from '@/components/common/GlobalToast.vue'

const route = useRoute()
</script>

<template>
  <!-- 登录页：无 Layout -->
  <template v-if="route.path === '/login'">
    <RouterView />
  </template>
  <!-- 其他页面：带侧边栏和顶栏 -->
  <template v-else>
    <Layout>
      <RouterView />
    </Layout>
  </template>
  <GlobalToast />
</template>
