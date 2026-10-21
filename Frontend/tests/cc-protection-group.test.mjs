import test from 'node:test'
import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'

test('CC protection group is restored, submitted and localized within the plugin', async () => {
  const editor = await readFile(new URL('../src/components/cc/CcRuleEditor.vue', import.meta.url), 'utf8')
  assert.match(editor, /protectionGroup: rule\.protectionGroup \|\| ''/)
  assert.match(editor, /protectionGroup: form\.value\.scope === 'SiteGroup' \? form\.value\.protectionGroup\.trim\(\) : ''/)
  assert.match(editor, /v-model="form\.protectionGroup"[^>]*maxlength="128"/)
  for (const locale of ['en-US', 'zh-CN']) {
    const dictionary = JSON.parse(await readFile(new URL(`../src/plugins/analysis/locales/${locale}.json`, import.meta.url), 'utf8'))
    assert.ok(dictionary['cc.protectionGroup'])
  }
})
