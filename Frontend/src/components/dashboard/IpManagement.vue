<script setup lang="ts">
import { locale } from '@/i18n'
import { t } from '@/i18n'
import { ref, onMounted, onBeforeUnmount } from 'vue'
import Section from '@/components/common/Section.vue'
import ListToolbar from '@/components/common/ListToolbar.vue'
import IpAccessList from './IpAccessList.vue'
import { ipApi, type IpGroup } from '@/api'
import { useToast } from '@/composables/useToast'

const { showSuccess, showError } = useToast()

const groups = ref<IpGroup[]>([])
const groupSearch = ref('')
const groupPage = ref(1)
const groupLoading = ref(false)
const groupTotal = ref(0)
const listLoading = ref(false)
const accessRevision = ref(0)
let generation = 0
onBeforeUnmount(() => { generation++ })

const formatTime = (value: string) => new Intl.DateTimeFormat(locale.value, {
  year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit'
}).format(new Date(value))

const entryPreview = (group: IpGroup) => group.entries.slice(0, 3).join('、')

// 加载数据
const loadData = async () => {
  const current = ++generation
  listLoading.value = true
  accessRevision.value++
  try {
    const result = await ipApi.listGroups(groupPage.value, 20, groupSearch.value)
    if (current !== generation) return
    groups.value = result.data
    groupTotal.value = result.total
    if (!result.data.length && groupPage.value > 1) {
      groupPage.value = Math.max(1, Math.ceil(result.total / 20))
      await loadData()
    }
  } catch {
    if (current === generation) showError(t('page.f93c6b2d56f7'))
  } finally {
    if (current === generation) listLoading.value = false
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
    showError(t('page.faaad037d9e4'))
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
      showError(res.message || t('ui.save.failed'))
      return
    }
    const index = groups.value.findIndex(group => group.id === res.group!.id)
    if (index >= 0) groups.value[index] = res.group
    else groups.value.unshift(res.group)
    showGroupDialog.value = false
    await loadData()
    showSuccess(editingGroupId.value ? t('page.df4875f080ca') : t('page.3ce3ca373868'))
  } catch {
    showError(t('page.91759daa44ea'))
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
      showError(res.message || t('settings.deletion.failed'))
      return
    }
    groups.value = groups.value.filter(group => group.id !== pendingDeleteGroup.value?.id)
    showSuccess(t('page.2ad3819387e1'))
    pendingDeleteGroup.value = null
    await loadData()
  } catch {
    showError(t('page.dfccbe2583ca'))
  }
}

const setGroupUsage = async (group: IpGroup, target: 'white' | 'black') => {
  const whitelistEnabled = target === 'white' ? !group.whitelistEnabled : false
  const blacklistEnabled = target === 'black' ? !group.blacklistEnabled : false
  try {
    const res = await ipApi.setGroupUsage(group.id, whitelistEnabled, blacklistEnabled)
    if (!res.success || !res.group) {
      showError(res.message || t('page.e4ecfb26982c'))
      return
    }
    const index = groups.value.findIndex(item => item.id === group.id)
    if (index >= 0) groups.value[index] = res.group
    await loadData()
    showSuccess(whitelistEnabled ? t('page.824ca37bdd94') : blacklistEnabled ? t('page.e0c468c0c218') : t('page.c48b1bcb4adf'))
  } catch {
    showError(t('page.e4ecfb26982c'))
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
    showError(t('page.3d0f1a2926a9'))
    return
  }

  dialogLoading.value = true
  try {
    if (dialogTarget.value === 'white') {
      const res = await ipApi.addWhitelist(ip)
      if (res.success) {
        accessRevision.value++
        showSuccess(t('page.044758b1a391', { p0: ip }))
        showDialog.value = false
      } else {
        showError(res.message || t('dashboard.failed.to.add'))
      }
    } else {
      const res = await ipApi.addBlacklist(ip)
      if (res.success) {
        accessRevision.value++
        showSuccess(t('page.04bb99d59d8a', { p0: ip }))
        showDialog.value = false
      } else {
        showError(res.message || t('dashboard.failed.to.add'))
      }
    }
  } catch {
    showError(t('dashboard.failed.to.add'))
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
        accessRevision.value++
        showSuccess(t('page.e75468d35495', { p0: ip }))
      } else {
        showError(res.message || t('page.4757fc1d4709'))
      }
    } else {
      const res = await ipApi.removeBlacklist(ip)
      if (res.success) {
        accessRevision.value++
        showSuccess(t('page.a75e86dc7b8e', { p0: ip }))
      } else {
        showError(res.message || t('page.4757fc1d4709'))
      }
    }
  } catch {
    showError(t('page.4757fc1d4709'))
  } finally {
    showDeleteConfirm.value = false
    deleteInfo.value = null
  }
}
</script>

