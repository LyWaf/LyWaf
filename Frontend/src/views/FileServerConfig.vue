<script setup lang="ts">
import { t } from '@/i18n'
import { ref, onMounted } from 'vue'
import { fileServerApi } from '@/api'
import ListToolbar from '@/components/common/ListToolbar.vue'
import { useToast } from '@/composables/useToast'
import type { FileServerItem } from '@/types'

const { showSuccess, showError } = useToast()

// 数据状态
const loading = ref(false)
const items = ref<FileServerItem[]>([])
const page = ref(1)
const total = ref(0)
const search = ref('')
let listRequest = 0

// 对话框状态
const showDialog = ref(false)
const dialogMode = ref<'add' | 'edit'>('add')
const saving = ref(false)
const editingItem = ref<FileServerItem | null>(null)
const form = ref({
  itemId: '',
  prefix: '/',
  basePath: '',
  browse: false,
  preCompressed: false,
  tryFiles: '',
  defaultFiles: '',
})

// 加载数据
const loadData = async () => {
  const request = ++listRequest
  loading.value = true
  try {
    const res = await fileServerApi.list(page.value, search.value)
    if (request === listRequest && res.success) {
      items.value = res.data
      total.value = res.total
    }
  } catch (error) {
    if (request === listRequest) showError(t('page.706b475f8573'))
  } finally {
    if (request === listRequest) loading.value = false
  }
}

// 打开新增对话框
const openAddDialog = () => {
  dialogMode.value = 'add'
  editingItem.value = null
  form.value = {
    itemId: '',
    prefix: '/',
    basePath: './wwwroot',
    browse: false,
    preCompressed: false,
    tryFiles: '',
    defaultFiles: 'index.html, index.htm',
  }
  showDialog.value = true
}

// 打开编辑对话框
const openEditDialog = (item: FileServerItem) => {
  dialogMode.value = 'edit'
  editingItem.value = item
  form.value = {
    itemId: item.itemId,
    prefix: item.prefix,
    basePath: item.basePath,
    browse: item.browse,
    preCompressed: item.preCompressed,
    tryFiles: item.tryFiles.join(', '),
    defaultFiles: item.defaultFiles.join(', '),
  }
  showDialog.value = true
}

// 解析逗号分隔的字符串
const parseList = (str: string): string[] =>
  str.split(/[,\s]+/).map(s => s.trim()).filter(s => s.length > 0)

// 保存
const handleSave = async () => {
  saving.value = true
  try {
    const tryFiles = parseList(form.value.tryFiles)
    const defaultFiles = parseList(form.value.defaultFiles)

    if (dialogMode.value === 'add') {
      if (!form.value.itemId.trim()) {
        showError(t('page.a45065ad89c2'))
        saving.value = false
        return
      }
      const res = await fileServerApi.addItem({
        itemId: form.value.itemId.trim(),
        prefix: form.value.prefix || '/',
        basePath: form.value.basePath || undefined,
        browse: form.value.browse,
        preCompressed: form.value.preCompressed,
        tryFiles: tryFiles.length > 0 ? tryFiles : undefined,
        defaultFiles: defaultFiles.length > 0 ? defaultFiles : undefined,
      })
      if (res.success) {
        showSuccess(t('page.a6df95883889'))
        showDialog.value = false
        await loadData()
      } else {
        showError(res.message || t('page.a2ae1e9901c1'))
      }
    } else {
      if (!editingItem.value) return
      const res = await fileServerApi.updateItem({
        itemId: editingItem.value.itemId,
        prefix: form.value.prefix,
        basePath: form.value.basePath,
        browse: form.value.browse,
        preCompressed: form.value.preCompressed,
        tryFiles,
        defaultFiles,
      })
      if (res.success) {
        showSuccess(t('page.575b777a757c'))
        showDialog.value = false
        await loadData()
      } else {
        showError(res.message || t('page.ec99e5c45d64'))
      }
    }
  } catch (error) {
    showError(dialogMode.value === 'add' ? t('page.a2ae1e9901c1') : t('page.ec99e5c45d64'))
  } finally {
    saving.value = false
  }
}

