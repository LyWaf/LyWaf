<script setup lang="ts">
import LanguageSelect from '@/components/common/LanguageSelect.vue'
import { t, locale, translateText } from '@/i18n'
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import ListToolbar from '@/components/common/ListToolbar.vue'
import { useRouter } from 'vue-router'
import { settingsApi, authApi } from '@/api'
import type { RuntimeLogEntry, ManagementUser, ManagementRole } from '@/api'
import { useToast } from '@/composables/useToast'
import { useAuth } from '@/composables/useAuth'
import ManagementMfa from '@/components/ManagementMfa.vue'
import type { CertDetail, AuditLogEntry, CertificateRuntimeStatus, AcmeAccountStatus } from '@/types'

const { showSuccess, showError } = useToast()
const router = useRouter()
const { can } = useAuth()

// ==================== Tab 切换 ====================
const activeTab = ref<'certs' | 'console' | 'logs' | 'runtime'>('certs')
const tabs = computed(() => [
  { key: 'certs' as const, label: t('settings.certificate.management'), icon: '🔒' },
  { key: 'console' as const, label: t('settings.console.management'), icon: '🖥️' },
  { key: 'logs' as const, label: t('settings.system.logs'), icon: '📋' },
  { key: 'runtime' as const, label: t('settings.runtime.logs'), icon: '🕐' },
])

// ==================== 证书管理 ====================
const certsLoading = ref(false)
const certs = ref<CertDetail[]>([])
const certPage = ref(1)
const certSearch = ref('')
const certTotal = ref(0)
let certRequest = 0
onBeforeUnmount(() => { certRequest++ })
const showUploadDialog = ref(false)
const uploadForm = ref({ domain: '', pemContent: '', keyContent: '' })
const uploading = ref(false)
const showDeleteConfirm = ref(false)
const deletingCert = ref<CertDetail | null>(null)
const deleting = ref(false)
const runtimeCertificates = ref<CertificateRuntimeStatus[]>([])
const acmeAccounts = ref<AcmeAccountStatus[]>([])

const loadCerts = async () => {
  const request = ++certRequest
  certsLoading.value = true
  try {
    const [res, status] = await Promise.all([
      settingsApi.getCerts(certPage.value, certSearch.value),
      settingsApi.getCertificateStatus(),
    ])
    if (request !== certRequest) return
    if (res?.success) {
      certs.value = res.data
      certTotal.value = res.total
    }
    runtimeCertificates.value = status.certificates || []
    acmeAccounts.value = status.accounts || []
    if (res.success && !res.data.length && certPage.value > 1) {
      certPage.value = Math.max(1, Math.ceil(res.total / 20))
      await loadCerts()
    }
  } catch {
    if (request === certRequest) showError(t('settings.failed.to.load.certificates'))
  } finally {
    if (request === certRequest) certsLoading.value = false
  }
}

const handleUploadCert = async () => {
  if (!uploadForm.value.domain || !uploadForm.value.pemContent || !uploadForm.value.keyContent) {
    showError(t('settings.complete.all.fields'))
    return
  }
  uploading.value = true
  try {
    const res = await settingsApi.uploadCert(uploadForm.value) as unknown as { success: boolean; message?: string }
    if (res?.success) {
      showSuccess(t('settings.certificate.uploaded.reload.configuration.to.apply'))
      showUploadDialog.value = false
      uploadForm.value = { domain: '', pemContent: '', keyContent: '' }
      loadCerts()
    } else {
      showError(res?.message || t('settings.upload.failed'))
    }
  } catch {
    showError(t('settings.failed.to.upload.certificate'))
  } finally {
    uploading.value = false
  }
}

const confirmDeleteCert = (cert: CertDetail) => {
  deletingCert.value = cert
  showDeleteConfirm.value = true
}

const executeDeleteCert = async () => {
  if (!deletingCert.value) return
  deleting.value = true
  try {
    const res = await settingsApi.deleteCert(deletingCert.value.pemFile) as unknown as { success: boolean; message?: string }
    if (res?.success) {
      showSuccess(t('settings.certificate.deleted.reload.configuration.to.apply'))
      showDeleteConfirm.value = false
      deletingCert.value = null
      loadCerts()
    } else {
      showError(res?.message || t('settings.deletion.failed'))
    }
  } catch {
    showError(t('settings.failed.to.delete.certificate'))
  } finally {
    deleting.value = false
  }
}

const certTypeLabel = (type: string) => type === 'acme' ? t('settings.free.certificate') : t('settings.upload.certificate')
const certTypeColor = (type: string) => type === 'acme' ? 'bg-green-500/15 text-green-400' : 'bg-blue-500/15 text-blue-400'

// ---- ACME 申请/续期 ----
const showAcmeDialog = ref(false)
const acmeForm = ref({ domain: '', email: '' })
const acmeApplying = ref(false)
const acmeStatus = ref('')
const renewingCert = ref<CertDetail | null>(null)
const renewing = ref(false)

const openAcmeDialog = async () => {
  try {
    const res = await settingsApi.getAcmeEmail() as unknown as { success: boolean; email: string }
    if (res?.success && res.email) {
      acmeForm.value.email = res.email
    }
  } catch {}
  acmeForm.value.domain = ''
  showAcmeDialog.value = true
}

const handleAcmeApply = async () => {
  if (!acmeForm.value.domain || !acmeForm.value.email) {
    showError(t('settings.enter.a.domain.and.email.address'))
    return
  }
  acmeApplying.value = true
  acmeStatus.value = t('settings.submitting.certificate.issuance.task')
  try {
    const res = await settingsApi.acmeApply(acmeForm.value)
    if (res?.success) {
      showSuccess(t('settings.certificate.issuance.task.submitted'))
      showAcmeDialog.value = false
      await router.push({ path: '/management-tasks', query: { task: res.data.id } })
    } else {
      showError(res?.errorMessage || t('settings.certificate.request.failed'))
    }
  } catch {
    showError(t('settings.certificate.request.failed.check.that.the.domain.resolves.to.this.server'))
  } finally {
    acmeApplying.value = false
    acmeStatus.value = ''
  }
}

