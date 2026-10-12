import axios from 'axios'
import { locale } from '@/i18n'
import type {
  ApiResponse,
  TrafficStats,
  SecurityStats,
  ApiTimingStat,
  ApiTimingSummary,
  GeoTrafficStats,
  OverviewData
} from '@/types'

const http = axios.create({
  baseURL: '/api/v1',
  timeout: 30000,
  headers: {
    'Content-Type': 'application/json',
  },
})

// 请求拦截器 - 注入 JWT Token
http.interceptors.request.use((config) => {
  config.headers['Accept-Language'] = locale.value
  if (config.method && ['post', 'put', 'patch', 'delete'].includes(config.method.toLowerCase()) && !config.headers['Idempotency-Key']) {
    config.headers['Idempotency-Key'] = Array.from(crypto.getRandomValues(new Uint8Array(16)), n => n.toString(16).padStart(2, '0')).join('')
  }
  const token = localStorage.getItem('lywaf_token')
  if (token) {
    config.headers.Authorization = `Bearer ${token}`
  }
  return config
})

// 响应拦截器 - 直接返回 data，401 自动跳转登录
http.interceptors.response.use(
  (response) => response.data,
  async (error) => {
    if (error.response?.status === 401) {
      localStorage.removeItem('lywaf_token')
      localStorage.removeItem('lywaf_username')
      if (!window.location.hash.includes('/login')) {
        window.location.hash = '#/login'
      }
      return Promise.reject(error)
    }
    // Preserve the rejected HTTP status; resolving an error body can report failed writes as successful.
    const message = error.response?.data?.error?.message || error.response?.data?.message || error.response?.data?.errorMessage
    if (typeof message === 'string') error.message = message
    return Promise.reject(error)
  }
)

// 定义已解包的请求方法类型
type UnwrappedAxios = {
  get: <T>(url: string, config?: object) => Promise<T>
  post: <T>(url: string, data?: object, config?: object) => Promise<T>
  put: <T>(url: string, data?: object, config?: object) => Promise<T>
  delete: <T>(url: string, config?: object) => Promise<T>
}

const api = http as unknown as UnwrappedAxios

export interface ManagementPage<T> { success: boolean; data: T[]; total: number; page: number; pageSize: number }

// Configuration selectors require the complete set; normal tables should request one page.
export async function getAllManagementPages<T>(url: string, client: Pick<UnwrappedAxios, 'get'> = api): Promise<T[]> {
  const items: T[] = []
  for (let page = 1; page <= 100; page++) {
    const result = await client.get<ManagementPage<T>>(url, { params: { page, pageSize: 100 } })
    if (!result.success) throw new Error('读取列表失败')
    items.push(...result.data)
    if (items.length >= result.total) return items
    if (!result.data.length) throw new Error('列表发生变化，请刷新')
  }
  throw new Error('列表超过加载上限')
}

export interface ManagementJob {
  id: string; kind: string; operator: string; state: number; createdAt: string;
  startedAt?: string; finishedAt?: string; logs: string[]; error?: string;
  cancellationRequested: boolean; canRetry: boolean; retryOf?: string;
}
export const managementTaskApi = {
  detail: (id: string) => api.get<{ success: boolean; data: ManagementJob }>(`/tasks/${encodeURIComponent(id)}`),
  health: () => api.get<{ success: boolean; data: any }>('/system/health'),
  list: (page = 1, state = '') => api.get<{ success: boolean; data: ManagementJob[]; total: number }>('/tasks', { params: { page, pageSize: 20, state: state || undefined } }),
  action: (id: string, action: 'cancel' | 'retry') => api.post<{ success: boolean; message?: string }>(`/tasks/${id}/${action}`),
  downloadRules: (url: string) => api.post<{ success: boolean; message?: string }>('/rule-packages/download-task', { url }),
  certificate: (domain: string, email: string) => api.post<{ success: boolean; message?: string }>('/tasks/certificate', { domain, email }),
  stageUpgrade: (packageName: string, version: string, sha256: string) => api.post<{ success: boolean; message?: string }>('/tasks/upgrade-stage', { packageName, version, sha256 }),
}

export const siteApi = {
  health: (id: string) => api.get<any>(`/lb/clusters/${encodeURIComponent(id)}/health-check`),
  saveHealth: (id: string, data: object) => api.put<any>(`/lb/clusters/${encodeURIComponent(id)}/health-check`, data),
  list: (page = 1, search = '') => api.get<any>('/sites', { params: { page, pageSize: 20, search } }),
  save: (site: any, version: string) => api.post<any>('/sites', site, { headers: { 'If-Match': version } }),
  batch: (ids: string[], action: string, ruleIds?: string[] | null) => api.post<any>('/sites/batch', { ids, action, ruleIds }),
  export: () => api.get<any>('/sites/export'),
  import: (document: object) => api.post<any>('/sites/import', document),
}

export const backupApi = {
  list: (page = 1, search = '') => api.get<any>('/backups', { params: { page, pageSize: 20, search } }),
  create: (scope = 'configuration') => api.post<any>('/backups', { scope }),
  cancelPending: () => api.delete<any>('/backups/statistics-pending'),
  cancelInstancePending: () => api.delete<any>('/backups/instance-pending'),
  restore: (id: string, action: 'preflight' | 'restore') => api.post<any>(`/backups/${id}/${action}`, {}),
  remove: (id: string) => api.delete<any>(`/backups/${id}`),
  download: (id: string) => api.get<Blob>(`/backups/${id}/download`, { responseType: 'blob' }),
  upload: (file: File, scope = 'configuration') => api.post<any>('/backups/upload', file, { params: { scope }, headers: { 'Content-Type': 'application/octet-stream' } }),
}

export const securityIncidentApi = {
  list: (params: object) => api.get<any>('/security-incidents', { params }),
  detail: (id: string) => api.get<any>(`/security-incidents/${id}`),
  requests: (id: string, cursor?: string | null) => api.get<any>(`/security-incidents/${id}/requests`, { params: { cursor: cursor || undefined, pageSize: 20 } }),
  disposition: (id: string, state: number, note: string, expectedVersion: number) => api.put<any>(`/security-incidents/${id}/disposition`, { state, note, expectedVersion }),
  action: (id: string, action: string) => api.post<any>(`/security-incidents/${id}/${action}`),
}

export const notificationApi = {
  channels: (page = 1, search = '') => api.get<ManagementPage<any>>('/notifications/channels', { params: { page, pageSize: 20, search } }),
  deliveries: (page = 1, search = '') => api.get<ManagementPage<any>>('/notifications/deliveries', { params: { page, pageSize: 20, search } }),
  save: (channel: any) => api.post<any>('/notifications/channels', channel),
  remove: (id: string) => api.delete<any>(`/notifications/channels/${encodeURIComponent(id)}`),
  retry: (id: string) => api.post<any>(`/notifications/deliveries/${encodeURIComponent(id)}/retry`),
}

