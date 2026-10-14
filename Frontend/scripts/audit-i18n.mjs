import { readFileSync, readdirSync } from 'node:fs'
import { resolve, relative } from 'node:path'
import { fileURLToPath } from 'node:url'
import { parse as parseSfc } from '@vue/compiler-sfc'
import { parse as parseTemplate } from '@vue/compiler-dom'
import ts from 'typescript'

const root = fileURLToPath(new URL('../', import.meta.url))
const chinese = /\p{Script=Han}/u
export function scan(file) {
  const source = readFileSync(file, 'utf8'), results = []
  const add = (text, start, end, kind) => {
    if (chinese.test(text)) results.push({ text, start, end, kind, line: source.slice(0, start).split('\n').length })
  }
  function expression(code, offset) {
    const ast = ts.createSourceFile('source.ts', code, ts.ScriptTarget.Latest, true)
    function visit(node) {
      if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) add(node.text, offset + node.getStart(ast), offset + node.end, 'literal')
      else if (ts.isTemplateExpression(node) && chinese.test(node.getText(ast))) add(node.getText(ast), offset + node.getStart(ast), offset + node.end, 'template')
      ts.forEachChild(node, visit)
    }
    visit(ast)
  }
  if (!file.endsWith('.vue')) { expression(source, 0); return results }
  const { descriptor } = parseSfc(source)
  for (const block of [descriptor.script, descriptor.scriptSetup]) if (block) expression(block.content, block.loc.start.offset)
  if (descriptor.template) {
    const offset = descriptor.template.loc.start.offset
    function visit(node) {
      if (node.type === 2) add(node.content.trim(), offset + node.loc.start.offset, offset + node.loc.end.offset, 'text')
      if (node.type === 5) expression(node.content.content, offset + node.content.loc.start.offset)
      for (const prop of node.props || []) {
        if (prop.type === 6 && prop.value && ['title', 'tooltip', 'placeholder', 'aria-label', 'alt', 'label'].includes(prop.name))
          add(prop.value.content, offset + prop.loc.start.offset, offset + prop.loc.end.offset, 'attribute')
        if (prop.type === 7 && prop.exp) expression(prop.exp.content, offset + prop.exp.loc.start.offset)
      }
      for (const child of node.children || []) visit(child)
    }
    visit(parseTemplate(descriptor.template.content))
  }
  return results
}
function files(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => entry.isDirectory()
    ? files(resolve(directory, entry.name)) : /\.(vue|ts)$/.test(entry.name) ? [resolve(directory, entry.name)] : [])
}
if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const targets = process.argv.slice(2)
  const paths = targets.length ? targets.map(path => resolve(root, path)) : files(resolve(root, 'src'))
  for (const file of paths) {
    const findings = scan(file)
    if (findings.length) console.log(JSON.stringify({ file: relative(root, file), count: findings.length, findings }))
  }
}
