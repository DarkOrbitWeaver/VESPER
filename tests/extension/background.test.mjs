// Runs the real background.js with a mocked Chrome API. Run: node --test tests/extension/background.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { webcrypto } from 'node:crypto';

const isBrowserSubmission = message => ['browser-capture', 'browser-submit'].includes(message.type);

function load(nativeReplies = {}, { download, downloadHistory = [], signInPermission = false, prefs = {}, activity = false, sessionData = {},
  failDownloadSearch = false, failDownloadCancel = false, deferDownloadCancel = false,
  localData = {}, syncData = { ...prefs }, cookies = [{ name: 'session', value: 'test-value', path: '/' }],
  preflightReady = false, preflightDelay = 0, playerPicker = false, helperProofs, earlyFetch } = {}) {
  const listeners = [];
  const event = { addListener: (fn) => listeners.push(fn) };
  const storage = (data = {}) => {
    return {
      get: async key => {
        if (key === null) return structuredClone(data);
        const keys = Array.isArray(key) ? key : typeof key === 'string' ? [key] : Object.keys(key);
        const result = typeof key === 'object' && !Array.isArray(key) ? { ...key } : {};
        for (const name of keys) if (Object.hasOwn(data, name)) result[name] = data[name];
        return structuredClone(result);
      },
      set: async values => Object.assign(data, structuredClone(values)),
      remove: async key => { for (const name of Array.isArray(key) ? key : [key]) delete data[name]; }
    };
  };
  let onMessage, onCreated, onChanged, onFilename, onHeaders, onRedirect, onMedia, onAlarm, onContextClicked, onInstalled;
  const storageListeners = new Set();
  const storageChanged = (changes, area) => Promise.all([...storageListeners].map(listener => listener(changes, area)));
  const redirectListeners = [];
  const sent = [];
  const policyRequests = [];
  const preflights = [];
  const actions = [];
  const cookieLookups = [];
  const badges = [];
  const contextMenuItems = [];
  const tabMessages = [];
  const alarms = new Map();
  const ports = [];
  const timers = new Set();
  const timerDelays = [];
  const manifestWatchers = new Map();
  let finishDownloadCancel;
  let digestCalls = 0;
  let deferredDigestCall = 0;
  let finishDigest;
  const testSetTimeout = (callback, delay, ...args) => {
    timerDelays.push(delay);
    let timer;
    timer = setTimeout(() => { timers.delete(timer); callback(...args); }, delay);
    timers.add(timer);
    return timer;
  };
  const testClearTimeout = timer => { timers.delete(timer); clearTimeout(timer); };
  // Sign-in fixtures model a user who also approved the test site's exact host.
  const localSeed = { ...(prefs.useSignIn ? { signInHosts: ['example.com'] } : {}), ...localData };
  const local = storage(localSeed);
  const saveLocal = local.set;
  local.set = async values => {
    const changes = {};
    for (const [key, value] of Object.entries(values))
      changes[key] = { oldValue: (await local.get(key))[key], newValue: structuredClone(value) };
    await saveLocal(values);
    await storageChanged(changes, 'local');
  };
  if (download && !downloadHistory.some(item => item.id === download.id)) downloadHistory.push(download);
  const chrome = {
    runtime: {
      id: 'vesper-test',
      onMessage: { addListener: fn => { onMessage = fn; } }, onInstalled: { addListener: fn => { onInstalled = fn; } }, onStartup: event, lastError: undefined,
      sendNativeMessage: (host, message, cb) => {
        if (message.type === 'capture-preflight') {
          preflights.push(message);
          const respond = () => cb({ ok: true, data: { ready: preflightReady, siteRules: { neverCaptureHosts: [] },
            ...(helperProofs ? { proofs: helperProofs } : {}) } });
          if (preflightDelay) testSetTimeout(respond, preflightDelay); else respond();
          return;
        }
        if (message.type === 'hello') policyRequests.push(message);
        else {
          actions.push('native');
          sent.push(message);
        }
        const reply = Object.hasOwn(nativeReplies, message.type) ? nativeReplies[message.type]
          : message.type === 'browser-capture' ? nativeReplies['browser-submit']
          : message.type === 'hello' ? { ok: true, data: { siteRules: { neverCaptureHosts: [] } } } : undefined;
        const response = typeof reply === 'function' ? reply(message) : reply;
        const fallback = { ok: false, code: 'host-error', message: 'no reply' };
        if (response && typeof response.then === 'function') response.then(value => cb(value ?? fallback), () => cb(fallback));
        else cb(response ?? fallback);
      }
    },
    webRequest: {
      onBeforeRequest: { addListener: () => {} },
      onResponseStarted: {
        addListener: (fn, filter) => {
          if (filter?.tabId !== undefined) manifestWatchers.set(filter.tabId, fn);
          else onMedia = fn;
        },
        removeListener: fn => {
          for (const [tab, listener] of manifestWatchers) if (fn === listener) manifestWatchers.delete(tab);
        }
      },
      onHeadersReceived: { addListener: fn => { onHeaders = fn; } },
      onBeforeRedirect: { addListener: fn => {
        redirectListeners.push(fn);
        onRedirect = details => { for (const listener of redirectListeners) listener(details); };
      } }
    },
    tabs: {
      onUpdated: event, onRemoved: event, query: async () => [],
      sendMessage: async (tabId, message, options) => {
        tabMessages.push({ tabId, message, options });
        if (message.type === 'vesper-open-picker') return playerPicker ? { ok: true } : { ok: false, code: 'no-player' };
        throw new Error('No receiver');
      }
    },
    contextMenus: {
      create: item => contextMenuItems.push(item),
      remove: async id => { const index = contextMenuItems.findIndex(item => item.id === id); if (index >= 0) contextMenuItems.splice(index, 1); },
      removeAll: async () => { contextMenuItems.length = 0; }, onClicked: { addListener: fn => { onContextClicked = fn; } }
    },
    alarms: {
      get: async name => alarms.get(name),
      create: async (name, details) => alarms.set(name, details),
      onAlarm: { addListener: fn => { onAlarm = fn; } }
    },
    downloads: {
      onCreated: { addListener: fn => { onCreated = fn; } },
      onChanged: { addListener: fn => { onChanged = fn; } },
      onDeterminingFilename: { addListener: fn => { onFilename = fn; } },
      search: async ({ id, state, startedAfter, startedBefore, limit }) => {
        if (failDownloadSearch) throw new Error('Chrome download search failed.');
        return downloadHistory
          .filter(item => (id === undefined || item.id === id) && (state === undefined || item.state === state)
            && (startedAfter === undefined || Date.parse(item.startTime) >= Date.parse(startedAfter))
            && (startedBefore === undefined || Date.parse(item.startTime) <= Date.parse(startedBefore)))
          .slice(0, limit || downloadHistory.length).map(item => ({ ...item }));
      },
      download: async ({ url }) => {
        actions.push('chrome-start');
        const id = Math.max(0, ...downloadHistory.map(item => item.id)) + 1;
        downloadHistory.push({ id, url, startTime: new Date().toISOString(), state: 'in_progress', byExtensionId: chrome.runtime.id });
        return id;
      },
      pause: async () => { actions.push('pause'); download.paused = true; },
      resume: async () => {
        actions.push('resume');
        download.paused = false;
        if (download.state === 'interrupted') download.state = 'in_progress';
        download.canResume = false;
      },
      cancel: async () => {
        actions.push('cancel');
        if (failDownloadCancel) throw new Error('Chrome refused cancellation.');
        if (deferDownloadCancel) await new Promise(resolve => { finishDownloadCancel = resolve; });
        download.state = 'interrupted';
        download.paused = false;
        download.canResume = false;
        download.error = 'USER_CANCELED';
      },
      erase: async () => actions.push('erase')
    },
    action: { setBadgeText: async value => badges.push(value), setBadgeBackgroundColor: async () => {} },
    storage: {
      onChanged: { addListener: fn => storageListeners.add(fn), removeListener: fn => storageListeners.delete(fn) },
      sync: {
        ...storage(syncData),
        set: async values => {
          const changes = {};
          for (const [key, value] of Object.entries(values)) {
            changes[key] = { oldValue: syncData[key], newValue: value };
            syncData[key] = value;
          }
          await storageChanged(changes, 'sync');
        }
      },
      local, session: storage(sessionData)
    },
    permissions: { contains: async () => signInPermission },
    cookies: { getAll: async ({ url }) => { cookieLookups.push(url); return cookies.map(cookie => ({ ...cookie })); } }
  };
  if (activity) chrome.runtime.connectNative = () => {
    const port = {
      posted: [],
      onMessage: { addListener: fn => { port.reply = fn; } },
      onDisconnect: { addListener: fn => { port.close = fn; } },
      postMessage: message => {
        // This fixture's activity connection is independent of the legacy request host.
        if (message.type === 'session-open') {
          ports.splice(ports.indexOf(port), 1);
          queueMicrotask(() => port.reply({id: message.id, ok:false, code:'invalid', message:'Unknown request type.'}));
        } else port.posted.push(message);
      },
      disconnect: () => { port.disconnected = true; port.close(); }
    };
    ports.push(port);
    return port;
  };
  let sequence = 0;
  let now = Date.now();
  const TestDate = class extends Date { static now() { return now; } };
  const subtle = { digest: async (...args) => {
    digestCalls++;
    if (digestCalls === deferredDigestCall)
      await new Promise(resolve => { finishDigest = resolve; });
    return webcrypto.subtle.digest(...args);
  } };
  const context = { chrome, URL, AbortController, fetch: earlyFetch, Date: TestDate, TextEncoder, Uint8Array, crypto: { subtle, randomUUID: () => `test-uuid-${++sequence}-aaaa` }, navigator: { userAgent: 'test' }, setTimeout: testSetTimeout, clearTimeout: testClearTimeout, console };
  vm.createContext(context);
  context.importScripts = (...files) => {
    for (const file of files) {
      const path = `../../extension/${file}`;
      const source = readFileSync(new URL(path, import.meta.url), 'utf8');
      vm.runInContext(source, context);
    }
  };
  vm.runInContext(readFileSync(new URL('../../extension/background.js', import.meta.url), 'utf8') + '\n;globalThis.__describe = describe;', context);
  return { describe: context.__describe, sent, preflights, policyRequests, actions, cookieLookups, badges, contextMenuItems, tabMessages, local, sessionData, downloadHistory, timerDelays, onCreated, onChanged, onHeaders, onRedirect, onMedia, onContextClicked, ports,
    localData: localSeed, syncData, manifestWatchers, filename: item => {
      let suggested;
      onFilename(item, suggestion => { suggested = suggestion; });
      return suggested;
    },
    install: details => onInstalled(details),
    finishDownloadCancel: () => finishDownloadCancel?.(),
    deferDigestAt: callsAhead => { deferredDigestCall = digestCalls + callsAhead; },
    digestIsBlocked: () => typeof finishDigest === 'function',
    finishDigest: () => { const finish = finishDigest; finishDigest = undefined; finish?.(); },
    advanceTime: milliseconds => { now += milliseconds; },
    setPrefs: values => chrome.storage.sync.set(values),
    stopWorker: () => { for (const timer of timers) clearTimeout(timer); timers.clear(); },
    recover: () => { alarms.delete('vesper-chrome-downloads'); return onAlarm({ name: 'vesper-chrome-downloads' }); },
    expireSources: () => { alarms.delete('vesper-media-sources'); return onAlarm({ name: 'vesper-media-sources' }); },
    // Chrome always stamps the sender. A bare call models VESPER's own page; a sender with a tab models a content script.
    message: (msg, sender = {}) => new Promise(resolve => onMessage(msg, {
      id: 'vesper-test', ...(sender.tab || sender.url ? { url: sender.tab?.url } : { url: 'chrome-extension://vesper-test/transfers.html' }), ...sender
    }, resolve)),
    rawMessage: (msg, sender) => new Promise(resolve => onMessage(msg, sender, resolve)),
    evaluate: code => vm.runInContext(code, context) };
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
  filename: 'C:\\Downloads\\archive.zip', startTime: '2026-11-12T01:00:00.000Z', fileSize: 1000, exists: true, bytesReceived: 0
});
const started = { ok: true, results: [{ status: 'started', transferReady: true }] };
const transfer = state => ({ ok: true, data: { state } });

function observe(app, file, { method = 'GET', type = 'main_frame', contentType = 'application/zip', attachment = true, acceptsRanges = true, requestId = 'download-request' } = {}) {
  const headers = { 'Content-Length': String(file.fileSize), ETag: '"version-1"' };
  if (acceptsRanges) headers['Accept-Ranges'] = 'bytes';
  if (contentType) headers['Content-Type'] = contentType;
  if (attachment) headers['Content-Disposition'] = 'attachment; filename="archive.zip"';
  return app.onHeaders({ url: file.finalUrl || file.url, requestId, method, type, statusCode: 200,
    responseHeaders: Object.entries(headers).map(([name, value]) => ({ name, value })) });
}

async function waitFor(condition, message) {
  // WebCrypto digests and Chrome storage actions resolve asynchronously, not on the same event-loop turn.
  for (let attempt = 0; attempt < 100; attempt++) {
    if (condition()) return;
    await new Promise(resolve => setTimeout(resolve, 2));
  }
  assert.ok(condition(), message);
}

test('one native preflight covers an attachment burst without sending its URL', async () => {
  const file = activeDownload();
  const app = load({}, { prefs: { intercept: true, route: 'auto' } });
  await observe(app, file, { requestId: 'attachment-one' });
  await waitFor(() => app.preflights.length === 1, 'The first attachment did not preflight VESPER.');

  await observe(app, { ...file, url: 'https://example.com/files/second.zip?token=private' },
    { requestId: 'attachment-two' });
  await new Promise(resolve => setTimeout(resolve, 20));

  assert.equal(app.preflights.length, 1);
  assert.doesNotMatch(JSON.stringify(app.preflights[0]), /example\.com|private/);
});

test('a warm preflight pauses a validated attachment before starting native staging', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') },
    { download: file, preflightReady: true });
  await observe(app, file);
  await waitFor(() => app.preflights.length === 1, 'The native helper did not preflight the attachment.');

  await app.onCreated(file);

  assert.equal(file.paused, true);
  assert.equal(app.actions[0], 'pause');
  assert.equal(app.actions[1], 'native');
  assert.equal(app.sent[0].type, 'browser-capture');
  assert.equal(app.sent[1].operation, 'accept');
  assert.equal(app.localData.browserDownloads[file.id].stage, 'parked');
});

test('a validated binary browser download without Content-Disposition gets early takeover', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') },
    { download: file, preflightReady: true });
  try {
    await observe(app, file, { attachment: false, contentType: 'application/octet-stream' });
    await waitFor(() => app.preflights.length === 1);
    await app.onCreated(file);
    assert.equal(app.actions[0], 'pause');
    assert.equal(app.actions[1], 'native');
    assert.equal(app.localData.browserDownloads[file.id].stage, 'parked');
  } finally { app.stopWorker(); }
});
test('an in-flight cold preflight does not leave a verified attachment racing Chrome', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') },
    { download: file, preflightReady: true, preflightDelay: 3000 });
  try {
    await observe(app, file);
    await waitFor(() => app.preflights.length === 1);
    await app.onCreated(file);
    assert.equal(app.actions[0], 'pause');
    assert.equal(file.paused, true);
    assert.equal(app.localData.browserDownloads[file.id].stage, 'parked');
  } finally { app.stopWorker(); }
});
test('early capture resumes the Chrome stream when native staging fails', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': { ok: false, code: 'network', message: 'The server refused VESPER.' },
    'browser-transfer': transfer('failed') }, { download: file, preflightReady: true });
  await observe(app, file);
  await waitFor(() => app.preflights.length === 1, 'The native helper did not preflight the attachment.');

  await app.onCreated(file);

  assert.equal(file.paused, false);
  assert.deepEqual(app.actions, ['pause', 'native', 'native', 'resume']);
  assert.equal(app.localData.browserDownloads[file.id].stage, 'stand-down');
});

test('warm helper does not park before staging when server has not advertised byte ranges', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') },
    { download: file, preflightReady: true });
  await observe(app, file, { acceptsRanges: false });
  await waitFor(() => app.preflights.length === 1);
  await app.onCreated(file);
  assert.equal(app.actions[0], 'native');
  assert.equal(file.paused, true);
});

test('expired parking attempt is abandoned rather than accepted after worker restart', async () => {
  const file = activeDownload();
  const first = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file });
  await observe(first, file);
  await first.onCreated(file);
  const parked = first.localData.browserDownloads[file.id];
  first.stopWorker();
  const app = load({ 'browser-transfer': transfer('failed') }, { download: file,
    localData: { browserDownloads: { [file.id]: { ...parked, stage: 'parking', parkUntil: Date.now() - 1 } } } });
  await app.recover();
  assert.equal(app.sent.some(m => m.operation === 'accept'), false);
  assert.equal(app.sent.some(m => m.operation === 'abandon'), true);
  assert.equal(file.paused, false);
  assert.equal(app.actions.includes('cancel'), false);
});

test('automatic handoff verifies the response before pausing Chrome and accepts before monitoring', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  assert.deepEqual(app.actions, ['native', 'pause', 'native', 'native']);
  assert.equal(app.sent[0].type, 'browser-capture');
  assert.deepEqual(app.policyRequests, []);
  assert.deepEqual(structuredClone(app.sent[0].browserProof), { length: 1000, etag: '"version-1"', contentType: 'application/zip' });
  assert.equal(app.sent[0].items[0].fileName, 'archive.zip');
  assert.equal(app.sent[0].items[0].originalUrl, file.url);
  assert.equal(app.sent[1].operation, 'accept');
  assert.equal(app.sent[2].operation, 'watch');
  assert.equal(file.paused, true);
});

test('pausing during native verification leaves the new download with Chrome', async () => {
  const file = activeDownload();
  let finishSubmit;
  const submit = new Promise(resolve => { finishSubmit = resolve; });
  const app = load({ 'browser-submit': () => submit, 'browser-transfer': transfer('failed') }, { download: file });
  await observe(app, file);

  const capture = app.onCreated(file);
  await waitFor(() => app.sent.some(isBrowserSubmission));
  await app.local.set({ vesperPaused: true });
  finishSubmit(started);
  await capture;

  assert.deepEqual(app.sent.filter(message => message.type === 'browser-transfer').map(message => message.operation), ['abandon']);
  assert.equal(file.paused, false);
  assert.equal(file.state, 'in_progress');
  assert.equal(app.actions.includes('pause'), false);
  assert.equal(app.actions.includes('cancel'), false);
  assert.equal(app.actions.includes('erase'), false);
  assert.equal(app.localData.browserDownloads[file.id]?.stage, 'stand-down');

  await app.local.set({ vesperPaused: false });
  await app.onChanged({ id: file.id, totalBytes: { current: 1 } });
  assert.equal(app.sent.filter(isBrowserSubmission).length, 1);
});

test('pausing during native acceptance lets the accepted transfer finish', async () => {
  const file = activeDownload();
  let finishAccept;
  const accept = new Promise(resolve => { finishAccept = resolve; });
  let state = 'active';
  const app = load({ 'browser-submit': started, 'browser-transfer': message =>
    message.operation === 'accept' ? accept : transfer(state) }, { download: file });
  await observe(app, file);

  const capture = app.onCreated(file);
  await waitFor(() => app.sent.some(message => message.operation === 'accept'));
  assert.equal(file.paused, true);
  await app.local.set({ vesperPaused: true });
  finishAccept(transfer('active'));
  await capture;
  assert.deepEqual(app.sent.filter(message => message.type === 'browser-transfer').map(message => message.operation), ['accept', 'watch']);
  assert.equal(file.paused, true);
  assert.equal(app.localData.browserDownloads[file.id].stage, 'parked');
  assert.equal(app.actions.includes('resume'), false);
  assert.equal(app.actions.includes('cancel'), false);

  state = 'complete';
  await app.recover();
  assert.equal(file.state, 'interrupted');
  assert.deepEqual(app.actions.slice(-2), ['cancel', 'erase']);
  assert.equal(app.sent.some(message => message.operation === 'abandon'), false);
});

test('an exact never-capture rule leaves a new Chrome download alone before sending its address to VESPER', async () => {
  const file = activeDownload();
  file.referrer = 'https://video.example/watch?token=private';
  const app = load({ hello: { ok: true, data: { siteRules: { neverCaptureHosts: ['video.example'] } } },
    'browser-submit': started }, { download: file, localData: { vesperSiteNeverCaptureHosts: ['video.example'] } });

  await observe(app, file);
  await app.onCreated(file);

  assert.equal(app.policyRequests.length, 1);
  assert.equal(app.policyRequests[0].type, 'hello');
  assert.deepEqual(Object.keys(app.policyRequests[0]).sort(), ['id', 'type', 'v']);
  assert.deepEqual(app.sent, []);
  assert.deepEqual(app.localData.vesperSiteNeverCaptureHosts, ['video.example']);
  assert.equal(file.paused, false);
  assert.equal(app.actions.includes('pause'), false);
  assert.equal(app.actions.includes('cancel'), false);
  assert.equal(app.actions.includes('erase'), false);
  assert.equal(app.sessionData.vesperCaptureDecisions[0].outcome, 'site-rule');
  assert.doesNotMatch(JSON.stringify(app.sessionData.vesperCaptureDecisions), /token=private/);
});

test('automatic capture fails closed when the fresh site policy is unavailable', async () => {
  const unavailableReplies = [
    { ok: false, code: 'timeout', message: 'VESPER did not answer in time.' },
    { ok: true, data: { siteRules: { neverCaptureHosts: ['https://video.example/private'] } } }
  ];
  const cachedLists = [[], ['old.example'], ['video.example']];

  for (const hello of unavailableReplies) {
    for (const cachedHosts of cachedLists) {
      const file = activeDownload();
      file.url = 'https://video.example/file.zip?token=private';
      file.referrer = 'https://video.example/watch?token=private';
      const app = load({ hello, 'browser-submit': started }, {
        download: file,
        signInPermission: true,
        prefs: { useSignIn: true },
        localData: { signInHosts: ['video.example'], vesperSiteNeverCaptureHosts: cachedHosts }
      });

      await observe(app, file);
      await app.onCreated(file);

      assert.equal(app.policyRequests.length, 1);
      assert.deepEqual(Object.keys(app.policyRequests[0]).sort(), ['id', 'type', 'v']);
      assert.deepEqual(app.sent, []);
      assert.deepEqual(app.cookieLookups, []);
      assert.equal(file.paused, false);
      assert.equal(app.actions.includes('pause'), false);
      assert.equal(app.actions.includes('cancel'), false);
      assert.equal(app.actions.includes('erase'), false);
      assert.equal(app.sessionData.vesperCaptureDecisions[0].outcome, 'policy-unavailable');
      assert.doesNotMatch(JSON.stringify(app.sessionData.vesperCaptureDecisions), /https?:|token=private/);
    }
  }
});

test('signed capture site policy waits long enough for a cold desktop start', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started }, { download: file, signInPermission: true, prefs: { useSignIn: true } });
  await observe(app, file);
  await app.onCreated(file);
  await waitFor(() => app.policyRequests.length === 1, 'the site policy check did not run');

  assert.ok(app.timerDelays.includes(15_000));
});

test('unsigned capture honors a site rule the desktop added after the worker started', async () => {
  const file = activeDownload();
  const app = load({ 'browser-capture': { ok: false, code: 'site-capture-disabled' } }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  assert.deepEqual(app.policyRequests, []);
  assert.deepEqual(app.cookieLookups, []);
  assert.deepEqual(app.sent.map(message => message.type), ['browser-capture']);
  assert.equal(file.paused, false);
  assert.equal(app.actions.includes('cancel'), false);
  assert.equal(app.sessionData.vesperCaptureDecisions[0].outcome, 'site-rule');
});

test('an older helper does not fall back to a command that might ignore site rules', async () => {
  const file = activeDownload();
  const app = load({ 'browser-capture': { ok: false, code: 'invalid', message: 'Unknown request type.' } }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  assert.deepEqual(app.sent.map(message => message.type), ['browser-capture']);
  assert.equal(file.paused, false);
  assert.equal(app.actions.includes('cancel'), false);
});

test('Chrome taking bytes during the site check avoids a second download request', async () => {
  const file = activeDownload();
  let allowSite;
  const policy = new Promise(resolve => { allowSite = resolve; });
  const app = load({ hello: policy, 'browser-submit': started }, {
    download: file,
    signInPermission: true,
    prefs: { useSignIn: true }
  });
  await observe(app, file);
  const capture = app.onCreated(file);
  await waitFor(() => app.policyRequests.length === 1, 'the site policy check did not run');
  file.bytesReceived = 4096;
  allowSite({ ok: true, data: { siteRules: { neverCaptureHosts: [] } } });
  await capture;

  assert.deepEqual(app.sent, []);
  assert.deepEqual(app.cookieLookups, []);
  assert.equal(file.paused, false);
  assert.equal(app.sessionData.vesperCaptureDecisions[0].outcome, 'chrome-started');
});

test('automatic capture checks the referrer, original URL and final URL hosts', async () => {
  const cases = [
    { host: 'blocked-origin.example', url: 'https://blocked-origin.example/start?token=private', finalUrl: 'https://cdn.example/file.zip?token=private' },
    { host: 'blocked-final.example', url: 'https://origin.example/start?token=private', finalUrl: 'https://blocked-final.example/file.zip?token=private' }
  ];

  for (const scenario of cases) {
    const file = activeDownload();
    file.url = scenario.url;
    file.finalUrl = scenario.finalUrl;
    file.referrer = 'https://safe.example/page?token=private';
    const app = load({ hello: { ok: true, data: { siteRules: { neverCaptureHosts: [scenario.host] } } },
      'browser-submit': started }, {
      download: file,
      signInPermission: true,
      prefs: { useSignIn: true },
      localData: { signInHosts: [new URL(scenario.finalUrl).hostname] }
    });

    await app.onRedirect({url: file.url, redirectUrl: file.finalUrl, requestId: 'download-request', method: 'GET', type: 'main_frame'});
    await observe(app, file);
    await app.onCreated(file);

    assert.deepEqual(app.sent, []);
    assert.deepEqual(app.cookieLookups, []);
    assert.equal(file.paused, false);
    assert.equal(app.sessionData.vesperCaptureDecisions[0].outcome, 'site-rule');
    assert.doesNotMatch(JSON.stringify(app.sessionData.vesperCaptureDecisions), /https?:|token=private/);
  }
});

test('IPv6 never-capture hosts match the browser’s bracketed URL form', async () => {
  const file = activeDownload();
  file.referrer = 'https://[2001:db8::1]/watch';
  const app = load({ hello: { ok: true, data: { siteRules: { neverCaptureHosts: ['2001:db8::1'] } } } },
    { download: file, localData: { vesperSiteNeverCaptureHosts: ['2001:db8::1'] } });

  await observe(app, file);
  await app.onCreated(file);

  assert.deepEqual(app.sent, []);
  assert.deepEqual(app.localData.vesperSiteNeverCaptureHosts, ['2001:db8::1']);
  assert.equal(file.paused, false);
});

test('paused recovery distinguishes a staged file from an already accepted transfer', async () => {
  for (const state of ['staged', 'active']) {
    const file = { ...activeDownload(), paused: true };
    const hash = await webcrypto.subtle.digest('SHA-256', new TextEncoder().encode(file.url));
    const record = { startTime: file.startTime, transferId: 'lost-accept-reply', sourceHash: Buffer.from(hash).toString('hex'),
      stage: 'parking', resumeChrome: true, parkUntil: Date.now() + 10_000, at: Date.now() };
    const app = load({ 'browser-transfer': message => transfer(message.operation === 'abandon' ? 'failed' : state) }, {
      download: file, localData: { vesperPaused: true, browserDownloads: { [file.id]: record } }
    });
    await app.recover();
    const accepted = state === 'active';
    assert.equal(app.sent.some(message => message.operation === 'status'), true);
    assert.equal(app.sent.some(message => message.operation === 'accept'), false);
    assert.equal(app.sent.some(message => message.operation === 'abandon'), !accepted);
    assert.equal(file.paused, accepted);
    assert.equal(app.localData.browserDownloads[file.id].stage, accepted ? 'parked' : 'stand-down');
    assert.equal(app.actions.includes('cancel'), false);
  }
});


test('pausing during verified handoff cleanup still removes the redundant Chrome entry', async () => {
  const file = activeDownload();
  let state = 'active';
  const app = load({ 'browser-submit': started, 'browser-transfer': message =>
    message.operation === 'accept' ? transfer('active') : transfer(state) }, {
    download: file, deferDownloadCancel: true
  });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(file.paused, true);

  state = 'complete';
  const completing = app.recover();
  await waitFor(() => app.actions.includes('cancel'));
  await app.local.set({ vesperPaused: true });
  assert.equal(app.localData.browserDownloads[file.id].stage, 'parked');
  assert.equal(app.actions.includes('erase'), false);
  app.finishDownloadCancel();
  await completing;

  assert.equal(file.state, 'interrupted');
  assert.equal(file.paused, false);
  assert.equal(file.canResume, false);
  assert.equal(file.error, 'USER_CANCELED');
  assert.equal(app.actions.includes('resume'), false);
  assert.equal(app.actions.includes('erase'), true);
  assert.equal(app.localData.browserDownloads[file.id].stage, 'stand-down');
});

test('a user cancellation during the final source hash is not cancelled or erased by VESPER', async () => {
  const file = activeDownload();
  let state = 'active';
  const app = load({ 'browser-submit': started, 'browser-transfer': message =>
    message.operation === 'accept' ? transfer('active') : transfer(state) }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(file.paused, true);

  state = 'complete';
  app.deferDigestAt(3);
  const completing = app.recover();
  await waitFor(() => app.digestIsBlocked());
  file.state = 'interrupted';
  file.paused = false;
  file.canResume = false;
  file.error = 'USER_CANCELED';
  app.finishDigest();
  await completing;

  assert.equal(app.actions.includes('cancel'), false);
  assert.equal(app.actions.includes('erase'), false);
  assert.equal(file.state, 'interrupted');
  assert.equal(file.error, 'USER_CANCELED');
  const record = app.localData.browserDownloads[file.id];
  assert.equal(record.stage, 'stand-down');
  assert.equal(record.transferId, undefined);
  assert.equal(record.sourceHash, undefined);
});

test('legacy recovery leaves a user-cancelled Chrome file alone', async () => {
  const file = { ...activeDownload(), state: 'interrupted', paused: false,
    canResume: true, error: 'USER_CANCELED' };
  const localData = { browserDownloads: {
    [file.id]: { startTime: file.startTime, transferId: 'browser-transfer-user-cancelled', sourceHash: 'e'.repeat(64),
      stage: 'releasing', preserveChrome: true, resumeChrome: true, at: Date.now() }
  } };
  const app = load({ 'browser-transfer': transfer('failed') }, { download: file, localData });

  await app.recover();

  assert.equal(file.state, 'interrupted');
  assert.equal(file.error, 'USER_CANCELED');
  assert.equal(app.actions.includes('resume'), false);
  assert.equal(app.actions.includes('erase'), false);
  assert.equal(app.localData.browserDownloads?.[file.id], undefined);
});

test('automatic queued results without transfer readiness leave Chrome running', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': { ok: true, results: [{ status: 'queued' }] },
    'browser-transfer': transfer('failed') }, { download: file });
  await observe(app, file);
  await app.onCreated(file);

  assert.equal(app.actions.includes('pause'), false);
  assert.equal(file.paused, false);
  assert.equal(app.sent[0].mode, 'download');
  assert.equal(app.sent[1].operation, 'abandon');
});

