import assert from 'node:assert/strict'
import { execFile, spawn } from 'node:child_process'
import { createHash, randomBytes } from 'node:crypto'
import { createWriteStream } from 'node:fs'
import { cp, mkdir, mkdtemp, readFile, readdir, stat, writeFile } from 'node:fs/promises'
import { createServer } from 'node:http'
import { createServer as tcpServer } from 'node:net'
import { tmpdir } from 'node:os'
import { basename, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { createRequire } from 'node:module'
import { promisify } from 'node:util'

const repo = fileURLToPath(new URL('..', import.meta.url))
const source = resolve(process.argv[2] || resolve(repo, 'bin/NodeSocket/net9.0'))
const root = await mkdtemp(resolve(tmpdir(), 'lywaf-managed-program-'))
const binary = resolve(root, 'app')
const linuxSource = process.env.LYWAF_TEST_LINUX_DIRECTORY
const linuxBinary = resolve(root, 'app-linux')
const linuxDistro = process.env.LYWAF_TEST_WSL_DISTRO || 'Debian'
const executeFile = promisify(execFile)
const execute = (file, args) => executeFile(file, args, { timeout: 15000, windowsHide: true })
const linuxPath = path => {
  const full = resolve(path).replaceAll('\\', '/')
  assert.match(full, /^[A-Za-z]:\//)
  return `/mnt/${full[0].toLowerCase()}${full.slice(2)}`
}
const instances = [], checks = []
let browser, createViteServer
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms))
const hash = value => createHash('sha256').update(value).digest('hex')
const upstream = createServer((req, res) => { res.setHeader('Content-Type', 'application/json'); res.end('{"upstream":"fixture"}') })
const bounded = (promise, ms) => Promise.race([promise, new Promise((_, reject) => {
  const timer = setTimeout(() => reject(new Error('Timed out')), ms); timer.unref()
})])
async function freePort() {
  const server = tcpServer()
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const port = server.address().port
  await new Promise(resolve => server.close(resolve))
  return port
}
async function until(label, predicate, seconds = 60) {
  const end = Date.now() + seconds * 1000
  while (Date.now() < end) {
    const failed = instances.find(instance => instance.child.exitCode !== null)
    if (failed) throw new Error(`${failed.name} exited (${failed.child.exitCode}); inspect ${failed.log}`)
    try { if (await predicate()) return } catch (error) { if (error instanceof assert.AssertionError) throw error }
    await sleep(200)
  }
  throw new Error(`Timed out: ${label}`)
}
async function request(instance, path, method = 'GET', body, authenticated = true) {
  const response = await fetch(instance.url + path, { method, signal: AbortSignal.timeout(8000), headers: {
    ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
    ...(authenticated ? { Authorization: `Bearer ${instance.token}` } : {}),
  }, body: body === undefined ? undefined : JSON.stringify(body) })
  const text = await response.text()
  assert.ok(response.ok, `${instance.name}: ${method} ${path} returned ${response.status}: ${text.slice(0, 300)}`)
  return JSON.parse(text)
}
async function login(instance) {
  const { timestamp } = await request(instance, '/api/auth/time', 'GET', undefined, false)
  const result = await request(instance, '/api/auth/login', 'POST', {
    username: 'fixture-admin', passwordHash: hash(hash(instance.password) + timestamp), timestamp,
  }, false)
  assert.equal(result.success, true)
  instance.token = result.token
}
async function start(name) {
  const directory = resolve(root, name), port = await freePort(), proxy = await freePort()
  await mkdir(directory)
  const password = randomBytes(32).toString('hex')
  await writeFile(resolve(directory, '.lywaf.auth.json'), JSON.stringify({ username: 'fixture-admin',
    passwordHash: hash(password), jwtSecret: randomBytes(64).toString('base64'), role: 'admin', sessionVersion: 1 }))
  const configuration = {
    WafInfos: { Listens: [{ Host: '127.0.0.1', Port: proxy }], ControlListen: { Host: '127.0.0.1', Port: port } },
    Lifecycle: { DrainTimeoutSeconds: 2 },
    ReverseProxy: { Routes: { fixture: { ClusterId: 'fixture', Match: { Path: '/{**catch-all}' } } },
      Clusters: { fixture: { Destinations: { upstream: { Address: `http://127.0.0.1:${upstream.address().port}/` } } } } },
    Statistic: { CcRules: [{ Id: 'managed-fixture', Name: 'Managed fixture', Scope: 'Cluster', UseManagedMembership: true,
      RequireCompleteCoverage: true, Period: 10, Threshold: 100, Action: 'Block', ActionSeconds: 30,
      Conditions: [{ Target: 'UrlPath', Operator: 'Equal', Values: ['/shared'] }] }] },
  }
  await writeFile(resolve(directory, 'fixture.yaml'), JSON.stringify(configuration))
  const log = resolve(directory, 'process.log'), output = createWriteStream(log)
  const linux = !!linuxSource && name === 'b'
  const child = spawn(linux ? 'wsl' : 'dotnet', linux
    ? ['-d', linuxDistro, '--cd', linuxPath(directory), '--exec', linuxPath(resolve(linuxBinary, 'LyWaf')), 'run', '-c', 'fixture.yaml']
    : [resolve(binary, 'LyWaf.dll'), 'run', '-c', 'fixture.yaml'], {
    cwd: directory, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Production', DOTNET_ENVIRONMENT: 'Production' },
  })
  child.stdout.pipe(output, { end: false }); child.stderr.pipe(output, { end: false })
  const instance = { name, directory, port, proxy, password, child, output, log, linux, url: `http://127.0.0.1:${port}`, token: '' }
  instances.push(instance)
  await until(`${name} initial readiness`, async () => {
    const response = await fetch(instance.url + '/api/auth/time', { signal: AbortSignal.timeout(1000) })
    return response.ok
  })
  await login(instance)
  if (linux) instance.linuxPid = Number((await readFile(resolve(directory, 'lyxwqf.pid'), 'utf8')).trim())
  assert.equal((await request(instance, '/api/node-setup')).settings.activeMode, 'Standalone')
  return instance
}
async function proxyWorks(instance) {
  const response = await fetch(`http://127.0.0.1:${instance.proxy}/ordinary`, { signal: AbortSignal.timeout(5000) })
  assert.equal(response.status, 200)
  assert.equal((await response.json()).upstream, 'fixture')
}
async function configure(instance, body) {
  let saved, context, vite
  try {
  if (browser) {
    vite = await createViteServer({ root: resolve(repo, 'Frontend'), server: {
      host: '127.0.0.1', port: 0, open: false, proxy: { '/api': { target: instance.url, changeOrigin: true } },
    } })
    await vite.listen()
    assert.equal(vite.config.server.proxy['/api'].target, instance.url)
    context = await browser.newContext({ viewport: { width: 1440, height: 1000 } })
    await context.addInitScript(token => {
      localStorage.setItem('lywaf_token', token); localStorage.setItem('lywaf_locale', 'en-US')
    }, instance.token)
    const page = await context.newPage(), errors = []
    page.on('pageerror', error => errors.push(error.message))
    await page.goto(`http://127.0.0.1:${vite.httpServer.address().port}/#/multi-node`)
    const panel = page.locator('section[aria-labelledby=node-setup-title]')
    await panel.getByRole('combobox').selectOption(body.mode)
    assert.notEqual(await panel.evaluate(element => getComputedStyle(element).paddingTop), '0px')
    if (body.mode === 'Node') {
      await panel.getByLabel('Controller URL').fill(body.controllerUrl)
      await panel.locator('input[type=password]').fill(body.credential)
    }
    const response = page.waitForResponse(response => new URL(response.url()).pathname.endsWith('/node-setup')
      && response.request().method() === 'PUT')
    await panel.getByRole('button', { name: 'Save', exact: true }).click()
    saved = await (await response).json()
    assert.equal(saved.success, true)
    assert.equal(saved.settings.restartRequired, true)
    assert.equal(saved.settings.activeMode, 'Standalone')
    await panel.getByRole('button', { name: 'Restart LyWaf', exact: true }).click()
    const dialog = page.getByRole('dialog', { name: 'Apply Node Settings' })
    await dialog.waitFor()
    await page.screenshot({ path: resolve(instance.directory, 'browser-confirm.png'), fullPage: true })
    await dialog.getByRole('button', { name: 'Restart LyWaf', exact: true }).click()
    await until(`${instance.name} browser role restart`, async () => {
      const result = await request(instance, '/api/node-setup')
      return result.settings.activeMode === body.mode && !result.settings.restartRequired
    })
    await page.reload()
    await panel.getByRole('combobox').waitFor()
    await page.screenshot({ path: resolve(instance.directory, 'browser-active.png'), fullPage: true })
    assert.deepEqual(errors, [])
    assert.equal((await request(instance, '/api/node-setup')).settings.runtimeSynchronizationActive, true)
    return
  }
  saved = await request(instance, '/api/node-setup', 'PUT', body)
  assert.equal(saved.settings.restartRequired, true)
  assert.equal(saved.settings.activeMode, 'Standalone')
  await request(instance, '/api/restart', 'POST')
  await until(`${instance.name} role restart`, async () => {
    const result = await request(instance, '/api/node-setup')
    return result.settings.activeMode === body.mode && !result.settings.restartRequired
  })
  assert.equal((await request(instance, '/api/node-setup')).settings.runtimeSynchronizationActive, true)
  } finally { await context?.close(); await vite?.close() }
}
let failure, result
try {
  console.log(JSON.stringify({ stage: 'starting', artifacts: root }))
  if (linuxSource) {
    const probe = await execute('wsl', ['-d', linuxDistro, '--exec', 'sh', '-c', 'uname -s', root])
    assert.equal(probe.stdout.trim(), 'Linux')
    checks.push('bounded-linux-preflight')
  }
  // Copy runtime binaries only: never copy user config, authentication, or runtime state from the build output.
  const copyRuntime = async (from, to) => cp(from, to, { recursive: true, filter: async path => {
    const name = basename(path)
    if (name.startsWith('.') || /^(config|appsettings|logs$|data$|temp$)/i.test(name)) return false
    return (await stat(path)).isDirectory() || name === 'LyWaf' || /\.(dll|pdb|exe|so|dylib)$|\.(deps|runtimeconfig)\.json$/i.test(name)
  } })
  await copyRuntime(source, binary)
  let linuxDllHash
  if (linuxSource) {
    await copyRuntime(resolve(linuxSource), linuxBinary)
    await execute('wsl', ['-d', linuxDistro, '--exec', 'chmod', 'u+x', linuxPath(resolve(linuxBinary, 'LyWaf'))])
    linuxDllHash = hash(await readFile(resolve(linuxBinary, 'LyWaf.dll')))
  }
  const dllHash = hash(await readFile(resolve(binary, 'LyWaf.dll')))
  await new Promise(resolve => upstream.listen(0, '127.0.0.1', resolve))
  const controller = await start('controller'), a = await start('a'), b = await start('b')
  if (process.argv[3]) {
    process.chdir(resolve(repo, 'Frontend'))
    const { chromium } = createRequire(import.meta.url)(process.argv[3])
    browser = await chromium.launch({ headless: true, channel: 'chrome' })
    createViteServer = (await import(pathToFileURL(resolve(repo, 'Frontend/node_modules/vite/dist/node/index.js')).href)).createServer
  }
  for (const instance of instances) await proxyWorks(instance)
  checks.push('all-three-standalone-proxies')
  await configure(controller, { mode: 'Controller' })
  for (const instance of [a, b]) {
    const issued = await request(controller, '/api/node-setup/peers', 'POST', { name: instance.name })
    await configure(instance, { mode: 'Node', controllerUrl: controller.url, credential: issued.credential })
  }
  checks.push('real-api-role-restart-and-default-synchronization')
  if (browser) checks.push('full-spa-role-save-confirm-and-restart-with-real-backends')
  let dashboard
  await until('two healthy nodes over controller API', async () => {
    dashboard = (await request(controller, '/api/distribution/dashboard')).dashboard
    return dashboard.nodes.length === 2 && dashboard.nodes.every(node => node.healthyActive)
  })
  for (const instance of instances) await proxyWorks(instance)
  checks.push('controller-and-nodes-retain-proxy-service')
  for (const instance of instances) {
    const status = await request(instance, '/api/runtime-state/status')
    const setup = await request(instance, '/api/node-setup')
    assert.equal(status.status.nodeId, setup.settings.nodeId)
    assert.equal(status.status.capacityError, null)
    assert.equal(status.status.droppedMutations, 0)
    const rule = await request(instance, '/api/plugins/analysis/proxy/cc/advanced/managed-fixture')
    assert.equal(rule.rule.useManagedMembership, true)
    assert.deepEqual(rule.rule.expectedNodes, [])
  }
  const boundary = Math.ceil(Date.now() / 10000) * 10000
  await sleep(boundary - Date.now() + 50)
  for (const [index, instance] of instances.entries()) {
    for (let i = 0; i < [30, 40, 35][index]; i++) {
      const response = await fetch(`http://127.0.0.1:${instance.proxy}/shared`, { signal: AbortSignal.timeout(5000) })
      assert.ok([200, 403, 429].includes(response.status), `Unexpected traffic status ${response.status}`)
      await response.arrayBuffer()
    }
  }
  const decisions = []
  await until('global CC applied on all three real programs', async () => {
    decisions.length = 0
    for (const instance of instances) {
      const logs = await request(instance, '/api/plugins/analysis/proxy/ban-logs')
      const decision = logs.logs?.find(log => log.policy?.ruleId === 'managed-fixture' && log.execution?.status === 'Applied')
      if (!decision) return false
      decisions.push(decision)
    }
    return true
  }, 12)
  assert.equal(new Set(decisions.map(item => item.decisionId)).size, 1)
  for (const instance of instances) {
    const response = await fetch(`http://127.0.0.1:${instance.proxy}/shared`)
    assert.ok([403, 429].includes(response.status))
    await response.arrayBuffer()
  }
  checks.push('automatic-membership-shared-cc-and-three-node-enforcement')
  // A CC subject ban applies beyond the trigger path; let it expire before proxy checks.
  await sleep(31000)
  const original = JSON.parse((await request(controller, '/api/config/file')).content)
  const candidate = { ReverseProxy: structuredClone(original.ReverseProxy) }
  candidate.ReverseProxy.Routes.fixture.Metadata = { ManagedFixture: 'distributed-over-control-socket' }
  const savedVersion = await request(controller, '/api/config/file', 'POST', {
    content: JSON.stringify(candidate), reload: false,
  })
  assert.ok(savedVersion.versionId)
  const nodeIds = []
  for (const instance of [a, b]) nodeIds.push((await request(instance, '/api/node-setup')).settings.nodeId)
  const created = await request(controller, '/api/distribution/deployments', 'POST', {
    versionId: savedVersion.versionId, nodeIds, canaryCount: 1, batchSize: 1, pauseAfterEachBatch: false,
  })
  let deployment
  await until('configuration committed on Windows and Linux nodes', async () => {
    deployment = (await request(controller, '/api/distribution/deployments')).deployments.find(item => item.id === created.deployment.id)
    assert.ok(!['Failed', 'Paused'].includes(deployment.state), JSON.stringify(deployment))
    return deployment.state === 'Completed'
  })
  assert.ok(Object.values(deployment.targets).every(target => target.state === 'Ack'))
  for (const instance of [a, b]) {
    const content = (await request(instance, '/api/config/file')).content
    assert.match(content, /distributed-over-control-socket/)
    assert.match(content, new RegExp(`\\b${instance.port}\\b`))
    assert.match(content, new RegExp(`\\b${instance.proxy}\\b`))
    assert.equal((await request(instance, '/api/node-setup')).settings.activeMode, 'Node')
    await proxyWorks(instance)
  }
  await request(controller, `/api/distribution/deployments/${deployment.id}/rollback`, 'POST')
  await until('configuration rollback confirmed on both nodes', async () => {
    const current = (await request(controller, '/api/distribution/deployments')).deployments.find(item => item.id === deployment.id)
    return current.state === 'RolledBack' && Object.values(current.targets).every(target => target.state === 'RolledBack')
  })
  for (const instance of [a, b]) {
    assert.doesNotMatch((await request(instance, '/api/config/file')).content, /distributed-over-control-socket/)
    await proxyWorks(instance)
  }
  checks.push('real-api-configuration-distribution-local-listeners-preserved-and-rollback')
  if (linuxSource) checks.push('windows-controller-windows-node-linux-node-over-control-websocket')
  result = { success: true, dllHash, linuxDllHash, checks,
    configurationDeployment: { id: deployment.id, versionId: savedVersion.versionId, committedTargets: deployment.targets, rolledBack: true },
    instances: instances.map(({ name, port, proxy, child, linux, linuxPid }) => ({ name, port, proxy, pid: child.pid,
      platform: linux ? 'linux' : 'windows', linuxPid })),
    decisionIds: decisions.map(item => item.decisionId) }
} catch (error) {
  failure = error
  for (const instance of instances) {
    if (instance.child.exitCode !== null) continue
    const diagnostics = {}
    for (const path of ['/api/node-setup', '/api/runtime-state/status', '/api/plugins/analysis/proxy/ban-logs', '/api/distribution/deployments']) {
      try { diagnostics[path] = await request(instance, path) } catch (error) { diagnostics[path] = { error: error.message } }
    }
    await writeFile(resolve(instance.directory, 'diagnostics.json'), JSON.stringify(diagnostics, null, 2))
  }
  await writeFile(resolve(root, 'failure.json'), JSON.stringify({ success: false, checks, message: error.message }, null, 2))
  console.error(JSON.stringify({ success: false, artifacts: root, checks, message: error.message }))
} finally {
  await browser?.close()
  for (const instance of instances.toReversed()) {
    if (instance.child.exitCode === null) {
      try { await request(instance, '/api/stop', 'POST') } catch {}
      try { await bounded(new Promise(resolve => instance.child.once('exit', resolve)), 8000) } catch {
        if (instance.linux) {
          if (!instance.linuxPid) {
            try { instance.linuxPid = Number((await readFile(resolve(instance.directory, 'lyxwqf.pid'), 'utf8')).trim()) } catch {}
          }
          assert.ok(Number.isSafeInteger(instance.linuxPid) && instance.linuxPid > 1)
          await execute('wsl', ['-d', linuxDistro, '--exec', 'sh', '-c',
            'test "$(readlink /proc/$1/cwd)" = "$2" && test "$(tr "\\000" "\\n" < /proc/$1/cmdline | head -1)" = "$3" && kill -TERM "$1"',
            'sh', String(instance.linuxPid), linuxPath(instance.directory), linuxPath(resolve(linuxBinary, 'LyWaf'))])
        } else {
        instance.child.kill()
        }
        await bounded(new Promise(resolve => instance.child.once('exit', resolve)), 5000)
      }
    }
    instance.output.end()
  }
  upstream.closeAllConnections()
  await new Promise(resolve => upstream.close(resolve))
}
if (!failure) {
  for (const instance of instances) {
    assert.ok(instance.child.exitCode !== null || instance.child.signalCode !== null, `${instance.name} is still running`)
    if (instance.linux) await execute('wsl', ['-d', linuxDistro, '--exec', 'sh', '-c',
      'test ! -d /proc/$1 || test "$(readlink /proc/$1/cwd)" != "$2"', 'sh', String(instance.linuxPid), linuxPath(instance.directory)])
    assert.doesNotMatch(await readFile(instance.log, 'utf8'), /node_invalid_payload|ChannelClosedException/)
    const logs = []
    for (const name of await readdir(resolve(instance.directory, 'logs'))) {
      if (name.endsWith('.log')) {
        const text = await readFile(resolve(instance.directory, 'logs', name), 'utf8')
        logs.push(text)
        assert.doesNotMatch(text, /node_invalid_payload|ChannelClosedException|Unhandled exception|BackgroundService failed|后台刷写服务启动失败/)
      }
    }
    const audits = [...logs.join('\n').matchAll(/Hosted service shutdown audit: running=(\d+), cancelled=(\d+), faulted=(\d+)/g)]
    assert.equal(audits.length, 2, `${instance.name}: expected role-switch and final shutdown audits`)
    assert.ok(audits.every(match => match.slice(1).every(value => value === '0')), `${instance.name}: unsuccessful background task shutdown`)
  }
  result.checks.push('all-hosted-background-services-shutdown-successfully')
  result.checks.push('all-test-processes-stopped-without-invalid-payload-or-closed-channel-errors')
  await writeFile(resolve(root, 'result.json'), JSON.stringify(result, null, 2))
  console.log(JSON.stringify({ success: true, artifacts: root, checks }))
}
if (failure) process.exitCode = 1
