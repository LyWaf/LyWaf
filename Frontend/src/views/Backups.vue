<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { backupApi } from '@/api'
const rows = ref<Array<{ id: string; size: number; createdAt: string; scope: string }>>([])
const scope = ref('configuration')
const pending = ref(false)
const instancePending = ref(false)
const page = ref(1)
const total = ref(0)
const search = ref('')
const busy = ref(false)
const error = ref('')
const task = ref('')
async function load() {
  const result = await backupApi.list(page.value, search.value)
  if (!result.success) throw new Error(result.message || '备份列表读取失败')
  rows.value = result.data
  pending.value = result.statisticsRestorePending
  instancePending.value = result.instanceStatePending
  total.value = result.total
}
async function perform(action: () => Promise<any>) {
  if (busy.value) return
  busy.value = true; error.value = ''; task.value = ''
  try {
    const result = await action()
    if (result?.success === false) throw new Error(result.message || result.error?.message || '操作失败')
    if (result?.data?.id) task.value = result.data.id
    await load()
  } catch (e: any) { error.value = e.message || '请求失败' }
  finally { busy.value = false }
}
function restore(id: string, action: 'preflight' | 'restore') {
  if (action === 'restore' && !confirm(id.startsWith('i-') ? '实例状态将在下次重启时恢复，覆盖账号、证书和插件数据，并删除快照中不存在的状态文件。确定登记？' : id.startsWith('s-') ? '统计数据恢复会安排在下次重启时执行，并覆盖届时的统计数据。确定登记？' : '恢复将覆盖当前配置并重载。确定继续？')) return
  perform(() => backupApi.restore(id, action))
}
async function download(id: string) {
  const blob = await backupApi.download(id)
  if (!(blob instanceof Blob) || blob.type.includes('json')) throw new Error('备份下载失败')
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url; link.download = `${id}.lybackup`; link.click()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}
function upload(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]; input.value = ''
  if (!file) return
  if (file.size > 32 * 1024 * 1024) { error.value = '配置备份不能超过 32 MiB'; return }
  perform(() => backupApi.upload(file, scope.value))
}
function remove(id: string) {
  if (confirm('确定删除这份备份？')) perform(() => backupApi.remove(id))
}
function create() {
  if (scope.value === 'instance' && !confirm('实例状态备份需下次重启后、服务启动前执行。现在仅登记任务，是否继续？')) return
  perform(() => backupApi.create(scope.value))
}
onMounted(() => perform(async () => {}))
</script>
<template>
  <div class="space-y-5">
    <header class="flex flex-wrap items-center justify-between gap-3"><h1 class="text-xl font-semibold">备份恢复</h1>
      <button :disabled="busy" class="bg-dark-card border border-dark-border rounded-lg px-4 py-2" @click="perform(async () => {})">刷新</button>
    </header>
    <p v-if="error" role="alert" class="text-red-400 break-words">{{ error }}</p>
    <div v-if="pending" role="status" class="flex flex-wrap items-center gap-4 text-amber-400 border-b border-dark-border pb-3">
      <span>统计数据恢复待重启执行</span><button :disabled="busy" class="text-red-400" @click="perform(backupApi.cancelPending)">取消恢复</button>
    </div>
    <RouterLink v-if="task" to="/management-tasks" class="block text-primary-400 break-all">任务已创建：{{ task }}</RouterLink>
    <div v-if="instancePending" role="status" class="flex flex-wrap items-center gap-4 text-amber-400 border-b border-dark-border pb-3">
      <span>实例状态任务待重启执行</span><button :disabled="busy" class="text-red-400" @click="perform(backupApi.cancelInstancePending)">取消任务</button>
    </div>
    <form class="flex flex-wrap gap-3 items-end border-b border-dark-border pb-5" @submit.prevent="create">
      <label class="text-sm text-gray-400">备份范围<select v-model="scope" class="block mt-1 bg-dark-card border border-dark-border rounded-lg p-2"><option value="configuration">配置</option><option value="statistics">统计数据库</option><option value="instance">实例状态（需重启）</option></select></label>
      <button :disabled="busy" class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2 disabled:opacity-50">创建备份</button>
      <label class="block text-sm text-gray-400">上传备份<input type="file" accept=".lybackup" :disabled="busy" class="block mt-1 max-w-full" @change="upload" /></label>
    </form>
    <form class="flex flex-wrap gap-3" @submit.prevent="page = 1; perform(async () => {})">
      <input v-model="search" aria-label="搜索备份" placeholder="搜索备份 ID 或范围" maxlength="200" class="min-w-0 bg-dark-card border border-dark-border rounded-lg p-2" />
      <button :disabled="busy" class="border border-dark-border rounded-lg px-4 py-2">搜索</button>
    </form>
    <div class="overflow-x-auto"><table class="w-full text-sm text-left">
      <thead class="text-gray-400 border-b border-dark-border"><tr><th class="p-3">备份 ID</th><th class="p-3">大小</th><th class="p-3">创建时间</th><th class="p-3">操作</th></tr></thead>
      <tbody><tr v-for="row in rows" :key="row.id" class="border-b border-dark-border">
        <td class="p-3 font-mono">{{ row.id }}<div class="text-xs text-gray-500 mt-1">{{ row.scope === 'statistics' ? '统计数据库' : row.scope === 'instance' ? '实例状态' : '配置' }}</div></td><td class="p-3 whitespace-nowrap">{{ (row.size / 1024).toFixed(1) }} KiB</td><td class="p-3 whitespace-nowrap">{{ new Date(row.createdAt).toLocaleString() }}</td>
        <td class="p-3"><div class="flex gap-3 whitespace-nowrap">
          <button :disabled="busy" class="text-primary-400" @click="perform(() => download(row.id))">下载</button>
          <button :disabled="busy" class="text-primary-400" @click="restore(row.id, 'preflight')">预检</button>
          <button :disabled="busy" class="text-primary-400" @click="restore(row.id, 'restore')">恢复</button>
          <button :disabled="busy" class="text-red-400" @click="remove(row.id)">删除</button>
        </div></td>
      </tr><tr v-if="!rows.length"><td colspan="4" class="py-12 text-center text-gray-500">暂无配置备份</td></tr></tbody>
    </table></div>
    <div class="flex gap-4 items-center text-sm">
      <button :disabled="busy || page <= 1" class="disabled:opacity-40" @click="page--; perform(async () => {})">上一页</button>
      <span>{{ page }} / {{ Math.max(1, Math.ceil(total / 20)) }} · 共 {{ total }} 条</span>
      <button :disabled="busy || page * 20 >= total" class="disabled:opacity-40" @click="page++; perform(async () => {})">下一页</button>
    </div>
  </div>
</template>