test('a temporary native error leaves Chrome downloading and never retries late', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': { ok: false, code: 'host-error', message: 'Temporary disconnect.' },
    'browser-transfer': transfer('failed') }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(file.paused, false);
  assert.equal(app.sent.filter(isBrowserSubmission).length, 1);
  assert.equal((await app.local.get('browserDownloads')).browserDownloads[file.id].stage, 'stand-down');

  app.advanceTime(61_000);
  await app.recover();
  assert.equal(app.sent.filter(isBrowserSubmission).length, 1);
  assert.equal(file.state, 'in_progress');
  assert.equal(app.actions.includes('cancel'), false);
});

test('a lost native reply leaves durable cleanup and never takes the Chrome file', async () => {
  const file = activeDownload();
  const timeout = { ok: false, code: 'timeout' };
  const app = load({ 'browser-submit': timeout, 'browser-transfer': timeout }, { download: file });
  await observe(app, file);
  await app.onCreated(file);

  const [submission, cleanup] = app.sent;
  assert.equal(submission.type, 'browser-capture');
  assert.equal(cleanup.operation, 'abandon');
  assert.equal(cleanup.transferId, submission.id);
  assert.equal(app.localData.browserDownloads[file.id].stage, 'releasing');
  assert.deepEqual(app.actions, ['native', 'native']);
  assert.equal(file.paused, false);

  const restarted = load({ 'browser-transfer': transfer('failed') }, {
    download: file, localData: structuredClone(app.localData)
  });
  await restarted.recover();
  assert.ok(restarted.sent.length > 0);
  assert.ok(restarted.sent.every(message => message.operation === 'abandon' && message.transferId === submission.id));
  assert.equal(restarted.localData.browserDownloads[file.id].stage, 'stand-down');
  assert.equal(file.state, 'in_progress');
  assert.equal(file.paused, false);
});

test('same-size responses without a strong ETag stay in Chrome', async () => {
  for (const [index, etag] of [undefined, 'W/"version-1"'].entries()) {
    const file = { ...activeDownload(), id: 20 + index };
    const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file });
    const responseHeaders = [
      { name: 'Content-Type', value: 'application/zip' },
      { name: 'Content-Disposition', value: 'attachment; filename="archive.zip"' },
      { name: 'Content-Length', value: String(file.fileSize) }
    ];
    if (etag) responseHeaders.push({ name: 'ETag', value: etag });
    await app.onHeaders({ url: file.url, requestId: `no-validator-${index}`, method: 'GET', type: 'main_frame', statusCode: 200,
      responseHeaders });
    await app.onCreated(file);

    await waitFor(() => app.sessionData.vesperCaptureDecisions?.some(entry => entry.outcome === 'no-validator'),
      'the missing strong validator should be explained');
    assert.equal(app.sent.some(isBrowserSubmission), false);
    assert.equal(file.paused, false);
    assert.equal(app.sessionData.vesperCaptureDecisions[0].outcome, 'no-validator');
  }
});

test('automatic handoff includes extensionless files and starts them by default', async () => {
  const file = { ...activeDownload(), url: 'https://example.com/export?receipt=42', filename: '' };
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(app.sent[0].items[0].url, file.url);
  assert.equal(app.sent[0].mode, 'download');
  assert.equal(file.paused, true);
  assert.deepEqual(app.actions, ['native', 'pause', 'native', 'native']);
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
  await waitFor(() => app.sent.some(isBrowserSubmission), 'Late response capture was not submitted.');
  assert.equal(app.sent[0].browserProof.length, file.fileSize);
  assert.equal(app.sent[0].browserProof.contentType, undefined);
  await waitFor(() => file.paused, 'Chrome was not paused after the late response was accepted.');
});

test('a redirected API attachment matches when Chrome creates the item with finalUrl already set', async () => {
  const originalUrl = 'https://arena.example/api/chat/chat-1/workspace/workspace-1/download';
  const finalUrl = 'https://arena.example/api/files/file-1/download';
  const file = { ...activeDownload(), url: originalUrl, finalUrl, fileSize: -1, totalBytes: -1 };
  let finishSubmit;
  const pendingSubmit = new Promise(resolve => { finishSubmit = resolve; });
  const app = load({ 'browser-submit': () => pendingSubmit, 'browser-transfer': transfer('failed') },
    { download: file, signInPermission: true, prefs: { useSignIn: true }, localData: { signInHosts: ['arena.example'] } });

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
      { name: 'Content-Disposition', value: 'attachment; filename="export.zip"' },
      { name: 'ETag', value: '"api-v1"' }
    ] });

  await waitFor(() => app.sessionData.vesperDownloadCapture.awaiting.length === 0,
    'the final response should match the pending original URL');
  await waitFor(() => app.sent.some(isBrowserSubmission),
    'the strong ETag should identify the response even before Chrome reports a size');
  const request = app.sent.find(isBrowserSubmission);
  assert.equal(request.browserProof.etag, '"api-v1"');
  assert.equal(file.paused, false);

  file.totalBytes = 1000;
  const changed = app.onChanged({ id: file.id, totalBytes: { current: 1000 } });
  await waitFor(() => app.sent.some(isBrowserSubmission),
    'the strong-ETag response should start native verification');
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
    { download: file, signInPermission: true, prefs: { useSignIn: true }, localData: { signInHosts: ['arena.example'] } });
  await app.onCreated(file);
  app.onRedirect({ requestId: 'arena-late-final', url: originalUrl, redirectUrl: finalUrl, method: 'GET' });
  await waitFor(() => app.sessionData.vesperDownloadCapture.chains?.length === 1);

  file.finalUrl = finalUrl;
  await app.onChanged({ id: file.id, finalUrl: { current: finalUrl } });
  assert.equal(app.sent.some(isBrowserSubmission), false,
    'the final URL alone is not proof of the source representation');
  assert.equal(file.paused, false);

  file.totalBytes = 1000;
  app.onHeaders({ url: finalUrl, requestId: 'arena-late-final', method: 'GET', type: 'other', statusCode: 200,
    responseHeaders: [
      { name: 'Content-Type', value: 'application/zip' },
      { name: 'Content-Disposition', value: 'attachment; filename="export.zip"' },
      { name: 'ETag', value: '"api-v4"' },
      { name: 'Content-Length', value: '1000' }
    ] });
  await waitFor(() => app.sent.some(isBrowserSubmission));
  const request = app.sent.find(isBrowserSubmission);
  assert.equal(request.items[0].cookie, 'session=test-value');
  assert.equal(request.authorized, true);
  assert.equal(file.paused, false, 'Chrome must continue while VESPER checks readiness');

  finishSubmit(started);
  await waitFor(() => file.paused, 'Chrome was not paused after the strong-ETag response was accepted.');
  assert.equal(file.paused, true);
  assert.ok(app.sent.some(message => message.type === 'browser-transfer' && message.operation === 'accept'));
  assert.equal(app.actions.includes('cancel'), false);
});

test('an authenticated API capture does not mirror Chrome activity while native verifies', async () => {
  const url = 'https://arena.example/api/chat/chat-2/workspace/workspace-2/download';
  const file = { ...activeDownload(), url, finalUrl: url };
  let finishSubmit;
  const pendingSubmit = new Promise(resolve => { finishSubmit = resolve; });
  const app = load({ 'browser-submit': () => pendingSubmit, 'browser-transfer': transfer('active') },
    { download: file, activity: true, signInPermission: true, prefs: { useSignIn: true },
      localData: { signInHosts: ['arena.example'] } });
  await observe(app, file, { requestId: 'arena-direct' });

  const created = app.onCreated(file);
  await waitFor(() => app.sent.some(isBrowserSubmission),
    'VESPER may verify the request without listing Chrome downloads');
  assert.equal(app.ports.length, 0);
  assert.equal(file.paused, false);
  assert.equal(app.actions.includes('cancel'), false);
  const request = app.sent.find(isBrowserSubmission);
  assert.equal(request.authorized, true);
  assert.equal(request.items[0].cookie, 'session=test-value');
  assert.deepEqual(app.cookieLookups, [url]);

  finishSubmit(started);
  await created;
  assert.equal(file.paused, true);
  assert.ok(app.sent.some(message => message.type === 'browser-transfer' && message.operation === 'accept'));
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

    assert.equal(app.sent.some(isBrowserSubmission), false, scenario.name);
    assert.equal(app.cookieLookups.length, 0, scenario.name);
    assert.equal(file.paused, false, scenario.name);
  }
});

test('automatic API interception off leaves Chrome as the only download owner', async () => {
  const file = { ...activeDownload(), url: 'https://arena.example/api/chat/chat-3/workspace/workspace-3/download' };
  const app = load({ 'browser-submit': started }, { download: file, activity: true, prefs: { intercept: false, useSignIn: true }, signInPermission: true });
  await observe(app, file, { requestId: 'arena-disabled' });
  await app.onCreated(file);

  assert.equal(app.sent.some(isBrowserSubmission), false);
  assert.equal(app.cookieLookups.length, 0);
  assert.equal(file.paused, false);
  assert.equal(app.actions.includes('cancel'), false);
  assert.equal(app.ports.length, 0);
});

test('Pause leaves new automatic downloads with Chrome and keeps right-click downloads usable', async () => {
  const file = activeDownload();
  const app = load({ submit: { ok: true }, 'browser-capture': started }, { download: file, localData: { vesperPaused: true } });
  const tab = { id: 2, url: 'https://example.com/files/' };
  try {
    await app.install({ reason: 'update' });
    assert.ok(app.contextMenuItems.some(item => item.id === 'vesper-download'));
    assert.ok(app.contextMenuItems.some(item => item.id === 'vesper-smart'));
    await observe(app, file);
    await app.onCreated(file);
    const early = await app.message({ type: 'early-download', url: file.url }, { tab });
    assert.equal(early.fallback, true);
    assert.deepEqual(app.sent, []);
    assert.equal(file.paused, false);

    await app.onContextClicked({ menuItemId: 'vesper-download', linkUrl: file.url, frameId: 0 }, tab);
    assert.equal(app.sent[0].type, 'submit');
    assert.equal(app.sent[0].mode, 'download');
    assert.equal(app.sent[0].items[0].url, file.url);
    await app.local.set({ vesperPaused: false });
    await observe(app, file);
    await app.onChanged({ id: file.id, totalBytes: { current: 1000 } });
    assert.equal(app.sent.some(isBrowserSubmission), false, 'Resuming does not adopt a file Chrome already started.');
  } finally { app.stopWorker(); }
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

  assert.equal(workerAfterRestart.sent[0].type, 'browser-capture');
  assert.equal(file.paused, true);
});

test('an attempted native handoff resumes after worker restart with the same transfer identity', async () => {
  const file = activeDownload();
  let finishFirst, finishSecond;
  const firstReply = new Promise(resolve => { finishFirst = resolve; });
  const secondReply = new Promise(resolve => { finishSecond = resolve; });
  const firstWorker = load({ 'browser-submit': () => firstReply, 'browser-transfer': transfer('failed') }, { download: file });
  await observe(firstWorker, file);

  const firstCapture = firstWorker.onCreated(file);
  await waitFor(() => firstWorker.sent.some(isBrowserSubmission));
  const firstRequest = firstWorker.sent.find(isBrowserSubmission);
  const attempted = (await firstWorker.local.get('browserDownloads')).browserDownloads[file.id];
  assert.equal(attempted.stage, 'attempted');
  assert.equal(file.paused, false, 'Chrome keeps running while native readiness is unknown');

  const restarted = load({ 'browser-submit': () => secondReply, 'browser-transfer': transfer('failed') }, {
    download: file, localData: firstWorker.localData, sessionData: firstWorker.sessionData
  });
  await waitFor(() => restarted.sent.some(isBrowserSubmission));
  const retriedRequest = restarted.sent.find(isBrowserSubmission);

  assert.equal(retriedRequest.id, firstRequest.id,
    'a lost worker reply must retry the idempotent native operation, not create a second transfer');
  assert.equal(retriedRequest.items[0].url, firstRequest.items[0].url);
  assert.equal(file.paused, false);
  assert.equal(restarted.actions.includes('cancel'), false);

  const failed = { ok: false, code: 'host-error', message: 'Temporary disconnect.' };
  finishFirst(failed);
  finishSecond(failed);
  await firstCapture;
  await waitFor(() => restarted.localData.browserDownloads?.[file.id]?.stage === 'stand-down');
  assert.equal(file.paused, false);
  assert.equal(file.state, 'in_progress');
  assert.equal(restarted.actions.includes('cancel'), false);
});

test('three interrupted browser submissions reuse one ID then abandon and stand down', async () => {
  const file = activeDownload();
  const sessionData = {};
  const downloadHistory = [];
  const unresolved = () => new Promise(() => {});
  let worker = load({ 'browser-submit': unresolved }, { download: file, sessionData, downloadHistory });
  await observe(worker, file);

  const firstCapture = worker.onCreated(file);
  await waitFor(() => worker.sent.some(isBrowserSubmission));
  const firstRequest = worker.sent.find(isBrowserSubmission);
  let localData = worker.localData;
  assert.equal(localData.browserDownloads[file.id].attempts, 1);
  worker.stopWorker();

  for (const attempt of [2, 3]) {
    worker = load({ 'browser-submit': unresolved }, { download: file, sessionData, downloadHistory, localData });
    await waitFor(() => worker.sent.some(isBrowserSubmission));
    const request = worker.sent.find(isBrowserSubmission);
    assert.equal(request.id, firstRequest.id);
    localData = worker.localData;
    assert.equal(localData.browserDownloads[file.id].attempts, attempt);
    worker.stopWorker();
  }

  worker = load({ 'browser-submit': () => { throw new Error('The retry limit must prevent another submission.'); },
    'browser-transfer': transfer('failed') }, { download: file, sessionData, downloadHistory, localData });
  await waitFor(() => worker.sent.some(message => message.type === 'browser-transfer' && message.operation === 'abandon'));

  const abandon = worker.sent.find(message => message.type === 'browser-transfer' && message.operation === 'abandon');
  const standDown = worker.localData.browserDownloads[file.id];
  assert.equal(abandon.transferId, firstRequest.id);
  assert.equal(worker.sent.some(isBrowserSubmission), false);
  assert.equal(standDown.stage, 'stand-down');
  assert.equal(standDown.transferId, undefined);
  assert.equal(standDown.sourceHash, undefined);
  assert.ok(!JSON.stringify(worker.localData.browserDownloads).includes(file.url));
  assert.ok(!JSON.stringify(worker.localData.browserDownloads).includes('test-value'));
  assert.equal(file.paused, false);
  assert.equal(file.state, 'in_progress');
  assert.equal(worker.actions.includes('pause'), false);
  assert.equal(worker.actions.includes('cancel'), false);
  void firstCapture;
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
    { download: file, sessionData, downloadHistory, signInPermission: true, prefs: { useSignIn: true },
      localData: { signInHosts: ['arena.example'] } });
  file.finalUrl = finalUrl;
  file.totalBytes = 1000;
  nextWorker.onHeaders({ url: finalUrl, requestId, method: 'GET', type: 'other', statusCode: 200,
    responseHeaders: [
      { name: 'Content-Type', value: 'application/zip' },
      { name: 'Content-Disposition', value: 'attachment; filename="export.zip"' },
      { name: 'ETag', value: '"api-v5"' }
    ] });
  await waitFor(() => nextWorker.sent.some(isBrowserSubmission));

  const request = nextWorker.sent.find(isBrowserSubmission);
  assert.equal(request.items[0].cookie, 'session=test-value');
  assert.equal(request.authorized, true);
  await waitFor(() => file.paused, 'Chrome was not paused after native acceptance.');
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

    assert.equal(nextWorker.sent.some(isBrowserSubmission), false, scenario.kind);
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

  assert.equal(workerAfterRestart.sent[0].type, 'browser-capture');
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

test('preexisting Chrome downloads are never adopted by a worker restart or later property changes', async () => {
  const file = { ...activeDownload(), bytesReceived: 800 };
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file });
  await observe(app, file);
  await app.onChanged({ id: file.id, totalBytes: { current: 1000 } });
  await app.recover();

  assert.deepEqual(app.sent, []);
  assert.equal(file.paused, false);
  assert.equal(file.state, 'in_progress');
  assert.equal(app.actions.includes('cancel'), false);
});

test('Chrome keeps a started download the server cannot resume', async () => {
  const file = { ...activeDownload(), bytesReceived: 1 };
  const app = load({ 'browser-submit': started }, { download: file });
  await observe(app, file, { acceptsRanges: false });
  await app.onCreated(file);

  assert.deepEqual(app.sent, []);
  assert.equal(file.paused, false);
  const decisions = (await app.sessionData.vesperCaptureDecisions) || [];
  assert.equal(decisions[0]?.outcome, 'chrome-started');
});

test('bytes arriving while VESPER verifies keep a download Chrome cannot resume in Chrome', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': () => {
    file.bytesReceived = 400;
    return started;
  }, 'browser-transfer': transfer('failed') }, { download: file });
  await observe(app, file, { acceptsRanges: false });
  await app.onCreated(file);

  assert.equal(file.paused, false);
  assert.equal(file.state, 'in_progress');
  assert.equal(app.actions.includes('pause'), false);
  assert.equal(app.actions.includes('cancel'), false);
  assert.ok(app.sent.some(message => message.type === 'browser-transfer' && message.operation === 'abandon'));
  assert.equal(app.sessionData.vesperCaptureDecisions[0].outcome, 'chrome-started');
});

test('bytes arriving while VESPER verifies do not lose a handoff Chrome could resume', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': () => {
    file.bytesReceived = 400;
    return started;
  }, 'browser-transfer': transfer('active') }, { download: file });
  await observe(app, file);
  await app.onCreated(file);

  assert.equal(file.paused, true);
  assert.equal(app.localData.browserDownloads[file.id].stage, 'parked');
  assert.equal(app.localData.browserDownloads[file.id].parkedBytes, 400);
});

test('a resumable download that already received bytes is parked, and those bytes are discarded on completion', async () => {
  const file = { ...activeDownload(), bytesReceived: 300 };
  let state = 'active';
  const app = load({ 'browser-submit': started, 'browser-transfer': () => transfer(state) }, { download: file });
  await observe(app, file);
  await app.onCreated(file);

  assert.equal(file.paused, true);
  const record = app.localData.browserDownloads[file.id];
  assert.equal(record.stage, 'parked');
  assert.equal(record.chromeResumable, true);
  assert.equal(record.parkedBytes, 300);
  const pending = app.sessionData.vesperCaptureDecisions[0];
  assert.equal(pending.outcome, 'handoff-pending');
  assert.equal(pending.discarded, 300);
  assert.ok(Number.isFinite(pending.parkMs) && pending.parkMs >= 0);
  assert.equal(app.actions.includes('cancel'), false, 'Chrome is cancelled only after VESPER has the whole file');

  state = 'complete';
  await app.recover();
  assert.equal(app.actions.includes('cancel'), true);
  assert.equal(file.error, 'USER_CANCELED');
  const moved = app.sessionData.vesperCaptureDecisions[0];
  assert.equal(moved.outcome, 'moved');
  assert.equal(moved.discarded, 300);
});

test('a parked download that holds bytes goes back to Chrome when VESPER fails', async () => {
  const file = { ...activeDownload(), bytesReceived: 300 };
  let state = 'active';
  const app = load({ 'browser-submit': started, 'browser-transfer': () => transfer(state) }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(file.paused, true);

  state = 'failed';
  await app.recover();
  assert.equal(file.paused, false);
  assert.equal(file.state, 'in_progress');
  assert.equal(file.bytesReceived, 300, 'Chrome continues from the bytes it already had');
  assert.equal(app.actions.includes('cancel'), false);
  assert.ok(!app.sessionData.vesperCaptureDecisions.some(entry => entry.outcome === 'moved'));
});

test('Chrome keeps a download it is already more than halfway through', async () => {
  const file = { ...activeDownload(), bytesReceived: 501 };
  const app = load({ 'browser-submit': started }, { download: file });
  await observe(app, file);
  await app.onCreated(file);

  assert.deepEqual(app.sent, []);
  assert.equal(file.paused, false);
  assert.equal(app.sessionData.vesperCaptureDecisions[0].outcome, 'chrome-started');
});

test('after a worker restart a parked download with bytes stays parked only if its record says Chrome can resume it', async () => {
  const file = { ...activeDownload(), bytesReceived: 300 };
  const first = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file });
  await observe(first, file);
  await first.onCreated(file);
  const parked = first.localData.browserDownloads[file.id];
  first.stopWorker();

  // The new worker has no response evidence left. The record's own fact keeps the handoff valid.
  const resumed = load({ 'browser-transfer': transfer('active') }, { download: file,
    localData: { browserDownloads: { [file.id]: { ...parked, stage: 'parking', parkUntil: Date.now() + 10_000 } } } });
  await resumed.recover();
  assert.equal(file.paused, true);
  assert.equal(resumed.localData.browserDownloads[file.id].stage, 'parked');
  resumed.stopWorker();

  const unsafe = load({ 'browser-transfer': transfer('failed') }, { download: file,
    localData: { browserDownloads: { [file.id]: { ...parked, chromeResumable: false, stage: 'parking', parkUntil: Date.now() + 10_000 } } } });
  await unsafe.recover();
  assert.equal(unsafe.sent.some(message => message.operation === 'accept'), false);
  assert.equal(file.paused, false);
  assert.equal(unsafe.actions.includes('cancel'), false);
});

// ---- Last-Modified as the strong validator (RFC 9110 8.8.2.2), for helpers that accept it

const datedHeaders = (file, extra = {}) => Object.entries({ 'Content-Length': String(file.fileSize), 'Accept-Ranges': 'bytes',
  'Content-Type': 'application/zip', 'Content-Disposition': 'attachment; filename="archive.zip"',
  'Last-Modified': 'Tue, 03 Nov 2026 10:00:00 GMT', Date: 'Thu, 12 Nov 2026 01:00:00 GMT', ...extra })
  .filter(([, value]) => value !== undefined).map(([name, value]) => ({ name, value }));
const observeDated = (app, file, extra) => app.onHeaders({ url: file.url, requestId: 'dated-request', method: 'GET',
  type: 'main_frame', statusCode: 200, responseHeaders: datedHeaders(file, extra) });
const datedProofs = ['etag', 'last-modified'];

test('a strong Last-Modified with an exact length proves the file to a helper that accepts it', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') },
    { download: file, helperProofs: datedProofs });
  await observeDated(app, file);
  await app.onCreated(file);

  assert.deepEqual(app.localData.vesperCaptureProofs, datedProofs, 'the helper was asked once and its answer kept');
  assert.deepEqual(structuredClone(app.sent[0].browserProof), { length: 1000,
    lastModified: 'Tue, 03 Nov 2026 10:00:00 GMT', contentType: 'application/zip', basis: 'last-modified' });
  assert.equal(app.localData.browserDownloads[file.id].stage, 'parked');
  assert.equal(app.localData.browserDownloads[file.id].chromeResumable, true);
});

test('an older helper that knows only ETags leaves a Last-Modified download in Chrome', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started }, { download: file });
  await observeDated(app, file);
  await app.onCreated(file);

  assert.deepEqual(app.sent, []);
  assert.equal(file.paused, false);
  assert.equal(app.sessionData.vesperCaptureDecisions[0].outcome, 'no-validator');
  assert.deepEqual(app.localData.vesperCaptureProofs, ['etag'], 'an older helper is not asked again for every download');
});

test('a helper that once accepted Last-Modified proofs loses that trust when an older helper answers', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started }, { download: file, localData: { vesperCaptureProofs: datedProofs } });
  await observeDated(app, file);
  await app.onCreated(file);

  assert.deepEqual(app.sent, []);
  assert.deepEqual(app.localData.vesperCaptureProofs, ['etag']);
});

test('a weak ETag, a date too close to the response, or a missing length never stands in for a strong validator', async () => {
  for (const extra of [{ ETag: 'W/"weak"' }, { Date: 'Tue, 03 Nov 2026 10:00:00 GMT' }, { Date: 'Tue, 03 Nov 2026 10:00:01 GMT' }, { Date: undefined },
    { 'Last-Modified': '2026-11-03T10:00:00Z' }, { 'Content-Length': undefined }]) {
    const file = activeDownload();
    const app = load({ 'browser-submit': started }, { download: file, helperProofs: datedProofs });
    await observeDated(app, file, extra);
    await app.onCreated(file);
    assert.deepEqual(app.sent, [], JSON.stringify(extra));
    assert.equal(app.sessionData.vesperCaptureDecisions[0].outcome, 'no-validator', JSON.stringify(extra));
  }
});

test('the proofs a helper accepts are remembered, and withdrawn when an older helper answers', async () => {
  const app = load();
  await app.evaluate(`rememberCaptureProofs({ ok: true, data: { proofs: ['etag', 'last-modified'] } })`);
  assert.deepEqual(app.localData.vesperCaptureProofs, ['etag', 'last-modified']);
  await app.evaluate(`rememberCaptureProofs({ ok: false, data: { proofs: ['etag'] } })`);
  assert.deepEqual(app.localData.vesperCaptureProofs, ['etag', 'last-modified'], 'a failed reply says nothing');
  await app.evaluate(`rememberCaptureProofs({ ok: true, data: { proofs: ['etag'] } })`);
  assert.deepEqual(app.localData.vesperCaptureProofs, ['etag']);
});

