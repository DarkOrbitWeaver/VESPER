// Runs the real background.js with a mocked Chrome API. Run: node --test tests/extension/background.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

function load(nativeReplies = {}, { download, downloadHistory = [], signInPermission = false, prefs = {}, activity = false, sessionData = {} } = {}) {
  const listeners = [];
  const noop = () => {};
  const event = { addListener: (fn) => listeners.push(fn) };
  const storage = (data = {}) => {
    return {
      get: async key => structuredClone({ [key]: data[key] }),
      set: async values => Object.assign(data, structuredClone(values)),
      remove: async key => { delete data[key]; }
    };
  };
  let onMessage, onCreated, onChanged, onHeaders, onRedirect, onMedia, onAlarm, onContextClicked;
  const sent = [];
  const actions = [];
  const cookieLookups = [];
  const badges = [];
  const alarms = new Map();
  const ports = [];
  const local = storage();
  if (download && !downloadHistory.some(item => item.id === download.id)) downloadHistory.push(download);
  const chrome = {
    runtime: {
      id: 'vesper-test',
      onMessage: { addListener: fn => { onMessage = fn; } }, onInstalled: event, onStartup: event, lastError: undefined,
      sendNativeMessage: (host, message, cb) => {
        actions.push('native');
        sent.push(message);
        const reply = nativeReplies[message.type];
        const response = typeof reply === 'function' ? reply(message) : reply;
        const fallback = { ok: false, code: 'host-error', message: 'no reply' };
        if (response && typeof response.then === 'function') response.then(value => cb(value ?? fallback), () => cb(fallback));
        else cb(response ?? fallback);
      }
    },
    webRequest: {
      onResponseStarted: { addListener: fn => { onMedia = fn; } },
      onHeadersReceived: { addListener: fn => { onHeaders = fn; } },
      onBeforeRedirect: { addListener: fn => { onRedirect = fn; } }
    },
    tabs: { onUpdated: event, onRemoved: event, query: async () => [] },
    contextMenus: { create: noop, removeAll: async () => {}, onClicked: { addListener: fn => { onContextClicked = fn; } } },
    alarms: {
      get: async name => alarms.get(name),
      create: async (name, details) => alarms.set(name, details),
      onAlarm: { addListener: fn => { onAlarm = fn; } }
    },
    downloads: {
      onCreated: { addListener: fn => { onCreated = fn; } },
      onChanged: { addListener: fn => { onChanged = fn; } },
      search: async ({ id, state, startedAfter, startedBefore, limit }) => downloadHistory
        .filter(item => (id === undefined || item.id === id) && (state === undefined || item.state === state)
          && (startedAfter === undefined || Date.parse(item.startTime) >= Date.parse(startedAfter))
          && (startedBefore === undefined || Date.parse(item.startTime) <= Date.parse(startedBefore)))
        .slice(0, limit || downloadHistory.length).map(item => ({ ...item })),
      download: async ({ url }) => {
        actions.push('chrome-start');
        const id = Math.max(0, ...downloadHistory.map(item => item.id)) + 1;
        downloadHistory.push({ id, url, startTime: new Date().toISOString(), state: 'in_progress', byExtensionId: chrome.runtime.id });
        return id;
      },
      pause: async () => { actions.push('pause'); download.paused = true; },
      resume: async () => { actions.push('resume'); download.paused = false; },
      cancel: async () => { actions.push('cancel'); download.state = 'interrupted'; download.paused = false; },
      erase: async () => actions.push('erase')
    },
    action: { setBadgeText: async value => badges.push(value), setBadgeBackgroundColor: async () => {} },
    storage: { sync: { get: async (d) => ({ ...d, ...prefs }), set: async () => {} }, local, session: storage(sessionData) },
    permissions: { contains: async () => signInPermission },
    cookies: { getAll: async ({ url }) => { cookieLookups.push(url); return [{ name: 'session', value: 'test-value' }]; } }
  };
  if (activity) chrome.runtime.connectNative = () => {
    const port = {
      posted: [],
      onMessage: { addListener: fn => { port.reply = fn; } },
      onDisconnect: { addListener: fn => { port.close = fn; } },
      postMessage: message => port.posted.push(message),
      disconnect: () => { port.disconnected = true; port.close(); }
    };
    ports.push(port);
    return port;
  };
  let sequence = 0;
  let now = Date.now();
  const TestDate = class extends Date { static now() { return now; } };
  const context = { chrome, URL, Date: TestDate, crypto: { randomUUID: () => `test-uuid-${++sequence}-aaaa` }, navigator: { userAgent: 'test' }, setTimeout, clearTimeout, console };
  vm.createContext(context);
  context.importScripts = (...files) => {
    for (const file of files) vm.runInContext(readFileSync(new URL(`../../extension/${file}`, import.meta.url), 'utf8'), context);
  };
  vm.runInContext(readFileSync(new URL('../../extension/background.js', import.meta.url), 'utf8') + '\n;globalThis.__describe = describe;', context);
  return { describe: context.__describe, sent, actions, cookieLookups, badges, local, sessionData, downloadHistory, onCreated, onChanged, onHeaders, onRedirect, onMedia, onContextClicked, ports,
    advanceTime: milliseconds => { now += milliseconds; },
    recover: () => { alarms.delete('vesper-chrome-downloads'); return onAlarm({ name: 'vesper-chrome-downloads' }); },
    message: (msg, sender = {}) => new Promise(resolve => onMessage(msg, sender, resolve)) };
}

const hlsMaster = {
  ok: true, data: { kind: 'hls-master', variants: [
    { url: 'u1080', bandwidth: 5_000_000, height: 1080, audioOnly: false },
    { url: 'u1080b', bandwidth: 3_000_000, height: 1080, audioOnly: false },   // same height: must be de-duplicated
    { url: 'u720', bandwidth: 2_800_000, height: 720, audioOnly: false },
    { url: 'u480', bandwidth: 1_200_000, height: 480, audioOnly: false },
    { url: 'u360', bandwidth: 600_000, height: 360, audioOnly: false },
    { url: 'uaudio', bandwidth: 128_000, height: 0, audioOnly: true }
  ] }
};

test('offers Best / Balanced / Smaller / Audio only, de-duplicates qualities and never invents sizes', async () => {
  const { describe } = load({ probe: hlsMaster });
  const r = await describe('https://x/master.m3u8', null);        // duration unknown -> no size estimate
  assert.equal(r.ok, true);
  assert.deepEqual([...r.choices.map((c) => c.url)], ['u1080', 'u720', 'u360', 'uaudio']);   // [...] leaves the VM realm
  assert.ok(r.choices[0].recommended);
  assert.ok(r.choices.every((c) => !c.detail.includes('~')), 'no estimate without a known duration');
  assert.deepEqual([...r.more.map((c) => c.url)], ['u480']);            // the rest hide behind "Show more qualities"
  assert.ok(!r.more.concat(r.choices).some((c) => c.url === 'u1080b'), 'duplicate 1080p removed');
});

test('size estimates are marked approximate and only appear with a real duration', async () => {
  const { describe } = load({ probe: hlsMaster });
  const r = await describe('https://x/master.m3u8', 600);
  assert.match(r.choices[0].detail, /~/);
});