const handleRenewCert = async (cert: CertDetail) => {
  renewingCert.value = cert
  renewing.value = true
  try {
    const res = await settingsApi.acmeRenew({ domain: cert.domain })
    if (res?.success) {
      showSuccess(t('settings.certificate.renewal.task.submitted'))
      await router.push({ path: '/management-tasks', query: { task: res.data.id } })
    } else {
      showError(res?.errorMessage || t('settings.certificate.renewal.failed'))
    }
  } catch {
    showError(t('settings.certificate.renewal.failed'))
  } finally {
    renewing.value = false
    renewingCert.value = null
  }
}

// ==================== 控制台管理 ====================
const consoleLoading = ref(false)
const consoleInfo = ref<{ username: string; lastLoginAt: string | null; controlListen: { host: string; port: number } } | null>(null)
const showPasswordDialog = ref(false)
const passwordForm = ref({ currentPassword: '', newPassword: '', confirmPassword: '' })
const changingPassword = ref(false)
const managementUsers = ref<ManagementUser[]>([])
const userPage = ref(1)
const userTotal = ref(0)
const userSearch = ref('')
let userListRequest = 0
const userForm = ref({ username: '', password: '', role: 'auditor' })
const generatedPassword = ref('')
const managementRoles = ref<ManagementRole[]>([])
const visibleRoles = ref<ManagementRole[]>([])
const rolePage = ref(1)
const roleSearch = ref('')
const roleTotal = ref(0)
const roleListLoading = ref(false)
let roleRequest = 0
onBeforeUnmount(() => { roleRequest++; userListRequest++ })
const loadRoles = async () => {
  const current = ++roleRequest
  roleListLoading.value = true
  try {
    const result = await authApi.rolePage(rolePage.value, roleSearch.value)
    if (current !== roleRequest) return
    visibleRoles.value = result.data
    roleTotal.value = result.total
    if (!result.data.length && rolePage.value > 1) {
      rolePage.value = Math.max(1, Math.ceil(result.total / 20))
      await loadRoles()
    }
  } catch {
    if (current === roleRequest) showError(t('settings.failed.to.load.roles'))
  } finally {
    if (current === roleRequest) roleListLoading.value = false
  }
}
const roleForm = ref({ name: '', permissions: ['read'] })
const roleBusy = ref(false)
const permissionLabels = computed<Record<string, string>>(() => ({ read: t('settings.read'), write: t('settings.write'), 'users.manage': t('settings.manage.users'), 'system.manage': t('settings.manage.system') }))
const saveManagementRole = async () => {
  roleBusy.value = true
  try {
    await authApi.saveRole(roleForm.value)
    roleForm.value = { name: '', permissions: ['read'] }
    await loadManagementUsers()
    showSuccess(t('settings.role.saved.affected.users.must.sign.in.again'))
  } catch (error: any) { showError(error?.message || t('settings.failed.to.save.role')) }
  finally { roleBusy.value = false }
}
const deleteManagementRole = async (name: string) => {
  try { await authApi.deleteRole(name); await loadManagementUsers() }
  catch (error: any) { showError(error?.message || t('settings.failed.to.delete.role')) }
}

const loadManagementUsers = async () => {
  if (!can('users.manage')) return
  const request = ++userListRequest
  const res = await authApi.users(userPage.value, userSearch.value)
  const roles = await authApi.roles()
  if (request !== userListRequest) return
  managementUsers.value = res.data || []
  userTotal.value = res.total
  managementRoles.value = roles.data || []
  await loadRoles()
}

const createManagementUser = async () => {
  try {
    await authApi.createUser(userForm.value)
    userForm.value = { username: '', password: '', role: 'auditor' }
    await loadManagementUsers()
    showSuccess(t('settings.management.user.created'))
  } catch (error: any) { showError(error?.response?.data?.message || t('settings.failed.to.create.user')) }
}

const toggleManagementUser = async (user: ManagementUser) => {
  try {
    await authApi.setUserDisabled(user.id, !user.disabled)
    await loadManagementUsers()
  } catch (error: any) { showError(error?.response?.data?.message || t('settings.failed.to.change.user.status')) }
}

const changeManagementUserRole = async (user: ManagementUser) => {
  try {
    await authApi.setUserRole(user.id, user.role)
    await loadManagementUsers()
  } catch (error: any) {
    showError(error?.response?.data?.message || t('settings.failed.to.change.role'))
    await loadManagementUsers()
  }
}

const resetManagementUserPassword = async (user: ManagementUser) => {
  try {
    const res = await authApi.resetUserPassword(user.id)
    generatedPassword.value = res.password
  } catch { showError(t('settings.failed.to.reset.password')) }
}

const revokeManagementUserSessions = async (user: ManagementUser) => {
  try {
    await authApi.revokeUserSessions(user.id)
    showSuccess(t('settings.existing.sessions.for.this.user.have.been.revoked'))
  } catch { showError(t('settings.failed.to.revoke.sessions')) }
}

const loadConsole = async () => {
  consoleLoading.value = true
  try {
    const res = await settingsApi.getConsole() as unknown as { success: boolean; username: string; lastLoginAt: string | null; controlListen: { host: string; port: number } }
    if (res?.success) {
      consoleInfo.value = { username: res.username, lastLoginAt: res.lastLoginAt, controlListen: res.controlListen }
      await loadManagementUsers()
    }
  } catch {
    showError(t('settings.failed.to.load.console.information'))
  } finally {
    consoleLoading.value = false
  }
}