// ---- experience: a site that refuses VESPER's own request is not asked again for a while

test('a fresh verified attachment is attempted even after earlier site refusals', async () => {
  const refused = { ok: true, results: [{ status: 'started', transferReady: false, transferReason: 'browser-fallback' }] };
  const file = activeDownload();
  const app = load({ 'browser-submit': refused, 'browser-transfer': transfer('failed') }, { download: file });
  const click = async number => {
    Object.assign(file, { id: 20 + number, startTime: `2026-11-12T01:0${number}:00.000Z`, bytesReceived: 0, paused: false });
    await observe(app, file, { requestId: `click-${number}` });
    await app.onCreated(file);
  };

  await click(1);
  await click(2);
  assert.equal(app.sent.filter(isBrowserSubmission).length, 2);

  await click(3);
  assert.equal(app.sent.filter(isBrowserSubmission).length, 3, 'a new verified attachment gets a fresh attempt');
  assert.notEqual(app.sessionData.vesperCaptureDecisions[0].outcome, 'site-declines');
  assert.equal(file.paused, false);
  assert.doesNotMatch(JSON.stringify(app.localData.vesperExperience), /example\.com|archive/);

  app.advanceTime(4 * 24 * 60 * 60_000);
  await click(4);
  assert.equal(app.sent.filter(isBrowserSubmission).length, 4);
});

test('a signed address too long to name a site is still handed off, and still refused twice without an error', async () => {
  const longUrl = 'https://example.com/files/archive.zip?signature=' + 'a'.repeat(5000);
  const file = { ...activeDownload(), url: longUrl };
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(app.localData.browserDownloads[file.id].stage, 'parked');

  const refused = { ok: true, results: [{ status: 'started', transferReady: false, transferReason: 'browser-fallback' }] };
  const other = { ...activeDownload(), url: longUrl };
  const declined = load({ 'browser-submit': refused, 'browser-transfer': transfer('failed') }, { download: other });
  for (const number of [1, 2, 3]) {
    Object.assign(other, { id: 40 + number, startTime: `2026-11-12T03:0${number}:00.000Z`, bytesReceived: 0, paused: false });
    await observe(declined, other, { requestId: `long-${number}` });
    await declined.onCreated(other);
  }
  assert.equal(declined.sent.filter(isBrowserSubmission).length, 3, 'nothing is learned about a site that cannot be named');
  assert.ok(!declined.sessionData.vesperCaptureDecisions.some(entry => entry.outcome === 'capture-error'));
});

test('an extension update forgets what the previous version learned about sites', async () => {
  const app = load();
  const promising = () => app.evaluate(`VesperExperience.promising('files.example', 'takeover')`);
  for (let count = 0; count < 2; count++)
    await app.evaluate(`VesperExperience.note('files.example', 'takeover', 'refused')`);
  assert.equal(await promising(), false);

  await app.install({ reason: 'chrome_update' });
  assert.equal(await promising(), false, 'a browser update changes nothing VESPER learned');

  await app.install({ reason: 'update' });
  assert.equal(await promising(), true);
  assert.equal(app.localData.vesperExperience, undefined);
});

test('a timeout or a busy helper teaches nothing about the site', async () => {
  const slow = { ok: true, results: [{ status: 'started', transferReady: false, transferReason: 'slow' }] };
  const file = activeDownload();
  const app = load({ 'browser-submit': slow, 'browser-transfer': transfer('failed') }, { download: file });
  for (const number of [1, 2, 3]) {
    Object.assign(file, { id: 30 + number, startTime: `2026-11-12T02:0${number}:00.000Z`, bytesReceived: 0, paused: false });
    await observe(app, file, { requestId: `slow-${number}` });
    await app.onCreated(file);
  }
  assert.equal(app.sent.filter(isBrowserSubmission).length, 3);
  assert.equal(app.localData.vesperExperience, undefined);
});

test('Chrome completion during native acceptance never becomes a VESPER completion', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started, 'browser-transfer': message => {
    if (message.operation === 'accept') file.state = 'complete';
    return transfer(message.operation === 'abandon' ? 'failed' : 'active');
  } }, { download: file });
  await observe(app, file);
  await app.onCreated(file);

  assert.equal(file.state, 'complete');
  assert.equal(app.actions.includes('cancel'), false);
  assert.equal(app.actions.includes('erase'), false);
  assert.ok(app.sent.some(message => message.operation === 'abandon'));
  assert.ok(!app.sessionData.vesperCaptureDecisions?.some(entry => entry.outcome === 'moved'));
  assert.notEqual((await app.local.get('browserDownloads')).browserDownloads?.[file.id]?.stage, 'parked');
});

test('bytes arriving during native acceptance return a download Chrome cannot resume to Chrome', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started, 'browser-transfer': message => {
    if (message.operation === 'accept') file.bytesReceived = 4;
    return transfer(message.operation === 'abandon' ? 'failed' : 'active');
  } }, { download: file });
  await observe(app, file, { acceptsRanges: false });
  await app.onCreated(file);

  assert.equal(file.state, 'in_progress');
  assert.equal(file.paused, false, 'Chrome resumes after the native transfer is stopped');
  assert.equal(app.actions.includes('cancel'), false);
  assert.ok(!app.sessionData.vesperCaptureDecisions?.some(entry => entry.outcome === 'moved'));
});

test('a delayed Chrome safety scan cannot initiate a late capture', async () => {
  const file = { ...activeDownload(), danger: 'asyncScanning' };
  const app = load({ 'browser-submit': started }, { download: file });
  await app.onCreated(file);
  await observe(app, file);
  app.advanceTime(16_000);
  file.danger = 'safe';
  await app.onChanged({ id: file.id, danger: { previous: 'asyncScanning', current: 'safe' } });
  await app.recover();

  assert.deepEqual(app.sent, []);
  assert.equal(file.paused, false);
  assert.equal(file.state, 'in_progress');
});

test('POST attachments stay in Chrome and a completed one is never claimed by VESPER', async () => {
  const file = activeDownload();
  const app = load({}, { download: file });
  await observe(app, file, { method: 'POST' });
  await app.onCreated(file);
  assert.equal((await app.message({ type: 'move-download', downloadId: file.id })).ok, false);
  assert.equal(app.sent.length, 0);
  file.state = 'complete';
  await app.onChanged({ id: file.id, state: { current: 'complete' } });
  await app.onChanged({ id: file.id, danger: { current: 'deepScannedSafe' } });
  // Chrome's file stays Chrome's: nothing is sent to VESPER and nothing is added to its list.
  assert.equal(app.sent.length, 0);
  assert.deepEqual((await app.local.get('browserDownloads')).browserDownloads ?? {}, {});
});

test('a completed blob stays in Chrome and is never sent to VESPER', async () => {
  const file = { ...activeDownload(), state: 'complete', url: 'blob:https://example.com/temporary-uuid' };
  const app = load({}, { download: file });
  await app.onChanged({ id: file.id, state: { current: 'complete' } });
  assert.equal(app.sent.length, 0);
  assert.ok(!JSON.stringify(await app.local.get('browserDownloads')).includes('temporary-uuid'));
  for (const options of [{ download: { ...file, incognito: true } }, { download: file, prefs: { intercept: false } }]) {
    const optedOut = load({}, options);
    await optedOut.onCreated(options.download);
    assert.equal(optedOut.sent.length, 0);
  }
});

test('Chrome can complete while VESPER is staging the handoff: VESPER stands down, Chrome keeps the file', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': () => { file.state = 'complete'; return started; },
    'browser-transfer': transfer('failed') }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  await app.onChanged({ id: file.id, state: { current: 'complete' } });
  assert.equal(app.actions.includes('pause'), false);
  assert.equal(app.actions.includes('cancel'), false);
  // VESPER abandoned its own transfer and claimed nothing.
  assert.equal(app.sent.at(-1).type, 'browser-transfer');
  assert.equal(app.sent.at(-1).operation, 'abandon');
  assert.ok(!app.sent.some(message => message.type === 'browser-completed'));
  assert.deepEqual((await app.local.get('browserDownloads')).browserDownloads ?? {}, {});
});

test('Chrome completion after acceptance aborts VESPER and preserves Chrome\'s finished file', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started, 'browser-transfer': message =>
    transfer(message.operation === 'abandon' ? 'failed' : 'active') }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(file.paused, true);
  assert.equal((await app.local.get('browserDownloads')).browserDownloads[file.id].stage, 'parked');

  file.state = 'complete';
  file.paused = false;
  await app.onChanged({ id: file.id, state: { current: 'complete' } });

  assert.equal(file.state, 'complete');
  assert.equal(app.actions.includes('cancel'), false);
  assert.equal(app.actions.includes('erase'), false);
  assert.ok(app.sent.some(message => message.type === 'browser-transfer' && message.operation === 'abandon'));
  assert.deepEqual((await app.local.get('browserDownloads')).browserDownloads ?? {}, {});
  assert.equal(app.sessionData.vesperCaptureDecisions?.[0]?.outcome, 'chrome-finished');
});

test('Chrome completion keeps a durable cleanup record while the native helper is unreachable', async () => {
  const file = activeDownload();
  let nativeAvailable = false;
  const app = load({ 'browser-submit': started, 'browser-transfer': message =>
    message.operation === 'abandon'
      ? nativeAvailable ? transfer('failed') : { ok: false, code: 'host-error' }
      : transfer('active') }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  file.state = 'complete';
  file.paused = false;
  await app.onChanged({ id: file.id, state: { current: 'complete' } });

  const records = (await app.local.get('browserDownloads')).browserDownloads;
  assert.equal(records[file.id].stage, 'releasing', 'The native candidate still requires cleanup.');
  assert.equal(app.actions.includes('cancel'), false);
  assert.equal(app.actions.includes('erase'), false);
  // Simulate MV3 service-worker teardown while cleanup is awaiting the native helper.
  const restarted = load({ 'browser-transfer': transfer('failed') },
    { download: file, localData: { browserDownloads: records }, sessionData: app.sessionData });
  await restarted.recover();
  assert.deepEqual((await restarted.local.get('browserDownloads')).browserDownloads ?? {}, {});
  assert.equal(restarted.sessionData.vesperCaptureDecisions?.[0]?.outcome, 'chrome-finished');
  assert.equal(restarted.actions.includes('cancel'), false);
  assert.equal(file.state, 'complete');
});

test('completed Chrome files without a VESPER candidate are never sent to the native helper', async () => {
  const file = { ...activeDownload(), state: 'complete' };
  const app = load({}, { download: file });
  await app.onChanged({ id: file.id, state: { current: 'complete' } });
  assert.deepEqual(app.sent, []);
  assert.equal(app.actions.includes('cancel'), false);
  assert.deepEqual((await app.local.get('browserDownloads')).browserDownloads ?? {}, {});
});

test('an unknown abandon state keeps a Chrome download paused by an automatic capture', async () => {
  const file = activeDownload();
  let state = 'active';
  const app = load({ 'browser-submit': started, 'browser-transfer': () => transfer(state) }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(file.paused, true);
  state = 'stopping';
  await app.recover();

  assert.equal(file.paused, true);
  assert.equal(app.actions.includes('resume'), false);
  assert.equal((await app.local.get('browserDownloads')).browserDownloads[file.id].stage, 'parked');
});

test('an unavailable status check keeps the paused Chrome item attached to its native transfer', async () => {
  const file = activeDownload();
  let bridgeDown = false;
  const app = load({ 'browser-submit': started, 'browser-transfer': message => {
    if (bridgeDown && message.operation === 'status')
      return { ok: false, code: 'app-unavailable', message: 'VESPER is restarting.' };
    return transfer('active');
  } }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(file.paused, true);

  bridgeDown = true;
  await app.recover();

  assert.equal(file.paused, true);
  assert.equal(app.actions.includes('resume'), false);
  assert.equal(app.sent.some(message => message.type === 'browser-transfer' && message.operation === 'abandon'), false);
  assert.equal((await app.local.get('browserDownloads')).browserDownloads[file.id].stage, 'parked');
});

test('Chrome resumes when an automatic acceptance reports a failed VESPER transfer', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started, 'browser-transfer': () => transfer('failed') },
    { download: file });
  await observe(app, file);
  await app.onCreated(file);

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
  const deadline = Date.now() + 2000;
  while (!app.actions.includes('cancel') && Date.now() < deadline)
    await new Promise(resolve => setTimeout(resolve, 5));

  assert.equal(watches, 2);
  assert.equal(app.actions.includes('cancel'), true);
});

test('recovery resumes Chrome after an accepted capture fails, but never takes an already-paused file', async () => {
  const file = activeDownload();
  let state = 'active';
  const app = load({ 'browser-submit': started, 'browser-transfer': () => transfer(state) }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(file.paused, true);
  state = 'failed';
  await app.recover();
  assert.equal(app.actions.includes('resume'), true);
  assert.equal(app.actions.includes('cancel'), false);
  assert.equal(file.paused, false);

  const paused = { ...activeDownload(), paused: true };
  const other = load({ 'browser-submit': started }, { download: paused });
  await observe(other, paused);
  await other.onCreated(paused);
  assert.equal(other.sent.length, 0);
  assert.equal(paused.paused, true);
});

test('after VESPER completes, only its confirmed Chrome cancellation is erased', async () => {
  const file = activeDownload();
  let state = 'active';
  const app = load({ 'browser-submit': started, 'browser-transfer': () => transfer(state) }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  await app.recover();
  assert.equal(app.sessionData.vesperCaptureDecisions?.[0]?.outcome, 'handoff-pending');
  assert.equal(app.actions.includes('cancel'), false);
  state = 'complete';
  await app.recover();
  assert.equal(app.actions.includes('cancel'), true);
  assert.equal(app.sessionData.vesperCaptureDecisions?.[0]?.outcome, 'moved');
  assert.equal(app.actions.includes('erase'), true);
  assert.equal(file.state, 'interrupted');
  assert.equal(file.error, 'USER_CANCELED');
  const record = app.localData.browserDownloads[file.id];
  assert.equal(record.stage, 'stand-down');
  assert.equal(record.transferId, undefined);
  assert.equal(record.sourceHash, undefined);
});

test('a failed Chrome cancel never erases a download that still belongs to Chrome', async () => {
  const file = activeDownload();
  let state = 'active';
  const app = load({ 'browser-submit': started, 'browser-transfer': message =>
    transfer(message.operation === 'abandon' ? 'complete' : state) },
    { download: file, failDownloadCancel: true });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(file.paused, true);

  state = 'complete';
  await app.recover();
  assert.equal(file.state, 'in_progress', 'Chrome never confirmed that its own download was canceled.');
  assert.equal(file.paused, false, 'Chrome must not remain indefinitely paused after cancel failure.');
  assert.ok(app.actions.includes('cancel'));
  assert.equal(app.actions.includes('erase'), false);
  assert.equal((await app.local.get('browserDownloads')).browserDownloads[file.id].stage, 'stand-down');
});

test('a changed redirect during native acceptance remains Chrome-owned', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started, 'browser-transfer': message => {
    if (message.operation === 'accept') file.finalUrl = 'https://example.com/changed?signature=secret-2';
    return transfer(message.operation === 'abandon' ? 'failed' : 'active');
  } }, { download: file });
  await observe(app, file);
  await app.onCreated(file);

  assert.equal(file.paused, false, 'Chrome must be resumed after native abandonment.');
  assert.equal(file.state, 'in_progress');
  assert.equal(app.actions.includes('cancel'), false);
  assert.equal(app.actions.includes('erase'), false);
  assert.ok(app.sent.some(message => message.operation === 'abandon'));
  const records = (await app.local.get('browserDownloads')).browserDownloads;
  assert.notEqual(records[file.id]?.stage, 'parked');
  assert.equal(app.sessionData.vesperCaptureDecisions?.[0]?.outcome, 'not-ready');
});

test('changed source after worker restart is never completed on Chrome\'s behalf', async () => {
  const file = activeDownload();
  let state = 'active';
  const app = load({ 'browser-submit': started, 'browser-transfer': message =>
    transfer(message.operation === 'abandon' ? 'complete' : state) }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(file.paused, true);

  const records = (await app.local.get('browserDownloads')).browserDownloads;
  assert.match(records[file.id].sourceHash, /^[a-f0-9]{64}$/);
  assert.equal(Object.hasOwn(records[file.id], 'sourceUrl'), false,
    'Signed source URLs must not persist in local storage.');
  file.finalUrl = 'https://other.example/changed?token=secret-3';
  state = 'complete';
  const restarted = load({ 'browser-transfer': message =>
    transfer(message.operation === 'abandon' ? 'complete' : state) },
    { download: file, localData: { browserDownloads: records } });
  await restarted.recover();

  assert.equal(file.paused, false, 'Chrome keeps downloading when the native source has changed.');
  assert.equal(file.state, 'in_progress');
  assert.equal(restarted.actions.includes('cancel'), false);
  assert.equal(restarted.actions.includes('erase'), false);
  assert.equal((await restarted.local.get('browserDownloads')).browserDownloads[file.id].stage, 'stand-down');
});

test('legacy saved captures with no URL fingerprint fail closed and resume Chrome', async () => {
  const file = { ...activeDownload(), paused: true };
  const old = { startTime: file.startTime, transferId: 'legacy-transfer-identity', stage: 'parked',
    resumeChrome: true, at: Date.now() };
  const app = load({ 'browser-transfer': message =>
    transfer(message.operation === 'abandon' ? 'failed' : 'active') }, {
    download: file, localData: { browserDownloads: { [file.id]: old } }
  });
  await app.recover();
  assert.equal(file.paused, false);
  assert.equal(app.actions.includes('cancel'), false);
  assert.equal(app.actions.includes('erase'), false);
  assert.equal((await app.local.get('browserDownloads')).browserDownloads[file.id].stage, 'stand-down');
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

test('native collection picker uses the supplied same-frame URL and rejects another origin', async () => {
  const app = load({ 'media-picker': { ok: true } });
  const home = 'https://www.youtube.com/';
  const page = home + 'watch?v=abcdefghijk&list=PLexample';
  const sender = { url: home, tab: { id: 5, url: home }, frameId: 0, documentId: 'collection' };
  assert.equal((await app.message({ type: 'native-picker', pageUrl: page, title: 'Playlist' }, sender)).ok, true);
  assert.equal(app.sent.at(-1).type, 'media-picker');
  assert.equal(app.sent.at(-1).url, page);
  const count = app.sent.length;
  for (const url of ['https://other.example/playlist', 'javascript:alert(1)', 'https://user@www.youtube.com/playlist'])
    assert.equal((await app.message({ type: 'native-picker', pageUrl: url }, sender)).code, 'invalid');
  assert.equal(app.sent.length, count);
});

test('format cache does not reuse collection metadata for another playlist containing the same video', async () => {
  const app = load({ 'media-options': message => ({ ok: true, data: {
    choices: [{ index: 0, label: 'Best quality' }], collectionUrl: message.url, hasRelatedCollection: true
  } }) });
  const first = 'https://www.youtube.com/watch?v=abcdefghijk&list=PLfirst';
  const second = 'https://www.youtube.com/watch?v=abcdefghijk&list=PLsecond';
  const sender = { url: first, tab: { id: 5, url: first }, frameId: 0, documentId: 'collection' };
  const options = pageUrl => app.message({ type: 'media-options', pageUrl, playerPageUrl: pageUrl, generation: 1 }, sender);
  assert.equal((await options(first)).data.collectionUrl, first);
  assert.equal((await options(second)).data.collectionUrl, second);
  assert.equal((await options(second)).data.collectionUrl, second);
  assert.equal(app.sent.filter(message => message.type === 'media-options').length, 2);
});

test('a blob player only receives its declared manifest from its current frame and page', async () => {
  const app = load({ 'media-options': { ok: true } });
  const previous = 'https://example.com/first';
  const current = 'https://example.com/second';
  const sender = { url: previous, tab: { id: 5, url: previous }, documentId: 'main-document' };
  const changedAt = Date.now();
  const observe = (url, documentId = 'main-document') => app.onMedia({
    url, documentId, frameId: 0, tabId: 5, method: 'GET', statusCode: 200, type: 'xmlhttprequest', timeStamp: changedAt
  });
  await Promise.all([observe('https://cdn.example/first.m3u8'), observe('https://cdn.example/second.m3u8'),
    observe('https://cdn.example/iframe.m3u8', 'iframe-document')]);
  await app.message({ type: 'media-options', pageUrl: current, playerPageUrl: current,
    playerUrl: 'blob:https://example.com/video', manifestUrl: 'https://cdn.example/second.m3u8',
    generation: 1, mediaChangedAt: changedAt }, sender);
  assert.deepEqual([...app.sent[0].candidates.map(c => c.url)], ['https://cdn.example/second.m3u8']);
  await app.message({ type: 'media-options', pageUrl: current, playerPageUrl: current,
    playerUrl: 'blob:https://example.com/video', manifestUrl: 'https://cdn.example/iframe.m3u8',
    generation: 1, mediaChangedAt: changedAt }, sender);
  assert.equal(app.sent[1].candidates.length, 0);
});

// A verified exact-source reply from VESPER: a five-minute media session with real choices.
const checkedSource = (seconds = 61, detail = '720p MP4') => ({ ok: true, data: { mediaSession: 'a'.repeat(32),
  duration: seconds, choices: [{ index: 0, label: 'Best quality', detail }] } });
const onlyExactChecks = app => app.sent.every(message => message.type === 'media-source-options' && message.candidates.length === 1);

test('a blob player confirms even a single inferred manifest; VESPER checks only that exact source', async () => {
  const app = load({ 'media-options': { ok: true }, 'media-source-options': checkedSource() });
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
  assert.match(options.data.sourceChoices[0].label, /^1:01 · 720p · HLS · 1 format$/);
  assert.equal(app.sent.length, 1);
  assert.ok(onlyExactChecks(app), 'only the one-source check may receive an inferred candidate');
  assert.deepEqual([...app.sent[0].candidates.map(candidate => candidate.url)],
    ['https://cdn.example.com/video/playlist.m3u8']);
  const selected = await app.message({ type: 'media-source-select', sourceChoice: options.data.sourceChoices[0].token,
    pageUrl: page, generation: 3, playerUrl: message.playerUrl }, sender);
  assert.equal(selected.data.choices[0].label, 'Best quality');
  assert.equal(app.sent.length, 1, 'the verified reply is reused when the user confirms');
});

test('the muted-loop hint only travels with the selected direct video', async () => {
  const app = load({ 'media-options': { ok: true } });
  const page = 'https://example.com/demo';
  const sender = { url: page, tab: { id: 18 }, frameId: 0, documentId: 'demo-player' };
  const url = 'https://cdn.example.com/demo.mp4';
  await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: url, mutedLoop: true }, sender);
  assert.equal(app.sent[0].candidates[0].url, url);
  assert.equal(app.sent[0].candidates[0].mutedLoop, true);

  await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'https://cdn.example.com/master.m3u8', mutedLoop: true }, sender);
  assert.equal(app.sent[1].candidates[0].mutedLoop, undefined);

  await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: url, mutedLoop: true }, { ...sender, frameId: 1 });
  assert.equal(app.sent[2].candidates[0].mutedLoop, undefined);
});

test('an Instagram blob player can choose a complete browser-observed video response', async () => {
  const app = load({ 'media-options': request => request.candidates.length
    ? { ok: true } : { ok: false, code: 'no-media' }, 'media-source-options': checkedSource(15) });
  const page = 'https://www.instagram.com/reel/abc123/';
  const sender = { url: page, tab: { id: 19 }, frameId: 0, documentId: 'instagram-player' };
  const changedAt = Date.now();
  const mediaUrl = 'https://scontent.cdninstagram.com/v/t50.2886-16/video.mp4?signature=short-lived';
  await app.onMedia({ url: mediaUrl, tabId: 19, frameId: 0, documentId: sender.documentId,
    method: 'GET', statusCode: 200, type: 'xmlhttprequest', timeStamp: changedAt + 100,
    responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });

  const message = { type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'blob:https://www.instagram.com/player', generation: 4, playerPlaying: true, mediaChangedAt: changedAt };
  const options = await app.message(message, sender);
  assert.equal(options.ok, true);
  assert.equal(options.data.sourceChoices.length, 1);
  assert.equal(JSON.stringify(options.data.sourceChoices).includes('signature='), false);
  assert.equal(options.data.sourceChoices[0].label, '0:15 · 720p · MP4 · 1 format');
  assert.match(options.data.sourceChoices[0].detail, /MP4/);
  assert.equal(app.sent.length, 2);
  assert.equal(app.sent[0].candidates.length, 0);
  assert.equal(app.sent[1].type, 'media-source-options');

  // Another response for the same URL should not invalidate the menu already on screen.
  await app.onMedia({ url: mediaUrl, tabId: 19, frameId: 0, documentId: sender.documentId,
    method: 'GET', statusCode: 200, type: 'xmlhttprequest', timeStamp: changedAt + 200,
    responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
  const selected = await app.message({ type: 'media-source-select', sourceChoice: options.data.sourceChoices[0].token,
    pageUrl: page, generation: 4, playerUrl: message.playerUrl }, sender);
  assert.equal(app.sent[1].candidates[0].url, mediaUrl);
  assert.equal(selected.ok, true);
  assert.equal(app.sent.length, 2);
});

// ---- experience: which of two capable routes is asked first for a single-video page

function singleVideoPlayer(app, page) {
  const sender = { url: page, tab: { id: 21 }, frameId: 0, documentId: 'video-document' };
  return async generation => {
    const changedAt = Date.now();
    await app.onMedia({ url: 'https://cdn.example.com/video.mp4', tabId: 21, frameId: 0, documentId: sender.documentId,
      method: 'GET', statusCode: 200, type: 'media', timeStamp: changedAt,
      responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
    const before = app.sent.length;
    const reply = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page, playerUrl: 'blob:player',
      generation, playerPlaying: true, mediaChangedAt: changedAt }, sender);
    return { reply, asked: app.sent.slice(before).map(message => message.type) };
  };
}

test('slow page refusal overlaps exact-source verification but retains the page policy verdict', async () => {
  for (const code of ['no-media', 'protected']) {
    let finishPage, observedStarted;
    const started = new Promise(resolve => { observedStarted = resolve; });
    const app = load({
      'media-options': () => new Promise(resolve => { finishPage = resolve; }),
      'media-source-options': () => { observedStarted(); return checkedSource(15); }
    });
    try {
      const play = singleVideoPlayer(app, 'https://www.tiktok.com/@example/video/1234567');
      const result = play(4);
      let returned = false;
      void result.then(() => { returned = true; });
      await Promise.race([started, new Promise((_,reject) => {
        const timer = setTimeout(() => reject(new Error('Traffic verification was not started while the page waited')), 1500);
        void started.then(() => clearTimeout(timer));
      })]);
      assert.equal(returned, false);
      assert.equal(app.sent[0].type, 'media-options');
      assert.equal(app.sent[1].type, 'media-source-options');
      finishPage({ok:false,code});
      const { reply } = await result;
      if (code === 'protected') assert.equal(reply.code, 'protected');
      else assert.ok(reply.ok && reply.data.sourceChoices.length === 1);
    } finally { app.stopWorker(); }
  }
});

test('a page lookup that keeps being refused stops delaying what the player traffic can answer', async () => {
  const verified = { ok: true, data: { choices: [{ index: 0, label: '720p', container: 'MP4' }], durationSeconds: 30 } };
  const app = load({ 'media-options': { ok: false, code: 'no-media', message: 'Nothing found.' }, 'media-source-options': verified });
  const play = singleVideoPlayer(app, 'https://www.tiktok.com/@example/video/1234567');

  for (const generation of [4, 5]) {
    const { asked } = await play(generation);
    assert.equal(asked[0], 'media-options', 'the page lookup is asked first while it has not proven unhelpful');
    assert.ok(asked.includes('media-source-options'));
  }
  const third = await play(6);
  assert.equal(third.asked.includes('media-options'), false, 'two refusals: the page lookup no longer comes first');
  assert.ok(third.asked.includes('media-source-options'));
  assert.equal(third.reply.ok, true);
  assert.doesNotMatch(JSON.stringify(app.localData.vesperExperience), /tiktok|example/);
});

test('the refused page lookup is still asked when the player traffic gives nothing, and success restores its place', async () => {
  const resolved = { ok: true, data: { choices: [{ index: 0, label: 'Best quality' }] } };
  const app = load({ 'media-options': resolved, 'media-source-options': { ok: false, code: 'no-media', message: 'Not media.' } });
  for (let count = 0; count < 2; count++)
    await app.evaluate(`VesperExperience.note('www.tiktok.com', 'page-lookup', 'refused')`);
  const play = singleVideoPlayer(app, 'https://www.tiktok.com/@example/video/1234567');

  const first = await play(4);
  assert.equal(first.asked[0], 'media-source-options');
  assert.equal(first.asked.at(-1), 'media-options');
  assert.equal(first.reply.data.choices[0].label, 'Best quality');
  assert.equal(await app.evaluate(`VesperExperience.promising('www.tiktok.com', 'page-lookup')`), true);

  const second = await play(5);
  assert.deepEqual(second.asked, ['media-options']);
});

test('learned traffic-first route cannot bypass a protected or blocked source', async () => {
  for (const code of ['protected','blocked','rate-limited','desktop-version-mismatch']) {
    const app = load({'media-options': {ok:true,data:{choices:[{index:0,label:'Best'}]}},
      'media-source-options':{ok:false,code}});
    try {
      for(let i=0;i<2;i++) await app.evaluate(`VesperExperience.note('www.tiktok.com','page-lookup','refused')`);
      const {reply,asked}=await singleVideoPlayer(app,'https://www.tiktok.com/@example/video/1234567')(4);
      assert.equal(reply.code,code);
      assert.ok(!asked.includes('media-options'));
    } finally {app.stopWorker();}
  }
});

test('TikTok and reels show resolved formats before offering recently observed streams', async () => {
  for (const page of ['https://www.tiktok.com/@example/video/1234567', 'https://www.instagram.com/reel/abc123/']) {
    const reply = { ok: true, data: { choices: [{ index: 0, label: 'Best quality' }] } };
    const app = load({ 'media-options': reply });
    const sender = { url: page, tab: { id: 21 }, frameId: 0, documentId: 'video-document' };
    const changedAt = Date.now();
    for (const name of ['preview.mp4', 'other.mp4'])
      await app.onMedia({ url: `https://cdn.example.com/${name}`, tabId: 21, frameId: 0, documentId: sender.documentId,
        method: 'GET', statusCode: 200, type: 'media', timeStamp: changedAt,
        responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });

    const options = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
      playerUrl: 'blob:player', generation: 4, playerPlaying: true, mediaChangedAt: changedAt }, sender);
    assert.equal(options.data.choices[0].label, 'Best quality');
    assert.equal(options.data.sourceChoices, undefined);
    assert.equal(app.sent.length, 1);
    assert.equal(app.sent[0].url, page);
    assert.equal(app.sent[0].candidates.length, 0);
  }
});

