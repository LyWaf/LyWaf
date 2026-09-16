import { pluginProxyApi, getAllManagementPages, type ManagementPage } from '@/api'
import type { ApiResponse } from '@/types'

const api = pluginProxyApi('analysis')

function duration(seconds: number): string {
  if (!Number.isInteger(seconds) || seconds <= 0 || seconds > 2147483647) throw new Error('封禁时间必须为有效的正整数秒数')
  const days = Math.floor(seconds / 86400)
  const time = [Math.floor(seconds / 3600) % 24, Math.floor(seconds / 60) % 60, seconds % 60]
    .map(value => String(value).padStart(2, '0')).join(':')
  return days ? `${days}.${time}` : time
}

// ==================== CC 防护 API ====================

export const ccApi = {
  addRule: (path: string, limitNum: number, period: number, fbTime: number) =>
    api.post<ApiResponse>('cc/rules/add', { path, limitNum, period, fbTime: duration(fbTime) }),
    
  removeRule: (path: string) =>
    api.post<ApiResponse>('cc/rules/remove', { path }),
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
  groupOperator?: 'And' | 'Or'
  conditionGroups?: AdvancedCcRule['conditionGroups']
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
  getRules: async (): Promise<AdvancedCcRulesResponse> => {
    const rules = await getAllManagementPages<AdvancedCcRule>('cc/advanced', api)
    return { success: true, count: rules.length, rules }
  },
  list: (page = 1, search = '') => api.get<ManagementPage<AdvancedCcRule>>('cc/advanced', { params: { page, pageSize: 20, search } }),
    
  // 按类型获取规则
  getRulesByType: async (type: string): Promise<AdvancedCcRulesResponse> => {
    const rules = await getAllManagementPages<AdvancedCcRule>(`cc/advanced/type/${encodeURIComponent(type)}`, api)
    return { success: true, count: rules.length, rules }
  },
    
  // 获取单个规则
  getRule: (ruleId: string) =>
    api.get<AdvancedCcRuleResponse>(`cc/advanced/${encodeURIComponent(ruleId)}`),
    
  // 添加规则
  addRule: (rule: CreateAdvancedCcRuleRequest) =>
    api.post<ApiResponse & { ruleId: string }>('cc/advanced/add', rule),
    
  // 更新规则
  updateRule: (rule: CreateAdvancedCcRuleRequest & { id: string }) =>
    api.post<ApiResponse & { ruleId: string }>('cc/advanced/update', rule),
    
  // 删除规则
  removeRule: (ruleId: string) =>
    api.post<ApiResponse>('cc/advanced/remove', { ruleId }),
    
  // 启用/禁用规则
  toggleRule: (ruleId: string, enabled?: boolean) =>
    api.post<ApiResponse & { enabled: boolean }>('cc/advanced/toggle', { ruleId, enabled }),
    
  // 获取枚举值
  getEnums: () =>
    api.get<CcEnumsResponse>('cc/advanced/enums'),
}

// ==================== 统计配置 API ====================

import type { StatisticConfig } from '@/types'

export const statisticApi = {
  getConfig: () =>
    api.get<StatisticConfig & { success: boolean }>('statistic/config'),

  addWhitePath: (path: string) =>
    api.post<ApiResponse>('statistic/white-paths/add', { path }),

  removeWhitePath: (path: string) =>
    api.post<ApiResponse>('statistic/white-paths/remove', { path }),

  addPathSta: (path: string) =>
    api.post<ApiResponse>('statistic/path-stas/add', { path }),

  removePathSta: (path: string) =>
    api.post<ApiResponse>('statistic/path-stas/remove', { path }),
}
