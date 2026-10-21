import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { createHash, randomBytes } from 'node:crypto'
import { createWriteStream } from 'node:fs'
import { cp, mkdir, mkdtemp, readFile, readdir, stat, writeFile } from 'node:fs/promises'
import { createServer } from 'node:http'
import { createServer as createTcpServer } from 'node:net'
import { tmpdir } from 'node:os'
import { basename, resolve } from 'node:path'

const source = resolve(process.argv[2] || 'bin/NodeSocket/net9.0')
const root = await mkdtemp(resolve(tmpdir(), 'lywaf-restart-'))
const binary = resolve(root, 'app'), directory = resolve(root, 'instance')
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms))
const hash = value => createHash('sha256').update(value).digest('hex')
let child, token, slowStarted, success = false
const upstream = createServer(async (req, res) => {
  if (req.url === '/slow') { slowStarted?.(); await sleep(300) }
  res.end('fixture')
})
async function port() {
  const server = createTcpServer()
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const value = server.address().port
  await new Promise(resolve => server.close(resolve))
  return value
}
const control = await port(), proxy = await port(), base = `http://127.0.0.1:${control}`
async function request(path, method = 'GET', body) {
  const response = await fetch(base + path, { method, signal: AbortSignal.timeout(5000), headers: {
    ...(token ? { Authorization: `Bearer ${token}` } : {}), 'Content-Type': 'application/json',
  }, body: body === undefined ? undefined : JSON.stringify(body) })
  assert.ok(response.ok, `${method} ${path}: ${response.status}`)
  return response.json()
}
async function logs() {
  const files = await readdir(resolve(directory, 'logs')).catch(() => [])
  return (await Promise.all(files.filter(name => name.endsWith('.log')).map(name => readFile(resolve(directory, 'logs', name), 'utf8')))).join('\n')
}
async function until(label, condition) {
  const end = Date.now() + 45000
  while (Date.now() < end) {
    assert.equal(child.exitCode, null, 'Owned process exited unexpectedly')
    try { if (await condition()) return } catch (error) { if (error instanceof assert.AssertionError) throw error }
    await sleep(100)
  }
  throw new Error(`Timed out: ${label}`)
}
const output = createWriteStream(resolve(root, 'process.log'))
try {
  console.log(JSON.stringify({ stage: 'starting', artifacts: root }))
  await cp(source, binary, { recursive: true, filter: async path => {
    const name = basename(path)
    if (name.startsWith('.') || /^(config|appsettings|logs$|data$|temp$)/i.test(name)) return false
    return (await stat(path)).isDirectory() || /\.(dll|pdb|exe|so|dylib)$|\.(deps|runtimeconfig)\.json$/i.test(name)
  } })
  await mkdir(directory)
  await new Promise(resolve => upstream.listen(0, '127.0.0.1', resolve))
  const password = randomBytes(32).toString('hex')
  await writeFile(resolve(directory, '.lywaf.auth.json'), JSON.stringify({ username: 'fixture-admin',
    passwordHash: hash(password), jwtSecret: randomBytes(64).toString('base64'), role: 'admin', sessionVersion: 1 }))
  await writeFile(resolve(directory, 'fixture.yaml'), JSON.stringify({
    WafInfos: { Listens: [{ Host: '127.0.0.1', Port: proxy }], ControlListen: { Host: '127.0.0.1', Port: control } },
    Lifecycle: { DrainTimeoutSeconds: 2 }, ServiceDiscovery: { Enabled: true },
    DuckDb: { Enabled: true, DatabasePath: 'bin/fixture.duckdb', FlushIntervalSeconds: 1 },
    ReverseProxy: { Routes: { fixture: { ClusterId: 'fixture', Match: { Path: '/{**catch-all}' } } },
      Clusters: { fixture: { Destinations: { fixture: { Address: `http://127.0.0.1:${upstream.address().port}/` } } } } },
  }))
  child = spawn('dotnet', [resolve(binary, 'LyWaf.dll'), 'run', '-c', 'fixture.yaml'], {
    cwd: directory, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
  })
  child.stdout.pipe(output, { end: false }); child.stderr.pipe(output, { end: false })
  await until('initial readiness', async () => (await fetch(base + '/api/auth/time', { signal: AbortSignal.timeout(1000) })).ok)
  const { timestamp } = await request('/api/auth/time')
  token = (await request('/api/auth/login', 'POST', { username: 'fixture-admin', passwordHash: hash(hash(password) + timestamp), timestamp })).token
  assert.ok(token)
  for (const [index, delay] of [0, 0, 6500].entries()) {
    if (delay) await sleep(delay)
    const started = new Promise(resolve => { slowStarted = resolve })
    const inflight = fetch(`http://127.0.0.1:${proxy}/slow`, { signal: AbortSignal.timeout(5000) })
    await started
    await request('/api/restart', 'POST')
    assert.equal(await (await inflight).text(), 'fixture')
    await until(`restart ${index + 1}`, async () => {
      const text = await logs()
      if ((text.match(/服务就绪\(到 ApplicationStarted\)/g) || []).length < index + 2) return false
      return (await request('/api/auth/time')).timestamp > 0
    })
    const response = await fetch(`http://127.0.0.1:${proxy}/ready`, { signal: AbortSignal.timeout(3000) })
    assert.equal(await response.text(), 'fixture')
  }
  await request('/api/stop', 'POST')
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Owned process did not stop gracefully')), 15000)
    child.once('exit', () => { clearTimeout(timer); resolve() })
  })
  assert.equal(child.exitCode, 0)
  const text = await logs()
  const audits = [...text.matchAll(/Hosted service shutdown audit: running=(\d+), cancelled=(\d+), faulted=(\d+)/g)]
  assert.equal(audits.length, 4)
  assert.ok(audits.every(match => match.slice(1).every(value => value === '0')), JSON.stringify(audits.map(match => match[0])))
  assert.doesNotMatch(text, /TaskCanceledException|OperationCanceledException|BackgroundService failed|后台刷写服务启动失败|Unhandled exception|\bERROR\b/)
  const result = { success: true, pid: child.pid, dllHash: hash(await readFile(resolve(binary, 'LyWaf.dll'))),
    restarts: 3, audits: audits.map(match => match[0]), checks: ['early-and-steady-state-restarts', 'inflight-request-drained',
      'proxy-ready-after-each-restart', 'all-background-tasks-successful', 'no-cancellation-error-logs', 'owned-process-exited-zero'] }
  await writeFile(resolve(root, 'result.json'), JSON.stringify(result, null, 2))
  success = true
  console.log(JSON.stringify({ success: true, artifacts: root, ...result }))
} catch (error) {
  await writeFile(resolve(root, 'failure.json'), JSON.stringify({ success: false, message: error.message }, null, 2))
  console.error(JSON.stringify({ success: false, artifacts: root, message: error.message }))
  process.exitCode = 1
} finally {
  if (child && child.exitCode === null) {
    try { await request('/api/stop', 'POST') } catch {}
    await Promise.race([new Promise(resolve => child.once('exit', resolve)), sleep(8000)])
    if (child.exitCode === null) {
      child.kill()
      await new Promise(resolve => child.once('exit', resolve))
    }
  }
  output.end()
  upstream.closeAllConnections()
  await new Promise(resolve => upstream.close(resolve))
  if (!success) process.exitCode = 1
}