test('a paused or ended blob player can use its recent manifest after a late click', async () => {
  const app = load({ 'media-options': { ok: true }, 'media-source-options': checkedSource() });
  const page = 'https://video.example/watch/demo';
  const sender = { url: page, tab: { id: 29 }, frameId: 0, documentId: 'late-click-doc' };
  const changedAt = Date.now();
  await app.onMedia({ url: 'https://cdn.video.example/live/master.m3u8', tabId: 29,
    frameId: 0, documentId: sender.documentId, method: 'GET', statusCode: 200, type: 'xmlhttprequest',
    timeStamp: changedAt + 200, responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }] });
  app.advanceTime(60_000);

  const result = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'blob:https://video.example/player', generation: 4, playerPlaying: false, playerHasMedia: true,
    mediaChangedAt: changedAt }, sender);
  assert.equal(result.ok, true);
  assert.equal(result.data?.sourceChoices?.length, 1);
  assert.equal(result.data.sourceChoices[0].label, '1:01 · 720p · HLS · 1 format');
});

test('a manifest observed long ago still serves a late click while the page is open', async () => {
  const app = load({ 'media-options': { ok: true }, 'media-source-options': checkedSource(2400) });
  const page = 'https://video.example/watch/long';
  const sender = { url: page, tab: { id: 31 }, frameId: 0, documentId: 'long-doc' };
  const changedAt = Date.now();
  await app.onMedia({ url: 'https://cdn.video.example/vod/master.m3u8', tabId: 31,
    frameId: 0, documentId: sender.documentId, method: 'GET', statusCode: 200, type: 'xmlhttprequest',
    timeStamp: changedAt + 200, responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }] });
  app.advanceTime(25 * 60_000);
  const result = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'blob:https://video.example/player', generation: 2, playerPlaying: true, mediaChangedAt: changedAt }, sender);
  assert.equal(result.data?.sourceChoices?.length, 1);
  assert.match(result.data.sourceChoices[0].label, /^40:00 · 720p · HLS/);
});

test('a manifest refreshed after playback starts is available to a later picker click', async () => {
  const app = load({ 'media-options': { ok: true }, 'media-source-options': checkedSource(2400) });
  const page = 'https://video.example/watch/refreshed';
  const sender = { url: page, tab: { id: 32 }, frameId: 0, documentId: 'refreshed-doc' };
  const changedAt = Date.now();
  app.advanceTime(30_000);
  await app.onMedia({ url: 'https://cdn.video.example/vod/master.m3u8', tabId: 32,
    frameId: 0, documentId: sender.documentId, method: 'GET', statusCode: 200, type: 'xmlhttprequest',
    timeStamp: changedAt + 30_000,
    responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }] });
  const result = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'blob:https://video.example/player', generation: 3, playerPlaying: true,
    mediaChangedAt: changedAt }, sender);
  assert.equal(result.data?.sourceChoices?.length, 1);
  assert.match(result.data.sourceChoices[0].label, /^40:00 · 720p · HLS/);
});

test('an extensionless manifest is observed only for the watched tab and gets an exact check', async () => {
  const app = load({ 'media-source-options': checkedSource() });
  const page = 'https://video.example/watch/demo';
  const sender = { url: page, tab: { id: 33 }, frameId: 0, documentId: 'content-type-doc' };
  const changedAt = Date.now();
  const url = 'https://cdn.video.example/play?id=42';
  try {
    await app.message({ type: 'media-element', watching: true }, sender);
    assert.deepEqual([...app.manifestWatchers.keys()], [33]);
    await app.manifestWatchers.get(33)({ url, tabId: 33, frameId: 0, documentId: sender.documentId,
      method: 'GET', statusCode: 200, type: 'xmlhttprequest', timeStamp: changedAt,
      responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }] });
    const result = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
      playerUrl: 'blob:https://video.example/player', generation: 1, playerPlaying: true, mediaChangedAt: changedAt }, sender);
    assert.equal(result.data?.sourceChoices?.length, 1);
    assert.ok(onlyExactChecks(app));
    assert.equal(app.sent[0].candidates[0].url, url);
  } finally {
    app.stopWorker();
  }
});

test('revoking sign-in before confirming an inferred source sends a fresh unsigned check', async () => {
  const app = load({ 'media-options': { ok: false, code: 'no-media' }, 'media-source-options': checkedSource() },
    { signInPermission: true });
  const page = 'https://www.instagram.com/reel/example/';
  const sender = { url: page, tab: { id: 34, url: page }, frameId: 0, documentId: 'signed-source-doc' };
  const changedAt = Date.now();
  const playerUrl = 'blob:https://www.instagram.com/player';
  await app.onMedia({ url: 'https://cdn.example/stream.m3u8', tabId: 34, frameId: 0, documentId: sender.documentId,
    method: 'GET', statusCode: 200, type: 'xmlhttprequest', timeStamp: changedAt });
  const options = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl, generation: 1, playerPlaying: true, mediaChangedAt: changedAt }, sender);
  assert.equal(app.sent.at(-1).authorized, true);
  await app.setPrefs({ useSignIn: false });
  const selected = await app.message({ type: 'media-source-select', sourceChoice: options.data.sourceChoices[0].token,
    pageUrl: page, playerUrl, generation: 1 }, sender);
  assert.equal(selected.ok, true);
  assert.equal(app.sent.filter(request => request.type === 'media-source-options').length, 2);
  assert.equal(app.sent.at(-1).mediaAuth, undefined);
});

test('when no candidate verifies, the picker reports the specific refusal instead of asking to play', async () => {
  const app = load({ 'media-options': { ok: true }, 'media-source-options': { ok: false, code: 'sign-in', message: 'Sign in.' } });
  const page = 'https://video.example/watch/private';
  const sender = { url: page, tab: { id: 32 }, frameId: 0, documentId: 'private-doc' };
  const changedAt = Date.now();
  await app.onMedia({ url: 'https://cdn.video.example/private/master.m3u8', tabId: 32,
    frameId: 0, documentId: sender.documentId, method: 'GET', statusCode: 200, type: 'xmlhttprequest',
    timeStamp: changedAt + 100, responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }] });
  const result = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'blob:https://video.example/player', generation: 2, playerPlaying: true, mediaChangedAt: changedAt }, sender);
  assert.equal(result.ok, false);
  assert.equal(result.code, 'sign-in');
});

test('evidence strength orders verification: an element-requested, typed manifest near the change goes first', async () => {
  const app = load({});
  const rank = app.evaluate('rankObservedSources');
  const changedAt = 1_000_000;
  const ranked = rank([
    { url: 'https://a.example/late.mp4', kind: 'direct', mime: 'video/mp4', wholeRepresentation: true, seen: changedAt + 14_000 },
    { url: 'https://a.example/x', kind: 'hls', typedManifest: true, requestType: 'xmlhttprequest', seen: changedAt + 400 },
    { url: 'https://a.example/clip.mp4', kind: 'direct', mime: 'video/mp4', wholeRepresentation: true, requestType: 'media', seen: changedAt + 100 }
  ], changedAt);
  assert.deepEqual([...ranked.map(source => source.url)],
    ['https://a.example/x', 'https://a.example/clip.mp4', 'https://a.example/late.mp4']);
});

test('the active player can recover its observed manifest during a late click', async () => {
  const page = 'https://video.example/watch/episode-2';
  const sender = { url: page, tab: { id: 35 }, frameId: 2, documentId: 'episode-2-doc' };
  const manifestUrl = 'https://cdn.video.example/episode-2/master.m3u8';
  const app = load({ 'media-options': { ok: true, data: { choices: [{ index: 0, label: 'Best quality' }] } } });
  const changedAt = Date.now();
  await app.onMedia({ url: manifestUrl, tabId: 35, frameId: 2, documentId: sender.documentId,
    method: 'GET', statusCode: 200, type: 'xmlhttprequest', timeStamp: changedAt - 10_000,
    responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }] });
  app.advanceTime(25 * 60_000);

  const result = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'blob:https://video.example/player', manifestUrl, generation: 7,
    playerPlaying: true, mediaChangedAt: changedAt, playerFacts: { durationSeconds: 720, height: 1080 } }, sender);

  assert.equal(result.ok, true);
  const request = app.sent.find(message => message.type === 'media-options');
  assert.equal(request.candidates[0].url, manifestUrl);
  assert.equal(app.sent.some(message => message.type === 'media-source-options'), false);
});

test('an observed manifest from another document or frame is not reused for this player', async () => {
  const page = 'https://video.example/watch/current';
  const sender = { url: page, tab: { id: 36 }, frameId: 2, documentId: 'current-doc' };
  const staleManifest = 'https://cdn.video.example/watch/old.m3u8';
  const app = load({ 'media-options': { ok: true, data: { choices: [{ index: 0, label: 'Best quality' }] } } });
  for (const [frameId, documentId, url] of [
    [2, 'old-doc', staleManifest], [3, 'current-doc', 'https://cdn.video.example/frame.m3u8']
  ]) {
    await app.onMedia({ url, tabId: 36, frameId, documentId, method: 'GET', statusCode: 200,
      type: 'xmlhttprequest', timeStamp: Date.now(),
      responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }] });
  }

  const result = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'blob:https://video.example/player', manifestUrl: staleManifest, generation: 8,
    playerPlaying: true, mediaChangedAt: Date.now(), playerFacts: { durationSeconds: 120 } }, sender);

  assert.equal(result.ok, true);
  assert.deepEqual([...app.sent.find(message => message.type === 'media-options').candidates], []);
  assert.equal(app.sent.some(message => message.type === 'media-source-options'), false);
});

test('verified duration, height and track facts rank source choices and make labels distinct', async () => {
  const page = 'https://video.example/watch/movie';
  const sender = { url: page, tab: { id: 37 }, frameId: 0, documentId: 'movie-doc' };
  const changedAt = Date.now();
  const sources = [
    ['https://cdn.video.example/trailer.m3u8', 30, 1080, true],
    ['https://cdn.video.example/720.m3u8', 720, 720, false],
    ['https://cdn.video.example/main.m3u8', 720, 1080, true]
  ];
  const verified = new Map(sources.map(([url, duration, height, hasAudio]) => [url, {
    ok: true,
    data: { mediaSession: 'a'.repeat(32), duration, height, hasAudio, hasVideo: true,
      choices: [{ index: 0, label: 'Best quality', detail: `${height}p · H.264${hasAudio ? ' + AAC' : ''}` }] }
  }]));
  const app = load({ 'media-options': { ok: false, code: 'no-media' },
    'media-source-options': request => verified.get(request.candidates[0].url) });
  for (const [url] of sources) {
    await app.onMedia({ url, tabId: 37, frameId: 0, documentId: sender.documentId,
      method: 'GET', statusCode: 200, type: 'xmlhttprequest', timeStamp: changedAt + 100,
      responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }] });
  }

  const result = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'blob:https://video.example/player', generation: 9, playerPlaying: true,
    mediaChangedAt: changedAt, playerFacts: { durationSeconds: 720, height: 1080 } }, sender);

  assert.deepEqual([...result.data.sourceChoices.map(choice => choice.label)], [
    '12:00 · 1080p · audio + video · HLS · 1 format',
    '12:00 · 720p · video only · HLS · 1 format',
    '0:30 · 1080p · audio + video · HLS · 1 format'
  ]);
  assert.equal(result.data.verifiedSources, true);
  assert.equal(result.data.choices, undefined, 'the player facts rank choices but do not select a stream');

  const unknown = app.evaluate(`VesperMediaEvidence.mediaFacts({ ok: true, data: {
    duration: 60, choices: [{ index: 0, label: 'Best quality', detail: 'HLS' }] } })`);
  assert.equal(unknown.hasAudio, undefined);
  assert.equal(unknown.hasVideo, undefined);
  const explicitTrackList = app.evaluate(`VesperMediaEvidence.mediaFacts({ ok: true, data: {
    audioTracks: [{ kind: 'audio' }], choices: [{ detail: 'audio not confirmed' }] } })`);
  assert.equal(explicitTrackList.hasAudio, true);
  assert.equal(app.evaluate('VesperMediaEvidence.trackLabel(VesperMediaEvidence.mediaFacts({ data: { choices: [] } }))'), '');
  const live = app.evaluate(`VesperMediaEvidence.mediaFacts({ data: { isLive: true,
    choices: [{ detail: '1080p H.264 with audio' }] } })`);
  assert.equal(live.live, true);
  assert.equal(live.hasAudio, true);
  assert.equal(live.hasVideo, true);
});

test('source verification checks at most five exact URLs', async () => {
  const page = 'https://video.example/watch/long-list';
  const sender = { url: page, tab: { id: 38 }, frameId: 0, documentId: 'long-list-doc' };
  const changedAt = Date.now();
  const app = load({ 'media-options': { ok: false, code: 'no-media' },
    'media-source-options': checkedSource() });
  for (let index = 0; index < 7; index++) {
    await app.onMedia({ url: `https://cdn.video.example/${index}.m3u8`, tabId: 38, frameId: 0,
      documentId: sender.documentId, method: 'GET', statusCode: 200, type: 'xmlhttprequest',
      timeStamp: changedAt + index,
      responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }] });
  }

  await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'blob:https://video.example/player', generation: 10, playerPlaying: true,
    mediaChangedAt: changedAt, playerFacts: { durationSeconds: 61 } }, sender);

  assert.equal(app.sent.filter(message => message.type === 'media-source-options').length, 5);
});

test('known adapter permalinks share identity without merging different episodes or servers', async () => {
  const app = load();
  const identity = app.evaluate('VesperPlayerSource.identity');
  assert.equal(identity('https://old.reddit.com/r/video/comments/abc123/title'),
    identity('https://redd.it/abc123'));
  assert.notEqual(identity('https://redd.it/abc123'), identity('https://v.redd.it/abc123/DASHPlaylist.mpd'));
  assert.equal(identity('https://vimeo.com/channels/staff/12345678/deadbeef'),
    identity('https://player.vimeo.com/video/12345678?h=deadbeef'));
  assert.notEqual(identity('https://vimeo.com/12345678/deadbeef'), identity('https://vimeo.com/12345678/cafebabe'));
  assert.equal(identity('https://www.bilibili.com/video/BV1Q541167Qg?p=1'), 'bilibili:BV1Q541167Qg');
  assert.equal(identity('https://www.bilibili.com/video/BV1Q541167Qg?p=2'), 'bilibili:BV1Q541167Qg:p:2');
  assert.notEqual(identity('https://tube.example/videos/watch/abcdef123456'),
    identity('https://mirror.example/videos/watch/abcdef123456'));
  assert.notEqual(identity('https://tube.example:8443/videos/watch/abcdef123456'),
    identity('https://tube.example/videos/watch/abcdef123456'));
  assert.equal(identity('https://generic.example/watch/abcdef123456'),
    'https://generic.example/watch/abcdef123456');
});

test('an Instagram carousel with multiple observed videos requires the user to choose one', async () => {
  const app = load({ 'media-options': { ok: true }, 'media-source-options': request =>
    checkedSource(10, request.candidates[0].url.includes('first') ? '2.9 MB MP4' : '1.0 MB MP4') });
  const page = 'https://www.instagram.com/p/abc123/';
  const sender = { url: page, tab: { id: 22 }, frameId: 0, documentId: 'instagram-carousel' };
  const changedAt = Date.now();
  const first = 'https://scontent.cdninstagram.com/v/t50.2886-16/first.mp4?sig=one';
  const second = 'https://scontent.cdninstagram.com/v/t50.2886-16/second.mp4?sig=two';
  for (const [url, size, seen] of [[first, 3_000_000, 100], [second, 1_000_000, 200]])
    await app.onMedia({ url, tabId: 22, frameId: 0, documentId: sender.documentId, method: 'GET',
      statusCode: 200, type: 'xmlhttprequest', timeStamp: changedAt + seen,
      responseHeaders: [
        { name: 'Content-Type', value: 'video/mp4' },
        { name: 'Content-Length', value: String(size) }
      ] });

  const message = { type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'blob:https://www.instagram.com/player', generation: 7, playerPlaying: true, mediaChangedAt: changedAt };
  const options = await app.message(message, sender);
  assert.equal(options.data.sourceChoices.length, 2);
  assert.match(options.data.sourceChoices[0].detail, /2\.9 MB/);
  assert.match(options.data.sourceChoices[1].detail, /1\.0 MB/);
  assert.equal(app.sent.length, 2);
  assert.ok(onlyExactChecks(app));

  const selected = await app.message({ type: 'media-source-select', sourceChoice: options.data.sourceChoices[0].token,
    pageUrl: page, generation: 7, playerUrl: message.playerUrl }, sender);
  assert.match(selected.data.choices[0].detail, /2\.9 MB/);
  assert.equal(app.sent.length, 2, 'no second request for an already verified source');
});

test('a late same-frame ad response is not correlated to the active Instagram player', async () => {
  const app = load({ 'media-options': { ok: true } });
  const page = 'https://www.instagram.com/reel/abc123/';
  const sender = { url: page, tab: { id: 23 }, frameId: 0, documentId: 'instagram-active-player' };
  const changedAt = Date.now();
  await app.onMedia({ url: 'https://scontent.cdninstagram.com/ads/ad.mp4?token=not-the-reel', tabId: 23,
    frameId: 0, documentId: sender.documentId, method: 'GET', statusCode: 200, type: 'xmlhttprequest',
    timeStamp: changedAt + 16_000, responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
  app.advanceTime(16_000);

  const result = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'blob:https://www.instagram.com/player', generation: 8, playerPlaying: true, mediaChangedAt: changedAt }, sender);
  assert.equal(result.data?.sourceChoices, undefined);
  assert.equal(app.sent[0].candidates.length, 0);
});

test('a late same-frame playlist response is not correlated to the active Instagram player', async () => {
  const app = load({ 'media-options': { ok: true } });
  const page = 'https://www.instagram.com/reel/abc123/';
  const sender = { url: page, tab: { id: 25 }, frameId: 0, documentId: 'instagram-active-player' };
  const changedAt = Date.now();
  await app.onMedia({ url: 'https://scontent.cdninstagram.com/ads/ad.m3u8', tabId: 25, frameId: 0,
    documentId: sender.documentId, method: 'GET', statusCode: 200, type: 'xmlhttprequest',
    timeStamp: changedAt + 16_000,
    responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }] });
  app.advanceTime(16_000);

  const result = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'blob:https://www.instagram.com/player', generation: 9, playerPlaying: true, mediaChangedAt: changedAt }, sender);
  assert.equal(result.data?.sourceChoices, undefined);
  assert.equal(app.sent[0].candidates.length, 0);
});

test('a partial video range is not offered as a complete Instagram source', async () => {
  const app = load({ 'media-options': { ok: true } });
  const page = 'https://www.instagram.com/reel/abc123/';
  const sender = { url: page, tab: { id: 20 }, frameId: 0, documentId: 'instagram-range-player' };
  const changedAt = Date.now();
  await app.onMedia({ url: 'https://scontent.cdninstagram.com/v/t50.2886-16/video.mp4', tabId: 20,
    frameId: 0, documentId: sender.documentId, method: 'GET', statusCode: 206, type: 'xmlhttprequest',
    timeStamp: changedAt + 100, responseHeaders: [
      { name: 'Content-Type', value: 'video/mp4' },
      { name: 'Content-Range', value: 'bytes 0-999/5000' },
      { name: 'Content-Length', value: '1000' }
    ] });

  const result = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'blob:https://www.instagram.com/player', generation: 5, playerPlaying: true, mediaChangedAt: changedAt }, sender);
  assert.equal(result.ok, true);
  assert.equal(result.data?.sourceChoices, undefined);
  assert.equal(app.sent[0].candidates.length, 0);

  const completeApp = load({ 'media-options': { ok: false, code: 'no-media' }, 'media-source-options': checkedSource(5) });
  const completeSender = { url: page, tab: { id: 21 }, frameId: 0, documentId: 'instagram-full-range-player' };
  await completeApp.onMedia({ url: 'https://scontent.cdninstagram.com/v/t50.2886-16/video.mp4', tabId: 21,
    frameId: 0, documentId: completeSender.documentId, method: 'GET', statusCode: 206, type: 'xmlhttprequest',
    timeStamp: changedAt + 200, responseHeaders: [
      { name: 'Content-Type', value: 'video/mp4' },
      { name: 'Content-Range', value: 'bytes 0-4999/5000' },
      { name: 'Content-Length', value: '5000' }
    ] });
  const full = await completeApp.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
    playerUrl: 'blob:https://www.instagram.com/player', generation: 6, playerPlaying: true, mediaChangedAt: changedAt }, completeSender);
  assert.equal(full.data.sourceChoices.length, 1);
});

test('an unfamiliar page with multiple players cannot substitute page-wide formats for the current player', async () => {
  const app = load({ 'media-options': checkedSource(15), 'media-source-options': {ok:false, code:'source-unavailable'} });
  const page = 'https://media.example.com/feed';
  const source = 'https://media.example.com/current.mp4';
  const sender = {url:page, tab:{id:81}, frameId:0, documentId:'multi-player-document'};
  const request = {type:'media-options', pageUrl:page, playerPageUrl:page, playerUrl:source, playerKey:'player-1',
    generation:5, playerHasMedia:true, mediaChangedAt:Date.now()};
  try {
    assert.equal((await app.message(request, sender)).ok, true);
    const result = await app.message({...request, multiplePlayers:true}, sender);
    assert.equal(result.code, 'source-unavailable');
    assert.deepEqual(app.sent.map(message => message.type), ['media-options', 'media-source-options']);
    assert.equal(app.sent[1].candidates[0].url, source);
  } finally { app.stopWorker(); }
});

for (const mime of ['video/mp4', 'video/webm', 'audio/webm', 'audio/mp4']) {
  test(`${mime} with a strong range validator gets full-file verification`, async () => {
    const app = load({ 'media-options': {ok:false, code:'no-media'}, 'media-range-options': checkedSource(15) });
    const page='https://www.instagram.com/reel/ranged/', url='https://cdn.example.com/opaque-video';
    const sender={url:page,tab:{id:81},frameId:0,documentId:'ranged-document'};
    const changedAt=Date.now();
    const request={type:'media-options',pageUrl:page,playerPageUrl:page,playerUrl:'blob:https://www.instagram.com/current',
      generation:5,playerPlaying:true,mediaChangedAt:changedAt};
    try {
      await app.onMedia({url,tabId:81,frameId:0,documentId:sender.documentId,method:'GET',statusCode:206,
        type:'xmlhttprequest',timeStamp:changedAt,responseHeaders:[
          {name:'Content-Type',value:mime},{name:'Content-Range',value:'bytes 0-999/700000'},
          {name:'Content-Length',value:'1000'},{name:'ETag',value:'"original"'}]});
      const result=await app.message(request,sender);
      assert.equal(result.data.sourceChoices.length,1);
      const check=app.sent.find(message=>message.type==='media-range-options');
      assert.equal(check.candidates[0].mimeType,mime);
      assert.equal(check.candidates[0].observedLength,700000);
      assert.equal(check.candidates[0].observedETag,'"original"');
      assert.equal(check.candidates[0].url,url);
      assert.equal(app.sent.some(message=>message.type==='media-source-options'),false);
      const selected=await app.message({...request,type:'media-source-select',sourceChoice:result.data.sourceChoices[0].token},sender);
      assert.equal(selected.ok,true);
    } finally {app.stopWorker();}
  });
}

