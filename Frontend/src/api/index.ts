import axios from 'axios'
import type {
  ApiResponse,
  TrafficStats,
  SecurityStats,
  ApiTimingStat,
  ApiTimingSummary,
  GeoTrafficStats
} from '@/types'

const http = axios.create({
  baseURL: '/api',
  timeout: 30000,
  headers: {
    'Content-Type': 'application/json',
  },
})

// 响应拦截器 - 直接返回 data
http.interceptors.response.use(
  (response) => response.data,
  (error) => {
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
  blockIp: (ip: string, reason?: string, durationSeconds?: number) => {
    let duration: string | undefined
    if (durationSeconds) {
      const h = Math.floor(durationSeconds / 3600)
      const m = Math.floor((durationSeconds % 3600) / 60)
      const s = durationSeconds % 60
      duration = `${h.toString().padStart(2, '0')}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`
    }
    return api.post<ApiResponse>('/blocked-ips/add', { ip, reason, duration })
  },
    
  unblockIp: (ip: string) =>
    api.post<ApiResponse>('/blocked-ips/remove', { ip }),
    
  clearBlockedIps: () =>
    api.post<ApiResponse>('/blocked-ips/clear'),
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
    api.post<ApiResponse>('/config/file', { content, reload }),

  convert: (content: string) =>
    api.post<ConvertResponse>('/config/convert', { content }),
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

export default http
