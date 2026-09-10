import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { test } from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import { parse } from '@vue/compiler-sfc'

const source = await readFile(new URL('../src/views/Sites.vue', import.meta.url), 'utf8')
const { descriptor } = parse(source)
const compiled = ts.transpileModule(descriptor.scriptSetup.content + '\nexport { rules, error, rows };', {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText

async function mountSites(pages) {
  let mounted
  const calls = []
  const module = { exports: {} }
  vm.runInNewContext(compiled, {
    module, exports: module.exports,
    require(name) {
      if (name === 'vue') return { ref: value => ({ value }), onMounted: callback => { mounted = callback } }
      if (name === '@/api') return {
        siteApi: { list: async () => ({ success: true, data: [{ site: { id: 'site' } }], clusters: [], listeners: [], total: 1 }) },
        wafRuleApi: { list: async params => { calls.push(params); return pages[params.page - 1] } },
      }
      throw new Error(`Unexpected import: ${name}`)
    },
  })
  await mounted()
  return { ...module.exports, calls }
}

test('site rule selector reads data across pages without the legacy rules field', async () => {
  const first = Array.from({ length: 100 }, (_, i) => ({ id: `rule-${i}`, name: `Rule ${i}` }))
  const result = await mountSites([
    { success: true, data: first, total: 101 },
    { success: true, data: [{ id: 'last', name: 'Last rule' }], total: 101 },
  ])
  assert.equal(result.error.value, '')
  assert.equal(result.rules.value.length, 101)
  assert.equal(result.rules.value[100].id, 'last')
  assert.equal(result.rows.value.length, 1)
  assert.equal(result.calls.length, 2)
  assert.equal(result.calls[1].page, 2)
  assert.equal(result.calls[1].source, 'User')
})

test('site rule selector accepts an empty data page', async () => {
  const result = await mountSites([{ success: true, data: [], total: 0 }])
  assert.equal(result.error.value, '')
  assert.equal(result.rules.value.length, 0)
  assert.equal(result.calls.length, 1)
})

test('malformed rule responses show an actionable error without losing sites', async () => {
  const result = await mountSites([{ success: true, total: 1 }])
  assert.equal(result.error.value, '规则列表响应格式无效')
  assert.equal(result.rows.value.length, 1)
})
