import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { test } from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'

const source = await readFile(new URL('../src/api/index.ts', import.meta.url), 'utf8')
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText

function clientFixture(response) {
  let rejectResponse
  let retryConfig
  const calls = []
  const client = {
    get(url, config) { calls.push({ url, config }); return Promise.resolve(response) },
    interceptors: {
      request: { use() {} },
      response: { use(_success, failure) { rejectResponse = failure } },
    },
    request(config) { retryConfig = config; return Promise.resolve({ success: true }) },
  }
  const module = { exports: {} }
  const context = {
    module, exports: module.exports,
    require(name) {
      if (name === 'axios') return { default: { create: () => client } }
      if (name === '@/composables/useStepUp') return { requestStepUp: async () => 'proof' }
      throw new Error(`Unexpected runtime import: ${name}`)
    },
    localStorage: { removeItem() {}, getItem() { return 'session' } }, window: { location: { hash: '#/login' } },
    console: { error() { throw new Error('Do not log request credentials') } },
  }
  vm.runInNewContext(compiled, context)
  return { reject: error => rejectResponse(error), retry: () => retryConfig, api: module.exports, calls }
}

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

test('legacy step-up response is rejected without prompting or retrying', async () => {
  const fixture = clientFixture()
  const config = { method: 'post', url: '/backups', headers: { 'Idempotency-Key': 'one-request-key' } }
  const error = { config, response: { status: 428, data: { code: 'step_up_required' } } }
  await assert.rejects(fixture.reject(error))
  assert.ok(!fixture.retry())
})
