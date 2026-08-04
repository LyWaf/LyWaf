<script setup lang="ts">
import { computed, ref, onMounted } from 'vue'
import Section from '@/components/common/Section.vue'
import { ipApi, type IpGroup } from '@/api'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()

const whitelist = ref<string[]>([])
const blacklist = ref<string[]>([])
const groups = ref<IpGroup[]>([])
const groupSearch = ref('')
const groupLoading = ref(false)

const filteredGroups = computed(() => {
  const keyword = groupSearch.value.trim().toLowerCase()
  if (!keyword) return groups.value
  return groups.value.filter(group =>
    group.name.toLowerCase().includes(keyword) ||
    group.description.toLowerCase().includes(keyword) ||
    group.entries.some(entry => entry.toLowerCase().includes(keyword))
  )
})

const formatTime = (value: string) => new Intl.DateTimeFormat('zh-CN', {
  year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit'
}).format(new Date(value))

const entryPreview = (group: IpGroup) => group.entries.slice(0, 3).join('、')

// 加载数据
const loadData = async () => {
  try {
    const [wRes, bRes, gRes] = await Promise.all([
      ipApi.getWhitelist(),
      ipApi.getBlacklist(),
      ipApi.getGroups()
    ])
    if (wRes.success) whitelist.value = wRes.whitelist || []
    if (bRes.success) blacklist.value = bRes.blacklist || []
    if (gRes.success) groups.value = gRes.groups || []
  } catch {
    // 静默处理
  }
}

onMounted(loadData)

const showGroupDialog = ref(false)
const editingGroupId = ref<string | null>(null)
const groupForm = ref({ name: '', description: '', entriesText: '' })

const openGroupDialog = (group?: IpGroup) => {
  editingGroupId.value = group?.id ?? null
  groupForm.value = {
    name: group?.name ?? '',
    description: group?.description ?? '',
    entriesText: group?.entries.join('\n') ?? ''
  }
  showGroupDialog.value = true
}

const parseEntries = (value: string) => value
  .split(/[\n,;\s]+/)
  .map(item => item.trim())
  .filter(Boolean)

const saveGroup = async () => {
  if (!groupForm.value.name.trim()) {
    showError('请输入 IP 组名称')
    return
  }
  groupLoading.value = true
  const payload = {
    name: groupForm.value.name.trim(),
    description: groupForm.value.description.trim(),
    entries: parseEntries(groupForm.value.entriesText)
  }
  try {
    const res = editingGroupId.value
      ? await ipApi.updateGroup(editingGroupId.value, payload)
      : await ipApi.createGroup(payload)
    if (!res.success || !res.group) {
      showError(res.message || '保存失败')
      return
    }
    const index = groups.value.findIndex(group => group.id === res.group!.id)
    if (index >= 0) groups.value[index] = res.group
    else groups.value.unshift(res.group)
    showGroupDialog.value = false
    showSuccess(editingGroupId.value ? 'IP 组已更新' : 'IP 组已创建')
  } catch {
    showError('保存 IP 组失败')
  } finally {
    groupLoading.value = false
  }
}