test('audio-only is not offered when the stream has none', async () => {
  const noAudio = { ok: true, data: { kind: 'hls-master', variants: hlsMaster.data.variants.filter((v) => !v.audioOnly) } };
  const r = await load({ probe: noAudio }).describe('https://x/m.m3u8', null);
  assert.ok(!r.choices.some((c) => c.audio));
});

test('a direct file gets exactly one honest choice with its real type and size', async () => {
  const direct = { ok: true, data: { kind: 'direct', contentType: 'video/mp4', contentLength: 25 * 1048576 } };
  const r = await load({ probe: direct }).describe('https://x/a.mp4', null);
  assert.equal(r.choices.length, 1);
  assert.equal(r.choices[0].detail, 'MP4 · 25.0 MB');
  assert.equal(r.choices[0].kind, 'http');
});

test('unsupported sources and a missing connection are reported, not hidden', async () => {
  const dash = await load({ probe: { ok: true, data: { kind: 'unsupported', message: 'DASH streams are not downloadable yet.' } } }).describe('https://x/a.mpd', null);
  assert.equal(dash.ok, false);
  assert.match(dash.message, /DASH/);
  const missing = await load({ probe: { ok: false, code: 'host-missing', message: 'Specified native messaging host not found.' } }).describe('https://x/a.mp4', null);
  assert.equal(missing.code, 'host-missing');
});

test('requests carry the protocol version and a valid id', async () => {
  const { describe, sent } = load({ probe: { ok: true, data: { kind: 'hls-media' } } });
  await describe('https://x/a.m3u8', null);
  assert.equal(sent[0].v, 1);
  assert.match(sent[0].id, /^[A-Za-z0-9_-]{8,64}$/);
  assert.equal(sent[0].type, 'probe');
});

const activeDownload = () => ({
  id: 14, state: 'in_progress', danger: 'safe', paused: false, url: 'https://example.com/files/archive.zip',
  filename: 'C:\\Downloads\\archive.zip', startTime: '2026-11-12T01:00:00.000Z', fileSize: 1000, exists: true
});
const started = { ok: true, results: [{ status: 'started', transferReady: true }] };
const transfer = state => ({ ok: true, data: { state } });

function observe(app, file, { method = 'GET', type = 'main_frame', contentType = 'application/zip', attachment = true, requestId = 'download-request' } = {}) {
  const headers = { 'Content-Length': String(file.fileSize), ETag: '"version-1"' };
  if (contentType) headers['Content-Type'] = contentType;
  if (attachment) headers['Content-Disposition'] = 'attachment; filename="archive.zip"';
  return app.onHeaders({ url: file.finalUrl || file.url, requestId, method, type, statusCode: 200,
    responseHeaders: Object.entries(headers).map(([name, value]) => ({ name, value })) });
}

async function waitFor(condition, message) {
  for (let attempt = 0; attempt < 40; attempt++) {
    if (condition()) return;
    await new Promise(resolve => setImmediate(resolve));
  }
  assert.ok(condition(), message);
}

test('automatic handoff verifies the response before pausing Chrome and accepts before monitoring', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  assert.deepEqual(app.actions, ['native', 'pause', 'native', 'native']);
  assert.equal(app.sent[0].type, 'browser-submit');
  assert.deepEqual(structuredClone(app.sent[0].browserProof), { length: 1000, etag: '"version-1"', contentType: 'application/zip' });
  assert.equal(app.sent[0].items[0].fileName, 'archive.zip');
  assert.equal(app.sent[1].operation, 'accept');
  assert.equal(app.sent[2].operation, 'watch');
  assert.equal(file.paused, true);
});

test('automatic queued results without transfer readiness leave Chrome running', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': { ok: true, results: [{ status: 'queued' }] },
    'browser-transfer': transfer('failed') }, { download: file, prefs: { handoff: 'queue' } });
  await observe(app, file);
  await app.onCreated(file);

  assert.equal(app.actions.includes('pause'), false);
  assert.equal(file.paused, false);
  assert.equal(app.sent[0].mode, 'queue');
  assert.equal(app.sent[1].operation, 'abandon');
});

test('automatic handoff includes extensionless files and starts them by default', async () => {
  const file = { ...activeDownload(), url: 'https://example.com/export?receipt=42', filename: '' };
  const app = load({}, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(app.sent[0].items[0].url, file.url);
  assert.equal(app.sent[0].mode, 'download');
  assert.equal(file.paused, false);
  assert.deepEqual(app.actions, ['native']);
});

test('automatic handoff waits for Chrome to clear a download', async () => {
  const file = { ...activeDownload(), danger: 'asyncScanning' };
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(app.sent.length, 0);
  file.danger = 'safe';
  await app.onChanged({ id: file.id, danger: { previous: 'asyncScanning', current: 'safe' } });
  assert.deepEqual(app.actions, ['native', 'pause', 'native', 'native']);
});

test('browser-started file downloads without Content-Disposition can use the same handoff', async () => {
  const file = { ...activeDownload(), url: 'https://example.com/image.png', filename: 'C:\\Downloads\\image.png' };
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file });
  await observe(app, file, { contentType: 'image/png', attachment: false });
  await app.onCreated(file);
  assert.equal(app.sent[0].browserProof.contentType, 'image/png');
  assert.equal(file.paused, true);
});

test('a download is picked up when Chrome reports its response after the download started', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file });
  await app.onCreated(file);
  assert.equal(app.sent.length, 0);
  await observe(app, file, { contentType: null, attachment: false });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(app.sent[0].browserProof.length, file.fileSize);
  assert.equal(app.sent[0].browserProof.contentType, undefined);
  assert.equal(file.paused, true);
});

test('a redirected API attachment matches when Chrome creates the item with finalUrl already set', async () => {
  const originalUrl = 'https://arena.example/api/chat/chat-1/workspace/workspace-1/download';
  const finalUrl = 'https://arena.example/api/files/file-1/download';
  const file = { ...activeDownload(), url: originalUrl, finalUrl, fileSize: -1, totalBytes: -1 };
  let finishSubmit;
  const pendingSubmit = new Promise(resolve => { finishSubmit = resolve; });
  const app = load({ 'browser-submit': () => pendingSubmit, 'browser-transfer': transfer('failed') },
    { download: file, signInPermission: true, prefs: { useSignIn: true } });

  app.onRedirect({ requestId: 'arena-request', url: originalUrl, redirectUrl: finalUrl, method: 'GET' });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(app.sessionData.vesperDownloadCapture, undefined,
    'an unassociated redirect should stay in memory');
  await app.onCreated(file);
  await waitFor(() => app.sessionData.vesperDownloadCapture?.chains?.length === 1,
    'the matched download should make the redirect chain eligible for recovery');
  assert.equal(app.sessionData.vesperDownloadCapture.awaiting.length, 1);

  app.onHeaders({ url: finalUrl, requestId: 'arena-request', method: 'GET', type: 'other', statusCode: 200,
    responseHeaders: [
      { name: 'Content-Type', value: 'application/zip' },
      { name: 'Content-Disposition', value: 'attachment; filename="export.zip"' }
    ] });

  await waitFor(() => app.sessionData.vesperDownloadCapture.awaiting.length === 0,
    'the final response should match the pending original URL');
  assert.equal(app.sent.some(message => message.type === 'browser-submit'), false,
    'an unknown length and missing ETag are not enough to prove the file');
  assert.equal(file.paused, false);

  file.totalBytes = 1000;
  const changed = app.onChanged({ id: file.id, totalBytes: { current: 1000 } });
  await waitFor(() => app.sent.some(message => message.type === 'browser-submit'),
    'Chrome size evidence should let VESPER attempt the handoff');
  const request = app.sent.find(message => message.type === 'browser-submit');
  assert.equal(request.items[0].cookie, 'session=test-value');
  assert.equal(request.authorized, true);
  assert.deepEqual(app.cookieLookups, [finalUrl]);
  assert.equal(file.paused, false, 'Chrome must continue until native readiness is confirmed');

  finishSubmit({ ok: true, results: [{ status: 'started', transferReady: false }] });
  await changed;
  assert.equal(file.paused, false);
  assert.equal(app.actions.includes('cancel'), false);
  assert.ok(!JSON.stringify(await app.local.get('browserDownloads')).includes('test-value'));
  assert.ok(!JSON.stringify(app.sessionData).includes('test-value'));
});