test('two independently witnessed query-addressed ranges can propose a verified full resource, not automatically select it',async()=>{
  const page='https://www.instagram.com/reel/ranged/', base='https://cdn.example.com/asset.mp4?signature=abc%2Fdef';
  const sender={url:page,tab:{id:81},frameId:0,documentId:'query-range-document'};
  const changedAt=Date.now(), urls=[base+'&bytestart=0&byteend=999',base+'&bytestart=1000&byteend=1999'];
  const hash=async url=>Buffer.from(await webcrypto.subtle.digest('SHA-256',new TextEncoder().encode(url))).toString('hex');
  for(const scenario of ['verified','one-witness','changed-tag','wrong-offset','other-document','changed-after-picker',
    'query-200','query-200-incorrect-length','query-200-replaced']){
    const app=load({'media-options':{ok:false,code:'no-media'},'media-range-options':checkedSource(15)});
    try {
      async function report(i,tag,at=changedAt){
        const querySlice = scenario.startsWith('query-200');
        await app.onMedia({url:urls[i],tabId:81,frameId:0,documentId:scenario==='other-document'&&i===1?'other':sender.documentId,
          method:'GET',statusCode:querySlice ? 200 : 206,type:'xmlhttprequest',timeStamp:at,responseHeaders:[
            {name:'Content-Type',value:'video/mp4'},
            ...(!querySlice ? [{name:'Content-Range',value:scenario==='wrong-offset'&&i===1?'bytes 2000-2999/700000'
              :'bytes '+(i*1000)+'-'+(i*1000+999)+'/700000'}] : []),
            {name:'Content-Length',value:scenario==='query-200-incorrect-length'&&i===1?'999':'1000'},{name:'ETag',value:tag}]});
      }
      await report(0,'"v1"');await report(1,scenario==='changed-tag'?'"v2"':'"v1"');
      const request={type:'media-options',pageUrl:page,playerPageUrl:page,playerUrl:'blob:https://www.instagram.com/current',
        generation:5,playerKey:'p',playerPlaying:true,mediaChangedAt:changedAt,
        playerResourceFingerprints:await Promise.all((scenario==='one-witness'?urls.slice(0,1):urls).map(hash))};
      const result=await app.message(request,sender);
      const checks=app.sent.filter(m=>m.type==='media-range-options');
      if(!['verified','changed-after-picker','query-200','query-200-replaced'].includes(scenario)){
        assert.equal(checks.length,0,scenario);
        assert.equal(result.ok,false,scenario);
        continue;
      }
      assert.equal(checks.length,1);
      assert.equal(checks[0].candidates[0].url,base);
      assert.equal(checks[0].candidates[0].observedETag,'"v1"');
      const querySlice = scenario.startsWith('query-200');
      assert.equal(checks[0].candidates[0].observedLength,querySlice ? undefined : 700000);
      assert.equal(checks[0].candidates[0].observedMinimumLength,querySlice ? 2000 : undefined);
      assert.equal(result.data.sourceChoices.length,1);
      if(['changed-after-picker','query-200-replaced'].includes(scenario))await report(1,'"changed"',changedAt+1);
      const selected=await app.message({...request,type:'media-source-select',sourceChoice:result.data.sourceChoices[0].token},sender);
      assert.equal(selected.ok,['verified','query-200'].includes(scenario),scenario);
    }finally{app.stopWorker()}
  }
});
test('signed media response URLs are removed from session storage after their TTL', async () => {
  const sessionData = {};
  const app = load({}, { sessionData });
  const mediaUrl = 'https://scontent.cdninstagram.com/video.mp4?signature=short-lived';
  const seenAt = Date.now();
  await app.onMedia({ url: mediaUrl, tabId: 24, frameId: 0, documentId: 'instagram-ttl', method: 'GET',
    statusCode: 200, type: 'xmlhttprequest', timeStamp: seenAt,
    responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
  assert.equal(sessionData['tab:24'].sources[0].url, mediaUrl);

  app.advanceTime(10 * 60_000 + 1000);
  await app.expireSources();
  assert.deepEqual(sessionData['tab:24'].sources, []);
});

test('observed media sources stay under a global session-storage budget', async () => {
  const sessionData = {};
  const now = Date.now();
  for (let tabId = 1; tabId <= 9; tabId++) {
    sessionData[`tab:${tabId}`] = { sources: Array.from({ length: 25 }, (_, index) => ({
      url: `https://cdn.example.com/${tabId}/${index}.mp4`, kind: 'direct', mime: 'video/mp4',
      documentId: `doc-${tabId}`, frameId: 0, seen: now - (225 - ((tabId - 1) * 25 + index)) * 1000
    })) };
  }
  const app = load({}, { sessionData });
  await app.onMedia({ url: 'https://cdn.example.com/newest.mp4', tabId: 10, frameId: 0, documentId: 'doc-10',
    method: 'GET', statusCode: 200, type: 'media', timeStamp: now,
    responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });

  const stored = Object.entries(sessionData).filter(([key]) => /^tab:\d+$/.test(key));
  const sourceCount = stored.reduce((count, [, state]) => count + state.sources.length, 0);
  assert.ok(sourceCount <= 200);
  assert.equal(sessionData['tab:10'].sources.length, 1);
  assert.equal(sessionData['tab:1'].sources.some(source => source.url.endsWith('/0.mp4')), false);
  assert.ok(stored.every(([, state]) => state.sources.length <= 25));
});

test('an ambiguous blob player shows verified, separate stream choices and never a broad lookup', async () => {
  const app = load({ 'media-source-options': request => checkedSource(request.candidates[0].url.includes('video') ? 61 : 15) });
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
  assert.equal(app.sent.length, 2);
  assert.ok(onlyExactChecks(app));
  assert.equal(JSON.stringify(options.data.sourceChoices).includes('sig='), false);
  assert.equal(JSON.stringify(options.data.sourceChoices).includes('.m3u8'), false);

  const token = options.data.sourceChoices.find(choice => choice.label.startsWith('1:01')).token;
  const selected = await app.message({ type: 'media-source-select', sourceChoice: token, pageUrl: page,
    generation: 3, playerUrl: message.playerUrl }, sender);
  assert.equal(selected.ok, true);
  assert.equal(selected.data.duration, 61);
  assert.equal(app.sent.length, 2);

  const replay = await app.message({ type: 'media-source-select', sourceChoice: token, pageUrl: page,
    generation: 3, playerUrl: message.playerUrl }, sender);
  assert.equal(replay.code, 'expired-selection');
  assert.equal(app.sent.length, 2);

  const reopened = await app.message(message, sender);
  assert.notEqual(reopened.data.sourceChoices[0].token, token);
  const retried = await app.message({ type: 'media-source-select', sourceChoice: reopened.data.sourceChoices[0].token,
    pageUrl: page, generation: 3, playerUrl: message.playerUrl }, sender);
  assert.equal(retried.ok, true);
});

test('source-choice tokens are not restored after a worker restarts', async () => {
  const sessionData = {};
  const app = load({ 'media-source-options': checkedSource() }, { sessionData });
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
  const app = load({ 'media-options': { ok: true }, 'media-source-options': checkedSource() });
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
  assert.ok(onlyExactChecks(app), 'mismatched selections never reach VESPER beyond the exact-source checks');
});

test('direct player evidence observed just before playback metadata is still current', async () => {
  const app = load({ 'media-options': { ok: false, code: 'no-media' } });
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

test('the Chrome fallback uses one observed complete file and rejects stale or foreign selections', async () => {
  const app = load({ 'media-options': { ok: false, code: 'no-media' } }, { localData: { vesperPaused: true } });
  const current = 'https://www.youtube.com/shorts/bbbbbbbbbbb';
  const playerUrl = 'https://video.example/current.mp4';
  const sender = { url: current, tab: { id: 9 }, frameId: 0, documentId: 'current' };
  await app.onMedia({ url: playerUrl, documentId: 'current', frameId: 0, tabId: 9, method: 'GET', statusCode: 200,
    type: 'media', timeStamp: Date.now(), responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
  const options = await app.message({ type: 'media-options', pageUrl: current, playerPageUrl: current, playerUrl }, sender);
  assert.equal(options.ok, true);
  assert.equal(options.data.choices[0].container, 'MP4');
  const select = { type: 'browser-media-select', pageUrl: current, playerUrl, browserChoice: options.data.browserChoice };
  assert.equal((await app.message({ ...select, generation: 4 }, sender)).code, 'expired-selection');
  assert.equal((await app.message({ ...select, playerUrl: 'https://video.example/next.mp4' }, sender)).code, 'expired-selection');
  assert.equal((await app.message(select, { ...sender, documentId: 'foreign' })).ok, false);
  assert.equal((await app.message({ ...select, pageUrl: 'https://www.youtube.com/shorts/ccccccccccc' }, sender)).ok, false);
  assert.equal((await app.message(select, sender)).ok, true);
  assert.equal((await app.message(select, sender)).ok, true);
  assert.deepEqual(app.actions, ['native', 'chrome-start']);
});

test('a new player generation cannot reuse an old direct-file response on the same URL', async () => {
  const app = load({ 'media-options': { ok: false, code: 'no-media' } });
  const page = 'https://video.example/watch/current';
  const playerUrl = 'https://video.example/player.mp4';
  const sender = { url: page, tab: { id: 9 }, frameId: 0, documentId: 'current' };
  const changedAt = Date.now();
  await app.onMedia({ url: playerUrl, documentId: 'current', frameId: 0, tabId: 9, method: 'GET', statusCode: 200,
    type: 'media', timeStamp: changedAt - 10_000, responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
  const result = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page, playerUrl,
    generation: 2, mediaChangedAt: changedAt }, sender);
  assert.equal(result.ok, false);
  assert.equal(result.data?.browserChoice, undefined);
});

test('a browser format choice survives a service worker restart without starting twice', async () => {
  const sessionData = {};
  const downloadHistory = [];
  const current = 'https://www.youtube.com/shorts/bbbbbbbbbbb';
  const playerUrl = 'https://video.example/current.mp4';
  const sender = { url: current, tab: { id: 9 }, frameId: 0, documentId: 'current' };
  const options = { sessionData, downloadHistory };
  const firstWorker = load({}, options);
  await firstWorker.onMedia({ url: playerUrl, documentId: 'current', frameId: 0, tabId: 9, method: 'GET', statusCode: 200,
    type: 'media', timeStamp: Date.now(), responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
  const formats = await firstWorker.message({ type: 'media-options', pageUrl: current, playerPageUrl: current, playerUrl }, sender);

  const workerAfterRestart = load({}, options);
  const select = { type: 'browser-media-select', pageUrl: current, playerUrl, browserChoice: formats.data.browserChoice };
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
  const app = load({ 'media-options': { ok: false, code: 'no-media' } }, { sessionData, downloadHistory });
  await app.onMedia({ url: playerUrl, documentId: 'current', frameId: 0, tabId: 9, method: 'GET', statusCode: 200,
    type: 'media', timeStamp: Date.now(), responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
  const formats = await app.message({ type: 'media-options', pageUrl: current, playerPageUrl: current, playerUrl }, sender);
  const select = { type: 'browser-media-select', pageUrl: current, playerUrl, browserChoice: formats.data.browserChoice };

  const results = await Promise.all([app.message(select, sender), app.message(select, sender)]);

  assert.deepEqual(results.map(result => result.ok), [true, true]);
  assert.deepEqual(app.actions, ['native', 'chrome-start']);
});

test('a choice reconciles a Chrome start whose reply was lost during worker restart', async () => {
  const sessionData = {};
  const downloadHistory = [];
  const current = 'https://www.youtube.com/shorts/bbbbbbbbbbb';
  const playerUrl = 'https://video.example/current.mp4';
  const sender = { url: current, tab: { id: 9 }, frameId: 0, documentId: 'current' };
  const options = { sessionData, downloadHistory };
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
  const select = { type: 'browser-media-select', pageUrl: current, playerUrl, browserChoice: formats.data.browserChoice };
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
  const options = { sessionData, downloadHistory };
  const firstWorker = load({}, options);
  await firstWorker.onMedia({ url: playerUrl, documentId: 'current', frameId: 0, tabId: 9, method: 'GET', statusCode: 200,
    type: 'media', timeStamp: Date.now(), responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
  const formats = await firstWorker.message({ type: 'media-options', pageUrl: current, playerPageUrl: current, playerUrl }, sender);
  const saved = sessionData.vesperBrowserChoices.find(([token]) => token === formats.data.browserChoice)[1];
  saved.started = true;
  saved.startedAt = Date.now();

  const workerAfterRestart = load({}, options);
  const select = { type: 'browser-media-select', pageUrl: current, playerUrl, browserChoice: formats.data.browserChoice };
  assert.equal((await workerAfterRestart.message(select, sender)).ok, true);
  assert.deepEqual(workerAfterRestart.actions, ['chrome-start']);
  assert.equal(workerAfterRestart.downloadHistory.length, 1);
});

test('carousel slides and TikTok embed players keep the exact item identity', () => {
  const app = load();
  const identity = url => app.evaluate(`VesperPlayerSource.identity(${JSON.stringify(url)})`);
  assert.notEqual(identity('https://www.instagram.com/p/clip/?img_index=2'), identity('https://www.instagram.com/p/clip/'));
  assert.equal(identity('https://www.instagram.com/p/clip/?img_index=1'), identity('https://www.instagram.com/p/clip/'));
  for (const id of ['7704503448573578527', '7688010685061664021'])
    assert.equal(identity(`https://www.tiktok.com/player/v1/${id}`), identity(`https://www.tiktok.com/@fixture/video/${id}`));
  app.stopWorker();
});

test('known fragments never reach the native picker as complete direct files', async () => {
  const page = 'https://example.com/player';
  const sender = { url: page, tab: { id: 5 }, frameId: 0, documentId: 'fragment-fixture' };
  for (const [url, range] of [['https://cdn.example/asset.mp4?ByteStart=50', null],
    ['https://cdn.example/asset.mp4', 'bytes 50-99/120']]) {
    const app = load({ 'media-options': { ok: false, code: 'no-media' } });
    await app.onMedia({ url, documentId: sender.documentId, frameId: 0, tabId: 5, method: 'GET', statusCode: 200,
      type: 'media', timeStamp: Date.now(), responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' },
        ...(range ? [{ name: 'Content-Range', value: range }] : [])] });
    await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page, playerUrl: url }, sender);
    assert.equal(app.sent.find(r => r.type === 'media-options').candidates.length, 0);
    app.stopWorker();
  }
  const app = load({ 'media-options': { ok: false, code: 'no-media' } });
  const url = 'https://cdn.example/seekable.mp4';
  await app.onMedia({ url, documentId: sender.documentId, frameId: 0, tabId: 5, method: 'GET', statusCode: 206,
    type: 'media', timeStamp: Date.now(), responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' },
      { name: 'Content-Range', value: 'bytes 50-99/120' }] });
  await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page, playerUrl: url }, sender);
  assert.equal(app.sent.find(r => r.type === 'media-options').candidates[0].url, url);
  app.stopWorker();
});

test('blob and data media context-menu actions show failure feedback', async () => {
  const app = load();
  await app.onContextClicked({ menuItemId: 'vesper-download', mediaType: 'video', srcUrl: 'blob:https://example.com/media' }, { id: 3 });
  await app.onContextClicked({ menuItemId: 'vesper-download', mediaType: 'video', srcUrl: 'data:video/mp4;base64,AAAA' }, { id: 3 });
  assert.deepEqual(JSON.parse(JSON.stringify(app.badges)), [{ tabId: 3, text: '!' }, { tabId: 3, text: '!' }]);
  const notices = app.tabMessages.filter(entry => entry.message.type === 'vesper-context-notice');
  assert.equal(notices.length, 2);
  assert.ok(notices.every(entry => entry.message.reply.message.length > 0));
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

test('a new blob is tracked in VESPER while Chrome keeps the original connection', async () => {
  const download = { id: 7, url: 'blob:https://example.com/private',
    filename: 'C:\\Downloads\\archive.zip', state: 'in_progress',
    danger: 'safe', startTime: '2026-10-10T08:00:00.000Z', totalBytes: 500,
    bytesReceived: 20, canResume: true };
  const app = load({}, { download, activity: true });
  await app.onCreated(download);
  await app.recover();
  const port = app.ports[0];
  assert.ok(port);
  assert.equal(port.posted[0].type, 'browser-managed');
  assert.equal(port.posted[0].operation, 'watch');
  assert.equal(port.posted[0].files[0].received, 20);
  assert.equal(port.posted[0].files[0].hosts[0], 'example.com');
  assert.deepEqual(app.sent, []);
  assert.deepEqual(app.actions, []);
  assert.equal(download.state, 'in_progress');
  assert.equal(app.filename(download), undefined);
  port.reply({ ok: true, data: { managedDownloads: true, siteRules: { neverCaptureHosts: ['blocked.example'] } } });
  assert.equal(app.filename(download).filename, 'VESPER/archive.zip');
  assert.equal(app.filename({ ...download, referrer: 'https://blocked.example/' }), undefined);
  assert.equal(app.filename({ ...download, incognito: true }), undefined);
});

test('Continue in VESPER follows a non-resumable transfer without acting on the Chrome connection', async () => {
  const download = { id: 9, url: 'https://example.com/export', filename: 'C:\\Downloads\\export.zip',
    state: 'in_progress', danger: 'safe', startTime: '2026-10-10T08:00:00.000Z',
    bytesReceived: 25, canResume: false };
  const app = load({ 'browser-managed': { ok: true } }, { download, activity: true, prefs: { intercept: false } });
  const list = await app.message({ type: 'managed-downloads' });
  assert.equal(list.files[0].id, download.id);
  const action = { type: 'continue-managed', id: download.id, startTime: download.startTime };
  const page = { tab: { id: 2, url: download.url } };
  assert.equal((await app.message(action, page)).ok, false);
  assert.equal((await app.message({ ...action, startTime: '2026-10-10T08:00:01.000Z' })).ok, false);
  assert.equal((await app.message(action)).ok, true);
  assert.deepEqual(app.actions, ['native']);
  assert.equal(app.sent[0].operation, 'connect');
  assert.equal((await app.message({ type: 'managed-downloads' })).files.length, 0);
  assert.equal(download.state, 'in_progress');
});

test('an enrolled Chrome file can finish while the extension worker is asleep', async () => {
  const download = { id: 12, url: 'blob:https://example.com/export', filename: 'D:\\Chosen\\export.zip',
    state: 'in_progress', danger: 'safe', startTime: '2026-10-10T08:00:00.000Z', bytesReceived: 25 };
  const first = load({}, { download, activity: true });
  await first.onCreated(download);
  await first.recover();
  const receipt = first.ports[0].posted[0].files[0].id;
  first.stopWorker();
  Object.assign(download, { state: 'complete', fileSize: 50, exists: true });
  const next = load({ 'browser-managed': { ok: true } }, { download, activity: true, localData: first.localData });
  await next.recover();
  const result = next.sent.find(message => message.operation === 'complete');
  assert.equal(result.downloadId, receipt);
  assert.equal(result.path, download.filename);
  assert.equal(result.length, 50);
  assert.deepEqual(next.actions, ['native']);
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

test('manual requests to take ownership of an existing Chrome download are forbidden', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started }, { download: file });
  const result = await app.message({ type: 'move-download', downloadId: file.id });
  assert.equal(result.code, 'forbidden');
  assert.equal(file.paused, false);
  assert.equal(file.state, 'in_progress');
  assert.deepEqual(app.sent, []);
  assert.deepEqual(app.actions, []);
});

test('unsafe downloads remain exclusively Chrome-owned and are not exposed for manual moving', async () => {
  const file = { ...activeDownload(), danger: 'uncommon' };
  const app = load({}, { download: file });
  assert.equal((await app.message({ type: 'chrome-downloads' })).code, 'forbidden');
  assert.equal((await app.message({ type: 'move-download', downloadId: file.id })).code, 'forbidden');
  await app.onCreated(file);
  assert.equal(app.sent.length, 0);
  assert.equal(file.paused, false);
});

test('file download sign-in defaults on without requiring a host list', async () => {
  const app = load({ submit: { ok: true } }, { signInPermission: true });

  await app.message({ type: 'submit', pageUrl: 'https://public.example/',
    items: [{ url: 'https://public.example/file.zip' }] });

  assert.deepEqual(app.cookieLookups, ['https://public.example/file.zip']);
  assert.equal(app.sent[0].items[0].cookie, 'session=test-value');
  assert.equal(app.sent[0].authorized, true);
});

test('global sign-in works for HTTPS destinations without a host list', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, {
    download: file, signInPermission: true, prefs: { useSignIn: true }, localData: { signInHosts: [] }
  });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(app.cookieLookups.length, 1);
  assert.equal(app.sent[0].authorized, true);

  const approved = load({ submit: { ok: true } }, {
    signInPermission: true, prefs: { useSignIn: true }, localData: { signInHosts: ['example.com'] }
  });
  const result = await approved.message({ type: 'submit', items: [
    { url: 'https://example.com/file.zip' },
    { url: 'https://cdn.example.com/file.zip' },
    { url: 'http://example.com/file.zip' }
  ] });
  assert.equal(result.ok, true);
  assert.equal(approved.sent[0].items[0].cookie, 'session=test-value');
  assert.equal(approved.sent[0].items[1].cookie, 'session=test-value');
  assert.equal(approved.sent[0].items[2].cookie, undefined);
  assert.deepEqual(approved.cookieLookups, ['https://example.com/file.zip', 'https://cdn.example.com/file.zip']);
});

test('sign-in context is used only during a fresh capture and a failed attempt does not retry', async () => {
  const file = activeDownload();
  const replies = { 'browser-submit': { ok: false, code: 'host-error' }, 'browser-transfer': transfer('failed') };
  const app = load(replies, { download: file, signInPermission: true, prefs: { useSignIn: true } });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(app.sent[0].items[0].cookie, 'session=test-value');
  assert.ok(!JSON.stringify(await app.local.get('browserDownloads')).includes('test-value'));

  app.advanceTime(61_000);
  await app.recover();
  assert.deepEqual(app.cookieLookups, [file.url]);
  assert.equal(app.sent.filter(isBrowserSubmission).length, 1);

  const fresh = { ...activeDownload(), id: 15 };
  const next = load({ 'browser-submit': started, 'browser-transfer': transfer('active') },
    { download: fresh, signInPermission: true, prefs: { useSignIn: true } });
  await observe(next, fresh);
  await next.onCreated(fresh);
  assert.equal(next.sent[0].items[0].cookie, 'session=test-value');
  assert.deepEqual(next.cookieLookups, [fresh.url]);

  for (const opts of [{ signInPermission: false }, { signInPermission: true, prefs: { useSignIn: false } }]) {
    const optedOut = load({ 'browser-submit': started, 'browser-transfer': transfer('active') },
      { download: activeDownload(), ...opts });
    await observe(optedOut, activeDownload());
    await optedOut.onCreated(activeDownload());
    assert.equal(optedOut.cookieLookups.length, 0);
    assert.equal(optedOut.sent[0].authorized, false);
  }
});

// The browser stamps this sender; a web page cannot forge the extension's own origin.
const contentScript = { tab: { id: 7, url: 'https://shop.example/item' }, url: 'https://shop.example/item', frameId: 0, documentId: 'doc-1' };

test('the native collection picker rejects a page-supplied cross-origin URL instead of opening the wrong page', async () => {
  const app = load({ 'media-picker': { ok: true } });
  const result = await app.message({ type: 'native-picker', pageUrl: 'https://attacker.example/playlist' }, contentScript);

  assert.equal(result.ok, false);
  assert.equal(result.code, 'invalid');
  assert.equal(app.sent.some(message => message.type === 'media-picker'), false);
});

test('a content script cannot use the commands reserved for VESPER pages', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active'), hello: { ok: true },
    probe: { ok: true, data: { kind: 'direct' } }, submit: { ok: true }, 'media-picker': { ok: true } },
    { download: file, signInPermission: true, prefs: { useSignIn: true } });
  await observe(app, file);
  const privileged = [
    { type: 'tab-state', tabId: 99 }, { type: 'chrome-downloads' }, { type: 'move-download', downloadId: file.id },
    { type: 'retry', id: 'anything' }, { type: 'submit', items: [{ url: 'https://bank.example/export.csv' }] },
    { type: 'describe', url: 'https://example.com/a.mp4' }, { type: 'hello' },
    { type: 'not-a-command' }
  ];
  for (const message of privileged)
    assert.equal((await app.message(message, contentScript)).code, 'forbidden', message.type);
  assert.equal(app.sent.length, 0, 'nothing reached VESPER');
  assert.equal(app.cookieLookups.length, 0, 'no cookies were read');
  assert.equal(file.paused, false);
});

test('in-page media picking works while even VESPER extension pages cannot claim Chrome downloads', async () => {
  const file = activeDownload();
  const app = load({ hello: { ok: true, message: 'VESPER is running.' } }, { download: file });
  await observe(app, file);
  assert.equal((await app.message({ type: 'media-select', mediaSession: 'bad' }, contentScript)).code, 'invalid');
  assert.equal((await app.message({ type: 'media-element' }, contentScript)).ok, true);

  const transfersPage = { tab: { id: 3, url: 'chrome-extension://vesper-test/transfers.html' },
    url: 'chrome-extension://vesper-test/transfers.html' };
  assert.equal((await app.message({ type: 'chrome-downloads' }, transfersPage)).code, 'forbidden');
  assert.equal((await app.message({ type: 'move-download', downloadId: file.id }, transfersPage)).code, 'forbidden');
  assert.equal((await app.message({ type: 'tab-state' }, transfersPage)).ok, true);
  assert.equal((await app.message({ type: 'hello' })).ok, true);
  assert.equal(file.paused, false);
});

test('messages from another extension or without a browser-stamped sender are refused', async () => {
  const app = load({ hello: { ok: true } });
  const page = 'chrome-extension://vesper-test/popup.html';
  for (const sender of [undefined, {}, { url: page }, { id: 'other-extension', url: 'chrome-extension://other-extension/popup.html' },
    { id: 'other-extension', url: page }, { id: 'vesper-test', url: 'https://vesper-test.example/popup.html' },
    { id: 'vesper-test', url: 'chrome-extension://vesper-test-evil/popup.html' }, { id: 'vesper-test', tab: { id: 1 }, url: 'file:///C:/page.html' }])
    assert.equal((await app.rawMessage({ type: 'hello' }, sender)).code, 'forbidden', JSON.stringify(sender));
  assert.equal((await app.rawMessage(null, { id: 'vesper-test', url: page })).code, 'forbidden');
  assert.equal(app.sent.length, 0);
});

// ---------------------------------------------------------------- sign-in cookies follow SameSite (VES-003)

const mediaOptionsReply = { ok: true, data: { title: 'Example video', choices: [{ index: 0, label: '720p' }] } };
const videoPage = 'https://www.youtube.com/watch?v=EK3tCffac5k';
const videoSender = { tab: { id: 17, url: videoPage }, url: videoPage, frameId: 0, documentId: 'doc-17' };
const videoOptionsMessage = (pageUrl, extra = {}) => ({
  type: 'media-options', pageUrl, playerPageUrl: pageUrl, playerUrl: '', generation: 1,
  mediaChangedAt: 0, playerPlaying: false, ...extra
});

test('opening any HTTPS video picker uses sign-in after Chrome approves it', async () => {
  const pageUrl = 'https://www.tiktok.com/@author/video/1234567';
  const sender = { ...videoSender, tab: { id: 17, url: pageUrl }, url: pageUrl };
  const app = load({ 'media-options': mediaOptionsReply }, { signInPermission: true });
  const result = await app.message(videoOptionsMessage(pageUrl), sender);
  assert.equal(result.ok, true);
  assert.deepEqual(app.cookieLookups, [pageUrl]);
  assert.equal(app.sent[0].mediaAuth.cookie, 'session=test-value');
  assert.equal(app.sent[0].authorized, true);

  const unfamiliar = 'https://example.com/video';
  const generic = load({ 'media-options': mediaOptionsReply }, { signInPermission: true });
  await generic.message(videoOptionsMessage(unfamiliar), { ...sender, url: unfamiliar, tab: { id: 17, url: unfamiliar } });
  assert.deepEqual(generic.cookieLookups, [unfamiliar]);
  assert.equal(generic.sent[0].mediaAuth.cookie, 'session=test-value');

  for (const [address, opts, extra] of [
    [pageUrl, { signInPermission: false }, {}],
    [pageUrl, { signInPermission: true, prefs: { useSignIn: false } }, {}],
    [pageUrl, { signInPermission: true }, { prefetch: true }],
    ['http://example.com/video', { signInPermission: true }, {}]
  ]) {
    const blocked = load({ 'media-options': mediaOptionsReply }, opts);
    await blocked.message(videoOptionsMessage(address, extra), { ...sender, url: address, tab: { id: 17, url: address } });
    assert.deepEqual(blocked.cookieLookups, []);
    assert.equal(blocked.sent[0].mediaAuth, undefined);
  }
});

test('video format prefetch defaults on and skips paused or unbound pages', async () => {
  const app = load({ 'media-options': mediaOptionsReply });
  const enabled = await app.message(videoOptionsMessage(videoPage, { prefetch: true }), videoSender);
  assert.equal(enabled.ok, true);
  assert.equal(app.sent.length, 1);

  const disabled = load({ 'media-options': mediaOptionsReply }, { localData: { vesperPaused: true } });
  const skipped = await disabled.message(videoOptionsMessage(videoPage, { prefetch: true }), videoSender);
  assert.equal(skipped.data.prefetchSkipped, true);
  assert.equal(disabled.sent.length, 0);

  const unsupported = load({ 'media-options': mediaOptionsReply });
  const page = 'https://example.com/article';
  const result = await unsupported.message(videoOptionsMessage(page, { prefetch: true }), {
    tab: { id: 17, url: page }, url: page, frameId: 0, documentId: 'doc-17'
  });
  assert.equal(result.data.prefetchSkipped, true);
  assert.equal(unsupported.sent.length, 0);
});

test('picker joins an in-flight anonymous prefetch of the exact same player', async () => {
  let complete, started;
  const ready = new Promise(resolve => { started = resolve; });
  const reply = new Promise(resolve => { complete = resolve; });
  const app = load({ 'media-options': () => { started(); return reply; } });
  const warm = app.message(videoOptionsMessage(videoPage, { prefetch: true }), videoSender);
  await ready;
  const click = app.message(videoOptionsMessage(videoPage, { playerFacts: { durationSeconds: 60, height: 720 } }), videoSender);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(app.sent.filter(message => message.type === 'media-options').length, 1);
  complete(mediaOptionsReply);
  assert.deepEqual((await click).data.choices, mediaOptionsReply.data.choices);
  assert.equal((await warm).ok, true);
});

test('prefetch never uses cookies or starts on a hidden, incognito, unbound or paused tab', async () => {
  for (const [sender, extra, localData] of [
    [{ ...videoSender, tab: { ...videoSender.tab, active: false } }, {}, {}],
    [{ ...videoSender, tab: { ...videoSender.tab, incognito: true } }, {}, {}],
    [{ ...videoSender, documentId: undefined }, {}, {}],
    [videoSender, { generation: 0 }, {}],
    [videoSender, {}, { vesperPaused: true }]
  ]) {
    const app = load({ 'media-options': mediaOptionsReply }, { signInPermission: true, localData });
    const result = await app.message(videoOptionsMessage(videoPage, { prefetch: true, ...extra }), sender);
    assert.equal(result?.data?.prefetchSkipped, true);
    assert.equal(app.sent.length, 0);
    assert.equal(app.cookieLookups.length, 0);
  }
});

test('warm formats stay usable beyond thirty seconds, but not beyond the native expiry', async () => {
  const expiresAt = new Date(Date.now() + 70_000).toISOString();
  const reply = { ...mediaOptionsReply, data: { ...mediaOptionsReply.data, formatsExpireAt: expiresAt } };
  const app = load({ 'media-options': reply });
  await app.message(videoOptionsMessage(videoPage, { prefetch: true }), videoSender);
  app.advanceTime(40_000);
  await app.message(videoOptionsMessage(videoPage), videoSender);
  assert.equal(app.sent.length, 1);
  app.advanceTime(26_000);
  await app.message(videoOptionsMessage(videoPage), videoSender);
  assert.equal(app.sent.length, 2);
});

test('a changed video generation cannot reuse an earlier prepared selection', async () => {
  const app = load({ 'media-options': mediaOptionsReply });
  await app.message(videoOptionsMessage(videoPage, { prefetch: true }), videoSender);
  await app.message(videoOptionsMessage(videoPage, { generation: 2 }), videoSender);
  assert.equal(app.sent.length, 2);
});

test('background prefetch obeys site retry deadlines without delaying explicit picker clicks', async () => {
  const retryAt = new Date(Date.now() + 180_000).toISOString();
  const app = load({ 'media-options': { ok: false, code: 'rate-limited', data: { retryAt } } });
  const first = await app.message(videoOptionsMessage(videoPage, { prefetch: true }), videoSender);
  assert.equal(first.code, 'rate-limited');
  app.advanceTime(20_000);
  const other = 'https://www.youtube.com/watch?v=abcdefghijk';
  const sender = { ...videoSender, tab: { id: 18, url: other }, url: other, documentId: 'doc-18' };
  const second = await app.message(videoOptionsMessage(other, { prefetch: true }), sender);
  assert.equal(second.data.prefetchSkipped, true);
  assert.equal(app.sent.length, 1);
  app.advanceTime(161_000);
  await app.message(videoOptionsMessage(other, { prefetch: true }), sender);
  assert.equal(app.sent.length, 2);
});

test('a signed-in picker click gets fresh formats after anonymous prefetch', async () => {
  const app = load({ 'media-options': mediaOptionsReply }, {
    signInPermission: true,
    cookies: [{ name: 'session', value: 'test-value', path: '/' }, { name: 'watch-only', value: 'restricted', path: '/watch' }]
  });
  await app.message(videoOptionsMessage(videoPage, { prefetch: true }), videoSender);
  await app.message(videoOptionsMessage(videoPage), videoSender);
  const requests = app.sent.filter(request => request.type === 'media-options');
  assert.equal(requests.length, 2);
  assert.equal(requests[0].mediaAuth, undefined);
  assert.equal(requests[1].mediaAuth.cookie, 'session=test-value');
});

test('an identified Dailymotion embed loads formats before listing observed streams', async () => {
  const app = load({ 'media-options': mediaOptionsReply });
  const page = 'https://geo.dailymotion.com/player.html?video=x7abc1';
  const sender = { url: page, tab: { id: 7 }, frameId: 2, documentId: 'active-player' };
  const now = Date.now();
  for (const name of ['master', 'alternate']) {
    await app.onMedia({ url: `https://cdn.example/${name}.m3u8`, tabId: 7, frameId: 2,
      documentId: sender.documentId, method: 'GET', statusCode: 200, type: 'xmlhttprequest', timeStamp: now });
  }
  const result = await app.message(videoOptionsMessage(page, {
    playerUrl: 'blob:https://geo.dailymotion.com/player', playerPlaying: true, mediaChangedAt: now
  }), sender);
  assert.equal(app.sent[0].url, page);
  assert.deepEqual(result.data.choices, mediaOptionsReply.data.choices);
  assert.equal(result.data.sourceChoices, undefined);
});

test('prepared formats are reused, refreshed on request and expire quickly', async () => {
  const app = load({ 'media-options': mediaOptionsReply });
  const prefetched = await app.message(videoOptionsMessage(videoPage, { prefetch: true }), videoSender);
  assert.equal(prefetched.ok, true);
  assert.equal(app.sent.filter(message => message.type === 'media-options').length, 1);

  const click = await app.message(videoOptionsMessage(videoPage, { playerFacts: { durationSeconds: 61, height: 720 } }), videoSender);
  assert.deepEqual(click.data.choices, mediaOptionsReply.data.choices);
  assert.equal(app.sent.filter(message => message.type === 'media-options').length, 1);

  await app.message(videoOptionsMessage(videoPage, { refresh: true }), videoSender);
  assert.equal(app.sent.filter(message => message.type === 'media-options').length, 2);

  app.advanceTime(31_000);
  await app.message(videoOptionsMessage(videoPage), videoSender);
  assert.equal(app.sent.filter(message => message.type === 'media-options').length, 3);
});

test('generic player prefetch requires a media URL observed by Chrome', async () => {
  const app = load({ 'media-options': mediaOptionsReply });
  const pageUrl = 'https://example.com/watch';
  const mediaUrl = 'https://media.example/video.mp4';
  const sender = { tab: { id: 22, url: pageUrl }, url: pageUrl, frameId: 0, documentId: 'doc-22' };
  await app.onMedia({ url: mediaUrl, method: 'GET', type: 'media', statusCode: 200, tabId: 22, frameId: 0, documentId: 'doc-22',
    responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }, { name: 'Content-Length', value: '1000' }] });

  await app.message(videoOptionsMessage(pageUrl, { playerUrl: mediaUrl, prefetch: true }), sender);
  const request = app.sent.find(message => message.type === 'media-options');
  assert.equal(request.candidates[0].url, mediaUrl);
});

