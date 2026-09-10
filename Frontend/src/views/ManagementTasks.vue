<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { managementTaskApi, type ManagementJob } from '@/api'
const jobs = ref<ManagementJob[]>([])
const total = ref(0)
const page = ref(1)
const state = ref('')
const error = ref('')
const busy = ref(false)
const acting = ref(false)
const route = useRoute()
const selected = ref<string | null>(typeof route.query.task === 'string' ? route.query.task : null)
const url = ref('')
const kind = ref('rules')
const domain = ref('')
const email = ref('')
const packageName = ref('')
const version = ref('')
const sha256 = ref('')
const states = ['Queued', 'Running', 'Succeeded', 'Failed', 'Cancelled', 'TimedOut']
const labels = ['排队中', '运行中', '成功', '失败', '已取消', '超时']
let timer: ReturnType<typeof setTimeout> | undefined
let disposed = false
async function load() {
  if (busy.value) return
  busy.value = true
  try {
    const result = await managementTaskApi.list(page.value, state.value)
    if (!result.success) { error.value = '无法读取任务列表'; return }
    jobs.value = result.data
    total.value = result.total
    if (selected.value && !jobs.value.some(job => job.id === selected.value)) {
      try {
        const detail = await managementTaskApi.detail(selected.value)
        if (detail.success) jobs.value.unshift(detail.data)
      } catch (e: any) {
        if (e?.response?.status !== 404) throw e
        selected.value = null
        error.value = '该任务已过期或服务已重启，当前任务列表仍可正常使用'
      }
    }
  } catch { error.value = '任务列表请求失败' }
  finally {
    busy.value = false
    clearTimeout(timer)
    if (!disposed) timer = setTimeout(load, 5000)
  }
}
async function action(id: string, command: 'cancel' | 'retry') {
  acting.value = true
  error.value = ''
  try {
    const result = await managementTaskApi.action(id, command)
    if (!result.success) error.value = result.message || '当前任务状态不允许此操作'
    await load()
  } catch (e: any) { error.value = e.message || '操作失败' }
  finally { acting.value = false }
}
async function download() {
  acting.value = true
  error.value = ''
  try {
    const result = kind.value === 'certificate'
      ? await managementTaskApi.certificate(domain.value.trim(), email.value.trim())
      : kind.value === 'upgrade'
        ? await managementTaskApi.stageUpgrade(packageName.value.trim(), version.value.trim(), sha256.value.trim())
        : await managementTaskApi.downloadRules(url.value.trim())
    if (!result.success) error.value = result.message || '任务创建失败'
    else { url.value = ''; page.value = 1; state.value = ''; await load() }
  } catch (e: any) { error.value = e.message || '任务创建失败' }
  finally { acting.value = false }
}
onMounted(load)
onUnmounted(() => { disposed = true; clearTimeout(timer) })
</script>
<template>
  <div class="space-y-5">
    <header class="flex items-center justify-between gap-3"><h1 class="text-xl font-semibold">任务中心</h1>
      <button :disabled="busy" class="bg-dark-card border border-dark-border rounded-lg px-4 py-2" @click="load">{{ busy ? '刷新中...' : '刷新' }}</button>
    </header>
    <p v-if="error" role="alert" class="text-red-400">{{ error }}</p>
    <form class="flex flex-wrap gap-3 items-end border-b border-dark-border pb-5" @submit.prevent="download">
      <label class="text-sm text-gray-400">任务类型
        <select v-model="kind" class="block mt-1 bg-dark-card border border-dark-border rounded-lg p-2">
          <option value="rules">规则包更新</option><option value="certificate">证书签发</option><option value="upgrade">升级包准备</option>
        </select>
      </label>
      <label v-if="kind === 'rules'" class="flex-1 min-w-0 text-sm text-gray-400">规则包地址
        <input v-model="url" type="url" required placeholder="https://" class="block mt-1 w-full min-w-0 bg-dark-card border border-dark-border rounded-lg p-2" />
      </label>
      <template v-if="kind === 'certificate'">
        <label class="flex-1 min-w-0 text-sm text-gray-400">域名<input v-model="domain" required maxlength="253" class="block mt-1 w-full bg-dark-card border border-dark-border rounded-lg p-2" /></label>
        <label class="flex-1 min-w-0 text-sm text-gray-400">联系邮箱<input v-model="email" type="email" required maxlength="254" class="block mt-1 w-full bg-dark-card border border-dark-border rounded-lg p-2" /></label>
      </template>
      <template v-if="kind === 'upgrade'">
        <label class="flex-1 min-w-0 text-sm text-gray-400">包文件名<input v-model="packageName" required maxlength="255" class="block mt-1 w-full bg-dark-card border border-dark-border rounded-lg p-2" /></label>
        <label class="text-sm text-gray-400">版本<input v-model="version" required maxlength="100" class="block mt-1 w-full bg-dark-card border border-dark-border rounded-lg p-2" /></label>
        <label class="basis-full text-sm text-gray-400">SHA256<input v-model="sha256" required pattern="[a-fA-F0-9]{64}" maxlength="64" class="block mt-1 w-full font-mono bg-dark-card border border-dark-border rounded-lg p-2" /></label>
      </template>
      <button :disabled="acting" class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2 disabled:opacity-50">创建任务</button>
    </form>
    <select v-model="state" class="bg-dark-card border border-dark-border rounded-lg p-2" aria-label="任务状态" @change="page = 1; load()">
      <option value="">全部状态</option><option v-for="(s, i) in states" :key="s" :value="s">{{ labels[i] }}</option>
    </select>
    <div class="overflow-x-auto">
      <table class="w-full min-w-[680px] text-sm text-left"><thead class="text-gray-400 border-b border-dark-border"><tr>
        <th class="p-3">任务</th><th class="p-3">操作人</th><th class="p-3">状态</th><th class="p-3">创建时间</th><th class="p-3">操作</th>
      </tr></thead><tbody>
        <template v-for="job in jobs" :key="job.id">
          <tr class="border-b border-dark-border"><td class="p-3">{{ job.kind }}<div class="font-mono text-xs text-gray-500">{{ job.id }}</div></td>
            <td class="p-3">{{ job.operator }}</td><td class="p-3">{{ labels[job.state] }}{{ job.cancellationRequested && job.state === 1 ? '（正在取消）' : '' }}</td>
            <td class="p-3 whitespace-nowrap">{{ new Date(job.createdAt).toLocaleString() }}</td>
            <td class="p-3 whitespace-nowrap space-x-3">
              <button class="text-primary-400" @click="selected = selected === job.id ? null : job.id">日志</button>
              <button v-if="job.state < 2" :disabled="acting || job.cancellationRequested" class="text-gray-300 disabled:opacity-50" @click="action(job.id, 'cancel')">取消</button>
              <button v-if="job.canRetry" :disabled="acting" class="text-primary-400 disabled:opacity-50" @click="action(job.id, 'retry')">重试</button>
            </td>
          </tr>
          <tr v-if="selected === job.id"><td colspan="5" class="p-3 bg-dark-sidebar">
            <p v-if="job.error" class="text-red-400">{{ job.error }}</p>
            <pre class="max-h-72 overflow-auto whitespace-pre-wrap break-all text-xs">{{ job.logs.join('\n') || '暂无日志' }}</pre>
          </td></tr>
        </template>
        <tr v-if="!jobs.length"><td colspan="5" class="py-12 text-center text-gray-500">暂无任务</td></tr>
      </tbody></table>
    </div>
    <footer class="flex justify-end items-center gap-4 text-sm"><span>共 {{ total }} 项</span>
      <button :disabled="page <= 1 || busy" class="disabled:opacity-40" @click="page--; load()">上一页</button><span>{{ page }}</span>
      <button :disabled="page * 20 >= total || busy" class="disabled:opacity-40" @click="page++; load()">下一页</button>
    </footer>
  </div>
</template>
