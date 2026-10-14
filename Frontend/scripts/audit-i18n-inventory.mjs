import { readFileSync, readdirSync, existsSync, mkdirSync, writeFileSync } from 'node:fs'
import { resolve, dirname, relative } from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'
import { scan } from './audit-i18n.mjs'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const repo = resolve(root, '..')
const normalize = path => relative(repo, path).replaceAll('\\', '/')
const files = directory => readdirSync(directory, { withFileTypes: true }).flatMap(entry =>
  entry.isDirectory() ? files(resolve(directory, entry.name)) : [resolve(directory, entry.name)])
const sourceFiles = files(resolve(root, 'src')).filter(path => /\.(vue|ts)$/.test(path))
const chinese = /\p{Script=Han}/u
const parse = path => ts.createSourceFile(path, readFileSync(path, 'utf8'), ts.ScriptTarget.Latest, true)
const classify = (file, text) => {
  if (file.endsWith('/common/LanguageSelect.vue') && text === '简体中文') return 'native-language-name'
  if (file.endsWith('/GeneralSettings.vue') && ['正常关闭', '异常退出'].includes(text)) return 'protocol-comparison'
  if (file.endsWith('/Layout/Sidebar.vue')) return 'legacy-translated-navigation-or-state'
  if (file.endsWith('/utils/geoNameMap.ts')) return 'geographic-data'
  return 'review'
}
const visit = (node, fn) => { fn(node); ts.forEachChild(node, child => visit(child, fn)) }
const resolveImport = (from, specifier) => {
  const path = specifier.startsWith('@/') ? resolve(root, 'src', specifier.slice(2)) : resolve(dirname(from), specifier)
  return [path, `${path}.ts`, resolve(path, 'index.ts')].find(candidate => existsSync(candidate) && /\.(vue|ts)$/.test(candidate))
}

