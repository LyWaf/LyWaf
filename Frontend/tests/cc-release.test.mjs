import test from 'node:test'
import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'

test('CC release submits explicit scope and never substitutes global IP unban', async () => {
  const page = await readFile(new URL('../src/plugins/analysis/CcProtection.vue', import.meta.url), 'utf8')
  const api = await readFile(new URL('../src/plugins/analysis/api.ts', import.meta.url), 'utf8')
  assert.match(api, /'cc\/release', request/)
  assert.match(page, /ruleId: releaseRule\.value\.id, scopeType: releaseScopeType\.value, scope: releaseScope\.value\.trim\(\), ip: releaseIp\.value\.trim\(\)/)
  assert.match(page, /window\.confirm/)
  assert.match(page, /propagation\.status === 'pending'/)
  assert.doesNotMatch(page, /blocked-ips\/remove|unblockIp/)
  for (const locale of ['en-US', 'zh-CN']) {
    const dictionary = JSON.parse(await readFile(new URL(`../src/plugins/analysis/locales/${locale}.json`, import.meta.url), 'utf8'))
    for (const key of ['action', 'scopeType', 'scope', 'confirm', 'warning', 'pending', 'forbidden', 'authorityUnavailable'])
      assert.ok(dictionary[`cc.release.${key}`])
  }
})
