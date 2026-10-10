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
