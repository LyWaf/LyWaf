import { ref, watch } from 'vue'
import { createI18n } from 'vue-i18n'
import zh from '../../../Localization/zh-CN.json'
import en from '../../../Localization/en-US.json'

export function normalizeLocale(value: string | null): 'zh-CN' | 'en-US' {
  return /^en(?:[-_]|$)/i.test(value || '') ? 'en-US' : 'zh-CN'
}
let initial = 'zh-CN'
try { initial = localStorage.getItem('lywaf_locale') || initial } catch { /* Storage can be disabled. */ }
export const i18n = createI18n({
  legacy: false, locale: normalizeLocale(initial), fallbackLocale: 'zh-CN',
  missingWarn: false, fallbackWarn: false,
  messageResolver: (messages, key) => Object.prototype.hasOwnProperty.call(messages, key)
    ? (messages as Record<string, string>)[key] : null,
  messages: { 'zh-CN': zh, 'en-US': en },
})
export const locale = i18n.global.locale
export const t = (key: string, values: Record<string, string | number> = {}) => i18n.global.t(key, values)
export function setLocale(value: string) {
  locale.value = normalizeLocale(value)
  try { localStorage.setItem('lywaf_locale', locale.value) } catch { /* Keep the in-memory preference. */ }
}
// Legacy display labels only; never apply this to configuration keys or user data.
const displayKeys = new Map(Object.entries(zh).filter(([, value]) => !value.includes('{')).map(([key, value]) => [value, key]))
export const translateText = (text: string) => displayKeys.has(text) ? t(displayKeys.get(text)!) : text
const pluginResources = ref<Record<string, Record<string, Record<string, string>>>>({})
export function registerPluginMessages(pluginId: string, resources: Record<string, Record<string, string>> = {}) {
  // Replace instead of merging so removed keys do not survive a plugin update.
  pluginResources.value = { ...pluginResources.value, [pluginId]: resources }
}
function pluginText(pluginId: string | undefined, key: string) {
  const resources = pluginId ? pluginResources.value[pluginId] : undefined
  for (const language of [locale.value, 'zh-CN']) {
    const messages = resources?.[language]
    if (messages && Object.prototype.hasOwnProperty.call(messages, key)) return messages[key]
  }
  return undefined
}
export function fieldLabel(path: string, fallback?: string, pluginId?: string, labelKey?: string) {
  const name = path.split(':').slice(-1)[0]
  const pluginLabel = (labelKey ? pluginText(pluginId, labelKey) : undefined)
    ?? pluginText(pluginId, `plugins.${pluginId}.${name}.label`)
    ?? pluginText(pluginId, `fields.${name}`)
  if (pluginLabel !== undefined) return pluginLabel
  if (labelKey && i18n.global.te(labelKey)) return t(labelKey)
  const specific = `plugins.${pluginId}.${name}.label`
  const specificLabel = pluginText(pluginId, specific)
  if (specificLabel !== undefined) return specificLabel
  const key = `fields.${name}`
  return i18n.global.te(key) ? t(key) : translateText(fallback || name)
}
export function fieldDescription(key?: string, pluginId?: string) {
  return key ? pluginText(pluginId, key) ?? (i18n.global.te(key) ? t(key) : '') : ''
}
export const formatNumber = (value: number) => new Intl.NumberFormat(locale.value).format(value)
export function formatDuration(value?: number) {
  if (value === undefined || !Number.isFinite(value)) return '-'
  const total = Math.max(0, Math.floor(value))
  const days = Math.floor(total / 86400), hours = Math.floor(total % 86400 / 3600)
  const minutes = Math.floor(total % 3600 / 60), seconds = total % 60
  if (days) return t('duration.daysHours', { days, hours })
  if (hours) return t('duration.hoursMinutes', { hours, minutes })
  if (minutes) return t('duration.minutesSeconds', { minutes, seconds })
  return t('duration.seconds', { count: seconds })
}
export const formatDateTime = (value: string | number | Date) => {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '-' : new Intl.DateTimeFormat(locale.value, { dateStyle: 'short', timeStyle: 'medium' }).format(date)
}
watch(locale, value => { document.documentElement.lang = value }, { immediate: true })
window.addEventListener('storage', event => {
  if (event.key === 'lywaf_locale') locale.value = normalizeLocale(event.newValue)
})
