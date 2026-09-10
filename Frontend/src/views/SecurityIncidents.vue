<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { securityIncidentApi } from '@/api'
import { useAuth } from '@/composables/useAuth'
import DateTimePicker from '@/components/common/DateTimePicker.vue'
const { can } = useAuth()
const filters = reactive({ site: '', rule: '', ip: '', attackType: '', state: '', from: '', to: '' })
const page = ref(1)
const total = ref(0)
const dropped = ref(0)
const events = ref<any[]>([])
const detail = ref<any>(null)
const loading = ref(false)
const busy = ref(false)
const error = ref('')
const state = ref(0)
const note = ref('')
const requests = ref<any[] | null>(null)
const requestsTotal = ref(0)
const requestsDropped = ref(0)
const nextCursor = ref<string | null>(null)
const states = ['待处理', '已确认误报', '已修正规则', '已忽略']
async function load() {
  loading.value = true; error.value = ''
  try {
    const params: Record<string, unknown> = { page: page.value, pageSize: 20 }
    for (const [key, value] of Object.entries(filters)) if (value !== '') params[key] = key === 'from' || key === 'to' ? new Date(value.replace(' ', 'T')).toISOString() : value
    const result = await securityIncidentApi.list(params)
    if (!result.success) error.value = result.message || '加载失败'
    else { events.value = result.data; total.value = result.total; dropped.value = result.dropped }
  } catch { error.value = '事件加载失败' }
  finally { loading.value = false }
}
async function open(id: string) {
  busy.value = true
  try {
    const result = await securityIncidentApi.detail(id)
    if (!result.success) { error.value = result.message || '事件不存在'; return }
    detail.value = result; state.value = result.data.state; note.value = ''
    requests.value = null; nextCursor.value = null
  } catch { error.value = '详情加载失败' }
  finally { busy.value = false }
}
async function disposition() {
  busy.value = true; error.value = ''
  try {
    const result = await securityIncidentApi.disposition(detail.value.data.id, state.value, note.value, detail.value.data.stateVersion)
    if (!result.success) error.value = result.message || '处置失败'
    else { await open(detail.value.data.id); await load() }
  } catch (e: any) { error.value = e.message || '处置失败' }
  finally { busy.value = false }
}
async function loadRequests(cursor?: string | null) {
  if (!detail.value || busy.value) return
  const id = detail.value.data.id
  busy.value = true
  try {
    const result = await securityIncidentApi.requests(id, cursor)
    if (!result.success) throw new Error(result.message || '请求记录加载失败')
    if (detail.value?.data.id !== id) return
    requests.value = result.data; requestsTotal.value = result.total; requestsDropped.value = result.dropped; nextCursor.value = result.nextCursor
  } catch (e: any) { error.value = e.message || '请求记录加载失败' }
  finally { busy.value = false }
}
async function act(action: string) {
  if (!window.confirm(`${action === 'allow-ip' ? '将 IP 加入全局白名单' : '封禁 IP'}：${detail.value.data.ip}？`)) return
  busy.value = true
  try { const result = await securityIncidentApi.action(detail.value.data.id, action); if (!result.success) error.value = result.message || '操作失败' }
  catch (e: any) { error.value = e.message || '操作失败' }
  finally { busy.value = false }
}
onMounted(load)
</script>
<template>
  <div class="space-y-5">
    <header class="flex justify-between items-center"><h1 class="text-xl font-semibold">事件处置</h1><button :disabled="loading" class="px-4 py-2 bg-dark-card rounded-lg" @click="load">{{ loading ? '刷新中...' : '刷新' }}</button></header>
    <p v-if="error" class="text-red-400" role="alert">{{ error }}</p>
    <p v-if="dropped" class="text-amber-400">事件容量已满，未收录 {{ dropped }} 次新指纹事件。</p>
    <form class="grid grid-cols-2 lg:grid-cols-4 gap-3" @submit.prevent="page = 1; load()">
      <input v-model="filters.site" aria-label="站点" placeholder="站点" class="min-w-0 bg-dark-card border border-dark-border rounded-lg p-2" />
      <input v-model="filters.rule" aria-label="规则" placeholder="规则" class="min-w-0 bg-dark-card border border-dark-border rounded-lg p-2" />
      <input v-model="filters.ip" aria-label="IP" placeholder="IP" class="min-w-0 bg-dark-card border border-dark-border rounded-lg p-2" />
      <input v-model="filters.attackType" aria-label="攻击类型" placeholder="攻击类型" class="min-w-0 bg-dark-card border border-dark-border rounded-lg p-2" />
      <select v-model="filters.state" aria-label="处置状态" class="min-w-0 bg-dark-card border border-dark-border rounded-lg p-2"><option value="">全部状态</option><option v-for="(label, i) in states" :key="i" :value="String(i)">{{ label }}</option></select>
      <DateTimePicker v-model="filters.from" placeholder="开始时间" class="min-w-0" />
      <DateTimePicker v-model="filters.to" placeholder="结束时间" class="min-w-0" />
      <button class="bg-primary-500 text-gray-900 rounded-lg p-2">查询</button>
    </form>
    <div class="overflow-auto"><table class="w-full text-sm text-left"><thead class="text-gray-400"><tr><th class="p-3">站点 / IP</th><th>规则 / 类型</th><th>次数</th><th>首次 / 最后发生</th><th>状态</th><th>操作</th></tr></thead><tbody>
      <tr v-for="event in events" :key="event.id" class="border-t border-dark-border"><td class="p-3">{{ event.site }}<div class="text-gray-400">{{ event.ip }}</div></td><td class="break-all">{{ event.ruleId }}<div class="text-gray-400">{{ event.attackType }}</div></td><td>{{ event.count }}</td><td class="whitespace-nowrap">{{ new Date(event.firstSeen).toLocaleString() }}<div>{{ new Date(event.lastSeen).toLocaleString() }}</div></td><td>{{ states[event.state] }}</td><td><button :disabled="busy" class="text-primary-400" @click="open(event.id)">详情 / 处置</button></td></tr>
      <tr v-if="!events.length"><td colspan="6" class="text-center text-gray-500 py-12">暂无事件</td></tr>
    </tbody></table></div>
    <footer class="flex justify-end gap-4 text-sm"><span>共 {{ total }} 项</span><button :disabled="page <= 1 || loading" @click="page--; load()">上一页</button><span>{{ page }}</span><button :disabled="page * 20 >= total || loading" @click="page++; load()">下一页</button></footer>
    <div v-if="detail" class="fixed inset-0 z-50 bg-black/60 flex items-center justify-center p-4">
      <section role="dialog" aria-modal="true" aria-label="安全事件详情" class="bg-dark-card border border-dark-border rounded-lg w-full max-w-4xl max-h-[90vh] overflow-auto p-5 space-y-4">
        <header class="flex justify-between gap-3"><h2 class="font-semibold break-all">{{ detail.data.ruleId }}</h2><button aria-label="关闭" @click="detail = null">关闭</button></header>
        <p v-if="error" role="alert" class="text-red-400">{{ error }}</p>
        <div class="flex flex-wrap gap-3 text-sm"><span>{{ detail.data.host }}</span><span>{{ detail.data.ip }}</span><span>{{ states[detail.data.state] }}</span>
          <button v-if="can('system.manage')" :disabled="busy" class="text-green-400" @click="act('allow-ip')">加入白名单</button><button v-if="can('system.manage')" :disabled="busy" class="text-red-400" @click="act('block-ip')">封禁 IP</button>
          <RouterLink v-if="detail.explanation && can('write')" class="text-primary-400" :to="{ path: '/waf-rules', query: { edit: detail.data.ruleId } }">查看 / 调整规则</RouterLink>
        </div>
        <h3 class="font-medium">最近请求样本</h3>
        <div v-for="sample in detail.data.samples" :key="sample.requestId" class="border-t border-dark-border py-3 space-y-1 text-sm break-all">
          <div class="font-mono">{{ sample.method }} {{ sample.path }}</div><div class="text-gray-400">{{ new Date(sample.time).toLocaleString() }} · {{ sample.requestId }}</div>
          <div>命中字段：{{ sample.matchedFields.join(', ') || '未记录' }}</div><div>规范化：{{ sample.normalizationSteps.join(' → ') || '无转换记录' }}</div>
          <div v-for="location in sample.matchLocations || []" :key="`${location.group}:${location.condition}`">
            条件组 {{ location.group }} / 条件 {{ location.condition }}：{{ location.field }}<span v-if="location.fieldName">[{{ location.fieldName }}]</span> · {{ location.operator }}
          </div>
          <div>参数名：{{ sample.queryKeys.join(', ') || '无' }}</div><div v-if="sample.bodyTruncated" class="text-amber-400">检测请求体已截断</div>
        </div>
        <div class="border-t border-dark-border pt-3 space-y-3">
          <div class="flex flex-wrap items-center gap-3"><h3 class="font-medium">单次请求记录</h3><button :disabled="busy" class="text-primary-400 text-sm" @click="loadRequests()">最新记录</button><button v-if="nextCursor" :disabled="busy" class="text-primary-400 text-sm" @click="loadRequests(nextCursor)">更早记录</button><span v-if="requests" class="text-sm text-gray-400">保留 {{ requestsTotal }} 条</span></div>
          <p v-if="requestsDropped" class="text-sm text-amber-400">记录队列累计丢弃 {{ requestsDropped }} 条。</p>
          <p v-if="requests && !requests.length" class="text-sm text-gray-500">暂无保留记录</p>
          <div v-for="record in requests" :key="record.id" class="border-t border-dark-border py-2 text-sm break-all">
            <div class="font-mono">{{ record.request.method }} {{ record.request.path }}</div>
            <div class="text-gray-400">{{ new Date(record.request.time).toLocaleString() }} · {{ record.request.requestId }}</div>
            <div>{{ record.ip }} · {{ record.ruleId }}</div>
            <div>命中字段：{{ record.request.matchedFields.join(', ') || '未记录' }}</div>
            <div v-for="location in record.request.matchLocations || []" :key="`${location.group}:${location.condition}`">
              条件组 {{ location.group }} / 条件 {{ location.condition }}：{{ location.field }}<span v-if="location.fieldName">[{{ location.fieldName }}]</span> · {{ location.operator }}
            </div>
            <div>规范化：{{ record.request.normalizationSteps.join(' → ') || '无转换记录' }}</div>
            <div>参数名：{{ record.request.queryKeys.join(', ') || '无' }}</div>
          </div>
        </div>
        <details v-if="detail.explanation"><summary class="cursor-pointer text-primary-400">规则解释</summary><pre class="text-xs whitespace-pre-wrap break-all mt-2">{{ JSON.stringify(detail.explanation, null, 2) }}</pre></details>
        <details v-if="detail.related.length"><summary class="cursor-pointer text-primary-400">关联事件 {{ detail.related.length }}</summary><div v-for="event in detail.related" :key="event.id" class="py-2"><button class="text-primary-400 text-sm" @click="open(event.id)">{{ event.ip }} · {{ event.ruleId }} · {{ event.count }}</button></div></details>
        <form v-if="can('write')" class="border-t border-dark-border pt-4 space-y-3" @submit.prevent="disposition">
          <select v-model="state" aria-label="处置状态" class="bg-dark-sidebar border border-dark-border rounded-lg p-2"><option v-for="(label, i) in states" :key="i" :value="i">{{ label }}</option></select>
          <textarea v-model="note" required maxlength="200" placeholder="处置备注" class="block w-full bg-dark-sidebar border border-dark-border rounded-lg p-2" />
          <button :disabled="busy" class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2">{{ busy ? '处理中...' : '保存处置' }}</button>
        </form>
        <div v-for="(entry, index) in detail.data.history" :key="index" class="border-t border-dark-border pt-2 text-sm"><span class="text-gray-400">{{ new Date(entry.time).toLocaleString() }} · {{ entry.operator }} · {{ states[entry.state] }}</span><p class="break-all">{{ entry.note }}</p></div>
      </section>
    </div>
  </div>
</template>
