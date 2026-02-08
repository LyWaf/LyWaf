<script setup lang="ts">
import Sidebar from './Sidebar.vue'
import Topbar from './Topbar.vue'
</script>

<template>
  <div class="flex min-h-screen bg-dark-bg">
    <!-- 侧边栏 -->
    <Sidebar />
    
    <!-- 主内容区 -->
    <div class="flex-1 ml-[220px] flex flex-col min-h-screen">
      <!-- 顶部栏 -->
      <Topbar />
      
      <!-- 内容 -->
      <main class="flex-1 p-6 overflow-auto">
        <slot />
      </main>
      
      <!-- 页脚 -->
      <footer class="text-center py-4 text-gray-500 text-sm border-t border-dark-border">
        <p>LyWaf 网关防火墙 · {{ new Date().getFullYear() }}</p>
      </footer>
    </div>
  </div>
</template>
