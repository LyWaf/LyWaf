import assert from 'node:assert/strict'
import { createRequire } from 'node:module'
import { mkdir } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import { resolve } from 'node:path'
import { createServer } from 'vite'

const { chromium } = createRequire(import.meta.url)(process.argv[2] || 'playwright')
const root = fileURLToPath(new URL('..', import.meta.url))
const artifacts = resolve(root, '../artifacts/node-setup-ui')
await mkdir(artifacts, { recursive: true })
const server = await createServer({
  root, server: { host: '127.0.0.1', port: 0, open: false },
  plugins: [{
    name: 'node-setup-ui-fixture',
    configureServer(vite) {
      vite.middlewares.use(async (req, res, next) => {
        if (!req.url?.startsWith('/__node_setup')) return next()
        res.setHeader('Content-Type', 'text/html')
        const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head>
          <body class="bg-dark-bg text-gray-200"><main id="app" class="p-4 max-w-6xl mx-auto"></main>
          <script type="module">
          import {createApp} from 'vue';
          import Panel from '/src/components/nodes/NodeSetupPanel.vue';
          import {setLocale} from '/src/i18n/index.ts';
          import '/src/styles/main.css';
          setLocale(new URLSearchParams(location.search).get('language') || 'en-US');
          createApp(Panel).mount('#app');
          </script></body></html>`
        res.end(await vite.transformIndexHtml(req.url, html))
      })
    },
  }],
})
let browser
const results = []
try {
  await server.listen()
  const port = server.httpServer.address().port
  browser = await chromium.launch({ headless: true, channel: process.argv[3] || 'chrome' })
  for (const language of ['en-US', 'zh-CN']) {
    for (const width of [1440, 390]) {
      for (const upgrading of [false, true]) {
      const context = await browser.newContext({ viewport: { width, height: 900 } })
      const page = await context.newPage()
      const errors = []
      let restarts = 0, saves = 0
      let settings = { mode: upgrading ? 'Node' : 'Standalone', activeMode: upgrading ? 'Node' : 'Standalone',
        controllerUrl: upgrading ? 'https://controller.example:7030/' : '', hasCredential: upgrading, restartRequired: false, revision: 1,
        synchronizeRuntimeState: upgrading, runtimeSynchronizationConfigured: upgrading, runtimeSynchronizationActive: false }
      const scenario = `${language}-${width}${upgrading ? '-upgrade' : ''}`
      page.on('pageerror', error => errors.push(error.message))
      page.on('dialog', async dialog => { errors.push('Native dialog: ' + dialog.message()); await dialog.dismiss() })
      // Intercept every API request, including restart. This script must never reach a running LyWaf instance.
      await page.route(url => url.pathname.startsWith('/api/'), async route => {
        const request = route.request(), path = new URL(request.url()).pathname.replace('/api/v1/', '/api/')
        const respond = body => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
        if (path === '/api/node-setup' && request.method() === 'GET')
          return respond({ success: true, settings, peers: [], effectiveMode: settings.activeMode })
        if (path === '/api/node-setup' && request.method() === 'PUT') {
          saves++
          const body = request.postDataJSON()
          assert.equal(body.mode, 'Node')
          assert.equal(body.controllerUrl, 'https://controller.example:7030/')
          assert.equal(body.synchronizeRuntimeState, !upgrading)
          settings = { ...settings, mode: 'Node', hasCredential: true, restartRequired: true, authorityUpgradePending: upgrading, revision: 2,
            synchronizeRuntimeState: body.synchronizeRuntimeState, runtimeSynchronizationConfigured: true, runtimeHistoryChangePending: !upgrading }
          return respond({ success: true, settings })
        }
        if (path === '/api/restart' && request.method() === 'POST') { restarts++; return respond({ success: true }) }
        errors.push('Unexpected API: ' + request.method() + ' ' + path)
        await route.abort()
      })
      await page.goto(`http://127.0.0.1:${port}/__node_setup?language=${language}`)
      try { await page.getByRole('combobox').waitFor({ timeout: 10000 }) }
      catch (error) {
        await page.screenshot({ path: resolve(artifacts, `${scenario}-failure.png`), fullPage: true })
        console.error(JSON.stringify({ errors, body: await page.locator('body').innerText() }))
        throw error
      }
      const english = language === 'en-US'
      const restartName = english ? 'Restart LyWaf' : '重启 LyWaf'
      const confirmName = english ? 'Apply Node Settings' : '应用节点设置'
      await page.getByRole('combobox').selectOption('Node')
      const synchronization = page.getByRole('checkbox')
      assert.equal(await synchronization.isChecked(), true)
      if (upgrading) await synchronization.uncheck()
      await page.getByLabel(english ? 'Controller URL' : '中控地址').fill('https://controller.example:7030/')
      await page.locator('input[type=password]').fill('lywaf_node_test_fixture')
      await page.getByRole('button', { name: english ? 'Save' : '保存', exact: true }).click()
      const restartButton = page.getByRole('button', { name: restartName, exact: true })
      await restartButton.waitFor()
      assert.equal(saves, 1)
      assert.equal(restarts, 0)
      if (upgrading) await page.getByRole('status').filter({ hasText: english ? 'Controller authority settings have been verified' : '中控验证配置已校验并保存' }).waitFor()
      await page.screenshot({ path: resolve(artifacts, `${scenario}-pending.png`), fullPage: true })
      await restartButton.click()
      const dialog = page.getByRole('dialog', { name: confirmName })
      await dialog.waitFor()
      assert.equal(await dialog.getByRole('note').count(), upgrading ? 0 : 1)
      const bounds = await dialog.boundingBox()
      assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= width)
      assert.ok(bounds.y >= 0 && bounds.y + bounds.height <= 900)
      await page.screenshot({ path: resolve(artifacts, `${scenario}-confirm.png`), fullPage: true })
      await page.keyboard.press('Escape')
      assert.equal(await dialog.count(), 0)
      assert.equal(restarts, 0)
      await restartButton.click()
      await dialog.getByRole('button', { name: restartName, exact: true }).click()
      await page.getByRole('status').filter({ hasText: english ? 'Restart requested.' : '已请求重启' }).waitFor()
      assert.equal(restarts, 1)
      assert.equal(await page.getByRole('combobox').isDisabled(), true)
      assert.equal(await restartButton.isDisabled(), true)
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true)
      assert.deepEqual(errors, [])
      results.push({ language, width, upgrading, saves, restarts })
      await context.close()
      }
    }
  }
  console.log(JSON.stringify({ success: true, results, artifacts }))
} finally { await browser?.close(); await server.close() }
