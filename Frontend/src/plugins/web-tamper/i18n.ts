import { createPluginTranslator } from '@/i18n/plugin'
import zh from './locales/zh-CN.json'
import en from './locales/en-US.json'

export const t = createPluginTranslator({ 'zh-CN': zh, 'en-US': en })
