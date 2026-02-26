<script setup lang="ts">
import { ref, watch } from 'vue'
import Section from '@/components/common/Section.vue'
import { ipApi } from '@/api'
import { useToast } from '@/composables/useToast'

interface BlockedIpInfo {
  ip: string
  type: string
  reason: string
  remainingSeconds?: number
}

interface Props {
  initialData?: BlockedIpInfo[]
}

const props = defineProps<Props>()
const { showSuccess, showError } = useToast()

const blockedIps = ref<BlockedIpInfo[]>([])

// 监听父组件传入的数据
watch(() => props.initialData, (newData) => {
  if (newData) {
    blockedIps.value = newData
  }
}, { immediate: true })

// 类型标签
const typeLabel = (type: string) => {
  switch (type) {
    case 'blocked': return '封禁'
    case 'captcha': return '验证码'
    case 'throttled': return '限速'
    default: return type
  }
}

// 类型样式
const typeClass = (type: string) => {
  switch (type) {
    case 'blocked': return 'bg-red-500/20 text-red-400'
    case 'captcha': return 'bg-yellow-500/20 text-yellow-400'
    case 'throttled': return 'bg-blue-500/20 text-blue-400'
    default: return 'bg-gray-500/20 text-gray-400'
  }
}

// 格式化剩余时间
const formatRemainingTime = (seconds?: number) => {
  if (seconds === undefined || seconds === null) return '永久'
  if (seconds <= 0) return '已过期'

  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  const secs = Math.floor(seconds % 60)

  if (days > 0) return `${days}天${hours}时`
  if (hours > 0) return `${hours}时${minutes}分`
  if (minutes > 0) return `${minutes}分${secs}秒`
  return `${secs}秒`
}

const emit = defineEmits<{
  (e: 'refresh'): void
}>()

// 封禁弹窗
const showDialog = ref(false)
const blockForm = ref({ ip: '', reason: '手动封禁', duration: 3600 })
const blockLoading = ref(false)

const openBlockDialog = () => {
  blockForm.value = { ip: '', reason: '手动封禁', duration: 3600 }
  showDialog.value = true
}

const submitBlock = async () => {
  const { ip, reason, duration } = blockForm.value
  if (!ip.trim()) {
    showError('请输入 IP 地址')
    return
  }

  blockLoading.value = true
  try {
    const res = await ipApi.blockIp(ip.trim(), reason || undefined, duration || undefined)
    if (res.success) {
      blockedIps.value.push({
        ip: ip.trim(),
        type: 'blocked',
        reason: reason || '手动封禁',
        remainingSeconds: duration || undefined,
      })
      showSuccess(`已封禁: ${ip.trim()}`)
      showDialog.value = false
      emit('refresh')
    } else {
      showError((res as any).message || '封禁失败')
    }
  } catch {
    showError('封禁失败')
  } finally {
    blockLoading.value = false
  }
}

const unblockIp = async (ip: string) => {
  if (!confirm(`确定要解封 ${ip} 吗？`)) return

  try {
    const res = await ipApi.unblockIp(ip)
    if (res.success) {
      blockedIps.value = blockedIps.value.filter(i => i.ip !== ip)
      showSuccess(`已解封: ${ip}`)
      emit('refresh')
    }
  } catch {
    showError('解封失败')
  }
}

const clearAll = async () => {
  if (!confirm('确定要清空所有封禁 IP 吗？')) return

  try {
    const res = await ipApi.clearBlockedIps()
    if (res.success) {
      blockedIps.value = []
      showSuccess('已清空封禁列表')
      emit('refresh')
    }
  } catch {
    showError('清空失败')
  }
}
</script>

<template>
  <Section id="blocked-ips" :title="`当前封禁/受限的 IP (${blockedIps.length})`">
    <template #actions>
      <div class="flex gap-2">
        <button @click="openBlockDialog" class="btn btn-sm btn-primary">+ 手动封禁</button>
        <button @click="clearAll" class="btn btn-sm btn-danger">清空全部</button>
      </div>
    </template>

    <div class="space-y-2 max-h-[400px] overflow-y-auto">
      <div
        v-for="item in blockedIps"
        :key="`${item.type}-${item.ip}`"
        class="flex items-center justify-between p-4 bg-dark-card-hover rounded-lg"
      >
        <div class="flex items-center gap-6">
          <div>
            <span class="text-gray-400 text-sm">IP 地址</span>
            <div class="text-red-400 font-mono">{{ item.ip }}</div>
          </div>
          <div>
            <span class="text-gray-400 text-sm">类型</span>
            <div>
              <span :class="typeClass(item.type)" class="inline-block px-2 py-0.5 rounded text-xs font-medium">
                {{ typeLabel(item.type) }}
              </span>
            </div>
          </div>
          <div v-if="item.reason">
            <span class="text-gray-400 text-sm">原因</span>
            <div class="text-gray-300">{{ item.reason }}</div>
          </div>
          <div>
            <span class="text-gray-400 text-sm">剩余时间</span>
            <div class="text-yellow-400">{{ formatRemainingTime(item.remainingSeconds) }}</div>
          </div>
        </div>
        <button
          @click="unblockIp(item.ip)"
          class="btn btn-sm btn-secondary"
        >
          解封
        </button>
      </div>

      <div v-if="blockedIps.length === 0" class="text-gray-500 text-center py-8">
        暂无封禁的 IP
      </div>
    </div>
  </Section>

  <!-- 封禁弹窗 -->
  <Teleport to="body">
    <div v-if="showDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="showDialog = false"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-[420px] max-w-[90vw]">
        <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border">
          <h3 class="text-lg font-semibold text-gray-100">手动封禁 IP</h3>
          <button @click="showDialog = false" class="text-gray-400 hover:text-gray-200 text-xl leading-none">&times;</button>
        </div>
        <div class="px-6 py-5 space-y-4">
          <div>
            <label class="block text-sm text-gray-400 mb-1">IP 地址 <span class="text-red-400">*</span></label>
            <input
              v-model="blockForm.ip"
              type="text"
              class="input"
              placeholder="例如 192.168.1.100"
              @keydown.enter="submitBlock"
            />
          </div>
          <div>
            <label class="block text-sm text-gray-400 mb-1">封禁原因</label>
            <input
              v-model="blockForm.reason"
              type="text"
              class="input"
              placeholder="手动封禁"
            />
          </div>
          <div>
            <label class="block text-sm text-gray-400 mb-1">封禁时长（秒，0 表示永久）</label>
            <input
              v-model.number="blockForm.duration"
              type="number"
              class="input"
              min="0"
              placeholder="3600"
            />
            <span class="text-xs text-gray-500 mt-1 block">
              {{ blockForm.duration > 0 ? formatRemainingTime(blockForm.duration) : '永久' }}
            </span>
          </div>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="showDialog = false" class="btn btn-secondary">取消</button>
          <button @click="submitBlock" :disabled="blockLoading" class="btn btn-primary">
            {{ blockLoading ? '提交中...' : '确定封禁' }}
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
