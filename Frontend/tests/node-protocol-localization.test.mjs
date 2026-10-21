import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'

const read = path => readFileSync(new URL(path, import.meta.url), 'utf8')
const en = JSON.parse(read('../../Localization/en-US.json'))
const zh = JSON.parse(read('../../Localization/zh-CN.json'))

test('target errors localize stable codes while legacy prose remains compatible', () => {
  for (const dictionary of [en, zh]) {
    const exports = {}
    const js = ts.transpileModule(read('../src/i18n/node-error.ts'), {
      compilerOptions: { module: ts.ModuleKind.CommonJS },
    }).outputText
    vm.runInNewContext(js, { exports, require: () => ({
      i18n: { global: { te: key => !!dictionary[key] } }, t: key => dictionary[key],
    }) })
    assert.equal(exports.nodeErrorText({ error: 'node_config_conflict' }), dictionary['node.errors.node_config_conflict'])
    assert.equal(exports.nodeErrorText({ errorCode: 'node_config_invalid', error: 'untrusted prose' }), dictionary['node.errors.node_config_invalid'])
    assert.equal(exports.nodeErrorText({ error: 'legacy error' }), 'legacy error')
    assert.equal(exports.nodeErrorText({ error: 'node_future_unknown' }), dictionary['node.errors.node_http_rejected'])
  }
  assert.match(read('../src/views/MultiNode.vue'), /:title="nodeErrorText\(target\)"/)
})

test('node protocol and replication authorization errors have both language resources', () => {
  const sources = [
    '../../Services/NodeCommunication/NodeProtocolResponse.cs',
    '../../Services/Distribution/NodeConfigurationErrors.cs',
    '../../Services/NodeCommunication/NodeMessageAuthentication.cs',
    '../../Services/NodeCommunication/NodeFailure.cs',
    '../../Services/NodeCommunication/NodeCommandInbox.cs',
    '../../Services/NodeCommunication/NodeCommunicationEndpoints.cs',
    '../../Services/NodeCommunication/NodeIdentityConflictResolutionService.cs',
    '../../Services/NodeCommunication/PluginNodeMessageService.cs',
    '../../Services/Statistic/CcExecutionRelayService.cs',
    '../../Services/RuntimeState/RuntimeStateReplicationAuthorization.cs',
  ]
  const codes = new Set(sources.flatMap(path =>
    [...read(path).matchAll(/"((?:node|runtime|cc)_[a-z_]+)"/g)].map(match => match[1])))
  assert.ok(codes.size >= 28)
  for (const code of codes) {
    const key = `node.errors.${code}`
    assert.ok(en[key]?.trim(), `Missing English: ${key}`)
    assert.ok(zh[key]?.trim(), `Missing Chinese: ${key}`)
    assert.doesNotMatch(en[key], /\p{Script=Han}/u, key)
  }
})
