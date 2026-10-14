import { locale, t } from '@/i18n'
import { countryNameMap } from './geoNameMap'
import provinces from './provinceNames.json'

// Keep map identifiers unchanged; localize only their displayed labels.
export function geoLabel(name: string): string {
  const province = (provinces as Record<string, string>)[name]
  if (province) return t(`geo.province.${province}`)
  if (locale.value === 'en-US') return countryNameMap[name] || name
  return Object.entries(countryNameMap).find(([, english]) => english === name)?.[0] || name
}
