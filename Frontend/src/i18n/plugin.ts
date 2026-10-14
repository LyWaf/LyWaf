import { createI18n } from 'vue-i18n'
import { watch } from 'vue'
import { locale } from './index'

export function createPluginTranslator(messages: Record<'zh-CN' | 'en-US', Record<string, string>>) {
  const instance = createI18n({
    legacy: false, locale: locale.value, fallbackLocale: 'zh-CN',
    missingWarn: false, fallbackWarn: false,
    messageResolver: (catalog, key) => Object.prototype.hasOwnProperty.call(catalog, key)
      ? (catalog as Record<string, string>)[key] : null,
    messages,
  })
  watch(locale, value => { instance.global.locale.value = value }, { flush: 'sync' })
  return (key: string, values: Record<string, string | number> = {}) => instance.global.t(key, values)
}
