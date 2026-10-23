import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import { test } from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'
import { parse } from '@vue/compiler-sfc'

const vue = await readFile(new URL('../src/views/PluginManagement.vue', import.meta.url), 'utf8')
const script = parse(vue).descriptor.scriptSetup.content
const ast = ts.createSourceFile('editor.ts', script, ts.ScriptTarget.Latest, true)
const functions = new Set(['parseFlatToStructured', 'parseObjectArray', 'flattenItems'])
const selected = ast.statements.filter(node => ts.isFunctionDeclaration(node) && functions.has(node.name?.text) ||
  ts.isVariableStatement(node) && node.declarationList.declarations.some(d => d.name.getText(ast) === 'isBoolValue'))
const source = selected.map(node => node.getText(ast)).join('\n')
const compiled = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022 } }).outputText
const editor = vm.runInNewContext(compiled + '\n({ parseFlatToStructured, flattenItems })')
const plain = value => JSON.parse(JSON.stringify(value))

test('saved short arrays do not regain default elements on reopen', () => {
  const items = editor.parseFlatToStructured({ 'Formats:0': 'png' }, { 'Formats:0': 'jpg', 'Formats:1': 'webp' }, {}, {})
  assert.deepEqual(plain(items[0].items), ['png'])
  assert.deepEqual(plain(editor.flattenItems(items)), { 'Formats:0': 'png' })
})

test('empty arrays round trip as explicit empty collections', () => {
  const items = editor.parseFlatToStructured({ Formats: '' }, { 'Formats:0': 'jpg' }, {}, {})
  assert.deepEqual(plain(items[0].items), [])
  assert.deepEqual(plain(editor.flattenItems(items)), { Formats: '' })
})

test('empty object arrays and dictionaries retain their editor types', () => {
  const items = editor.parseFlatToStructured({ Rules: '', Headers: '' }, {}, { Rules: 'Rules', 'Rules:Path': 'Path' }, {},
    { Rules: { type: 'List`1' }, Headers: { type: 'Dictionary`2' } })
  assert.equal(items[0].type, 'object-array')
  assert.equal(items[1].type, 'dict')
  assert.deepEqual(plain(editor.flattenItems(items)), { Rules: '', Headers: '' })
})

test('plugin schema keeps response defaults beside response operations regardless of saved key order', () => {
  const flat = { 'Headers:X-Frame-Options': 'SAMEORIGIN', RequestHeaders: '', ResponseHeaders: '', AppendResponseHeaders: '', RemoveResponseHeaders: '' }
  const schema = {
    RequestHeaders: { type: 'Dictionary`2' }, Headers: { type: 'Dictionary`2' },
    ResponseHeaders: { type: 'Dictionary`2' }, AppendResponseHeaders: { type: 'Dictionary`2' }, RemoveResponseHeaders: { type: 'List`1' },
  }
  const items = editor.parseFlatToStructured(flat, { 'Headers:X-Frame-Options': 'SAMEORIGIN' }, {}, {}, schema)
  assert.deepEqual(plain(items.map(item => item.key)), Object.keys(schema))
  assert.deepEqual(plain(editor.flattenItems(items)), flat)
})

test('effective configuration shares the editor scroll area and footer cannot collapse', () => {
  const template = parse(vue).descriptor.template.ast
  const attribute = (node, name) => node.props?.find(p => p.type === 6 && p.name === name)?.value?.content
  function find(node, id) {
    if (attribute(node, 'data-testid') === id) return node
    for (const child of node.children || []) { const found = find(child, id); if (found) return found }
  }
  const dialog = find(template, 'plugin-config-dialog')
  const scroll = find(dialog, 'plugin-config-scroll')
  assert.ok(find(scroll, 'plugin-effective-config'), 'query inputs and results must scroll with the form')
  assert.match(attribute(scroll, 'class'), /min-h-0/)
  assert.match(attribute(scroll, 'class'), /overflow-y-auto/)
  const effective = find(scroll, 'plugin-effective-config')
  assert.doesNotMatch(attribute(effective, 'class'), /overflow-auto|max-h-/)
  assert.match(attribute(find(dialog, 'plugin-config-footer'), 'class'), /shrink-0/)
})

test('collection labels consume the same plugin schema localization keys as scalar fields', () => {
  assert.equal((vue.match(/fieldLabel\(item\.key, editConfigLabels\[item\.key\], editConfigPlugin\?\.id, editConfigSchema\[item\.key\]\?\.labelKey\)/g) || []).length, 4)
})
