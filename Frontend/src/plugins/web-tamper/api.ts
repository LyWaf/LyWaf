import { pluginProxyApi } from '@/api'

export interface WebTamperResource {
  id: string
  name: string
  website: string
  path: string
  url: string
  enabled: boolean
  contentType: string
  baselineHash: string
  currentHash?: string
  createdAt: string
  updatedAt: string
  lastCheckedAt?: string
  changed: boolean
  isPattern: boolean
  parentResourceId?: string
  capturedCount: number
  capturedResources?: WebTamperResource[]
}

export interface WebTamperEvent {
  id: string
  resourceId: string
  resourceName: string
  url: string
  baselineHash: string
  currentHash: string
  detectedAt: string
  resolved: boolean
  resolvedAt?: string
}

export interface WebTamperComparison {
  resource: WebTamperResource
  changed: boolean
  baselineContent: string
  currentContent: string
}

export interface WebTamperWebsite {
  id: string
  name: string
  host: string
  domain: string
  source: 'config' | 'manual'
  removable: boolean
}

const api = pluginProxyApi('web-tamper')

export const webTamperApi = {
  getWebsites: () => api.get<{ success: boolean; websites: WebTamperWebsite[] }>('websites'),
  addWebsite: (name: string, domain: string, port?: number) => api.post<{ success: boolean; website?: WebTamperWebsite; message?: string }>('websites', { name, origin: domain, port }),
  renameWebsite: (name: string, domain: string, source: 'config' | 'manual') => api.put<{ success: boolean; message?: string }>('websites/name', { name, origin: domain, source }),
  deleteWebsite: (id: string) => api.delete<{ success: boolean; message?: string }>(`websites/${encodeURIComponent(id)}`),
  getResources: () => api.get<{ success: boolean; resources: WebTamperResource[] }>('resources'),
  getEvents: () => api.get<{ success: boolean; events: WebTamperEvent[] }>('events'),
  add: (website: string, path: string) => api.post<{ success: boolean; resource?: WebTamperResource; message?: string }>('resources', { website, path }),
  update: (id: string, name: string, enabled: boolean) => api.put<{ success: boolean; message?: string }>(`resources/${encodeURIComponent(id)}`, { name, enabled }),
  delete: (id: string) => api.delete<{ success: boolean; message?: string }>(`resources/${encodeURIComponent(id)}`),
  check: (id: string) => api.post<{ success: boolean; comparison?: WebTamperComparison; message?: string }>(`resources/${encodeURIComponent(id)}/check`, undefined, { timeout: 60000 }),
  accept: (id: string) => api.post<{ success: boolean; comparison?: WebTamperComparison; message?: string }>(`resources/${encodeURIComponent(id)}/accept`),
}
