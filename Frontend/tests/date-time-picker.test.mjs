import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { test } from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import { parse } from '@vue/compiler-sfc'

const source = await readFile(new URL('../src/components/common/DateTimePicker.vue', import.meta.url), 'utf8')
const { descriptor } = parse(source)
const compiled = ts.transpileModule(descriptor.scriptSetup.content + '\nexport { parseValue, selectDay, selectHour, confirm, clear, open };', {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText

test('date and time changes are drafts until confirmation', () => {
  const events = []
  const module = { exports: {} }
  vm.runInNewContext(compiled, {
    module, exports: module.exports,
    defineProps: () => ({ modelValue: '' }),
    defineEmits: () => (...args) => events.push(args),
    require: () => ({ ref: value => ({ value }), computed: fn => ({ get value() { return fn() } }), onMounted() {}, onUnmounted() {}, nextTick() {} }),
  })
  const picker = module.exports
  picker.parseValue('2026-09-10T10:07')
  picker.open.value = true
  picker.selectDay(11, true)
  picker.selectHour(12)
  assert.equal(events.length, 0)
  picker.confirm()
  assert.equal(events[0][1], '2026-09-11 12:07:00')
  assert.equal(picker.open.value, false)
  picker.clear({ stopPropagation() {} })
  assert.equal(events[1][1], '')
  assert.match(descriptor.template.content, />确定<\/button>/)
})
