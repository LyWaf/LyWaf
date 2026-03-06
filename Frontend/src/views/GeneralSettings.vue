<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { settingsApi, authApi } from '@/api'
import { useToast } from '@/composables/useToast'
import type { CertDetail, AuditLogEntry } from '@/types'

const { showSuccess, showError } = useToast()

// ==================== Tab 切换 ====================
const activeTab = ref<'certs' | 'console' | 'logs'>('certs')
const tabs = [
  { key: 'certs' as const, label: '证书管理', icon: '🔒' },
  { key: 'console' as const, label: '控制台管理', icon: '🖥️' },
  { key: 'logs' as const, label: '系统日志', icon: '📋' },
]

// ==================== 证书管理 ====================
const certsLoading = ref(false)
const certs = ref<CertDetail[]>([])
const showUploadDialog = ref(false)
const uploadForm = ref({ domain: '', pemContent: '', keyContent: '' })
const uploading = ref(false)
const showDeleteConfirm = ref(false)
const deletingCert = ref<CertDetail | null>(null)
const deleting = ref(false)

const loadCerts = async () => {
  certsLoading.value = true
  try {
    const res = await settingsApi.getCerts() as unknown as { success: boolean; certs: CertDetail[] }
    if (res?.success) {
      certs.value = res.certs
    }
  } catch {
    showError('加载证书列表失败')
  } finally {
    certsLoading.value = false
  }
}

const handleUploadCert = async () => {
  if (!uploadForm.value.domain || !uploadForm.value.pemContent || !uploadForm.value.keyContent) {
    showError('请填写所有字段')
    return
  }
  uploading.value = true
  try {
    const res = await settingsApi.uploadCert(uploadForm.value) as unknown as { success: boolean; message?: string }
    if (res?.success) {
      showSuccess('证书上传成功，需重载配置生效')
      showUploadDialog.value = false
      uploadForm.value = { domain: '', pemContent: '', keyContent: '' }
      loadCerts()
    } else {
      showError(res?.message || '上传失败')
    }
  } catch {
    showError('上传证书失败')
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
      showSuccess('证书已删除，需重载配置生效')
      showDeleteConfirm.value = false
      deletingCert.value = null
      loadCerts()
    } else {
      showError(res?.message || '删除失败')
    }
  } catch {
    showError('删除证书失败')
  } finally {
    deleting.value = false
  }
}

const certTypeLabel = (type: string) => type === 'acme' ? '免费证书' : '上传证书'
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
    showError('请填写域名和邮箱')
    return
  }
  acmeApplying.value = true
  acmeStatus.value = '正在申请证书，请耐心等待（约30-60秒）...'
  try {
    const res = await settingsApi.acmeApply(acmeForm.value) as unknown as { success: boolean; expiresAt?: string; errorMessage?: string }
    if (res?.success) {
      showSuccess(`证书申请成功，到期时间：${res.expiresAt}`)
      showAcmeDialog.value = false
      loadCerts()
    } else {
      showError(res?.errorMessage || '证书申请失败')
    }
  } catch {
    showError('证书申请失败，请检查域名是否正确解析到本服务器')
  } finally {
    acmeApplying.value = false
    acmeStatus.value = ''
  }
}