test('a redirected response waits for Chrome finalUrl before retrying the native handoff', async () => {
  const originalUrl = 'https://arena.example/api/chat/chat-4/workspace/workspace-4/download';
  const finalUrl = 'https://arena.example/api/files/file-4/download';
  const file = { ...activeDownload(), url: originalUrl, finalUrl: undefined, fileSize: -1, totalBytes: -1 };
  let finishSubmit;
  const pendingSubmit = new Promise(resolve => { finishSubmit = resolve; });
  const app = load({ 'browser-submit': () => pendingSubmit, 'browser-transfer': transfer('active') },
    { download: file, signInPermission: true, prefs: { useSignIn: true } });
  await app.onCreated(file);
  app.onRedirect({ requestId: 'arena-late-final', url: originalUrl, redirectUrl: finalUrl, method: 'GET' });
  await waitFor(() => app.sessionData.vesperDownloadCapture.chains?.length === 1);

  app.onHeaders({ url: finalUrl, requestId: 'arena-late-final', method: 'GET', type: 'other', statusCode: 200,
    responseHeaders: [
      { name: 'Content-Type', value: 'application/zip' },
      { name: 'Content-Disposition', value: 'attachment; filename="export.zip"' }
    ] });
  await waitFor(() => app.sessionData.vesperDownloadCapture.responses?.length === 1);
  assert.equal(app.sessionData.vesperDownloadCapture.awaiting.length, 1,
    'keep the pending item while Chrome still reports the original URL');
  assert.equal(app.sent.some(message => message.type === 'browser-submit'), false);

  file.finalUrl = finalUrl;
  await app.onChanged({ id: file.id, finalUrl: { current: finalUrl } });
  assert.equal(app.sent.some(message => message.type === 'browser-submit'), false,
    'the final URL alone is not proof of a complete transferable file');
  assert.equal(file.paused, false);

  file.totalBytes = 1000;
  const changed = app.onChanged({ id: file.id, totalBytes: { current: 1000 } });
  await waitFor(() => app.sent.some(message => message.type === 'browser-submit'));
  const request = app.sent.find(message => message.type === 'browser-submit');
  assert.equal(request.items[0].cookie, 'session=test-value');
  assert.equal(request.authorized, true);
  assert.equal(file.paused, false, 'Chrome must continue while VESPER checks readiness');

  finishSubmit(started);
  await changed;
  assert.equal(file.paused, true);
  assert.ok(app.sent.some(message => message.type === 'browser-transfer' && message.operation === 'accept'));
  assert.equal(app.actions.includes('cancel'), false);
});

test('an authenticated API handoff wakes VESPER before waiting for native readiness', async () => {
  const url = 'https://arena.example/api/chat/chat-2/workspace/workspace-2/download';
  const file = { ...activeDownload(), url, finalUrl: url };
  let finishSubmit;
  const pendingSubmit = new Promise(resolve => { finishSubmit = resolve; });
  const app = load({ 'browser-submit': () => pendingSubmit, 'browser-transfer': transfer('active') },
    { download: file, activity: true, signInPermission: true, prefs: { useSignIn: true } });
  await observe(app, file, { requestId: 'arena-direct' });

  const created = app.onCreated(file);
  await waitFor(() => app.ports.length === 1 && app.sent.some(message => message.type === 'browser-submit'),
    'the activity port and native request should start independently');
  assert.equal(app.ports[0].posted[0].operation, 'connect');
  assert.equal(file.paused, false);
  assert.equal(app.actions.includes('cancel'), false);
  const request = app.sent.find(message => message.type === 'browser-submit');
  assert.equal(request.authorized, true);
  assert.equal(request.items[0].cookie, 'session=test-value');
  assert.deepEqual(app.cookieLookups, [url]);

  finishSubmit(started);
  await created;
  assert.equal(file.paused, true);
  assert.ok(app.sent.some(message => message.type === 'browser-transfer' && message.operation === 'accept'));
  assert.equal(app.actions.includes('cancel'), false);
});

test('a POST redirect hop or 401/407 challenge prevents replaying an API attachment', async () => {
  const cases = [
    { name: 'POST redirect', method: 'POST', redirect: true },
    { name: '401 retry', method: 'GET', statusCode: 401, challenge: true },
    { name: '407 proxy retry', method: 'GET', statusCode: 407, challenge: true }
  ];
  for (const [index, scenario] of cases.entries()) {
    const originalUrl = `https://arena.example/api/chat/chat-${index}/workspace/workspace-${index}/download`;
    const finalUrl = scenario.redirect ? `https://arena.example/api/files/file-${index}/download` : originalUrl;
    const requestId = `arena-nonreplay-${index}`;
    const file = { ...activeDownload(), url: originalUrl, finalUrl: undefined, fileSize: -1, totalBytes: -1 };
    const app = load({}, { download: file, signInPermission: true, prefs: { useSignIn: true } });
    await app.onCreated(file);

    if (scenario.redirect) {
      app.onRedirect({ requestId, url: originalUrl, redirectUrl: finalUrl, method: scenario.method });
      file.finalUrl = finalUrl;
    } else {
      app.onHeaders({ url: originalUrl, requestId, method: 'GET', type: 'other', statusCode: scenario.statusCode, responseHeaders: [] });
    }
    file.totalBytes = 1000;
    app.onHeaders({ url: finalUrl, requestId, method: 'GET', type: 'other', statusCode: 200,
      responseHeaders: [
        { name: 'Content-Type', value: 'application/zip' },
        { name: 'Content-Disposition', value: 'attachment; filename="export.zip"' },
        { name: 'Content-Length', value: '1000' }
      ] });
    await waitFor(() => app.sessionData.vesperDownloadCapture.awaiting.length === 0,
      'the response should settle the matching pending Chrome download');
    await app.onChanged({ id: file.id, totalBytes: { current: 1000 } });

    assert.equal(app.sent.some(message => message.type === 'browser-submit'), false, scenario.name);
    assert.equal(app.cookieLookups.length, 0, scenario.name);
    assert.equal(file.paused, false, scenario.name);
  }
});

