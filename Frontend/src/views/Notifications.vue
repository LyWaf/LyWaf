<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { notificationApi } from '@/api'
import ListToolbar from '@/components/common/ListToolbar.vue'
const channels = ref<any[]>([])
const deliveries = ref<any[]>([])
const form = ref<any>(null)
const busy = ref(false)
const error = ref('')
const silence = ref('')
const channelPage = ref(1)
const channelSearch = ref('')
const channelTotal = ref(0)
const deliveryPage = ref(1)
const deliverySearch = ref('')
const deliveryTotal = ref(0)
const loading = ref(false)
let listRequest = 0
async function load() {
  const request = ++listRequest
  loading.value = true
  try {
    const [channelResult, deliveryResult] = await Promise.all([
      notificationApi.channels(channelPage.value, channelSearch.value),
      notificationApi.deliveries(deliveryPage.value, deliverySearch.value),
    ])
    if (request !== listRequest) return
    channels.value = channelResult.data; channelTotal.value = channelResult.total
    deliveries.value = deliveryResult.data; deliveryTotal.value = deliveryResult.total
  } catch { if (request === listRequest) error.value = '读取通知配置失败' }
  finally { if (request === listRequest) loading.value = false }
}
function edit(channel?: any) {
  form.value = channel ? { ...channel } : { id: '', name: '', kind: 'webhook', destination: '', enabled: true, minCount: 1, mergeSeconds: 60, smtpPort: 587 }
  silence.value = channel?.silencedUntil ? new Date(new Date(channel.silencedUntil).getTime() - new Date().getTimezoneOffset() * 60000).toISOString().slice(0, 16) : ''
}
async function save() {
  busy.value = true; error.value = ''
  try {
    const result = await notificationApi.save({ ...form.value, silencedUntil: silence.value ? new Date(silence.value).toISOString() : null })
    if (!result.success) error.value = result.message || '保存失败'
    else { form.value = null; await load() }
  } catch (e: any) { error.value = e.message || '保存失败' }
  finally { busy.value = false }
}
async function remove(id: string) {
  if (!window.confirm('删除此通知渠道？')) return
  busy.value = true
  try { const result = await notificationApi.remove(id); if (!result.success) error.value = result.message || '删除失败'; await load() }
  catch (e: any) { error.value = e.message || '删除失败' }
  finally { busy.value = false }
}
async function retry(id: string) {
  busy.value = true
  try { const result = await notificationApi.retry(id); if (!result.success) error.value = result.message || '不能重试'; await load() }
  catch (e: any) { error.value = e.message || '重试失败' }
  finally { busy.value = false }
}
onMounted(load)
</script>
<template>
  <div class="space-y-5">
    <header class="flex items-center justify-between"><h1 class="text-xl font-semibold">通知告警</h1><div class="flex gap-3"><button class="bg-dark-card rounded-lg px-4 py-2" @click="load">刷新</button><button class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2" @click="edit()">添加渠道</button></div></header>
    <p v-if="error" role="alert" class="text-red-400">{{ error }}</p>
    <ListToolbar v-model:page="channelPage" v-model:search="channelSearch" :total="channelTotal" :busy="loading" @refresh="load" />
    <div class="overflow-auto"><table class="w-full text-left text-sm"><thead class="text-gray-400"><tr><th class="p-3">渠道</th><th>类型</th><th>告警条件</th><th>静默截止</th><th>操作</th></tr></thead><tbody>
      <tr v-for="channel in channels" :key="channel.id" class="border-t border-dark-border"><td class="p-3">{{ channel.name }}<span class="text-gray-500"> {{ channel.enabled ? '' : '已禁用' }}</span></td><td>{{ channel.kind }}</td><td>累计 ≥ {{ channel.minCount }}，合并 {{ channel.mergeSeconds }} 秒</td><td>{{ channel.silencedUntil ? new Date(channel.silencedUntil).toLocaleString() : '无' }}</td><td class="space-x-3"><button class="text-primary-400" @click="edit(channel)">编辑</button><button :disabled="busy" class="text-red-400" @click="remove(channel.id)">删除</button></td></tr>
    </tbody></table></div>
    <h2 class="font-medium">发送记录</h2>
    <ListToolbar v-model:page="deliveryPage" v-model:search="deliverySearch" :total="deliveryTotal" :busy="loading" @refresh="load" />
    <div class="overflow-auto"><table class="w-full text-left text-sm"><thead class="text-gray-400"><tr><th class="p-3">渠道 / 规则</th><th>状态</th><th>尝试次数</th><th>错误</th><th>操作</th></tr></thead><tbody>
      <tr v-for="delivery in deliveries" :key="delivery.id" class="border-t border-dark-border"><td class="p-3">{{ delivery.channelId }}<div class="text-gray-400">{{ delivery.message.ruleId }}</div></td><td>{{ delivery.state }}</td><td>{{ delivery.attempts }}</td><td>{{ delivery.error || '-' }}</td><td><button v-if="delivery.state === 'failed'" :disabled="busy" class="text-primary-400" @click="retry(delivery.id)">重试</button></td></tr>
      <tr v-if="!deliveries.length"><td colspan="5" class="text-center text-gray-500 py-10">暂无发送记录</td></tr>
    </tbody></table></div>
    <div v-if="form" class="fixed inset-0 z-50 bg-black/60 flex items-center justify-center p-4">
      <form role="dialog" aria-modal="true" aria-label="通知渠道" class="w-full max-w-xl max-h-[90vh] overflow-auto bg-dark-card border border-dark-border rounded-lg p-5 space-y-3" @submit.prevent="save">
        <h2 class="font-semibold">通知渠道</h2><p v-if="error" class="text-red-400" role="alert">{{ error }}</p>
        <label class="block text-sm">渠道 ID<input v-model="form.id" required maxlength="100" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="block text-sm">名称<input v-model="form.name" required maxlength="100" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="block text-sm">类型<select v-model="form.kind" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2"><option value="webhook">Webhook</option><option value="smtp">SMTP</option><option value="plugin">插件</option></select></label>
        <label class="block text-sm">{{ form.kind === 'webhook' ? 'HTTPS 地址' : form.kind === 'smtp' ? '收件人' : '插件 ID' }}<input v-model="form.destination" required class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <template v-if="form.kind === 'smtp'">
          <label class="block text-sm">SMTP 主机<input v-model="form.smtpHost" required class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
          <label class="block text-sm">端口<input v-model.number="form.smtpPort" type="number" min="1" max="65535" required class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
          <label class="block text-sm">发件人<input v-model="form.sender" type="email" required class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
          <label class="block text-sm">用户名<input v-model="form.smtpUser" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
          <label class="block text-sm">新密码<input v-model="form.password" type="password" autocomplete="new-password" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        </template>
        <div class="grid grid-cols-2 gap-3"><label class="text-sm">次数阈值<input v-model.number="form.minCount" type="number" min="1" required class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label><label class="text-sm">合并秒数<input v-model.number="form.mergeSeconds" type="number" min="1" max="86400" required class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label></div>
        <label class="block text-sm">站点筛选<input v-model="form.site" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="block text-sm">攻击类型筛选<input v-model="form.attackType" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="block text-sm">静默截止<input v-model="silence" type="datetime-local" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="flex gap-2"><input v-model="form.enabled" type="checkbox" />启用</label>
        <div class="flex justify-end gap-3"><button type="button" :disabled="busy" @click="form = null">取消</button><button :disabled="busy" class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2">保存</button></div>
      </form>
    </div>
  </div>
</template>
