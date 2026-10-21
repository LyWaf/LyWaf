// API 响应类型
export interface ApiResponse<T = unknown> {
  success: boolean
  message?: string
  data?: T
  error?: {
    code: string
    message: string
    traceId: string
    details?: unknown
  }
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
  uptimeSeconds?: number
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
  scope?: 'SiteGroup' | 'Node' | 'Cluster'
  subject?: 'Ip' | 'Account' | 'ApiCredential'
  subjectAuthenticationType?: string
  subjectIssuer?: string
  expectedNodes?: string[]
  useManagedMembership?: boolean
  requireCompleteCoverage?: boolean
  protectionGroup?: string
  groupOperator?: 'And' | 'Or'
  conditionGroups?: Array<{ operator: 'And' | 'Or'; conditions: CcCondition[] }>
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

export interface OutlierDestination {
  clusterId: string
  destinationId: string
  address: string
  state: 'available' | 'ejected'
  reason?: string
  ejectedUntil?: string
  consecutiveFailures: number
  consecutive5xx: number
  windowRequests: number
  windowFailures: number
  errorRate: number
}

// =============== 多节点控制台类型 ===============

export interface NodeUpstreamSnapshot {
  clusterId: string
  destinationId: string
  address: string
  activeHealth: string
  passiveHealth: string
  concurrentRequests: number
}

export interface NodeSecuritySnapshot {
  totalInterceptCount: number
  wafInterceptCount: number
  blacklistBlockCount: number
  ccAttackCount: number
  crawlerDetectCount: number
  geoBlockCount: number
  rateLimitCount: number
  uniqueAttackIps: number | null
}

export interface NodeSecurityEvent {
  bootId?: string | null
  eventId: string
  sourceIp: string
  application: string
  ruleName: string
  ruleType: string
  hitCount: number
  lastHitAt: string
}

export interface NodeDashboardSnapshot {
  bootId?: string | null
  bootSource?: string | null
  diagnostics?: NodeLocalDiagnostics | null
  telemetry?: NodeTelemetryWindow[] | null
  statisticsStartedAt?: string | null
  nodeId: string
  applicationVersion: string
  configVersionId: string | null
  state: string
  timestamp: string
  uptimeSeconds: number
  runtimeStateEntries: number
  security: NodeSecuritySnapshot
  upstreams: NodeUpstreamSnapshot[]
  recentEvents: NodeSecurityEvent[]
}

export interface ManagedNodeSnapshot {
  diagnostics?: NodePeerDiagnostics | null
  admissionState?: string
  admissionEnabled?: boolean
  healthyActive?: boolean
  lifecycle?: string
  consecutiveFailures?: number
  firstAttemptAt?: string | null
  nodeId: string
  endpoint: string
  tags: string[]
  online: boolean
  stale: boolean
  clockSkewSeconds: number | null
  clockSkewWarning: boolean
  lastSeenAt: string | null
  error: string | null
  errorCode?: string | null
  remoteTraceId?: string | null
  canOperate: boolean
  snapshot: NodeDashboardSnapshot | null
}

export interface MultiNodeDashboard {
  telemetry?: NodeTelemetryAggregate | null
  healthyActiveNodes?: number
  coverage?: {
    expectedNodes: number
    includedNodes: number
    complete: boolean
    missingNodes: string[]
    oldestSnapshotAt: string | null
    statisticsPeriodsKnown: boolean
  }
  generatedAt: string
  canOperateGlobally: boolean
  nodes: ManagedNodeSnapshot[]
  security: NodeSecuritySnapshot
  recentEvents: NodeSecurityEvent[]
}

export interface NodeTelemetryWindow {
  bootId: string
  schema: string
  start: string
  end: string
  watermark: string
  complete: boolean
  requests: number
  errors: number
  intercepts: number
  boundsMilliseconds: number[]
  counts: number[]
}

export interface NodeTelemetryAggregate {
  sourceGenerations?: Record<string, string>
  visitors?: NodeSetMetric | null
  logicalBans?: NodeSetMetric | null
  schema: string
  start: string
  end: string
  expectedSetVersion: string
  status: 'Complete' | 'Partial'
  includedNodes: string[]
  missingNodes: Record<string, string>
  watermark: string | null
  requests: number
  errors: number
  intercepts: number
  errorRate: number | null
  interceptRate: number | null
  p95Milliseconds: number | null
  p99Milliseconds: number | null
  quantilesApproximate: boolean
  quantileMethod: string
  boundsMilliseconds: number[]
  counts: number[]
}

export interface NodeSetMetric {
  sourceGenerations?: Record<string, string>
  status: 'Complete' | 'Partial'
  count: number | null
  observedCount: number
  includedNodes: string[]
  missingNodes: Record<string, string>
  method: string
  final: boolean
  observedAt?: string | null
}

export interface NodeLocalDiagnostics {
  communication: {
    startedAt: string
    capturedAt: string
    scope: string
    series: { operation: string; direction: string; requests: number; failed: number; rejected: number;
      requestBytes: number; responseBytes: number; unknownSizes: number; totalMilliseconds: number; durationCounts: number[] }[]
    events: Record<string, number>
    durationBoundsMilliseconds: number[]
    replicationAcknowledgement?: { samples: number; totalMilliseconds: number; maxMilliseconds: number; clockSkewSamples: number; semantics: string } | null
  }
  replicationQueue: { entries: number; bytes: number; coalesced: number; capacityDropped: number;
    oversizeDropped: number; closedDropped: number; contentionBuffered: number; authorizationRejected: number } | null
}

export interface NodePeerDiagnostics {
  bootId: string | null
  lastHandshakeAt: string | null
  reconciliationState: string | null
  expectedSourceVersion: string | null
  acknowledgedAppliedVersion: string | null
  actualAppliedVersion: string | null
  actualFileVersion: string | null
  pendingCommands: number | null
  unknownCommands: number | null
  retryQueries: number | null
  oldestPendingMilliseconds: number | null
  error: string | null
  capturedAt: string
}

export interface DistributionTargetState {
  nodeId: string
  state: string
  updatedAt: string
  configVersionId: string | null
  error: string | null
}

export interface ConfigurationDeployment {
  taskId?: string | null
  id: string
  versionId: string
  createdAt: string
  state: string
  nodeOrder: string[]
  targets: Record<string, DistributionTargetState>
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

export type WafLogicalOperator = 'And' | 'Or'

export interface WafConditionGroup {
  operator: WafLogicalOperator
  conditions: WafCondition[]
}

export interface WafCustomRule {
  id: string
  name: string
  description: string
  enabled: boolean
  priority: number
  source?: WafRuleSource
  groupOperator: WafLogicalOperator
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

export interface CertificateRuntimeStatus {
  domain: string
  expiresAt: string
  daysRemaining: number
  source: 'acme' | 'internal' | 'file'
  status: 'unknown' | 'valid' | 'revoked' | 'error' | 'expired'
  statusCheckedAt?: string
}

export interface AcmeAccountStatus {
  directoryUrl: string
  accountKeyExists: boolean
  email?: string
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
    args: string[]
    post: string[]
    argsFileCount: number
    postFileCount: number
  }
  ccRules: CcRule[]
  blockedIps: IpInfo[]
  abTests: AbTest[]
}
