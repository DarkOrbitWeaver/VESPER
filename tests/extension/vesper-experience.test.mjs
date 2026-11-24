// Runs the real vesper-experience.js against an in-memory chrome.storage. Run: node --test tests/extension/vesper-experience.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { webcrypto } from 'node:crypto';

const DAY = 24 * 60 * 60_000;
const source = readFileSync(new URL('../../extension/vesper-experience.js', import.meta.url), 'utf8');

function load(saved = {}) {
  const data = structuredClone(saved);
  let writes = 0;
  const chrome = { storage: { local: {
    get: async key => structuredClone({ [key]: data[key] }),
    set: async values => { writes++; Object.assign(data, structuredClone(values)); },
    remove: async key => { delete data[key]; }
  } } };
  const context = { chrome, crypto: webcrypto, TextEncoder, Uint8Array, Date };
  vm.createContext(context);
  vm.runInContext(source + '\n;globalThis.experience = VesperExperience;', context);
  return { experience: context.experience, data, writes: () => writes };
}

test('with nothing learned every strategy is worth trying and nothing is written', async () => {
  const { experience, data, writes } = load();
  assert.equal(await experience.promising('files.example', 'takeover'), true);
  assert.deepEqual({ ...(await experience.standing('files.example', 'takeover')) }, { verified: 0, refused: 0 });
  assert.equal(writes(), 0);
  assert.equal(data.vesperExperience, undefined);
});

test('one refusal is an accident; two with no verified file since are a pattern', async () => {
  const { experience } = load();
  await experience.note('files.example', 'takeover', 'refused');
  assert.equal(await experience.promising('files.example', 'takeover'), true);
  await experience.note('files.example', 'takeover', 'refused');
  assert.equal(await experience.promising('files.example', 'takeover'), false);
  assert.equal(await experience.promising('other.example', 'takeover'), true, 'another site is unaffected');
  assert.equal(await experience.promising('files.example', 'takeover-signed'), true, 'signing in is a different strategy');
});

test('a stand-down fades: the site is asked again after about three days', async () => {
  const { experience } = load();
  const start = Date.now();
  await experience.note('files.example', 'takeover', 'refused', start);
  await experience.note('files.example', 'takeover', 'refused', start);
  assert.equal(await experience.promising('files.example', 'takeover', start + 2 * DAY), false);
  assert.equal(await experience.promising('files.example', 'takeover', start + 4 * DAY), true);
});

test('a verified file clears the doubt earlier refusals raised', async () => {
  const { experience } = load();
  await experience.note('files.example', 'takeover', 'refused');
  await experience.note('files.example', 'takeover', 'refused');
  await experience.note('files.example', 'takeover', 'verified');
  assert.equal(await experience.promising('files.example', 'takeover'), true);
  assert.equal((await experience.standing('files.example', 'takeover')).refused, 0);
});

test('a site that mostly works keeps being tried until refusals outnumber verified files', async () => {
  const { experience } = load();
  for (let count = 0; count < 3; count++) await experience.note('mixed.example', 'takeover', 'verified');
  for (let count = 0; count < 2; count++) await experience.note('mixed.example', 'takeover', 'refused');
  assert.equal(await experience.promising('mixed.example', 'takeover'), true, 'three verified files outweigh two refusals');
  for (let count = 0; count < 2; count++) await experience.note('mixed.example', 'takeover', 'refused');
  assert.equal(await experience.promising('mixed.example', 'takeover'), false, 'four refusals outweigh three verified files');
});

test('only definite outcomes are learned', async () => {
  const { experience, writes } = load();
  for (const outcome of ['uncertain', 'timeout', 'queued', '', undefined])
    await experience.note('files.example', 'takeover', outcome);
  await experience.note('', 'takeover', 'refused');
  await experience.note('files.example', '', 'refused');
  assert.equal(writes(), 0);
});

test('the stored memory holds no site, strategy or address in readable form', async () => {
  const { experience, data } = load();
  await experience.note('private-files.example', 'takeover', 'refused');
  const stored = JSON.stringify(data.vesperExperience);
  assert.doesNotMatch(stored, /private-files|example|takeover/);
  const keys = Object.keys(data.vesperExperience.entries);
  assert.equal(keys.length, 1);
  assert.match(keys[0], /^[0-9a-f]{24}$/);
});

test('the memory is capped and forgets the oldest sites first', async () => {
  const { experience, data } = load();
  const start = Date.now();
  for (let site = 0; site < 300; site++) await experience.note(`site-${site}.example`, 'takeover', 'refused', start + site);
  assert.equal(Object.keys(data.vesperExperience.entries).length, 256);
  assert.equal((await experience.standing('site-0.example', 'takeover', start + 300)).refused, 0);
  assert.ok((await experience.standing('site-299.example', 'takeover', start + 300)).refused > 0.99);
});

test('repeated refusals cannot keep VESPER away for more than about seventeen days', async () => {
  const { experience } = load();
  const start = Date.now();
  for (let count = 0; count < 40; count++) await experience.note('files.example', 'takeover', 'refused', start);
  assert.ok((await experience.standing('files.example', 'takeover', start)).refused <= 8);
  assert.equal(await experience.promising('files.example', 'takeover', start + 16 * DAY), false);
  assert.equal(await experience.promising('files.example', 'takeover', start + 18 * DAY), true);
});

test('a damaged or foreign record is discarded rather than trusted', async () => {
  for (const saved of [{ vesperExperience: 'text' }, { vesperExperience: { salt: 7, entries: {} } },
    { vesperExperience: { salt: 'long-enough-salt', entries: { bad: [1, 2, 3], '0123456789abcdef01234567': ['x', 1, 2] } } }]) {
    const { experience } = load(saved);
    assert.equal(await experience.promising('files.example', 'takeover'), true);
  }
});

test('clearing forgets everything', async () => {
  const { experience, data } = load();
  await experience.note('files.example', 'takeover', 'refused');
  await experience.note('files.example', 'takeover', 'refused');
  await experience.clear();
  assert.equal(data.vesperExperience, undefined);
  assert.equal(await experience.promising('files.example', 'takeover'), true);
});
