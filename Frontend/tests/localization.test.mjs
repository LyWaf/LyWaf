import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import vm from 'node:vm'
import ts from 'typescript'

const require = createRequire(import.meta.url)
function runtime(saved = null) {
  let stored = saved
  const document = { documentElement: { lang: '' } }
  const module = { exports: {} }
  const source = readFileSync(new URL('../src/i18n/index.ts', import.meta.url), 'utf8')
  const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 } }).outputText
  vm.runInNewContext(code, {
    module, exports: module.exports, require: name => name.endsWith('.json') ? { default: read(name.includes('zh-CN') ? 'zh-CN' : 'en-US') } : require(name),
    document, window: { addEventListener() {} },
    localStorage: { getItem: () => stored, setItem: (_key, value) => { stored = value } },
  })
  return { api: module.exports, document, stored: () => stored }
}

const read = language => JSON.parse(readFileSync(new URL(`../../Localization/${language}.json`, import.meta.url), 'utf8'))
const pluginRead = (plugin, language) => JSON.parse(readFileSync(new URL(`../../Plugins/Sys/${plugin}/Localization/${language}.json`, import.meta.url), 'utf8'))

test('node errors follow the local language instead of the remote exception text', () => {
  const { api } = runtime('en-US')
  const module = { exports: {} }
  const source = readFileSync(new URL('../src/i18n/node-error.ts', import.meta.url), 'utf8')
  const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText
  vm.runInNewContext(code, { module, exports: module.exports, require: () => api })
  const node = { errorCode: 'node_timeout', error: 'remote language and private details' }
  assert.equal(module.exports.nodeErrorText(node), 'The node request timed out')
  api.setLocale('zh-CN')
  assert.equal(module.exports.nodeErrorText(node), read('zh-CN')['node.errors.node_timeout'])
  assert.equal(module.exports.nodeErrorText({ errorCode: 'future_unknown_code', error: 'private' }),
    read('zh-CN')['node.errors.node_http_rejected'])
  assert.equal(module.exports.nodeErrorText({ error: 'legacy error' }), 'legacy error')
  assert.equal(module.exports.nodeLifecycleText({ lifecycle: 'AuthenticationFailed', online: false, stale: false }),
    read('zh-CN')['node.lifecycle.AuthenticationFailed'])
  api.setLocale('en-US')
  assert.equal(module.exports.nodeLifecycleText({ lifecycle: 'AuthenticationFailed', online: false, stale: false }),
    'Authentication failed')
})

test('dashboard durations change language without fetching data again', () => {
  const { api } = runtime('en-US')
  assert.equal(api.formatDuration(75239), '20 h 53 min')
  assert.equal(api.formatDuration(59), '59 s')
  assert.equal(api.formatDuration(undefined), '-')
  assert.equal(api.formatDuration(NaN), '-')
  api.setLocale('zh-CN')
  assert.equal(api.formatDuration(75239), '20 小时 53 分')
  assert.equal(api.formatDuration(0), '0 秒')
})
test('shared language catalogs have matching keys and interpolation parameters', () => {
  const zh = read('zh-CN'), en = read('en-US')
  assert.deepEqual(Object.keys(zh).sort(), Object.keys(en).sort())
  for (const key of Object.keys(zh)) {
    assert.ok(en[key].trim(), key)
    assert.deepEqual(zh[key].match(/\{\w+\}/g) || [], en[key].match(/\{\w+\}/g) || [], key)
  }
})
test('upload configuration display names cover every field without renaming keys', () => {
  const zh = pluginRead('HttpUpload', 'zh-CN'), en = pluginRead('HttpUpload', 'en-US')
  for (const field of ['Enabled','Path','Hosts','RequireHttps','Key','SignatureWindowSeconds','NonceDirectory','Directory','AllowZip','MaxUploadBytes','MaxExtractedBytes','MaxEntries']) {
    assert.ok(zh[`fields.${field}`], field)
    assert.ok(en[`fields.${field}`], field)
  }
})
test('language changes update labels, persist preference and preserve unknown values', async () => {
  const { api, document, stored } = runtime('invalid-language')
  assert.equal(api.locale.value, 'zh-CN')
  assert.equal(api.fieldLabel('Enabled'), read('zh-CN')['fields.Enabled'])
  api.setLocale('en-GB')
  await require('vue').nextTick()
  assert.equal(document.documentElement.lang, 'en-US')
  assert.equal(stored(), 'en-US')
  assert.equal(api.fieldLabel('Enabled'), 'Enabled')
  api.registerPluginMessages('http-upload', { 'zh-CN': pluginRead('HttpUpload', 'zh-CN'), 'en-US': pluginRead('HttpUpload', 'en-US') })
  assert.equal(api.fieldLabel('Directory', undefined, 'http-upload'), pluginRead('HttpUpload', 'en-US')['plugins.http-upload.Directory.label'])
  assert.equal(api.fieldLabel('CustomField', 'Custom label'), 'Custom label')
  assert.equal(api.translateText('/data/中文'), '/data/中文')
  assert.ok(api.t('login.wait', { seconds: 12 }).includes('12'))
})
test('site authentication and OAuth2 configuration fields have translations', () => {
  const source = readFileSync(new URL('../../Plugins/Sys/SiteAuth/SiteAuthMiddleware.cs', import.meta.url), 'utf8').split('public sealed record SiteIdentity')[0]
  for (const match of source.matchAll(/public\s+[\w<>]+\s+(\w+)\s*\{\s*get;/g)) {
    for (const language of ['zh-CN', 'en-US']) assert.ok(pluginRead('SiteAuth', language)[`fields.${match[1]}`], `${language}: ${match[1]}`)
  }
})
test('plugin catalogs are isolated, replaced on reload, and cannot overwrite host messages', () => {
  const { api } = runtime('en-US')
  api.registerPluginMessages('one', { 'zh-CN': { 'fields.Custom': 'One', 'common.save': 'Override' } })
  api.registerPluginMessages('two', { 'en-US': { 'fields.Custom': 'Two' } })
  assert.equal(api.fieldLabel('Custom', undefined, 'one'), 'One')
  assert.equal(api.fieldLabel('Custom', undefined, 'two'), 'Two')
  assert.equal(api.t('common.save'), 'Save')
  api.registerPluginMessages('one', {})
  assert.equal(api.fieldLabel('Custom', undefined, 'one'), 'Custom')
  assert.equal(read('zh-CN')['fields.CookieName'], undefined)
  for (const plugin of ['HttpUpload', 'SiteAuth']) assert.deepEqual(Object.keys(pluginRead(plugin, 'zh-CN')).sort(), Object.keys(pluginRead(plugin, 'en-US')).sort())
})
