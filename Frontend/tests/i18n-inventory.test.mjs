import test from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

test('full inventory includes plugin routes, child dialogs, global UI and external templates', () => {
  const directory = mkdtempSync(join(tmpdir(), 'lywaf-i18n-audit-'))
  try {
    const output = join(directory, 'report.json')
    execFileSync(process.execPath, [fileURLToPath(new URL('../scripts/audit-i18n-inventory.mjs', import.meta.url)), output])
    const report = JSON.parse(readFileSync(output, 'utf8'))
    assert.ok(report.routes.some(route => route.path === '/plugins/analysis/cc' && route.owner === 'plugin'))
    assert.ok(report.routes.some(route => route.path === '/plugins/web-tamper' && route.owner === 'plugin'))
    assert.ok(report.routes.find(route => route.path === '/waf-rules').components.includes('Frontend/src/components/waf/WafRuleEditor.vue'))
    assert.ok(report.routes.find(route => route.path === '/sites').components.includes('Frontend/src/components/UpstreamHealthDialog.vue'))
    assert.ok(report.globalComponents.includes('Frontend/src/components/Layout/Sidebar.vue'))
    assert.ok(report.htmlCandidates.some(entry => entry.file === 'templates/403.html'))
    assert.ok(report.files.find(entry => entry.file.endsWith('/LanguageSelect.vue')).findings.every(item => item.classification === 'native-language-name'))
    assert.ok(report.files.find(entry => entry.file.endsWith('/GeneralSettings.vue')).findings.every(item => item.classification === 'protocol-comparison'))
    assert.equal(new Set(report.routes.map(route => route.path)).size, report.routes.length)
  } finally {
    rmSync(directory, { recursive: true })
  }
})
