import axios from 'axios'
import type { 
  ApiResponse, 
  TrafficStats, 
  SecurityStats, 
  ApiTimingStat,
  ApiTimingSummary 
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
  addWhitelist: (ipOrCidr: string) =>
    api.post<ApiResponse>('/ac/whitelist/add', { ipOrCidr }),
    
  removeWhitelist: (ipOrCidr: string) =>
    api.post<ApiResponse>('/ac/whitelist/remove', { ipOrCidr }),
    
  // 黑名单
  addBlacklist: (ipOrCidr: string) =>
    api.post<ApiResponse>('/ac/blacklist/add', { ipOrCidr }),
    
  removeBlacklist: (ipOrCidr: string) =>
    api.post<ApiResponse>('/ac/blacklist/remove', { ipOrCidr }),
    
  // 封禁管理
  blockIp: (ip: string, reason?: string, duration?: number) =>
    api.post<ApiResponse>('/ac/block', { ip, reason, duration }),
    
  unblockIp: (ip: string) =>
    api.post<ApiResponse>('/ac/unblock', { ip }),
    
  clearBlockedIps: () =>
    api.post<ApiResponse>('/ac/blocked/clear'),
}

// ==================== 地理访问 API ====================

export const geoApi = {
  addDenyCountry: (country: string) =>
    api.post<ApiResponse>('/geo/deny-country/add', { country }),
    
  removeDenyCountry: (country: string) =>
    api.post<ApiResponse>('/geo/deny-country/remove', { country }),
    
  addDenyRegion: (region: string) =>
    api.post<ApiResponse>('/geo/deny-region/add', { region }),
    
  removeDenyRegion: (region: string) =>
    api.post<ApiResponse>('/geo/deny-region/remove', { region }),
    
  addAllowCountry: (country: string) =>
    api.post<ApiResponse>('/geo/allow-countries/add', { country }),
    
  removeAllowCountry: (country: string) =>
    api.post<ApiResponse>('/geo/allow-countries/remove', { country }),
    
  addAllowRegion: (region: string) =>
    api.post<ApiResponse>('/geo/allow-region/add', { region }),
    
  removeAllowRegion: (region: string) =>
    api.post<ApiResponse>('/geo/allow-region/remove', { region }),
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
    api.post<ApiResponse>('/cc/rule/add', { path, limitNum, period, fbTime }),
    
  removeRule: (path: string) =>
    api.post<ApiResponse>('/cc/rule/remove', { path }),
}

// ==================== 流量统计 API ====================

export const trafficApi = {
  getStats: () =>
    api.get<TrafficStats>('/traffic/stats'),
    
  reset: () =>
    api.post<ApiResponse>('/traffic/reset'),
}

// ==================== 安全统计 API ====================

export const securityApi = {
  getStats: (hours = 24) =>
    api.get<SecurityStats>('/security/stats', { params: { hours } }),
    
  reset: () =>
    api.post<ApiResponse>('/security/reset'),
}

// ==================== API 耗时统计 ====================

export interface TimingListResponse {
  summary: ApiTimingSummary
  items: ApiTimingStat[]
  backends: Record<string, ApiTimingStat[]>
}

export const timingApi = {
  getList: () =>
    api.get<TimingListResponse>('/timing/list'),
    
  clear: () =>
    api.post<ApiResponse>('/timing/clear'),
}

// ==================== A/B 测试 API ====================

export const abTestApi = {
  toggle: (id: string, enabled: boolean) =>
    api.post<ApiResponse>(`/abtest/${id}/toggle`, { enabled }),
    
  getStats: (id: string) =>
    api.get<ApiResponse>(`/abtest/${id}/stats`),
}

export default http
