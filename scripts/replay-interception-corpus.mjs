#!/usr/bin/env node
// Replays the ordinary-download corpus through the extension's real takeover code.
//
//   node scripts/replay-interception-corpus.mjs [--extension=<dir>] [--json=<file>]
//
// What is real: the HTTP responses (tests/fixture-server), and the extension scripts, loaded unmodified.
// What is modeled: Chrome's download item (its byte counts follow the scenario) and the desktop app's answer,
// which makes one real request and applies the same identity rule as JobRunner.CheckBrowserResponse.
// This shows which branch the takeover code chooses for each response. It is not a browser measurement.
import { readFileSync, writeFileSync } from 'node:fs';
import http from 'node:http';
import vm from 'node:vm';
import { webcrypto } from 'node:crypto';
import { createFixtureServer } from '../tests/fixture-server/server.mjs';

const root = new URL('../', import.meta.url);
const option = name => process.argv.find(arg => arg.startsWith(`--${name}=`))?.slice(name.length + 3);
const extensionDir = option('extension') ? new URL(option('extension') + '/', `file://${process.cwd()}/`) : new URL('extension/', root);
const corpus = JSON.parse(readFileSync(new URL('tests/corpus/interception-corpus.json', root), 'utf8'));
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

// One request, headers only: the body is never read.
function headers(url, { method = 'GET', cookie } = {}) {
  return new Promise((resolve, reject) => {
    const request = http.request(url, { method, headers: { ...(cookie ? { Cookie: cookie } : {}), ...(method === 'POST' ? { 'Content-Length': 7 } : {}) } },
      response => {
        const list = [];
        for (let index = 0; index < response.rawHeaders.length; index += 2)
          list.push({ name: response.rawHeaders[index], value: response.rawHeaders[index + 1] });
        response.destroy();
        resolve({ status: response.statusCode, list, get: name => response.headers[name] });
      });
    request.on('error', reject);
    request.end(method === 'POST' ? 'order=1' : undefined);
  });
}

function storageArea() {
  const data = {};
  const pick = keys => keys == null ? data : typeof keys === 'string' ? { [keys]: data[keys] }
    : Array.isArray(keys) ? Object.fromEntries(keys.map(key => [key, data[key]]))
      : Object.fromEntries(Object.keys(keys).map(key => [key, key in data ? data[key] : keys[key]]));
  return { data, get: async keys => structuredClone(pick(keys)),
    set: async values => { Object.assign(data, structuredClone(values)); },
    remove: async keys => { for (const key of [].concat(keys)) delete data[key]; } };
}

// Everything the scripts touch but this replay does not model answers "nothing".
function permissive(path, hooks) {
  return new Proxy(function () {}, {
    get(_, key) {
      if (key === 'then' || typeof key === 'symbol') return undefined;
      const name = `${path}.${key}`;
      return name in hooks ? hooks[name] : permissive(name, hooks);
    },
    apply: () => Promise.resolve(undefined)
  });
}

function loadExtension(item, native) {
  const listeners = { created: [], changed: [], headers: [], redirect: [], alarm: [] };
  const on = list => ({ addListener: listener => list.push(listener) });
  const actions = [];
  const local = storageArea(), session = storageArea(), sync = storageArea();
  const chrome = permissive('chrome', {
    'chrome.runtime.id': 'vesper-replay',
    'chrome.storage.local': local, 'chrome.storage.session': session, 'chrome.storage.sync': sync,
    'chrome.downloads.onCreated': on(listeners.created), 'chrome.downloads.onChanged': on(listeners.changed),
    'chrome.webRequest.onHeadersReceived': on(listeners.headers), 'chrome.webRequest.onBeforeRedirect': on(listeners.redirect),
    'chrome.alarms.onAlarm': on(listeners.alarm),
    'chrome.downloads.search': async ({ id } = {}) => id === undefined || id === item.id ? [{ ...item }] : [],
    'chrome.downloads.pause': async () => { actions.push('pause'); item.paused = true; },
    'chrome.downloads.resume': async () => { actions.push('resume'); item.paused = false; },
    'chrome.downloads.cancel': async () => {
      actions.push('cancel');
      Object.assign(item, { state: 'interrupted', paused: false, canResume: false, error: 'USER_CANCELED' });
    },
    'chrome.downloads.erase': async () => { actions.push('erase'); }
  });
  const context = { chrome, URL, URLSearchParams, TextEncoder, TextDecoder, Uint8Array, crypto: webcrypto, structuredClone,
    navigator: { userAgent: 'Mozilla/5.0 (replay) Chrome/140.0.0.0' }, setTimeout, clearTimeout, setInterval, clearInterval, console };
  vm.createContext(context);
  context.self = context;
  context.importScripts = (...files) => {
    for (const file of files) vm.runInContext(readFileSync(new URL(file, extensionDir), 'utf8'), context, { filename: file });
  };
  vm.runInContext(readFileSync(new URL('background.js', extensionDir), 'utf8'), context, { filename: 'background.js' });
  context.__native = native;
  vm.runInContext('nativeRequest = message => Promise.resolve(globalThis.__native(message));', context);
  const each = (list, ...args) => Promise.all(list.map(listener => listener(...args)));
  return { actions, local: local.data, session: session.data,
    headers: details => each(listeners.headers, details), redirect: details => each(listeners.redirect, details),
    created: details => each(listeners.created, details) };
}

