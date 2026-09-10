import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { test } from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import { parse } from '@vue/compiler-sfc'

const source = await readFile(new URL('../src/views/SystemHealth.vue', import.meta.url), 'utf8')
const { descriptor } = parse(source)
const compiled = ts.transpileModule(descriptor.scriptSetup.content + '\nexport { data, upstreamGroups };', {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText

test('system health merges nonadjacent cluster cells without dropping destination states', () => {
  const module = { exports: {} }
  vm.runInNewContext(compiled, {
    module, exports: module.exports,
    require(name) {
      if (name === 'vue') return { ref: value => ({ value }), computed: fn => ({ get value() { return fn() } }), onMounted() {} }
      if (name === '@/api') return { managementTaskApi: {} }
      throw new Error(`Unexpected import: ${name}`)
    },
  })
  const { data, upstreamGroups } = module.exports
  assert.equal(upstreamGroups.value.length, 0)
  const first = { cluster: 'one', destination: 'a', active: 'Healthy', passive: 'Unknown', connections: 2 }
  const second = { cluster: 'two', destination: 'a', active: 'Unhealthy', passive: 'Unknown', connections: 0 }
  const third = { cluster: 'one', destination: 'b', active: 'Unknown', passive: 'Healthy', connections: 3 }
  data.value = { upstreams: [first, second, third] }
  assert.equal(upstreamGroups.value.length, 2)
  assert.equal(upstreamGroups.value[0].entries.length, 2)
  assert.equal(upstreamGroups.value[0].entries[1], third)
  assert.equal(upstreamGroups.value[1].entries[0], second)
  assert.match(descriptor.template.content, /:rowspan="group.entries.length"/)
})
