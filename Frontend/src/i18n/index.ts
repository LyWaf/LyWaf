import { watch } from 'vue'
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
export function fieldLabel(path: string, fallback?: string, pluginId?: string, labelKey?: string) {
  if (labelKey && i18n.global.te(labelKey)) return t(labelKey)
  const name = path.split(':').slice(-1)[0]
  const specific = `plugins.${pluginId}.${name}.label`
  if (i18n.global.te(specific)) return t(specific)
  const key = `fields.${name}`
  return i18n.global.te(key) ? t(key) : translateText(fallback || name)
}
export function fieldDescription(key?: string) {
  return key && i18n.global.te(key) ? t(key) : ''
}
export const formatNumber = (value: number) => new Intl.NumberFormat(locale.value).format(value)
export const formatDateTime = (value: string | number | Date) => {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '-' : new Intl.DateTimeFormat(locale.value, { dateStyle: 'short', timeStyle: 'medium' }).format(date)
}
watch(locale, value => { document.documentElement.lang = value }, { immediate: true })
window.addEventListener('storage', event => {
  if (event.key === 'lywaf_locale') locale.value = normalizeLocale(event.newValue)
})