// The desktop app, modeled: one real request, judged by the rule in JobRunner.CheckBrowserResponse.
function nativeModel(item, scenario, length, counters) {
  const state = { staged: false };
  return async message => {
    if (message.type === 'hello' || message.type === 'capture-preflight') {
      if (message.type === 'capture-preflight' && scenario.helper === 'cold') {
        // A helper that has to wake up answers late, and Chrome keeps receiving while it does.
        await sleep(400);
        if (!item.paused && item.state === 'in_progress')
          item.bytesReceived = Math.max(item.bytesReceived, Math.floor(length * scenario.duringProof));
      }
      return { ok: true, data: { ready: true, siteRules: { neverCaptureHosts: [] }, proofs: ['etag', 'last-modified'] } };
    }
    if (message.type === 'browser-capture' || message.type === 'browser-submit') {
      counters.secondRequests++;
      const proof = message.browserProof || {};
      const answer = await headers(message.items[0].url, { cookie: message.items[0].cookie });
      // Chrome keeps receiving while VESPER proves the file, unless the extension parked it first.
      if (!item.paused && item.state === 'in_progress') item.bytesReceived = Math.floor(length * scenario.duringProof);
      const refusal = { 401: 'auth', 403: 'forbidden', 404: 'expired', 410: 'expired' }[answer.status]
        || (![200, 206].includes(answer.status) ? 'unavailable' : null);
      const etag = answer.get('etag');
      const sameValidator = proof.basis === 'last-modified'
        ? !etag && answer.get('last-modified') === proof.lastModified
          && Date.parse(answer.get('date')) - Date.parse(proof.lastModified) >= 1000
        : !!proof.etag && etag === proof.etag;
      const same = sameValidator && !answer.get('content-encoding')
        && (proof.length === undefined || Number(answer.get('content-length')) === proof.length)
        && (!proof.contentType || (answer.get('content-type') || '').split(';')[0] === proof.contentType);
      state.staged = !refusal && same;
      return { ok: true, results: [{ status: 'started', transferReady: state.staged,
        transferReason: state.staged ? undefined : refusal || 'browser-fallback' }] };
    }
    if (message.type === 'browser-transfer') {
      if (message.operation === 'abandon') { state.staged = false; return { ok: true, data: { state: 'failed' } }; }
      if (message.operation === 'accept') return state.staged ? { ok: true, data: { state: 'active' } } : { ok: false, code: 'unavailable' };
      return { ok: true, data: { state: state.staged ? 'complete' : 'failed' } };
    }
    return { ok: false, code: 'unsupported' };
  };
}