test('automatic API interception off leaves Chrome as the only download owner', async () => {
  const file = { ...activeDownload(), url: 'https://arena.example/api/chat/chat-3/workspace/workspace-3/download' };
  const app = load({ 'browser-submit': started }, { download: file, activity: true, prefs: { intercept: false, useSignIn: true }, signInPermission: true });
  await observe(app, file, { requestId: 'arena-disabled' });
  await app.onCreated(file);

  assert.equal(app.sent.some(message => message.type === 'browser-submit'), false);
  assert.equal(app.cookieLookups.length, 0);
  assert.equal(file.paused, false);
  assert.equal(app.actions.includes('cancel'), false);
  assert.equal(app.ports.length, 0);
});

test('a pending Chrome capture survives a service worker restart', async () => {
  const file = activeDownload();
  const sessionData = {};
  const firstWorker = load({}, { download: file, sessionData });
  await firstWorker.onCreated(file);
  assert.equal(sessionData.vesperDownloadCapture.awaiting.length, 1);

  const workerAfterRestart = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file, sessionData });
  await workerAfterRestart.message({ type: 'tab-state', tabId: 2 });
  await observe(workerAfterRestart, file);
  await new Promise(resolve => setTimeout(resolve, 20));

  assert.equal(workerAfterRestart.sent[0].type, 'browser-submit');
  assert.equal(file.paused, true);
});

test('a redirect chain survives service worker restart until the final attachment response', async () => {
  const originalUrl = 'https://arena.example/api/chat/chat-5/workspace/workspace-5/download';
  const finalUrl = 'https://arena.example/api/files/file-5/download';
  const requestId = 'arena-worker-restart';
  const file = { ...activeDownload(), url: originalUrl, finalUrl: undefined, fileSize: -1, totalBytes: -1 };
  const sessionData = {};
  const downloadHistory = [];
  const firstWorker = load({}, { download: file, sessionData, downloadHistory });
  await firstWorker.onCreated(file);
  firstWorker.onRedirect({ requestId, url: originalUrl, redirectUrl: finalUrl, method: 'GET' });
  await waitFor(() => sessionData.vesperDownloadCapture.chains?.length === 1);

  const nextWorker = load({ 'browser-submit': started, 'browser-transfer': transfer('active') },
    { download: file, sessionData, downloadHistory, signInPermission: true, prefs: { useSignIn: true } });
  file.finalUrl = finalUrl;
  file.totalBytes = 1000;
  nextWorker.onHeaders({ url: finalUrl, requestId, method: 'GET', type: 'other', statusCode: 200,
    responseHeaders: [
      { name: 'Content-Type', value: 'application/zip' },
      { name: 'Content-Disposition', value: 'attachment; filename="export.zip"' }
    ] });
  await waitFor(() => nextWorker.sent.some(message => message.type === 'browser-submit'));

  const request = nextWorker.sent.find(message => message.type === 'browser-submit');
  assert.equal(request.items[0].cookie, 'session=test-value');
  assert.equal(request.authorized, true);
  assert.equal(file.paused, true);
  assert.equal(file.state, 'in_progress');
  assert.equal(nextWorker.actions.includes('cancel'), false);
});

test('unrelated page redirects stay in memory instead of session storage', async () => {
  const sessionData = {};
  const app = load({}, { sessionData });
  const originalUrl = 'https://private.example/chat/42?share=private-value';
  const finalUrl = 'https://private.example/home';
  app.onRedirect({ requestId: 'ordinary-navigation', url: originalUrl, redirectUrl: finalUrl, method: 'GET' });
  await new Promise(resolve => setImmediate(resolve));

  assert.equal(sessionData.vesperDownloadCapture, undefined);
  assert.ok(!JSON.stringify(sessionData).includes(originalUrl));
  assert.ok(!JSON.stringify(sessionData).includes(finalUrl));
});

test('POST, 401, and 407 replay markers survive restart without persisting their URLs', async () => {
  const cases = [
    { kind: 'post', statusCode: 0 },
    { kind: 'challenge', statusCode: 401 },
    { kind: 'challenge', statusCode: 407 }
  ];
  for (const [index, scenario] of cases.entries()) {
    const originalUrl = `https://private.example/api/download/${index}`;
    const finalUrl = scenario.kind === 'post' ? `https://cdn.example/file/${index}` : originalUrl;
    const requestId = `opaque-request-${index}`;
    const sessionData = {};
    const firstWorker = load({}, { sessionData });
    if (scenario.kind === 'post') {
      firstWorker.onRedirect({ requestId, url: originalUrl, redirectUrl: finalUrl, method: 'POST' });
    } else {
      firstWorker.onHeaders({ url: originalUrl, requestId, method: 'GET', type: 'other',
        statusCode: scenario.statusCode, responseHeaders: [] });
    }
    await waitFor(() => sessionData.vesperDownloadCapture?.blocked?.length === 1);
    assert.equal(sessionData.vesperDownloadCapture.chains?.length || 0, 0);
    assert.ok(!JSON.stringify(sessionData).includes(originalUrl));
    assert.ok(!JSON.stringify(sessionData).includes(finalUrl));

    const file = { ...activeDownload(), url: originalUrl, finalUrl, fileSize: -1, totalBytes: -1 };
    const nextWorker = load({ 'browser-submit': started, 'browser-transfer': transfer('active') },
      { download: file, sessionData });
    await nextWorker.onCreated(file);
    file.totalBytes = 1000;
    nextWorker.onHeaders({ url: finalUrl, requestId, method: 'GET', type: 'other', statusCode: 200,
      responseHeaders: [
        { name: 'Content-Type', value: 'application/zip' },
        { name: 'Content-Disposition', value: 'attachment; filename="export.zip"' },
        { name: 'Content-Length', value: '1000' }
      ] });
    await waitFor(() => nextWorker.sessionData.vesperDownloadCapture.responses?.length === 1);
    await nextWorker.onChanged({ id: file.id, totalBytes: { current: 1000 } });

    assert.equal(nextWorker.sent.some(message => message.type === 'browser-submit'), false, scenario.kind);
    assert.equal(file.paused, false, scenario.kind);
  }
});

test('recent response evidence survives a service worker restart before download capture', async () => {
  const file = activeDownload();
  const sessionData = {};
  const firstWorker = load({}, { download: file, sessionData });
  await observe(firstWorker, file);
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(sessionData.vesperDownloadCapture.responses.length, 1);

  const workerAfterRestart = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file, sessionData });
  await workerAfterRestart.onCreated(file);

  assert.equal(workerAfterRestart.sent[0].type, 'browser-submit');
  assert.equal(file.paused, true);
});

test('late response evidence cannot cause Chrome to replay a POST download', async () => {
  const file = activeDownload();
  const app = load({}, { download: file });
  await app.onCreated(file);
  await observe(app, file, { method: 'POST', contentType: null, attachment: false });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(app.sent.length, 0);
  assert.equal(file.paused, false);
});

test('POST attachments stay in Chrome and completion is registered once', async () => {
  const file = activeDownload();
  const app = load({ 'browser-completed': { ok: true } }, { download: file });
  await observe(app, file, { method: 'POST' });
  await app.onCreated(file);
  assert.equal((await app.message({ type: 'move-download', downloadId: file.id })).ok, false);
  assert.equal(app.sent.length, 0);
  file.state = 'complete';
  await app.onChanged({ id: file.id, state: { current: 'complete' } });
  await app.onChanged({ id: file.id, danger: { current: 'deepScannedSafe' } });
  assert.deepEqual(app.sent.map(message => message.type), ['browser-completed']);
  assert.equal(app.sent[0].path, file.filename);
  assert.equal(app.sent[0].origin, 'https://example.com');
  assert.equal(app.sent[0].url, undefined);
});

