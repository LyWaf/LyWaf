import { createServer } from 'node:http';
import { createServer as createTcpServer } from 'node:net';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { finished } from 'node:stream/promises';
import { mkdirSync, writeFileSync, readFileSync, createWriteStream, cpSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { generate } from './generate.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const profile = process.argv[2] || 'strict';
const grouped = process.argv.includes('--grouped');
if (!['default', 'strict'].includes(profile)) throw new Error('Use strict or default');
const work = resolve(root, 'artifacts/hurl-security', `${new Date().toISOString().replaceAll(':', '-')}-${profile}`);
mkdirSync(work, { recursive: true });
cpSync(resolve(root, 'Rules'), resolve(work, 'Rules'), { recursive: true });
const manifest = generate(resolve(work, 'cases'));
const received = new Set();
const origin = createServer((req, res) => {
  if (req.headers['x-hurl-case']) received.add(req.headers['x-hurl-case']);
  // Consume only; never execute payloads, follow URLs, render HTML, or access payload paths.
  req.resume();
  req.on('end', () => { res.writeHead(200, { 'X-Hurl-Origin': 'local-fixture', 'Content-Type': 'text/plain' }); res.end('origin-ok'); });
});
async function freePort() {
  const server = createTcpServer();
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const port = server.address().port;
  await new Promise(resolve => server.close(resolve)); return port;
}
async function command(executable, args, options = {}) {
  const child = spawn(executable, args, { cwd: root, windowsHide: true, ...options });
  const [code] = await once(child, 'exit');
  return code;
}
let waf;
try {
  if (await command('dotnet', ['build', '-c', 'Release', '--no-restore', '--verbosity', 'quiet'], { stdio: 'inherit' })) throw new Error('Build failed');
  origin.listen(0, '127.0.0.1'); await once(origin, 'listening');
  const proxyPort = await freePort(), controlPort = await freePort();
  const config = {
    WafInfos: { ControlListen: { Host: '127.0.0.1', Port: controlPort, IsHttps: false },
      Listens: [{ Host: '127.0.0.1', Port: proxyPort, IsHttps: false }] },
    Acme: { Enabled: false },
    // Disable automatic IP bans only in this disposable content-inspection fixture.
    Plugins: { DisabledPlugins: ['analysis'] },
    Protect: profile === 'strict' ? { SemanticDetectionEnabled: true, SemanticDetectionAction: 'Reject' } : {},
    ReverseProxy: { Routes: { regression: { ClusterId: 'regression', Match: { Path: '/{**catch-all}' } } },
      Clusters: { regression: { HealthCheck: { Active: { Enabled: false } },
        Destinations: { local: { Address: `http://127.0.0.1:${origin.address().port}/` } } } } }
  };
  // JSON is valid YAML; the production loader reads this isolated YAML file.
  writeFileSync(resolve(work, 'config.yaml'), JSON.stringify(config, null, 2));
  const log = createWriteStream(resolve(work, 'server.log'));
  waf = spawn('dotnet', [resolve(root, 'bin/Release/net9.0/LyWaf.dll'), 'run', '-c', 'config.yaml'], { cwd: work, windowsHide: true });
  waf.stdout.pipe(log); waf.stderr.pipe(log, { end: false });
  const base = `http://127.0.0.1:${proxyPort}`;
  let ready = false;
  for (let i = 0; i < 120; i++) {
    if (waf.exitCode !== null) throw new Error(`WAF exited: see ${work}/server.log`);
    try {
      const r = await fetch(`${base}/control`, { signal: AbortSignal.timeout(1000) });
      ready = r.status === 200 && r.headers.get('x-hurl-origin') === 'local-fixture';
      await r.arrayBuffer(); if (ready) break;
    } catch {}
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  if (!ready) throw new Error(`Proxy not ready: see ${work}/server.log`);
  const stdout = createWriteStream(resolve(work, 'hurl.json'));
  const stderr = createWriteStream(resolve(work, 'hurl.log'));
  const child = spawn('hurl', ['--test', '--jobs', '1', '--continue-on-error', '--json', '--no-color',
    '--noproxy', '*', '--path-as-is', '--connect-timeout', '2', '--max-time', '5',
    '--variable', `base_url=${base}`, '--glob', (grouped ? resolve(root, 'tests/hurl-security/cases/*.hurl') : resolve(work, 'cases/*.hurl')).replaceAll('\\', '/'),
    '--report-junit', resolve(work, 'junit.xml'), '--report-html', resolve(work, 'html')], { windowsHide: true });
  child.stdout.pipe(stdout); child.stderr.pipe(stderr);
  const [code] = await once(child, 'exit');
  await Promise.all([finished(stdout), finished(stderr)]);
  const hurlResults = readFileSync(resolve(work, 'hurl.json'), 'utf8').trim().split('\n').filter(Boolean).map(line => JSON.parse(line));
  const byId = new Map();
  for (const file of hurlResults) for (const entry of file.entries || []) {
    const id = entry.calls?.[0]?.request?.headers?.find(h => h.name.toLowerCase() === 'x-hurl-case')?.value;
    if (id) byId.set(id, entry);
  }
  const passed = entry => entry?.asserts?.length > 0 && entry.asserts.every(a => a.success);
  const results = manifest.map(c => {
    const r = byId.get(c.id);
    return { ...c, status: r?.calls?.at(-1)?.response?.status ?? null,
      hurlPassed: passed(r) && passed(byId.get(`${c.id}-before`)) && passed(byId.get(`${c.id}-after`)), reachedOrigin: received.has(c.id),
      beforeReached: received.has(`${c.id}-before`), afterReached: received.has(`${c.id}-after`) };
  });
  writeFileSync(resolve(work, 'origin-results.json'), JSON.stringify(results, null, 2));
  const summary = { profile, grouped, total: results.length, attacks: results.filter(c => c.attack).length,
    attackReachedOrigin: results.filter(c => c.attack && c.reachedOrigin).length,
    benign: results.filter(c => !c.attack).length,
    benignNotReached: results.filter(c => !c.attack && !c.reachedOrigin).length,
    blocked403: results.filter(c => c.attack && c.status === 403 && !c.reachedOrigin).length,
    missingResults: results.filter(c => c.status === null).length,
    hurlPassed: results.filter(c => c.hurlPassed).length,
    failedControls: results.filter(c => !c.beforeReached || !c.afterReached).length, hurlExitCode: code };
  writeFileSync(resolve(work, 'summary.json'), JSON.stringify(summary, null, 2));
  const categories = [...new Set(results.map(c => c.category))];
  const report = [`# Hurl Security Results (${profile})`, '', `Run: ${new Date().toISOString()}`, '',
    '```json', JSON.stringify(summary, null, 2), '```', '',
    'Detection rates are valid only when missingResults and failedControls are zero.', '',
    '| Category | Cases | Hurl passed | Reached origin |', '|---|---:|---:|---:|',
    ...categories.map(category => { const rows = results.filter(c => c.category === category);
      return `| ${category} | ${rows.length} | ${rows.filter(c => c.hurlPassed).length} | ${rows.filter(c => c.reachedOrigin).length} |`; }),
    '', '## Failed cases', '', '| Case | Expected | Actual | Origin received |', '|---|---:|---:|---|',
    ...results.filter(c => !c.hurlPassed).map(c => `| ${c.id} | ${c.attack ? 403 : 200} | ${c.status ?? 'no response'} | ${c.reachedOrigin} |`), ''];
  writeFileSync(resolve(work, 'REPORT.md'), report.join('\n'));
  console.log(JSON.stringify(summary, null, 2)); console.log(`Reports: ${work}`);
  process.exitCode = code || (summary.failedControls ? 1 : 0);
} finally {
  if (waf && waf.exitCode === null) { const stopped = once(waf, 'exit'); waf.kill(); await stopped; }
  origin.closeAllConnections(); await new Promise(resolve => origin.close(resolve));
}