test('a verified range for the current player is prepared once and reused on click', async () => {
  const page = 'https://example.com/watch/current';
  const url = 'https://media.example/current.webm';
  const sender = { tab: { id: 22, url: page }, url: page, frameId: 0, documentId: 'range-doc' };
  const app = load({ 'media-range-options': mediaOptionsReply });
  const message = videoOptionsMessage(page, { playerUrl: url, playerHasMedia: true,
    playerKey: 'media-1', mediaChangedAt: Date.now(), title: 'Current video' });
  try {
    await app.onMedia({ url, method: 'GET', type: 'media', statusCode: 206, tabId: 22,
      frameId: 0, documentId: sender.documentId, timeStamp: Date.now(),
      responseHeaders: responseHeaders({ 'Content-Type': 'video/webm', 'Content-Range': 'bytes 0-999/700000',
        'Content-Length': '1000', ETag: '"current"' }) });

    const preview = await app.message({ ...message, prefetch: true, mutedLoop: true }, sender);
    assert.equal(preview.data.prefetchSkipped, true);
    assert.equal(app.sent.length, 0);

    const prepared = await app.message({ ...message, prefetch: true }, sender);
    assert.equal(prepared.data.prepared, true);
    const reply = await app.message(message, sender);
    assert.equal(reply.ok, true);
    assert.deepEqual(app.sent.map(request => request.type), ['media-range-options']);
    assert.equal(app.sent[0].candidates[0].observedETag, '"current"');

    await app.message({ ...message, generation: 2 }, sender);
    assert.equal(app.sent.length, 2, 'A recycled player gets a fresh check.');
  } finally { app.stopWorker(); }
});

test('verified playback evidence prepares an unfamiliar stream and reuses the check on click', async () => {
  const page = 'https://example.com/watch/current';
  const manifest = 'https://media.example/asset-481562/master.m3u8';
  const fragment = 'https://media.example/asset-481562/segment.m4s';
  const digest = Buffer.from(await webcrypto.subtle.digest('SHA-256', new TextEncoder().encode(fragment))).toString('hex');
  const sender = { tab: { id: 22, url: page }, url: page, frameId: 0, documentId: 'stream-doc' };
  const app = load({ 'media-source-options': { ...mediaOptionsReply, data: { ...mediaOptionsReply.data, playbackMatches: 1 } } });
  const message = videoOptionsMessage(page, { playerUrl: 'blob:https://example.com/current', playerHasMedia: true,
    playerKey: 'media-1', mediaChangedAt: Date.now(), title: 'Current stream',
    playerResourceFingerprints: [digest], playerFragmentUrls: [fragment] });
  try {
    await app.onMedia({ url: manifest, method: 'GET', type: 'xmlhttprequest', statusCode: 200, tabId: 22,
      frameId: 0, documentId: sender.documentId, timeStamp: Date.now(),
      responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }] });

    const skipped = await app.message({ ...message, prefetch: true, playerResourceFingerprints: [] }, sender);
    assert.equal(skipped.data?.prefetchSkipped, true);
    assert.equal(app.sent.length, 0);

    const prepared = await app.message({ ...message, prefetch: true }, sender);
    assert.deepEqual(structuredClone(prepared.data), { prepared: true });
    assert.deepEqual(app.sent.map(request => request.type), ['media-source-options']);
    assert.equal(app.sent[0].candidates[0].url, manifest);
    const opened = await app.message(message, sender);
    assert.equal(opened.ok, true);
    assert.equal(app.sent.length, 1, 'The click reuses the successful verification.');

    assert.equal((await app.message({ ...message, generation: 2 }, sender)).ok, true);
    assert.equal(app.sent.length, 2, 'A new player generation gets its own check.');
  } finally { app.stopWorker(); }
});

test('unconfirmed signed submissions stay in session storage and expire', async () => {
  const url = 'https://cdn.example/video.mp4?sig=short-lived-token';
  const app = load({ submit: { ok: false, code: 'host-error', message: 'VESPER did not answer.' } });
  await app.message({ type: 'submit', pageUrl: 'https://example/page', items: [{ url }] });

  assert.equal((await app.local.get('pending')).pending, undefined);
  assert.ok(JSON.stringify(app.sessionData.pending).includes(url));
  const id = app.sent[0].id;
  assert.equal(app.sessionData.pending[id].request.authorized, false);

  app.advanceTime(31 * 60_000);
  await app.message({ type: 'tab-state', tabId: 1 });
  assert.deepEqual(app.sessionData.pending, {});
});

test('legacy persistent pending entries migrate without cookie headers', async () => {
  const url = 'https://cdn.example/video.mp4?sig=legacy-token';
  const localData = { pending: { old: { at: Date.now(), request: { id: 'old', authorized: true, items: [{ url, cookie: 'sid=secret' }] } } } };
  const app = load({}, { localData });
  await app.evaluate('pendingReady');

  assert.equal((await app.local.get('pending')).pending, undefined);
  assert.equal(app.sessionData.pending.old.request.authorized, false);
  assert.equal(app.sessionData.pending.old.request.items[0].cookie, undefined);
  assert.equal(app.sessionData.pending.old.request.items[0].url, url);
});

const cookieJar = [
  { name: 'strict', value: 's', sameSite: 'strict' }, { name: 'lax', value: 'l', sameSite: 'lax' },
  { name: 'none', value: 'n', sameSite: 'no_restriction' }, { name: 'plain', value: 'p', sameSite: 'unspecified' }
];
const everyCookie = 'strict=s; lax=l; none=n; plain=p';
const withoutStrict = 'lax=l; none=n; plain=p';

test('a link on another site never carries SameSite=Strict cookies to VESPER', async () => {
  const cases = [
    [{ pageUrl: 'https://bank.example/home' }, everyCookie],                       // same site
    [{ pageUrl: 'https://www.bank.example/home' }, withoutStrict],                 // exact-host fallback withholds across subdomains
    [{ pageUrl: 'https://forum.example/thread' }, withoutStrict],                  // another site
    [{ pageUrl: 'https://evilbank.example/' }, withoutStrict],                     // look-alike host
    [{ pageUrl: 'https://bank.example.evil.test/' }, withoutStrict],
    [{ pageUrl: 'http://bank.example/home' }, withoutStrict],                      // same host over plain http
    [{ pageUrl: 'https://example/' }, withoutStrict],                              // a bare suffix is not a parent site
    [{ pageUrl: 'https://bank.example/home', frameUrl: 'https://ads.example/frame' }, withoutStrict],   // the link sits in a foreign frame
    [{}, withoutStrict]                                                            // unknown origin is treated as another site
  ];
  for (const [where, expected] of cases) {
    const app = load({ submit: { ok: true } }, { signInPermission: true, prefs: { useSignIn: true },
      localData: { signInHosts: ['bank.example'] }, cookies: cookieJar });
    await app.onContextClicked({ menuItemId: 'vesper-download', linkUrl: 'https://bank.example/export.csv', ...where }, undefined);
    assert.equal(app.sent[0].items[0].cookie, expected, JSON.stringify(where));
    assert.equal(app.sent[0].authorized, true);
  }
});

test('sibling subdomains are treated as different sites, which can only withhold a cookie', async () => {
  const app = load({ submit: { ok: true } }, { signInPermission: true, prefs: { useSignIn: true },
    localData: { signInHosts: ['alice.pages.example'] }, cookies: cookieJar });
  await app.onContextClicked({ menuItemId: 'vesper-download', linkUrl: 'https://alice.pages.example/file.zip', pageUrl: 'https://mallory.pages.example/' }, undefined);
  assert.equal(app.sent[0].items[0].cookie, withoutStrict);
});

test('public-suffix tenant hosts do not receive each other’s Strict cookies', async () => {
  const app = load({ submit: { ok: true } }, { signInPermission: true, prefs: { useSignIn: true },
    localData: { signInHosts: ['user.github.io'] }, cookies: cookieJar });
  await app.onContextClicked({ menuItemId: 'vesper-download', linkUrl: 'https://user.github.io/export.zip', pageUrl: 'https://github.io/' }, undefined);
  assert.equal(app.sent[0].items[0].cookie, withoutStrict);
});

test('an automatic handoff sends only the cookies Chrome would send for that download', async () => {
  for (const [referrer, expected] of [['https://files.example/list', everyCookie], ['https://other.example/', withoutStrict], [undefined, withoutStrict]]) {
    const file = { ...activeDownload(), url: 'https://files.example/archive.zip', referrer };
    const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') },
      { download: file, signInPermission: true, prefs: { useSignIn: true },
        localData: { signInHosts: ['files.example'] }, cookies: cookieJar });
    await observe(app, file);
    await app.onCreated(file);
    assert.equal(app.sent[0].items[0].cookie, expected, String(referrer));
  }
});

test('with sign-in off no cookie is read or sent', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file, signInPermission: true, prefs: { useSignIn: false }, cookies: cookieJar });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(app.cookieLookups.length, 0);
  assert.equal(app.sent[0].items[0].cookie, undefined);
  assert.equal(app.sent[0].authorized, false);
});

// ---------------------------------------------------------------- matching Chrome's download to its response

const responseHeaders = headers => Object.entries(headers).map(([name, value]) => ({ name, value }));
const zipHeaders = { 'Content-Length': '1000', ETag: '"version-1"', 'Content-Type': 'application/zip', 'Content-Disposition': 'attachment' };
const decisions = app => app.sessionData.vesperCaptureDecisions || [];
const outcomeOf = app => decisions(app)[0]?.outcome;

test('a link with a #fragment matches the response Chrome reported without it', async () => {
  const file = { ...activeDownload(), url: 'https://example.com/files/archive.zip#mirror-2' };
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file });
  await app.onHeaders({ url: 'https://example.com/files/archive.zip', requestId: 'r1', method: 'GET', type: 'main_frame',
    statusCode: 200, responseHeaders: responseHeaders(zipHeaders) });
  await app.onCreated(file);
  assert.equal(app.sent[0].type, 'browser-capture');
  assert.equal(app.sent[0].items[0].url, 'https://example.com/files/archive.zip');   // a fragment is never sent to a server
  assert.equal(file.paused, true);
  assert.equal(outcomeOf(app), 'handoff-pending');
});

test('a redirected download with a #fragment is matched when its response arrives late', async () => {
  const file = { ...activeDownload(), url: 'https://example.com/get/42#top', finalUrl: 'https://cdn.example.net/files/archive.zip#top' };
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file });
  await app.onCreated(file);
  assert.equal(app.sent.length, 0);
  assert.equal(outcomeOf(app), 'unseen');
  await app.onRedirect({ url: 'https://example.com/get/42', redirectUrl: 'https://cdn.example.net/files/archive.zip', requestId: 'r2', method: 'GET', type: 'main_frame' });
  await app.onHeaders({ url: 'https://cdn.example.net/files/archive.zip', requestId: 'r2', method: 'GET', type: 'main_frame',
    statusCode: 200, responseHeaders: responseHeaders(zipHeaders) });
  await waitFor(() => outcomeOf(app) === 'handoff-pending', 'the late response should start the handoff');
  assert.equal(app.sent[0].items[0].url, 'https://cdn.example.net/files/archive.zip');
  assert.equal(file.paused, true);
});

test('a size Chrome reports as 0 or -1 is unknown, not proof; a strong ETag still identifies the file', async () => {
  for (const unknown of [0, -1]) {
    const file = { ...activeDownload(), fileSize: unknown, totalBytes: unknown };
    const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file });
    await app.onHeaders({ url: file.url, requestId: 'r3', method: 'GET', type: 'main_frame', statusCode: 200,
      responseHeaders: responseHeaders({ ETag: '"build-9"', 'Content-Type': 'application/zip', 'Content-Disposition': 'attachment' }) });
    await app.onCreated(file);
    assert.equal(app.sent[0].browserProof.length, undefined, 'no invented length');
    assert.equal(app.sent[0].browserProof.etag, '"build-9"');
    assert.equal(file.paused, true);
  }
});

test('Chrome keeps a file with no strong ETag even after its size is known', async () => {
  const file = { ...activeDownload(), fileSize: 0, totalBytes: 0 };
  const app = load({ 'browser-submit': started, 'browser-transfer': transfer('active') }, { download: file });
  await app.onHeaders({ url: file.url, requestId: 'r4', method: 'GET', type: 'main_frame', statusCode: 200,
    responseHeaders: responseHeaders({ ETag: 'W/"weak"', 'Content-Type': 'application/zip', 'Content-Disposition': 'attachment' }) });
  await app.onCreated(file);
  assert.equal(app.sent.length, 0);
  assert.equal(outcomeOf(app), 'no-validator');

  file.fileSize = file.totalBytes = 4096;
  await app.onChanged({ id: file.id, fileSize: { previous: 0, current: 4096 } });
  assert.equal(app.sent.length, 0);
  assert.equal(file.paused, false);
  assert.equal(outcomeOf(app), 'no-validator');
});

// ---------------------------------------------------------------- telling the user why Chrome kept a download (VES-021)

test('each way a handoff can fail is recorded with its own reason and leaves Chrome downloading', async () => {
  const notReady = (transferReason, status = 'started') => ({ ok: true, results: [{ status, transferReady: false, transferReason }] });
  const signedIn = { signInPermission: true, prefs: { useSignIn: true } };
  const cases = [
    ['sign-in', notReady('auth'), {}],
    ['site-refused', notReady('forbidden'), {}],
    ['site-refused', notReady('forbidden'), signedIn],
    ['different-signed-out', notReady('browser-fallback'), {}],
    ['different', notReady('browser-fallback'), signedIn],
    ['queued', notReady('queued', 'queued'), {}],
    ['queued', { ok: true, results: [{ status: 'queued' }] }, {}],
    ['busy', notReady('busy'), {}],
    ['slow', notReady('slow'), {}],
    ['not-ready', notReady(undefined), {}],
    ['not-ready', notReady('some-new-reason'), {}],
    ['app-missing', { ok: false, code: 'host-missing', message: 'Specified native messaging host not found.' }, {}],
    ['app-unavailable', { ok: false, code: 'app-unavailable', message: 'The desktop connection is unavailable.' }, {}],
    ['app-unavailable', { ok: false, code: 'timeout', message: 'VESPER did not answer in time.' }, {}],
    ['duplicate', { ok: false, code: 'duplicate', message: 'Already in the list.' }, {}],
    ['refused', { ok: false, code: 'rejected', message: 'That address is on a private network.' }, {}]
  ];
  for (const [expected, reply, options] of cases) {
    const file = activeDownload();
    const app = load({ 'browser-submit': reply, 'browser-transfer': transfer('failed') }, { download: file, ...options });
    await observe(app, file);
    await app.onCreated(file);
    assert.equal(outcomeOf(app), expected, JSON.stringify(reply));
    assert.equal(file.paused, false, 'Chrome keeps downloading');
    assert.equal(app.actions.includes('pause'), false);
    assert.equal(decisions(app).length, 1);
  }
});

test('an unexpected automatic capture error is explained without changing Chrome ownership', async () => {
  const file = activeDownload();
  const app = load({}, { download: file, failDownloadSearch: true });
  await observe(app, file);
  await app.onCreated(file);

  assert.equal(outcomeOf(app), 'capture-error');
  assert.deepEqual(Object.keys(decisions(app)[0]).sort(), ['at', 'id', 'name', 'outcome', 'site']);
  assert.equal(file.paused, false);
  assert.equal(app.actions.includes('pause'), false);
  assert.equal(app.sent.length, 0);
});

test('downloads VESPER must not repeat are explained without contacting VESPER', async () => {
  const posted = activeDownload();
  let app = load({}, { download: posted });
  await observe(app, posted, { method: 'POST' });
  await app.onCreated(posted);
  assert.equal(outcomeOf(app), 'form');

  const compressed = activeDownload();
  app = load({}, { download: compressed });
  await app.onHeaders({ url: compressed.url, requestId: 'r5', method: 'GET', type: 'main_frame', statusCode: 200,
    responseHeaders: responseHeaders({ ...zipHeaders, 'Content-Encoding': 'gzip' }) });
  await app.onCreated(compressed);
  assert.equal(outcomeOf(app), 'compressed');

  const blob = { ...activeDownload(), url: 'blob:https://app.example/6f1c-report', filename: 'C:\\Downloads\\report.xlsx' };
  app = load({}, { download: blob });
  await app.onCreated(blob);
  assert.equal(outcomeOf(app), 'page-made');
  assert.equal(decisions(app)[0].name, 'report.xlsx');
  assert.equal(app.sent.length, 0);
});