const pendingDeleteGroup = ref<IpGroup | null>(null)
const deleteGroup = async () => {
  if (!pendingDeleteGroup.value) return
  try {
    const res = await ipApi.deleteGroup(pendingDeleteGroup.value.id)
    if (!res.success) {
      showError(res.message || '删除失败')
      return
    }
    groups.value = groups.value.filter(group => group.id !== pendingDeleteGroup.value?.id)
    showSuccess('IP 组已删除')
    pendingDeleteGroup.value = null
  } catch {
    showError('删除 IP 组失败')
  }
}

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
  <Section id="ip-groups" title="IP 组">
    <div class="flex flex-col sm:flex-row sm:items-center justify-between gap-3 mb-5">
      <div class="relative w-full sm:w-72">
        <input v-model="groupSearch" class="input pl-10" placeholder="搜索名称、说明或 IP" />
        <span class="absolute left-3 top-2.5 text-gray-500" aria-hidden="true">⌕</span>
      </div>
      <button class="btn btn-primary shrink-0" @click="openGroupDialog()">添加 IP 组</button>
    </div>

    <div class="overflow-x-auto border border-dark-border rounded-lg">
      <table class="w-full min-w-[760px] text-sm">
        <thead class="bg-dark-card-hover text-gray-400">
          <tr>
            <th class="text-left font-medium px-4 py-3">名称</th>
            <th class="text-left font-medium px-4 py-3">内容</th>
            <th class="text-left font-medium px-4 py-3 w-32">更新时间</th>
            <th class="text-right font-medium px-4 py-3 w-32">操作</th>
          </tr>
        </thead>
        <tbody class="divide-y divide-dark-border">
          <tr v-for="group in filteredGroups" :key="group.id" class="hover:bg-dark-card-hover/60">
            <td class="px-4 py-4 align-top">
              <div class="font-medium text-gray-200">{{ group.name }}</div>
              <div v-if="group.description" class="text-xs text-gray-500 mt-1 max-w-xs truncate">{{ group.description }}</div>
            </td>
            <td class="px-4 py-4 align-top">
              <div v-if="group.entries.length" class="text-gray-300 font-mono text-xs">
                {{ entryPreview(group) }}<span v-if="group.entries.length > 3"> 等</span>
              </div>
              <div v-else class="text-gray-500">暂无条目</div>
              <div class="text-xs text-gray-500 mt-1">共 {{ group.entries.length }} 条</div>
            </td>
            <td class="px-4 py-4 text-gray-400 align-top whitespace-nowrap">{{ formatTime(group.updatedAt) }}</td>
            <td class="px-4 py-4 text-right align-top whitespace-nowrap">
              <button class="text-primary-400 hover:text-primary-300 mr-5" @click="openGroupDialog(group)">编辑</button>
              <button class="text-red-400 hover:text-red-300" @click="pendingDeleteGroup = group">删除</button>
            </td>
          </tr>
          <tr v-if="filteredGroups.length === 0">
            <td colspan="4" class="px-4 py-12 text-center text-gray-500">
              {{ groupSearch ? '没有匹配的 IP 组' : '暂无 IP 组' }}
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </Section>

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

  <Teleport to="body">
    <div v-if="showGroupDialog" class="fixed inset-0 z-[100] flex items-center justify-center p-4">
      <div class="absolute inset-0 bg-black/60" @click="showGroupDialog = false"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-lg shadow-2xl w-[560px] max-w-full">
        <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border">
          <h3 class="text-lg font-semibold text-gray-100">{{ editingGroupId ? '编辑 IP 组' : '添加 IP 组' }}</h3>
          <button class="text-gray-400 hover:text-gray-200 text-xl" title="关闭" @click="showGroupDialog = false">×</button>
        </div>
        <div class="px-6 py-5 space-y-4">
          <div>
            <label class="block text-sm text-gray-400 mb-1">组名称 <span class="text-red-400">*</span></label>
            <input v-model="groupForm.name" class="input" maxlength="64" placeholder="例如：办公网出口 IP" />
          </div>
          <div>
            <label class="block text-sm text-gray-400 mb-1">说明</label>
            <input v-model="groupForm.description" class="input" placeholder="该 IP 组的用途" />
          </div>
          <div>
            <label class="block text-sm text-gray-400 mb-1">IP / CIDR</label>
            <textarea v-model="groupForm.entriesText" class="input min-h-48 font-mono text-sm resize-y" placeholder="192.168.1.10&#10;10.0.0.0/24&#10;2001:db8::/32"></textarea>
            <p class="text-xs text-gray-500 mt-2">每行一个，支持 IPv4、IPv6 和 CIDR；重复项会自动去除。</p>
          </div>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button class="btn btn-secondary" @click="showGroupDialog = false">取消</button>
          <button class="btn btn-primary" :disabled="groupLoading" @click="saveGroup">{{ groupLoading ? '保存中...' : '保存' }}</button>
        </div>
      </div>
    </div>
  </Teleport>

  <Teleport to="body">
    <div v-if="pendingDeleteGroup" class="fixed inset-0 z-[100] flex items-center justify-center p-4">
      <div class="absolute inset-0 bg-black/60" @click="pendingDeleteGroup = null"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-lg shadow-2xl w-[420px] max-w-full">
        <div class="px-6 py-5">
          <h3 class="text-lg font-semibold text-gray-100 mb-2">删除 IP 组</h3>
          <p class="text-gray-400">确定删除“<span class="text-gray-200">{{ pendingDeleteGroup.name }}</span>”吗？组内 IP 不会加入黑白名单。</p>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button class="btn btn-secondary" @click="pendingDeleteGroup = null">取消</button>
          <button class="btn btn-danger" @click="deleteGroup">删除</button>
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