test('a completed blob passes its existing path and origin without replaying the URL', async () => {
  const file = { ...activeDownload(), state: 'complete', url: 'blob:https://example.com/temporary-uuid' };
  const app = load({ 'browser-completed': { ok: true } }, { download: file });
  await app.onChanged({ id: file.id, state: { current: 'complete' } });
  assert.equal(app.sent[0].type, 'browser-completed');
  assert.equal(app.sent[0].length, file.fileSize);
  assert.equal(app.sent[0].origin, 'https://example.com');
  assert.ok(!JSON.stringify(await app.local.get('browserDownloads')).includes('temporary-uuid'));
  for (const options of [{ download: { ...file, incognito: true } }, { download: file, prefs: { intercept: false } }]) {
    const optedOut = load({}, options);
    await optedOut.onCreated(options.download);
    assert.equal(optedOut.sent.length, 0);
  }
});

test('Chrome can complete while VESPER is staging the handoff', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': () => { file.state = 'complete'; return started; },
    'browser-transfer': transfer('failed'), 'browser-completed': { ok: true } }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  await app.onChanged({ id: file.id, state: { current: 'complete' } });
  assert.equal(app.actions.includes('pause'), false);
  assert.equal(app.actions.includes('cancel'), false);
  assert.equal(app.sent.at(-1).type, 'browser-completed');
  assert.equal(app.sent.at(-1).transferId, app.sent[0].id);
});

test('an unknown abandon state keeps a paused Chrome download parked', async () => {
  const file = activeDownload();
  let state = 'active';
  const app = load({ 'browser-submit': started, 'browser-transfer': () => transfer(state) }, { download: file });
  await observe(app, file);

  const result = await app.message({ type: 'move-download', downloadId: file.id });
  state = 'stopping';
  await app.recover();

  assert.equal(result.chromePaused, true);
  assert.equal(file.paused, true);
  assert.equal(app.actions.includes('resume'), false);
  assert.equal((await app.local.get('browserDownloads')).browserDownloads[file.id].stage, 'parked');
});

test('Chrome resumes when an ok acceptance reply reports a failed VESPER transfer', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started, 'browser-transfer': () => transfer('failed') },
    { download: file, prefs: { intercept: false } });
  await observe(app, file);

  const result = await app.message({ type: 'move-download', downloadId: file.id });

  assert.equal(result.chromePaused, false);
  assert.equal(file.paused, false);
  assert.ok(app.actions.includes('resume'));
  assert.deepEqual(app.sent.filter(message => message.type === 'browser-transfer').map(message => message.operation),
    ['accept', 'abandon']);
});

test('a browser watch that reaches its timeout is rearmed', async () => {
  const file = activeDownload();
  let app;
  let watches = 0;
  const replies = { 'browser-submit': started, 'browser-transfer': message => {
    if (message.operation !== 'watch') return transfer('active');
    watches++;
    if (watches === 1) { app.advanceTime(25000); return transfer('active'); }
    return transfer('complete');
  } };
  app = load(replies, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  for (let attempt = 0; attempt < 20 && !app.actions.includes('cancel'); attempt++)
    await new Promise(resolve => setImmediate(resolve));

  assert.equal(watches, 2);
  assert.equal(app.actions.includes('cancel'), true);
});

test('recovery resumes only a Chrome download paused by VESPER when the transfer fails', async () => {
  for (const alreadyPaused of [false, true]) {
    const file = { ...activeDownload(), paused: alreadyPaused };
    let state = 'active';
    const app = load({ 'browser-submit': started, 'browser-transfer': () => transfer(state) }, { download: file });
    assert.equal((await app.message({ type: 'move-download', downloadId: file.id })).ok, true);
    state = 'failed';
    await app.recover();
    assert.equal(app.actions.includes('resume'), !alreadyPaused);
    assert.equal(app.actions.includes('cancel'), false);
    assert.equal(file.paused, alreadyPaused);
  }
});

test('recovery cancels the paused Chrome transfer only after VESPER reports completion', async () => {
  const file = activeDownload();
  let state = 'active';
  const app = load({ 'browser-submit': started, 'browser-transfer': () => transfer(state) }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  await app.recover();
  assert.equal(app.actions.includes('cancel'), false);
  state = 'complete';
  await app.recover();
  assert.equal(app.actions.at(-1), 'cancel');
  assert.equal(app.actions.includes('erase'), false);
});

test('first-click discovery uses the actual player frame and its HTTP source', async () => {
  const options = { ok: true, data: { choices: [{ index: 0, label: 'Best quality' }] } };
  const app = load({ 'media-options': options });
  const sender = { url: 'https://player.example/embed', tab: { id: 5, url: 'https://example.com/watch' } };
  const result = await app.message({ type: 'media-options', title: 'Example clip',
    pageUrl: 'https://unrelated.example/', playerPageUrl: sender.url, playerUrl: 'https://cdn.example/master.m3u8?key=1' }, sender);
  assert.deepEqual(result, options);
  assert.equal(app.sent.length, 1);
  assert.equal(app.sent[0].url, sender.url);
  assert.equal(app.sent[0].candidates[0].kind, 'hls');
  assert.equal(app.sent[0].pageTitle, 'Example clip');
});

test('feed previews resolve against the selected YouTube, Instagram, and TikTok item URL', async () => {
  const cases = [
    ['https://www.youtube.com/', 'https://www.youtube.com/watch?v=abcdefghijk'],
    ['https://www.instagram.com/', 'https://www.instagram.com/reel/shortcode/'],
    ['https://www.tiktok.com/foryou', 'https://www.tiktok.com/@creator/video/1234567890']
  ];
  for (const [feed, item] of cases) {
    const app = load({ 'media-options': { ok: true } });
    const sender = { url: feed, tab: { id: 12 }, frameId: 0, documentId: 'feed-document' };
    await app.message({ type: 'media-options', pageUrl: item, playerPageUrl: item,
      playerUrl: 'blob:https://www.youtube.com/preview', title: 'Feed preview' }, sender);
    assert.equal(app.sent[0].url, item);
    assert.equal(app.sent[0].pageTitle, 'Feed preview');
  }
});

test('YouTube playlist changes use the live watch URL and omit the previous player source and title', async () => {
  const app = load({ 'media-options': { ok: true } });
  const previous = 'https://www.youtube.com/watch?v=aaaaaaaaaaa&list=RDlist';
  const current = 'https://www.youtube.com/watch?v=bbbbbbbbbbb&list=RDlist';
  await app.message({ type: 'media-options', pageUrl: current, title: 'Previous video',
    playerUrl: 'https://video.example/previous.mp4' }, { url: previous, tab: { id: 5, url: previous } });
  assert.equal(app.sent[0].url, current);
  assert.equal(app.sent[0].candidates, undefined);
  assert.equal(app.sent[0].pageTitle, undefined);
});

test('a blob player only receives its declared manifest from its current frame and page', async () => {
  const app = load({ 'media-options': { ok: true } });
  const previous = 'https://example.com/first';
  const current = 'https://example.com/second';
  const sender = { url: previous, tab: { id: 5, url: previous }, documentId: 'main-document' };
  const observe = (url, documentId = 'main-document') => app.onMedia({
    url, documentId, frameId: 0, tabId: 5, method: 'GET', statusCode: 200, type: 'xmlhttprequest', timeStamp: Date.now()
  });
  await Promise.all([observe('https://cdn.example/first.m3u8'), observe('https://cdn.example/second.m3u8'),
    observe('https://cdn.example/iframe.m3u8', 'iframe-document')]);
  await app.message({ type: 'media-options', pageUrl: current, playerPageUrl: current,
    playerUrl: 'blob:https://example.com/video', manifestUrl: 'https://cdn.example/second.m3u8' }, sender);
  assert.deepEqual([...app.sent[0].candidates.map(c => c.url)], ['https://cdn.example/second.m3u8']);
  await app.message({ type: 'media-options', pageUrl: current, playerPageUrl: current,
    playerUrl: 'blob:https://example.com/video', manifestUrl: 'https://cdn.example/iframe.m3u8' }, sender);
  assert.equal(app.sent[1].candidates.length, 0);
});

test('a blob player must choose even a single inferred manifest before VESPER receives it', async () => {
  const app = load({ 'media-options': { ok: true } });
  const page = 'https://example.com/watch/42';
  const sender = { url: page, tab: { id: 7 }, frameId: 2, documentId: 'active-player' };
  const changedAt = Date.now();
  await app.onMedia({ url: 'https://cdn.example.com/video/playlist.m3u8', tabId: 7, frameId: 2,
    documentId: 'active-player', method: 'GET', statusCode: 200, type: 'xmlhttprequest', timeStamp: changedAt + 100,
    responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }] });

  const message = { type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'blob:https://example.com/player', generation: 3, playerPlaying: true, mediaChangedAt: changedAt };
  const options = await app.message(message, sender);

  assert.equal(options.data.sourceChoices.length, 1);
  assert.deepEqual(app.sent, []);
  await app.message({ type: 'media-source-select', sourceChoice: options.data.sourceChoices[0].token,
    pageUrl: page, generation: 3, playerUrl: message.playerUrl }, sender);
  assert.deepEqual([...app.sent[0].candidates.map(candidate => candidate.url)],
    ['https://cdn.example.com/video/playlist.m3u8']);
});

