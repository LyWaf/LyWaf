<script setup lang="ts">
import { RouterView } from 'vue-router'
import Layout from '@/components/Layout/Layout.vue'
import GlobalToast from '@/components/common/GlobalToast.vue'
</script>

<template>
  <Layout>
    <RouterView />
  </Layout>
  <GlobalToast />
</template>
