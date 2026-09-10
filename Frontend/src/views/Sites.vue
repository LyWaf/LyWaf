<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { siteApi, wafRuleApi } from '@/api'
const rows = ref<any[]>([])
const clusters = ref<any[]>([])
const rules = ref<any[]>([])
const listeners = ref<any[]>([])
const total = ref(0)
const page = ref(1)
const search = ref('')
const selected = ref<string[]>([])
const loading = ref(false)
const saving = ref(false)
const message = ref('')
const error = ref('')
const form = ref<any>(null)
const hosts = ref('')
const editing = ref(false)
const bound = ref(false)
const publication = ref<any>(null)
const version = ref('')
const editVersion = ref('')
const batchRules = ref<string[]>([])
async function load() {
  loading.value = true
  try {
    const result = await siteApi.list(page.value, search.value)
    if (!result.success) { error.value = result.message || '站点加载失败'; return }
    rows.value = result.data; clusters.value = result.clusters; listeners.value = result.listeners
    total.value = result.total; publication.value = result.latestPublication; version.value = result.version
    selected.value = selected.value.filter(id => rows.value.some(r => r.site.id === id))
  } catch { error.value = '站点加载失败' }
  finally { loading.value = false }
}
function edit(site?: any, clone = false) {
  editVersion.value = version.value
  editing.value = !!site && !clone
  form.value = site ? JSON.parse(JSON.stringify(site)) : { id: '', name: '', enabled: true, route: { routeId: '', clusterId: clusters.value[0]?.id || '', match: { path: '/{**path}', hosts: [] } }, ruleIds: null }
  if (clone) { form.value.id = `${site.id}-copy`; form.value.name = `${site.name} 副本` }
  hosts.value = form.value.route.match.hosts?.join('\n') || ''
  bound.value = form.value.ruleIds != null
  if (!form.value.ruleIds) form.value.ruleIds = []
}
async function save() {
  saving.value = true; error.value = ''; message.value = ''
  try {
    const site = JSON.parse(JSON.stringify(form.value))
    site.route.routeId = site.id
    site.route.match.hosts = hosts.value.split(/[\s,]+/).filter(Boolean)
    if (!bound.value) site.ruleIds = null
    const result = await siteApi.save(site, editVersion.value)
    if (!result.success) { error.value = result.message || '创建发布任务失败'; return }
    message.value = `发布任务已创建：${result.data.id}`; form.value = null; await load()
  } catch (e: any) { error.value = e.message || '保存失败' }
  finally { saving.value = false }
}
async function batch(action: string) {
  if (action === 'delete' && !window.confirm(`删除选中的 ${selected.value.length} 个站点？`)) return
  saving.value = true; error.value = ''; message.value = ''
  try {
    const result = await siteApi.batch(selected.value, action, action === 'bind-rules' ? batchRules.value : undefined)
    if (!result.success) error.value = result.message || '批量操作失败'
    else {
      const failed = result.data.filter((item: any) => !item.success)
      message.value = `已创建 ${result.data.length - failed.length} 个任务`
      error.value = failed.map((item: any) => `${item.id}: ${item.message}`).join('\n')
    }
    await load()
  } catch (e: any) { error.value = e.message || '批量操作失败' }
  finally { saving.value = false }
}
async function exportSites() {
  try {
    const result = await siteApi.export()
    if (!result.success) { error.value = '导出失败'; return }
    const url = URL.createObjectURL(new Blob([JSON.stringify(result.data, null, 2)], { type: 'application/json' }))
    const link = document.createElement('a'); link.href = url; link.download = 'lywaf-sites.json'; link.click(); URL.revokeObjectURL(url)
  } catch { error.value = '导出失败' }
}
async function importSites(event: Event) {
  const input = event.target as HTMLInputElement; const file = input.files?.[0]
  if (!file) return
  saving.value = true; error.value = ''
  try {
    if (file.size > 8 * 1024 * 1024) throw new Error('文件不能超过 8 MiB')
    const result = await siteApi.import(JSON.parse(await file.text()))
    if (!result.success) error.value = result.message || '导入失败'
    else { message.value = `导入任务已创建：${result.data.id}`; await load() }
  } catch (e: any) { error.value = e.message || '导入失败' }
  finally { saving.value = false; input.value = '' }
}
onMounted(async () => {
  await load()
  try {
    const collected: any[] = []
    for (let page = 1; ; page++) {
      const result: any = await wafRuleApi.list({ page, pageSize: 100, source: 'User' })
      if (!result.success) throw new Error('规则读取失败')
      if (!Array.isArray(result.data)) throw new Error('规则列表响应格式无效')
      collected.push(...result.data)
      if (collected.length >= result.total) break
      if (!result.data.length || page >= 100) throw new Error('规则数量过多，请先缩小规则配置范围')
    }
    rules.value = collected
  } catch (e: any) { error.value = e.message || '可绑定规则读取失败' }
})
</script>
<template>
  <div class="space-y-5">
    <header class="flex flex-wrap items-center justify-between gap-3"><h1 class="text-xl font-semibold">站点管理</h1><div class="flex flex-wrap gap-3">
      <button class="bg-dark-card rounded-lg px-4 py-2" :disabled="loading" @click="load">刷新</button>
      <button class="bg-dark-card rounded-lg px-4 py-2" @click="exportSites">导出</button>
      <label class="bg-dark-card rounded-lg px-4 py-2 cursor-pointer">导入<input type="file" accept="application/json,.json" class="hidden" :disabled="saving" @change="importSites" /></label>
      <button class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2" @click="edit()">添加站点</button>
    </div></header>
    <p v-if="error" role="alert" class="text-red-400 whitespace-pre-wrap">{{ error }}</p>
    <p v-if="message" class="text-green-400 break-all">{{ message }} <RouterLink class="underline" to="/management-tasks">查看任务</RouterLink></p>
    <div class="flex flex-wrap gap-3 items-center">
      <input v-model="search" placeholder="名称或域名" class="bg-dark-card border border-dark-border rounded-lg p-2" @keyup.enter="page = 1; load()" />
      <button :disabled="!selected.length || saving" class="px-3 py-2 bg-dark-card rounded-lg disabled:opacity-40" @click="batch('enable')">启用</button>
      <button :disabled="!selected.length || saving" class="px-3 py-2 bg-dark-card rounded-lg disabled:opacity-40" @click="batch('disable')">暂停</button>
      <button :disabled="!selected.length || saving" class="px-3 py-2 bg-red-500/10 text-red-400 rounded-lg disabled:opacity-40" @click="batch('delete')">删除</button>
    </div>
    <div v-if="selected.length" class="flex flex-wrap items-end gap-3">
      <label class="text-sm text-gray-400">批量绑定用户规则<select v-model="batchRules" multiple class="block mt-1 bg-dark-card border border-dark-border rounded-lg p-2 max-w-full"><option v-for="rule in rules" :key="rule.id" :value="rule.id">{{ rule.name }}</option></select></label>
      <button :disabled="saving" class="bg-dark-card rounded-lg px-3 py-2" @click="batch('bind-rules')">绑定规则</button>
    </div>
    <p v-if="publication" class="text-sm text-gray-400">最近发布：{{ ['排队中', '运行中', '成功', '失败', '取消', '超时'][publication.state] }} <RouterLink to="/management-tasks" class="text-primary-400">查看日志</RouterLink></p>
    <div class="overflow-auto"><table class="w-full text-left text-sm"><thead class="text-gray-400"><tr><th class="p-3">选择</th><th>站点</th><th>上游集群</th><th>今日请求 / 拦截</th><th>状态</th><th>证书</th><th>操作</th></tr></thead><tbody>
      <tr v-for="row in rows" :key="row.site.id" class="border-t border-dark-border">
        <td class="p-3"><input v-model="selected" type="checkbox" :value="row.site.id" :aria-label="`选择 ${row.site.name}`" /></td>
        <td class="py-3"><div>{{ row.site.name }}</div><div class="text-xs text-gray-400 break-all">{{ row.site.route.match.hosts?.join(', ') || '*' }}</div><div class="text-xs text-gray-500">{{ row.site.route.match.path }}</div>
          <div v-for="listener in row.listeners" :key="`${listener.host}:${listener.port}`" class="text-xs text-gray-400">{{ listener.https ? 'HTTPS' : 'HTTP' }} :{{ listener.port }}</div>
        </td>
        <td class="py-3"><div>{{ row.site.route.clusterId }}</div><div v-for="upstream in row.upstreams" :key="upstream.id" class="text-xs text-gray-400 break-all">{{ upstream.address }}</div>
          <div v-for="health in row.upstreamHealth" :key="health.id" class="text-xs" :class="health.active === 'Unhealthy' || health.passive === 'Unhealthy' ? 'text-red-400' : 'text-gray-400'">{{ health.id }}: {{ health.active }} / {{ health.passive }}</div>
        </td><td><span>{{ row.traffic?.requests ?? 0 }} / {{ row.traffic?.intercepts ?? 0 }}</span><div v-if="row.traffic?.requests" class="text-xs text-gray-500">非 5xx 比例 {{ ((1 - row.traffic.errors / row.traffic.requests) * 100).toFixed(1) }}%<br />自 {{ new Date(row.traffic.observedSince).toLocaleString() }}</div></td>
        <td :class="row.site.enabled ? 'text-green-400' : 'text-gray-500'">{{ row.site.enabled ? '启用' : '暂停' }}<div v-if="row.latestPublication" class="text-xs text-gray-400">{{ ['排队中', '发布中', '已生效', '发布失败', '已取消', '发布超时'][row.latestPublication.state] }}</div></td>
        <td><div v-for="cert in row.certificates" :key="cert.domain" :class="cert.daysRemaining < 15 ? 'text-red-400' : 'text-green-400'">{{ cert.daysRemaining }} 天</div><span v-if="!row.certificates.length" class="text-gray-500">未托管</span></td>
        <td class="whitespace-nowrap space-x-3"><button class="text-primary-400" @click="edit(row.site)">编辑</button><button class="text-primary-400" @click="edit(row.site, true)">复制</button></td>
      </tr><tr v-if="!rows.length"><td colspan="7" class="text-center text-gray-500 py-12">暂无站点</td></tr>
    </tbody></table></div>
    <footer class="flex justify-end gap-4 text-sm"><span>共 {{ total }} 个</span><button :disabled="page <= 1 || loading" @click="page--; load()">上一页</button><span>{{ page }}</span><button :disabled="page * 20 >= total || loading" @click="page++; load()">下一页</button></footer>
    <p class="text-xs text-gray-500 break-all">监听：{{ listeners.map(l => `${l.host || '*'}:${l.port}`).join(', ') }}</p>
    <div v-if="form" class="fixed inset-0 z-50 bg-black/60 flex items-center justify-center p-4">
      <form role="dialog" aria-modal="true" aria-label="编辑站点" class="bg-dark-card rounded-lg border border-dark-border w-full max-w-xl max-h-[90vh] overflow-auto p-5 space-y-4" @submit.prevent="save">
        <h2 class="font-semibold">{{ editing ? '编辑站点' : '添加站点' }}</h2>
        <label class="block text-sm">站点 ID<input v-model="form.id" required :disabled="editing" maxlength="100" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="block text-sm">名称<input v-model="form.name" required maxlength="200" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="block text-sm">域名<textarea v-model="hosts" rows="3" class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="block text-sm">路径<input v-model="form.route.match.path" required class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" /></label>
        <label class="block text-sm">上游集群<select v-model="form.route.clusterId" required class="mt-1 w-full bg-dark-sidebar border border-dark-border rounded-lg p-2"><option v-for="cluster in clusters" :key="cluster.id" :value="cluster.id">{{ cluster.id }}</option></select></label>
        <label class="flex items-center gap-2 text-sm"><input v-model="form.enabled" type="checkbox" />启用站点</label>
        <label class="flex items-center gap-2 text-sm"><input v-model="bound" type="checkbox" />指定用户规则</label>
        <div v-if="bound" class="max-h-40 overflow-auto space-y-2"><label v-for="rule in rules" :key="rule.id" class="flex items-center gap-2 text-sm"><input v-model="form.ruleIds" type="checkbox" :value="rule.id" />{{ rule.name }}</label></div>
        <div class="flex justify-end gap-3"><button type="button" :disabled="saving" @click="form = null">取消</button><button :disabled="saving" class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2">{{ saving ? '提交中...' : '发布' }}</button></div>
      </form>
    </div>
  </div>
</template>
