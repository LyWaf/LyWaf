<script setup lang="ts">
import { ref, onMounted } from 'vue'
import Section from '@/components/common/Section.vue'
import { ipApi } from '@/api'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()

const whitelist = ref<string[]>([])
const blacklist = ref<string[]>([])

// 加载数据
const loadData = async () => {
  try {
    const [wRes, bRes] = await Promise.all([
      ipApi.getWhitelist(),
      ipApi.getBlacklist()
    ])
    if (wRes.success) whitelist.value = wRes.whitelist || []
    if (bRes.success) blacklist.value = bRes.blacklist || []
  } catch {
    // 静默处理
  }
}

onMounted(loadData)

// 添加弹窗
const showDialog = ref(false)
const dialogTarget = ref<'white' | 'black'>('white')
const dialogIp = ref('')
const dialogLoading = ref(false)

const openAddDialog = (target: 'white' | 'black') => {
  dialogTarget.value = target
  dialogIp.value = ''
  showDialog.value = true
}

const submitAdd = async () => {
  const ip = dialogIp.value.trim()
  if (!ip) {
    showError('请输入 IP 或 CIDR')
    return
  }

  dialogLoading.value = true
  try {
    if (dialogTarget.value === 'white') {
      const res = await ipApi.addWhitelist(ip)
      if (res.success) {
        whitelist.value.push(ip)
        showSuccess(`已添加到白名单: ${ip}`)
        showDialog.value = false
      } else {
        showError(res.message || '添加失败')
      }
    } else {
      const res = await ipApi.addBlacklist(ip)
      if (res.success) {
        blacklist.value.push(ip)
        showSuccess(`已添加到黑名单: ${ip}`)
        showDialog.value = false
      } else {
        showError(res.message || '添加失败')
      }
    }
  } catch {
    showError('添加失败')
  } finally {
    dialogLoading.value = false
  }
}

// 删除确认弹窗
const showDeleteConfirm = ref(false)
const deleteInfo = ref<{ ip: string; target: 'white' | 'black' } | null>(null)

const confirmRemove = (ip: string, target: 'white' | 'black') => {
  deleteInfo.value = { ip, target }
  showDeleteConfirm.value = true
}

const doRemove = async () => {
  if (!deleteInfo.value) return
  const { ip, target } = deleteInfo.value
  try {
    if (target === 'white') {
      const res = await ipApi.removeWhitelist(ip)
      if (res.success) {
        whitelist.value = whitelist.value.filter(i => i !== ip)
        showSuccess(`已从白名单移除: ${ip}`)
      } else {
        showError(res.message || '移除失败')
      }
    } else {
      const res = await ipApi.removeBlacklist(ip)
      if (res.success) {
        blacklist.value = blacklist.value.filter(i => i !== ip)
        showSuccess(`已从黑名单移除: ${ip}`)
      } else {
        showError(res.message || '移除失败')
      }
    }
  } catch {
    showError('移除失败')
  } finally {
    showDeleteConfirm.value = false
    deleteInfo.value = null
  }
}
</script>

<template>
  <Section id="ip-control" title="IP 访问控制">
    <div class="grid grid-cols-1 lg:grid-cols-2 gap-6">
      <!-- 白名单 -->
      <div>
        <div class="flex items-center justify-between mb-4">
          <h3 class="text-gray-300 font-medium">
            IP 白名单
            <span class="badge badge-success ml-2">{{ whitelist.length }}</span>
          </h3>
          <button @click="openAddDialog('white')" class="btn btn-sm btn-primary">+ 添加</button>
        </div>
        <div class="space-y-2 max-h-[200px] overflow-y-auto">
          <div
            v-for="ip in whitelist"
            :key="ip"
            class="flex items-center justify-between p-3 bg-dark-card-hover rounded-lg"
          >
            <code class="text-green-400 text-sm">{{ ip }}</code>
            <button
              @click="confirmRemove(ip, 'white')"
              class="text-gray-500 hover:text-red-400 transition-colors"
            >
              ×
            </button>
          </div>
          <div v-if="whitelist.length === 0" class="text-gray-500 text-sm text-center py-4">
            暂无白名单 IP
          </div>
        </div>
      </div>

      <!-- 黑名单 -->
      <div>
        <div class="flex items-center justify-between mb-4">
          <h3 class="text-gray-300 font-medium">
            IP 黑名单
            <span class="badge badge-danger ml-2">{{ blacklist.length }}</span>
          </h3>
          <button @click="openAddDialog('black')" class="btn btn-sm btn-primary">+ 添加</button>
        </div>
        <div class="space-y-2 max-h-[200px] overflow-y-auto">
          <div
            v-for="ip in blacklist"
            :key="ip"
            class="flex items-center justify-between p-3 bg-dark-card-hover rounded-lg"
          >
            <code class="text-red-400 text-sm">{{ ip }}</code>
            <button
              @click="confirmRemove(ip, 'black')"
              class="text-gray-500 hover:text-red-400 transition-colors"
            >
              ×
            </button>
          </div>
          <div v-if="blacklist.length === 0" class="text-gray-500 text-sm text-center py-4">
            暂无黑名单 IP
          </div>
        </div>
      </div>
    </div>
  </Section>

  <!-- 添加 IP 弹窗 -->
  <Teleport to="body">
    <div v-if="showDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="showDialog = false"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-[420px] max-w-[90vw]">
        <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border">
          <h3 class="text-lg font-semibold text-gray-100">添加 IP 到{{ dialogTarget === 'white' ? '白' : '黑' }}名单</h3>
          <button @click="showDialog = false" class="text-gray-400 hover:text-gray-200 text-xl leading-none">&times;</button>
        </div>
        <div class="px-6 py-5">
          <label class="block text-sm text-gray-400 mb-1">IP 地址或 CIDR <span class="text-red-400">*</span></label>
          <input
            v-model="dialogIp"
            type="text"
            class="input"
            placeholder="例如 192.168.1.100 或 10.0.0.0/24"
            @keydown.enter="submitAdd"
          />
          <p class="text-xs text-gray-500 mt-2">支持单个 IP 或 CIDR 网段格式</p>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="showDialog = false" class="btn btn-secondary">取消</button>
          <button @click="submitAdd" :disabled="dialogLoading" class="btn btn-primary">
            {{ dialogLoading ? '提交中...' : '确定添加' }}
          </button>
        </div>
      </div>
    </div>
  </Teleport>

  <!-- 删除确认弹窗 -->
  <Teleport to="body">
    <div v-if="showDeleteConfirm" class="fixed inset-0 z-[100] flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="showDeleteConfirm = false"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-[400px] max-w-[90vw]">
        <div class="px-6 py-5">
          <h3 class="text-lg font-semibold text-gray-100 mb-2">确认移除</h3>
          <p class="text-gray-400">
            确定要从{{ deleteInfo?.target === 'white' ? '白' : '黑' }}名单移除
            <code class="text-red-400">{{ deleteInfo?.ip }}</code> 吗？
          </p>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="showDeleteConfirm = false" class="btn btn-secondary">取消</button>
          <button @click="doRemove" class="btn btn-danger">确定移除</button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