test('a small file Chrome finishes first is explained, not reported as a failure', async () => {
  const file = activeDownload();
  const app = load({ 'browser-submit': () => { file.state = 'complete'; return started; }, 'browser-transfer': transfer('failed') }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  assert.equal(outcomeOf(app), 'chrome-finished');
  assert.equal(app.actions.includes('pause'), false);
});

test('the decision list keeps a name and a site, never a link, and nothing from Incognito', async () => {
  const file = { ...activeDownload(), url: 'https://cdn.example.net/get/annual%20report.pdf?token=SECRET-TOKEN&user=42#page=3', filename: '' };
  const app = load({ 'browser-submit': { ok: false, code: 'host-missing' } }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  const [entry] = decisions(app);
  assert.deepEqual(Object.keys(entry).sort(), ['at', 'id', 'name', 'outcome', 'site']);
  assert.equal(entry.name, 'annual report.pdf');
  assert.equal(entry.site, 'cdn.example.net');
  assert.doesNotMatch(JSON.stringify(decisions(app)), /SECRET|token|https?:|user=42/);

  const hidden = { ...activeDownload(), incognito: true };
  const incognito = load({ 'browser-submit': started }, { download: hidden });
  await observe(incognito, hidden);
  await incognito.onCreated(hidden);
  assert.deepEqual(decisions(incognito), []);
  assert.equal(incognito.sent.length, 0);
});

test('nothing is recorded when the user turned capture off or keeps Chrome’s connection', async () => {
  for (const prefs of [{ intercept: false }, { route: 'chrome' }]) {
    const file = activeDownload();
    const app = load({ 'browser-submit': started }, { download: file, prefs });
    await app.onCreated(file);                      // would be "unseen"
    await observe(app, file, { method: 'POST' });   // would be "form"
    await app.onChanged({ id: file.id, totalBytes: { current: 1000 } });
    assert.deepEqual(decisions(app), [], JSON.stringify(prefs));
    assert.equal(app.sent.length, 0);
    assert.equal(app.sessionData.vesperDownloadCapture, undefined,
      'disabled capture must not retain download URLs or response evidence');
  }
});

test('Pause clears pending URL and response evidence', async () => {
  const file = activeDownload();
  const app = load({}, { download: file });
  await app.onCreated(file);
  assert.equal(app.sessionData.vesperDownloadCapture?.awaiting?.[0]?.[1]?.url, file.url);
  assert.ok(app.sessionData.vesperCaptureDecisions?.length);
  await app.local.set({ vesperPaused: true });

  assert.equal(app.sessionData.vesperDownloadCapture, undefined);
  assert.equal(app.sessionData.vesperCaptureDecisions, undefined);
  await observe(app, file);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(app.sessionData.vesperDownloadCapture, undefined);
});

test('legacy modes migrate once and keep other preferences and a later resume', async () => {
  for (const [legacy, localPause, paused] of [
    [{ intercept: true, route: 'auto', handoff: 'download' }, false, false],
    [{ intercept: false }, false, true],
    [{ route: 'chrome' }, false, true],
    [{ handoff: 'queue' }, false, true],
    [{ intercept: true }, true, true]
  ]) {
    const syncData = { ...legacy, theme: 'dark', useSignIn: false };
    const app = load({}, { syncData, localData: { vesperPaused: localPause, prefetchFormats: false, vesperSeen: true } });
    await app.evaluate('browserPreferencesReady');
    assert.equal(app.localData.vesperPaused, paused);
    assert.equal(app.localData.vesperPauseMigrated, true);
    for (const key of ['intercept', 'route', 'handoff']) assert.equal(Object.hasOwn(syncData, key), false);
    for (const key of ['prefetchFormats', 'vesperSeen']) assert.equal(Object.hasOwn(app.localData, key), false);
    assert.equal(syncData.theme, 'dark');
    assert.equal(syncData.useSignIn, false);
    await app.local.set({ vesperPaused: false });
    const restarted = load({}, { syncData, localData: app.localData });
    await restarted.evaluate('browserPreferencesReady');
    assert.equal(restarted.localData.vesperPaused, false);
    if (!localPause && paused) {
      const otherProfile = load({}, { syncData });
      await otherProfile.evaluate('browserPreferencesReady');
      assert.equal(otherProfile.localData.vesperPaused, true);
    }
  }
});

test('Pause preserves an active release record until native stop is confirmed', async () => {
  const file = activeDownload();
  let reply = { ok: false, code: 'host-unavailable' };
  const localData = { browserDownloads: {
    [file.id]: { startTime: file.startTime, transferId: 'browser-transfer-releasing', sourceHash: 'b'.repeat(64),
      attempts: 3, stage: 'releasing', at: Date.now() }
  } };
  const app = load({ 'browser-transfer': () => reply }, { download: file, localData });

  await app.local.set({ vesperPaused: true });
  await app.recover();
  assert.equal(app.localData.browserDownloads[file.id].stage, 'releasing');
  assert.equal(app.localData.browserDownloads[file.id].transferId, 'browser-transfer-releasing');

  reply = transfer('failed');
  await app.recover();
  assert.equal(app.localData.browserDownloads[file.id], undefined);
});

test('legacy Chrome ownership survives an upgrade and a completed native copy', async () => {
  const file = { ...activeDownload(), paused: true };
  let reply = { ok: false, code: 'host-unavailable' };
  const localData = { browserDownloads: {
    [file.id]: { startTime: file.startTime, transferId: 'browser-transfer-parked', stage: 'releasing', preserveChrome: true, resumeChrome: true, at: Date.now() }
  } };
  const app = load({ 'browser-transfer': () => reply }, { download: file, localData });

  await app.recover();

  const releasing = app.localData.browserDownloads[file.id];
  assert.equal(releasing.stage, 'releasing');
  assert.equal(releasing.transferId, 'browser-transfer-parked');
  assert.equal(releasing.startTime, file.startTime);
  assert.equal(releasing.resumeChrome, true);
  assert.equal(releasing.preserveChrome, true);
  assert.equal(releasing.sourceHash, undefined);
  assert.equal(releasing.attempts, undefined);
  assert.equal(releasing.retryAt, undefined);
  assert.equal(file.paused, true, 'Chrome stays parked until native stop is confirmed.');
  assert.equal(app.actions.includes('cancel'), false);
  assert.equal(app.actions.includes('erase'), false);

  reply = transfer('complete');
  await app.recover();

  assert.equal(app.localData.browserDownloads[file.id], undefined);
  assert.equal(file.state, 'in_progress');
  assert.equal(file.paused, false, 'Chrome retains the download after upgrade.');
  assert.equal(app.actions.includes('cancel'), false);
  assert.equal(app.actions.includes('erase'), false);
});

test('legacy release resumes Chrome after native stop even when bytes arrived while parked', async () => {
  const file = { ...activeDownload(), paused: true };
  let finishAbandon;
  const abandon = new Promise(resolve => { finishAbandon = resolve; });
  const localData = { browserDownloads: {
    [file.id]: { startTime: file.startTime, transferId: 'browser-transfer-byte-race', stage: 'releasing', preserveChrome: true, resumeChrome: true, at: Date.now() }
  } };
  const app = load({ 'browser-transfer': message =>
    message.operation === 'abandon' ? abandon : transfer('active') }, { download: file, localData });

  const recovering = app.recover();
  await waitFor(() => app.sent.some(message => message.type === 'browser-transfer' && message.operation === 'abandon'));
  file.bytesReceived = 128;
  finishAbandon(transfer('failed'));
  await recovering;

  assert.equal(file.bytesReceived, 128);
  assert.equal(file.state, 'in_progress');
  assert.equal(file.paused, false);
  assert.equal(app.actions.includes('resume'), true);
  assert.equal(app.actions.includes('cancel'), false);
  assert.equal(app.actions.includes('erase'), false);
  assert.equal(app.localData.browserDownloads?.[file.id], undefined);
});

test('the decision list survives a worker restart, holds 20 entries and keeps one per download', async () => {
  const sessionData = {};
  for (let id = 1; id <= 23; id++) {
    const file = { ...activeDownload(), id, url: `https://example.com/files/file-${id}.zip` };
    const worker = load({ 'browser-submit': { ok: false, code: 'host-missing' } }, { download: file, sessionData });   // a fresh worker each time
    await observe(worker, file, { requestId: `request-${id}` });
    await worker.onCreated(file);
    await worker.onChanged({ id, totalBytes: { current: 1000 } });   // a second look at the same download
  }
  const list = sessionData.vesperCaptureDecisions;
  assert.equal(list.length, 20);
  assert.deepEqual(list.slice(0, 3).map(entry => entry.id), [23, 22, 21]);
  assert.equal(new Set(list.map(entry => entry.id)).size, 20);
});

// ---------------------------------------------------------------- right-clicking a player-made video (issue 5)

test('the download action opens Find media for the clicked YouTube Mix card', async () => {
  const page = 'https://www.youtube.com/watch?v=abcdefghijk&list=RDabcdefghijk';
  const tab = { id: 9, url: 'https://www.youtube.com/' };
  const app = load({ 'media-picker': { ok: true } });
  for (const media of [
    {}, { mediaType: 'video', srcUrl: 'blob:https://www.youtube.com/preview' }, { mediaType: 'video' }
  ]) {
    await app.onContextClicked({ menuItemId: 'vesper-download', linkUrl: page, pageUrl: tab.url, ...media }, tab);
    assert.equal(app.sent.at(-1).type, 'media-picker');
    assert.equal(app.sent.at(-1).url, page);
  }
  assert.equal(app.sent.length, 3);
});

test('the clicked TikTok card takes precedence over its temporary preview file', async () => {
  const page = 'https://www.tiktok.com/@owner/video/12345';
  const tab = { id: 9, url: 'https://www.tiktok.com/' };
  const app = load({ 'media-picker': { ok: true } });
  await app.onContextClicked({ menuItemId: 'vesper-download', mediaType: 'video', linkUrl: page,
    srcUrl: 'https://cdn.example/preview.mp4', pageUrl: tab.url }, tab);
  assert.equal(app.sent[0].type, 'media-picker');
  assert.equal(app.sent[0].url, page);
});

test('an unavailable generated file explains the failure in the clicked frame', async () => {
  const app = load();
  await app.onContextClicked({ menuItemId: 'vesper-download', linkUrl: 'blob:https://example.com/item', frameId: 2 }, { id: 3 });
  assert.equal(app.sent.length, 0);
  const notice = app.tabMessages.at(-1);
  assert.equal(notice.options.frameId, 2);
  assert.equal(notice.message.type, 'vesper-context-notice');
  assert.match(notice.message.reply.message, /browser file is no longer available/);
});

test('right-clicking a blob video opens the media picker for the page instead of failing silently', async () => {
  const app = load({ 'media-picker': { ok: true } });
  const tab = { id: 9, url: 'https://video.example/watch?v=abc' };
  await app.onContextClicked({ menuItemId: 'vesper-download', mediaType: 'video', srcUrl: 'blob:https://video.example/6f1c-uuid', pageUrl: tab.url }, tab);
  assert.equal(app.sent.length, 1);
  assert.equal(app.sent[0].type, 'media-picker');
  assert.equal(app.sent[0].url, tab.url);
  assert.equal([...app.badges].at(-1).text, '✓');
});

test('right-clicking an ordinary video still downloads it directly', async () => {
  const app = load({ submit: { ok: true } });
  const tab = { id: 9, url: 'https://cdn.example/page' };
  await app.onContextClicked({ menuItemId: 'vesper-download', mediaType: 'video', srcUrl: 'https://cdn.example/clip.mp4', pageUrl: tab.url }, tab);
  assert.equal(app.sent[0].type, 'submit');
  assert.equal(app.sent[0].items[0].url, 'https://cdn.example/clip.mp4');
});

test('right-clicking a blob video in a frame uses that frame for discovery and sign-in', async () => {
  const frameUrl = 'https://player.example/embed';
  const app = load({ 'media-picker': { ok: true } }, {
    prefs: { useSignIn: true }, signInPermission: true,
    localData: { signInHosts: ['player.example'] },
    cookies: [{ name: 'session', value: 'authorized', sameSite: 'lax', path: '/' }]
  });
  const tab = { id: 9, url: 'https://video.example/watch' };
  await app.onContextClicked({ menuItemId: 'vesper-download', mediaType: 'video',
    srcUrl: 'blob:https://player.example/video', pageUrl: tab.url, frameUrl, frameId: 2 }, tab);

  assert.equal(app.sent.length, 1);
  assert.equal(app.sent[0].type, 'media-picker');
  assert.equal(app.sent[0].url, frameUrl);
  assert.equal(app.sent[0].authorized, true);
  assert.equal(app.sent[0].mediaAuth.cookie, 'session=authorized');
  assert.equal(app.sent.some(message => message.type === 'submit'), false);
  assert.equal(app.tabMessages[0].message.type, 'vesper-open-picker');
});

test('the download action opens the existing picker for a blob-backed player', async () => {
  const app = load({}, { playerPicker: true });
  const tab = { id: 11, url: 'https://video.example/watch' };

  await app.onContextClicked({ menuItemId: 'vesper-download', mediaType: 'video',
    srcUrl: 'blob:https://video.example/watch', pageUrl: tab.url, frameUrl: tab.url, frameId: 0 }, tab);

  assert.equal(app.tabMessages[0].message.type, 'vesper-open-picker');
  assert.equal(app.tabMessages[0].options.frameId, 0);
  assert.equal(app.sent.length, 0);
});

test('Smart Select and Download use separate Chrome right-click contexts', async () => {
  const app = load();
  await app.install();
  const registered = app.contextMenuItems.map(menu => [menu.id, Array.from(menu.contexts).join(',')]);
  assert.deepEqual(registered, [
    ['vesper-smart', 'page'], ['vesper-download', 'link,video,audio'], ['vesper-transfers', 'action']
  ]);
});

test('right-clicking a blob video on a page that cannot be inspected says so', async () => {
  const app = load({ 'media-picker': { ok: true } });
  await app.onContextClicked({ menuItemId: 'vesper-download', mediaType: 'video', srcUrl: 'blob:file:///tmp/x', pageUrl: 'file:///tmp/page.html' },
    { id: 9, url: 'file:///tmp/page.html' });
  assert.equal(app.sent.length, 0);
  assert.equal([...app.badges].at(-1).text, '!');
});

// ---------------------------------------------------------------- ownership (issue 4)

test('a download that finishes in Chrome before any handoff is left entirely alone', async () => {
  const file = { ...activeDownload(), state: 'complete' };
  const app = load({ 'browser-submit': started }, { download: file });
  await observe(app, file);
  await app.onCreated(file);
  await app.onChanged({ id: file.id, state: { current: 'complete' } });
  assert.equal(app.sent.length, 0);
  assert.deepEqual(app.actions, []);
});

test('media responses no longer wake the download-capture listeners', async () => {
  // The capture listeners register with document types only (VES-020).
  const source = readFileSync(new URL('../../extension/browser-downloads.js', import.meta.url), 'utf8');
  assert.match(source, /DOWNLOAD_TYPES = \['main_frame', 'sub_frame', 'other'\]/);
  assert.doesNotMatch(source.split('DOWNLOAD_TYPES')[0], /'xmlhttprequest'/);
});

// ---------------------------------------------------------------- "Download part" from the extension (VES-023)

test('a media selection carries its time range to VESPER and refuses an impossible one', async () => {
  const session = 'a'.repeat(32);
  const app = load({ 'media-select': { ok: true }, 'media-options': { ok: true,
    data: { mediaSession: session, choices: [{ index: 1, label: '720p' }] } } });
  const scope = { pageUrl: contentScript.url, playerPageUrl: contentScript.url, playerUrl: '', playerKey: 'player-1', generation: 1 };
  assert.equal((await app.message({ type: 'media-options', ...scope }, contentScript)).ok, true);
  const base = { type: 'media-select', mediaSession: session, formatChoice: 1, mode: 'download', ...scope };
  const ok = await app.message({ ...base, range: { start: 72, end: 102 } }, contentScript);
  assert.equal(ok.ok, true);
  assert.deepEqual(structuredClone(app.sent.at(-1).range), { start: 72, end: 102 });
  assert.notEqual(app.sent.at(-1).id, `${session}-1-download`, 'a clipped request has its own id');
  for (const range of [{ start: 10, end: 5 }, { start: -1, end: 5 }, { start: 0, end: Infinity }, { start: 'a', end: 5 }])
    assert.equal((await app.message({ ...base, range }, contentScript)).code, 'invalid');
  const plain = await app.message(base, contentScript);
  assert.equal(plain.ok, true);
  assert.equal(app.sent.at(-1).range, undefined);
});

test('unrelated Chrome capture recovery does not block a scoped media picker or selection', async () => {
  const token = 'e'.repeat(32);
  const app = load({ 'media-options': { ok: true, data: { mediaSession: token, choices: [{ index: 0 }] } },
    'media-select': { ok: true } });
  app.evaluate('captureRecovery = new Promise(resolve => { globalThis.finishQaRecovery = resolve; })');
  let timer, managedFinished = false;
  const managed = app.message({ type: 'managed-downloads' }).then(reply => { managedFinished = true; return reply; });
  const scope = { pageUrl: videoPage, playerPageUrl: videoPage, playerKey: 'player-1', generation: 1, playerUrl: '' };
  const select = { type: 'media-select', mediaSession: token, formatChoice: 0, mode: 'download', ...scope };
  try {
    const foreground = (async () => {
      assert.equal((await app.message({ type: 'media-options', ...scope }, videoSender)).ok, true);
      assert.equal((await app.message({ ...select, generation: 2 }, videoSender)).code, 'expired-selection');
      assert.equal((await app.message(select, videoSender)).ok, true);
    })();
    await Promise.race([foreground, new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error('Media operations waited for unrelated Chrome recovery')), 1000);
    })]);
    assert.equal(managedFinished, false);
    assert.deepEqual(app.sent.map(m => m.type), ['media-options', 'media-select']);
  } finally {
    clearTimeout(timer);
    app.evaluate('finishQaRecovery()');
    await managed;
    app.stopWorker();
  }
});

test('native format sessions are bound to the requesting document, player and generation', async () => {
  const token = 'b'.repeat(32);
  const app = load({ 'media-options': { ok: true, data: { mediaSession: token, choices: [{ index: 0 }] } },
    'media-select': { ok: true } });
  const scope = { pageUrl: videoPage, playerPageUrl: videoPage, playerKey: 'player-1', generation: 1, playerUrl: '' };
  const select = { type: 'media-select', mediaSession: token, formatChoice: 0, mode: 'download', ...scope };
  assert.equal((await app.message(select, videoSender)).code, 'expired-selection');
  assert.equal((await app.message({ type: 'media-options', ...scope }, videoSender)).ok, true);
  for (const [changes, sender] of [
    [{ playerKey: 'player-2' }, videoSender],
    [{ generation: 2 }, videoSender],
    [{ playerUrl: 'https://example.com/other.mp4' }, videoSender],
    [{ pageUrl: 'https://www.youtube.com/watch?v=abcdefghijk' }, videoSender],
    [{}, { ...videoSender, documentId: 'other-document' }],
    [{}, { ...videoSender, frameId: 1 }],
    [{}, { ...videoSender, tab: { ...videoSender.tab, id: 99 } }]
  ]) assert.equal((await app.message({ ...select, ...changes }, sender)).code, 'expired-selection');
  assert.equal(app.sent.filter(r => r.type === 'media-select').length, 0);
  assert.equal((await app.message(select, videoSender)).ok, true);
  assert.equal((await app.message(select, videoSender)).ok, true);
  assert.equal(app.sent.at(-1).id, app.sent.at(-2).id);
});

test('prefetch alone cannot establish a native download selection', async () => {
  const token = 'c'.repeat(32);
  const app = load({ 'media-options': { ok: true, data: { mediaSession: token, choices: [{ index: 0 }] } },
    'media-select': { ok: true } });
  const scope = { pageUrl: videoPage, playerPageUrl: videoPage, playerKey: 'player-1', generation: 1, playerUrl: '' };
  const select = { type: 'media-select', mediaSession: token, formatChoice: 0, mode: 'download', ...scope };
  assert.equal((await app.message({ type: 'media-options', prefetch: true, ...scope }, videoSender)).ok, true);
  assert.equal((await app.message(select, videoSender)).code, 'expired-selection');
  assert.equal((await app.message({ type: 'media-options', ...scope }, videoSender)).ok, true);
  assert.equal((await app.message(select, videoSender)).ok, true);
});

test('scoped format sessions survive worker restart without retaining raw player URLs', async () => {
  const sessionData = {};
  const token = 'd'.repeat(32);
  const replies = { 'media-options': { ok: true, data: { mediaSession: token, choices: [{ index: 0 }] } },
    'media-select': { ok: true } };
  const app = load(replies, { sessionData });
  const scope = { pageUrl: videoPage, playerPageUrl: videoPage, playerKey: 'player-1', generation: 1, playerUrl: 'blob:https://www.youtube.com/secret-test-source' };
  assert.equal((await app.message({ type: 'media-options', ...scope }, videoSender)).ok, true);
  assert.ok(sessionData.vesperFormatSessions.length === 1);
  assert.equal(JSON.stringify(sessionData.vesperFormatSessions).includes('secret-test-source'), false);
  const restarted = load(replies, { sessionData });
  const select = { type: 'media-select', mediaSession: token, formatChoice: 0, mode: 'download', ...scope };
  assert.equal((await restarted.message(select, videoSender)).ok, true);
  restarted.advanceTime(601_000);
  assert.equal((await restarted.message(select, videoSender)).code, 'expired-selection');
});


test('Smart Select from a content script cannot bypass native review or choose a destination', async () => {
  const app = load({ 'batch-review': { ok: true, results: [] } });
  const reply = await app.message({ type: 'smart-submit', mode: 'download', destination: 'C:\\Windows',
    items: [{ url: 'https://files.example/a.zip', sha256: 'ab'.repeat(32), reviewLabel: 'Archive', reviewType: 'ZIP archive' },
      { url: 'https://files.example/a.zip' }] },
    { tab: { id: 7, url: 'https://page.example/list' }, frameId: 0, documentId: 'doc' });
  assert.equal(reply.ok, true);
  const request = app.sent.find(value => value.type === 'batch-review');
  assert.equal(request.mode, 'ask');
  assert.equal(request.items.length, 1);
  assert.equal(request.items[0].sha256, 'AB'.repeat(32));
  assert.equal(request.items[0].reviewLabel, 'Archive');
  assert.equal(request.items[0].reviewType, 'ZIP archive');
  assert.equal(request.items[0].fileName, undefined);
  assert.equal(request.destination, undefined);
});

test('media track intents get safe distinct idempotency keys', async () => {
  const app = load({ 'media-select': { ok: true } });
  const base = { type: 'media-select', mediaSession: 'edc1604be3d34136a798fca8addbcbcd', formatChoice: 1, mode: 'queue' };
  await app.message({ ...base, audioTrack: 'en.original', subtitles: ['en', 'es'] });
  await app.message({ ...base, audioTrack: 'es.dubbed', subtitles: ['en', 'es'] });
  await app.message({ ...base, audioTrack: 'en.original', subtitles: ['es', 'en'] });
  await app.message({ ...base, audioTrack: 'en.original', subtitles: ['en', 'es'], embedSubtitles: true });
  const requests = app.sent.filter(value => value.type === 'media-select');
  assert.notEqual(requests[0].id, requests[1].id);
  assert.equal(requests[0].id, requests[2].id);
  assert.notEqual(requests[0].id, requests[3].id);
  assert.equal(requests[0].embedSubtitles, false);
  assert.equal(requests[3].embedSubtitles, true);
  assert.match(requests[0].id, /^[a-z0-9-]+$/i);
  assert.ok(requests[0].id.length <= 120);
});

test('CHIPS lookup uses the browser-stamped frame partition without serializing cookie reads', async () => {
  const app = load({}, { signInPermission: true, prefs: { useSignIn: true } });
  app.evaluate(`globalThis.cookieQueries = [];
    chrome.cookies.getAllCookieStores = async () => [{id:'current-store',tabIds:[7]}];
    chrome.cookies.getPartitionKey = async frame => {
      if (frame.tabId !== 7 || frame.frameId !== 3 || frame.documentId !== 'current') throw Error('wrong frame');
      return { partitionKey: { topLevelSite: 'https://top.example' } };
    };
    chrome.cookies.getAll = async query => { cookieQueries.push(query); return query.partitionKey
      ? [{ name: 'partition', value: 'scoped', sameSite: 'no_restriction', partitionKey: query.partitionKey }]
      : new Promise(resolve => { globalThis.finishPlainCookies = () => resolve([{ name: 'strict', value: 'withheld', sameSite: 'strict' }]); }); };`);
  const pending = app.evaluate(`signInCookies('https://media.example/file', 'https://top.example/',
    { frame: { tabId: 7, frameId: 3, documentId: 'current' }, userGesture: false })`);
  try {
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(app.evaluate('cookieQueries.length'), 2, 'partition lookup starts while ordinary cookies are pending');
    assert.equal(app.evaluate('cookieQueries.every(query => query.storeId === "current-store")'), true);
    assert.equal(app.evaluate('cookieQueries[1].partitionKey.topLevelSite'), 'https://top.example');
  } finally {
    app.evaluate('finishPlainCookies()');
  }
  const result = await pending;
  assert.equal(result.length, 1);
  assert.equal(result[0].name, 'partition');
  app.stopWorker();
});

test('resource witnesses enter the five-source verification budget before weaker candidates', async () => {
  const page = 'https://video.example/watch/resource-witness';
  const sender = { url: page, tab: { id: 38 }, frameId: 0, documentId: 'witness-budget-doc' };
  const changedAt = Date.now();
  const app = load({ 'media-options': { ok: false, code: 'no-media' },
    'media-source-options': checkedSource() });
  const actual = 'https://cdn.video.example/actual.m3u8';
  try {
    for (let index = 0; index < 7; index++) {
      await app.onMedia({ url: `https://cdn.video.example/noise-${index}.m3u8`, tabId: 38, frameId: 0,
        documentId: sender.documentId, method: 'GET', statusCode: 200, type: 'xmlhttprequest',
        timeStamp: changedAt + index, responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }] });
    }
    await app.onMedia({ url: actual, tabId: 38, frameId: 0, documentId: sender.documentId,
      method: 'GET', statusCode: 200, type: 'xmlhttprequest', timeStamp: changedAt - 12000,
      responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }] });
    const result = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
      playerUrl: 'blob:https://video.example/player', generation: 10, playerPlaying: true,
      mediaChangedAt: changedAt, resourceUrls: [actual] }, sender);
    const checks = app.sent.filter(message => message.type === 'media-source-options');
    assert.equal(checks.length, 5);
    assert.equal(checks[0].candidates[0].url, actual);
    assert.ok(result.data?.sourceChoices?.length);
  } finally { app.stopWorker(); }
});

test('exact current file can recover a refused page resolver without checking unrelated streams', async () => {
  const page='https://video.example/feed',url='https://cdn.example/movie.mp4',changedAt=Date.now();
  const sender={url:page,tab:{id:41},frameId:0,documentId:'exact-recovery-doc'};
  for(const code of ['site-refused','protected','blocked','source-expired']) {
    const app=load({'media-options':{ok:false,code},'media-source-options':checkedSource(60)});
    try {
      await app.onMedia({url,tabId:41,frameId:0,documentId:sender.documentId,method:'GET',statusCode:200,type:'media',
        timeStamp:changedAt,responseHeaders:responseHeaders({'Content-Type':'video/mp4','Content-Length':'700000',ETag:'"asset-v1"'})});
      const result=await app.message({type:'media-options',pageUrl:page,playerPageUrl:page,playerUrl:url,
        generation:1,playerPlaying:true,mediaChangedAt:changedAt},sender);
      const checks=app.sent.filter(m=>m.type==='media-source-options');
      const recoverable = ['site-refused', 'source-expired'].includes(code);
      assert.equal(checks.length,recoverable?1:0);
      if(recoverable) {
        assert.equal(checks[0].candidates[0].url,url);
        assert.equal(result.ok,true);
      } else assert.equal(result.code,code);
    } finally {app.stopWorker();}
  }
});

test('same-origin browser proof obeys the current sign-in preference', async()=>{
  const page='https://video.example/watch',url='https://video.example/movie.mp4',changedAt=Date.now();
  const sender={url:page,tab:{id:42},frameId:0,documentId:'cookie-mode-doc'};
  for(const useSignIn of [false,true]) {
    const app=load({}, {prefs:{useSignIn}});
    try {
      await app.onMedia({url,tabId:42,frameId:0,documentId:sender.documentId,method:'GET',statusCode:200,type:'media',
        timeStamp:changedAt,responseHeaders:responseHeaders({'Content-Type':'video/mp4','Content-Length':'700000',ETag:'"asset-v1"'})});
      const result=await app.message({type:'browser-body-media-proof',pageUrl:page,playerPageUrl:page,playerUrl:url,
        generation:1,playerPlaying:true,mediaChangedAt:changedAt},sender);
      assert.equal(result.ok,true);
      assert.equal(result.data.credentials,useSignIn?'same-origin':'omit');
    } finally {app.stopWorker();}
  }
});

test('playback membership can recover a correct manifest beyond the first usable candidate wave', async () => {
  const page = 'https://video.example/feed';
  const sender = { url:page, tab:{id:40}, frameId:0, documentId:'manifest-membership-doc' };
  const changedAt = Date.now();
  const actual = 'https://elsewhere.example/program.m3u8';
  const fingerprint = 'b'.repeat(64);
  const app = load({'media-source-options': message => {
    const reply = checkedSource(60);
    reply.data.playbackMatches = message.candidates[0].url === actual && message.playbackFingerprints?.includes(fingerprint) ? 1 : 0;
    return reply;
  }});
  try {
    for (let i=0;i<7;i++) {
      await app.onMedia({url:'https://cdn.video.example/noise-'+i+'.m3u8',tabId:40,frameId:0,documentId:sender.documentId,
        method:'GET',statusCode:200,type:'xmlhttprequest',timeStamp:changedAt+i,
        responseHeaders:[{name:'Content-Type',value:'application/vnd.apple.mpegurl'}]});
    }
    await app.onMedia({url:actual,tabId:40,frameId:0,documentId:sender.documentId,
      method:'GET',statusCode:200,type:'xmlhttprequest',timeStamp:changedAt-12000,
      responseHeaders:[{name:'Content-Type',value:'application/vnd.apple.mpegurl'}]});
    const msg={type:'media-options',pageUrl:page,playerPageUrl:page,playerUrl:'blob:https://video.example/player',
      generation:1,playerPlaying:true,mediaChangedAt:changedAt};
    await app.message(msg,sender);
    assert.equal(app.sent.filter(m=>m.type==='media-source-options').length,5);
    const result=await app.message({...msg,playerResourceFingerprints:[fingerprint]},sender);
    const checks=app.sent.filter(m=>m.type==='media-source-options').slice(5);
    assert.equal(checks.length,8,'bounded second wave, even though unrelated sources were usable');
    assert.ok(checks.some(m=>m.candidates[0].url===actual));
    assert.ok(checks.every(m=>m.playbackFingerprints?.[0]===fingerprint));
    assert.ok(result.data.sourceChoices.length);
    assert.equal(app.evaluate('sourceSelections.get(' + JSON.stringify(result.data.sourceChoices[0].token) + ').url'), actual);
  } finally {app.stopWorker();}
});
test('exact player resource fingerprint prioritizes a signed source without authorizing automatic selection', async () => {
  const page = 'https://video.example/feed';
  const sender = { url: page, tab: { id: 39 }, frameId: 0, documentId: 'exact-resource-doc' };
  const changedAt = Date.now(), base = 'https://cdn.video.example/movie-1234/video.mp4';
  const actual = base + '?signature=actual', other = base + '?signature=other';
  const app = load({ 'media-source-options': checkedSource(60) });
  try {
    for (const [url, at] of [[actual, changedAt-1000],[other, changedAt]]) {
      await app.onMedia({url, tabId:39, frameId:0, documentId:sender.documentId,
        method:'GET', statusCode:200, type:'xmlhttprequest', timeStamp:at,
        responseHeaders:[{name:'Content-Type',value:'video/mp4'},{name:'Content-Length',value:'300000'}]});
    }
    const digest = await webcrypto.subtle.digest('SHA-256',new TextEncoder().encode(actual));
    const fingerprint = Buffer.from(digest).toString('hex');
    const result = await app.message({type:'media-options',pageUrl:page,playerPageUrl:page,
      playerUrl:'blob:https://video.example/player',generation:1,playerPlaying:true,
      mediaChangedAt:changedAt,playerFragmentUrls:[base],playerResourceFingerprints:[fingerprint]},sender);
    const checks=app.sent.filter(message=>message.type==='media-source-options');
    assert.equal(checks.length,2);
    assert.equal(checks[0].candidates[0].url,actual);
    assert.equal(result.data.sourceChoices.length,2);
    assert.equal(JSON.stringify(result.data.sourceChoices).includes('signature='),false);
  } finally {app.stopWorker();}
});

test('watched video tabs discover extensionless complete media without accepting segments or unrelated data', async () => {
  const app = load({ 'media-source-options': checkedSource() });
  const page = 'https://video.example/watch/typed-media';
  const sender = { url: page, tab: { id: 34 }, frameId: 0, documentId: 'typed-media-doc' };
  const changedAt = Date.now();
  const actual = 'https://unfamiliar-cdn.example/object?id=42';
  try {
    await app.message({ type: 'media-element', watching: true }, sender);
    const observe = app.manifestWatchers.get(34);
    const report = (url, mime, overrides = {}) => observe({ url, tabId: 34, frameId: 0,
      documentId: sender.documentId, method: 'GET', statusCode: 200, type: 'xmlhttprequest',
      timeStamp: changedAt, responseHeaders: [{ name: 'Content-Type', value: mime }], ...overrides });
    await report(actual, 'video/mp4');
    await report('https://unfamiliar-cdn.example/object?segment=1', 'video/mp4');
    await report('https://unfamiliar-cdn.example/unrelated', 'application/json');
    await report('https://unfamiliar-cdn.example/other-tab', 'video/mp4', { tabId: 999 });
    await report('https://unfamiliar-cdn.example/post-only', 'video/mp4', { method: 'POST' });
    const result = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
      playerUrl: 'blob:https://video.example/player', generation: 1, playerPlaying: true, mediaChangedAt: changedAt }, sender);
    assert.equal(result.data?.sourceChoices?.length, 1);
    assert.deepEqual(app.sent.filter(message => message.type === 'media-source-options')
      .map(message => message.candidates[0].url), [actual]);
  } finally { app.stopWorker(); }
});

