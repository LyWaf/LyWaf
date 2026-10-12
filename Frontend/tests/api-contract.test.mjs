import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { test } from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'

const source = await readFile(new URL('../src/api/index.ts', import.meta.url), 'utf8')
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText
const analysisSource = await readFile(new URL('../src/plugins/analysis/api.ts', import.meta.url), 'utf8')
const analysisCompiled = ts.transpileModule(analysisSource, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText

function clientFixture(response) {
  let rejectResponse
  let retryConfig
  let prepareRequest
  const calls = []
  const client = {
    get(url, config) { calls.push({ url, config }); return Promise.resolve(response) },
    post(url, data, config) { calls.push({ url, data, config }); return Promise.resolve(response) },
    interceptors: {
      request: { use(handler) { prepareRequest = handler } },
      response: { use(_success, failure) { rejectResponse = failure } },
    },
    request(config) { retryConfig = config; return Promise.resolve({ success: true }) },
  }
  const module = { exports: {} }
  const context = {
    module, exports: module.exports,
    require(name) {
      if (name === 'axios') return { default: { create: () => client } }
      if (name === '@/i18n') return { locale: { value: 'en-US' } }
      if (name === '@/composables/useStepUp') return { requestStepUp: async () => 'proof' }
      throw new Error(`Unexpected runtime import: ${name}`)
    },
    localStorage: { removeItem() {}, getItem() { return 'session' } }, window: { location: { hash: '#/login' } },
    console: { error() { throw new Error('Do not log request credentials') } },
  }
  vm.runInNewContext(compiled, context)
  const plugin = { exports: {} }
  vm.runInNewContext(analysisCompiled, { module: plugin, exports: plugin.exports, require: name => {
    if (name === '@/api') return module.exports
    throw new Error(`Unexpected plugin import: ${name}`)
  } })
  return { prepareRequest, reject: error => rejectResponse(error), retry: () => retryConfig, api: module.exports, analysis: plugin.exports, calls }
}

test('management requests include the selected UI language without changing authentication', () => {
  const result = clientFixture({}).prepareRequest({ method: 'get', headers: {} })
  assert.equal(result.headers['Accept-Language'], 'en-US')
  assert.equal(result.headers.Authorization, 'Bearer session')
})

test('CC management uses the analysis proxy for reads, paging and writes', async () => {
  const fixture = clientFixture({ success: true, data: [], total: 0 })
  await fixture.analysis.advancedCcApi.list(2, 'rule')
  await fixture.analysis.advancedCcApi.getRules()
  await fixture.analysis.advancedCcApi.getRulesByType('FrequentAccess')
  await fixture.analysis.advancedCcApi.addRule({ name: 'test' })
  await fixture.analysis.statisticApi.getConfig()
  await fixture.analysis.statisticApi.addWhitePath('/health')
  await fixture.analysis.ccApi.addRule('/api', 10, 10, 90061)
  assert.ok(fixture.calls.every(call => call.url.startsWith('/plugins/analysis/proxy/')))
  assert.equal(fixture.calls[0].config.params.page, 2)
  assert.equal(fixture.calls[1].config.params.pageSize, 100)
  assert.equal(fixture.calls.at(-1).data.fbTime, '1.01:01:01')
  assert.equal(fixture.api.advancedCcApi, undefined)
})

test('plugin config preflight posts the candidate without calling the save endpoint', async () => {
  const fixture = clientFixture({ success: true })
  const candidate = { DefaultQuality: '85' }
  await fixture.api.pluginApi.validateConfig('image-process', candidate)
  assert.equal(fixture.calls.length, 1)
  assert.equal(fixture.calls[0].url, '/plugins/image-process/config/validate')
  assert.equal(fixture.calls[0].data, candidate)
})

test('timing list requests only the selected page and retains global aggregates', async () => {
  const response = { data: [], total: 10000, summary: { apiCount: 10000 }, backendStats: [{ backend: 'origin', apiCount: 10000 }] }
  const fixture = clientFixture(response)
  const result = await fixture.api.timingApi.getList({ page: 2, search: '/api' })
  assert.equal(fixture.calls.length, 1)
  assert.equal(fixture.calls[0].url, '/timing/list')
  assert.equal(fixture.calls[0].config.params.page, 2)
  assert.equal(fixture.calls[0].config.params.pageSize, 20)
  assert.equal(fixture.calls[0].config.params.search, '/api')
  assert.equal(result.total, 10000)
  assert.equal(result.summary, response.summary)
  assert.equal(result.backends, response.backendStats)
})

for (const [apiName, method, url] of [['paramApi', 'getEnv', '/param/env'], ['pluginApi', 'list', '/plugins'], ['settingsApi', 'getCerts', '/settings/certs'], ['authApi', 'rolePage', '/auth/roles']]) {
  test(`${url} requests one server-side page`, async () => {
    const response = { success: true, data: [], total: 10000 }
    const fixture = clientFixture(response)
    assert.equal(await fixture.api[apiName][method](3, 'test'), response)
    assert.equal(fixture.calls.length, 1)
    assert.equal(fixture.calls[0].url, url)
    assert.equal(fixture.calls[0].config.params.page, 3)
    assert.equal(fixture.calls[0].config.params.pageSize, 20)
    assert.equal(fixture.calls[0].config.params.search, 'test')
  })
}

test('destination page scopes its request to the selected cluster', async () => {
  const response = { success: true, data: [], total: 300 }
  const fixture = clientFixture(response)
  assert.equal(await fixture.api.lbApi.destinations('cluster one', 2, 'origin'), response)
  assert.equal(fixture.calls.length, 1)
  assert.equal(fixture.calls[0].url, '/lb/clusters/cluster%20one/destinations')
  assert.equal(fixture.calls[0].config.params.page, 2)
  assert.equal(fixture.calls[0].config.params.pageSize, 20)
})

test('IP management fetches groups and access entries as separate pages', async () => {
  const response = { success: true, data: [], total: 300 }
  const fixture = clientFixture(response)
  await fixture.api.ipApi.listGroups(2, 20, 'office')
  await fixture.api.ipApi.listAccess('whitelist', 3, 'office')
  assert.equal(fixture.calls.length, 2)
  assert.equal(fixture.calls[0].url, '/ip-groups')
  assert.equal(fixture.calls[0].config.params.page, 2)
  assert.equal(fixture.calls[1].url, '/ac/whitelist/entries')
  assert.equal(fixture.calls[1].config.params.page, 3)
  assert.equal(fixture.calls[1].config.params.search, 'office')
})

for (const status of [400, 403, 409, 429, 500]) {
  test(`HTTP ${status} remains rejected and preserves structured details`, async () => {
    const fixture = clientFixture()
    const error = { message: 'transport', config: { data: { password: 'private' } }, response: { status, data: { error: { code: 'failure', message: 'business failure' } } } }
    await assert.rejects(fixture.reject(error), value => value === error && value.message === 'business failure')
  })
}

test('plugin publication sends the version read by the editor', async () => {
  const fixture = clientFixture({ success: true, status: 'applied' })
  await fixture.api.pluginApi.saveConfig('image-process', { DefaultQuality: '70' }, 'version-one')
  assert.equal(fixture.calls.length, 1)
  assert.equal(fixture.calls[0].config.headers['If-Match'], 'version-one')
})

test('effective plugin config queries host and path without saving', async () => {
  const fixture = clientFixture({ config: {}, sources: {} })
  await fixture.api.pluginApi.getEffectiveConfig('image-process', 'img.example.com', '/thumb/a.png')
  assert.equal(fixture.calls.length, 1)
  assert.equal(fixture.calls[0].url, '/plugins/image-process/config/effective')
  assert.equal(fixture.calls[0].config.params.host, 'img.example.com')
  assert.equal(fixture.calls[0].config.params.path, '/thumb/a.png')
})

test('legacy step-up response is rejected without prompting or retrying', async () => {
  const fixture = clientFixture()
  const config = { method: 'post', url: '/backups', headers: { 'Idempotency-Key': 'one-request-key' } }
  const error = { config, response: { status: 428, data: { code: 'step_up_required' } } }
  await assert.rejects(fixture.reject(error))
  assert.ok(!fixture.retry())
})