// 插件前端只能通过统一代理访问自身后端，不在核心 API 中声明业务路由。
export const pluginProxyApi = (pluginId: string) => {
  const prefix = `/plugins/${encodeURIComponent(pluginId)}/proxy`
  return {
    get: <T>(path: string, config?: object) => api.get<T>(`${prefix}/${path}`, config),
    post: <T>(path: string, data?: object, config?: object) => api.post<T>(`${prefix}/${path}`, data, config),
    put: <T>(path: string, data?: object, config?: object) => api.put<T>(`${prefix}/${path}`, data, config),
    delete: <T>(path: string, config?: object) => api.delete<T>(`${prefix}/${path}`, config),
  }
}

// ==================== 功能开关 API ====================

export const featureApi = {
  toggleIpControl: (enabled: boolean) =>
    api.post<ApiResponse>('/feature/ip-control/toggle', { enabled }),
    
  toggleGeoControl: (enabled: boolean) =>
    api.post<ApiResponse>('/feature/geo-control/toggle', { enabled }),
    
  toggleWafArgs: (enabled: boolean) =>
    api.post<ApiResponse>('/feature/waf-args/toggle', { enabled }),
    
  toggleWafPost: (enabled: boolean) =>
    api.post<ApiResponse>('/feature/waf-post/toggle', { enabled }),
}

// ==================== IP 请求日志类型 ====================

export interface IpLogEntry {
  index: number
  time: string
  method: string
  url: string
  host: string
  requestLine: string
  headers?: string
  requestBody?: string
  responseBody?: string
  statusCode?: number
  duration?: string
  clientIp?: string
  scheme?: string
  raw: string
}

export interface IpGroup {
  id: string
  name: string
  description: string
  entries: string[]
  whitelistEnabled: boolean
  blacklistEnabled: boolean
  createdAt: string
  updatedAt: string
}

// ==================== IP 管理 API ====================

export const ipApi = {
  listAccess: (list: 'whitelist' | 'blacklist', page = 1, search = '') =>
    api.get<ManagementPage<{ id: string; name: string; ip: string | null; group: IpGroup | null }>>(`/ac/${list}/entries`, { params: { page, pageSize: 20, search } }),
  listGroups: (page = 1, pageSize = 20, search = '') =>
    api.get<{ success: boolean; data: IpGroup[]; total: number; page: number; pageSize: number }>('/ip-groups', { params: { page, pageSize, search } }),

  getGroups: async () => ({ success: true, groups: await getAllManagementPages<IpGroup>('/ip-groups') }),

  createGroup: (data: { name: string; description: string; entries: string[] }) =>
    api.post<{ success: boolean; group?: IpGroup; message?: string }>('/ip-groups', data),

  updateGroup: (id: string, data: { name: string; description: string; entries: string[] }) =>
    api.put<{ success: boolean; group?: IpGroup; message?: string }>(`/ip-groups/${encodeURIComponent(id)}`, data),

  deleteGroup: (id: string) =>
    api.delete<{ success: boolean; message?: string }>(`/ip-groups/${encodeURIComponent(id)}`),

  setGroupUsage: (id: string, whitelistEnabled: boolean, blacklistEnabled: boolean) =>
    api.put<{ success: boolean; group?: IpGroup; message?: string }>(
      `/ip-groups/${encodeURIComponent(id)}/usage`,
      { whitelistEnabled, blacklistEnabled }
    ),

  // 白名单
  getWhitelist: () =>
    getAllManagementPages<string>('/ac/whitelist').then(whitelist => ({ success: true, whitelist })),

  addWhitelist: (ipOrCidr: string) =>
    api.post<ApiResponse>('/ac/whitelist/add', { ipOrCidr }),

  removeWhitelist: (ipOrCidr: string) =>
    api.post<ApiResponse>('/ac/whitelist/remove', { ipOrCidr }),

  // 黑名单
  getBlacklist: () =>
    getAllManagementPages<string>('/ac/blacklist').then(blacklist => ({ success: true, blacklist })),

  addBlacklist: (ipOrCidr: string) =>
    api.post<ApiResponse>('/ac/blacklist/add', { ipOrCidr }),

  removeBlacklist: (ipOrCidr: string) =>
    api.post<ApiResponse>('/ac/blacklist/remove', { ipOrCidr }),
    
  // 封禁管理 (duration 为 TimeSpan 格式，如 "01:30:00" 表示1小时30分钟)
  blockIp: (ip: string, reason?: string, durationSeconds?: number, type?: string, speedLimit?: number) => {
    let duration: string | undefined
    if (durationSeconds) {
      const h = Math.floor(durationSeconds / 3600)
      const m = Math.floor((durationSeconds % 3600) / 60)
      const s = durationSeconds % 60
      duration = `${h.toString().padStart(2, '0')}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`
    }
    return api.post<ApiResponse>('/blocked-ips/add', { ip, reason, duration, type, speedLimit })
  },
    
  unblockIp: (ip: string) =>
    api.post<ApiResponse>('/blocked-ips/remove', { ip }),
    
  clearBlockedIps: () =>
    api.post<ApiResponse>('/blocked-ips/clear'),

  // IP 请求日志
  addIpLog: (ip: string, durationSeconds?: number) => {
    let duration: string | undefined
    if (durationSeconds && durationSeconds > 0) {
      const h = Math.floor(durationSeconds / 3600)
      const m = Math.floor((durationSeconds % 3600) / 60)
      const s = durationSeconds % 60
      duration = `${h.toString().padStart(2, '0')}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`
    }
    return api.post<ApiResponse>('/ip-log/add', { ip, duration })
  },

  removeIpLog: (ip: string) =>
    api.post<ApiResponse>('/ip-log/remove', { ip }),

  readIpLog: async (ip: string, offset = 0, limit = 50) => {
    const response = await api.get<ManagementPage<IpLogEntry> & { fileSize: number }>(`/ip-log/read/${encodeURIComponent(ip)}`, {
      params: { page: Math.floor(offset / limit) + 1, pageSize: limit }
    })
    return { ...response, entries: response.data, ip, offset, limit }
  },

  deleteIpLogFile: (ip: string) =>
    api.post<ApiResponse>('/ip-log/delete-file', { ip }),
}

// ==================== 地理访问 API ====================

export const geoApi = {
  addDenyCountry: (country: string) =>
    api.post<ApiResponse>('/geo/deny-countries/add', { country }),
    
  removeDenyCountry: (country: string) =>
    api.post<ApiResponse>('/geo/deny-countries/remove', { country }),
    
  addDenyRegion: (region: string) =>
    api.post<ApiResponse>('/geo/deny-regions/add', { region }),
    
  removeDenyRegion: (region: string) =>
    api.post<ApiResponse>('/geo/deny-regions/remove', { region }),
    
  addAllowCountry: (country: string) =>
    api.post<ApiResponse>('/geo/allow-countries/add', { country }),
    
  removeAllowCountry: (country: string) =>
    api.post<ApiResponse>('/geo/allow-countries/remove', { country }),
    
  addAllowRegion: (region: string) =>
    api.post<ApiResponse>('/geo/allow-regions/add', { region }),
    
  removeAllowRegion: (region: string) =>
    api.post<ApiResponse>('/geo/allow-regions/remove', { region }),
}

