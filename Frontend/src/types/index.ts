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
  httpConnections: number
  wsConnections: number
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
  actionSeconds: number
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

// 安全事件类型（与后端 SecurityEventType 枚举一致）
export type SecurityEventType = 'WafIntercept' | 'CcAttack' | 'BlacklistBlock' | 'GeoBlock' | 'CrawlerDetect' | 'RateLimit'

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

// 地理位置流量数据
export interface GeoTrafficItem {
  name: string
  value: number
}

export interface GeoTrafficStats {
  countryVisits: GeoTrafficItem[]
  countryIntercepts: GeoTrafficItem[]
  regionVisits: GeoTrafficItem[]
  regionIntercepts: GeoTrafficItem[]
}

// API 耗时统计
export interface ApiTimingStat {
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

// =============== 集群配置类型 ===============

export interface LbDestination {
  id: string
  address: string
  host: string
  port: number
  scheme: string
  metadata: Record<string, string>
  source: 'original' | 'patch'
}

export interface LbCluster {
  id: string
  loadBalancingPolicy: string
  destinations: LbDestination[]
  destinationCount: number
}

export interface LbPolicy {
  name: string
  label: string
}

// =============== 路由配置类型 ===============

export interface RouteMatch {
  path: string
  hosts: string[]
  methods: string[]
}

export interface RouteConfig {
  routeId: string
  clusterId: string
  order: number
  match: RouteMatch
  source: 'original' | 'patch'
}

// =============== 文件服务配置类型 ===============

export interface FileServerItem {
  itemId: string
  prefix: string
  basePath: string
  browse: boolean
  preCompressed: boolean
  maxFileSize: number
  tryFiles: string[]
  defaultFiles: string[]
  source: 'original' | 'patch'
}

// =============== 简单响应配置类型 ===============

export interface SimpleResItem {
  itemId: string
  body: string
  contentType: string
  statusCode: number
  charset: string
  showReq: boolean
  headers: Record<string, string>
  source: 'original' | 'patch'
}

// =============== 配置概览类型 ===============

export interface ListenBoundRoute {
  routeId: string
  clusterId: string
  path: string
  hosts: string[]
  serviceType: 'proxy' | 'fileserver' | 'simpleres'
  source: 'original' | 'patch'
}

export interface ListenInfo {
  host: string
  port: number
  isHttps: boolean
  autoHttpsPort: number | null
  routes: ListenBoundRoute[]
  source: 'original' | 'patch'
}

export interface CertInfo {
  host: string
  pemFile: string
  hasKey: boolean
}

export interface OverviewClusterDest {
  id: string
  address: string
}

export interface OverviewCluster {
  id: string
  policy: string
  destinationCount: number
  destinations: OverviewClusterDest[]
}

export interface OverviewData {
  success: boolean
  listens: ListenInfo[]
  controlListen: { host: string; port: number }
  certs: CertInfo[]
  clusters: OverviewCluster[]
  timestamp: string
}

// =============== WAF 自定义规则类型 ===============

export type WafMatchField = 'UriPath' | 'FullUrl' | 'QueryString' | 'Method'
  | 'ClientIp' | 'XForwardedFor' | 'UserAgent' | 'Referer'
  | 'ContentType' | 'ContentLength' | 'Cookie' | 'Header'
  | 'QueryParam' | 'Body' | 'ServerPort'

export type WafMatchOperator = 'Equal' | 'NotEqual' | 'Contains' | 'NotContains'
  | 'StartsWith' | 'EndsWith' | 'Regex' | 'Exists' | 'NotExists'
  | 'LengthGreaterThan' | 'LengthLessThan'

export type WafRuleAction = 'Observe' | 'Block' | 'Reject' | 'Captcha'

export type WafRuleSource = 'User' | 'Config' | 'System'

export interface WafCondition {
  field: WafMatchField
  fieldName?: string
  operator: WafMatchOperator
  value: string
  ignoreCase: boolean
}

export interface WafConditionGroup {
  conditions: WafCondition[]
}

export interface WafCustomRule {
  id: string
  name: string
  description: string
  enabled: boolean
  priority: number
  source?: WafRuleSource
  conditionGroups: WafConditionGroup[]
  action: WafRuleAction
  actionSeconds: number
  responseCode: number
  createdAt: string
  updatedAt?: string
}

// =============== 通用设置类型 ===============

// 证书详情
export interface CertDetail {
  domain: string
  type: 'acme' | 'uploaded'
  issuer: string
  notAfter: string
  pemFile: string
  usedBy: string[]
}

// 审计日志条目
export interface AuditLogEntry {
  username: string
  action: string
  ip: string
  timestamp: string
}

// 控制台信息
export interface ConsoleInfo {
  username: string
  lastLoginAt: string | null
  controlListen: { host: string; port: number }
}

// =============== 统计配置类型 ===============

export interface StatisticConfig {
  whitePaths: string[]
  pathStas: string[]
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
