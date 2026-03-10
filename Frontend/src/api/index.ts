import axios from 'axios'
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
  baseURL: '/api',
  timeout: 30000,
  headers: {
    'Content-Type': 'application/json',
  },
})

// 请求拦截器 - 注入 JWT Token
http.interceptors.request.use((config) => {
  const token = localStorage.getItem('lywaf_token')
  if (token) {
    config.headers.Authorization = `Bearer ${token}`
  }
  return config
})

// 响应拦截器 - 直接返回 data，401 自动跳转登录
http.interceptors.response.use(
  (response) => response.data,
  (error) => {
    if (error.response?.status === 401) {
      localStorage.removeItem('lywaf_token')
      localStorage.removeItem('lywaf_username')
      if (!window.location.hash.includes('/login')) {
        window.location.hash = '#/login'
      }
      return Promise.reject(error)
    }
    // 如果服务端返回了 JSON body（如 400），直接返回 data 让业务层处理
    if (error.response?.data) {
      return error.response.data
    }
    console.error('API Error:', error)
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

interface IpLogReadResponse {
  success: boolean
  ip: string
  entries: IpLogEntry[]
  total: number
  offset: number
  limit: number
  fileSize: number
  timestamp: string
}

// ==================== IP 管理 API ====================

export const ipApi = {
  // 白名单
  getWhitelist: () =>
    api.get<{ success: boolean; whitelist: string[] }>('/ac/whitelist'),

  addWhitelist: (ipOrCidr: string) =>
    api.post<ApiResponse>('/ac/whitelist/add', { ipOrCidr }),

  removeWhitelist: (ipOrCidr: string) =>
    api.post<ApiResponse>('/ac/whitelist/remove', { ipOrCidr }),

  // 黑名单
  getBlacklist: () =>
    api.get<{ success: boolean; blacklist: string[] }>('/ac/blacklist'),

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

  readIpLog: (ip: string, offset = 0, limit = 50) =>
    api.get<IpLogReadResponse>(`/ip-log/read/${encodeURIComponent(ip)}`, { params: { offset, limit } }),

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

// ==================== CC 防护 API ====================

export const ccApi = {
  addRule: (path: string, limitNum: number, period: number, fbTime: number) =>
    api.post<ApiResponse>('/cc/rules/add', { path, limitNum, period, fbTime }),
    
  removeRule: (path: string) =>
    api.post<ApiResponse>('/cc/rules/remove', { path }),
}

// ==================== 高级 CC 规则 API ====================

import type { AdvancedCcRule } from '@/types'

interface AdvancedCcRulesResponse {
  success: boolean
  count: number
  rules: AdvancedCcRule[]
}

interface AdvancedCcRuleResponse {
  success: boolean
  rule: AdvancedCcRule
}

interface CcEnumsResponse {
  success: boolean
  matchTargets: { name: string; value: string }[]
  matchOperators: { name: string; value: string }[]
  ruleTypes: { name: string; value: string }[]
  actions: { name: string; value: string }[]
}

export interface CreateAdvancedCcRuleRequest {
  name: string
  enabled?: boolean
  type?: string
  conditions?: Array<{
    target: string
    operator: string
    values: string[]
  }>
  period?: number
  threshold?: number
  action?: string
  actionSeconds?: number
  priority?: number
}

export const advancedCcApi = {
  // 获取所有高级 CC 规则
  getRules: () =>
    api.get<AdvancedCcRulesResponse>('/cc/advanced'),
    
  // 按类型获取规则
  getRulesByType: (type: string) =>
    api.get<AdvancedCcRulesResponse>(`/cc/advanced/type/${type}`),
    
  // 获取单个规则
  getRule: (ruleId: string) =>
    api.get<AdvancedCcRuleResponse>(`/cc/advanced/${ruleId}`),
    
  // 添加规则
  addRule: (rule: CreateAdvancedCcRuleRequest) =>
    api.post<ApiResponse & { ruleId: string }>('/cc/advanced/add', rule),
    
  // 更新规则
  updateRule: (rule: CreateAdvancedCcRuleRequest & { id: string }) =>
    api.post<ApiResponse & { ruleId: string }>('/cc/advanced/update', rule),
    
  // 删除规则
  removeRule: (ruleId: string) =>
    api.post<ApiResponse>('/cc/advanced/remove', { ruleId }),
    
  // 启用/禁用规则
  toggleRule: (ruleId: string, enabled?: boolean) =>
    api.post<ApiResponse & { enabled: boolean }>('/cc/advanced/toggle', { ruleId, enabled }),
    
  // 获取枚举值
  getEnums: () =>
    api.get<CcEnumsResponse>('/cc/advanced/enums'),
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
  backends: Record<string, ApiTimingStat[]>
}

export const timingApi = {
  getList: async (): Promise<TimingListResponse> => {
    const raw = await api.get<TimingRawResponse>('/timing/list')
    
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
    
    // 按后端分组
    const backends: Record<string, ApiTimingStat[]> = {}
    for (const item of items) {
      if (item.backend) {
        if (!backends[item.backend]) {
          backends[item.backend] = []
        }
        backends[item.backend].push(item)
      }
    }
    
    return {
      summary: raw.summary,
      items,
      backends,
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

export const configFileApi = {
  getFile: () =>
    api.get<ConfigFileResponse>('/config/file'),

  saveFile: (content: string, reload: boolean = false) =>
    api.post<ApiResponse & { portsChanged?: boolean }>('/config/file', { content, reload }),

  convert: (content: string) =>
    api.post<ConvertResponse>('/config/convert', { content }),
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
    api.get<{ success: boolean; templates: ErrorTemplateItem[] }>('/error-templates'),

  get: (statusCode: number) =>
    api.get<ErrorTemplateDetail>(`/error-templates/${statusCode}`),

  save: (statusCode: number, content: string) =>
    api.post<ApiResponse>(`/error-templates/${statusCode}`, { content }),

  revert: (statusCode: number) =>
    api.post<ApiResponse>(`/error-templates/${statusCode}/revert`),
}

// ==================== WAF 自定义规则 API ====================

export const wafRuleApi = {
  list: () => api.get('/waf-rules'),
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
    api.get<ApiResponse & { portsChanged?: boolean }>('/reload'),

  restart: () =>
    api.get<ApiResponse>('/restart'),

  stop: () =>
    api.get<ApiResponse>('/stop'),
}

// ==================== 集群配置 API ====================

import type { LbCluster, LbPolicy } from '@/types'

interface LbClustersResponse {
  success: boolean
  clusters: LbCluster[]
  timestamp: string
}

interface LbPoliciesResponse {
  success: boolean
  policies: LbPolicy[]
}

export const lbApi = {
  getClusters: () =>
    api.get<LbClustersResponse>('/lb/clusters'),

  getPolicies: () =>
    api.get<LbPoliciesResponse>('/lb/policies'),

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
    api.post<ApiResponse>('/lb/destinations/batch-remove', { clusterId, destinationIds }),

  removeClusterPatch: (clusterId: string) =>
    api.post<ApiResponse>('/lb/patch/remove', { clusterId }),
}

// ==================== 路由配置 API ====================

import type { RouteConfig } from '@/types'

interface RoutesResponse {
  success: boolean
  routes: RouteConfig[]
  timestamp: string
}

export const routeApi = {
  getRoutes: () =>
    api.get<RoutesResponse>('/routes'),

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
  timestamp: string
}

export const fileServerApi = {
  getItems: () =>
    api.get<FileServerResponse>('/fileserver'),

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
  timestamp: string
}

export const simpleResApi = {
  getItems: () =>
    api.get<SimpleResResponse>('/simpleres'),

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
  success: boolean
  pluginId: string
  config: Record<string, string>
  defaults: Record<string, string>
  labels?: Record<string, string>
  fieldDefaults?: Record<string, string>
}

export const pluginApi = {
  getPlugins: () =>
    api.get<PluginsResponse>('/plugins'),

  togglePlugin: (pluginId: string) =>
    api.post<ApiResponse & { state: string; isEnabled: boolean }>('/plugins/toggle', { pluginId }),

  getConfig: (pluginId: string) =>
    api.get<PluginConfigResponse>(`/plugins/${encodeURIComponent(pluginId)}/config`),

  saveConfig: (pluginId: string, config: Record<string, string>) =>
    api.post<ApiResponse>(`/plugins/${encodeURIComponent(pluginId)}/config`, config),

  executeAction: (pluginId: string, actionId: string, parameters?: Record<string, string>) =>
    api.post<PluginActionResponse>(
      `/plugins/${encodeURIComponent(pluginId)}/actions/${encodeURIComponent(actionId)}`,
      parameters || {},
    ),
}

// ==================== 统计配置 API ====================

import type { StatisticConfig } from '@/types'

export const statisticApi = {
  getConfig: () =>
    api.get<StatisticConfig & { success: boolean }>('/statistic/config'),

  addWhitePath: (path: string) =>
    api.post<ApiResponse>('/statistic/white-paths/add', { path }),

  removeWhitePath: (path: string) =>
    api.post<ApiResponse>('/statistic/white-paths/remove', { path }),

  addPathSta: (path: string) =>
    api.post<ApiResponse>('/statistic/path-stas/add', { path }),

  removePathSta: (path: string) =>
    api.post<ApiResponse>('/statistic/path-stas/remove', { path }),
}

// ==================== 通用设置 API ====================

import type { CertDetail, AuditLogEntry, ConsoleInfo } from '@/types'

interface CertsResponse {
  success: boolean
  certs: CertDetail[]
}

interface AuditLogsResponse {
  success: boolean
  items: AuditLogEntry[]
  total: number
}

export const settingsApi = {
  getCerts: () =>
    api.get<CertsResponse>('/settings/certs'),

  uploadCert: (data: { domain: string; pemContent: string; keyContent: string }) =>
    api.post<ApiResponse>('/settings/certs/upload', data),

  deleteCert: (pemFile: string) =>
    api.post<ApiResponse>('/settings/certs/delete', { pemFile }),

  acmeApply: (data: { domain: string; email: string }) =>
    api.post<{ success: boolean; domain?: string; expiresAt?: string; errorMessage?: string }>('/settings/certs/acme-apply', data, { timeout: 120000 }),

  acmeRenew: (data: { domain: string; email?: string }) =>
    api.post<{ success: boolean; domain?: string; expiresAt?: string; errorMessage?: string }>('/settings/certs/acme-renew', data, { timeout: 120000 }),

  getAcmeEmail: () =>
    api.get<{ success: boolean; email: string }>('/settings/acme-email'),

  getConsole: () =>
    api.get<ConsoleInfo & { success: boolean }>('/settings/console'),

  getAuditLogs: (offset = 0, limit = 50) =>
    api.get<AuditLogsResponse>('/settings/audit-logs', { params: { offset, limit } }),

  getRuntimeLogs: (offset = 0, limit = 20) =>
    api.get<{ success: boolean; items: RuntimeLogEntry[]; total: number }>('/settings/runtime-logs', { params: { offset, limit } }),
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
  /** 登录：发送 SHA256(SHA256(password) + timestamp) + timestamp */
  login: (username: string, passwordHash: string, timestamp: number) =>
    axios.post('/api/auth/login', { username, passwordHash, timestamp }).then(r => r.data) as Promise<{
      success: boolean; token?: string; username?: string; expiresAt?: string; message?: string; retryAfterSeconds?: number
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
  list: (params?: { ip?: string; domain?: string; startTime?: string; endTime?: string }) => {
    const query = new URLSearchParams()
    if (params?.ip) query.set('ip', params.ip)
    if (params?.domain) query.set('domain', params.domain)
    if (params?.startTime) query.set('startTime', params.startTime)
    if (params?.endTime) query.set('endTime', params.endTime)
    const qs = query.toString()
    return api.get<{ success: boolean; count: number; events: InterceptEvent[] }>(`/intercept-events${qs ? '?' + qs : ''}`)
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
  raw: string
}

export const interceptLogApi = {
  listFiles: () => api.get<{ success: boolean; files: InterceptLogFile[] }>('/intercept-logs/files'),
  getEntries: (params: { file: string; offset?: number; limit?: number; search?: string }) => {
    const query = new URLSearchParams()
    query.set('file', params.file)
    if (params.offset !== undefined) query.set('offset', params.offset.toString())
    if (params.limit !== undefined) query.set('limit', params.limit.toString())
    if (params.search) query.set('search', params.search)
    return api.get<{ success: boolean; entries: InterceptLogEntry[]; total: number; offset: number; limit: number; fileName: string }>(`/intercept-logs/entries?${query.toString()}`)
  },
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
