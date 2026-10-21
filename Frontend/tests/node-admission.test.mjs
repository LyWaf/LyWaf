import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import ts from 'typescript'

const read = path => readFileSync(new URL(path, import.meta.url), 'utf8')
const js = ts.transpileModule(read('../src/utils/node-health.ts'), {
  compilerOptions: { module: ts.ModuleKind.ESNext },
}).outputText
const { isHealthyActive } = await import(`data:text/javascript;base64,${Buffer.from(js).toString('base64')}`)

test('admission cannot be overridden by a healthy observation or old server flag', () => {
  const node = { online: true, stale: false, lifecycle: 'Active', healthyActive: true }
  for (const admissionState of ['PendingApproval', 'Draining', 'Revoked', 'Unknown', 'Invalid']) {
    assert.equal(isHealthyActive({ ...node, admissionState }), false)
  }
  assert.equal(isHealthyActive({ ...node, admissionState: 'Active', admissionEnabled: false }), false)
  assert.equal(isHealthyActive({ ...node, admissionState: 'Active' }), true)
  assert.equal(isHealthyActive({ ...node, healthyActive: false }), false)
  assert.equal(isHealthyActive({ ...node, online: false }), false)
  assert.equal(isHealthyActive({ ...node, clockSkewWarning: true }), false)
  assert.equal(isHealthyActive({ online: true, stale: false }), true)
})

test('admission and observation are displayed separately with both locales', () => {
  const view = read('../src/views/MultiNode.vue')
  assert.match(view, /nodes\.filter\(isHealthyActive\)/)
  assert.match(view, /nodeAdmissionText\(node\)/)
  assert.match(view, /nodeLifecycleText\(node\)/)
  assert.match(view, /nodeErrorText\(node\)/)
  for (const locale of ['en-US', 'zh-CN']) {
    const messages = JSON.parse(read(`../../Localization/${locale}.json`))
    for (const key of ['label', 'observation', 'healthyCount', 'Active', 'PendingApproval', 'Draining', 'Revoked', 'Disabled', 'Unknown', 'Invalid', 'Legacy']) {
      assert.ok(messages[`node.admission.${key}`]?.trim())
    }
  }
})
