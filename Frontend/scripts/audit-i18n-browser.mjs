import { createRequire } from 'node:module'
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import assert from 'node:assert/strict'

const require = createRequire(import.meta.url)
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright')
const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
const inventory = JSON.parse(readFileSync(resolve(repo, 'artifacts/i18n-inventory.json')))
const origin = process.env.I18N_AUDIT_URL || 'http://127.0.0.1:5174'
const output = resolve(repo, 'artifacts/i18n-browser.json')
const results = []
const menus = [
  { pluginId: 'analysis', id: 'cc', name: 'CC protection', path: '/plugins/analysis/cc' },
  { pluginId: 'web-tamper', id: 'web-tamper', name: 'Web tamper protection', path: '/plugins/web-tamper' },
]
const browser = await chromium.launch({ channel: 'chrome', headless: true })
try {
  for (const routeInfo of inventory.routes) {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } })
    await context.addInitScript(({ login }) => {
      localStorage.setItem('lywaf_locale', 'en-US')
      localStorage.setItem('lywaf_permissions', '["read","system.manage","users.manage"]')
      if (!login) localStorage.setItem('lywaf_token', 'audit-fixture')
    }, { login: routeInfo.path === '/login' })
    const page = await context.newPage(), errors = [], requests = []
    page.on('pageerror', error => errors.push(error.message))
    page.on('console', entry => { if (entry.type() === 'error' && /intlify|message compilation/i.test(entry.text())) errors.push(entry.text()) })
    // Fully isolated from the actual management API. No configuration is written.
    await page.route('**/api/**', async route => {
      const path = new URL(route.request().url()).pathname
      if (!path.startsWith('/api/')) return route.continue()
      requests.push({ path, method: route.request().method() })
      let body = { success: false, message: 'Audit fixture: no server data', data: [], total: 0 }
      if (path.endsWith('/plugins/menus')) body = { success: true, menus }
      if (path.endsWith('/auth/time')) body = { timestamp: Math.floor(Date.now() / 1000) }
      await route.fulfill({ json: body })
    })
    let navigationError
    try {
      await page.goto(`${origin}/#${routeInfo.path}`)
      await page.waitForLoadState('networkidle')
    } catch (error) { navigationError = error.message }
    const snapshot = await page.evaluate(() => {
      const chinese = /\p{Script=Han}/u, texts = [], attributes = []
      const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT)
      while (walker.nextNode()) {
        const node = walker.currentNode, element = node.parentElement
        if (!element || element.closest('script,style,option') || !element.getClientRects().length) continue
        if (chinese.test(node.textContent)) texts.push(node.textContent.trim())
      }
      for (const element of document.querySelectorAll('[title],[placeholder],[aria-label]')) {
        if (!element.getClientRects().length) continue
        for (const name of ['title', 'placeholder', 'aria-label']) {
          const value = element.getAttribute(name)
          if (value && chinese.test(value)) attributes.push({ name, value })
        }
      }
      for (const element of document.querySelectorAll('select')) {
        if (!element.getClientRects().length) continue
        const option = element.selectedOptions[0]
        if (option && chinese.test(option.textContent)) texts.push(option.textContent.trim())
      }
      return { title: document.title, untranslatedText: [...new Set(texts)], attributes,
        hasMainContent: Boolean(document.querySelector('main')?.textContent.trim() || document.querySelector('form')),
        route: location.hash.slice(1) }
    })
    results.push({ path: routeInfo.path, ...snapshot, errors, navigationError, requests })
    console.log(`${routeInfo.path}\t${snapshot.untranslatedText.length} Chinese texts\t${snapshot.attributes.length} attributes\t${errors.length} runtime errors`)
    await context.close()
  }
} finally { await browser.close() }
mkdirSync(dirname(output), { recursive: true })
writeFileSync(output, JSON.stringify({ generatedAt: new Date().toISOString(),
  scope: 'English initial and failed/empty-data states, with isolated API fixtures. Not a production end-to-end test. Hidden dialogs and populated states require separate coverage; static inventory includes their source.',
  results }, null, 2) + '\n')
console.log(`Report: ${output}`)
assert.ok(results.every(result => result.hasMainContent && !result.navigationError && !result.errors.length && !result.untranslatedText.length && !result.attributes.length && !/\p{Script=Han}/u.test(result.title)), 'Some routes still have localization or runtime failures; inspect the report')
