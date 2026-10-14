import { createRequire } from 'node:module'
import assert from 'node:assert/strict'
import { resolve } from 'node:path'
import { readFileSync } from 'node:fs'
const require = createRequire(import.meta.url)
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright')
const origin = process.env.I18N_AUDIT_URL || 'http://127.0.0.1:5174'
const now = '2026-10-14T06:00:00Z'
const uploadMessages = Object.fromEntries(['zh-CN','en-US'].map(lang => [lang, JSON.parse(readFileSync(resolve('../Plugins/Sys/HttpUpload/Localization', lang + '.json'), 'utf8'))]))
const menus = [{ pluginId: 'analysis', name: 'CC protection', path: '/plugins/analysis/cc' }, { pluginId: 'web-tamper', name: 'Web tamper protection', path: '/plugins/web-tamper' }]
const browser = await chromium.launch({ channel: 'chrome', headless: true })
try {
  for (const width of [1440, 390]) {
    const context = await browser.newContext({ viewport: { width, height: 1000 } })
    await context.addInitScript(() => {
      if (location.protocol === 'about:') return
      localStorage.setItem('lywaf_locale', 'en-US')
      localStorage.setItem('lywaf_token', 'fixture')
      localStorage.setItem('lywaf_permissions', '["read","system.manage","users.manage"]')
    })
    const page = await context.newPage(), errors = []
    page.on('pageerror', e => errors.push(e.message))
    page.on('console', e => { if (e.type() === 'error' && /intlify|message compilation|SyntaxError/i.test(e.text())) errors.push(e.text()) })
    await page.route('**/api/**', async route => {
      const path = new URL(route.request().url()).pathname
      if (!path.startsWith('/api/')) return route.continue()
      assert.equal(route.request().method(), 'GET', `Unexpected write: ${path}`)
      let body = { success: true, data: [], total: 0 }
      if (path.endsWith('/plugins/menus')) body = { success: true, menus }
      if (path.endsWith('/plugins')) body = { success: true, data: [{ id: 'http-upload', name: uploadMessages['zh-CN']['metadata.name'], description: uploadMessages['zh-CN']['metadata.description'], version: '1.0', priority: 'Normal', state: 'Stopped', isEnabled: false, hasOptions: false, actions: [], localization: uploadMessages }], total: 1 }
      if (path.endsWith('/sites')) body = { success: true, total: 1, version: 'fixture', clusters: [{ id: 'cluster1' }], listeners: [{ host: '*', port: 80 }], data: [{
        site: { id: 'route1', name: 'Example site', enabled: true, route: { clusterId: 'cluster1', match: { path: '/{**path}', hosts: ['example.test'] } } },
        listeners: [{ host: '*', port: 80 }], upstreams: [{ id: 'dest1', address: 'http://127.0.0.1:8080' }],
        upstreamHealth: [{ id: 'dest1', active: 'Unknown', activeEnabled: true, passive: 'Unknown', passiveEnabled: false }],
        traffic: { requests: 10, intercepts: 2, errors: 1, observedSince: now }, certificates: [],
      }] }
      if (path.endsWith('/health-check')) body = { success: true, data: { active: { enabled: true, path: '/health', method: 'GET', statusCodes: ['20x'] }, passive: { enabled: false } } }
      if (path.endsWith('/lb/cluster-options')) body = { success: true, total: 1, data: [{ id: 'cluster1', loadBalancingPolicy: 'RoundRobin', destinationCount: 1 }] }
      if (path.endsWith('/lb/policies')) body = { success: true, policies: [{ name: 'RoundRobin', label: '\u8f6e\u8be2' }] }
      if (path.endsWith('/destinations')) body = { success: true, data: [{ id: 'dest1', address: 'http://127.0.0.1:8080', ip: '127.0.0.1', port: 8080, weight: 1 }], total: 1 }
      if (path.endsWith('/ban-logs')) body = { success: true, logs: [{ id: 1, time: now, ip: '192.0.2.1', reason: 'Test rule', durationSeconds: 60 }] }
      if (path.endsWith('/websites')) body = { success: true, websites: [{ id: 'site1', name: 'Example', host: 'example.test', domain: 'example.test', source: 'manual', removable: true }] }
      if (path.endsWith('/resources')) body = { success: true, resources: [] }
      if (path.endsWith('/events')) body = { success: true, events: [] }
      await route.fulfill({ json: body })
    })
    async function scan(label) {
      const found = await page.evaluate(() => {
        const found = [], walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT)
        while (walker.nextNode()) {
          const e = walker.currentNode.parentElement
          if (e?.getClientRects().length && !e.closest('script,style,option') && /\p{Script=Han}/u.test(walker.currentNode.textContent)) found.push(walker.currentNode.textContent.trim())
        }
        for (const e of document.querySelectorAll('select')) if (e.getClientRects().length && !e.closest('header')) for (const o of e.options) if (/\p{Script=Han}/u.test(o.text)) found.push(o.text)
        for (const e of document.querySelectorAll('[title],[aria-label],[placeholder]')) if (e.getClientRects().length) for (const a of ['title','aria-label','placeholder']) if (/\p{Script=Han}/u.test(e.getAttribute(a) || '')) found.push(e.getAttribute(a))
        return found
      })
      assert.deepEqual(found, [], `${label} at ${width}px`)
    }
    async function switchLocale(value) {
      // Drive the actual app instance; importing a Vite HMR module by URL can create a second instance.
      await page.locator('header select').selectOption(value, { force: true })
      await page.waitForTimeout(80)
      assert.equal(await page.evaluate(() => document.documentElement.lang), value)
    }
    async function visit(path) {
      await page.goto('about:blank')
      await page.goto(origin + '/#' + path)
      await page.waitForLoadState('networkidle')
      await scan(path)
    }
    async function checkDialog(path, button) {
      await visit(path)
      await page.getByRole('button', { name: button, exact: true }).first().click()
      await scan(path + ' dialog')
      const before = await page.locator('input:not([type="checkbox"]):not([type="radio"]),textarea').evaluateAll(es => es.map(e => e.value))
      await switchLocale('zh-CN')
      assert.ok(/\p{Script=Han}/u.test(await page.locator('body').innerText()))
      await switchLocale('en-US')
      await scan(path + ' dialog after switching')
      assert.deepEqual(await page.locator('input:not([type="checkbox"]):not([type="radio"]),textarea').evaluateAll(es => es.map(e => e.value)), before, 'Language switch must preserve drafts')
      console.log(width, path, button, 'verified')
    }
    await visit('/sites')
    await page.getByText('Awaiting probe', { exact: false }).waitFor()
    await page.screenshot({ path: resolve('../artifacts', `sites-en-${width}.png`) })
    await checkDialog('/sites', 'Add site')
    await checkDialog('/sites', 'Health checks')
    await checkDialog('/cluster-config', 'Add cluster')
    await checkDialog('/route-config', 'Add')
    await checkDialog('/fileserver-config', 'Add')
    await checkDialog('/simpleres-config', 'Add')
    await checkDialog('/waf-rules', 'Add rule')
    await checkDialog('/plugins/analysis/cc', 'Add rule')
    await checkDialog('/plugins/web-tamper', 'Add site')
    await visit('/plugin-management')
    await page.getByRole('heading', { name: 'HTTP upload', exact: true }).waitFor()
    await switchLocale('zh-CN')
    await page.getByRole('heading', { name: uploadMessages['zh-CN']['metadata.name'], exact: true }).waitFor()
    await switchLocale('en-US')
    await scan('plugin metadata switched back')
    assert.deepEqual(errors, [])
    await context.close()
  }
} finally { await browser.close() }
