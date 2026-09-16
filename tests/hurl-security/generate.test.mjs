import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, mkdirSync, readdirSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { generate } from './generate.mjs';

test('Hurl matrix has unique cases, attack/benign assertions and before/after controls', () => {
  const output = resolve(dirname(fileURLToPath(import.meta.url)), '../../artifacts/hurl-generator-test');
  mkdirSync(output, { recursive: true });
  const manifest = generate(output);
  assert.ok(manifest.length >= 1400);
  assert.equal(new Set(manifest.map(c => c.id)).size, manifest.length);
  assert.ok(manifest.filter(c => !c.attack).length >= 400);
  assert.ok(manifest.some(c => c.source.includes('SafeLine')));
  for (const row of manifest) {
    const hurl = readFileSync(resolve(output, `${row.id}.hurl`), 'utf8');
    assert.ok(hurl.includes(`${row.id}-before`));
    assert.ok(hurl.includes(`${row.id}-after`));
    assert.equal((hurl.match(/^HTTP /gm) || []).length, 3);
    assert.equal((hurl.match(/^HTTP 403$/gm) || []).length, row.attack ? 1 : 0);
    assert.equal((hurl.match(/^HTTP 200$/gm) || []).length, row.attack ? 2 : 3);
    assert.ok(!/^(GET|POST|PUT|PATCH|DELETE|OPTIONS) https?:/m.test(hurl));
  }
});

test('Checked-in Hurl categories contain every case and can run without a generator', () => {
  const directory = resolve(dirname(fileURLToPath(import.meta.url)), 'cases');
  const files = readdirSync(directory).filter(name => name.endsWith('.hurl'));
  assert.equal(files.length, 21);
  for (const name of ['xss.hurl', 'sqli.hurl', 'code.hurl', 'ssrf.hurl', 'benign.hurl']) assert.ok(files.includes(name));
  const ids = [];
  let responses = 0;
  for (const file of files) {
    const content = readFileSync(resolve(directory, file), 'utf8');
    ids.push(...[...content.matchAll(/^# ([\w-]+) \| /gm)].map(match => match[1]));
    responses += (content.match(/^HTTP (200|403)$/gm) || []).length;
    assert.ok(!content.includes('local-fixture'));
    assert.ok(!content.includes('origin-ok'));
  }
  assert.equal(ids.length, 1430);
  assert.equal(new Set(ids).size, ids.length);
  assert.equal(responses, 4290);
});