test('an ambiguous blob player shows scoped stream choices before sending a candidate to VESPER', async () => {
  const app = load({ 'media-options': { ok: true } });
  const page = 'https://example.com/watch/42';
  const sender = { url: page, tab: { id: 7 }, frameId: 2, documentId: 'active-player' };
  const changedAt = Date.now();
  for (const name of ['ad.m3u8?sig=hidden', 'video.m3u8?sig=private'])
    await app.onMedia({ url: `https://cdn.example.com/${name}`, tabId: 7, frameId: 2,
      documentId: 'active-player', method: 'GET', statusCode: 200, type: 'xmlhttprequest',
      timeStamp: changedAt + (name.startsWith('video') ? 200 : 100),
      responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }] });

  const message = { type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'blob:https://example.com/player', generation: 3, playerPlaying: true, mediaChangedAt: changedAt };
  const options = await app.message(message, sender);

  assert.equal(options.ok, true);
  assert.equal(options.data.sourceChoices.length, 2);
  assert.equal(app.sent.length, 0);
  assert.equal(JSON.stringify(options.data.sourceChoices).includes('sig='), false);
  assert.equal(JSON.stringify(options.data.sourceChoices).includes('.m3u8'), false);

  const token = options.data.sourceChoices[0].token;
  const selected = await app.message({ type: 'media-source-select', sourceChoice: token, pageUrl: page,
    generation: 3, playerUrl: message.playerUrl }, sender);
  assert.equal(selected.ok, true);
  assert.deepEqual([...app.sent[0].candidates.map(candidate => candidate.url)],
    ['https://cdn.example.com/video.m3u8?sig=private']);

  const replay = await app.message({ type: 'media-source-select', sourceChoice: token, pageUrl: page,
    generation: 3, playerUrl: message.playerUrl }, sender);
  assert.equal(replay.code, 'expired-selection');
  assert.equal(app.sent.length, 1);
});

test('source-choice tokens are not restored after a worker restarts', async () => {
  const sessionData = {};
  const app = load({ 'media-options': { ok: true } }, { sessionData });
  const page = 'https://example.com/watch/42';
  const sender = { url: page, tab: { id: 7 }, frameId: 2, documentId: 'active-player' };
  const changedAt = Date.now();
  for (const name of ['one.m3u8', 'two.m3u8']) await app.onMedia({ url: `https://cdn.example.com/${name}`,
    tabId: 7, frameId: 2, documentId: 'active-player', method: 'GET', statusCode: 200,
    type: 'xmlhttprequest', timeStamp: changedAt + 100 });
  const message = { type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'blob:https://example.com/player', generation: 4, playerPlaying: true, mediaChangedAt: changedAt };
  const options = await app.message(message, sender);
  const select = token => app.message({ type: 'media-source-select', sourceChoice: token,
    pageUrl: page, generation: 4, playerUrl: message.playerUrl }, sender);

  assert.equal((await select(options.data.sourceChoices[0].token)).ok, true);
  const wrongWorker = load({ 'media-options': { ok: true } }, { sessionData });
  assert.equal((await wrongWorker.message({ type: 'media-source-select', sourceChoice: options.data.sourceChoices[1].token,
    pageUrl: page, generation: 4, playerUrl: message.playerUrl }, sender)).code, 'expired-selection');
  assert.deepEqual(wrongWorker.sent, []);
});

test('source-choice tokens reject mismatched frame, document, page, generation, and player source', async () => {
  const app = load({ 'media-options': { ok: true } });
  const page = 'https://example.com/watch/42';
  const sender = { url: page, tab: { id: 7 }, frameId: 2, documentId: 'active-player' };
  const changedAt = Date.now();
  for (const name of ['one.m3u8', 'two.m3u8']) await app.onMedia({ url: `https://cdn.example.com/${name}`,
    tabId: 7, frameId: 2, documentId: 'active-player', method: 'GET', statusCode: 200,
    type: 'xmlhttprequest', timeStamp: changedAt + 100 });
  const original = { pageUrl: page, playerPageUrl: page, playerUrl: 'blob:https://example.com/player',
    generation: 8, playerPlaying: true, mediaChangedAt: changedAt };
  const invalid = [
    [{ ...sender, frameId: 3 }, { pageUrl: page }],
    [{ ...sender, documentId: 'new-document' }, { pageUrl: page }],
    [sender, { pageUrl: 'https://example.com/watch/99' }],
    [sender, { pageUrl: page, generation: 9 }],
    [sender, { pageUrl: page, playerUrl: 'blob:https://example.com/other' }]
  ];
  for (const [badSender, changes] of invalid) {
    const options = await app.message({ type: 'media-options', ...original }, sender);
    const reply = await app.message({ type: 'media-source-select', sourceChoice: options.data.sourceChoices[0].token,
      pageUrl: page, generation: original.generation, playerUrl: original.playerUrl, ...changes }, badSender);
    assert.equal(reply.code, 'expired-selection');
  }
  assert.deepEqual(app.sent, []);
});

