// API 响应类型
export interface ApiResponse<T = unknown> {
  success: boolean
  message?: string
  data?: T
}

// 流量分析统计
export interface TrafficStats {
  totalRequests: number
  pageViews: number
  uniqueVisitors: number
  uniqueIps: number
  interceptCount: number
  attackIps: number
  error4xxCount: number
  error5xxCount: number
  intercept4xxCount: number
  error4xxRate: number
  error5xxRate: number
  intercept4xxRate: number
  startTime: string
}

// 功能状态
export interface FeatureStatus {
  ipControl: boolean
  geoControl: boolean
  wafArgs: boolean
  wafPost: boolean
  ccProtection: boolean
}

// 系统状态
export interface SystemStatus {
  uptime: string
  memory: number
  totalConnections: number
  blockedIpCount: number
  processStartTime: string
}

// IP 信息
export interface IpInfo {
  ip: string
  reason?: string
  duration?: string
  lastAccess?: string
}

// CC 规则
export interface CcRule {
  id: string
  path: string
  limitNum: number
  period: number
  fbTime: number
}

// =============== 高级 CC 规则类型 ===============

// 匹配目标
export type CcMatchTarget = 
  | 'UrlPath' 
  | 'FullUrl' 
  | 'Method' 
  | 'ContentType' 
  | 'UserAgent' 
  | 'Referer' 
  | 'Header' 
  | 'QueryParam' 
  | 'Cookie' 
  | 'ClientIp'
  | 'StatusCode'

// 匹配操作符
export type CcMatchOperator = 
  | 'Equal' 
  | 'NotEqual' 
  | 'Contains' 
  | 'NotContains' 
  | 'StartsWith' 
  | 'EndsWith' 
  | 'Regex' 
  | 'Exists' 
  | 'NotExists'

// 规则类型
export type CcRuleType = 'FrequentAccess' | 'FrequentAttack' | 'FrequentError'

// 触发动作
export type CcAction = 'Captcha' | 'Block' | 'Reject' | 'RateLimit' | 'LogOnly'

// CC 条件
export interface CcCondition {
  target: CcMatchTarget
  operator: CcMatchOperator
  values: string[]
}

// 高级 CC 规则
export interface AdvancedCcRule {
  id: string
  name: string
  enabled: boolean
  type: CcRuleType
  conditions: CcCondition[]
  period: number
  threshold: number
  action: CcAction
  actionDuration: number
  priority: number
  createdAt: string
}

// 枚举值选项
export interface EnumOption {
  name: string
  value: string
}

// CC 枚举值
export interface CcEnums {
  matchTargets: EnumOption[]
  matchOperators: EnumOption[]
  ruleTypes: EnumOption[]
  actions: EnumOption[]
}

// WAF 规则
export interface WafRule {
  id: string
  pattern: string
  type: 'args' | 'post'
}

// 地理访问配置
export interface GeoAccess {
  allowCountries: string[]
  allowRegions: string[]
  denyCountries: string[]
  denyRegions: string[]
}

// A/B 测试
export interface AbTest {
  id: string
  name: string
  enabled: boolean
  mode: string
}

// 安全事件类型
export type SecurityEventType = 'WafIntercept' | 'CcAttack' | 'Blacklist' | 'GeoDeny' | 'CrawlerDetect' | 'RateLimit'

// 安全统计时间槽
export interface SecurityTimeSlot {
  time: string
  count: number
}

// 攻击源统计
export interface AttackSourceStat {
  ip: string
  count: number
}

// 安全统计快照
export interface SecurityStats {
  wafIntercepts: number
  ccAttacks: number
  blacklistBlocks: number
  geoBlocks: number
  crawlerDetects: number
  rateLimits: number
  totalEvents: number
  history: {
    [key: string]: SecurityTimeSlot[]
  }
  topAttackSources: {
    [key: string]: AttackSourceStat[]
  }
}

// API 耗时统计
export interface ApiTimingStat {
  path: string
  method: string
  backend: string
  requestCount: number
  avgTotalTime: number
  avgBackendTime: number
  avgGatewayTime: number
  minTotalTime: number
  maxTotalTime: number
  minBackendTime: number
  maxBackendTime: number
  lastRequestTime: string
  errorRate: number
  statusCodes: { [key: string]: number }
}

// API 耗时汇总
export interface ApiTimingSummary {
  totalRequests: number
  avgTotalTime: number
  avgBackendTime: number
  avgGatewayTime: number
  errorRate: number
  apiCount: number
}

// 仪表板数据
export interface DashboardData {
  system: SystemStatus
  traffic: TrafficStats
  features: FeatureStatus
  recentClients: IpInfo[]
  whitelist: string[]
  blacklist: string[]
  geoAccess: GeoAccess
  wafRules: {
    args: WafRule[]
    post: WafRule[]
  }
  ccRules: CcRule[]
  blockedIps: IpInfo[]
  abTests: AbTest[]
}