// ==================== WAF 规则 API ====================

export const wafApi = {
  addArgsRule: (pattern: string) =>
    api.post<ApiResponse>('/waf/args/add', { pattern }),
    
  removeArgsRule: (pattern: string) =>
    api.post<ApiResponse>('/waf/args/remove', { pattern }),
    
  addPostRule: (pattern: string) =>
    api.post<ApiResponse>('/waf/post/add', { pattern }),
    
  removePostRule: (pattern: string) =>
    api.post<ApiResponse>('/waf/post/remove', { pattern }),
}


// ==================== 流量统计 API ====================

export const trafficApi = {
  getStats: () =>
    api.get<TrafficStats>('/traffic/stats'),
    
  reset: () =>
    api.post<ApiResponse>('/traffic/reset'),
}

// ==================== 安全统计 API ====================

// 后端返回的原始安全统计格式
interface SecurityRawResponse {
  snapshot: {
    startTime: string
    wafInterceptCount: number
    blacklistBlockCount: number
    ccAttackCount: number
    crawlerDetectCount: number
    geoBlockCount: number
    rateLimitCount: number
    totalInterceptCount: number
    uniqueAttackIps: number
  }
  timeSlots: Array<{
    time: string
    wafIntercept: number
    blacklistBlock: number
    ccAttack: number
    crawlerDetect: number
    geoBlock: number
    rateLimit: number
    total: number
  }>
  topAttackSources: Array<{ ip: string; count: number }>
  topWafSources: Array<{ ip: string; count: number }>
  topCcSources: Array<{ ip: string; count: number }>
  topBlacklistSources: Array<{ ip: string; count: number }>
  topGeoSources: Array<{ ip: string; count: number }>
  topCrawlerSources: Array<{ ip: string; count: number }>
}

// 时间槽中事件类型到字段的映射
const timeSlotFieldMap: Record<string, keyof SecurityRawResponse['timeSlots'][0]> = {
  WafIntercept: 'wafIntercept',
  CcAttack: 'ccAttack',
  BlacklistBlock: 'blacklistBlock',
  GeoBlock: 'geoBlock',
  CrawlerDetect: 'crawlerDetect',
  RateLimit: 'rateLimit',
}

// Top 来源字段映射
const topSourcesFieldMap: Record<string, keyof SecurityRawResponse> = {
  WafIntercept: 'topWafSources',
  CcAttack: 'topCcSources',
  BlacklistBlock: 'topBlacklistSources',
  GeoBlock: 'topGeoSources',
  CrawlerDetect: 'topCrawlerSources',
}

function transformSecurityStats(raw: SecurityRawResponse): SecurityStats {
  const s = raw.snapshot

  // 将 timeSlots 按事件类型分组为 history
  const history: Record<string, Array<{ time: string; count: number }>> = {}
  for (const [eventType, field] of Object.entries(timeSlotFieldMap)) {
    history[eventType] = (raw.timeSlots || []).map(slot => ({
      time: slot.time,
      count: (slot[field] as number) || 0,
    }))
  }

  // 将各类型 Top 来源合并到 topAttackSources
  const topAttackSources: Record<string, Array<{ ip: string; count: number }>> = {}
  for (const [eventType, field] of Object.entries(topSourcesFieldMap)) {
    topAttackSources[eventType] = (raw[field] as Array<{ ip: string; count: number }>) || []
  }

  return {
    wafIntercepts: s?.wafInterceptCount || 0,
    ccAttacks: s?.ccAttackCount || 0,
    blacklistBlocks: s?.blacklistBlockCount || 0,
    geoBlocks: s?.geoBlockCount || 0,
    crawlerDetects: s?.crawlerDetectCount || 0,
    rateLimits: s?.rateLimitCount || 0,
    totalEvents: s?.totalInterceptCount || 0,
    history,
    topAttackSources,
  }
}

export const securityApi = {
  getStats: async (hours = 24): Promise<SecurityStats> => {
    const raw = await api.get<SecurityRawResponse>('/security/stats', { params: { hours } })
    return transformSecurityStats(raw)
  },

  reset: () =>
    api.post<ApiResponse>('/security/reset'),

  getQpsHistory: (granularity: string = '5s', from?: string, to?: string) => {
    const params: Record<string, string> = { granularity }
    if (from) params.from = from
    if (to) params.to = to
    return api.get<{ success: boolean; data: Array<{ time: string; qps: number; requestCount: number }>; granularity: string }>('/qps/history', { params })
  },

  getBandwidthHistory: (granularity: string = '5s', from?: string, to?: string) => {
    const params: Record<string, string> = { granularity }
    if (from) params.from = from
    if (to) params.to = to
    return api.get<{ success: boolean; data: Array<{ time: string; inboundBytes: number; outboundBytes: number; inboundRate: number; outboundRate: number }> }>('/bandwidth/history', { params })
  },
}

// ==================== 地理位置流量 API ====================

export const geoTrafficApi = {
  getStats: () =>
    api.get<{ success: boolean } & GeoTrafficStats>('/geo-traffic/stats'),
}

// ==================== API 耗时统计 ====================

// 后端返回的原始格式
interface TimingRawResponse {
  success: boolean
  total: number
  data: Array<{
    path: string
    method: string
    backend: string
    originalHost: string
    requestCount: number
    avgTotalTime: number
    avgBackendTime: number
    avgGatewayTime: number
    minTotalTime: number
    maxTotalTime: number
    minBackendTime: number
    maxBackendTime: number
    totalTime: number
    backendTime: number
    statusCodeCounts: Record<string, number>
    lastRequestTime: string
    errorRate: number
  }>
  backendStats: Array<{
    backend: string
    apiCount: number
    totalRequests: number
    avgTotalTime: number
    avgBackendTime: number
    errorCount: number
    errorRate: number
  }>
  summary: ApiTimingSummary
}

export interface TimingListResponse {
  summary: ApiTimingSummary
  items: ApiTimingStat[]
  total: number
  backends: TimingRawResponse['backendStats']
}