const handleChangePassword = async () => {
  if (!passwordForm.value.currentPassword || !passwordForm.value.newPassword) {
    showError(t('settings.enter.your.current.and.new.passwords'))
    return
  }
  if (passwordForm.value.newPassword.length < 6) {
    showError(t('settings.the.new.password.must.contain.at.least.6.characters'))
    return
  }
  if (passwordForm.value.newPassword !== passwordForm.value.confirmPassword) {
    showError(t('settings.the.new.passwords.do.not.match'))
    return
  }
  changingPassword.value = true
  try {
    const res = await authApi.changePassword(passwordForm.value.currentPassword, passwordForm.value.newPassword) as unknown as { success: boolean; message?: string }
    if (res?.success) {
      showSuccess(t('settings.password.changed'))
      showPasswordDialog.value = false
      passwordForm.value = { currentPassword: '', newPassword: '', confirmPassword: '' }
    } else {
      showError(res?.message || t('settings.failed.to.change.password'))
    }
  } catch {
    showError(t('settings.failed.to.change.password'))
  } finally {
    changingPassword.value = false
  }
}

// ==================== 系统日志 ====================
const logsLoading = ref(false)
const logs = ref<AuditLogEntry[]>([])
const logsTotal = ref(0)
const logsPage = ref(1)
const logsPageSize = 20

const loadLogs = async () => {
  logsLoading.value = true
  try {
    const offset = (logsPage.value - 1) * logsPageSize
    const res = await settingsApi.getAuditLogs(offset, logsPageSize) as unknown as { success: boolean; items: AuditLogEntry[]; total: number }
    if (res?.success) {
      logs.value = res.items
      logsTotal.value = res.total
    }
  } catch {
    showError(t('settings.failed.to.load.audit.logs'))
  } finally {
    logsLoading.value = false
  }
}

const totalPages = () => Math.ceil(logsTotal.value / logsPageSize) || 1

const goPage = (page: number) => {
  if (page < 1 || page > totalPages()) return
  logsPage.value = page
  loadLogs()
}

// ==================== 运行日志 ====================
const runtimeLoading = ref(false)
const runtimeLogs = ref<RuntimeLogEntry[]>([])
const runtimeTotal = ref(0)
const runtimePage = ref(1)
const runtimePageSize = 15

const loadRuntimeLogs = async () => {
  runtimeLoading.value = true
  try {
    const offset = (runtimePage.value - 1) * runtimePageSize
    const res = await settingsApi.getRuntimeLogs(offset, runtimePageSize) as unknown as { success: boolean; items: RuntimeLogEntry[]; total: number }
    if (res?.success) {
      runtimeLogs.value = res.items
      runtimeTotal.value = res.total
    }
  } catch {
    showError(t('settings.failed.to.load.runtime.logs'))
  } finally {
    runtimeLoading.value = false
  }
}

const runtimeTotalPages = () => Math.ceil(runtimeTotal.value / runtimePageSize) || 1

const goRuntimePage = (page: number) => {
  if (page < 1 || page > runtimeTotalPages()) return
  runtimePage.value = page
  loadRuntimeLogs()
}

const formatDuration = (seconds: number): string => {
  if (seconds <= 0) return '-'
  if (seconds < 60) return t('duration.seconds', { count: seconds })
  if (seconds < 3600) {
    const m = Math.floor(seconds / 60)
    const s = seconds % 60
    return t('duration.minutesSeconds', { minutes: m, seconds: s })
  }
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  if (h < 24) return t('duration.hoursMinutes', { hours: h, minutes: m })
  const d = Math.floor(h / 24)
  const rh = h % 24
  return t('duration.daysHours', { days: d, hours: rh })
}

const formatNum = (n: number): string => {
  return new Intl.NumberFormat(locale.value, { notation: n >= 10000 ? 'compact' : 'standard', maximumFractionDigits: 1 }).format(n)
}

const exitReasonBg = (reason: string): string => {
  if (!reason) return 'bg-yellow-500/15 text-yellow-400 border-yellow-500/30'
  if (reason === '正常关闭') return 'bg-green-500/15 text-green-400 border-green-500/30'
  if (reason === '异常退出') return 'bg-red-500/15 text-red-400 border-red-500/30'
  return 'bg-gray-500/15 text-gray-400 border-gray-500/30'
}

// ==================== 初始化 ====================
const switchTab = (key: 'certs' | 'console' | 'logs' | 'runtime') => {
  activeTab.value = key
  if (key === 'certs' && certs.value.length === 0) loadCerts()
  if (key === 'console' && !consoleInfo.value) loadConsole()
  if (key === 'logs' && logs.value.length === 0) loadLogs()
  if (key === 'runtime' && runtimeLogs.value.length === 0) loadRuntimeLogs()
}

onMounted(() => {
  loadCerts()
})
</script>

