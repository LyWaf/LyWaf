import test from 'node:test'
import assert from 'node:assert/strict'
import { fileURLToPath } from 'node:url'
import { scan } from '../scripts/audit-i18n.mjs'
import { readFileSync, readdirSync } from 'node:fs'
import { resolve } from 'node:path'
import ts from 'typescript'
import { parse } from '@vue/compiler-sfc'
import { baseCompile } from '@intlify/message-compiler'

const root = fileURLToPath(new URL('../src', import.meta.url))
const files = dir => readdirSync(dir, { withFileTypes: true }).flatMap(e => e.isDirectory() ? files(resolve(dir, e.name)) : [resolve(dir, e.name)])
test('host and plugin page catalogs have valid messages and matching interpolation parameters', () => {
  for (const dir of [resolve(root, '../../Localization'), resolve(root, 'plugins/analysis/locales'), resolve(root, 'plugins/web-tamper/locales')]) {
    const zh = JSON.parse(readFileSync(resolve(dir, 'zh-CN.json'), 'utf8'))
    const en = JSON.parse(readFileSync(resolve(dir, 'en-US.json'), 'utf8'))
    assert.deepEqual(Object.keys(zh).sort(), Object.keys(en).sort(), dir)
    for (const key of Object.keys(zh)) {
      assert.ok(!/\p{Script=Han}/u.test(en[key]), `${dir}: ${key}`)
      for (const message of [zh[key], en[key]]) baseCompile(message, { onError: error => { throw new Error(`${dir}: ${key}: ${error.message}`) } })
      const params = value => [...value.matchAll(/\{([a-zA-Z]\w*)\}/g)].map(m => m[1]).sort()
      assert.deepEqual(params(zh[key]), params(en[key]), key)
    }
  }
})
test('all frontend views and dialogs keep Chinese in catalogs or explicit protocol data only', () => {
  for (const file of files(root).filter(f => f.endsWith('.vue'))) {
    const source = readFileSync(file, 'utf8')
    const findings = scan(file).filter(hit => {
      if (file.endsWith('LanguageSelect.vue') && hit.text === '简体中文') return false
      if (file.endsWith('GeneralSettings.vue') && ['异常退出', '正常关闭'].includes(hit.text)) return false
      if (file.endsWith('Sidebar.vue')) return false // Legacy menu labels pass through translateText.
      if (file.endsWith('GeoAccess.vue') && /code:\s*$/.test(source.slice(hit.start - 10, hit.start))) return false
      return true
    })
    assert.deepEqual(findings, [], file)
  }
})

test('every static translation reference resolves in its own bilingual catalog', () => {
  const catalog = owner => JSON.parse(readFileSync(owner === 'host' ? resolve(root, '../../Localization/zh-CN.json') : resolve(root, `plugins/${owner}/locales/zh-CN.json`), 'utf8'))
  for (const file of files(root).filter(f => f.endsWith('.vue'))) {
    const source = readFileSync(file, 'utf8')
    const owner = source.match(/import \{ t \} from '@\/plugins\/([^/]+)\/i18n'/)?.[1] || 'host'
    const messages = catalog(owner)
    for (const match of source.matchAll(/\bt\('([^']+)'/g)) assert.ok(Object.hasOwn(messages, match[1]), `${file}: ${match[1]}`)
    const script = parse(source).descriptor.scriptSetup?.content
    if (!script) continue
    const ast = ts.createSourceFile(file, script, ts.ScriptTarget.Latest, true)
    for (const statement of ast.statements) if (ts.isVariableStatement(statement)) {
      for (const d of statement.declarationList.declarations) if (d.initializer && (ts.isArrayLiteralExpression(d.initializer) || ts.isObjectLiteralExpression(d.initializer))) {
        assert.ok(!/\bt\(/.test(d.initializer.getText(ast)), `${file}: ${d.name.getText(ast)} must react to locale changes`)
      }
    }
  }
})

test('security and management pages contain no hardcoded Chinese display text', () => {
  for (const name of ['Security', 'Notifications', 'Backups', 'SystemHealth', 'ManagementTasks', 'ApiTiming', 'Login']) {
    assert.deepEqual(scan(fileURLToPath(new URL(`../src/views/${name}.vue`, import.meta.url))), [], name)
  }
})

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
