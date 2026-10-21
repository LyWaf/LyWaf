import test from 'node:test'
import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'

test('CC logical logs use decision identity and expose node hits without claiming global acknowledgement', async () => {
  const page = await readFile(new URL('../src/plugins/analysis/CcProtection.vue', import.meta.url), 'utf8')
  assert.match(page, /:key="log\.decisionId \?\? log\.id"/)
  assert.match(page, /log\.protectionScope/)
  assert.match(page, /node\.interceptedRequests/)
  assert.match(page, /node\.firstHit/)
  assert.match(page, /node\.lastHit/)
  assert.match(page, /log\.propagation\?\.allNodesConfirmed \? 'cc\.propagation\.confirmed' : 'cc\.decision\.pending'/)
  assert.match(page, /log\.propagation\.acknowledgedNodes/)
  assert.match(page, /log\.propagation\.pendingNodes/)
  assert.match(page, /advancedCcApi\.releaseStatus\(releaseReceipt\.value\)/)
  assert.match(page, /policyGeneration: result\.policyGeneration/)
  for (const locale of ['en-US', 'zh-CN']) {
    const dictionary = JSON.parse(await readFile(new URL(`../src/plugins/analysis/locales/${locale}.json`, import.meta.url), 'utf8'))
    for (const key of ['scope', 'nodes', 'hits', 'first', 'last', 'pending']) assert.ok(dictionary[`cc.decision.${key}`])
    for (const key of ['confirmed', 'nodes', 'refresh']) assert.ok(dictionary[`cc.propagation.${key}`])
  }
})