const handleRenewCert = async (cert: CertDetail) => {
  renewingCert.value = cert
  renewing.value = true
  try {
    const res = await settingsApi.acmeRenew({ domain: cert.domain }) as unknown as { success: boolean; expiresAt?: string; errorMessage?: string }
    if (res?.success) {
      showSuccess(`证书续期成功，新到期时间：${res.expiresAt}`)
      loadCerts()
    } else {
      showError(res?.errorMessage || '证书续期失败')
    }
  } catch {
    showError('证书续期失败')
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

const loadConsole = async () => {
  consoleLoading.value = true
  try {
    const res = await settingsApi.getConsole() as unknown as { success: boolean; username: string; lastLoginAt: string | null; controlListen: { host: string; port: number } }
    if (res?.success) {
      consoleInfo.value = { username: res.username, lastLoginAt: res.lastLoginAt, controlListen: res.controlListen }
    }
  } catch {
    showError('加载控制台信息失败')
  } finally {
    consoleLoading.value = false
  }
}

const handleChangePassword = async () => {
  if (!passwordForm.value.currentPassword || !passwordForm.value.newPassword) {
    showError('请填写当前密码和新密码')
    return
  }
  if (passwordForm.value.newPassword.length < 6) {
    showError('新密码长度至少6位')
    return
  }
  if (passwordForm.value.newPassword !== passwordForm.value.confirmPassword) {
    showError('两次输入的新密码不一致')
    return
  }
  changingPassword.value = true
  try {
    const res = await authApi.changePassword(passwordForm.value.currentPassword, passwordForm.value.newPassword) as unknown as { success: boolean; message?: string }
    if (res?.success) {
      showSuccess('密码修改成功')
      showPasswordDialog.value = false
      passwordForm.value = { currentPassword: '', newPassword: '', confirmPassword: '' }
    } else {
      showError(res?.message || '修改密码失败')
    }
  } catch {
    showError('修改密码失败')
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
    showError('加载审计日志失败')
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

// ==================== 初始化 ====================
const switchTab = (key: 'certs' | 'console' | 'logs') => {
  activeTab.value = key
  if (key === 'certs' && certs.value.length === 0) loadCerts()
  if (key === 'console' && !consoleInfo.value) loadConsole()
  if (key === 'logs' && logs.value.length === 0) loadLogs()
}

onMounted(() => {
  loadCerts()
})
</script>

<template>
  <div class="space-y-4">
    <!-- 页面标题 -->
    <div class="card">
      <div class="flex items-center gap-3">
        <h2 class="text-lg font-semibold text-gray-100">通用设置</h2>
        <span class="text-xs text-gray-500">证书管理、控制台管理、系统操作日志</span>
      </div>
    </div>

    <!-- Tab 栏 -->
    <div class="card !p-0">
      <div class="flex border-b border-dark-border">
        <button
          v-for="tab in tabs" :key="tab.key"
          @click="switchTab(tab.key)"
          :class="[
            'px-6 py-3 text-sm font-medium transition-colors border-b-2 -mb-px',
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
        <div class="flex items-center justify-between mb-4">
          <span class="text-sm text-gray-400">共 {{ certs.length }} 个证书</span>
          <div class="flex items-center gap-2">
            <button @click="openAcmeDialog" class="btn btn-sm btn-success flex items-center gap-1.5">
              <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
              </svg>
              申请免费证书
            </button>
            <button @click="showUploadDialog = true" class="btn btn-sm btn-primary flex items-center gap-1.5">
              <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 6v6m0 0v6m0-6h6m-6 0H6" />
              </svg>
              上传证书
            </button>
          </div>
        </div>

        <!-- 加载 -->
        <div v-if="certsLoading" class="flex items-center justify-center py-12">
          <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
        </div>

        <!-- 空状态 -->
        <div v-else-if="certs.length === 0" class="text-center py-12 text-gray-500">
          <p class="text-lg mb-2">暂无证书</p>
          <p class="text-sm">点击「上传证书」添加 SSL/TLS 证书</p>
        </div>

        <!-- 证书表格 -->
        <div v-else class="overflow-x-auto">
          <table class="w-full text-sm">
            <thead>
              <tr class="text-left text-gray-400 border-b border-dark-border">
                <th class="pb-3 font-medium">域名</th>
                <th class="pb-3 font-medium">类型</th>
                <th class="pb-3 font-medium">颁发机构</th>
                <th class="pb-3 font-medium">到期时间</th>
                <th class="pb-3 font-medium">文件</th>
                <th class="pb-3 font-medium text-right">操作</th>
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
                    {{ renewing && renewingCert?.domain === cert.domain ? '续期中...' : '续期' }}
                  </button>
                  <button
                    @click="confirmDeleteCert(cert)"
                    class="text-red-400 hover:text-red-300 text-xs px-2 py-1 rounded hover:bg-red-500/10 transition-colors"
                  >
                    删除
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
              <h3 class="text-sm font-medium text-gray-300">用户信息</h3>
              <button @click="showPasswordDialog = true; passwordForm = { currentPassword: '', newPassword: '', confirmPassword: '' }" class="btn btn-sm btn-primary">
                修改密码
              </button>
            </div>
            <div class="grid grid-cols-1 sm:grid-cols-3 gap-4">
              <div class="bg-dark-card rounded-lg p-4 border border-dark-border">
                <div class="text-xs text-gray-500 mb-1">用户名</div>
                <div class="text-gray-200 font-medium">{{ consoleInfo.username }}</div>
              </div>
              <div class="bg-dark-card rounded-lg p-4 border border-dark-border">
                <div class="text-xs text-gray-500 mb-1">上次登录</div>
                <div class="text-gray-200">{{ consoleInfo.lastLoginAt || '无记录' }}</div>
              </div>
              <div class="bg-dark-card rounded-lg p-4 border border-dark-border">
                <div class="text-xs text-gray-500 mb-1">控制台监听</div>
                <div class="text-gray-200 font-mono text-sm">{{ consoleInfo.controlListen.host }}:{{ consoleInfo.controlListen.port }}</div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- ==================== Tab 3: 系统日志 ==================== -->
      <div v-if="activeTab === 'logs'" class="p-5">
        <!-- 加载 -->
        <div v-if="logsLoading" class="flex items-center justify-center py-12">
          <span class="w-6 h-6 border-2 border-gray-400 border-t-transparent rounded-full animate-spin"></span>
        </div>

        <!-- 空状态 -->
        <div v-else-if="logs.length === 0" class="text-center py-12 text-gray-500">
          <p class="text-lg mb-2">暂无操作日志</p>
          <p class="text-sm">系统操作日志将在此显示</p>
        </div>

        <!-- 日志表格 -->
        <div v-else>
          <div class="overflow-x-auto">
            <table class="w-full text-sm">
              <thead>
                <tr class="text-left text-gray-400 border-b border-dark-border">
                  <th class="pb-3 font-medium">用户名</th>
                  <th class="pb-3 font-medium">操作内容</th>
                  <th class="pb-3 font-medium">IP 地址</th>
                  <th class="pb-3 font-medium">时间</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="(log, idx) in logs" :key="idx" class="border-b border-dark-border/50 hover:bg-white/[0.02]">
                  <td class="py-3 text-gray-200">{{ log.username }}</td>
                  <td class="py-3 text-gray-300">{{ log.action }}</td>
                  <td class="py-3 text-gray-400 font-mono text-xs">{{ log.ip || '-' }}</td>
                  <td class="py-3 text-gray-400 text-xs">{{ log.timestamp }}</td>
                </tr>
              </tbody>
            </table>
          </div>

          <!-- 分页 -->
          <div class="flex items-center justify-between mt-4 pt-4 border-t border-dark-border">
            <span class="text-xs text-gray-500">共 {{ logsTotal }} 条记录</span>
            <div class="flex items-center gap-2">
              <button
                @click="goPage(logsPage - 1)"
                :disabled="logsPage <= 1"
                :class="['px-3 py-1 text-xs rounded transition-colors', logsPage <= 1 ? 'text-gray-600 cursor-not-allowed' : 'text-gray-400 hover:bg-white/5 hover:text-gray-200']"
              >
                上一页
              </button>
              <span class="text-xs text-gray-400">{{ logsPage }} / {{ totalPages() }}</span>
              <button
                @click="goPage(logsPage + 1)"
                :disabled="logsPage >= totalPages()"
                :class="['px-3 py-1 text-xs rounded transition-colors', logsPage >= totalPages() ? 'text-gray-600 cursor-not-allowed' : 'text-gray-400 hover:bg-white/5 hover:text-gray-200']"
              >
                下一页
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
          <h3 class="text-lg font-semibold text-gray-100 mb-4">上传证书</h3>

          <div class="space-y-4">
            <div>
              <label class="block text-xs text-gray-400 mb-1">域名</label>
              <input v-model="uploadForm.domain" type="text" class="input w-full" placeholder="example.com 或 *.example.com" />
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">证书内容 (PEM)</label>
              <textarea
                v-model="uploadForm.pemContent"
                class="input w-full font-mono text-xs"
                rows="6"
                placeholder="-----BEGIN CERTIFICATE-----&#10;...&#10;-----END CERTIFICATE-----"
              ></textarea>
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">密钥内容 (KEY)</label>
              <textarea
                v-model="uploadForm.keyContent"
                class="input w-full font-mono text-xs"
                rows="6"
                placeholder="-----BEGIN PRIVATE KEY-----&#10;...&#10;-----END PRIVATE KEY-----"
              ></textarea>
            </div>
          </div>

          <div class="flex items-center justify-end gap-3 mt-6">
            <button @click="showUploadDialog = false" class="btn btn-sm text-gray-400 hover:text-gray-200">取消</button>
            <button
              @click="handleUploadCert"
              :disabled="uploading"
              class="btn btn-sm btn-primary"
            >
              {{ uploading ? '上传中...' : '上传' }}
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
          <h3 class="text-lg font-semibold text-gray-100 mb-4">申请免费证书</h3>
          <p class="text-xs text-gray-500 mb-4">
            使用 Let's Encrypt 免费申请 SSL 证书。域名必须已解析到本服务器，且 80 端口可访问。
          </p>
          <div class="space-y-4">
            <div>
              <label class="block text-xs text-gray-400 mb-1">域名</label>
              <input v-model="acmeForm.domain" type="text" class="input w-full"
                     placeholder="example.com" :disabled="acmeApplying" />
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">联系邮箱</label>
              <input v-model="acmeForm.email" type="email" class="input w-full"
                     placeholder="admin@example.com" :disabled="acmeApplying" />
              <p class="text-xs text-gray-600 mt-1">用于 Let's Encrypt 账户注册和证书到期通知</p>
            </div>
          </div>
          <div v-if="acmeApplying" class="mt-4 flex items-center gap-2 text-sm text-yellow-400">
            <span class="w-4 h-4 border-2 border-yellow-400 border-t-transparent rounded-full animate-spin shrink-0"></span>
            {{ acmeStatus }}
          </div>
          <div class="flex items-center justify-end gap-3 mt-6">
            <button @click="showAcmeDialog = false" :disabled="acmeApplying" class="btn btn-sm text-gray-400 hover:text-gray-200">取消</button>
            <button
              @click="handleAcmeApply"
              :disabled="acmeApplying"
              class="btn btn-sm btn-success"
            >
              {{ acmeApplying ? '申请中...' : '申请证书' }}
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
          <h3 class="text-lg font-semibold text-gray-100 mb-4">修改密码</h3>
          <div class="space-y-3">
            <div>
              <label class="block text-xs text-gray-400 mb-1">当前密码</label>
              <input
                v-model="passwordForm.currentPassword"
                type="password"
                class="input w-full"
                placeholder="请输入当前密码"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">新密码</label>
              <input
                v-model="passwordForm.newPassword"
                type="password"
                class="input w-full"
                placeholder="至少6位"
              />
            </div>
            <div>
              <label class="block text-xs text-gray-400 mb-1">确认新密码</label>
              <input
                v-model="passwordForm.confirmPassword"
                type="password"
                class="input w-full"
                placeholder="再次输入新密码"
              />
            </div>
          </div>
          <div class="flex items-center justify-end gap-3 mt-6">
            <button @click="showPasswordDialog = false" class="btn btn-sm text-gray-400 hover:text-gray-200">取消</button>
            <button
              @click="handleChangePassword"
              :disabled="changingPassword"
              class="btn btn-sm btn-primary"
            >
              {{ changingPassword ? '提交中...' : '确认修改' }}
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
          <h3 class="text-lg font-semibold text-gray-100 mb-3">确认删除</h3>
          <p class="text-sm text-gray-400 mb-5">
            确定要删除域名 <span class="text-gray-200 font-mono">{{ deletingCert?.domain }}</span> 的证书吗？此操作不可撤销。
          </p>
          <div class="flex items-center justify-end gap-3">
            <button @click="showDeleteConfirm = false" class="btn btn-sm text-gray-400 hover:text-gray-200">取消</button>
            <button
              @click="executeDeleteCert"
              :disabled="deleting"
              class="btn btn-sm bg-red-500/20 text-red-400 hover:bg-red-500/30"
            >
              {{ deleting ? '删除中...' : '确认删除' }}
            </button>
          </div>
        </div>
      </div>
    </Teleport>
  </div>
</template>