export const timingApi = {
  getList: async (params: Record<string, unknown> = {}): Promise<TimingListResponse> => {
    const raw = await api.get<TimingRawResponse>('/timing/list', { params: { page: 1, pageSize: 20, ...params } })
    
    // 转换数据格式
    const items: ApiTimingStat[] = (raw.data || []).map(item => ({
      path: item.path,
      method: item.method,
      backend: item.backend,
      originalHost: item.originalHost || '',
      requestCount: item.requestCount,
      avgTotalTime: item.avgTotalTime,
      avgBackendTime: item.avgBackendTime,
      avgGatewayTime: item.avgGatewayTime,
      minTotalTime: item.minTotalTime,
      maxTotalTime: item.maxTotalTime,
      minBackendTime: item.minBackendTime,
      maxBackendTime: item.maxBackendTime,
      lastRequestTime: item.lastRequestTime,
      statusCodes: item.statusCodeCounts,
      errorRate: item.errorRate,
    }))
    
    return {
      summary: raw.summary,
      items,
      total: raw.total,
      backends: raw.backendStats,
    }
  },
    
  clear: () =>
    api.post<ApiResponse>('/timing/clear'),
}

// ==================== A/B 测试 API ====================

export const abTestApi = {
  toggle: (id: string, enabled: boolean) =>
    api.post<ApiResponse>(`/abtest/configs/${id}/toggle`, { enabled }),
    
  getStats: (id: string) =>
    api.get<ApiResponse>(`/abtest/stats/${id}`),
}

// ==================== 带宽限速 API ====================

export const throttleApi = {
  getConfig: () =>
    api.get<{ success: boolean; enabled: boolean; global: number; pathLimits: Record<string, number>; ipLimits: Record<string, number> }>('/throttle/config'),

  toggle: () =>
    api.post<ApiResponse & { enabled: boolean }>('/throttle/toggle'),

  setGlobal: (limitKbps: number) =>
    api.post<ApiResponse>('/throttle/global', { limitKbps }),

  addIpThrottle: (ip: string, limitKbps: number) =>
    api.post<ApiResponse>('/throttle/ip/add', { ip, limitKbps }),

  removeIpThrottle: (ip: string) =>
    api.post<ApiResponse>('/throttle/ip/remove', { ip }),

  addPathThrottle: (path: string, limitKbps: number) =>
    api.post<ApiResponse>('/throttle/path/add', { path, limitKbps }),

  removePathThrottle: (path: string) =>
    api.post<ApiResponse>('/throttle/path/remove', { path }),

  savePatch: () =>
    api.post<ApiResponse>('/throttle/patch/save'),

  removePatch: () =>
    api.post<ApiResponse>('/throttle/patch/remove'),
}

// ==================== 连接限制 API ====================

export const connectionLimitApi = {
  getConfig: () =>
    api.get<any>('/connection-limit/config'),

  toggle: (enabled?: boolean) =>
    api.post<any>('/connection-limit/toggle', { enabled }),

  update: (config: { maxConnectionsPerIp?: number; maxConnectionsPerDestination?: number; maxTotalConnections?: number; rejectStatusCode?: number }) =>
    api.post<any>('/connection-limit/update', config),

  addPathLimit: (path: string, maxConnections: number) =>
    api.post<any>('/connection-limit/path/add', { path, maxConnections }),

  removePathLimit: (path: string) =>
    api.post<any>('/connection-limit/path/remove', { path }),
}

// ==================== 配置文件 API ====================

interface ConfigFileResponse {
  success: boolean
  fileName: string
  format: string
  content: string
  draftLoaded: boolean | null
  draftContent: string | null
  hasDraft: boolean
}

interface ConvertResponse {
  success: boolean
  yaml: string
  message?: string
}

export interface ConfigVersionInfo {
  id: string
  createdAt: string
  operator: string
  kind: string
  targetPath: string
  sha256: string
  previousVersionId: string | null
  action: string
}

export interface ConfigDiffLine {
  kind: 'equal' | 'add' | 'remove'
  oldLine: number | null
  newLine: number | null
  text: string
}

export interface ConfigVersionDiff {
  version: ConfigVersionInfo
  previousVersion: ConfigVersionInfo | null
  lines: ConfigDiffLine[]
  truncated: boolean
}

export const configFileApi = {
  getFile: () =>
    api.get<ConfigFileResponse>('/config/file'),

  saveFile: (content: string, reload: boolean = false) =>
    api.post<ApiResponse & { data?: { id: string }; portsChanged?: boolean; restartRequired?: boolean }>('/config/file', { content, reload }),

  convert: (content: string) =>
    api.post<ConvertResponse>('/config/convert', { content }),

  getHistory: (page = 1, search = '') =>
    api.get<ManagementPage<ConfigVersionInfo>>('/config/history', { params: { page, pageSize: 20, search } }),
  getAllHistory: () => getAllManagementPages<ConfigVersionInfo>('/config/history'),

  getVersionDiff: (id: string) =>
    api.get<{ success: boolean; diff: ConfigVersionDiff }>(`/config/history/${encodeURIComponent(id)}/diff`),

  rollback: (id: string) =>
    api.post<ApiResponse & { data: { id: string } }>(`/config/history/${encodeURIComponent(id)}/rollback`),
}

export interface UpgradeReleaseInfo {
  version: string
  directory: string
  entryPoint: string
  sha256: string
  stagedAt: string
  status: string
  validationOutput: string
}

export interface UpgradePointers {
  current: string | null
  previous: string | null
  pending: string | null
}

export const upgradeApi = {
  list: (page = 1, search = '') => api.get<ManagementPage<UpgradeReleaseInfo>>('/upgrade/releases', { params: { page, pageSize: 20, search } }),
  pointers: () => api.get<UpgradePointers>('/upgrade/pointers'),
  stage: (packageName: string, version: string, sha256: string) =>
    api.post<{ success: boolean; data: { id: string } }>('/upgrade/stage', { packageName, version, sha256 }),
  activate: (version: string) => api.post<{ success: boolean; data: { id: string } }>('/upgrade/activate', { version }),
  rollback: () => api.post<{ success: boolean; data: { id: string } }>('/upgrade/rollback'),
}

// ==================== 参数配置 API ====================

export const paramApi = {
  get: () =>
    api.get<{ success: boolean; isFirstServer: boolean }>('/param'),

  update: (data: { isFirstServer?: boolean }) =>
    api.post<{ success: boolean; isFirstServer: boolean; message: string }>('/param', data),

  getEnv: (page = 1, search = '') =>
    api.get<ManagementPage<{ key: string; value: string }>>('/param/env', { params: { page, pageSize: 20, search } }),
}

// ==================== 补丁 API ====================

export const patchApi = {
  /** 清除所有补丁 */
  clearAll: () =>
    api.post<ApiResponse>('/patch/clear', {}),
}

// ==================== 错误模板 API ====================

export interface ErrorTemplateItem {
  statusCode: number
  hasOriginal: boolean
  hasEdit: boolean
}

export interface ErrorTemplateDetail {
  success: boolean
  statusCode: number
  editContent: string | null
  originalContent: string | null
  hasEdit: boolean
  hasOriginal: boolean
  activeContent: string
}

