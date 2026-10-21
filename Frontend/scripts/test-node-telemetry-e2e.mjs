import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import { resolve } from 'node:path'
import { createServer } from 'vite'

// Pass a Playwright module path when using a shared runtime instead of a local install.
const { chromium } = createRequire(import.meta.url)(process.argv[2] || 'playwright')
const root = fileURLToPath(new URL('../..', import.meta.url))
const artifacts = resolve(root, 'artifacts/node-telemetry-e2e')
await mkdir(artifacts, { recursive: true })
const fixturePath = resolve(artifacts, 'controller.json')
await new Promise((ok, fail) => {
  const child = spawn('dotnet', ['test', 'tests/LyWaf.Tests/LyWaf.Tests.csproj', '-c', 'Release',
    '--artifacts-path', '.replication-queue-test-artifacts', '--no-restore', '--filter',
    'FullyQualifiedName~NodeTelemetryTests.ActualMiddlewareAgentHttpClientAndControllerCarryRawWindows'],
  { cwd: root, env: { ...process.env, LYWAF_TELEMETRY_E2E_OUTPUT: fixturePath }, stdio: 'inherit' })
  child.on('error', fail)
  child.on('exit', code => code === 0 ? ok() : fail(new Error(`Backend test exited ${code}`)))
})
const fixtures = JSON.parse(await readFile(fixturePath, 'utf8'))
const server = await createServer({
  root: resolve(root, 'Frontend'),
  server: { host: '127.0.0.1', port: 0, open: false },
  plugins: [{
    name: 'telemetry-e2e-fixture',
    configureServer(vite) {
      vite.middlewares.use(async (req, res, next) => {
        if (req.url === '/__telemetry_fixture') {
          res.setHeader('Content-Type', 'application/json')
          res.end(JSON.stringify(fixtures)); return
        }
        if (!req.url?.startsWith('/__telemetry_e2e')) return next()
        res.setHeader('Content-Type', 'text/html')
        const html = `<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head>
          <body class="bg-dark-bg text-gray-200"><main id="app" class="p-4 max-w-6xl mx-auto"></main>
          <script type="module">
          import {createApp, h, ref} from 'vue';
          import Summary from '/src/components/nodes/NodeTelemetrySummary.vue';
          import Dashboard from '/src/views/MultiNode.vue';
          import {setLocale} from '/src/i18n/index.ts';
          import '/src/styles/main.css';
          const fixtures = await fetch('/__telemetry_fixture').then(r => r.json());
          const selected = ref(fixtures.complete.telemetry);
          window.selectTelemetry = (name) => selected.value = name === 'legacy' ? null : fixtures[name].telemetry;
          window.setTelemetry = value => selected.value = value;
          window.changeLocale = setLocale;
          setLocale('en-US');
          createApp({render: () => new URLSearchParams(location.search).has('full')
            ? h(Dashboard) : h(Summary, {telemetry: selected.value})}).mount('#app');
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
  const address = server.httpServer.address()
  browser = await chromium.launch({ headless: true, ...(process.argv[3] ? { channel: process.argv[3] } : {}) })
  const page = await browser.newPage()
  const errors = []
  page.on('pageerror', error => errors.push(error.message))
  await page.goto(`http://127.0.0.1:${address.port}/__telemetry_e2e`)
  const get = name => page.getByTestId(`telemetry-${name}`)
  await get('requests').waitFor()
  assert.equal(await get('requests').textContent(), '100')
  assert.equal(await get('error-rate').textContent(), '10%')
  assert.equal(await get('intercept-rate').textContent(), '10%')
  assert.equal(await get('error-fraction').textContent(), '10 / 100')
  assert.equal(await get('visitors-count').textContent(), '1')
  assert.equal(await get('logicalBans-count').textContent(), '1')
  assert.equal(fixtures.complete.telemetry.logicalBans.final, false)
  assert.equal(await get('p95').textContent(), `~${fixtures.complete.telemetry.p95Milliseconds.toLocaleString('en-US')} ms`)
  assert.match(await get('coverage').textContent(), /Complete: 2\/2/)
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 })
    for (const language of ['en-US', 'zh-CN']) {
      await page.evaluate(language => window.changeLocale(language), language)
      for (const state of ['complete', 'partial', 'incompatible', 'missing', 'legacy']) {
        await page.evaluate(state => window.selectTelemetry(state), state)
        await page.waitForTimeout(30)
        if (state === 'legacy') await get('unavailable').waitFor()
        else if (state === 'missing') assert.equal(await get('requests').textContent(), '-')
        else assert.equal(await get('requests').textContent(), '100')
        if (state === 'partial') assert.match(await get('coverage').textContent(), /2\/3/)
        if (state === 'incompatible') assert.match(await get('missing').textContent(), /不兼容|Incompatible/)
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false)
        await page.screenshot({ path: resolve(artifacts, `${state}-${language}-${width}.png`), fullPage: true })
        results.push({ state, language, width, passed: true })
      }
    }
  }
  await page.evaluate(value => window.setTelemetry({ ...value, schema: 'future-v2' }), fixtures.complete.telemetry)
  await get('unsupported').waitFor()
  assert.equal(await get('requests').count(), 0)
  await page.evaluate(value => window.setTelemetry({ ...value, p95Milliseconds: null, p99Milliseconds: null }), fixtures.complete.telemetry)
  await get('p95').waitFor()
  assert.match(await get('p95').textContent(), /^> /)
  let dashboardResponse = fixtures.complete
  await page.route('**/api/v1/distribution/dashboard', route => route.fulfill({ json: { success: true, dashboard: dashboardResponse } }))
  await page.route('**/api/v1/distribution/deployments', route => route.fulfill({ json: { success: true, deployments: [] } }))
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 1000 })
    for (const state of ['complete', 'partial']) {
      dashboardResponse = fixtures[state]
      await page.goto(`http://127.0.0.1:${address.port}/__telemetry_e2e?full=1`)
      await get('requests').waitFor()
      assert.equal(await get('requests').textContent(), '100')
      assert.equal(await get('error-rate').textContent(), '10%')
      assert.match(await get('coverage').textContent(), state === 'complete' ? /Complete: 2\/2/ : /Partial: 2\/3/)
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false)
      await page.screenshot({ path: resolve(artifacts, `dashboard-${state}-${width}.png`), fullPage: true })
      results.push({ state, width, surface: 'MultiNode dashboard API consumer', passed: true })
      for (const language of ['en-US', 'zh-CN']) {
        await page.evaluate(language => window.changeLocale(language), language)
        await page.getByRole('button', { name: language === 'en-US' ? 'Communication' : '通信诊断', exact: true }).click()
        await page.getByTestId('node-communication').waitFor()
        assert.ok(await page.getByTestId('communication-events').count() >= 2)
        assert.ok(await page.getByTestId('ack-unsampled').count() >= 2)
        assert.match(await page.getByTestId('node-communication').textContent(), /node.dashboard/)
        assert.doesNotMatch(await page.getByTestId('node-communication').textContent(), /nodes\.communication\./)
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false)
        await page.screenshot({ path: resolve(artifacts, `communication-${state}-${language}-${width}.png`), fullPage: true })
        results.push({ state, language, width, surface: 'MultiNode communication API consumer', passed: true })
      }
    }
  }
  // Explicit contract fixtures: this checks the consumer, not the production durable-ACK hook.
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 1000 })
    for (const language of ['en-US', 'zh-CN']) {
      for (const sampled of [true, false]) {
        dashboardResponse = structuredClone(fixtures.complete)
        for (const node of dashboardResponse.nodes) {
          node.snapshot.diagnostics.communication.replicationAcknowledgement = {
            samples: sampled ? 2 : 0, totalMilliseconds: sampled ? 600 : 0,
            maxMilliseconds: sampled ? 450 : 0, clockSkewSamples: 3,
            semantics: 'mutation-change-to-transport-ack; not-business-execution-ack',
          }
        }
        await page.goto(`http://127.0.0.1:${address.port}/__telemetry_e2e?full=1`)
        await get('requests').waitFor()
        await page.evaluate(language => window.changeLocale(language), language)
        await page.getByRole('button', { name: language === 'en-US' ? 'Communication' : '通信诊断', exact: true }).click()
        const delay = page.getByTestId('ack-delay').first()
        await delay.waitFor()
        assert.match(await delay.textContent(), language === 'en-US' ? /not business execution ACK/ : /非业务执行确认/)
        assert.match(await page.getByTestId('ack-scope').first().textContent(), language === 'en-US' ? /Includes queueing and retries/ : /包含排队及重试/)
        assert.match(await delay.textContent(), sampled ? /300 ms\s*\/ 2/ : /- ms\s*\/ 0/)
        assert.match(await delay.textContent(), sampled ? /450 ms/ : /- ms/)
        assert.match(await page.getByTestId('ack-skew').first().textContent(), /: 3$/)
        assert.equal(await page.getByTestId('ack-unsampled').count(), sampled ? 0 : 2)
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false)
        results.push({ sampled, width, language, surface: 'ACK diagnostic JSON consumer fixture', passed: true })
      }
    }
  }
  assert.deepEqual(errors, [])
  await writeFile(resolve(artifacts, 'result.json'), JSON.stringify({ backend: 'actual HTTP collector -> agent -> client -> controller', results, errors }, null, 2))
  console.log(`Telemetry E2E passed: ${results.length} cases plus unsupported schema and overflow; ${artifacts}`)
} finally {
  await browser?.close()
  await server.close()
}
