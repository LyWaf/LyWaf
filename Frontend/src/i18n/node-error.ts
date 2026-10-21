import { i18n, t } from './index'

export function nodeErrorText(node: { errorCode?: string | null; error?: string | null }) {
  const code = node.errorCode || (/^node_[a-z_]+$/.test(node.error || '') ? node.error : null)
  if (code) {
    const key = `node.errors.${code}`
    return i18n.global.te(key) ? t(key) : t('node.errors.node_http_rejected')
  }
  return node.error || ''
}

export function nodeLifecycleText(node: { lifecycle?: string; online: boolean; stale: boolean }) {
  const key = `node.lifecycle.${node.lifecycle}`
  return node.lifecycle && i18n.global.te(key) ? t(key)
    : node.online ? t('page.b9086662b1df') : node.stale ? t('page.a00963a24f1b') : t('page.be1b4f3c6c1c')
}

export function nodeAdmissionText(node: { admissionState?: string; admissionEnabled?: boolean }) {
  const key = `node.admission.${node.admissionState ?? 'Legacy'}`
  const state = i18n.global.te(key) ? t(key) : t('node.admission.Unknown')
  return node.admissionEnabled === false ? `${state} (${t('node.admission.Disabled')})` : state
}