export const errorTemplateApi = {
  list: () =>
    getAllManagementPages<ErrorTemplateItem>('/error-templates').then(templates => ({ success: true, templates })),

  get: (statusCode: number) =>
    api.get<ErrorTemplateDetail>(`/error-templates/${statusCode}`),

  save: (statusCode: number, content: string) =>
    api.post<ApiResponse>(`/error-templates/${statusCode}`, { content }),

  revert: (statusCode: number) =>
    api.post<ApiResponse>(`/error-templates/${statusCode}/revert`),
}

// ==================== WAF 自定义规则 API ====================

export const wafRuleApi = {
  list: (params: Record<string, unknown> = {}) => api.get('/waf-rules', { params }),
  get: (id: string) => api.get(`/waf-rules/${id}`),
  create: (rule: Record<string, unknown>) => api.post('/waf-rules', rule),
  update: (id: string, rule: Record<string, unknown>) => api.put(`/waf-rules/${id}`, rule),
  delete: (id: string) => api.delete(`/waf-rules/${id}`),
  toggle: (id: string) => api.post(`/waf-rules/${id}/toggle`),
  enums: () => api.get('/waf-rules/enums'),
}

// ==================== 配置概览 API ====================

export const overviewApi = {
  getData: () =>
    api.get<OverviewData>('/overview'),
}

export const listenApi = {
  add: (data: { host: string; port: number; isHttps: boolean; autoHttpsPort?: number }) =>
    api.post<ApiResponse & { restartRequired?: boolean }>('/listen/add', data),
  remove: (data: { host: string; port: number }) =>
    api.post<ApiResponse & { restartRequired?: boolean }>('/listen/remove', data),
}

// ==================== 服务管理 API ====================

export const serviceApi = {
  reload: () =>
    api.post<ApiResponse & { portsChanged?: boolean }>('/reload'),

  restart: () =>
    api.post<ApiResponse>('/restart'),

  stop: () =>
    api.post<ApiResponse>('/stop'),
}

// ==================== 集群配置 API ====================

import type { LbCluster, LbDestination, LbPolicy, OutlierDestination } from '@/types'
import type { MultiNodeDashboard, ConfigurationDeployment } from '@/types'

interface LbPoliciesResponse {
  success: boolean
  policies: LbPolicy[]
}

export const lbApi = {
  createCluster: (data: { id: string; policy: string; address: string }) => api.post<ApiResponse>('/lb/clusters', data),
  getClusterOptions: () => getAllManagementPages<Omit<LbCluster, 'destinations'> & { hasPatch: boolean }>('/lb/cluster-options'),
  destinations: (clusterId: string, page = 1, search = '') => api.get<ManagementPage<LbDestination & { outlier?: OutlierDestination }>>(
    `/lb/clusters/${encodeURIComponent(clusterId)}/destinations`, { params: { page, pageSize: 20, search } }),
  getClusters: async () => ({ success: true, clusters: await getAllManagementPages<LbCluster>('/lb/clusters') }),

  getPolicies: () =>
    api.get<LbPoliciesResponse>('/lb/policies'),

  getOutliers: async () => ({ success: true, destinations: await getAllManagementPages<OutlierDestination>('/lb/outliers') }),

  updatePolicy: (clusterId: string, policy: string) =>
    api.post<ApiResponse>('/lb/policy/update', { clusterId, policy }),

  addDestination: (data: {
    clusterId: string
    destinationId: string
    address: string
    metadata?: Record<string, string>
  }) => api.post<ApiResponse>('/lb/destinations/add', data),

  updateDestination: (data: {
    clusterId: string
    destinationId: string
    address?: string
    metadata?: Record<string, string>
  }) => api.post<ApiResponse>('/lb/destinations/update', data),

  removeDestination: (clusterId: string, destinationId: string) =>
    api.post<ApiResponse>('/lb/destinations/remove', { clusterId, destinationId }),

  batchRemoveDestinations: (clusterId: string, destinationIds: string[]) =>
    api.post<ApiResponse & { allSucceeded: boolean; succeeded: number; failed: number; results: Array<{ id: string; success: boolean; message?: string }> }>('/lb/destinations/batch-remove', { clusterId, destinationIds }),

  removeClusterPatch: (clusterId: string) =>
    api.post<ApiResponse>('/lb/patch/remove', { clusterId }),
}

// ==================== 路由配置 API ====================

import type { RouteConfig } from '@/types'

interface RoutesResponse {
  success: boolean
  routes: RouteConfig[]
}

export const routeApi = {
  getRoutes: async (): Promise<RoutesResponse> => ({ success: true, routes: await getAllManagementPages<RouteConfig>('/routes') }),
  list: (page = 1, search = '') => api.get<ManagementPage<RouteConfig>>('/routes', { params: { page, pageSize: 20, search } }),

  addRoute: (data: {
    routeId: string
    clusterId?: string
    order?: number
    match?: {
      path?: string
      hosts?: string[]
      methods?: string[]
    }
  }) => api.post<ApiResponse>('/routes/add', data),

  updateRoute: (data: {
    routeId: string
    clusterId?: string
    order?: number
    match?: {
      path?: string
      hosts?: string[]
      methods?: string[]
    }
  }) => api.post<ApiResponse>('/routes/update', data),

  removeRoute: (routeId: string) =>
    api.post<ApiResponse>('/routes/remove', { routeId }),

  removeRoutePatch: (routeId: string) =>
    api.post<ApiResponse>('/routes/patch/remove', { routeId }),
}

// ==================== 文件服务配置 API ====================

import type { FileServerItem } from '@/types'

interface FileServerResponse {
  success: boolean
  items: FileServerItem[]
}

export const fileServerApi = {
  getItems: async (): Promise<FileServerResponse> => ({ success: true, items: await getAllManagementPages<FileServerItem>('/fileserver') }),
  list: (page = 1, search = '') => api.get<ManagementPage<FileServerItem>>('/fileserver', { params: { page, pageSize: 20, search } }),

  addItem: (data: {
    itemId: string
    prefix?: string
    basePath?: string
    browse?: boolean
    preCompressed?: boolean
    maxFileSize?: number
    tryFiles?: string[]
    defaultFiles?: string[]
  }) => api.post<ApiResponse>('/fileserver/add', data),

  updateItem: (data: {
    itemId: string
    prefix?: string
    basePath?: string
    browse?: boolean
    preCompressed?: boolean
    maxFileSize?: number
    tryFiles?: string[]
    defaultFiles?: string[]
  }) => api.post<ApiResponse>('/fileserver/update', data),

  removeItem: (itemId: string) =>
    api.post<ApiResponse>('/fileserver/remove', { itemId }),

  removePatch: () =>
    api.post<ApiResponse>('/fileserver/patch/remove', {}),
}

// ==================== 简单响应配置 API ====================

import type { SimpleResItem } from '@/types'

interface SimpleResResponse {
  success: boolean
  items: SimpleResItem[]
}

