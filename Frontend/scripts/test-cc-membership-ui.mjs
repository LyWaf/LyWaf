import assert from 'node:assert/strict'
import { createRequire } from 'node:module'
import { mkdir } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import { resolve } from 'node:path'
import { createServer } from 'vite'

const { chromium } = createRequire(import.meta.url)(process.argv[2] || 'playwright')
const root = fileURLToPath(new URL('..', import.meta.url))
const artifacts = resolve(root, '../artifacts/cc-membership-ui')
await mkdir(artifacts, { recursive: true })
const server = await createServer({ root, server: { host: '127.0.0.1', port: 0, open: false }, plugins: [{
  name: 'cc-membership-fixture',
  configureServer(vite) {
    vite.middlewares.use(async (req, res, next) => {
      if (!req.url?.startsWith('/__cc_membership')) return next()
      res.setHeader('Content-Type', 'text/html')
      res.end(await vite.transformIndexHtml(req.url, `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head>
        <body class="bg-dark-bg text-gray-200"><main id="app"></main><script type="module">
        import {createApp} from 'vue'; import Editor from '/src/components/cc/CcRuleEditor.vue';
        import {setLocale} from '/src/i18n/index.ts'; import '/src/styles/main.css';
        setLocale(new URLSearchParams(location.search).get('language'));
        createApp(Editor, {show:true, editRule:{id:'fixture',name:'Shared API',scope:'SiteGroup',subject:'Ip',enabled:true,
        type:'FrequentAccess',conditions:[],period:10,threshold:100,action:'Block',actionSeconds:600,priority:100,
        expectedNodes:['manual-a','manual-b'],requireCompleteCoverage:true}}).mount('#app');
        </script></body></html>`))
    })
  },
}] })
let browser
const results = []
try {
  await server.listen()
  browser = await chromium.launch({ headless: true, channel: process.argv[3] || 'chrome' })
  for (const language of ['en-US', 'zh-CN']) for (const width of [1440, 390]) {
    const context = await browser.newContext({ viewport: { width, height: 900 } })
    const page = await context.newPage()
    const errors = [], requests = []
    page.on('pageerror', error => errors.push(error.message))
    await page.route(url => url.pathname.startsWith('/api/'), async route => {
      assert.ok(route.request().url().endsWith('/cc/advanced/update'))
      requests.push(route.request().postDataJSON())
      await route.fulfill({ status: 200, contentType: 'application/json', body: '{"success":true}' })
    })
    await page.goto(`http://127.0.0.1:${server.httpServer.address().port}/__cc_membership?language=${language}`)
    const english = language === 'en-US'
    const scope = page.getByLabel(english ? 'Scope' : '作用域', { exact: true })
    const membership = page.getByLabel(english ? 'Expected members' : '预期成员', { exact: true })
    const nodes = page.getByLabel(english ? 'Node IDs (comma-separated)' : '节点 ID（逗号分隔）', { exact: true })
    const coverage = page.getByRole('checkbox', { name: english ? 'Require complete coverage' : '要求完整覆盖' })
    await scope.waitFor()
    assert.equal(await nodes.inputValue(), 'manual-a, manual-b')
    await membership.selectOption('true')
    assert.equal(await nodes.count(), 0)
    assert.equal(await coverage.isChecked(), true)
    assert.equal(await coverage.isDisabled(), true)
    await page.getByRole('button', { name: english ? 'Save' : '保存', exact: true }).click()
    await page.waitForFunction(() => !document.querySelector('.animate-spin'))
    assert.equal(requests.length, 1)
    assert.equal(requests[0].useManagedMembership, true)
    assert.equal(requests[0].requireCompleteCoverage, true)
    assert.deepEqual(requests[0].expectedNodes, [])
    await page.screenshot({ path: resolve(artifacts, `${language}-${width}.png`), fullPage: true })
    await membership.selectOption('false')
    assert.equal(await nodes.inputValue(), 'manual-a, manual-b')
    await scope.selectOption('Node')
    assert.equal(await membership.count(), 0)
    await page.getByRole('button', { name: english ? 'Save' : '保存', exact: true }).click()
    await page.waitForFunction(() => !document.querySelector('.animate-spin'))
    assert.equal(requests.length, 2)
    assert.equal(requests[1].useManagedMembership, false)
    assert.equal(requests[1].scope, 'Node')
    assert.deepEqual(requests[1].expectedNodes, [])
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true)
    assert.deepEqual(errors, [])
    results.push({ language, width, requests: requests.length })
    await context.close()
  }
  console.log(JSON.stringify({ success: true, results, artifacts }))
} finally { await browser?.close(); await server.close() }