test('direct player evidence observed just before playback metadata is still current', async () => {
  const app = load({}, { prefs: { route: 'chrome' } });
  const page = 'https://www.youtube.com/shorts/bbbbbbbbbbb';
  const playerUrl = 'https://video.example/current.mp4';
  const sender = { url: page, tab: { id: 9 }, frameId: 0, documentId: 'current' };
  const seenAt = Date.now();
  await app.onMedia({ url: playerUrl, documentId: 'current', frameId: 0, tabId: 9, method: 'GET', statusCode: 200,
    type: 'media', timeStamp: seenAt, responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });

  const options = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl, mediaChangedAt: seenAt + 500 }, sender);

  assert.equal(options.ok, true);
  assert.equal(options.data.choices[0].container, 'MP4');
});

test('a complete direct player file falls back to Chrome when VESPER reports no media', async () => {
  const app = load({ 'media-options': { ok: false, code: 'no-media', message: 'No downloadable media was found.' } });
  const page = 'https://www.youtube.com/shorts/bbbbbbbbbbb';
  const playerUrl = 'https://video.example/current.mp4';
  const sender = { url: page, tab: { id: 9 }, frameId: 0, documentId: 'current' };
  await app.onMedia({ url: playerUrl, documentId: 'current', frameId: 0, tabId: 9, method: 'GET', statusCode: 200,
    type: 'media', timeStamp: Date.now(), responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });

  const options = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl, mediaChangedAt: Date.now() }, sender);

  assert.equal(options.ok, true);
  assert.ok(options.data.browserChoice);
  assert.equal(app.actions.filter(action => action === 'native').length, 1);
  assert.equal(app.actions.includes('chrome-start'), false);
});