export const simpleResApi = {
  getItems: async (): Promise<SimpleResResponse> => ({ success: true, items: await getAllManagementPages<SimpleResItem>('/simpleres') }),
  list: (page = 1, search = '') => api.get<ManagementPage<SimpleResItem>>('/simpleres', { params: { page, pageSize: 20, search } }),

  addItem: (data: {
    itemId: string
    body?: string
    contentType?: string
    statusCode?: number
    charset?: string
    showReq?: boolean
    headers?: Record<string, string>
  }) => api.post<ApiResponse>('/simpleres/add', data),

  updateItem: (data: {
    itemId: string
    body?: string
    contentType?: string
    statusCode?: number
    charset?: string
    showReq?: boolean
    headers?: Record<string, string>
  }) => api.post<ApiResponse>('/simpleres/update', data),

  removeItem: (itemId: string) =>
    api.post<ApiResponse>('/simpleres/remove', { itemId }),

  removePatch: () =>
    api.post<ApiResponse>('/simpleres/patch/remove', {}),
}

// ==================== 仪表板 API ====================

export interface DashboardResponse {
  success: boolean
  geoTraffic: GeoTrafficStats
  system: {
    uptime: string
    memory: number
    totalConnections: number
    httpConnections: number
    wsConnections: number
    blockedIpCount: number
    processStartTime: string
    uniqueIps: number
  }
  traffic: TrafficStats
  features: {
    ipControl: boolean
    geoControl: boolean
    wafArgs: boolean
    wafPost: boolean
    ccProtection: boolean
  }
  recentClients: Array<{ ip: string; lastAccessTime: string }>
  whitelist: string[]
  blacklist: string[]
  geoAccess: {
    allowCountries: string[]
    allowRegions: string[]
    denyCountries: string[]
    denyRegions: string[]
  }
  wafRules: {
    args: string[]
    post: string[]
    argsFileCount: number
    postFileCount: number
  }
  ccRules: Array<{ path: string; period: number; limitNum: number; fbTime: number }>
  blockedIps: Array<{ ip: string; type: string; reason: string; remainingSeconds?: number }>
  abTests: Array<{ testId: string; name: string; enabled: boolean; mode: string; variants: Record<string, number> }>
  timestamp: string
}

export const dashboardApi = {
  getData: () =>
    api.get<DashboardResponse>('/dashboard'),
}

// ==================== 插件管理 API ====================

export interface PluginActionItem {
  id: string
  name: string
  type: string
  description?: string
  path?: string
}

export const distributionApi = {
  getDashboard: () =>
    api.get<{ success: boolean; dashboard: MultiNodeDashboard; message?: string }>('/distribution/dashboard'),
  getDeployments: () =>
    api.get<{ success: boolean; deployments: ConfigurationDeployment[] }>('/distribution/deployments'),
  createDeployment: (data: { versionId: string; nodeIds?: string[]; canaryCount?: number; batchSize?: number; pauseAfterEachBatch?: boolean }) =>
    api.post<{ success: boolean; deployment?: ConfigurationDeployment; message?: string }>('/distribution/deployments', data),
  command: (id: string, command: 'pause' | 'resume' | 'rollback') =>
    api.post<{ success: boolean; deployment?: ConfigurationDeployment; message?: string }>(`/distribution/deployments/${encodeURIComponent(id)}/${command}`),
}

export interface PluginItem {
  id: string
  name: string
  version: string
  description: string
  author: string
  priority: string
  state: string
  isEnabled: boolean
  isSystem: boolean
  enabledByDefault: boolean
  hasOptions: boolean
  actions: PluginActionItem[]
}

// PluginLogEntry 与 IpLogEntry 共用统一的 LogEntry 模型（后端 LogUtil.LogEntry）
export type PluginLogEntry = IpLogEntry

export interface PluginLogFile {
  name: string
  size: number
  lastModified: string
}

export interface PluginActionResponse {
  success: boolean
  message?: string
  data?: any
}

export interface PluginsResponse {
  success: boolean
  plugins: PluginItem[]
}

export interface PluginConfigResponse {
  version: string
  supportsScopedConfiguration?: boolean
  success: boolean
  pluginId: string
  config: Record<string, string>
  defaults: Record<string, string>
  labels?: Record<string, string>
  fieldDefaults?: Record<string, string>
  schema?: PluginConfigurationField[]
}

export interface PluginConfigurationField {
  labelKey?: string; descriptionKey?: string
  path: string; type: string; label: string; required: boolean
  minimum?: string; maximum?: string; sensitive: boolean; requiresRestart: boolean
}

export const pluginApi = {
  getMenus: () => api.get<{ success: boolean; menus: { pluginId: string; id: string; name: string; path: string }[] }>('/plugins/menus'),
  list: (page = 1, search = '') => api.get<ManagementPage<PluginItem>>('/plugins', { params: { page, pageSize: 20, search } }),
  getPlugins: async (): Promise<PluginsResponse> => ({ success: true, plugins: await getAllManagementPages<PluginItem>('/plugins') }),

  togglePlugin: (pluginId: string) =>
    api.post<ApiResponse & { state: string; isEnabled: boolean }>('/plugins/toggle', { pluginId }),

  installPackage: (file: File) =>
    api.post<ApiResponse & { id: string; version: string; requiresRestart: boolean }>('/plugins/install', file, {
      headers: { 'Content-Type': 'application/octet-stream' },
    }),

  rollback: (pluginId: string) =>
    api.post<ApiResponse & { version: string; requiresRestart: boolean }>(
      `/plugins/${encodeURIComponent(pluginId)}/rollback`,
    ),

  getConfig: (pluginId: string) =>
    api.get<PluginConfigResponse>(`/plugins/${encodeURIComponent(pluginId)}/config`),

  saveConfig: (pluginId: string, config: Record<string, string>, version: string) =>
    api.post<ApiResponse & { status: string; version: string }>(`/plugins/${encodeURIComponent(pluginId)}/config`, config,
      { headers: { 'If-Match': version } }),

  validateConfig: (pluginId: string, config: Record<string, string>) =>
    api.post<ApiResponse>(`/plugins/${encodeURIComponent(pluginId)}/config/validate`, config),

  getEffectiveConfig: (pluginId: string, host: string, path: string) =>
    api.get<{ config: Record<string, string>; sources: Record<string, string>; lastReloadError?: string }>(
      `/plugins/${encodeURIComponent(pluginId)}/config/effective`, { params: { host, path } }),

  executeAction: (pluginId: string, actionId: string, parameters?: Record<string, string>) =>
    api.post<PluginActionResponse>(
      `/plugins/${encodeURIComponent(pluginId)}/actions/${encodeURIComponent(actionId)}`,
      parameters || {},
    ),
}


// ==================== 通用设置 API ====================

import type { CertDetail, AuditLogEntry, ConsoleInfo } from '@/types'

interface AuditLogsResponse {
  success: boolean
  items: AuditLogEntry[]
  total: number
}