// 删除单个文件服务
const removeItem = async (item: FileServerItem) => {
  if (!confirm(t('page.878c70deefc1', { p0: item.itemId }))) return
  try {
    const res = await fileServerApi.removeItem(item.itemId)
    if (res.success) {
      showSuccess(t('page.a89a44e75a8b'))
      await loadData()
    } else {
      showError(res.message || t('settings.deletion.failed'))
    }
  } catch (error) {
    showError(t('settings.deletion.failed'))
  }
}

// 删除所有文件服务补丁
const removePatch = async () => {
  if (!confirm(t('page.de640c02b729'))) return
  try {
    const res = await fileServerApi.removePatch()
    if (res.success) {
      showSuccess(t('page.1803eff6f4de'))
      await loadData()
    } else {
      showError(res.message || t('page.2f476ccaec39'))
    }
  } catch (error) {
    showError(t('page.2f476ccaec39'))
  }
}

onMounted(loadData)
</script>

<template>
  <div class="space-y-6">
    <div class="card">
      <div class="flex items-center justify-between mb-4">
        <h2 class="text-lg font-medium text-gray-100">{{ t('page.146619a0ce01') }}</h2>
        <div class="flex items-center gap-2">
          <button @click="removePatch" class="btn btn-sm btn-warning flex items-center gap-1">
            {{ t('page.c9fee9110ba0') }}
          </button>
          <button @click="openAddDialog" class="btn btn-sm btn-primary flex items-center gap-1">
            <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
            </svg>
            {{ t('page.0006d696d8e1') }}
          </button>
        </div>
      </div>

      <!-- 表格 -->
      <ListToolbar v-model:page="page" v-model:search="search" :total="total" :busy="loading" @refresh="loadData" />
      <div class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead>
            <tr class="border-b border-dark-border text-gray-400">
              <th class="py-3 px-3 text-left">ID</th>
              <th class="py-3 px-3 text-left">{{ t('settings.source') }}</th>
              <th class="py-3 px-3 text-left">Prefix</th>
              <th class="py-3 px-3 text-left">BasePath</th>
              <th class="py-3 px-3 text-left">TryFiles</th>
              <th class="py-3 px-3 text-left">Browse</th>
              <th class="py-3 px-3 text-left">{{ t('settings.actions') }}</th>
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
                >{{ t('page.70f565337ffd') }}</span>
                <span
                  v-else
                  class="inline-block px-1.5 py-0.5 text-xs bg-gray-500/20 text-gray-400 rounded"
                >{{ t('page.844b8cc8dff7') }}</span>
              </td>
              <td class="py-3 px-3 text-primary-400 font-mono">{{ item.prefix }}</td>
              <td class="py-3 px-3 text-gray-300 font-mono">{{ item.basePath }}</td>
              <td class="py-3 px-3 text-gray-300">
                <div v-if="item.tryFiles.length > 0" class="flex flex-wrap gap-1">
                  <span
                    v-for="tf in item.tryFiles"
                    :key="tf"
                    class="inline-block px-1.5 py-0.5 text-xs bg-blue-500/20 text-blue-400 rounded"
                  >{{ tf }}</span>
                </div>
                <span v-else class="text-gray-500">-</span>
              </td>
              <td class="py-3 px-3">
                <span
                  :class="item.browse
                    ? 'text-green-400'
                    : 'text-gray-500'"
                >{{ item.browse ? 'ON' : 'OFF' }}</span>
              </td>
              <td class="py-3 px-3">
                <div class="flex items-center gap-2">
                  <button
                    @click="openEditDialog(item)"
                    class="px-3 py-1 text-xs border border-primary-500 text-primary-400 rounded hover:bg-primary-500/10 transition-colors"
                  >
                    {{ t('common.edit') }}
                  </button>
                  <button
                    @click="removeItem(item)"
                    class="px-3 py-1 text-xs border border-red-500 text-red-400 rounded hover:bg-red-500/10 transition-colors"
                  >
                    {{ t('common.delete') }}
                  </button>
                </div>
              </td>
            </tr>
          </tbody>
        </table>

        <div v-if="!loading && items.length === 0" class="text-center py-12 text-gray-500">
          {{ t('page.1b647f145df5') }}
        </div>
      </div>
    </div>

    <div v-if="loading" class="text-center py-12 text-gray-500">
      {{ t('common.loading') }}
    </div>

    <!-- 新增/编辑对话框 -->
    <Teleport to="body">
      <div v-if="showDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="showDialog = false"></div>

        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-full max-w-lg mx-4 p-6">
          <h3 class="text-lg font-medium text-gray-100 mb-4">
            {{ dialogMode === 'add' ? t('page.bd8088f3a890') : t('page.205df6af3304') }}
            <span v-if="dialogMode === 'edit'" class="text-primary-400 font-mono">{{ editingItem?.itemId }}</span>
          </h3>

          <div class="space-y-4">
            <!-- ID (仅新增) -->
            <div v-if="dialogMode === 'add'">
              <label class="block text-sm text-gray-400 mb-1">ID</label>
              <input
                v-model="form.itemId"
                type="text"
                :placeholder="t('page.546aadec0057')"
                class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 font-mono focus:outline-none focus:border-primary-500"
              />
            </div>

            <!-- Prefix + BasePath -->
            <div class="flex gap-3">
              <div class="w-1/3">
                <label class="block text-sm text-gray-400 mb-1">Prefix</label>
                <input
                  v-model="form.prefix"
                  type="text"
                  placeholder="/"
                  class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 font-mono focus:outline-none focus:border-primary-500"
                />
              </div>
              <div class="flex-1">
                <label class="block text-sm text-gray-400 mb-1">BasePath</label>
                <input
                  v-model="form.basePath"
                  type="text"
                  placeholder="./wwwroot"
                  class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 font-mono focus:outline-none focus:border-primary-500"
                />
              </div>
            </div>

            <!-- TryFiles -->
            <div>
              <label class="block text-sm text-gray-400 mb-1">TryFiles <span class="text-gray-600">{{ t('page.04a69541d065') }}</span></label>
              <input
                v-model="form.tryFiles"
                type="text"
                :placeholder="t('page.6a904c002797')"
                class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 font-mono focus:outline-none focus:border-primary-500"
              />
            </div>

            <!-- Default Files -->
            <div>
              <label class="block text-sm text-gray-400 mb-1">Default <span class="text-gray-600">{{ t('page.64e2e00835f2') }}</span></label>
              <input
                v-model="form.defaultFiles"
                type="text"
                placeholder="index.html, index.htm"
                class="w-full bg-dark-sidebar border border-dark-border rounded-lg px-3 py-2 text-sm text-gray-200 font-mono focus:outline-none focus:border-primary-500"
              />
            </div>

            <!-- Browse + PreCompressed -->
            <div class="flex gap-6">
              <label class="flex items-center gap-2 text-sm text-gray-300 cursor-pointer">
                <input v-model="form.browse" type="checkbox" class="rounded border-gray-500" />
                {{ t('page.3a66d51ac371') }}
              </label>
              <label class="flex items-center gap-2 text-sm text-gray-300 cursor-pointer">
                <input v-model="form.preCompressed" type="checkbox" class="rounded border-gray-500" />
                {{ t('page.4e074bf17bd2') }}
              </label>
            </div>
          </div>

          <!-- 按钮 -->
          <div class="flex justify-end gap-3 mt-6">
            <button
              @click="showDialog = false"
              class="px-4 py-2 text-sm text-gray-400 border border-dark-border rounded-lg hover:bg-white/5 transition-colors"
            >
              {{ t('common.cancel') }}
            </button>
            <button
              @click="handleSave"
              :disabled="saving"
              class="px-4 py-2 text-sm bg-primary-500 text-gray-900 rounded-lg hover:bg-primary-400 transition-colors disabled:opacity-50"
            >
              {{ saving ? t('page.917b1c1f18d0') : t('common.confirm') }}
            </button>
          </div>
        </div>
      </div>
    </Teleport>
  </div>
</template>