<template>
  <Section id="ip-groups" :title="t('page.24c8624a21fa')">
    <div class="flex flex-col sm:flex-row sm:items-center justify-between gap-3 mb-5">
      <button class="btn btn-primary shrink-0" @click="openGroupDialog()">{{ t('page.5d29048c1efa') }}</button>
    </div>
    <ListToolbar v-model:page="groupPage" v-model:search="groupSearch" :total="groupTotal" :busy="listLoading" @refresh="loadData" />

    <div class="overflow-x-auto border border-dark-border rounded-lg">
      <table class="w-full min-w-[760px] text-sm">
        <thead class="bg-dark-card-hover text-gray-400">
          <tr>
            <th class="text-left font-medium px-4 py-3">{{ t('fields.Name') }}</th>
            <th class="text-left font-medium px-4 py-3">{{ t('page.7a688306423b') }}</th>
            <th class="text-left font-medium px-4 py-3 w-32">{{ t('page.0a5f9a892960') }}</th>
            <th class="text-left font-medium px-4 py-3 w-52">{{ t('page.05047cd9b13e') }}</th>
            <th class="text-right font-medium px-4 py-3 w-32">{{ t('settings.actions') }}</th>
          </tr>
        </thead>
        <tbody class="divide-y divide-dark-border">
          <tr v-for="group in groups" :key="group.id" class="hover:bg-dark-card-hover/60">
            <td class="px-4 py-4 align-top">
              <div class="font-medium text-gray-200">{{ group.name }}</div>
              <div v-if="group.description" class="text-xs text-gray-500 mt-1 max-w-xs truncate">{{ group.description }}</div>
            </td>
            <td class="px-4 py-4 align-top">
              <div v-if="group.entries.length" class="text-gray-300 font-mono text-xs">
                {{ entryPreview(group) }}<span v-if="group.entries.length > 3"> {{ t('page.385a845b6094') }}</span>
              </div>
              <div v-else class="text-gray-500">{{ t('page.590fdddd01bd') }}</div>
              <div class="text-xs text-gray-500 mt-1">{{ t('page.76e547a8fa54') }} {{ group.entries.length }} {{ t('page.f004f1d84cf9') }}</div>
            </td>
            <td class="px-4 py-4 text-gray-400 align-top whitespace-nowrap">{{ formatTime(group.updatedAt) }}</td>
            <td class="px-4 py-4 align-top">
              <div class="flex gap-2">
                <button
                  class="btn btn-sm"
                  :class="group.whitelistEnabled ? 'bg-emerald-700 text-white' : 'btn-secondary'"
                  @click="setGroupUsage(group, 'white')"
                >{{ t('page.27e2d9e2d924') }}</button>
                <button
                  class="btn btn-sm"
                  :class="group.blacklistEnabled ? 'bg-red-800 text-white' : 'btn-secondary'"
                  @click="setGroupUsage(group, 'black')"
                >{{ t('page.174ec5a4ab6a') }}</button>
              </div>
            </td>
            <td class="px-4 py-4 text-right align-top whitespace-nowrap">
              <button class="text-primary-400 hover:text-primary-300 mr-5" @click="openGroupDialog(group)">{{ t('common.edit') }}</button>
              <button class="text-red-400 hover:text-red-300" @click="pendingDeleteGroup = group">{{ t('common.delete') }}</button>
            </td>
          </tr>
          <tr v-if="groups.length === 0">
            <td colspan="5" class="px-4 py-12 text-center text-gray-500">
              {{ groupSearch ? t('page.65ecedae49e3') : t('page.da656161fa1a') }}
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </Section>

  <Section id="ip-control" :title="t('page.2316186c99ee')">
    <div class="grid grid-cols-1 lg:grid-cols-2 gap-6">
      <IpAccessList target="white" :revision="accessRevision" @add="openAddDialog('white')" @remove="confirmRemove($event, 'white')" @usage="setGroupUsage($event, 'white')" />
      <IpAccessList target="black" :revision="accessRevision" @add="openAddDialog('black')" @remove="confirmRemove($event, 'black')" @usage="setGroupUsage($event, 'black')" />
    </div>
  </Section>

  <!-- 添加 IP 弹窗 -->
  <Teleport to="body">
    <div v-if="showDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
      <div class="absolute inset-0 bg-black/60" @click="showDialog = false"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-[420px] max-w-[90vw]">
        <div class="flex items-center justify-between px-6 py-4 border-b border-dark-border">
          <h3 class="text-lg font-semibold text-gray-100">{{ t('page.f403125c7d85') }}{{ dialogTarget === 'white' ? t('page.3366645622a7') : t('page.59650f41f74b') }}{{ t('page.af488f5f257b') }}</h3>
          <button @click="showDialog = false" class="text-gray-400 hover:text-gray-200 text-xl leading-none">&times;</button>
        </div>
        <div class="px-6 py-5">
          <label class="block text-sm text-gray-400 mb-1">{{ t('page.f204a2f406a9') }} <span class="text-red-400">*</span></label>
          <input
            v-model="dialogIp"
            type="text"
            class="input"
            :placeholder="t('page.dd7f26cd1563')"
            @keydown.enter="submitAdd"
          />
          <p class="text-xs text-gray-500 mt-2">{{ t('page.02829c9a1dfa') }}</p>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="showDialog = false" class="btn btn-secondary">{{ t('common.cancel') }}</button>
          <button @click="submitAdd" :disabled="dialogLoading" class="btn btn-primary">
            {{ dialogLoading ? t('settings.submitting') : t('page.1945909fd7f7') }}
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
          <h3 class="text-lg font-semibold text-gray-100">{{ editingGroupId ? t('page.8188b47e940c') : t('page.5d29048c1efa') }}</h3>
          <button class="text-gray-400 hover:text-gray-200 text-xl" :title="t('common.close')" @click="showGroupDialog = false">×</button>
        </div>
        <div class="px-6 py-5 space-y-4">
          <div>
            <label class="block text-sm text-gray-400 mb-1">{{ t('page.3baad48a4c94') }} <span class="text-red-400">*</span></label>
            <input v-model="groupForm.name" class="input" maxlength="64" :placeholder="t('page.507b72bc92f9')" />
          </div>
          <div>
            <label class="block text-sm text-gray-400 mb-1">{{ t('fields.Description') }}</label>
            <input v-model="groupForm.description" class="input" :placeholder="t('page.7440b9d7180a')" />
          </div>
          <div>
            <label class="block text-sm text-gray-400 mb-1">IP / CIDR</label>
            <textarea v-model="groupForm.entriesText" class="input min-h-48 font-mono text-sm resize-y" placeholder="192.168.1.10&#10;10.0.0.0/24&#10;2001:db8::/32"></textarea>
            <p class="text-xs text-gray-500 mt-2">{{ t('page.e7750f439ea7') }}</p>
          </div>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button class="btn btn-secondary" @click="showGroupDialog = false">{{ t('common.cancel') }}</button>
          <button class="btn btn-primary" :disabled="groupLoading" @click="saveGroup">{{ groupLoading ? t('page.917b1c1f18d0') : t('common.save') }}</button>
        </div>
      </div>
    </div>
  </Teleport>

  <Teleport to="body">
    <div v-if="pendingDeleteGroup" class="fixed inset-0 z-[100] flex items-center justify-center p-4">
      <div class="absolute inset-0 bg-black/60" @click="pendingDeleteGroup = null"></div>
      <div class="relative bg-dark-card border border-dark-border rounded-lg shadow-2xl w-[420px] max-w-full">
        <div class="px-6 py-5">
          <h3 class="text-lg font-semibold text-gray-100 mb-2">{{ t('page.c984dcea396f') }}</h3>
          <p class="text-gray-400">{{ t('page.6ec00e356283') }}<span class="text-gray-200">{{ pendingDeleteGroup.name }}</span>{{ t('page.6dc425f0b2a4') }}</p>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button class="btn btn-secondary" @click="pendingDeleteGroup = null">{{ t('common.cancel') }}</button>
          <button class="btn btn-danger" @click="deleteGroup">{{ t('common.delete') }}</button>
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
          <h3 class="text-lg font-semibold text-gray-100 mb-2">{{ t('page.ee7dea070d88') }}</h3>
          <p class="text-gray-400">
            {{ t('page.929ab136ec59') }}{{ deleteInfo?.target === 'white' ? t('page.3366645622a7') : t('page.59650f41f74b') }}{{ t('page.4384a5b85e11') }}
            <code class="text-red-400">{{ deleteInfo?.ip }}</code> {{ t('page.9d45d8943988') }}
          </p>
        </div>
        <div class="flex justify-end gap-3 px-6 py-4 border-t border-dark-border">
          <button @click="showDeleteConfirm = false" class="btn btn-secondary">{{ t('common.cancel') }}</button>
          <button @click="doRemove" class="btn btn-danger">{{ t('page.4948800ce892') }}</button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
