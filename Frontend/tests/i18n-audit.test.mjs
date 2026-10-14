import test from 'node:test'
import assert from 'node:assert/strict'
import { fileURLToPath } from 'node:url'
import { scan } from '../scripts/audit-i18n.mjs'

test('reports and child workflows have no hardcoded Chinese display text', () => {
  for (const file of ['views/Dashboard.vue', ...['TrafficAnalysis', 'RecentClients', 'GeoTrafficMap', 'BlockedIps'].map(name => `components/dashboard/${name}.vue`)]) {
    assert.deepEqual(scan(fileURLToPath(new URL(`../src/${file}`, import.meta.url))), [], file)
  }
})

test('general settings has no hardcoded Chinese display text', () => {
  const findings = scan(fileURLToPath(new URL('../src/views/GeneralSettings.vue', import.meta.url)))
  // Legacy API enum values are compared unchanged, and translated only when displayed.
  assert.deepEqual(findings.map(finding => finding.text).sort(), ['异常退出', '正常关闭'].sort())
})
test('shared controls and MFA have no hardcoded Chinese display text', () => {
  for (const file of ['components/ManagementMfa.vue', 'components/common/ListToolbar.vue', 'components/common/DataTable.vue', 'components/common/FeatureSwitch.vue', 'components/common/DateTimePicker.vue', 'components/Layout/Layout.vue', 'components/Layout/Topbar.vue']) {
    assert.deepEqual(scan(fileURLToPath(new URL(`../src/${file}`, import.meta.url))), [], file)
  }
})
