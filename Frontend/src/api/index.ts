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

// 响应拦截器
http.interceptors.response.use(
  (response) => response.data,
  (error) => {
    console.error('API Error:', error)
    return Promise.reject(error)
  }
)

// ==================== 功能开关 API ====================

export const featureApi = {
  toggleIpControl: (enabled: boolean) =>
    http.post<ApiResponse>('/feature/ip-control/toggle', { enabled }),
    
  toggleGeoControl: (enabled: boolean) =>
    http.post<ApiResponse>('/feature/geo-control/toggle', { enabled }),
    
  toggleWafArgs: (enabled: boolean) =>
    http.post<ApiResponse>('/feature/waf-args/toggle', { enabled }),
    
  toggleWafPost: (enabled: boolean) =>
    http.post<ApiResponse>('/feature/waf-post/toggle', { enabled }),
}

// ==================== IP 管理 API ====================

export const ipApi = {
  // 白名单
  addWhitelist: (ipOrCidr: string) =>
    http.post<ApiResponse>('/ac/whitelist/add', { ipOrCidr }),
    
  removeWhitelist: (ipOrCidr: string) =>
    http.post<ApiResponse>('/ac/whitelist/remove', { ipOrCidr }),
    
  // 黑名单
  addBlacklist: (ipOrCidr: string) =>
    http.post<ApiResponse>('/ac/blacklist/add', { ipOrCidr }),
    
  removeBlacklist: (ipOrCidr: string) =>
    http.post<ApiResponse>('/ac/blacklist/remove', { ipOrCidr }),
    
  // 封禁管理
  blockIp: (ip: string, reason?: string, duration?: number) =>
    http.post<ApiResponse>('/ac/block', { ip, reason, duration }),
    
  unblockIp: (ip: string) =>
    http.post<ApiResponse>('/ac/unblock', { ip }),
    
  clearBlockedIps: () =>
    http.post<ApiResponse>('/ac/blocked/clear'),
}

// ==================== 地理访问 API ====================

export const geoApi = {
  addDenyCountry: (country: string) =>
    http.post<ApiResponse>('/geo/deny-country/add', { country }),
    
  removeDenyCountry: (country: string) =>
    http.post<ApiResponse>('/geo/deny-country/remove', { country }),
    
  addDenyRegion: (region: string) =>
    http.post<ApiResponse>('/geo/deny-region/add', { region }),
    
  removeDenyRegion: (region: string) =>
    http.post<ApiResponse>('/geo/deny-region/remove', { region }),
    
  addAllowCountry: (country: string) =>
    http.post<ApiResponse>('/geo/allow-countries/add', { country }),
    
  removeAllowCountry: (country: string) =>
    http.post<ApiResponse>('/geo/allow-countries/remove', { country }),
    
  addAllowRegion: (region: string) =>
    http.post<ApiResponse>('/geo/allow-region/add', { region }),
    
  removeAllowRegion: (region: string) =>
    http.post<ApiResponse>('/geo/allow-region/remove', { region }),
}

// ==================== WAF 规则 API ====================

export const wafApi = {
  addArgsRule: (pattern: string) =>
    http.post<ApiResponse>('/waf/args/add', { pattern }),
    
  removeArgsRule: (pattern: string) =>
    http.post<ApiResponse>('/waf/args/remove', { pattern }),
    
  addPostRule: (pattern: string) =>
    http.post<ApiResponse>('/waf/post/add', { pattern }),
    
  removePostRule: (pattern: string) =>
    http.post<ApiResponse>('/waf/post/remove', { pattern }),
}

// ==================== CC 防护 API ====================

export const ccApi = {
  addRule: (path: string, limitNum: number, period: number, fbTime: number) =>
    http.post<ApiResponse>('/cc/rule/add', { path, limitNum, period, fbTime }),
    
  removeRule: (path: string) =>
    http.post<ApiResponse>('/cc/rule/remove', { path }),
}

// ==================== 流量统计 API ====================

export const trafficApi = {
  getStats: () =>
    http.get<TrafficStats>('/traffic/stats'),
    
  reset: () =>
    http.post<ApiResponse>('/traffic/reset'),
}

// ==================== 安全统计 API ====================

export const securityApi = {
  getStats: (hours = 24) =>
    http.get<SecurityStats>('/security/stats', { params: { hours } }),
    
  reset: () =>
    http.post<ApiResponse>('/security/reset'),
}

// ==================== API 耗时统计 ====================

export const timingApi = {
  getList: () =>
    http.get<{ summary: ApiTimingSummary; items: ApiTimingStat[]; backends: Record<string, ApiTimingStat[]> }>('/timing/list'),
    
  clear: () =>
    http.post<ApiResponse>('/timing/clear'),
}

// ==================== A/B 测试 API ====================

export const abTestApi = {
  toggle: (id: string, enabled: boolean) =>
    http.post<ApiResponse>(`/abtest/${id}/toggle`, { enabled }),
    
  getStats: (id: string) =>
    http.get<ApiResponse>(`/abtest/${id}/stats`),
}

export default http
