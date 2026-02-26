<script setup lang="ts">
import Sidebar from './Sidebar.vue'
import Topbar from './Topbar.vue'
</script>

<template>
  <div class="flex h-screen bg-dark-bg overflow-hidden">
    <!-- 侧边栏 -->
    <Sidebar />

    <!-- 主内容区 -->
    <div class="flex-1 ml-[220px] flex flex-col min-h-0">
      <!-- 顶部栏 -->
      <Topbar />

      <!-- 内容 -->
      <main class="flex-1 p-6 overflow-y-auto min-h-0">
        <slot />
      </main>
      
      <!-- 页脚 -->
      <footer class="text-center py-4 text-gray-500 text-sm border-t border-dark-border">
        <p>LyWaf 网关防火墙 · {{ new Date().getFullYear() }}</p>
      </footer>
    </div>
  </div>
</template>
