<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { simpleResApi } from '@/api'
import { useToast } from '@/composables/useToast'
import type { SimpleResItem } from '@/types'

const { showSuccess, showError } = useToast()

// 数据状态
const loading = ref(false)
const items = ref<SimpleResItem[]>([])

// 对话框状态
const showDialog = ref(false)
const dialogMode = ref<'add' | 'edit'>('add')
const saving = ref(false)
const editingItem = ref<SimpleResItem | null>(null)
const form = ref({
  itemId: '',
  body: '',
  contentType: 'text/plain',
  statusCode: 200,
  charset: 'utf-8',
  showReq: false,
})

// 加载数据
const loadData = async () => {
  loading.value = true
  try {
    const res = await simpleResApi.getItems()
    if (res.success) {
      items.value = res.items
    }
  } catch (error) {
    showError('加载简单响应配置失败')
  } finally {
    loading.value = false
  }
}

// 打开新增对话框
const openAddDialog = () => {
  dialogMode.value = 'add'
  editingItem.value = null
  form.value = {
    itemId: '',
    body: 'OK',
    contentType: 'text/plain',
    statusCode: 200,
    charset: 'utf-8',
    showReq: false,
  }
  showDialog.value = true
}

// 打开编辑对话框
const openEditDialog = (item: SimpleResItem) => {
  dialogMode.value = 'edit'
  editingItem.value = item
  form.value = {
    itemId: item.itemId,
    body: item.body,
    contentType: item.contentType,
    statusCode: item.statusCode,
    charset: item.charset,
    showReq: item.showReq,
  }
  showDialog.value = true
}

// 保存
const handleSave = async () => {
  saving.value = true
  try {
    if (dialogMode.value === 'add') {
      if (!form.value.itemId.trim()) {
        showError('ID 不能为空')
        saving.value = false
        return
      }
      const res = await simpleResApi.addItem({
        itemId: form.value.itemId.trim(),
        body: form.value.body,
        contentType: form.value.contentType,
        statusCode: form.value.statusCode,
        charset: form.value.charset,
        showReq: form.value.showReq,
      })
      if (res.success) {
        showSuccess('简单响应已新增')
        showDialog.value = false
        await loadData()
      } else {
        showError(res.message || '新增失败')
      }
    } else {
      if (!editingItem.value) return
      const res = await simpleResApi.updateItem({
        itemId: editingItem.value.itemId,
        body: form.value.body,
        contentType: form.value.contentType,
        statusCode: form.value.statusCode,
        charset: form.value.charset,
        showReq: form.value.showReq,
      })
      if (res.success) {
        showSuccess('简单响应已更新')
        showDialog.value = false
        await loadData()
      } else {
        showError(res.message || '更新失败')
      }
    }
  } catch (error) {
    showError(dialogMode.value === 'add' ? '新增失败' : '更新失败')
  } finally {
    saving.value = false
  }
}

// 删除单个
const removeItem = async (item: SimpleResItem) => {
  if (!confirm(`确定要删除简单响应 "${item.itemId}" 吗？`)) return
  try {
    const res = await simpleResApi.removeItem(item.itemId)
    if (res.success) {
      showSuccess('简单响应已删除')
      await loadData()
    } else {
      showError(res.message || '删除失败')
    }
  } catch (error) {
    showError('删除失败')
  }
}

// 删除所有补丁
const removePatch = async () => {
  if (!confirm('确定要删除所有简单响应的补丁数据吗？这将恢复为原始配置。')) return
  try {
    const res = await simpleResApi.removePatch()
    if (res.success) {
      showSuccess('简单响应补丁已删除，已恢复原始配置')
      await loadData()
    } else {
      showError(res.message || '删除补丁失败')
    }
  } catch (error) {
    showError('删除补丁失败')
  }
}

onMounted(loadData)
</script>

