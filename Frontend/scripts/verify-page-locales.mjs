import { createRequire } from 'node:module'
import assert from 'node:assert/strict'
import { resolve, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
const require = createRequire(import.meta.url)
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright')
const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
const now = '2026-10-14T06:00:00Z'
const browser = await chromium.launch({ channel: 'chrome', headless: true })
try {
  for (const width of [1440, 390]) {
    const context = await browser.newContext({ viewport: { width, height: 1000 } })
    await context.addInitScript(() => {
      localStorage.setItem('lywaf_locale', 'zh-CN')
      localStorage.setItem('lywaf_token', 'fixture')
    })
    const page = await context.newPage(), errors = []
    page.on('pageerror', error => errors.push(error.message))
    await page.route('**/api/**', async route => {
      const path = new URL(route.request().url()).pathname
      if (!path.startsWith('/api/')) return route.continue()
      assert.equal(route.request().method(), 'GET', `Unexpected write: ${path}`)
      let body = { success: true, data: [], total: 0, menus: [] }
      if (path.endsWith('/security/stats')) body = { success: true, snapshot: { wafInterceptCount: 17094, ccAttackCount: 221, blacklistBlockCount: 37111 }, timeSlots: [{ time: now, wafIntercept: 3 }, { time: '2026-10-14T06:01:00Z', wafIntercept: 9 }], topWafSources: [{ ip: '192.0.2.1', count: 12 }] }
      if (path.includes('qps')) body.data = [{ time: '14:00', qps: 2, requestCount: 10 }, { time: '14:01', qps: 3, requestCount: 15 }]
      if (path.includes('bandwidth')) body.data = [{ time: '14:00', inboundRate: 1024, outboundRate: 2048 }, { time: '14:01', inboundRate: 2048, outboundRate: 4096 }]
      if (path.endsWith('/notifications/channels')) body = { success: true, total: 1, data: [{ id: 'test', name: 'Test channel', enabled: true, kind: 'webhook', minCount: 10, mergeSeconds: 60, silencedUntil: now }] }
      if (path.endsWith('/backups')) body = { success: true, total: 1, statisticsRestorePending: true, instanceStatePending: true, data: [{ id: 'backup-test', scope: 'configuration', size: 1024, createdAt: now }] }
      if (path.endsWith('/tasks')) body = { success: true, total: 1, data: [{ id: 'test-job', kind: 'rules', state: 0, operator: 'admin', createdAt: now, logs: [], canRetry: false }] }
      if (path.endsWith('/timing/list')) body = { success: true, total: 1, data: [{ method: 'GET', path: '/test', backend: 'http://example.test', requestCount: 10, avgTotalTime: 20, avgBackendTime: 15, avgGatewayTime: 5, minTotalTime: 10, maxTotalTime: 30, minBackendTime: 8, maxBackendTime: 20, statusCodeCounts: { 200: 10 }, errorRate: 0 }], summary: { totalRequests: 10, apiCount: 1 }, backendStats: [] }
      if (path.endsWith('/system/health')) body = { success: true, data: { process: { id: 1, memoryBytes: 1024, startedAt: now }, database: 'Ready', disk: { availableBytes: 1024, capacityBytes: 2048 }, queue: { queued: 0, running: 0, failed: 0 }, listeners: ['http://127.0.0.1:7030'], upstreams: [{ cluster: 'test', destination: 'dest', address: 'http://example.test', active: 'Healthy', passive: 'Unknown', hosts: ['*'], connections: 0 }], certificates: [{ domain: 'example.test', expiresAt: now, daysRemaining: 10 }], capturedAt: now } }
      await route.fulfill({ json: body })
    })
    async function noChinese(label) {
      const found = await page.evaluate(() => {
        const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT), texts = []
        while (walker.nextNode()) {
          const e = walker.currentNode.parentElement
          if (e && !e.closest('option,script,style') && e.getClientRects().length && /\p{Script=Han}/u.test(walker.currentNode.textContent)) texts.push(walker.currentNode.textContent.trim())
        }
        for (const e of document.querySelectorAll('[title],[placeholder]')) if (e.getClientRects().length && /\p{Script=Han}/u.test((e.title || '') + (e.getAttribute('placeholder') || ''))) texts.push(e.outerHTML)
        return texts
      })
      assert.deepEqual(found, [], label)
    }
    await page.goto('http://127.0.0.1:5174/#/security')
    await page.getByText('17094', { exact: true }).waitFor()
    await page.waitForLoadState('networkidle')
    await page.locator('header select').selectOption('en-US')
    await page.getByText('Attack blocks', { exact: true }).waitFor()
    await noChinese('security')
    const chartLabels = await page.evaluate(async () => {
      const moduleUrl = performance.getEntriesByType('resource').find(entry => entry.name.includes('/chart__js.js'))?.name
      if (!moduleUrl) throw new Error('Chart.js module not loaded')
      const { Chart } = await import(moduleUrl)
      return Object.values(Chart.instances).flatMap(chart => chart.data.datasets.map(dataset => dataset.label))
    })
    assert.ok(chartLabels.includes('Inbound'), JSON.stringify(chartLabels))
    assert.ok(chartLabels.includes('Attack blocks'))
    assert.ok(chartLabels.every(label => !/\p{Script=Han}/u.test(label)))
    assert.equal(await page.locator('main').evaluate(e => e.scrollWidth > e.clientWidth + 1), false)
    const painted = await page.locator('#qps-chart').evaluate(canvas => {
      const data = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data
      let count = 0
      for (let i = 3; i < data.length; i += 4) if (data[i]) count++
      return count
    })
    assert.ok(painted > 100)
    await page.screenshot({ path: resolve(repo, `artifacts/security-en-${width}.png`) })
    await page.locator('header select').selectOption('zh-CN')
    await page.getByText('攻击拦截', { exact: true }).waitFor()
    await page.locator('header select').selectOption('en-US')
    await noChinese('security switched back')
    for (const path of ['/notifications', '/backups', '/system-health', '/management-tasks', '/api-timing']) {
      await page.goto(`http://127.0.0.1:5174/#${path}`)
      await page.waitForLoadState('networkidle')
      await noChinese(path)
      if (path === '/notifications') {
        await page.getByRole('button', { name: 'Add channel', exact: true }).click()
        await page.getByRole('dialog').locator('select').selectOption('smtp')
        await noChinese('SMTP dialog')
        await page.getByRole('dialog').locator('select').selectOption('plugin')
        await noChinese('plugin channel dialog')
        await page.getByRole('button', { name: 'Cancel', exact: true }).click()
      }
    }
    assert.deepEqual(errors, [])
    console.log(`Populated pages, charts and notification dialogs verified at ${width}px`)
    await context.close()
  }
} finally { await browser.close() }
