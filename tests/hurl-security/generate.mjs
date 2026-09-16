import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync, deflateSync, brotliCompressSync } from 'node:zlib';

const here = dirname(fileURLToPath(import.meta.url));
export function generate(directory, { grouped = false } = {}) {
  mkdirSync(directory, { recursive: true });
  const corpus = JSON.parse(readFileSync(resolve(here, '../fixtures/security-corpus-v1.json'), 'utf8'));
  const samples = corpus.samples.map(s => ({ id: s.id, category: s.category || 'benign',
    attack: s.category !== null, value: Buffer.from(s.valueBase64, 'base64').toString(), source: 'LyWaf corpus 1.1.0' }));
  const extra = [
    ['safeline-kong', 'sqli', '1 and 2=2', 'SafeLine sdk/kong/Readme.md'],
    ['safeline-gzip', 'sqli', "' or 1=1 --", 'SafeLine issue 1222'],
    ['sql-case', 'sqli', '1 UnIoN SeLeCt 1,2'],
    ['sql-comments', 'sqli', '1 UNION/**/SELECT 1,2'],
    ['sql-newline', 'sqli', '1 UNION\nSELECT 1,2'],
    ['sql-time', 'sqli', "1; WAITFOR DELAY '00:00:01'"],
    ['sql-error', 'sqli', "1 AND extractvalue(1,concat(0x7e,version()))"],
    ['xss-script', 'xss', '<script>alert(1)</script>'],
    ['xss-image', 'xss', '<img src=x onerror=alert(1)>'],
    ['xss-svg', 'xss', '<svg onload=alert(1)>'],
    ['xss-link', 'xss', 'javascript:alert(1)'],
    ['xss-event', 'xss', '<input autofocus onfocus=alert(1)>'],
    ['xss-iframe', 'xss', '<iframe src=javascript:alert(1)>'],
    ['cmd-pipe', 'command', 'value|whoami'],
    ['cmd-substitution', 'command', '$(id)'],
    ['cmd-backtick', 'command', '`id`'],
    ['cmd-newline', 'command', 'value\nwhoami'],
    ['traversal-unix', 'traversal', '../../etc/passwd'],
    ['traversal-windows', 'traversal', '..\\..\\windows\\win.ini'],
    ['ssrf-ipv6', 'ssrf', 'http://[::1]/admin'],
    ['ssrf-integer', 'ssrf', 'http://2130706433/admin'],
    ['ssrf-hex', 'ssrf', 'http://0x7f000001/admin'],
    ['ssrf-metadata', 'ssrf', 'http://169.254.169.254/latest/meta-data/'],
    ['xxe-file', 'xxe', '<!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><x>&e;</x>'],
    ['ssti-arithmetic', 'template', '{{7*7}}'],
    ['ssti-other', 'template', '{{8*8}}'],
    ['spel', 'template', '${T(java.lang.Runtime).getRuntime()}'],
    ['log4j', 'template', '${jndi:ldap://example.invalid/a}'],
    ['nosql-ne', 'nosql', '{"$ne":null}'],
    ['nosql-regex', 'nosql', '{"$regex":".*"}'],
    ['ldap-filter', 'ldap', '*)(|(uid=*))'],
    ['xpath-boolean', 'xpath', "' or '1'='1"],
    ['crlf', 'crlf', 'value\r\nX-Injected: yes'],
  ];
  for (const [id, category, value, source] of extra)
    samples.push({ id: `extra-${id}`, category, value, attack: true, source: source || 'Original synthetic probe' });
  for (const [id, value] of [['apostrophe', "O'Reilly"], ['unicode', '正常搜索内容'],
    ['math', '7 * 7 = 49'], ['url', 'https://example.com/products'], ['email', 'user@example.com'],
    ['json', '{"name":"Alice","count":2}'], ['xml', '<root><name>Alice</name></root>'],
    ['sql-text', 'Please select a delivery method'], ['percent', 'discount 20%'], ['punctuation', 'a+b & c=d']])
    samples.push({ id: `normal-${id}`, category: 'benign', value, attack: false, source: 'Original benign control' });
  const manifest = [];
  const groups = new Map();
  const names = {
    'SQL注入': 'sqli', '命令注入': 'command', '代码注入': 'code', 'SSRF': 'ssrf', 'XXE': 'xxe',
    '路径穿越': 'traversal', '本地文件包含': 'lfi', '远程文件包含': 'rfi',
    'Java反序列化': 'java-deserialization', 'PHP反序列化': 'php-deserialization',
    '.NET反序列化': 'dotnet-deserialization', 'NoSQL注入': 'nosql', 'LDAP注入': 'ldap',
    'XPath注入': 'xpath', '模板/表达式注入': 'template'
  };
  const encode = value => encodeURIComponent(value).replace(/'/g, '%27');
  const escapeXml = value => value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
  function add(sample, transport, request) {
    const id = `${sample.id}--${transport}`;
    const marker = `X-Hurl-Case: ${id}`;
    const accepted = 'HTTP 200\n[Asserts]\nheader "X-Hurl-Origin" == "local-fixture"\nbody == "origin-ok"\n';
    const control = suffix => `GET {{base_url}}/control\nX-Hurl-Case: ${id}-${suffix}\n${accepted}\n`;
    const expected = sample.attack ? 'HTTP 403\n[Asserts]\nheader "X-Hurl-Origin" not exists\n' : accepted;
    const lines = request.split('\n');
    lines.splice(1, 0, marker);
    let content = `# ${id} | ${sample.category} | ${sample.source}\n` +
      control('before') + lines.join('\n') + '\n' + expected + '\n' + control('after');
    if (grouped) {
      const name = names[sample.category] || sample.category;
      content = content.replaceAll('GET {{base_url}}/control', 'GET {{base_url}}/')
        .replace(/\[Asserts\]\nheader "X-Hurl-Origin" == "local-fixture"\nbody == "origin-ok"\n/g, '')
        .replace(/\[Asserts\]\nheader "X-Hurl-Origin" not exists\n/g, '');
      // Keep binary/multipart bytes exact; ordinary bodies remain readable, without template interpolation.
      if (name !== 'code' && !request.includes('Content-Encoding:') && !request.includes('multipart/form-data') && transport !== 'raw') {
        content = content.replace(/^base64,([^;]+);$/m, (_, data) => {
          const value = Buffer.from(data, 'base64').toString('utf8');
          return value.includes('```') || value.includes('{{') ? `base64,${data};` : '```\n' + value + '\n```';
        });
      }
      groups.set(name, (groups.get(name) || '') + content + '\n');
    } else writeFileSync(resolve(directory, `${id}.hurl`), content);
    manifest.push({ id, category: sample.category, attack: sample.attack, source: sample.source, transport });
  }
  for (const sample of samples) {
    const v = sample.value;
    const body = (method, type, data, encoding = '') => `${method} {{base_url}}/probe\nContent-Type: ${type}\n${encoding ? `Content-Encoding: ${encoding}\n` : ''}base64,${Buffer.from(data).toString('base64')};`;
    add(sample, 'query', `GET {{base_url}}/probe?q=${encode(v)}`);
    add(sample, 'query-double', `GET {{base_url}}/probe?q=${encode(encode(v))}`);
    add(sample, 'query-duplicate', `GET {{base_url}}/probe?q=normal&q=${encode(v)}`);
    add(sample, 'header', `GET {{base_url}}/probe\nX-Probe: ${encode(v)}`);
    add(sample, 'cookie', `GET {{base_url}}/probe\nCookie: probe=${encode(v)}`);
    add(sample, 'form', body('POST', 'application/x-www-form-urlencoded', `q=${encode(v)}`));
    const json = JSON.stringify({ data: { values: ['normal', v] } });
    add(sample, 'json', body('POST', 'application/json', json));
    add(sample, 'json-put', body('PUT', 'application/json', json));
    add(sample, 'json-patch', body('PATCH', 'application/json', json));
    add(sample, 'text', body('POST', 'text/plain', v));
    add(sample, 'xml', body('POST', 'application/xml', `<root><value>${escapeXml(v)}</value></root>`));
    add(sample, 'multipart', body('POST', 'multipart/form-data; boundary=HurlBoundary',
      `--HurlBoundary\r\nContent-Disposition: form-data; name="q"\r\n\r\n${v}\r\n--HurlBoundary--\r\n`));
    for (const [encoding, compress] of [['gzip', gzipSync], ['deflate', deflateSync], ['br', brotliCompressSync]])
      add(sample, encoding, body('POST', 'application/json', compress(Buffer.from(json)), encoding));
  }
  for (const path of ['/.git/config', '/.env', '/../../etc/passwd', '/%2e%2e/%2e%2e/etc/passwd', '/wp-config.php.bak'])
    add({ id: `path-${manifest.length}`, category: 'sensitive-path', attack: true, source: 'Original path probe' }, 'path', `GET {{base_url}}${path}`);
  const sql = "' or 1=1 --";
  const special = (id, request, attack = true, category = 'boundary') =>
    add({ id: `edge-${id}`, category, attack, source: 'Original transport/boundary probe' }, 'raw', request);
  for (const method of ['DELETE', 'OPTIONS', 'PUT', 'PATCH'])
    special(`method-${method}`, `${method} {{base_url}}/probe?q=${encode(sql)}`);
  special('get-body', `GET {{base_url}}/probe\nContent-Type: text/plain\nbase64,${Buffer.from(sql).toString('base64')};`);
  special('json-unicode', 'POST {{base_url}}/probe\nContent-Type: application/json\nbase64,' +
    Buffer.from('{"q":"' + [...sql].map(c => '\\u' + c.charCodeAt(0).toString(16).padStart(4, '0')).join('') + '"}').toString('base64') + ';');
  special('query-key', `GET {{base_url}}/probe?${encode(sql)}=value`);
  special('query-triple', `GET {{base_url}}/probe?q=${encode(encode(encode(sql)))}`);
  special('candidate-limit', `GET {{base_url}}/probe?${Array.from({length: 140}, (_, i) => `p${i}=v${i}`).join('&')}&last=${encode(sql)}`);
  special('late-body', `POST {{base_url}}/probe\nContent-Type: text/plain\nbase64,${Buffer.from('a'.repeat(9000) + sql).toString('base64')};`);
  special('invalid-gzip', 'POST {{base_url}}/probe\nContent-Type: application/json\nContent-Encoding: gzip\nbase64,bm90LWd6aXA=;', true, 'protocol');
  special('unknown-encoding', `POST {{base_url}}/probe\nContent-Type: text/plain\nContent-Encoding: unsupported\nbase64,${Buffer.from(sql).toString('base64')};`, true, 'protocol');
  special('json-top-array', `POST {{base_url}}/probe\nContent-Type: application/json\nbase64,${Buffer.from(JSON.stringify(['normal', sql])).toString('base64')};`);
  special('raw-xxe', `POST {{base_url}}/probe\nContent-Type: application/xml\nbase64,${Buffer.from(extra.find(e => e[0] === 'xxe-file')[2]).toString('base64')};`);
  special('benign-large', `POST {{base_url}}/probe\nContent-Type: text/plain\nbase64,${Buffer.from('normal '.repeat(2000)).toString('base64')};`, false, 'benign');
  if (grouped) {
    for (const [name, content] of groups)
      writeFileSync(resolve(directory, `${name}.hurl`), `# ${name} regression cases\n# Set base_url to an authorized test WAF (no trailing slash).\n# The upstream root must return 200 for before/after controls.\n# Use --continue-on-error --path-as-is --jobs 1.\n\n${content}`);
  } else writeFileSync(resolve(directory, 'manifest.json'), JSON.stringify(manifest, null, 2));
  return manifest;
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const grouped = process.argv.includes('--grouped');
  const output = resolve(process.argv.slice(2).find(arg => arg !== '--grouped') || resolve(here, grouped ? 'cases' : 'generated'));
  console.log(`Generated ${generate(output, { grouped }).length} Hurl cases in ${output}`);
}