export const settingsApi = {
  getCerts: (page = 1, search = '') => api.get<ManagementPage<CertDetail>>('/settings/certs', { params: { page, pageSize: 20, search } }),

  uploadCert: (data: { domain: string; pemContent: string; keyContent: string }) =>
    api.post<ApiResponse>('/settings/certs/upload', data),

  deleteCert: (pemFile: string) =>
    api.post<ApiResponse>('/settings/certs/delete', { pemFile }),

  acmeApply: (data: { domain: string; email: string }) =>
    api.post<{ success: boolean; data: { id: string }; errorMessage?: string }>('/settings/certs/acme-apply', data),

  acmeRenew: (data: { domain: string; email?: string }) =>
    api.post<{ success: boolean; data: { id: string }; errorMessage?: string }>('/settings/certs/acme-renew', data),

  getAcmeEmail: () =>
    api.get<{ success: boolean; email: string }>('/settings/acme-email'),

  getCertificateStatus: () =>
    api.get<{ certificates: import('@/types').CertificateRuntimeStatus[]; accounts: import('@/types').AcmeAccountStatus[] }>('/settings/certs/status'),

  getConsole: () =>
    api.get<ConsoleInfo & { success: boolean }>('/settings/console'),

  getAuditLogs: async (offset = 0, limit = 50): Promise<AuditLogsResponse> => {
    const result = await api.get<ManagementPage<AuditLogEntry>>('/settings/audit-logs', { params: { page: Math.floor(offset / limit) + 1, pageSize: limit } })
    return { success: result.success, items: result.data, total: result.total }
  },

  getRuntimeLogs: async (offset = 0, limit = 20) => {
    const result = await api.get<ManagementPage<RuntimeLogEntry>>('/settings/runtime-logs', { params: { page: Math.floor(offset / limit) + 1, pageSize: limit } })
    return { success: result.success, items: result.data, total: result.total }
  },
}

export interface RuntimeLogEntry {
  id: number
  startTime: string
  stopTime: string | null
  exitReason: string
  durationSeconds: number
  peakMemoryMb: number
  finalMemoryMb: number
  gcGen0: number
  gcGen1: number
  gcGen2: number
  totalRequests: number
  totalIntercepts: number
  dotnetVersion: string
  osDescription: string
  processId: number
}

// ==================== 认证 API ====================

export const authApi = {
  mfaStatus: () => api.get<{ success: boolean; enabled: boolean }>('/auth/mfa'),
  mfaAction: (action: 'setup' | 'confirm' | 'disable', password: string, code?: string) =>
    api.post<{ success: boolean; message?: string; enrollment?: { secret: string; uri: string }; recoveryCodes?: string[] }>(`/auth/mfa/${action}`, { password, code }),
  /** 登录：发送 SHA256(SHA256(password) + timestamp) + timestamp */
  login: (username: string, passwordHash: string, timestamp: number, secondFactor?: string) =>
    axios.post('/api/auth/login', { username, passwordHash, timestamp, secondFactor }).then(r => r.data) as Promise<{
      success: boolean; token?: string; username?: string; role?: string; permissions?: string[]; expiresAt?: string; message?: string; retryAfterSeconds?: number
    }>,

  /** 获取服务器 Unix 时间戳（秒），用于校准客户端时间 */
  time: () =>
    axios.get('/api/auth/time').then(r => r.data) as Promise<{ timestamp: number }>,

  check: () =>
    api.get<{ success: boolean; authRequired: boolean }>('/auth/check'),

  refresh: () =>
    api.post<{ success: boolean; token?: string; expiresAt?: string }>('/auth/refresh'),

  me: () =>
    api.get<{ success: boolean; username: string }>('/auth/me'),

  changePassword: (currentPassword: string, newPassword: string) =>
    api.post<ApiResponse>('/auth/change-password', { currentPassword, newPassword }),

  users: (page = 1, search = '') => api.get<{ success: boolean; data: ManagementUser[]; total: number }>('/auth/users', { params: { page, pageSize: 20, search } }),
  roles: async () => ({ success: true, data: await getAllManagementPages<ManagementRole>('/auth/roles') }),
  rolePage: (page = 1, search = '') => api.get<ManagementPage<ManagementRole>>('/auth/roles', { params: { page, pageSize: 20, search } }),
  saveRole: (role: { name: string; permissions: string[] }) => api.post<ApiResponse>('/auth/roles', role),
  deleteRole: (name: string) => api.delete<ApiResponse>(`/auth/roles/${encodeURIComponent(name)}`),
  createUser: (request: { username: string; password: string; role: string }) =>
    api.post<{ success: boolean; data: ManagementUser }>('/auth/users', request),
  setUserDisabled: (id: string, disabled: boolean) =>
    api.put<ApiResponse>(`/auth/users/${encodeURIComponent(id)}/disabled`, { disabled }),
  setUserRole: (id: string, role: string) =>
    api.put<ApiResponse>(`/auth/users/${encodeURIComponent(id)}/role`, { role }),
  resetUserPassword: (id: string) =>
    api.post<{ success: boolean; password: string }>(`/auth/users/${encodeURIComponent(id)}/reset-password`),
  revokeUserSessions: (id: string) =>
    api.post<ApiResponse>(`/auth/users/${encodeURIComponent(id)}/revoke-sessions`),
  loginHistory: (limit = 100) =>
    api.get<ManagementPage<ManagementLoginRecord>>('/auth/login-history', { params: { page: 1, pageSize: limit } }),
}

export interface ManagementUser {
  id: string
  username: string
  role: string
  disabled: boolean
  createdAt: string
  lastLoginAt?: string
  permissions: string[]
}

export interface ManagementRole { name: string; permissions: string[]; builtIn: boolean }

export interface ManagementLoginRecord {
  username: string
  occurredAt: string
  success: boolean
  clientIp: string
}

// ==================== 拦截事件 API ====================

export interface InterceptEvent {
  sourceIp: string
  region: string
  city: string
  application: string
  ruleName: string
  ruleType: string
  hitCount: number
  duration: number
  firstHitTime: string
  lastHitTime: string
}

export const interceptEventApi = {
  list: (params?: { ip?: string; domain?: string; startTime?: string; endTime?: string; page?: number; search?: string }) => {
    const query = new URLSearchParams()
    if (params?.ip) query.set('ip', params.ip)
    if (params?.domain) query.set('domain', params.domain)
    if (params?.startTime) query.set('from', new Date(params.startTime).toISOString())
    if (params?.endTime) query.set('to', new Date(params.endTime).toISOString())
    query.set('page', (params?.page ?? 1).toString())
    query.set('pageSize', '20')
    if (params?.search) query.set('search', params.search)
    const qs = query.toString()
    return api.get<ManagementPage<InterceptEvent>>(`/intercept-events${qs ? '?' + qs : ''}`)
  },
  clear: () => api.post<{ success: boolean; message: string }>('/intercept-events/clear'),
}

