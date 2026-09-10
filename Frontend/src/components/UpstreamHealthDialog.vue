<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { siteApi } from '@/api'
const props = defineProps<{ clusterId: string }>()
const emit = defineEmits<{ close: []; saved: [] }>()
const form = ref<any>(null)
const error = ref('')
const busy = ref(false)
const numbers = [{ key: 'intervalSeconds', label: '间隔（秒）', max: 3600 }, { key: 'timeoutSeconds', label: '超时（秒）', max: 300 }, { key: 'passes', label: '连续成功次数', max: 100 }, { key: 'fails', label: '连续失败次数', max: 100 }]
onMounted(async () => {
  try {
    const result = await siteApi.health(props.clusterId)
    if (!result.success) throw new Error(result.message || '读取失败')
    form.value = result.data
  } catch (e: any) { error.value = e.message || '读取失败' }
})
async function save() {
  busy.value = true; error.value = ''
  try {
    const result = await siteApi.saveHealth(props.clusterId, form.value)
    if (!result.success) throw new Error(result.message || '保存失败')
    emit('saved')
  } catch (e: any) { error.value = e.message || '保存失败' }
  finally { busy.value = false }
}
</script>
<template>
  <div class="fixed inset-0 z-50 bg-black/60 flex items-center justify-center p-4">
    <form role="dialog" aria-modal="true" aria-label="上游健康检查" class="health-form bg-dark-card rounded-lg border border-dark-border w-full max-w-xl max-h-[90vh] overflow-auto p-5 space-y-4" @submit.prevent="save">
      <h2 class="font-semibold break-all">健康检查 · {{ clusterId }}（集群共用）</h2>
      <p v-if="error" role="alert" class="text-red-400 break-all">{{ error }}</p>
      <p v-if="!form && !error" role="status">加载中...</p>
      <template v-if="form">
        <label><input v-model="form.enabled" type="checkbox" /> 启用主动检查</label>
        <label>探测路径<input v-model="form.path" required maxlength="2048" placeholder="/health" /></label>
        <label>请求方法<select v-model="form.method"><option v-for="method in ['GET','HEAD','POST','PUT','PATCH','DELETE','OPTIONS']" :key="method">{{ method }}</option></select></label>
        <label>请求数据类型<select v-model="form.contentType"><option>application/json</option><option>application/x-www-form-urlencoded</option><option>text/plain</option></select></label>
        <label>请求数据<textarea v-model="form.body" rows="4" maxlength="65536" spellcheck="false" /></label>
        <label>健康状态码<input v-model="form.statusCodes" required maxlength="1024" pattern="[1-5xX][0-9xX]{2}(,[1-5xX][0-9xX]{2})*" placeholder="200,20x,30x" /></label>
        <div class="grid grid-cols-2 gap-3"><label v-for="field in numbers" :key="field.key">{{ field.label }}<input v-model.number="form[field.key]" type="number" required min="1" :max="field.max" /></label></div>
      </template>
      <footer class="flex justify-end gap-3"><button type="button" :disabled="busy" @click="emit('close')">取消</button><button :disabled="busy || !form" class="bg-primary-500 text-gray-900 rounded-lg px-4 py-2 disabled:opacity-50">{{ busy ? '保存中...' : '保存' }}</button></footer>
    </form>
  </div>
</template>
<style scoped>
.health-form label { display: block; font-size: 14px; min-width: 0; }
.health-form input:not([type=checkbox]), .health-form textarea, .health-form select { display: block; width: 100%; margin-top: 4px; padding: 8px; border: 1px solid #374151; border-radius: 8px; background: #141b26; color: #e5e7eb; }
</style>