const routeFiles = [resolve(root, 'src/router/index.ts'), ...sourceFiles.filter(path => /[\\/]plugins[\\/].+[\\/]plugin\.ts$/.test(path))]
const routes = []
for (const file of routeFiles) visit(parse(file), node => {
  if (!ts.isObjectLiteralExpression(node)) return
  const props = new Map(node.properties.filter(ts.isPropertyAssignment).map(prop => [prop.name.getText().replaceAll(/['"]/g, ''), prop.initializer]))
  const path = props.get('path')
  if (!path || !ts.isStringLiteral(path)) return
  let component
  const value = props.get('component')
  if (value) visit(value, child => {
    if (ts.isCallExpression(child) && child.expression.kind === ts.SyntaxKind.ImportKeyword && ts.isStringLiteral(child.arguments[0])) component = resolveImport(file, child.arguments[0].text)
  })
  if (component) routes.push({ path: path.text, component: normalize(component), owner: normalize(component).includes('/plugins/') ? 'plugin' : 'host' })
})

const entries = sourceFiles.map(file => {
  const source = readFileSync(file, 'utf8')
  const findings = scan(file).map(({ text, line, kind }) => ({ text, line, kind, classification: classify(normalize(file), text) }))
  const imports = []
  // Vue script content is valid TypeScript after dropping the SFC wrappers.
  const script = file.endsWith('.vue') ? source.match(/<script\b[^>]*>([\s\S]*?)<\/script>/)?.[1] || '' : source
  const ast = ts.createSourceFile(file, script, ts.ScriptTarget.Latest, true)
  visit(ast, node => {
    if (ts.isImportDeclaration(node) && ts.isStringLiteral(node.moduleSpecifier)) {
      const dependency = resolveImport(file, node.moduleSpecifier.text)
      if (dependency) imports.push(normalize(dependency))
    }
  })
  return {
    file: normalize(file), count: findings.length, reviewCount: findings.filter(item => item.classification === 'review').length,
    templateCount: findings.filter(item => item.kind === 'text' || item.kind === 'attribute').length,
    findings, imports,
    dateFormatCandidates: source.split(/\r?\n/).flatMap((line, index) => /toLocale(?:String|DateString|TimeString)\(/.test(line) ? [{ line: index + 1, source: line.trim() }] : []),
  }
})
const byFile = new Map(entries.map(entry => [entry.file, entry]))
const closure = file => {
  const found = new Set()
  function add(name) {
    if (found.has(name)) return
    found.add(name)
    for (const dependency of byFile.get(name)?.imports || []) add(dependency)
  }
  add(file)
  return [...found].filter(name => name.endsWith('.vue'))
}
for (const route of routes) {
  route.components = closure(route.component)
  route.candidateCount = route.components.reduce((sum, file) => sum + (byFile.get(file)?.count || 0), 0)
  route.reviewCount = route.components.reduce((sum, file) => sum + (byFile.get(file)?.reviewCount || 0), 0)
  route.templateCount = route.components.reduce((sum, file) => sum + (byFile.get(file)?.templateCount || 0), 0)
}
const reachable = new Set(routes.flatMap(route => route.components))
const globalComponents = closure('Frontend/src/App.vue')
globalComponents.forEach(file => reachable.add(file))
const catalogs = files(resolve(repo, 'Plugins')).filter(path => /[\\/]Localization[\\/]zh-CN\.json$/.test(path))
catalogs.unshift(resolve(repo, 'Localization/zh-CN.json'))
const catalogChecks = catalogs.map(path => {
  const enPath = path.replace('zh-CN.json', 'en-US.json')
  const zh = JSON.parse(readFileSync(path)), en = existsSync(enPath) ? JSON.parse(readFileSync(enPath)) : {}
  return { file: normalize(path), keyCount: Object.keys(zh).length,
    missingEnglish: Object.keys(zh).filter(key => !Object.hasOwn(en, key)),
    missingChinese: Object.keys(en).filter(key => !Object.hasOwn(zh, key)),
    chineseInEnglish: Object.entries(en).filter(([, value]) => chinese.test(value)).map(([key]) => key),
  }
})
const backendCandidates = files(resolve(repo, 'Control')).filter(path => path.endsWith('.cs')).flatMap(path => {
  const findings = readFileSync(path, 'utf8').split(/\r?\n/).flatMap((line, index) =>
    !line.trim().startsWith('//') && chinese.test(line) && /\b(?:message|Message|description|Description|title|Title)\s*=/.test(line)
      ? [{ line: index + 1, source: line.trim() }] : [])
  return findings.length ? [{ file: normalize(path), count: findings.length, findings }] : []
})
const pluginCandidates = files(resolve(repo, 'Plugins')).filter(path => path.endsWith('.cs')).flatMap(path => {
  const findings = readFileSync(path, 'utf8').split(/\r?\n/).flatMap((line, index) =>
    !line.trim().startsWith('//') && chinese.test(line) && /\b(?:message|Message|Description|Name|Label)\s*=/.test(line)
      ? [{ line: index + 1, source: line.trim() }] : [])
  return findings.length ? [{ file: normalize(path), count: findings.length, findings }] : []
})
const htmlCandidates = [resolve(root, 'index.html'), ...files(resolve(repo, 'templates')), resolve(repo, 'wwwroot/index.html')]
  .filter(path => path.endsWith('.html')).map(path => ({ file: normalize(path), findings: readFileSync(path, 'utf8').split(/\r?\n/)
    .flatMap((line, index) => chinese.test(line) && !line.trim().startsWith('<!--') ? [{ line: index + 1, source: line.trim() }] : []) }))
const report = {
  generatedAt: new Date().toISOString(),
  scope: 'Static candidates, not confirmed untranslated strings. Includes dialogs in source; does not prove all runtime states. Geographic data, protocol values and legacy translated navigation need manual classification.',
  summary: { routes: routes.length, vueFiles: entries.filter(e => e.file.endsWith('.vue')).length,
    routeAndGlobalComponentFiles: reachable.size, routesWithCandidates: routes.filter(r => r.reviewCount > 0).length,
    vueFilesWithCandidates: entries.filter(e => e.file.endsWith('.vue') && e.count > 0).length,
    backendCandidateFiles: backendCandidates.length, backendCandidateLines: backendCandidates.reduce((sum, e) => sum + e.count, 0) },
  routes, globalComponents,
  files: entries,
  unreferencedVueFiles: entries.filter(e => e.file.endsWith('.vue') && !reachable.has(e.file)).map(e => ({ file: e.file, count: e.count })),
  catalogChecks, backendCandidates, pluginCandidates, htmlCandidates,
}
const output = resolve(process.argv[2] || resolve(repo, 'artifacts/i18n-inventory.json'))
mkdirSync(dirname(output), { recursive: true })
writeFileSync(output, JSON.stringify(report, null, 2) + '\n')
console.log(JSON.stringify(report.summary, null, 2))
for (const route of routes) console.log(`${route.path}\t${route.candidateCount}\t${route.templateCount}\t${route.component}`)
console.log(`Report: ${output}`)