// ==================== 拦截明细日志 API ====================

export interface InterceptLogFile {
  name: string
  size: number
  lastModified: string
}

export interface InterceptLogEntry {
  index: number
  time: string
  method: string
  url: string
  host: string
  requestLine: string
  headers?: string
  requestBody?: string
  responseBody?: string
  statusCode?: number
  duration?: string
  clientIp?: string
  scheme?: string
  reason?: string
  raw: string
}

export const interceptLogApi = {
  listFiles: async () => ({ success: true, files: await getAllManagementPages<InterceptLogFile>('/intercept-logs/files') }),
  getEntries: async (params: { file: string; offset?: number; limit?: number; search?: string; startTime?: string; endTime?: string }) => {
    const query = new URLSearchParams()
    query.set('file', params.file)
    query.set('page', (Math.floor((params.offset ?? 0) / (params.limit ?? 20)) + 1).toString())
    query.set('pageSize', (params.limit ?? 20).toString())
    if (params.search) query.set('search', params.search)
    if (params.startTime) query.set('from', new Date(params.startTime).toISOString())
    if (params.endTime) query.set('to', new Date(params.endTime).toISOString())
    const response = await api.get<ManagementPage<InterceptLogEntry>>(`/intercept-logs/entries?${query.toString()}`)
    return { ...response, entries: response.data, fileName: params.file }
  },
}

// ==================== 代理管理 API ====================

export interface PcapPortStatus {
  port: string
  enablePcap: boolean
  enableHttp: boolean
  enableHttps: boolean
  enableSocks5: boolean
  pcapHosts: string[]
}

export interface PcapStatus {
  success: boolean
  proxyEnabled: boolean
  caCertExists: boolean
  ports: PcapPortStatus[]
}

export const pcapApi = {
  getStatus: () =>
    api.get<PcapStatus>('/pcap/status'),

  toggle: (portKey: string, enabled: boolean, pcapHosts?: string[]) =>
    api.post<{ success: boolean; enabled: boolean; message: string }>('/pcap/toggle', { portKey, enabled, pcapHosts }),

  downloadCaCert: async () => {
    const token = localStorage.getItem('lywaf_token')
    const res = await fetch('/api/pcap/ca-cert', {
      headers: { Authorization: `Bearer ${token}` },
    })
    if (!res.ok) throw new Error('下载失败')
    const blob = await res.blob()
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = 'proxy-ca.crt'
    a.click()
    URL.revokeObjectURL(url)
  },

  listLogFiles: async () => ({ success: true, files: await getAllManagementPages<InterceptLogFile>('/pcap/logs/files') }),

  getLogEntries: async (params: { file: string; offset?: number; limit?: number; search?: string; host?: string; clientIp?: string; startTime?: string; endTime?: string }) => {
    const query = new URLSearchParams()
    query.set('file', params.file)
    query.set('page', (Math.floor((params.offset ?? 0) / (params.limit ?? 20)) + 1).toString())
    query.set('pageSize', (params.limit ?? 20).toString())
    if (params.search) query.set('search', params.search)
    if (params.host) query.set('host', params.host)
    if (params.clientIp) query.set('clientIp', params.clientIp)
    if (params.startTime) query.set('from', new Date(params.startTime).toISOString())
    if (params.endTime) query.set('to', new Date(params.endTime).toISOString())
    const response = await api.get<ManagementPage<InterceptLogEntry>>(`/pcap/logs/entries?${query.toString()}`)
    return { ...response, entries: response.data, fileName: params.file }
  },

  deleteLogFile: (file: string) =>
    api.post<{ success: boolean; message?: string }>('/pcap/logs/delete', { file }),

  getLogHosts: (file: string) =>
    api.get<{ success: boolean; hosts: string[] }>(`/pcap/logs/hosts?file=${encodeURIComponent(file)}`),
}

// ==================== 访问控制（黑白名单）API ====================

export interface AccessControlData {
  success: boolean
  ipControl: {
    enabled: boolean
    whitelist: string[]
    blacklist: string[]
  }
  geoControl: {
    enabled: boolean
    mode: string
    denyCountries: string[]
    denyRegions: string[]
    allowCountries: string[]
    allowRegions: string[]
  }
  stats: {
    blacklistBlockCount: number
    geoBlockCount: number
  }
  rejectStatusCode: number
}

export const accessControlApi = {
  /** 获取所有访问控制数据 */
  getAll: () =>
    api.get<AccessControlData>('/ac/all'),

  /** 切换 IP 访问控制 */
  toggleIpControl: () =>
    api.post<{ success: boolean; enabled: boolean }>('/ac/ip-control/toggle'),

  /** 切换地理位置访问控制 */
  toggleGeoControl: () =>
    api.post<{ success: boolean; enabled: boolean }>('/ac/geo-control/toggle'),

  /** 添加白名单 */
  addWhitelist: (ipOrCidr: string) =>
    api.post<{ success: boolean; message: string }>('/ac/whitelist/add', { ipOrCidr }),

  /** 移除白名单 */
  removeWhitelist: (ipOrCidr: string) =>
    api.post<{ success: boolean; message: string }>('/ac/whitelist/remove', { ipOrCidr }),

  /** 添加黑名单 */
  addBlacklist: (ipOrCidr: string) =>
    api.post<{ success: boolean; message: string }>('/ac/blacklist/add', { ipOrCidr }),

  /** 移除黑名单 */
  removeBlacklist: (ipOrCidr: string) =>
    api.post<{ success: boolean; message: string }>('/ac/blacklist/remove', { ipOrCidr }),

  /** 添加禁止国家 */
  addDenyCountry: (country: string) =>
    api.post<{ success: boolean; message: string }>('/geo/deny-countries/add', { country }),

  /** 移除禁止国家 */
  removeDenyCountry: (country: string) =>
    api.post<{ success: boolean; message: string }>('/geo/deny-countries/remove', { country }),

  /** 添加禁止省份 */
  addDenyRegion: (region: string) =>
    api.post<{ success: boolean; message: string }>('/geo/deny-regions/add', { region }),

  /** 移除禁止省份 */
  removeDenyRegion: (region: string) =>
    api.post<{ success: boolean; message: string }>('/geo/deny-regions/remove', { region }),

  /** 添加允许国家 */
  addAllowCountry: (country: string) =>
    api.post<{ success: boolean; message: string }>('/geo/allow-countries/add', { country }),

  /** 移除允许国家 */
  removeAllowCountry: (country: string) =>
    api.post<{ success: boolean; message: string }>('/geo/allow-countries/remove', { country }),

  /** 添加允许省份 */
  addAllowRegion: (region: string) =>
    api.post<{ success: boolean; message: string }>('/geo/allow-regions/add', { region }),

  /** 移除允许省份 */
  removeAllowRegion: (region: string) =>
    api.post<{ success: boolean; message: string }>('/geo/allow-regions/remove', { region }),
}

export default http