test('playback transitions renew a watched tab without adding duplicate listeners', async () => {
  const app = load();
  const sender = { url: 'https://video.example/watch', tab: { id: 52 }, frameId: 0, documentId: 'renew-doc' };
  try {
    await app.message({ type: 'media-element', watching: true }, sender);
    const first = app.manifestWatchers.get(52);
    app.advanceTime(120000);
    await app.message({ type: 'media-element', watching: true }, sender);
    assert.equal(app.manifestWatchers.get(52), first);
    app.advanceTime(120000);
    await app.message({ type: 'media-element', watching: true }, sender);
    assert.equal(app.manifestWatchers.get(52), first);
    assert.equal(app.manifestWatchers.size, 1);
  } finally { app.stopWorker(); }
});

test('probe diversity does not let signed variants crowd out other media families', () => {
  const app = load({});
  try {
    const rank = app.evaluate('rankObservedSources');
    const changedAt = 1000000;
    const variant = i => ({url: 'https://cdn.test/video.m3u8?quality=720&sig=' + i, kind:'hls', seen:changedAt+i});
    const other = {url:'https://cdn.test/video.m3u8?quality=1080&sig=x',kind:'hls',seen:changedAt+1000};
    const unrelated = {url:'https://cdn.test/other.mpd',kind:'dash',seen:changedAt+2000};
    const sources = [...Array.from({length:6},(_,i)=>variant(i)),other,unrelated];
    const ranked = rank(sources,changedAt);
    assert.equal(ranked[0].url, sources[0].url);
    assert.ok(ranked.slice(0,3).includes(other));
    assert.ok(ranked.slice(0,3).includes(unrelated));
    assert.deepEqual(new Set([...ranked]),new Set(sources));
  } finally { app.stopWorker(); }
});

test('a generated file selected while paused is sent only to the clicked frame', async () => {
  const app=load({}, { localData: { vesperPaused: true } });
  try {
    await app.onContextClicked({menuItemId:'vesper-download',linkUrl:'blob:https://example.com/test-blob',frameId:7}, {id:4,url:'https://example.com/'});
    assert.equal(app.sent.length,0);
    assert.ok(app.tabMessages.some(m=>m.tabId===4 && m.options.frameId===7 && m.message.type==='vesper-copy-generated'));
  } finally {app.stopWorker();}
});
test('body receipt is read-only and bound to the original tab, frame and document', async () => {
  const id='10945678-1234-1234-1234-123456789abc';
  const sender={tab:{id:8},frameId:1,documentId:'doc-receipt-01',url:'https://example.com/private'};
  const app=load({'browser-body':{ok:true,data:{accepted:true,jobId:'job',offset:12}}});
  try {
    await app.evaluate('rememberBodyReceipt') (id,{...sender,id:'vesper-test'});
    assert.equal((await app.message({type:'browser-body-status',transferId:id},{...sender,documentId:'other-document'})).ok,false);
    assert.equal(app.sent.length,0);
    assert.equal((await app.message({type:'browser-body-status',transferId:id},sender)).ok,true);
    assert.equal(app.sent[0].operation,'status');
    assert.equal(app.sent[0].origin,'https://example.com/');
  } finally {app.stopWorker();}
});
test('conflicting cross-document attachment responses with identical validators are ambiguous', async () => {
  const file=activeDownload();
  const app=load({'browser-capture':started,'browser-transfer':transfer('active')},{download:file});
  try {
    for (const tabId of [3,4]) await app.onHeaders({url:file.url,requestId:'conflict-'+tabId,tabId,frameId:0,
      documentId:'document-'+tabId,method:'GET',type:'main_frame',statusCode:200,responseHeaders:responseHeaders(zipHeaders)});
    await app.onCreated(file);
    assert.equal(app.sent.filter(isBrowserSubmission).length,0);
    assert.equal(file.paused,false);
    assert.equal(outcomeOf(app),'changed');
  } finally {app.stopWorker();}
});

test('browser-session acquisition requires the exact current media document and a strong range validator', async () => {
  const page='https://video.example/watch/1', url='https://video.example/media/movie.mp4';
  const sender={tab:{id:71},frameId:0,documentId:'media-document-71',url:page};
  const failure={ok:false,code:'sign-in',message:'Browser session required'};
  const app=load({'media-options':failure,'media-range-options':async () => {
    await new Promise(resolve => setTimeout(resolve, 20)); return failure;
  }});
  const changedAt=Date.now();
  const selection={pageUrl:page,playerPageUrl:page,playerUrl:url,generation:1,mediaChangedAt:changedAt,playerHasMedia:true};
  const request={type:'browser-body-media-proof',...selection};
  try {
    const report=(tag,other={})=>app.onMedia({url,tabId:71,frameId:0,documentId:sender.documentId,
      method:'GET',statusCode:206,type:'media',timeStamp:changedAt,
      responseHeaders:responseHeaders({'Content-Type':'video/mp4','Content-Range':'bytes 0-999/700000',
        'Content-Length':'1000',ETag:tag}),...other});
    await report('"movie-v1"');
    const [options, concurrent]=await Promise.all([1,2].map(() => app.message({type:'media-options',...selection},sender)));
    assert.equal(options.data?.browserBodyAvailable,true);
    assert.equal(concurrent.data?.browserBodyAvailable,true);
    assert.equal(app.sent.filter(request => request.type === 'media-range-options').length,1);
    const proof=await app.message(request,sender);
    assert.equal(proof.ok,true);
    assert.equal(proof.data.etag,'"movie-v1"');
    assert.equal(proof.data.length,700000);
    await app.local.set({ vesperPaused: true });
    assert.deepEqual(await app.message(request, sender), proof, 'An explicit browser copy remains available while paused.');
    assert.equal((await app.message(request,{...sender,documentId:'other-document'})).ok,false);
    assert.equal((await app.message({...request,playerUrl:'https://cdn.example/movie.mp4'},sender)).ok,false);
    assert.equal((await app.message({...request,mediaChangedAt:changedAt+60000},sender)).ok,false);
    await report('"different-version"');
    assert.equal((await app.message(request,sender)).ok,false);
  } finally {app.stopWorker();}
});
test('incompatible installed helper produces one useful error and no unchecked stream choices', async()=>{
  const page='https://x.com/example/status/1234567890', changedAt=Date.now();
  const sender={url:page,tab:{id:74},frameId:0,documentId:'x-helper-version-test'};
  const app=load({'media-source-options':{ok:false,code:'invalid',message:'Unknown request type.'}});
  try {
    for(let i=0;i<9;i++) await app.onMedia({url:'https://cdn.test/video'+i+'.m3u8',tabId:74,frameId:0,documentId:sender.documentId,
      method:'GET',statusCode:200,type:'xmlhttprequest',timeStamp:changedAt,responseHeaders:responseHeaders({'Content-Type':'application/vnd.apple.mpegurl'})});
    const result=await app.message({type:'media-options',pageUrl:page,playerPageUrl:page,playerUrl:'blob:https://x.com/fixture',
      generation:1,playerPlaying:true,mediaChangedAt:changedAt},sender);
    assert.equal(result.code,'desktop-version-mismatch');
    assert.equal(result.data?.sourceChoices,undefined);
    assert.equal(app.sent.filter(m=>m.type==='media-source-options').length,5);
  } finally {app.stopWorker();}
});
test('additional source candidates must pass verification before appearing in the picker', async()=>{
  const page='https://x.com/example/status/1234567890', changedAt=Date.now();
  const sender={url:page,tab:{id:75},frameId:0,documentId:'x-verified-sources-test'};
  let count=0;
  const app=load({'media-source-options':()=>++count===7?checkedSource(11):{ok:false,code:'source-unavailable'}});
  try {
    for(let i=0;i<9;i++) await app.onMedia({url:'https://cdn.test/video'+i+'.m3u8',tabId:75,frameId:0,documentId:sender.documentId,
      method:'GET',statusCode:200,type:'xmlhttprequest',timeStamp:changedAt,responseHeaders:responseHeaders({'Content-Type':'application/vnd.apple.mpegurl'})});
    const result=await app.message({type:'media-options',pageUrl:page,playerPageUrl:page,playerUrl:'blob:https://x.com/fixture',
      generation:1,playerPlaying:true,mediaChangedAt:changedAt},sender);
    assert.equal(result.ok,true);
    assert.equal(result.data.verifiedSources,true);
    assert.equal(result.data.sourceChoices.length,1);
    assert.equal(count,9);
  } finally {app.stopWorker();}
});
test('only identical verified delivery fingerprints collapse, not identical qualities',()=>{
  const app=load();
  try {
    const distinct=app.evaluate('distinctVerifiedDeliveries');
    const reply=fingerprint=>({reply:{ok:true,data:{deliveryFingerprint:fingerprint,choices:[{label:'480p'}]}}});
    const items=[reply('a'.repeat(64)),reply('a'.repeat(64)),reply('b'.repeat(64)),reply(undefined)];
    assert.deepEqual([...distinct(items,false)],[items[0],items[2],items[3]]);
    assert.equal(distinct(items,true).length,4);
  } finally {app.stopWorker();}
});
test('X post identity is stable across query strings and distinguishes explicit video indexes',()=>{
  const app=load();
  try {
    const identity=app.evaluate('VesperPlayerSource.identity');
    assert.equal(identity('https://x.com/example/status/1234567890?s=20'),'x:1234567890');
    assert.equal(identity('https://twitter.com/example/status/1234567890'),'x:1234567890');
    assert.notEqual(identity('https://x.com/example/status/1234567890/video/1'),identity('https://x.com/example/status/1234567890/video/2'));
    assert.equal(identity('https://x.com/example'),'https://x.com/example');
  } finally {app.stopWorker();}
});

test('X grouped picker checks same-asset variants together and separates different asset IDs',async()=>{
  const page='https://x.com/example/status/1234567890',changedAt=Date.now();
  const sender={url:page,tab:{id:78},frameId:0,documentId:'x-group-test'};
  const base='https://video.twimg.com/ext_tw_video/9876543210/pu/';
  const app=load({'media-group-options':checkedSource(18),'media-source-options':checkedSource(11)});
  try {
    for(const url of [base+'pl/master.m3u8',base+'vid/480x480/file.mp4','https://video.twimg.com/ext_tw_video/9876543211/pu/pl/other.m3u8']){
      const mime=url.endsWith('.mp4')?'video/mp4':'application/vnd.apple.mpegurl';
      await app.onMedia({url,tabId:78,frameId:0,documentId:sender.documentId,method:'GET',statusCode:200,type:'xmlhttprequest',
        timeStamp:changedAt,responseHeaders:responseHeaders({'Content-Type':mime})});
    }
    const request={type:'media-options',pageUrl:page,playerPageUrl:page,playerUrl:'blob:https://x.com/fixture',
      generation:1,playerPlaying:true,mediaChangedAt:changedAt};
    const result=await app.message(request,sender);
    assert.equal(result.ok,true);
    assert.equal(result.data.sourceChoices.length,2);
    assert.equal(app.sent.filter(m=>m.type==='media-group-options').length,1);
    const grouped=app.sent.find(m=>m.type==='media-group-options');
    assert.equal(grouped.candidates.length,2);
    assert.ok(grouped.candidates.every(c=>c.url.startsWith(base)));
    assert.equal(app.sent.filter(m=>m.type==='media-source-options').length,1);
    const row=result.data.sourceChoices.find(c=>c.label.startsWith('0:18'));
    const selected=await app.message({type:'media-source-select',sourceChoice:row.token,pageUrl:page,generation:1,playerUrl:request.playerUrl},sender);
    assert.equal(selected.ok,true);
    assert.equal(selected.data.duration,18);
  }finally{app.stopWorker();}
});
test('provider grouping never trusts a lookalike CDN or profile URL',()=>{
  const app=load();
  try{
    const group=app.evaluate('groupObservedAssets');
    const source=url=>({url,kind:'direct'});
    const urls=[source('https://video.twimg.com/ext_tw_video/9876543210/pu/vid/a.mp4'),
      source('https://video.twimg.com/ext_tw_video/9876543210/pu/vid/b.mp4'),
      source('https://video.twimg.com.evil.example/ext_tw_video/9876543210/pu/vid/c.mp4')];
    assert.equal(group(urls,'x:1234567890').length,2);
    assert.equal(group(urls,'https://x.com/example').length,3);
  }finally{app.stopWorker();}
});

test('rejected grouping checks original members independently rather than losing valid formats',async()=>{
  const app=load({'media-group-options':{ok:false,code:'invalid',message:'Different media asset'},
    'media-source-options':request=>request.candidates[0].url.endsWith('a.mp4')?checkedSource(18):{ok:false,code:'source-unavailable'}});
  const base='https://video.twimg.com/ext_tw_video/9876543210/pu/vid/';
  try{
    const verify=app.evaluate('verifyObservedSources');
    const members=[{url:base+'a.mp4',kind:'direct'},{url:base+'b.mp4',kind:'direct'}];
    const checked=await verify([{...members[0],group:members}],{auth:{},pageUrl:'https://x.com/user/status/1234567890',title:'Fixture'});
    assert.equal(checked.length,2);
    assert.equal(checked.filter(r=>r.reply.ok).length,1);
    assert.equal(checked.find(r=>r.reply.ok).source.url,base+'a.mp4');
    assert.equal(app.sent.length,3);
  }finally{app.stopWorker();}
});
test('strict recipe subset is suppressed only when auxiliary tracks are also identical',()=>{
  const app=load();
  try {
    const distinct=app.evaluate('distinctVerifiedDeliveries');
    const a='a'.repeat(64),b='b'.repeat(64),x='c'.repeat(64),y='d'.repeat(64);
    const source=(recipes,tracks)=>({reply:{ok:true,data:{deliveryRecipeFingerprints:recipes,deliveryTrackFingerprint:tracks,choices:[{label:'Video'}]}}});
    const smaller=source([a],x),larger=source([a,b],x),different=source([a],y),unknown=source([a],undefined);
    assert.deepEqual([...distinct([smaller,larger,different,unknown],false)],[larger,different,unknown]);
    assert.equal(distinct([smaller,larger],true).length,2);
  } finally {app.stopWorker();}
});
test('cross-origin browser proof requires observed exposed validator and exact CORS permission',async()=>{
  const page='https://video.example/watch',url='https://cdn.example/file.mp4',changedAt=Date.now();
  const sender={tab:{id:83},frameId:0,documentId:'cors-proof-83',url:page};
  const request={type:'browser-body-media-proof',pageUrl:page,playerPageUrl:page,playerUrl:url,generation:1,mediaChangedAt:changedAt,playerHasMedia:true};
  const app=load({}, {prefs:{useSignIn:true}});
  try {
    const report=headers=>app.onMedia({url,tabId:83,frameId:0,documentId:sender.documentId,method:'GET',statusCode:200,type:'media',
      timeStamp:changedAt,responseHeaders:responseHeaders({'Content-Type':'video/mp4','Content-Length':'10000',ETag:'"asset-v1"',...headers})});
    await report({'Access-Control-Allow-Origin':'*'});
    assert.equal((await app.message(request,sender)).ok,false);
    await report({'Access-Control-Allow-Origin':'*','Access-Control-Expose-Headers':'ETag'});
    assert.equal((await app.message(request,sender)).data.credentials,'omit');
    assert.equal((await app.message({...request,playerCrossOrigin:'use-credentials'},sender)).ok,false);
    await report({'Access-Control-Allow-Origin':'https://video.example','Access-Control-Allow-Credentials':'true','Access-Control-Expose-Headers':'ETag'});
    assert.equal((await app.message({...request,playerCrossOrigin:'use-credentials'},sender)).data.credentials,'include');
    assert.equal((await app.message(request,{...sender,documentId:'cors-other-doc'})).ok,false);
  } finally {app.stopWorker();}
});

test('stale attachment evidence cannot take over a new download', async()=>{
  const file=activeDownload();
  const app=load({'browser-capture':started,'browser-submit':started},{download:file,prefs:{intercept:true,route:'auto'}});
  await observe(app,file,{requestId:'old-attachment'});
  await waitFor(()=>!!app.evaluate('recentResponse('+JSON.stringify(file.url)+')'),'Attachment response was not recorded');
  app.advanceTime(16000);
  await app.onCreated(file);
  await new Promise(resolve=>setTimeout(resolve,20));
  assert.equal(app.sent.filter(isBrowserSubmission).length,0);
  assert.equal(file.paused,false);
});
test('verified XHR media accepts anonymous CORS wildcard exposure but not credentialed wildcard',async()=>{
  const app=load(), page='https://example.com/watch', url='https://cdn.example.com/movie.mp4';
  const changedAt=Date.now();
  const sender={url:page,tab:{id:7},frameId:0,documentId:'doc-body-1'};
  const msg={playerUrl:url,pageUrl:page,playerPageUrl:page,generation:1,mediaChangedAt:changedAt};
  await app.onMedia({url,tabId:7,frameId:0,documentId:sender.documentId,method:'GET',statusCode:200,
    type:'xmlhttprequest',timeStamp:changedAt,responseHeaders:[
      {name:'Content-Type',value:'video/mp4'},{name:'Content-Length',value:'700000'},
      {name:'ETag',value:'"v1"'},{name:'Access-Control-Allow-Origin',value:'*'},
      {name:'Access-Control-Expose-Headers',value:'*'}]});
  const proof=await app.message({...msg,type:'browser-body-media-proof'},sender);
  assert.equal(proof.ok,true);
  assert.equal(proof.data.credentials,'omit');
  const refused=await app.message({...msg,type:'browser-body-media-proof',playerCrossOrigin:'use-credentials'},sender);
  assert.equal(refused.ok,false);
});
test('an exact observed XHR range permits browser continuation but a slice URL or another player does not',async()=>{
  const page='https://video.example/watch',url='https://video.example/movie.mp4',changedAt=Date.now();
  const sender={tab:{id:84},frameId:0,documentId:'xhr-range-doc',url:page};
  const app=load({}, {prefs:{useSignIn:true}});
  try {
    const report=address=>app.onMedia({url:address,tabId:84,frameId:0,documentId:sender.documentId,method:'GET',
      statusCode:206,type:'xmlhttprequest',timeStamp:changedAt,
      responseHeaders:responseHeaders({'Content-Type':'video/mp4','Content-Length':'1000',
        'Content-Range':'bytes 0-999/700000',ETag:'"asset-v1"'})});
    const request={type:'browser-body-media-proof',pageUrl:page,playerPageUrl:page,playerUrl:url,
      generation:1,mediaChangedAt:changedAt};
    await report(url);
    assert.equal((await app.message(request,sender)).data.length,700000);
    assert.equal((await app.message({...request,playerUrl:url+'?other=1'},sender)).ok,false);
    const slice=url+'?bytestart=0&byteend=999';
    await report(slice);
    assert.equal((await app.message({...request,playerUrl:slice},sender)).ok,false);
  } finally {app.stopWorker();}
});
test('out-of-order media callbacks cannot restore an obsolete ETag or CORS policy',async()=>{
  const app=load(), url='https://cdn.example.com/movie.mp4', at=Date.now();
  const base={url,documentId:'doc-body-1',frameId:0,kind:'direct',mime:'video/mp4',bytes:700000};
  await app.evaluate('addSource(7,'+JSON.stringify({...base,seen:at,validator:'"v2"',corsOrigin:null})+')');
  await app.evaluate('addSource(7,'+JSON.stringify({...base,seen:at-500,validator:'"v1"',corsOrigin:'*'})+')');
  const state=await app.evaluate('tabState(7)');
  assert.equal(state.sources.length,1);
  assert.equal(state.sources[0].validator,'"v2"');
  assert.equal(state.sources[0].corsOrigin,null);
  assert.equal(state.sources[0].conflictingValidator,true);
});

test('preloaded blob media is admitted only by a recent exact witness in the same document', async () => {
  const page = 'https://video.example/feed';
  const sender = { url: page, tab: { id: 81 }, frameId: 0, documentId: 'preloaded-doc' };
  const now = Date.now();
  const actual = 'https://cdn.example.com/actual.mp4?signature=one';
  const other = 'https://cdn.example.com/other.mp4?signature=two';
  const app = load({ 'media-options': { ok: false, code: 'site-refused' }, 'media-source-options': checkedSource(60) });
  try {
    for (const [url, documentId] of [[actual, sender.documentId], [other, sender.documentId], ['https://cdn.example.com/wrong.mp4', 'other-doc']]) {
      await app.onMedia({ url, tabId: 81, frameId: 0, documentId, method: 'GET', statusCode: 200, type: 'media',
        timeStamp: now - 180_000, responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }, { name: 'Content-Length', value: '300000' }] });
    }
    const hash = async url => Buffer.from(await webcrypto.subtle.digest('SHA-256', new TextEncoder().encode(url))).toString('hex');
    const message = { type: 'media-options', pageUrl: page, playerPageUrl: page, playerUrl: 'blob:https://video.example/active',
      generation: 1, playerPlaying: true, mediaChangedAt: now };
    const noWitness = await app.message(message, sender);
    assert.equal(noWitness.ok, false);
    assert.equal(app.sent.filter(m => m.type === 'media-source-options').length, 0);
    const result = await app.message({ ...message, playerResourceFingerprints: [await hash(actual), await hash('https://cdn.example.com/wrong.mp4')] }, sender);
    const checks = app.sent.filter(m => m.type === 'media-source-options');
    assert.equal(checks.length, 1);
    assert.equal(checks[0].candidates[0].url, actual);
    assert.equal(result.data.sourceChoices.length, 1, 'witness ranks verified media but does not auto-select');
  } finally { app.stopWorker(); }
});
const earlySender = {tab:{id:81,url:'https://example.com/releases'},frameId:0,documentId:'early-document',url:'https://example.com/releases'};
const earlyUrl = 'https://example.com/files/setup.msi';
function earlyHeaders(url=earlyUrl, changes={}) {
  return {status:200,url,headers:new Headers({'Content-Type':'application/octet-stream','Content-Length':'500000','ETag':'"v1"','Content-Disposition':'attachment',...changes})};
}
test('trusted direct intent stages and accepts before any Chrome download exists', async()=>{
  const app=load({'browser-capture':started,'browser-transfer':transfer('active')},{earlyFetch:async(url,opts)=>{
    assert.equal(url,earlyUrl);assert.equal(opts.method,'HEAD');assert.equal(opts.credentials,'omit');return earlyHeaders();
  }});
  try{
    const reply=await app.message({type:'early-download',url:earlyUrl},earlySender);
    assert.equal(reply.ok,true);
    assert.deepEqual(app.sent.map(m=>m.type),['browser-capture','browser-transfer']);
    assert.equal(app.sent[0].browserProof.etag,'"v1"');
    assert.equal(app.sent[1].operation,'accept');
    assert.equal(app.downloadHistory.length,0);
  }finally{app.stopWorker()}
});
test('trusted direct intent accepts a seasoned Last-Modified only without an ETag', async () => {
  for (const [etag, date, allowed] of [
    ['', 'Tue, 03 Nov 2026 10:02:00 GMT', true],
    ['', 'Tue, 03 Nov 2026 10:00:30 GMT', false],
    ['W/"v1"', 'Tue, 03 Nov 2026 10:02:00 GMT', false]
  ]) {
    const modified = 'Tue, 03 Nov 2026 10:00:00 GMT';
    const app = load({'browser-capture':started,'browser-transfer':transfer('active')}, {
      earlyFetch: async () => earlyHeaders(earlyUrl, {ETag: etag, 'Last-Modified': modified, Date: date})
    });
    try {
      const reply = await app.message({type:'early-download', url:earlyUrl}, earlySender);
      assert.equal(reply.ok === true, allowed);
      if (allowed) assert.equal(app.sent[0].browserProof.lastModified, modified);
      else assert.equal(app.sent.length, 0);
    } finally { app.stopWorker(); }
  }
});
test('explicit same-origin named exports admit extensionless URLs and safe query parameters',async()=>{
  for(const [url,name,mime] of [
    ['https://example.com/export','archive.zip','application/zip'],
    ['https://example.com/export?id=45&format=csv','report.csv','text/csv'],
    ['https://example.com/export?format=md','notes.md','text/markdown']
  ]){
    const app=load({'browser-capture':started,'browser-transfer':transfer('active')},{earlyFetch:async()=>earlyHeaders(url,{'Content-Type':mime})});
    try{
      assert.equal((await app.message({type:'early-download',url,downloadName:name},earlySender)).ok,true);
      assert.equal(app.sent[0].items[0].url,url);
    }finally{app.stopWorker()}
  }
});
test('explicit names cannot turn opaque tokens, HTML or unrelated origins into verified downloads',async()=>{
  for(const [url,name,mime] of [
    ['https://example.com/export?token=single-use','archive.zip','application/zip'],
    ['https://example.com/export','report.csv','text/html'],
    ['https://other.example/export','archive.zip','application/zip'],
    ['https://example.com/export','paper.pdf','application/pdf']
  ]){
    const app=load({}, {earlyFetch:async()=>earlyHeaders(url,{'Content-Type':mime})});
    try{assert.equal((await app.message({type:'early-download',url,downloadName:name},earlySender)).fallback,true);assert.equal(app.sent.length,0)}
    finally{app.stopWorker()}
  }
});
test('ambiguous links, HTML, missing validators, private mode and explicit queue preferences remain browser-owned',async()=>{
  for(const [url,changes,prefs,sender] of [
    [earlyUrl+'?token=x',{}, {},earlySender],
    ['https://example.com/article.pdf',{}, {},earlySender],
    [earlyUrl,{'Content-Type':'text/html'}, {},earlySender],
    [earlyUrl,{'ETag':'W/"v1"'}, {},earlySender],
    [earlyUrl,{}, {handoff:'queue'},earlySender],
    [earlyUrl,{}, {},{...earlySender,tab:{...earlySender.tab,incognito:true}}]
  ]){
    const app=load({}, {prefs,earlyFetch:async()=>earlyHeaders(url,changes)});
    try{assert.equal((await app.message({type:'early-download',url},sender)).fallback,true);assert.equal(app.sent.length,0)}
    finally{app.stopWorker()}
  }
});
test('same-origin signed-in early download requires permission and never follows a credentialed redirect',async()=>{
  for(const scenario of ['approved','disabled','no-permission','other-origin','redirect']){
    const url=scenario==='other-origin'?'https://other.example/setup.msi':earlyUrl;
    const requests=[];
    const app=load({'browser-submit':started,'browser-transfer':transfer('active')},{
      prefs:{useSignIn:scenario!=='disabled'},signInPermission:scenario!=='no-permission',
      earlyFetch:async(address,options)=>{
        requests.push(options);
        if(options.credentials==='omit')return earlyHeaders(address, {'ETag':'"guest-file"'});
        assert.equal(options.redirect,'error');
        return scenario==='redirect'?{...earlyHeaders('https://other.example/setup.msi'),redirected:true}:earlyHeaders(address);
      }});
    try{
      const reply=await app.message({type:'early-download',url},earlySender);
      assert.equal(reply.ok,scenario!=='redirect',scenario);
      assert.equal(requests.length,1,scenario);
      if(scenario==='approved'){
        assert.equal(app.sent[0].type,'browser-submit');
        assert.equal(app.sent[0].authorized,true);
        assert.equal(app.sent[0].items[0].cookie,'session=test-value');
        assert.equal(app.sent[0].browserProof.etag,'"v1"');
      }else if(scenario==='redirect') assert.equal(app.sent.length,0);
      else {
        assert.equal(requests[0].credentials,'omit');
        assert.equal(app.sent[0].type,'browser-capture');
        assert.equal(app.sent[0].items[0].cookie,undefined);
      }
    }finally{app.stopWorker()}
  }
});
test('refused native stage permits replay but uncertain acceptance never starts a duplicate',async()=>{
  for(const uncertain of [false,true]){
    const app=load({'browser-capture':uncertain?started:{ok:false,code:'site-refused'},
      'browser-transfer':()=>uncertain?{ok:false,code:'host-unavailable'}:transfer('failed')},
      {earlyFetch:async()=>earlyHeaders()});
    try{
      const reply=await app.message({type:'early-download',url:earlyUrl},earlySender);
      assert.equal(reply.fallback,!uncertain);
      assert.equal(app.sent.some(m=>m.operation==='accept'),uncertain);
      assert.equal(app.sent.at(-1).operation,'abandon');
    }finally{app.stopWorker()}
  }
});
