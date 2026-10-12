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
test('shared language catalogs have matching keys and interpolation parameters', () => {
  const zh = read('zh-CN'), en = read('en-US')
  assert.deepEqual(Object.keys(zh).sort(), Object.keys(en).sort())
  for (const key of Object.keys(zh)) {
    assert.ok(en[key].trim(), key)
    assert.deepEqual(zh[key].match(/\{\w+\}/g) || [], en[key].match(/\{\w+\}/g) || [], key)
  }
})
test('upload configuration display names cover every field without renaming keys', () => {
  const zh = read('zh-CN'), en = read('en-US')
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
  assert.equal(api.fieldLabel('Directory', undefined, 'http-upload'), read('en-US')['plugins.http-upload.Directory.label'])
  assert.equal(api.fieldLabel('CustomField', 'Custom label'), 'Custom label')
  assert.equal(api.translateText('/data/中文'), '/data/中文')
  assert.ok(api.t('login.wait', { seconds: 12 }).includes('12'))
})
test('site authentication and OAuth2 configuration fields have translations', () => {
  const source = readFileSync(new URL('../../Plugins/Sys/SiteAuth/SiteAuthMiddleware.cs', import.meta.url), 'utf8').split('public sealed record SiteIdentity')[0]
  for (const match of source.matchAll(/public\s+[\w<>]+\s+(\w+)\s*\{\s*get;/g)) {
    for (const language of ['zh-CN', 'en-US']) assert.ok(read(language)[`fields.${match[1]}`], `${language}: ${match[1]}`)
  }
})