<template>
  <div class="space-y-4">
    <!-- 页面标题 -->
    <div class="card">
      <div class="flex flex-wrap items-center gap-3">
        <h2 class="text-lg font-semibold text-gray-100">{{ t('nav.general') }}</h2>
        <span class="text-xs text-gray-500">{{ t('settings.certificates.console.audit.logs.and.runtime.logs') }}</span>
      </div>
    </div>

    <section class="flex items-center justify-between gap-4 border-b border-dark-border px-4 py-3">
      <span class="text-sm text-gray-300">{{ t('common.language') }}</span>
      <LanguageSelect />
    </section>

    <!-- Tab 栏 -->
    <div class="card !p-0">
      <div class="flex overflow-x-auto border-b border-dark-border">
        <button
          v-for="tab in tabs" :key="tab.key"
          @click="switchTab(tab.key)"
          :class="[
            'shrink-0 px-4 md:px-6 py-3 text-sm font-medium transition-colors border-b-2 -mb-px',
            activeTab === tab.key
              ? 'border-primary-500 text-primary-400'
              : 'border-transparent text-gray-400 hover:text-gray-200 hover:border-gray-600'
          ]"
        >
          <span class="mr-1.5">{{ tab.icon }}</span>
          {{ tab.label }}
        </button>
      </div>

      <!-- ==================== Tab 1: 证书管理 ==================== -->
      <div v-if="activeTab === 'certs'" class="p-5">
        <!-- 操作栏 -->
        <div class="flex flex-wrap items-center justify-between gap-3 mb-4">
          <span class="text-sm text-gray-400">{{ t('settings.certificateCount', { count: certTotal }) }}</span>
          <div class="flex flex-wrap items-center gap-2">
            <button @click="openAcmeDialog" class="btn btn-sm btn-success flex items-center gap-1.5">
              <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
              </svg>
              {{ t('settings.request.free.certificate') }}
            </button>
            <button @click="showUploadDialog = true" class="btn btn-sm btn-primary flex items-center gap-1.5">
              <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
              </svg>
              {{ t('settings.upload.certificate') }}
            </button>
          </div>
        </div>

        <div v-if="acmeAccounts.length" class="mb-5 overflow-x-auto border-y border-dark-border">
          <table class="min-w-[640px] [&_th]:pr-4 [&_td]:pr-4 w-full text-xs">
            <thead><tr class="text-left text-gray-500"><th class="py-2 font-medium">{{ t('settings.acme.service') }}</th><th class="py-2 font-medium">{{ t('settings.account') }}</th><th class="py-2 font-medium">{{ t('settings.contact.email') }}</th></tr></thead>
            <tbody><tr v-for="account in acmeAccounts" :key="account.directoryUrl" class="border-t border-dark-border/50">
              <td class="py-2 text-gray-300 font-mono">{{ account.directoryUrl }}</td>
              <td class="py-2" :class="account.accountKeyExists ? 'text-green-400' : 'text-yellow-400'">{{ account.accountKeyExists ? t('settings.created') : t('settings.not.created') }}</td>
              <td class="py-2 text-gray-400">{{ account.email || '-' }}</td>
            </tr></tbody>
          </table>
        </div>

        <div v-if="runtimeCertificates.length" class="mb-5 overflow-x-auto border-y border-dark-border">
          <table class="min-w-[640px] [&_th]:pr-4 [&_td]:pr-4 w-full text-xs">
            <thead><tr class="text-left text-gray-500"><th class="py-2 font-medium">{{ t('settings.runtime.certificate') }}</th><th class="py-2 font-medium">{{ t('settings.source') }}</th><th class="py-2 font-medium">{{ t('settings.status') }}</th><th class="py-2 font-medium">{{ t('settings.remaining') }}</th></tr></thead>
            <tbody><tr v-for="cert in runtimeCertificates" :key="cert.domain" class="border-t border-dark-border/50">
              <td class="py-2 text-gray-300 font-mono">{{ cert.domain }}</td>
              <td class="py-2 text-gray-400">{{ cert.source }}</td>
              <td class="py-2" :class="cert.status === 'valid' ? 'text-green-400' : cert.status === 'revoked' ? 'text-red-400' : 'text-yellow-400'">{{ cert.status }}</td>
              <td class="py-2 text-gray-400">{{ t('duration.days', { count: cert.daysRemaining }) }}</td>
            </tr></tbody>
          </table>
        </div>

        <!-- 加载 -->
        <ListToolbar v-model:page="certPage" v-model:search="certSearch" :total="certTotal" :busy="certsLoading" @refresh="loadCerts" />
        <div v-if="certsLoading" class="flex items-center justify-center py-12">
          <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
        </div>

        <!-- 空状态 -->
        <div v-else-if="certs.length === 0" class="text-center py-12 text-gray-500">
          <p class="text-lg mb-2">{{ t('settings.no.certificates') }}</p>
          <p class="text-sm">{{ t('settings.no.ssl.tls.certificates.configured') }}</p>
        </div>

        <!-- 证书表格 -->
        <div v-else class="overflow-x-auto">
          <table class="min-w-[640px] [&_th]:pr-4 [&_td]:pr-4 w-full text-sm">
            <thead>
              <tr class="text-left text-gray-400 border-b border-dark-border">
                <th class="pb-3 font-medium">{{ t('settings.domain') }}</th>
                <th class="pb-3 font-medium">{{ t('settings.type') }}</th>
                <th class="pb-3 font-medium">{{ t('settings.issuer') }}</th>
                <th class="pb-3 font-medium">{{ t('settings.expires.at') }}</th>
                <th class="pb-3 font-medium">{{ t('settings.file') }}</th>
                <th class="pb-3 font-medium text-right">{{ t('settings.actions') }}</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="cert in certs" :key="cert.pemFile" class="border-b border-dark-border/50 hover:bg-white/[0.02]">
                <td class="py-3 text-gray-200 font-mono text-xs">{{ cert.domain }}</td>
                <td class="py-3">
                  <span :class="['px-2 py-0.5 rounded text-xs', certTypeColor(cert.type)]">
                    {{ certTypeLabel(cert.type) }}
                  </span>
                </td>
                <td class="py-3 text-gray-400 text-xs max-w-[200px] truncate">{{ cert.issuer || '-' }}</td>
                <td class="py-3 text-gray-400">{{ cert.notAfter || '-' }}</td>
                <td class="py-3 text-gray-500 font-mono text-xs">{{ cert.pemFile }}</td>
                <td class="py-3 text-right space-x-1">
                  <button
                    v-if="cert.type === 'acme'"
                    @click="handleRenewCert(cert)"
                    :disabled="renewing && renewingCert?.domain === cert.domain"
                    class="text-green-400 hover:text-green-300 text-xs px-2 py-1 rounded hover:bg-green-500/10 transition-colors"
                  >
                    {{ renewing && renewingCert?.domain === cert.domain ? t('settings.renewing') : t('settings.renew') }}
                  </button>
                  <button
                    @click="confirmDeleteCert(cert)"
                    class="text-red-400 hover:text-red-300 text-xs px-2 py-1 rounded hover:bg-red-500/10 transition-colors"
                  >
                    {{ t('common.delete') }}
                  </button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- ==================== Tab 2: 控制台管理 ==================== -->
      <div v-if="activeTab === 'console'" class="p-5">
        <!-- 加载 -->
        <div v-if="consoleLoading" class="flex items-center justify-center py-12">
          <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
        </div>

        <div v-else-if="consoleInfo" class="space-y-6">
          <!-- 用户信息卡片 -->
          <div>
            <div class="flex items-center justify-between mb-3">
              <h3 class="text-sm font-medium text-gray-300">{{ t('settings.user.information') }}</h3>
              <button @click="showPasswordDialog = true; passwordForm = { currentPassword: '', newPassword: '', confirmPassword: '' }" class="btn btn-sm btn-primary">
                {{ t('settings.change.password') }}
              </button>
            </div>
            <div class="grid grid-cols-1 sm:grid-cols-3 gap-4">
              <div class="bg-dark-card rounded-lg p-4 border border-dark-border">
                <div class="text-xs text-gray-500 mb-1">{{ t('login.username') }}</div>
                <div class="text-gray-200 font-medium">{{ consoleInfo.username }}</div>
              </div>
              <div class="bg-dark-card rounded-lg p-4 border border-dark-border">
                <div class="text-xs text-gray-500 mb-1">{{ t('settings.last.sign.in') }}</div>
                <div class="text-gray-200">{{ consoleInfo.lastLoginAt || t('settings.no.record') }}</div>
              </div>
              <div class="bg-dark-card rounded-lg p-4 border border-dark-border">
                <div class="text-xs text-gray-500 mb-1">{{ t('settings.console.listener') }}</div>
                <div class="text-gray-200 font-mono text-sm">{{ consoleInfo.controlListen.host }}:{{ consoleInfo.controlListen.port }}</div>
              </div>
            </div>
          </div>

          <div v-if="can('users.manage')" class="border-t border-dark-border pt-6">
            <h3 class="mb-3 text-sm font-medium text-gray-300">{{ t('settings.manage.users') }}</h3>
            <form class="flex gap-3 mb-3" @submit.prevent="userPage = 1; loadManagementUsers().catch(() => showError(t('settings.failed.to.load.users')))">
              <input v-model="userSearch" class="input" :aria-label="t('settings.search.management.users')" :placeholder="t('settings.search.username.or.role')" maxlength="200" />
              <button class="btn btn-secondary">{{ t('common.search') }}</button>
            </form>
            <div class="mb-4 grid grid-cols-1 gap-3 md:grid-cols-[1fr_1fr_150px_auto]">
              <input v-model="userForm.username" class="input" :placeholder="t('login.username')" />
              <input v-model="userForm.password" class="input" type="password" :placeholder="t('settings.initial.password.at.least.8.characters')" />
              <select v-model="userForm.role" class="input"><option v-for="role in managementRoles" :key="role.name" :value="role.name">{{ role.name }}</option></select>
              <button class="btn btn-primary" :disabled="!userForm.username || userForm.password.length < 8" @click="createManagementUser">{{ t('settings.add.user') }}</button>
            </div>
            <div v-if="generatedPassword" class="mb-4 flex items-center justify-between border border-amber-500/30 bg-amber-500/10 px-4 py-3 text-sm text-amber-300">
              <span>{{ t('settings.temporary.password.shown.once') }}<strong class="font-mono">{{ generatedPassword }}</strong></span>
              <button class="text-amber-200 hover:text-white" @click="generatedPassword = ''">{{ t('common.close') }}</button>
            </div>
            <div class="overflow-x-auto border border-dark-border">
              <table class="min-w-[640px] [&_th]:pr-4 [&_td]:pr-4 w-full text-sm">
                <thead class="bg-dark-card"><tr class="text-left text-gray-400"><th class="p-3">{{ t('settings.user') }}</th><th class="p-3">{{ t('settings.role') }}</th><th class="p-3">{{ t('settings.status') }}</th><th class="p-3">{{ t('settings.last.sign.in2') }}</th><th class="p-3 text-right">{{ t('settings.actions') }}</th></tr></thead>
                <tbody>
                  <tr v-for="user in managementUsers" :key="user.id" class="border-t border-dark-border">
                    <td class="p-3 text-gray-200">{{ user.username }}</td>
                    <td class="p-3"><select v-model="user.role" class="input py-1 text-xs" @change="changeManagementUserRole(user)"><option v-for="role in managementRoles" :key="role.name" :value="role.name">{{ role.name }}</option></select></td>
                    <td class="p-3" :class="user.disabled ? 'text-red-400' : 'text-green-400'">{{ user.disabled ? t('common.disabled') : t('settings.healthy') }}</td>
                    <td class="p-3 text-xs text-gray-400">{{ user.lastLoginAt || t('settings.never.signed.in') }}</td>
                    <td class="p-3 text-right space-x-3"><button class="text-primary-400" @click="revokeManagementUserSessions(user)">{{ t('settings.revoke.sessions') }}</button><button class="text-amber-400" @click="resetManagementUserPassword(user)">{{ t('settings.reset.password') }}</button><button :class="user.disabled ? 'text-green-400' : 'text-red-400'" @click="toggleManagementUser(user)">{{ user.disabled ? t('fields.Enabled') : t('settings.disable') }}</button></td>
                  </tr>
                </tbody>
              </table>
            </div>
            <nav class="flex justify-end items-center gap-3 mt-3 text-sm" :aria-label="t('settings.management.user.pagination')">
              <span class="text-gray-400">{{ t('common.pagination', { count: userTotal, page: userPage }) }}</span>
              <button class="btn btn-secondary" :disabled="userPage <= 1" @click="userPage--; loadManagementUsers().catch(() => showError(t('settings.failed.to.load.users')))">{{ t('common.previous') }}</button>
              <button class="btn btn-secondary" :disabled="userPage * 20 >= userTotal" @click="userPage++; loadManagementUsers().catch(() => showError(t('settings.failed.to.load.users')))">{{ t('common.next') }}</button>
            </nav>
          </div>
        </div>
      </div>

      <ManagementMfa v-if="activeTab === 'console'" />
      <section v-if="activeTab === 'console' && can('users.manage')" class="border-t border-dark-border p-5">
        <h3 class="mb-3 text-sm font-medium text-gray-300">{{ t('settings.business.roles') }}</h3>
        <form class="flex flex-wrap items-center gap-3 mb-4" @submit.prevent="saveManagementRole">
          <input v-model="roleForm.name" :aria-label="t('settings.role.name')" class="input" :placeholder="t('settings.role.name')" required minlength="3" maxlength="48" />
          <label v-for="(label, permission) in permissionLabels" :key="permission" class="flex gap-2 items-center text-sm text-gray-300">
            <input type="checkbox" v-model="roleForm.permissions" :value="permission" />{{ label }}
          </label>
          <button type="submit" class="btn btn-primary" :disabled="roleBusy">{{ t('settings.save.role') }}</button>
        </form>
        <ListToolbar v-model:page="rolePage" v-model:search="roleSearch" :total="roleTotal" :busy="roleListLoading" @refresh="loadRoles" />
        <div v-for="role in visibleRoles" :key="role.name" class="flex flex-wrap gap-3 items-center border-t border-dark-border py-3 text-sm">
          <strong class="text-gray-200">{{ role.name }}</strong>
          <span class="flex-1 text-gray-400">{{ role.permissions.map(p => permissionLabels[p] || p).join('、') }}</span>
          <template v-if="!role.builtIn">
            <button class="text-primary-400" @click="roleForm = { name: role.name, permissions: [...role.permissions] }">{{ t('common.edit') }}</button>
            <button class="text-red-400" @click="deleteManagementRole(role.name)">{{ t('common.delete') }}</button>
          </template>
          <span v-else class="text-gray-500">{{ t('settings.built.in') }}</span>
        </div>
      </section>

      <!-- ==================== Tab 3: 系统日志 ==================== -->
      <div v-if="activeTab === 'logs'" class="p-5">
        <!-- 加载 -->
        <div v-if="logsLoading" class="flex items-center justify-center py-12">
          <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
        </div>

        <!-- 空状态 -->
        <div v-else-if="logs.length === 0" class="text-center py-12 text-gray-500">
          <p class="text-lg mb-2">{{ t('settings.no.audit.logs') }}</p>
          <p class="text-sm">{{ t('settings.no.system.activity.recorded') }}</p>
        </div>

        <!-- 日志表格 -->
        <div v-else>
          <div class="overflow-x-auto">
            <table class="min-w-[640px] [&_th]:pr-4 [&_td]:pr-4 w-full text-sm">
              <thead>
                <tr class="text-left text-gray-400 border-b border-dark-border">
                  <th class="pb-3 font-medium">{{ t('login.username') }}</th>
                  <th class="pb-3 font-medium">{{ t('settings.activity') }}</th>
                  <th class="pb-3 font-medium">{{ t('settings.ip.address') }}</th>
                  <th class="pb-3 font-medium">{{ t('settings.time') }}</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="(log, idx) in logs" :key="idx" class="border-b border-dark-border/50 hover:bg-white/[0.02]">
                  <td class="py-3 text-gray-200">{{ log.username }}</td>
                  <td class="py-3 text-gray-300">{{ log.action }}</td>
                  <td class="py-3 text-gray-400 font-mono text-xs">{{ log.ip || '-' }}</td>
                  <td class="py-3 text-gray-400 text-xs">{{ new Date(log.timestamp).toLocaleString(locale) }}</td>
                </tr>
              </tbody>
            </table>
          </div>

          <!-- 分页 -->
          <div class="flex items-center justify-between mt-4 pt-4 border-t border-dark-border">
            <span class="text-xs text-gray-500">{{ t('common.total', { count: logsTotal }) }}</span>
            <div class="flex items-center gap-2">
              <button
                @click="goPage(logsPage - 1)"
                :disabled="logsPage <= 1"
                :class="['px-3 py-1 text-xs rounded transition-colors', logsPage <= 1 ? 'text-gray-600 cursor-not-allowed' : 'text-gray-400 hover:bg-white/5 hover:text-gray-200']"
              >
                {{ t('common.previous') }}
              </button>
              <span class="text-xs text-gray-400">{{ logsPage }} / {{ totalPages() }}</span>
              <button
                @click="goPage(logsPage + 1)"
                :disabled="logsPage >= totalPages()"
                :class="['px-3 py-1 text-xs rounded transition-colors', logsPage >= totalPages() ? 'text-gray-600 cursor-not-allowed' : 'text-gray-400 hover:bg-white/5 hover:text-gray-200']"
              >
                {{ t('common.next') }}
              </button>
            </div>
          </div>
        </div>
      </div>

      <!-- ==================== Tab 4: 运行日志 ==================== -->
      <div v-if="activeTab === 'runtime'" class="p-5">
        <!-- 操作栏 -->
        <div class="flex items-center justify-between mb-4">
          <span class="text-sm text-gray-400">{{ t('common.total', { count: runtimeTotal }) }}</span>
          <button @click="loadRuntimeLogs" class="btn btn-sm btn-primary flex items-center gap-1.5">
            <svg class="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
            </svg>
            {{ t('common.refresh') }}
          </button>
        </div>

        <!-- 加载 -->
        <div v-if="runtimeLoading" class="flex items-center justify-center py-12">
          <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
        </div>

        <!-- 空状态 -->
        <div v-else-if="runtimeLogs.length === 0" class="text-center py-12 text-gray-500">
          <p class="text-lg mb-2">{{ t('settings.no.runtime.records') }}</p>
          <p class="text-sm">{{ t('settings.no.process.start.or.stop.events.recorded') }}</p>
        </div>

        <!-- 运行日志表格 -->
        <div v-else>
          <div class="overflow-x-auto">
            <table class="min-w-[640px] [&_th]:pr-4 [&_td]:pr-4 w-full text-sm">
              <thead>
                <tr class="text-left text-gray-400 border-b border-dark-border">
                  <th class="pb-3 font-medium min-w-[140px]">{{ t('settings.started.at') }}</th>
                  <th class="pb-3 font-medium min-w-[140px]">{{ t('settings.stopped.at') }}</th>
                  <th class="pb-3 font-medium min-w-[80px]">{{ t('settings.status') }}</th>
                  <th class="pb-3 font-medium min-w-[100px]">{{ t('settings.uptime') }}</th>
                  <th class="pb-3 font-medium text-right min-w-[90px]">{{ t('settings.peak.memory') }}</th>
                  <th class="pb-3 font-medium text-right min-w-[80px]">{{ t('settings.requests') }}</th>
                  <th class="pb-3 font-medium text-right min-w-[80px] pr-6">{{ t('settings.blocked.requests') }}</th>
                  <th class="pb-3 font-medium min-w-[70px] pl-6">PID</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="entry in runtimeLogs" :key="entry.id" class="border-b border-dark-border/50 hover:bg-white/[0.02]">
                  <td class="py-3 text-gray-200 font-mono text-xs whitespace-nowrap">{{ new Date(entry.startTime).toLocaleString(locale) }}</td>
                  <td class="py-3 text-gray-400 font-mono text-xs whitespace-nowrap">{{ entry.stopTime ? new Date(entry.stopTime).toLocaleString(locale) : '-' }}</td>
                  <td class="py-3">
                    <span :class="['px-2 py-0.5 rounded text-xs border', exitReasonBg(entry.exitReason)]">
                      {{ translateText(entry.exitReason || t('common.running')) }}
                    </span>
                  </td>
                  <td class="py-3 text-gray-300 text-sm whitespace-nowrap">
                    {{ entry.stopTime ? formatDuration(entry.durationSeconds) : t('settings.running') }}
                  </td>
                  <td class="py-3 text-gray-400 text-sm text-right whitespace-nowrap">
                    <template v-if="entry.peakMemoryMb > 0">
                      {{ entry.peakMemoryMb.toFixed(1) }} MB
                    </template>
                    <template v-else>-</template>
                  </td>
                  <td class="py-3 text-gray-400 text-sm text-right whitespace-nowrap">
                    {{ entry.totalRequests > 0 ? formatNum(entry.totalRequests) : '-' }}
                  </td>
                  <td class="py-3 text-sm text-right whitespace-nowrap pr-6">
                    <span v-if="entry.totalIntercepts > 0" class="text-red-400">{{ formatNum(entry.totalIntercepts) }}</span>
                    <span v-else class="text-gray-500">-</span>
                  </td>
                  <td class="py-3 text-gray-500 font-mono text-xs pl-6">{{ entry.processId || '-' }}</td>
                </tr>
              </tbody>
            </table>
          </div>

          <!-- 分页 -->
          <div class="flex items-center justify-between mt-4 pt-4 border-t border-dark-border">
            <span class="text-xs text-gray-500">{{ t('common.total', { count: runtimeTotal }) }}</span>
            <div class="flex items-center gap-2">
              <button
                @click="goRuntimePage(runtimePage - 1)"
                :disabled="runtimePage <= 1"
                :class="['px-3 py-1 text-xs rounded transition-colors', runtimePage <= 1 ? 'text-gray-600 cursor-not-allowed' : 'text-gray-400 hover:bg-white/5 hover:text-gray-200']"
              >
                {{ t('common.previous') }}
              </button>
              <span class="text-xs text-gray-400">{{ runtimePage }} / {{ runtimeTotalPages() }}</span>
              <button
                @click="goRuntimePage(runtimePage + 1)"
                :disabled="runtimePage >= runtimeTotalPages()"
                :class="['px-3 py-1 text-xs rounded transition-colors', runtimePage >= runtimeTotalPages() ? 'text-gray-600 cursor-not-allowed' : 'text-gray-400 hover:bg-white/5 hover:text-gray-200']"
              >
                {{ t('common.next') }}
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- ==================== 上传证书对话框 ==================== -->
    <Teleport to="body">
      <div v-if="showUploadDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="showUploadDialog = false"></div>
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-full max-w-lg mx-4 p-6">
          <h3 class="text-lg font-semibold text-gray-100 mb-4">{{ t('settings.upload.certificate') }}</h3>

          <div class="space-y-4">
            <div>
              <label class="block text-xs text-gray-400 mb-1">{{ t('settings.domain') }}</label>
              <input v-model="uploadForm.domain" type="text" class="input w-full" :placeholder="t('settings.example.com.or.example.com')" />
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">{{ t('settings.certificate.pem') }}</label>
              <textarea
                v-model="uploadForm.pemContent"
                class="input w-full font-mono text-xs"
                rows="6"
                placeholder="-----BEGIN CERTIFICATE-----&#10;...&#10;-----END CERTIFICATE-----"
              ></textarea>
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">{{ t('settings.private.key.key') }}</label>
              <textarea
                v-model="uploadForm.keyContent"
                class="input w-full font-mono text-xs"
                rows="6"
                placeholder="-----BEGIN PRIVATE KEY-----&#10;...&#10;-----END PRIVATE KEY-----"
              ></textarea>
            </div>
          </div>

          <div class="flex items-center justify-end gap-3 mt-6">
            <button @click="showUploadDialog = false" class="btn btn-sm text-gray-400 hover:text-gray-200">{{ t('common.cancel') }}</button>
            <button
              @click="handleUploadCert"
              :disabled="uploading"
              class="btn btn-sm btn-primary"
            >
              {{ uploading ? t('settings.uploading') : t('settings.upload') }}
            </button>
          </div>
        </div>
      </div>
    </Teleport>

    <!-- ==================== 申请免费证书对话框 ==================== -->
    <Teleport to="body">
      <div v-if="showAcmeDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="!acmeApplying && (showAcmeDialog = false)"></div>
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-full max-w-lg mx-4 p-6">
          <h3 class="text-lg font-semibold text-gray-100 mb-4">{{ t('settings.request.free.certificate') }}</h3>
          <p class="text-xs text-gray-500 mb-4">
            {{ t('settings.request.a.free.ssl.certificate.from.let.s.encrypt.the.domain.must.resolve.to.this.server.and.port.80.must.be.r') }}
          </p>
          <div class="space-y-4">
            <div>
              <label class="block text-xs text-gray-400 mb-1">{{ t('settings.domain') }}</label>
              <input v-model="acmeForm.domain" type="text" class="input w-full"
                     placeholder="example.com" :disabled="acmeApplying" />
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">{{ t('settings.contact.email') }}</label>
              <input v-model="acmeForm.email" type="email" class="input w-full"
                     placeholder="admin@example.com" :disabled="acmeApplying" />
              <p class="text-xs text-gray-600 mt-1">{{ t('settings.for.let.s.encrypt.registration.and.certificate.expiry.notices') }}</p>
            </div>
          </div>
          <div v-if="acmeApplying" class="mt-4 flex items-center gap-2 text-sm text-yellow-400">
            <span class="w-4 h-4 border-2 border-yellow-400 border-t-transparent rounded-full animate-spin shrink-0"></span>
            {{ acmeStatus }}
          </div>
          <div class="flex items-center justify-end gap-3 mt-6">
            <button @click="showAcmeDialog = false" :disabled="acmeApplying" class="btn btn-sm text-gray-400 hover:text-gray-200">{{ t('common.cancel') }}</button>
            <button
              @click="handleAcmeApply"
              :disabled="acmeApplying"
              class="btn btn-sm btn-success"
            >
              {{ acmeApplying ? t('settings.requesting') : t('settings.request.certificate') }}
            </button>
          </div>
        </div>
      </div>
    </Teleport>

    <!-- ==================== 修改密码对话框 ==================== -->
    <Teleport to="body">
      <div v-if="showPasswordDialog" class="fixed inset-0 z-[100] flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="showPasswordDialog = false"></div>
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-full max-w-sm mx-4 p-6">
          <h3 class="text-lg font-semibold text-gray-100 mb-4">{{ t('settings.change.password') }}</h3>
          <div class="space-y-3">
            <div>
              <label class="block text-xs text-gray-400 mb-1">{{ t('settings.current.password') }}</label>
              <input
                v-model="passwordForm.currentPassword"
                type="password"
                class="input w-full"
                :placeholder="t('settings.enter.current.password')"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">{{ t('settings.new.password') }}</label>
              <input
                v-model="passwordForm.newPassword"
                type="password"
                class="input w-full"
                :placeholder="t('settings.at.least.6.characters')"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">{{ t('settings.confirm.new.password') }}</label>
              <input
                v-model="passwordForm.confirmPassword"
                type="password"
                class="input w-full"
                :placeholder="t('settings.re.enter.new.password')"
              />
            </div>
          </div>
          <div class="flex items-center justify-end gap-3 mt-6">
            <button @click="showPasswordDialog = false" class="btn btn-sm text-gray-400 hover:text-gray-200">{{ t('common.cancel') }}</button>
            <button
              @click="handleChangePassword"
              :disabled="changingPassword"
              class="btn btn-sm btn-primary"
            >
              {{ changingPassword ? t('settings.submitting') : t('settings.confirm.change') }}
            </button>
          </div>
        </div>
      </div>
    </Teleport>

    <!-- ==================== 删除确认对话框 ==================== -->
    <Teleport to="body">
      <div v-if="showDeleteConfirm" class="fixed inset-0 z-[100] flex items-center justify-center">
        <div class="absolute inset-0 bg-black/60" @click="showDeleteConfirm = false"></div>
        <div class="relative bg-dark-card border border-dark-border rounded-xl shadow-2xl w-full max-w-sm mx-4 p-6">
          <h3 class="text-lg font-semibold text-gray-100 mb-3">{{ t('settings.confirm.deletion') }}</h3>
          <p class="text-sm text-gray-400 mb-5">
            {{ t('settings.deleteCertificateConfirm', { domain: deletingCert?.domain || '' }) }}
          </p>
          <div class="flex items-center justify-end gap-3">
            <button @click="showDeleteConfirm = false" class="btn btn-sm text-gray-400 hover:text-gray-200">{{ t('common.cancel') }}</button>
            <button
              @click="executeDeleteCert"
              :disabled="deleting"
              class="btn btn-sm bg-red-500/20 text-red-400 hover:bg-red-500/30"
            >
              {{ deleting ? t('settings.deleting') : t('settings.confirm.deletion') }}
            </button>
          </div>
        </div>
      </div>
    </Teleport>
  </div>
</template>