async function replay(base, testCase, scenario, sequence, shared) {
  const counters = shared?.counters || { secondRequests: 0 };
  const start = testCase.blob ? 'blob:' + base + '/' + webcrypto.randomUUID()
    : base + testCase.path + (testCase.uniqueToken ? `?token=${webcrypto.randomUUID()}` : '');
  const item = { id: 100 + sequence, url: start, finalUrl: start, state: 'in_progress', danger: 'safe', paused: false,
    incognito: testCase.incognito === true, startTime: new Date(Date.now() + sequence).toISOString(), bytesReceived: 0,
    totalBytes: 0, fileSize: 0, filename: 'C:\\Downloads\\fixture.bin', mime: 'application/octet-stream',
    referrer: base + '/', exists: true, canResume: true };
  let length = 0;
  const app = shared?.app?.(item) || null;
  const native = nativeModel(item, scenario, 0, counters);
  const extension = app || loadExtension(item, message => native.current(message));
  await sleep(5);

  if (!testCase.blob) {
    const method = testCase.method || 'GET';
    const requestId = `request-${sequence}`;
    let url = start, answer;
    for (let hop = 0; hop < 10; hop++) {
      answer = await headers(url, { method, cookie: testCase.cookie ? 'fixture_session=ok' : undefined });
      if (answer.status < 300 || answer.status >= 400 || !answer.get('location')) break;
      const next = new URL(answer.get('location'), url).href;
      await extension.redirect({ requestId, url, redirectUrl: next, method, type: 'main_frame', incognito: item.incognito });
      url = next;
    }
    length = Number(answer.get('content-length')) || 0;
    Object.assign(item, { finalUrl: url, totalBytes: length, fileSize: length });
    await extension.headers({ requestId, url, method, type: 'main_frame', statusCode: answer.status,
      responseHeaders: answer.list, tabId: 1, frameId: 0, incognito: item.incognito });
  }
  native.current = nativeModel(item, scenario, length, counters);
  await sleep(scenario.helper === 'warm' ? 30 : 5);
  item.bytesReceived = Math.floor(length * scenario.atCreation);
  await extension.created({ ...item });

  const decision = () => (extension.session.vesperCaptureDecisions || []).find(entry => entry.id === item.id);
  for (let wait = 0; wait < 150 && extension.local.browserDownloads?.[item.id]?.stage === 'parked'
    && decision()?.outcome !== 'moved'; wait++) await sleep(10);
  const settled = decision();
  return { case: testCase.id, scenario: scenario.id, owner: settled?.outcome === 'moved' ? 'vesper' : 'chrome',
    reason: settled?.outcome || (item.incognito ? 'private' : 'none'), discarded: settled?.discarded ?? 0,
    secondRequests: counters.secondRequests, chromePaused: item.paused, chromeState: item.state, actions: [...extension.actions] };
}
nativeModel = (inner => (...args) => { const model = inner(...args); model.current = model; return model; })(nativeModel);

const eligible = (testCase, scenario) => testCase.eligible === true
  && (Math.max(scenario.atCreation, scenario.duringProof) === 0
    || testCase.chromeResumable === true && Math.max(scenario.atCreation, scenario.duringProof) <= 0.5);

const fixture = createFixtureServer({ port: 0 });
const base = await fixture.listen();
const rows = [];
let sequence = 0;
for (const scenario of corpus.scenarios)
  for (const testCase of corpus.cases) {
    const row = await replay(base, testCase, scenario, ++sequence);
    row.eligible = eligible(testCase, scenario);
    row.asDefined = row.eligible === (row.owner === 'vesper');
    rows.push(row);
  }
await fixture.close();

const count = filter => rows.filter(filter).length;
const summary = {
  extension: extensionDir.pathname, total: rows.length, eligible: count(row => row.eligible),
  interceptedEligible: count(row => row.eligible && row.owner === 'vesper'),
  missedEligible: count(row => row.eligible && row.owner !== 'vesper'),
  interceptedIneligible: count(row => !row.eligible && row.owner === 'vesper'),
  chromeLeftPaused: count(row => row.owner !== 'vesper' && row.chromePaused),
  secondRequestsOnIneligible: rows.filter(row => !row.eligible).reduce((sum, row) => sum + row.secondRequests, 0)
};
if (option('json')) writeFileSync(option('json'), JSON.stringify({ summary, rows }, null, 2));

const pad = (text, width) => String(text).padEnd(width);
console.log(pad('case', 22) + corpus.scenarios.map(scenario => pad(scenario.id, 30)).join(''));
for (const testCase of corpus.cases)
  console.log(pad(testCase.id, 22) + corpus.scenarios.map(scenario => {
    const row = rows.find(entry => entry.case === testCase.id && entry.scenario === scenario.id);
    return pad(`${row.eligible ? 'E' : '-'} ${row.owner}:${row.reason}${row.asDefined ? '' : ' (!)'}`, 30);
  }).join(''));
console.log('\nE = eligible by the corpus definition, - = not eligible, (!) = outcome differs from the definition');
console.log(JSON.stringify(summary, null, 2));
if (summary.interceptedIneligible || summary.chromeLeftPaused) process.exitCode = 1;