test('a Chrome route uses one observed complete file and rejects stale or foreign selections', async () => {
  const app = load({}, { prefs: { route: 'chrome' } });
  const current = 'https://www.youtube.com/shorts/bbbbbbbbbbb';
  const playerUrl = 'https://video.example/current.mp4';
  const sender = { url: current, tab: { id: 9 }, frameId: 0, documentId: 'current' };
  await app.onMedia({ url: playerUrl, documentId: 'current', frameId: 0, tabId: 9, method: 'GET', statusCode: 200,
    type: 'media', timeStamp: Date.now(), responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
  const options = await app.message({ type: 'media-options', pageUrl: current, playerPageUrl: current, playerUrl }, sender);
  assert.equal(options.ok, true);
  assert.equal(options.data.choices[0].container, 'MP4');
  const select = { type: 'browser-media-select', pageUrl: current, browserChoice: options.data.browserChoice };
  assert.equal((await app.message(select, { ...sender, documentId: 'foreign' })).ok, false);
  assert.equal((await app.message({ ...select, pageUrl: 'https://www.youtube.com/shorts/ccccccccccc' }, sender)).ok, false);
  assert.equal((await app.message(select, sender)).ok, true);
  assert.equal((await app.message(select, sender)).ok, true);
  assert.deepEqual(app.actions, ['chrome-start']);
});

test('a browser format choice survives a service worker restart without starting twice', async () => {
  const sessionData = {};
  const downloadHistory = [];
  const current = 'https://www.youtube.com/shorts/bbbbbbbbbbb';
  const playerUrl = 'https://video.example/current.mp4';
  const sender = { url: current, tab: { id: 9 }, frameId: 0, documentId: 'current' };
  const options = { prefs: { route: 'chrome' }, sessionData, downloadHistory };
  const firstWorker = load({}, options);
  await firstWorker.onMedia({ url: playerUrl, documentId: 'current', frameId: 0, tabId: 9, method: 'GET', statusCode: 200,
    type: 'media', timeStamp: Date.now(), responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
  const formats = await firstWorker.message({ type: 'media-options', pageUrl: current, playerPageUrl: current, playerUrl }, sender);

  const workerAfterRestart = load({}, options);
  const select = { type: 'browser-media-select', pageUrl: current, browserChoice: formats.data.browserChoice };
  assert.equal((await workerAfterRestart.message(select, sender)).ok, true);
  assert.equal((await workerAfterRestart.message(select, sender)).ok, true);
  assert.deepEqual(workerAfterRestart.actions, ['chrome-start']);
});

test('concurrent browser format selections start Chrome once', async () => {
  const sessionData = {};
  const downloadHistory = [];
  const current = 'https://www.youtube.com/shorts/bbbbbbbbbbb';
  const playerUrl = 'https://video.example/current.mp4';
  const sender = { url: current, tab: { id: 9 }, frameId: 0, documentId: 'current' };
  const app = load({}, { prefs: { route: 'chrome' }, sessionData, downloadHistory });
  await app.onMedia({ url: playerUrl, documentId: 'current', frameId: 0, tabId: 9, method: 'GET', statusCode: 200,
    type: 'media', timeStamp: Date.now(), responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
  const formats = await app.message({ type: 'media-options', pageUrl: current, playerPageUrl: current, playerUrl }, sender);
  const select = { type: 'browser-media-select', pageUrl: current, browserChoice: formats.data.browserChoice };

  const results = await Promise.all([app.message(select, sender), app.message(select, sender)]);

  assert.deepEqual(results.map(result => result.ok), [true, true]);
  assert.deepEqual(app.actions, ['chrome-start']);
});

test('a choice reconciles a Chrome start whose reply was lost during worker restart', async () => {
  const sessionData = {};
  const downloadHistory = [];
  const current = 'https://www.youtube.com/shorts/bbbbbbbbbbb';
  const playerUrl = 'https://video.example/current.mp4';
  const sender = { url: current, tab: { id: 9 }, frameId: 0, documentId: 'current' };
  const options = { prefs: { route: 'chrome' }, sessionData, downloadHistory };
  const firstWorker = load({}, options);
  await firstWorker.onMedia({ url: playerUrl, documentId: 'current', frameId: 0, tabId: 9, method: 'GET', statusCode: 200,
    type: 'media', timeStamp: Date.now(), responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
  const formats = await firstWorker.message({ type: 'media-options', pageUrl: current, playerPageUrl: current, playerUrl }, sender);
  const saved = sessionData.vesperBrowserChoices.find(([token]) => token === formats.data.browserChoice)[1];
  saved.started = true;
  saved.startedAt = Date.now();
  downloadHistory.push({ id: 40, url: playerUrl, startTime: new Date(saved.startedAt).toISOString(),
    state: 'in_progress', byExtensionId: 'vesper-test' });

  const workerAfterRestart = load({}, options);
  const select = { type: 'browser-media-select', pageUrl: current, browserChoice: formats.data.browserChoice };
  assert.equal((await workerAfterRestart.message(select, sender)).ok, true);
  assert.deepEqual(workerAfterRestart.actions, []);
  assert.equal(sessionData.vesperBrowserChoices.find(([token]) => token === formats.data.browserChoice)[1].downloadId, 40);
});

test('a choice restarts Chrome when the worker died before the download began', async () => {
  const sessionData = {};
  const downloadHistory = [];
  const current = 'https://www.youtube.com/shorts/bbbbbbbbbbb';
  const playerUrl = 'https://video.example/current.mp4';
  const sender = { url: current, tab: { id: 9 }, frameId: 0, documentId: 'current' };
  const options = { prefs: { route: 'chrome' }, sessionData, downloadHistory };
  const firstWorker = load({}, options);
  await firstWorker.onMedia({ url: playerUrl, documentId: 'current', frameId: 0, tabId: 9, method: 'GET', statusCode: 200,
    type: 'media', timeStamp: Date.now(), responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
  const formats = await firstWorker.message({ type: 'media-options', pageUrl: current, playerPageUrl: current, playerUrl }, sender);
  const saved = sessionData.vesperBrowserChoices.find(([token]) => token === formats.data.browserChoice)[1];
  saved.started = true;
  saved.startedAt = Date.now();

  const workerAfterRestart = load({}, options);
  const select = { type: 'browser-media-select', pageUrl: current, browserChoice: formats.data.browserChoice };
  assert.equal((await workerAfterRestart.message(select, sender)).ok, true);
  assert.deepEqual(workerAfterRestart.actions, ['chrome-start']);
  assert.equal(workerAfterRestart.downloadHistory.length, 1);
});

test('blob and data media context-menu actions show failure feedback', async () => {
  const app = load();
  await app.onContextClicked({ menuItemId: 'vesper-media', srcUrl: 'blob:https://example.com/media' }, { id: 3 });
  await app.onContextClicked({ menuItemId: 'vesper-media', srcUrl: 'data:video/mp4;base64,AAAA' }, { id: 3 });
  assert.deepEqual(JSON.parse(JSON.stringify(app.badges)), [{ tabId: 3, text: '!' }, { tabId: 3, text: '!' }]);
});

test('a rate-limited lookup does not start a new request using Chrome', async () => {
  const rateLimit = { ok: false, code: 'rate-limited', data: { retryAt: new Date(Date.now() + 60_000).toISOString() } };
  const app = load({ 'media-options': rateLimit });
  const pageUrl = 'https://www.instagram.com/reel/current/';
  const playerUrl = 'https://media.example/current.mp4';
  await app.onMedia({ url: playerUrl, documentId: 'current', frameId: 0, tabId: 3, method: 'GET', statusCode: 200,
    type: 'media', timeStamp: Date.now(), responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
  const reply = await app.message({ type: 'media-options', pageUrl, playerPageUrl: pageUrl, playerUrl },
    { url: pageUrl, tab: { id: 3 }, documentId: 'current' });
  assert.deepEqual(reply, rateLimit);
  assert.deepEqual(app.actions, ['native']);
});

test('Chrome reports only live metadata and executes controls on the same download', async () => {
  const download = { id: 7, url: 'blob:https://example.com/private', filename: 'C:\\Downloads\\archive.zip', state: 'in_progress',
    danger: 'safe', startTime: '2026-10-10T08:00:00.000Z', totalBytes: 500, bytesReceived: 20, canResume: true };
  const app = load({}, { download, activity: true });
  await app.onCreated(download);
  const [port] = app.ports;
  assert.equal(port.posted[0].operation, 'connect');
  const [file] = port.posted[0].files;
  assert.equal(file.name, 'archive.zip');
  assert.equal(file.received, 20);
  assert.equal(file.length, 500);
  assert.equal(file.url, undefined);
  assert.equal(file.path, undefined);
  await port.reply({ ok: true, data: { commands: [{ id: file.id, action: 'pause' }] } });
  assert.equal(port.posted[1].files[0].paused, true);
  await port.reply({ ok: true, data: { commands: [{ id: file.id, action: 'resume' }] } });
  assert.equal(port.posted[2].files[0].paused, false);
  await port.reply({ ok: true, data: { commands: [{ id: file.id, action: 'cancel' }] } });
  assert.equal(port.posted[3].files.length, 0);
  await port.reply({ ok: true });
  assert.equal(port.disconnected, true);
  assert.deepEqual(app.actions, ['pause', 'resume', 'cancel']);
});

test('format submission uses an opaque selection and rejects an out-of-range choice', async () => {
  const app = load({ 'media-select': { ok: true } });
  const mediaSession = 'edc1604be3d34136a798fca8addbcbcd';
  const msg = { type: 'media-select', mediaSession, formatChoice: 2, mode: 'queue', url: 'https://unrelated.example' };
  assert.equal((await app.message(msg)).ok, true);
  assert.equal((await app.message({ ...msg, formatChoice: 64 })).ok, false);
  assert.equal(app.sent.length, 1);
  assert.equal(app.sent[0].id, mediaSession + '-2-queue');
  assert.equal(app.sent[0].url, undefined);
});

test('manual move starts in VESPER and leaves Chrome running if the host fails or only queues the file', async () => {
  for (const reply of [{ ok: false, code: 'app-unavailable' }, { ok: true, results: [{ status: 'queued' }] }]) {
    const file = activeDownload();
    const app = load({ 'browser-submit': reply }, { download: file, prefs: { intercept: false } });
    const result = await app.message({ type: 'move-download', downloadId: file.id });
    assert.equal(result.chromePaused, false);
    assert.equal(app.sent[0].mode, 'download');
    assert.equal(app.actions.includes('pause'), false);
  }
});

test('downloads awaiting Chrome safety review cannot be moved', async () => {
  const file = { ...activeDownload(), danger: 'uncommon' };
  const app = load({}, { download: file });
  assert.equal((await app.message({ type: 'chrome-downloads' })).downloads.length, 0);
  assert.equal((await app.message({ type: 'move-download', downloadId: file.id })).ok, false);
  assert.equal(app.sent.length, 0);
});

test('site cookies are omitted unless sign-in is explicitly enabled', async () => {
  const app = load({ submit: { ok: true } }, { signInPermission: true });

  await app.message({ type: 'submit', pageUrl: 'https://public.example/',
    items: [{ url: 'https://public.example/file.zip' }] });

  assert.equal(app.cookieLookups.length, 0);
  assert.equal(app.sent[0].items[0].cookie, undefined);
  assert.equal(app.sent[0].authorized, false);
});

test('explicit sign-in retries reacquire cookies and stored requests omit credentials', async () => {
  const file = activeDownload();
  const replies = { 'browser-submit': { ok: false, code: 'host-error' }, 'browser-transfer': transfer('active') };
  const app = load(replies, { download: file, signInPermission: true, prefs: { useSignIn: true } });
  await app.message({ type: 'move-download', downloadId: file.id });
  assert.equal(app.sent[0].items[0].cookie, 'session=test-value');
  assert.ok(!JSON.stringify(await app.local.get('browserDownloads')).includes('test-value'));
  replies['browser-submit'] = started;
  assert.equal((await app.message({ type: 'move-download', downloadId: file.id })).chromePaused, true);
  assert.equal(app.sent[1].items[0].cookie, 'session=test-value');
  assert.deepEqual(app.cookieLookups, [file.url, file.url]);

  for (const opts of [{ signInPermission: false }, { signInPermission: true, prefs: { useSignIn: false } }]) {
    const optedOut = load(replies, { download: activeDownload(), ...opts });
    await optedOut.message({ type: 'move-download', downloadId: file.id });
    assert.equal(optedOut.cookieLookups.length, 0);
    assert.equal(optedOut.sent[0].authorized, false);
  }
});