<template>
  <div class="space-y-6">
    <div class="card">
      <div class="flex items-center justify-between mb-4">
        <h2 class="text-lg font-medium text-gray-100">简单响应配置</h2>
        <div class="flex items-center gap-2">
          <button @click="removePatch" class="btn btn-sm btn-warning flex items-center gap-1">
            删除补丁
          </button>
          <button @click="openAddDialog" class="btn btn-sm btn-primary flex items-center gap-1">
            <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
            </svg>
            新增
          </button>
        </div>
      </div>

      <!-- 表格 -->
      <div class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead>
            <tr class="border-b border-dark-border text-gray-400">
              <th class="py-3 px-3 text-left">ID</th>
              <th class="py-3 px-3 text-left">来源</th>
              <th class="py-3 px-3 text-left">Body</th>
              <th class="py-3 px-3 text-left">ContentType</th>
              <th class="py-3 px-3 text-left">StatusCode</th>
              <th class="py-3 px-3 text-left">Charset</th>
              <th class="py-3 px-3 text-left">操作</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="item in items"
              :key="item.itemId"
              class="border-b border-dark-border/50 hover:bg-white/5 transition-colors"
            >
              <td class="py-3 px-3 text-gray-200 font-mono">{{ item.itemId }}</td>
              <td class="py-3 px-3">
                <span
                  v-if="item.source === 'patch'"
                  class="inline-block px-1.5 py-0.5 text-xs bg-yellow-500/20 text-yellow-400 rounded"
                >补丁</span>
                <span
                  v-else
                  class="inline-block px-1.5 py-0.5 text-xs bg-gray-500/20 text-gray-400 rounded"
                >默认</span>
              </td>
              <td class="py-3 px-3 text-gray-300 font-mono max-w-[200px] truncate" :title="item.body">
                {{ item.body || '-' }}
              </td>
              <td class="py-3 px-3 text-primary-400 font-mono">{{ item.contentType }}</td>
              <td class="py-3 px-3">
                <span
                  :class="item.statusCode >= 200 && item.statusCode < 300
                    ? 'text-green-400'
                    : item.statusCode >= 400
                      ? 'text-red-400'
                      : 'text-yellow-400'"
                >{{ item.statusCode }}</span>
              </td>
              <td class="py-3 px-3 text-gray-300">{{ item.charset }}</td>
              <td class="py-3 px-3">
                <div class="flex items-center gap-2">
                  <button
                    @click="openEditDialog(item)"
                    class="px-3 py-1 text-xs border border-primary-500 text-primary-400 rounded hover:bg-primary-500/10 transition-colors"
                  >
                    编辑
                  </button>
                  <button
                    @click="removeItem(item)"
                    class="px-3 py-1 text-xs border border-red-500 text-red-400 rounded hover:bg-red-500/10 transition-colors"
                  >
                    删除
                  </button>
                </div>
              </td>
            </tr>
          </tbody>
        </table>

        <div v-if="!loading && items.length === 0" class="text-center py-12 text-gray-500">
          暂无简单响应配置
        </div>
      </div>
    </div>

    <div v-if="loading" class="text-center py-12 text-gray-500">
      加载中...
    </div>

    <!-- 新增/编辑对话框 -->
    <Teleport to="body">
      <div v-if="showDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="showDialog = false"></div>

        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-full max-w-lg mx-4 p-6">
          <h3 class="text-lg font-medium text-gray-100 mb-4">
            {{ dialogMode === 'add' ? '新增简单响应' : '编辑简单响应' }}
            <span v-if="dialogMode === 'edit'" class="text-primary-400 font-mono">{{ editingItem?.itemId }}</span>
          </h3>

          <div class="space-y-4">
            <!-- ID (仅新增) -->
            <div v-if="dialogMode === 'add'">
              <label class="block text-sm text-gray-400 mb-1">ID</label>
              <input
                v-model="form.itemId"
                type="text"
                placeholder="如: simpleres_2"
                class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 font-mono focus:outline-none focus:border-primary-500"
              />
            </div>

            <!-- Body -->
            <div>
              <label class="block text-sm text-gray-400 mb-1">Body <span class="text-gray-600">(支持占位符: {PORT}, {HOST}, {PATH} 等)</span></label>
              <textarea
                v-model="form.body"
                rows="3"
                placeholder="OK"
                class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 font-mono focus:outline-none focus:border-primary-500 resize-y"
              ></textarea>
            </div>

            <!-- ContentType + StatusCode -->
            <div class="flex gap-3">
              <div class="flex-1">
                <label class="block text-sm text-gray-400 mb-1">ContentType</label>
                <input
                  v-model="form.contentType"
                  type="text"
                  placeholder="text/plain"
                  class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 font-mono focus:outline-none focus:border-primary-500"
                />
              </div>
              <div class="w-24">
                <label class="block text-sm text-gray-400 mb-1">StatusCode</label>
                <input
                  v-model.number="form.statusCode"
                  type="number"
                  class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 focus:outline-none focus:border-primary-500"
                />
              </div>
            </div>

            <!-- Charset + ShowReq -->
            <div class="flex gap-3 items-end">
              <div class="flex-1">
                <label class="block text-sm text-gray-400 mb-1">Charset</label>
                <select
                  v-model="form.charset"
                  class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 focus:outline-none focus:border-primary-500"
                >
                  <option value="utf-8">utf-8</option>
                  <option value="ascii">ascii</option>
                  <option value="utf-16">utf-16</option>
                  <option value="utf-32">utf-32</option>
                  <option value="gb2312">gb2312</option>
                  <option value="iso-8859-1">iso-8859-1</option>
                </select>
              </div>
              <label class="flex items-center gap-2 text-sm text-gray-300 cursor-pointer pb-2">
                <input v-model="form.showReq" type="checkbox" class="rounded border-gray-500" />
                ShowReq
              </label>
            </div>
          </div>

          <!-- 按钮 -->
          <div class="flex justify-end gap-3 mt-6">
            <button
              @click="showDialog = false"
              class="px-4 py-2 text-sm text-gray-400 border border-dark-border rounded-lg hover:bg-white/5 transition-colors"
            >
              取消
            </button>
            <button
              @click="handleSave"
              :disabled="saving"
              class="px-4 py-2 text-sm bg-primary-500 text-gray-900 rounded-lg hover:bg-primary-400 transition-colors disabled:opacity-50"
            >
              {{ saving ? '保存中...' : '确定' }}
            </button>
          </div>
        </div>
      </div>
    </Teleport>
  </div>
</template>
