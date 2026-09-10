<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { managementTaskApi } from '@/api'
const data = ref<any>(null)
const loading = ref(false)
const error = ref('')
const bytes = (value: number | null | undefined) => value == null ? '未知' : `${(value / 1024 / 1024).toFixed(1)} MiB`
async function load() {
  loading.value = true
  error.value = ''
  try { const result = await managementTaskApi.health(); if (result.success) data.value = result.data; else error.value = '无法读取系统健康状态' }
  catch { error.value = '请求失败' }
  finally { loading.value = false }
}
onMounted(load)
</script>
<template>
  <div class="space-y-5">
    <header class="flex items-center justify-between"><h1 class="text-xl font-semibold">系统健康</h1>
      <button :disabled="loading" class="bg-dark-card rounded-lg px-4 py-2" @click="load">{{ loading ? '刷新中...' : '刷新' }}</button>
    </header>
    <p v-if="error" role="alert" class="text-red-400">{{ error }}</p>
    <template v-if="data">
      <dl class="grid grid-cols-2 lg:grid-cols-4 gap-5 border-y border-dark-border py-5">
        <div><dt class="text-gray-400">进程</dt><dd>{{ data.process.id }}</dd></div>
        <div><dt class="text-gray-400">内存</dt><dd>{{ bytes(data.process.memoryBytes) }}</dd></div>
        <div><dt class="text-gray-400">数据库</dt><dd>{{ data.database }}</dd></div>
        <div><dt class="text-gray-400">磁盘可用 / 总量</dt><dd>{{ bytes(data.disk.availableBytes) }} / {{ bytes(data.disk.capacityBytes) }}</dd></div>
        <div><dt class="text-gray-400">排队任务</dt><dd>{{ data.queue.queued }}</dd></div>
        <div><dt class="text-gray-400">运行任务</dt><dd>{{ data.queue.running }}</dd></div>
        <div><dt class="text-gray-400">失败 / 超时任务</dt><dd>{{ data.queue.failed }}</dd></div>
        <div><dt class="text-gray-400">启动时间</dt><dd>{{ new Date(data.process.startedAt).toLocaleString() }}</dd></div>
      </dl>
      <section class="space-y-2"><h2 class="font-medium">监听地址</h2><div v-for="address in data.listeners" :key="address" class="font-mono text-sm">{{ address }}</div><p v-if="!data.listeners.length" class="text-gray-500">运行时未提供监听地址</p></section>
      <section class="space-y-2"><h2 class="font-medium">上游</h2><div class="overflow-auto"><table class="w-full text-left text-sm"><thead class="text-gray-400"><tr><th class="p-2">集群</th><th>目标</th><th>主动检查</th><th>被动检查</th><th>连接</th></tr></thead><tbody>
        <tr v-for="upstream in data.upstreams" :key="`${upstream.cluster}/${upstream.destination}`" class="border-t border-dark-border"><td class="p-2">{{ upstream.cluster }}</td><td>{{ upstream.destination }}</td><td>{{ upstream.active }}</td><td>{{ upstream.passive }}</td><td>{{ upstream.connections }}</td></tr>
      </tbody></table></div></section>
      <section class="space-y-2"><h2 class="font-medium">证书</h2><div v-for="certificate in data.certificates" :key="certificate.domain" class="flex flex-wrap gap-4 border-b border-dark-border py-2 text-sm"><span>{{ certificate.domain }}</span><span>{{ new Date(certificate.expiresAt).toLocaleString() }}</span><span :class="certificate.daysRemaining < 15 ? 'text-red-400' : 'text-green-400'">剩余 {{ certificate.daysRemaining }} 天</span></div><p v-if="!data.certificates?.length" class="text-gray-500">暂无托管证书</p></section>
      <section class="space-y-2"><h2 class="font-medium">规则版本</h2><pre class="text-sm whitespace-pre-wrap break-all text-gray-400">{{ data.rules ? JSON.stringify(data.rules, null, 2) : '未知' }}</pre></section>
      <p class="text-xs text-gray-500">{{ new Date(data.capturedAt).toLocaleString() }}</p>
    </template>
  </div>
</template>
