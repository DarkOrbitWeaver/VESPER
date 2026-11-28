// Runs the real background.js with a mocked Chrome API. Run: node --test tests/extension/background.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { webcrypto } from 'node:crypto';

const isBrowserSubmission = message => ['browser-capture', 'browser-submit'].includes(message.type);

function load(nativeReplies = {}, { download, downloadHistory = [], signInPermission = false, prefs = {}, activity = false, sessionData = {},
  failDownloadSearch = false, failDownloadCancel = false, failDownloadResume = false, deferDownloadCancel = false,
  localData = {}, syncData = { ...prefs }, cookies = [{ name: 'session', value: 'test-value', path: '/' }],
  preflightReady = false, preflightDelay = 0, playerPicker = false, helperProofs, earlyFetch, diagnosticBuild } = {}) {
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
      ...(diagnosticBuild ? { getManifest: () => ({ version_name: diagnosticBuild }) } : {}),
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
        if (typeof failDownloadResume === 'function' ? failDownloadResume() : failDownloadResume)
          throw new Error('Chrome refused to resume the download.');
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
      if (!onFilename) return undefined;
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
  assert.equal(r.choices[0].detail, 'MP4 · 25.0 MiB');
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
  assert.equal(sent[0].v, 2);
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

test('a Chrome download is a miss at zero bytes and stays Chrome-owned as bytes arrive', async () => {
  const file = { ...activeDownload(), url: 'https://example.com/exports/private-name?token=secret',
    finalUrl: 'https://cdn.example.net/files/signed-path/archive.zip?sig=secret-2' };
  const app = load({}, { download: file });
  await app.evaluate('capturePreferencesReady');
  await observe(app, file);
  await app.onCreated(file);
  let miss = app.sessionData.vesperCaptureDecisions[0];
  assert.equal(miss.outcome, 'failed');
  assert.equal(miss.received, 0);
  assert.equal(miss.redirected, true);
  assert.equal(miss.response.strongEtag, true);
  assert.equal(miss.original, 'https://example.com/exports/*');
  assert.equal(miss.final, 'https://cdn.example.net/files/*/*.zip');
  assert.doesNotMatch(JSON.stringify(miss), /secret|private-name|signed-path/);
  file.bytesReceived = 1;
  await app.onChanged({ id: file.id, bytesReceived: { current: 1 } });
  miss = app.sessionData.vesperCaptureDecisions[0];
  assert.equal(miss.received, 1);
  assert.equal(miss.outcome, 'failed');
  assert.deepEqual(app.actions, []);
  assert.equal(file.paused, false);
});

test('Chrome downloads with bytes, POSTs, and completed files are left alone without staging', async () => {
  for (const [bytesReceived, method, state] of [[1, 'GET', 'in_progress'], [0, 'POST', 'in_progress'], [1000, 'GET', 'complete']]) {
    const file = { ...activeDownload(), bytesReceived, state };
    const app = load({}, { download: file });
    await app.evaluate('capturePreferencesReady');
    await observe(app, file, { method });
    await app.onCreated(file);
    const [miss] = app.sessionData.vesperCaptureDecisions;
    assert.equal(miss.outcome, 'failed');
    assert.equal(miss.received, bytesReceived);
    assert.equal(miss.reason, method === 'POST' ? 'post' : 'chrome-created');
    assert.deepEqual(app.actions, []);
  }
});

test('Chrome misses omit private windows, deliberately excluded sites, and paused capture', async () => {
  for (const [file, localData] of [
    [{ ...activeDownload(), incognito: true }, {}],
    [activeDownload(), { vesperSiteNeverCaptureHosts: ['example.com'] }],
    [activeDownload(), { vesperPaused: true }]
  ]) {
    const app = load({}, { download: file, localData });
    await app.onCreated(file);
    assert.equal(app.sessionData.vesperCaptureDecisions, undefined);
    assert.deepEqual(app.actions, []);
  }
});

test('browser failure diagnostics survive a worker restart and export once when the helper is back', async () => {
  const file = { ...activeDownload(), url: 'https://cdn.test/download/private-name.patch?token=URL-SECRET', bytesReceived: 7 };
  const first = load({}, { download: file, diagnosticBuild: '2.0.0-old' });
  const failure = { type: 'diagnostic-failure', code: 'site-refused',
    resource: 'https://media.test/video/user-name.mp4?sig=MEDIA-SECRET',
    facts: { stage: 'formats', ready: 4, responses: 12, postLookup: 'site-refused',
      cookie: 'COOKIE-SECRET', observedLookups: ['source-changed'] } };
  const sender = { ...earlySender, url: 'https://www.tiktok.com/@PRIVATE_USER/video/123456789/' };
  try {
    await first.onCreated(file);
    file.state = 'interrupted';
    file.error = 'NETWORK_FAILED';
    await first.onChanged({ id: file.id, state: { current: 'interrupted' } });
    assert.equal((await first.message(failure, sender)).ok, true);
    assert.equal((await first.message(failure, { ...sender, tab: { ...sender.tab, incognito: true } })).ok, false);
    await first.evaluate('exportPendingFailures()');
    assert.equal(first.localData.vesperFailureLog.length, 3);
    assert.ok(first.localData.vesperFailureLog.every(entry => !entry.exported));
  } finally { first.stopWorker(); }

  const second = load({ 'browser-diagnostics': { ok: true } }, {
    diagnosticBuild: '2.0.0-new', localData: first.localData
  });
  try {
    await second.evaluate('exportPendingFailures()');
    await second.evaluate('exportPendingFailures()');
    const batch = second.sent.filter(message => message.type === 'browser-diagnostics');
    assert.equal(batch.length, 1);
    const [miss, stopped, media] = batch[0].entries;
    assert.equal(miss.kind, 'chrome-miss');
    assert.equal(miss.facts.bytes, 7);
    assert.equal(stopped.facts.chromeError, 'NETWORK_FAILED');
    assert.equal(media.kind, 'media-failed');
    assert.equal(media.build, '2.0.0-old');
    assert.equal(typeof media.utc, 'number');
    assert.equal(media.page, 'https://www.tiktok.com/*/video/*');
    assert.equal(media.resource, 'https://media.test/video/*.mp4');
    assert.equal(media.facts.responses, 12);
    assert.doesNotMatch(JSON.stringify(batch), /SECRET|PRIVATE_USER|123456789|private-name|user-name/);
    assert.ok(second.localData.vesperFailureLog.every(entry => entry.exported));
  } finally { second.stopWorker(); }
});

test('upgrade releases only a legacy zero-byte pause after its native transfer stops', async () => {
  for (const [bytesReceived, result, mayResume] of [
    [0, 'failed', true], [1, 'failed', false], [0, 'complete', false]
  ]) {
    const file = { ...activeDownload(), bytesReceived, paused: true };
    const app = load({ 'browser-transfer': transfer(result) }, { download: file, localData: {
      browserDownloads: { [file.id]: { startTime: file.startTime, transferId: 'legacy-id', stage: 'parked', resumeChrome: true } }
    } });
    await app.recover();
    assert.deepEqual(app.actions, mayResume ? ['native', 'resume'] : ['native']);
    assert.equal(app.sent[0].operation, 'abandon');
    assert.equal(file.paused, !mayResume);
    assert.deepEqual(app.localData.browserDownloads, {});
  }
});

test('upgrade preserves a legacy cleanup receipt while native is unavailable', async () => {
  let reply = { ok: false, code: 'host-missing' };
  const file = { ...activeDownload(), paused: true };
  const app = load({ 'browser-transfer': () => reply }, { download: file, localData: {
    browserDownloads: { [file.id]: { startTime: file.startTime, transferId: 'legacy-id', stage: 'parked', resumeChrome: true } }
  } });
  await app.recover();
  assert.equal(file.paused, true);
  assert.equal(app.localData.browserDownloads[file.id].transferId, 'legacy-id');
  reply = transfer('failed');
  await app.recover();
  assert.equal(file.paused, false);
  assert.deepEqual(app.localData.browserDownloads, {});
});

// ---- Last-Modified as the strong validator (RFC 9110 8.8.2.2), for helpers that accept it

const datedHeaders = (file, extra = {}) => Object.entries({ 'Content-Length': String(file.fileSize), 'Accept-Ranges': 'bytes',
  'Content-Type': 'application/zip', 'Content-Disposition': 'attachment; filename="archive.zip"',
  'Last-Modified': 'Tue, 03 Nov 2026 10:00:00 GMT', Date: 'Thu, 12 Nov 2026 01:00:00 GMT', ...extra })
  .filter(([, value]) => value !== undefined).map(([name, value]) => ({ name, value }));
const observeDated = (app, file, extra) => app.onHeaders({ url: file.url, requestId: 'dated-request', method: 'GET',
  type: 'main_frame', statusCode: 200, responseHeaders: datedHeaders(file, extra) });
const datedProofs = ['etag', 'last-modified'];

// ---- experience: a site that refuses VESPER's own request is not asked again for a while

test('an extension update preserves local outcome history', async () => {
  const app = load();
  const promising = () => app.evaluate(`VesperExperience.promising('files.example', 'takeover')`);
  for (let count = 0; count < 2; count++)
    await app.evaluate(`VesperExperience.note('files.example', 'takeover', 'refused')`);
  assert.equal(await promising(), false);

  await app.install({ reason: 'chrome_update' });
  assert.equal(await promising(), false, 'a browser update changes nothing VESPER learned');

  await app.install({ reason: 'update' });
  assert.equal(await promising(), false);
  assert.doesNotMatch(JSON.stringify(app.localData.vesperExperience), /files\.example|takeover/);
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
      generation, playerKey: 'player-1', playerPlaying: true, mediaChangedAt: changedAt }, sender);
    return { reply, asked: app.sent.slice(before).map(message => message.type) };
  };
}

test('a slow social page verdict is read before any CDN lookup', async () => {
  for (const code of ['no-media', 'protected']) {
    let finishPage;
    const app = load({
      'media-options': () => new Promise(resolve => { finishPage = resolve; }),
      'media-source-options': () => checkedSource(15)
    });
    try {
      const play = singleVideoPlayer(app, 'https://www.tiktok.com/@example/video/1234567');
      const result = play(4);
      for (let i = 0; !finishPage && i < 20; i++) await new Promise(resolve => setImmediate(resolve));
      assert.ok(finishPage);
      assert.deepEqual(app.sent.map(request => request.type), ['media-options']);
      finishPage({ok:false,code});
      const { reply } = await result;
      if (code === 'protected') {
        assert.equal(reply.code, 'protected');
        assert.equal(app.sent.length, 1);
      } else {
        assert.ok(reply.ok && reply.data.sourceChoices.length === 1);
        assert.deepEqual(app.sent.map(request => request.type), ['media-options', 'media-source-options']);
      }
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

test('social feed playback and prefetch never initiate format probes', async () => {
  for (const page of ['https://www.tiktok.com/foryou', 'https://www.instagram.com/']) {
    const app = load({ 'media-options': checkedSource() });
    const sender = { url: page, tab: { id: 41 }, frameId: 0, documentId: 'feed-document' };
    try {
      await app.onMedia({ url: 'https://cdn.example.com/playing.mp4', tabId: 41, frameId: 0,
        documentId: sender.documentId, method: 'GET', statusCode: 200, type: 'media',
        timeStamp: Date.now(), responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
      await app.message({ type: 'media-element', watching: true }, sender);
      const reply = await app.message({ type: 'media-options', prefetch: true, pageUrl: page,
        playerPageUrl: page, playerUrl: 'blob:' + page, generation: 1 }, sender);
      assert.equal(reply.data.prefetchSkipped, true);
      assert.equal(app.sent.length, 0, page);
    } finally { app.stopWorker(); }
  }
});

test('social page refusal ends the click without probing observed CDN neighbours', async () => {
  for (const code of ['blocked', 'rate-limited', 'site-refused', 'sign-in', 'browser-context']) {
    const app = load({ 'media-options': { ok: false, code }, 'media-source-options': checkedSource() });
    const page = 'https://www.tiktok.com/@example/video/1234567890';
    const sender = { url: page, tab: { id: 42 }, frameId: 0, documentId: 'refusal-document' };
    try {
      const changedAt = Date.now();
      await app.onMedia({ url: 'https://cdn.example.com/neighbor.mp4', tabId: 42, frameId: 0,
        documentId: sender.documentId, method: 'GET', statusCode: 200, type: 'media',
        timeStamp: changedAt, responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
      const reply = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
        playerUrl: 'blob:' + page, generation: 2, playerPlaying: true, mediaChangedAt: changedAt }, sender);
      assert.equal(reply.code, code);
      assert.deepEqual(app.sent.map(request => request.type), ['media-options'], code);
    } finally { app.stopWorker(); }
  }
});

test('a social CDN refusal stops the remaining observed-source checks after one click', async () => {
  const app = load({ 'media-options': { ok: false, code: 'no-media' },
    'media-source-options': { ok: false, code: 'site-refused' } });
  const page = 'https://www.instagram.com/reel/example/';
  const sender = { url: page, tab: { id: 43 }, frameId: 0, documentId: 'cdn-refusal-document' };
  try {
    const changedAt = Date.now();
    for (const name of ['first.mp4', 'second.mp4'])
      await app.onMedia({ url: `https://cdn.example.com/${name}`, tabId: 43, frameId: 0,
        documentId: sender.documentId, method: 'GET', statusCode: 200, type: 'media',
        timeStamp: changedAt, responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
    const reply = await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page,
      playerUrl: 'blob:' + page, generation: 2, playerPlaying: true, mediaChangedAt: changedAt }, sender);
    assert.equal(reply.code, 'site-refused');
    assert.deepEqual(app.sent.map(request => request.type), ['media-options', 'media-source-options']);
  } finally { app.stopWorker(); }
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

test('an attached playlist can predate playback and a typed playlist keeps its observed kind', async () => {
  const page = 'https://embed.example/player';
  const url = 'https://cdn.example/asset-123456/master.m3u8';
  const opaqueUrl = 'https://cdn.example/asset-123456/read';
  const sender = { url: page, tab: { id: 35 }, frameId: 2, documentId: 'preloaded-player' };
  const app = load({ 'media-options': { ok: true, data: { choices: [{ index: 0, label: 'Best quality' }] } } });
  const changedAt = Date.now();
  const response = { url, tabId: 35, frameId: 2, documentId: sender.documentId,
    method: 'GET', statusCode: 200, type: 'xmlhttprequest', timeStamp: changedAt - 45_000 };
  await app.onMedia({ ...response, responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }] });
  const request = { type: 'media-options', pageUrl: page, playerPageUrl: page, playerPlaying: true,
    playerUrl: 'blob:https://embed.example/player', manifestUrl: url, generation: 8, mediaChangedAt: changedAt };
  await app.message(request, sender);
  assert.equal(app.sent.find(message => message.type === 'media-options').candidates.length, 0);
  await app.message({ ...request, manifestOnPlayer: true }, sender);
  const candidate = app.sent.filter(message => message.type === 'media-options').at(-1).candidates[0];
  assert.equal(candidate.url, url);
  assert.equal(candidate.kind, 'hls');
  assert.equal(candidate.mimeType, 'application/vnd.apple.mpegurl');

  await app.onMedia({ ...response, url: opaqueUrl, timeStamp: changedAt - 10_000,
    responseHeaders: [{ name: 'Content-Type', value: 'application/dash+xml' }] });
  await app.message({ ...request, manifestUrl: opaqueUrl }, sender);
  const typed = app.sent.filter(message => message.type === 'media-options').at(-1).candidates[0];
  assert.equal(typed.url, opaqueUrl);
  assert.equal(typed.kind, 'dash');
});

test('a direct JSON-LD hint does not suppress observed playlist discovery for a blob player', async () => {
  const page = 'https://embed.example/player';
  const sender = { url: page, tab: { id: 35 }, frameId: 2, documentId: 'player-with-preview' };
  const manifest = 'https://cdn.example/asset-123456/master.m3u8';
  const preview = 'https://cdn.example/preview.mp4';
  const app = load({ 'media-source-options': { ok: true, data: { choices: [{ index: 0, label: 'Best quality' }] } } });
  const changedAt = Date.now();
  for (const [url, mime, timeStamp] of [[manifest, 'application/vnd.apple.mpegurl', changedAt],
    [preview, 'video/mp4', changedAt - 10_000]]) {
    await app.onMedia({ url, tabId: 35, frameId: 2, documentId: sender.documentId,
      method: 'GET', statusCode: 200, type: 'xmlhttprequest', timeStamp, responseHeaders: [{ name: 'Content-Type', value: mime }] });
  }
  await app.message({ type: 'media-options', pageUrl: page, playerPageUrl: page, playerPlaying: true,
    playerUrl: 'blob:https://embed.example/player', manifestUrl: preview, generation: 8, mediaChangedAt: changedAt }, sender);
  assert.equal(app.sent.find(message => message.type === 'media-source-options').candidates[0].url, manifest);
  assert.equal(app.sent.flatMap(message => message.candidates || []).some(candidate => candidate.url === preview), false);
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
    playerUrl: 'blob:https://video.example/player', manifestUrl: staleManifest, manifestOnPlayer: true, generation: 8,
    playerPlaying: true, mediaChangedAt: Date.now(), playerFacts: { durationSeconds: 120 } }, sender);

  assert.equal(result.ok, true);
  assert.deepEqual([...app.sent.find(message => message.type === 'media-options').candidates], []);
  assert.equal(app.sent.some(message => message.type === 'media-source-options'), false);
});

test('verified playback facts exclude a trailer and label the remaining source choices', async () => {
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
    '12:00 · 720p · video only · HLS · 1 format'
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

test('WebM initialization fragments and numbered chunks do not become source choices', async () => {
  const page = 'https://video.example/watch/movie';
  const sender = { url: page, tab: { id: 37 }, frameId: 0, documentId: 'webm-doc' };
  const app = load({ 'media-options': { ok: false, code: 'no-media' }, 'media-source-options': checkedSource() });
  const urls = ['init-0.1080p.vp9.webm', 'chunk-0-00001.1080p.vp9.webm']
    .map(file => 'https://cdn.video.example/movie.webm/' + file);
  for (const url of urls) {
    await app.onMedia({ url, tabId: 37, frameId: 0, documentId: sender.documentId,
      method: 'GET', statusCode: 200, type: 'xmlhttprequest',
      responseHeaders: responseHeaders({ 'Content-Type': 'video/webm', 'Content-Length': '47000' }) });
  }
  const reply = await app.message(videoOptionsMessage(page, { playerUrl: 'blob:https://video.example/player',
    playerHasMedia: true, playerFacts: { durationSeconds: 634, kind: 'video' } }), sender);
  assert.equal(reply.ok, false);
  assert.equal(app.sent.some(request => request.candidates?.some(candidate => urls.includes(candidate.url))), false);
});

test('a page lookup cannot offer a two-second preview for a long current player', async () => {
  const app = load({ 'media-options': { ok: true, data: { duration: 2, choices: [{ index: 0, label: 'MP4' }] } } });
  const result = await app.message(videoOptionsMessage(videoPage, { playerFacts: { durationSeconds: 86, kind: 'video' } }), videoSender);
  assert.equal(result.ok, false);
  assert.equal(result.code, 'source-changed');
});

test('an unverified 185-byte MP4 is not offered for a playing video', async () => {
  const page = 'https://video.example/watch/movie';
  const url = 'https://cdn.video.example/header.mp4';
  const sender = { url: page, tab: { id: 37 }, frameId: 0, documentId: 'short-response' };
  const app = load({ 'media-source-options': { ok: true, data: { choices: [{ index: 0, label: 'MP4', sizeBytes: 185 }] } } });
  await app.onMedia({ url, tabId: 37, frameId: 0, documentId: sender.documentId, method: 'GET',
    statusCode: 200, type: 'xmlhttprequest', timeStamp: Date.now(),
    responseHeaders: responseHeaders({ 'Content-Type': 'video/mp4', 'Content-Length': '185' }) });
  const reply = await app.message(videoOptionsMessage(page, {
    playerUrl: 'blob:https://video.example/player', playerHasMedia: true, mediaChangedAt: Date.now(),
    playerFacts: { durationSeconds: 5.5, kind: 'video' }
  }), sender);
  assert.equal(reply.ok, false);
  assert.equal(reply.data?.sourceChoices, undefined);
  assert.equal(reply.data?.browserBodyAvailable, undefined);
  assert.equal(app.sent.filter(request => request.type === 'media-source-options').length, 1);
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

test('a single witnessed slice can propose a full resource while contradictory ranges stay blocked',async()=>{
  const page='https://www.instagram.com/reel/ranged/', base='https://cdn.example.com/asset.mp4?signature=abc%2Fdef';
  const sender={url:page,tab:{id:81},frameId:0,documentId:'query-range-document'};
  const changedAt=Date.now(), urls=[base+'&bytestart=0&byteend=999',base+'&bytestart=1000&byteend=1999'];
  const hash=async url=>Buffer.from(await webcrypto.subtle.digest('SHA-256',new TextEncoder().encode(url))).toString('hex');
  for(const scenario of ['verified','one-witness','changed-tag','wrong-offset','other-document','changed-after-picker',
    'query-200','query-200-different-tags','query-200-incorrect-length','query-200-replaced']){
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
      await report(0,'"v1"');await report(1,['changed-tag','query-200-different-tags'].includes(scenario)?'"v2"':'"v1"');
      const request={type:'media-options',pageUrl:page,playerPageUrl:page,playerUrl:'blob:https://www.instagram.com/current',
        generation:5,playerKey:'p',playerPlaying:true,mediaChangedAt:changedAt,
        playerResourceFingerprints:await Promise.all((scenario==='one-witness'?urls.slice(0,1):urls).map(hash))};
      const result=await app.message(request,sender);
      const checks=app.sent.filter(m=>m.type==='media-range-options');
      if(['changed-tag','wrong-offset'].includes(scenario)){
        assert.equal(checks.length,0,scenario);
        assert.equal(result.ok,false,scenario);
        continue;
      }
      assert.equal(checks.length,1);
      assert.equal(checks[0].candidates[0].url,base);
      const querySlice = scenario.startsWith('query-200');
      assert.equal(checks[0].candidates[0].observedETag, querySlice ? undefined : '"v1"');
      assert.equal(checks[0].candidates[0].observedLength,querySlice ? undefined : 700000);
      assert.equal(checks[0].candidates[0].observedMinimumLength,
        querySlice ? (scenario === 'query-200-incorrect-length' ? 1000 : 2000) : undefined);
      assert.equal(result.data.sourceChoices.length,1);
      if(['changed-after-picker','query-200-replaced'].includes(scenario))await report(1,'"changed"',changedAt+1);
      const selected=await app.message({...request,type:'media-source-select',sourceChoice:result.data.sourceChoices[0].token},sender);
      assert.equal(selected.ok,!['changed-after-picker','query-200-replaced'].includes(scenario),scenario);
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

test('specific post declaration can verify an observed direct file without assigning it to the player', async () => {
  const page = 'https://www.facebook.com/reel/1234567890';
  const url = 'https://cdn.test/clip.mp4';
  const sender = { url: page, tab: { id: 87 }, frameId: 0, documentId: 'declared-direct' };
  const app = load({ 'media-source-options': checkedSource(15) });
  const changedAt = Date.now();
  try {
    await app.onMedia({ url, tabId: 87, frameId: 0, documentId: sender.documentId, method: 'GET',
      statusCode: 200, type: 'xmlhttprequest', timeStamp: changedAt,
      responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
    const request = { type: 'media-options', pageUrl: page, playerPageUrl: page, playerUrl: '',
      manifestUrl: url, playerHasMedia: true, generation: 1, mediaChangedAt: changedAt };
    const wrong = await app.message(request, { ...sender, documentId: 'foreign' });
    assert.equal(wrong.code, 'no-reusable-source');
    assert.equal(app.sent.length, 0);
    const generic = 'https://www.facebook.com/';
    const ambiguous = await app.message({ ...request, pageUrl: generic, playerPageUrl: generic }, { ...sender, url: generic });
    assert.equal(ambiguous.code, 'no-reusable-source');
    assert.equal(app.sent.length, 0);
    const reply = await app.message(request, sender);
    assert.equal(reply.ok, true);
    assert.equal(reply.data.sourceChoices.length, 1);
    assert.equal(reply.data.choices, undefined);
    assert.equal(app.sent.length, 1);
    assert.equal(app.sent[0].candidates[0].url, url);
  } finally { app.stopWorker(); }
});

test('generic feed attached declaration requires current document evidence and explicit selection', async () => {
  const page = 'https://www.facebook.com/';
  const url = 'https://cdn.test/opaque-source';
  const sender = { url: page, tab: { id: 88 }, frameId: 0, documentId: 'attached-direct' };
  const app = load({ 'media-source-options': checkedSource(15) });
  const changedAt = Date.now();
  try {
    await app.onMedia({ url, tabId: 88, frameId: 0, documentId: sender.documentId, method: 'GET',
      statusCode: 200, type: 'xmlhttprequest', timeStamp: changedAt,
      responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
    const request = { type: 'media-options', pageUrl: page, playerPageUrl: page, playerUrl: '',
      manifestUrl: url, manifestOnPlayer: true, playerHasMedia: true, generation: 1, mediaChangedAt: changedAt };
    const wrong = await app.message(request, { ...sender, documentId: 'other' });
    assert.equal(wrong.code, 'no-reusable-source');
    assert.equal(app.sent.length, 0);
    const unbound = await app.message({ ...request, manifestOnPlayer: false }, sender);
    assert.equal(unbound.code, 'no-reusable-source');
    assert.equal(app.sent.length, 0);
    const reply = await app.message(request, sender);
    assert.equal(reply.ok, true);
    assert.equal(reply.data.sourceChoices.length, 1);
    assert.equal(reply.data.choices, undefined);
    assert.equal(app.sent.length, 1);
  } finally { app.stopWorker(); }
});

test('service-worker playback never becomes an HTTP replay or passive prefetch', async () => {
  const page='https://example.com/watch';
  const sender={url:page,tab:{id:90},frameId:0,documentId:'sw-playback'};
  const app=load();
  const request={type:'media-options',pageUrl:page,playerPageUrl:page,playerUrl:'https://example.com/media.mp4',
    playerKey:'player-sw',generation:1,mediaChangedAt:Date.now(),playerHasMedia:true,serviceWorkerPlayback:true};
  try {
    assert.equal((await app.message({...request,prefetch:true},sender)).data.prefetchSkipped,true);
    assert.equal((await app.message(request,sender)).code,'no-reusable-source');
    assert.equal((await app.message({...request,type:'browser-body-media-proof'},sender)).ok,false);
    assert.equal(app.sent.length,0);
  } finally {app.stopWorker();}
});

test('worker-only player declaration is a confirmation choice, not an observed source', async () => {
  const page = 'https://www.facebook.com/';
  const url = 'https://cdn.test/worker.mpd';
  const sender = { url: page, tab: { id: 90 }, frameId: 0, documentId: 'worker-declaration' };
  const app = load({ 'media-source-options': checkedSource(15) });
  const request = { type: 'media-options', pageUrl: page, playerPageUrl: page, playerUrl: '',
    manifestUrl: url, manifestOnPlayer: true, workerMediaSource: true, playerHasMedia: true,
    playerKey: 'player-worker', generation: 1, mediaChangedAt: Date.now() };
  try {
    for (const change of [{workerMediaSource:false}, {manifestOnPlayer:false}, {playerKey:''},
      {manifestUrl:url+'?signature=x'}, {manifestUrl:'http://cdn.test/worker.mpd'}, {manifestUrl:'https://cdn.test/segment-1.m4s'}]) {
      const refused = await app.message({...request,...change},sender);
      assert.equal(refused.code,'no-reusable-source');
    }
    await app.message({...request,prefetch:true},sender);
    assert.equal(app.sent.length,0);
    const select = async change => {
      const options = await app.message(request,sender);
      assert.equal(options.ok,true);
      assert.equal(options.data.choices,undefined);
      const source = options.data.sourceChoices[0];
      assert.equal(source.requiresConfirmation,true);
      assert.equal(source.fingerprint,undefined);
      return app.message({type:'media-source-select',sourceChoice:source.token,pageUrl:page,
        playerUrl:'',playerKey:request.playerKey,generation:1,workerMediaSource:true,manifestUrl:url,...change},sender);
    };
    assert.equal((await select({manifestUrl:'https://cdn.test/new.mpd'})).code,'expired-selection');
    assert.equal((await select({generation:2})).code,'expired-selection');
    assert.equal((await select({workerMediaSource:false})).code,'expired-selection');
    assert.equal((await select({serviceWorkerPlayback:true})).code,'expired-selection');
    assert.equal(app.sent.length,0);
    const reply=await select({});
    assert.equal(reply.ok,true);
    assert.equal(app.sent.length,1);
    assert.equal(app.sent[0].candidates[0].url,url);
    assert.equal(app.sent[0].authorized,undefined);
    assert.equal(app.sent[0].hostCredentials,undefined);
  } finally { app.stopWorker(); }
});

test('worker without a declaration offers only bounded current-document choices, not automatic formats', async () => {
  const page = 'https://www.facebook.com/';
  const url = 'https://cdn.test/worker.mp4';
  const sender = { url: page, tab: { id: 91 }, frameId: 0, documentId: 'worker-observed' };
  const app = load({ 'media-source-options': { ok: true, data: {duration:15, choices:[{index:0,hasVideo:true}] } } });
  const at = Date.now();
  const request = { type:'media-options', pageUrl:page, playerPageUrl:page, playerUrl:'',
    workerMediaSource:true, playerHasMedia:true, playerKey:'worker-player', generation:1, mediaChangedAt:at,
    playerFacts:{kind:'video',durationSeconds:15,height:720} };
  const observe = (address, documentId=sender.documentId, seen=at) => app.onMedia({
    url:address, tabId:91, frameId:0, documentId, method:'GET', statusCode:200,
    type:'xmlhttprequest', timeStamp:seen, responseHeaders:[
      {name:'Content-Type',value:'video/mp4'},{name:'Content-Length',value:'2000000'}] });
  try {
    await observe('https://cdn.test/foreign.mp4','other-document');
    await observe('https://cdn.test/old.mp4',sender.documentId,at-5000);
    assert.equal((await app.message(request,sender)).code,'no-reusable-source');
    await observe(url);
    assert.equal((await app.message({...request,multiplePlayers:true},sender)).code,'no-reusable-source');
    await app.message({...request,prefetch:true},sender);
    assert.equal(app.sent.length,0);
    const options = await app.message(request,sender);
    assert.equal(options.ok,true);
    assert.equal(options.data.choices,undefined);
    assert.equal(options.data.sourceChoices.length,1);
    const choice = options.data.sourceChoices[0];
    assert.equal(choice.requiresConfirmation,true);
    assert.equal(choice.requiresDeclaration,false);
    assert.equal(choice.fingerprint,undefined);
    assert.equal(app.sent.length,0);
    const select = (token, change = {}) => app.message({type:'media-source-select',sourceChoice:token,pageUrl:page,
      playerUrl:'',playerKey:request.playerKey,generation:1,workerMediaSource:true,manifestUrl:'',multiplePlayers:false,...change},sender);
    assert.equal((await select(choice.token,{multiplePlayers:true})).code,'expired-selection');
    const next = await app.message({...request,refresh:true},sender);
    const reply = await select(next.data.sourceChoices[0].token);
    assert.equal(reply.ok,true);
    assert.equal(app.sent.length,1);
    assert.equal(app.sent[0].candidates[0].url,url);
    assert.equal(app.sent[0].authorized,undefined);
    const stale = await app.message({...request,refresh:true},sender);
    await observe('https://cdn.test/another.mp4');
    assert.equal((await select(stale.data.sourceChoices[0].token)).code,'expired-selection');
    const both = await app.message({...request,refresh:true},sender);
    assert.equal(both.data.sourceChoices.length,2);
    await observe('https://cdn.test/opaque.mp4?token=secret');
    assert.equal((await select(both.data.sourceChoices[0].token)).code,'expired-selection');
    assert.equal((await app.message({...request,refresh:true},sender)).code,'no-reusable-source');
    assert.equal(app.sent.length,1);
  } finally {app.stopWorker();}
});

test('confirmed opaque-worker format session rechecks its evidence before the actual download, including restart', async () => {
  const page='https://example.test/watch', url='https://cdn.test/worker.mp4';
  const sender={url:page,tab:{id:93},frameId:0,documentId:'worker-receipt'};
  const mediaSession='e'.repeat(32), sessionData={};
  const responses={'media-source-options':{ok:true,data:{mediaSession,duration:15,choices:[{index:0,hasVideo:true}]}},
    'media-select':{ok:true}};
  const app=load(responses,{sessionData});
  const at=Date.now();
  const scope={pageUrl:page,playerPageUrl:page,playerKey:'player-93',generation:1,playerUrl:''};
  const request={...scope,type:'media-options',workerMediaSource:true,playerHasMedia:true,multiplePlayers:false,
    mediaChangedAt:at,playerFacts:{kind:'video',durationSeconds:15,height:720}};
  const select={...scope,type:'media-select',mediaSession,formatChoice:0,mode:'download',
    workerMediaSource:true,manifestUrl:'',multiplePlayers:false};
  const observe=(target,address)=>target.onMedia({url:address,tabId:93,frameId:0,documentId:sender.documentId,
    method:'GET',statusCode:200,type:'xmlhttprequest',timeStamp:at,responseHeaders:[
      {name:'Content-Type',value:'video/mp4'},{name:'Content-Length',value:'2000000'}]});
  let restarted;
  try {
    await observe(app,url);
    const options=await app.message(request,sender);
    assert.equal(options.data.sourceChoices.length,1);
    const source=options.data.sourceChoices[0];
    const result=await app.message({...scope,type:'media-source-select',sourceChoice:source.token,
      workerMediaSource:true,manifestUrl:'',multiplePlayers:false},sender);
    assert.equal(result.data.mediaSession,mediaSession);
    assert.equal(JSON.stringify(sessionData.vesperFormatSessions).includes(url),false);
    assert.equal((await app.message({...select,multiplePlayers:true},sender)).code,'expired-selection');
    assert.equal((await app.message(select,sender)).ok,true);
    restarted=load(responses,{sessionData});
    assert.equal((await restarted.message(select,sender)).ok,true);
    await observe(restarted,'https://cdn.test/competing.mp4');
    assert.equal((await restarted.message(select,sender)).code,'expired-selection');
    assert.equal(restarted.sent.filter(r=>r.type==='media-select').length,1);
  } finally {app.stopWorker();restarted?.stopWorker();}
});

test('generic worker handle cannot bypass confirmation via ordinary traffic or a matched declaration', async () => {
  const page='https://example.test/watch';
  const url='https://cdn.test/worker.mpd';
  const sender={url:page,tab:{id:92},frameId:0,documentId:'generic-worker'};
  const app=load({'media-source-options':checkedSource(15)});
  const at=Date.now();
  const request={type:'media-options',pageUrl:page,playerPageUrl:page,playerUrl:'',
    workerMediaSource:true,playerHasMedia:true,playerKey:'worker',generation:1,mediaChangedAt:at,
    playerFacts:{kind:'video',durationSeconds:15,height:720}};
  try {
    await app.onMedia({url,tabId:92,frameId:0,documentId:sender.documentId,method:'GET',statusCode:200,
      type:'xmlhttprequest',timeStamp:at,responseHeaders:[{name:'Content-Type',value:'application/dash+xml'}]});
    const prefetch=await app.message({...request,prefetch:true},sender);
    assert.equal(prefetch.data.prefetchSkipped,true);
    for(const change of [{},{manifestUrl:url,manifestOnPlayer:true}]){
      const reply=await app.message({...request,...change,refresh:true},sender);
      assert.equal(reply.ok,true);
      assert.equal(reply.data.choices,undefined);
      assert.equal(reply.data.sourceChoices[0].requiresConfirmation,true);
    }
    assert.equal(app.sent.length,0);
  } finally {app.stopWorker();}
});


test('copied-buffer path hint admits a unique current source only for explicit verification', async () => {
  const page = 'https://www.facebook.com/';
  const path = 'https://cdn.test/opaque-media';
  const url = path + '?token=first';
  const sender = { url: page, tab: { id: 89 }, frameId: 0, documentId: 'copy-hint' };
  const app = load({ 'media-source-options': checkedSource(15) });
  const changedAt = Date.now();
  const observe = (address, documentId = sender.documentId) => app.onMedia({
    url: address, tabId: 89, frameId: 0, documentId, method: 'GET', statusCode: 200,
    type: 'xmlhttprequest', timeStamp: changedAt,
    responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }] });
  try {
    await observe(url);
    await observe('https://cdn.test/neighbor.mp4');
    const request = { type: 'media-options', pageUrl: page, playerPageUrl: page, playerUrl: '',
      playerFragmentUrls: [path], playerHasMedia: true, generation: 1, mediaChangedAt: changedAt };
    assert.equal((await app.message(request, { ...sender, documentId: 'foreign' })).code, 'no-reusable-source');
    await app.message({ ...request, prefetch: true }, sender);
    assert.equal(app.sent.length, 0);
    const reply = await app.message(request, sender);
    assert.equal(reply.ok, true);
    assert.equal(reply.data.sourceChoices.length, 1);
    assert.equal(reply.data.choices, undefined);
    assert.equal(app.sent[0].candidates[0].url, url);
    await observe(path + '?token=second');
    const ambiguous = await app.message({ ...request, refresh: true }, sender);
    assert.equal(ambiguous.code, 'no-reusable-source');
    assert.equal(app.sent.length, 1);
  } finally { app.stopWorker(); }
});

test('Facebook looks up the active story source without resolving the surrounding page', async () => {
  const page = 'https://www.facebook.com/stories/12345/UzpfTEST=/';
  const playerUrl = 'https://video.example/story.mp4';
  const sender = { url: page, tab: { id: 14 }, frameId: 0, documentId: 'facebook-story' };
  const app = load({ 'media-source-options': checkedSource(14) });
  try {
    const request = { type: 'media-options', pageUrl: page, playerPageUrl: page, playerHasMedia: true,
      generation: 1, mediaChangedAt: Date.now() };
    const unavailable = await app.message(request, sender);
    assert.equal(unavailable.code, 'no-reusable-source');
    assert.deepEqual(app.sent, []);

    const formats = await app.message({ ...request, playerUrl }, sender);
    assert.equal(formats.ok, true);
    assert.deepEqual(app.sent.map(message => message.type), ['media-source-options']);
    assert.equal(app.sent[0].candidates[0].url, playerUrl);
  } finally { app.stopWorker(); }
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
  const check = app.sent.find(r => r.type === 'media-range-options');
  assert.equal(check.candidates[0].url, url);
  assert.equal(check.candidates[0].observedLength, 120);
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
  assert.equal(reply.code, rateLimit.code);
  assert.equal(reply.data.retryAt, rateLimit.data.retryAt);
  assert.equal(reply.data.mediaFailure.postLookup, rateLimit.code);
  assert.deepEqual(app.actions, ['native']);
});

test('a managed watch leaves Chrome-owned download destinations unchanged', async () => {
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
  assert.equal(app.filename(download), undefined, 'A live watch must not redirect Chrome-owned files.');
  assert.equal(app.filename({ ...download, referrer: 'https://blocked.example/' }), undefined);
  assert.equal(app.filename({ ...download, incognito: true }), undefined);
});

test('Chrome fallback while paused keeps its destination after a managed watch', async () => {
  const download = { ...activeDownload(), url: 'blob:https://example.com/report', filename: 'C:\\Downloads\\report.xlsx' };
  const app = load({}, { download, activity: true, localData: {
    vesperPaused: true, vesperManagedDownloads: [{ id: download.id, startTime: download.startTime }]
  } });
  await app.recover();
  const port = app.ports[0];
  assert.ok(port);
  assert.equal(port.posted[0].files.length, 1);
  port.reply({ ok: true, data: { managedDownloads: true, siteRules: { neverCaptureHosts: [] } } });

  assert.equal(app.filename(download), undefined);
  assert.equal(download.state, 'in_progress');
  assert.deepEqual(app.actions, []);
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
  first.stopWorker();
  Object.assign(download, { state: 'complete', fileSize: 50, exists: true });
  const next = load({ 'browser-managed': { ok: true } }, { download, activity: true, localData: first.localData });
  await next.recover();
  assert.equal(next.sent.some(message => message.operation === 'complete'), false);
  assert.equal(download.state, 'complete');
  assert.deepEqual(next.actions, []);
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

test('passive media options are skipped on Facebook and X, and on a YouTube home preview', async () => {
  for (const page of ['https://www.facebook.com/reel/123456789012', 'https://fb.watch/abc123/',
    'https://x.com/home', 'https://twitter.com/home']) {
    const app = load({ 'media-options': mediaOptionsReply });
    const result = await app.message(videoOptionsMessage(page, { prefetch: true }),
      { url: page, tab: { id: 52, url: page, active: true }, frameId: 0, documentId: 'feed-doc' });
    assert.equal(result?.data?.prefetchSkipped, true, page);
    assert.equal(app.sent.some(message => message.type === 'media-options'), false, page);
    app.stopWorker();
  }

  const home = 'https://www.youtube.com/';
  const preview = videoPage;
  const app = load({ 'media-options': mediaOptionsReply });
  const result = await app.message(videoOptionsMessage(preview, { prefetch: true }),
    { url: home, tab: { id: 53, url: home, active: true }, frameId: 0, documentId: 'home-doc' });
  assert.equal(result?.data?.prefetchSkipped, true);
  assert.equal(app.sent.some(message => message.type === 'media-options'), false);
  app.stopWorker();
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
    const reply = await blocked.message(videoOptionsMessage(address, extra), { ...sender, url: address, tab: { id: 17, url: address } });
    assert.deepEqual(blocked.cookieLookups, []);
    if (extra.prefetch) {
      assert.equal(reply.data.prefetchSkipped, true);
      assert.equal(blocked.sent.length, 0);
    } else assert.equal(blocked.sent[0].mediaAuth, undefined);
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

test('a click during range prefetch gets picker choices from a single verification', async () => {
  const page = 'https://example.com/watch/current';
  const url = 'https://media.example/current.webm';
  const sender = { tab: { id: 22, url: page }, url: page, frameId: 0, documentId: 'range-doc' };
  let started, complete;
  const verifying = new Promise(resolve => { started = resolve; });
  const result = new Promise(resolve => { complete = resolve; });
  const app = load({ 'media-range-options': () => { started(); return result; } });
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

    const warm = app.message({ ...message, prefetch: true }, sender);
    await verifying;
    const click = app.message(message, sender);
    await new Promise(resolve => setImmediate(resolve));
    complete(mediaOptionsReply);
    const prepared = await warm;
    assert.equal(prepared.data.prepared, true);
    const reply = await click;
    assert.equal(reply.ok, true);
    assert.ok(reply.data.choices?.length || reply.data.sourceChoices?.length);
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

// ---------------------------------------------------------------- matching Chrome's download to its response

const responseHeaders = headers => Object.entries(headers).map(([name, value]) => ({ name, value }));
const zipHeaders = { 'Content-Length': '1000', ETag: '"version-1"', 'Content-Type': 'application/zip', 'Content-Disposition': 'attachment' };
const decisions = app => app.sessionData.vesperCaptureDecisions || [];
const outcomeOf = app => decisions(app)[0]?.outcome;

// ---------------------------------------------------------------- telling the user why Chrome kept a download (VES-021)

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

test('revoked media policy allows only a prior receipt, never a new submission', async () => {
  const token = 'e'.repeat(32);
  let blocked = false;
  const accepted = new Set();
  const app = load({
    hello: () => ({ ok: true, data: { siteRules: { neverCaptureHosts: blocked ? ['www.youtube.com'] : [] } } }),
    'media-options': { ok: true, data: { mediaSession: token, choices: [{ index: 0 }] } },
    'media-select': request => { accepted.add(request.id); return { ok: true }; },
    'media-selection-status': request => ({ ok: accepted.has(request.id) })
  });
  const scope = { pageUrl: videoPage, playerPageUrl: videoPage, playerKey: 'player-1', generation: 1, playerUrl: '' };
  const select = { type: 'media-select', mediaSession: token, formatChoice: 0, mode: 'download', ...scope };
  assert.equal((await app.message({ type: 'media-options', ...scope }, videoSender)).ok, true);
  assert.equal((await app.message(select, videoSender)).ok, true);
  blocked = true;
  assert.equal((await app.message(select, videoSender)).ok, true);
  assert.equal((await app.message({ ...select, mode: 'queue' }, videoSender)).code, 'expired-selection');
  assert.equal(app.sent.filter(r => r.type === 'media-select').length, 1);
  assert.equal(app.sent.filter(r => r.type === 'media-selection-status').length, 2);
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
test('Chrome misses omit response hints shared by different documents', async () => {
  const file=activeDownload();
  const app=load({'browser-capture':started,'browser-transfer':transfer('active')},{download:file});
  try {
    for (const tabId of [3,4]) await app.onHeaders({url:file.url,requestId:'conflict-'+tabId,tabId,frameId:0,
      documentId:'document-'+tabId,method:'GET',type:'main_frame',statusCode:200,responseHeaders:responseHeaders(zipHeaders)});
    await app.onCreated(file);
    assert.equal(app.sent.filter(isBrowserSubmission).length,0);
    assert.equal(file.paused,false);
    assert.equal(outcomeOf(app), 'failed');
    assert.equal(decisions(app)[0].response, undefined);
  } finally {app.stopWorker();}
});

for (const refusal of ['sign-in', 'network', 'timeout']) {
test(`browser-session acquisition after ${refusal} requires the current document and a strong validator`, async () => {
  const page='https://video.example/watch/1', url='https://video.example/media/movie.mp4';
  const sender={tab:{id:71},frameId:0,documentId:'media-document-71',url:page};
  const failure={ok:false,code:refusal,message:'The native request did not succeed.'};
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
}
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
    assert.equal(app.sent.filter(m=>m.type==='media-source-options').length,1,
      'a global helper refusal must stop the serial verification queue');
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

test('Chrome misses omit old response hints', async()=>{
  const file=activeDownload();
  const app=load({'browser-capture':started,'browser-submit':started},{download:file,prefs:{intercept:true,route:'auto'}});
  await observe(app,file,{requestId:'old-attachment'});
  app.advanceTime(31_000);
  await app.onCreated(file);
  assert.equal(decisions(app)[0].response, undefined);
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

test('a contradictory current HTTP representation invalidates the picker and existing session', async () => {
  const page='https://video.example/watch', url='https://cdn.example/movie.mp4', token='b'.repeat(32);
  const sender={url:page,tab:{id:87},frameId:0,documentId:'changed-current-file'};
  const app=load({'media-options':{ok:true,data:{mediaSession:token,choices:[{index:0}]}},'media-select':{ok:true}});
  const seen=Date.now();
  const base={url,tabId:87,frameId:0,documentId:sender.documentId,method:'GET',statusCode:200,type:'media'};
  const report=(tag,timeStamp)=>app.onMedia({...base,timeStamp,responseHeaders:responseHeaders({
    'Content-Type':'video/mp4','Content-Length':'700000',ETag:tag})});
  const scope={pageUrl:page,playerPageUrl:page,playerUrl:url,playerKey:'player-1',generation:1,mediaChangedAt:seen};
  try {
    await report('"v1"',seen);
    assert.equal((await app.message({type:'media-options',...scope},sender)).ok,true);
    await report('"v2"',seen+1);
    assert.equal((await app.message({type:'media-select',...scope,mediaSession:token,formatChoice:0,mode:'download'},sender)).ok,false);
    assert.equal(app.sent.filter(r=>r.type==='media-select').length,0);
    assert.equal((await app.message({type:'media-options',...scope},sender)).code,'source-changed');
    assert.equal((await app.message({type:'media-options',...scope,refresh:true},sender)).code,'source-changed');
  } finally {app.stopWorker();}
});

test('normal range progression preserves a verified picker but changed or missing identity does not',async()=>{
  const page='https://video.example/watch',url='https://cdn.example/movie.mp4',token='c'.repeat(32);
  const sender={url:page,tab:{id:87},frameId:0,documentId:'progressing-file'};
  for(const [nextTag,nextTotal,allowed] of [['"v1"',700000,true],['"v2"',700000,false],['"v1"',800000,false],[null,700000,false]]){
    const reply={ok:true,data:{mediaSession:token,choices:[{index:0}]}};
    const app=load({'media-options':reply,'media-range-options':reply,'media-source-options':reply,'media-select':{ok:true}});
    const seen=Date.now();
    const base={url,tabId:87,frameId:0,documentId:sender.documentId,method:'GET',statusCode:206,type:'media'};
    const report=(tag,total,start,stamp)=>app.onMedia({...base,timeStamp:stamp,responseHeaders:responseHeaders({
      'Content-Type':'video/mp4','Content-Length':'1000','Content-Range':'bytes '+start+'-'+(start+999)+'/'+total,
      ...(tag?{ETag:tag}:{})})});
    const scope={pageUrl:page,playerPageUrl:page,playerUrl:url,playerKey:'player-1',generation:1,mediaChangedAt:seen};
    try{
      await report('"v1"',700000,0,seen);
      assert.equal((await app.message({type:'media-options',...scope},sender)).ok,true);
      await report(nextTag,nextTotal,1000,seen+1);
      const result=await app.message({type:'media-select',...scope,mediaSession:token,formatChoice:0,mode:'download'},sender);
      assert.equal(result.ok,allowed);
      assert.equal(app.sent.filter(r=>r.type==='media-select').length,allowed?1:0);
    }finally{app.stopWorker()}
  }
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

test('two early requests for the same click never send the second download to Chrome', async () => {
  let finishProbe, enteredProbe;
  const probeStarted = new Promise(resolve => { enteredProbe = resolve; });
  const probe = new Promise(resolve => { finishProbe = resolve; });
  const app = load({ 'browser-capture': started, 'browser-transfer': transfer('active') }, {
    earlyFetch: () => { enteredProbe(); return probe; }
  });
  try {
    const request = { type: 'early-download', url: earlyUrl };
    const first = app.message(request, earlySender);
    await probeStarted;
    assert.equal((await app.message(request, earlySender)).ok, true);
    finishProbe(earlyHeaders());
    assert.equal((await first).ok, true);
    assert.equal(app.sent.filter(message => message.type === 'browser-capture').length, 1);
    assert.deepEqual(app.downloadHistory, []);
  } finally { app.stopWorker(); }
});
test('Chrome creation during the response probe suppresses native and default replay', async () => {
  let finish, entered;
  const probeEntered = new Promise(resolve => { entered = resolve; });
  const probe = new Promise(resolve => { finish = resolve; });
  const file = {...activeDownload(),url:earlyUrl,finalUrl:earlyUrl,bytesReceived:0};
  const app = load({'browser-capture':started,'browser-transfer':transfer('active')},
    {download:file,earlyFetch:()=>{entered();return probe;}});
  try {
    const pending = app.message({type:'early-download',url:earlyUrl},earlySender);
    await probeEntered;
    await app.onCreated(file);
    finish(earlyHeaders());
    assert.equal((await pending).browserOwned,true);
    assert.equal(app.sent.filter(m=>m.type==='browser-capture').length,0);
    assert.deepEqual(app.actions,[]);
  } finally { app.stopWorker(); }
});
test('Chrome creation during native staging abandons before accepting', async () => {
  let finish, entered;
  const stageEntered = new Promise(resolve=>{entered=resolve;});
  const stage = new Promise(resolve=>{finish=resolve;});
  const file = {...activeDownload(),url:earlyUrl,finalUrl:earlyUrl,bytesReceived:0};
  const app = load({'browser-capture':()=>{entered();return stage;},'browser-transfer':transfer('failed')},
    {download:file,earlyFetch:async()=>earlyHeaders()});
  try {
    const pending=app.message({type:'early-download',url:earlyUrl},earlySender);
    await stageEntered;
    await app.onCreated(file);
    finish(started);
    assert.equal((await pending).browserOwned,true);
    assert.deepEqual(app.sent.filter(m=>m.type==='browser-transfer').map(m=>m.operation),['abandon']);
    assert.deepEqual(app.actions.filter(action=>action!=='native'),[]);
  } finally {app.stopWorker();}
});
function earlyHeaders(url=earlyUrl, changes={}) {
  return {status:200,url,headers:new Headers({'Content-Type':'application/octet-stream','Content-Length':'500000','ETag':'"v1"','Content-Disposition':'attachment',...changes})};
}
test('trusted direct intent stages and accepts before any Chrome download exists', async()=>{
  const app=load({'browser-capture':started,'browser-transfer':transfer('active')},{earlyFetch:async(url,opts)=>{
    assert.equal(url,earlyUrl);assert.equal(opts.method,'HEAD');assert.equal(opts.credentials,'omit');return earlyHeaders();
  }});
  try{
    const reply=await app.message({type:'early-download',url:earlyUrl,clickAt:Date.now()-20},earlySender);
    assert.equal(reply.ok,true);
    for (const key of ['dispatchMs','probeMs','stageMs','acceptMs','clickToAcceptMs'])
      assert.ok(Number.isSafeInteger(reply.timing[key]) && reply.timing[key]>=0, key);
    assert.ok(reply.timing.clickToAcceptMs>=reply.timing.dispatchMs);
    assert.deepEqual(app.sent.map(m=>m.type),['browser-capture','browser-transfer']);
    assert.equal(app.sent[0].browserProof.etag,'"v1"');
    assert.equal(app.sent[1].operation,'accept');
    assert.equal(app.downloadHistory.length,0);
  }finally{app.stopWorker()}
});
test('successful startup timing is local, bounded and contains no source identifiers', async () => {
  const localData = { vesperStartupTimings: Array.from({ length: 64 }, () => ({ kind: 'startup-timing' })) };
  const app = load({ 'browser-capture': started, 'browser-transfer': transfer('active') }, {
    diagnosticBuild: 'timing-test', localData, earlyFetch: async () => earlyHeaders()
  });
  try {
    const reply = await app.message({ type: 'early-download', url: earlyUrl, clickAt: Date.now() - 10 }, earlySender);
    assert.equal(reply.ok, true);
    await new Promise(resolve => setImmediate(resolve));
    const entries = app.localData.vesperStartupTimings;
    assert.equal(entries.length, 64);
    const last = entries.at(-1);
    assert.equal(last.kind, 'startup-timing');
    assert.equal(last.build, 'timing-test');
    assert.equal(last.facts.clickToAcceptMs, reply.timing.clickToAcceptMs);
    assert.deepEqual(Object.keys(last).sort(), ['build', 'facts', 'kind', 'utc']);
    assert.ok(!JSON.stringify(last).includes('example.com'));
  } finally { app.stopWorker(); }
});
test('public GitHub archives use the correct codeload URL even without size or a strong validator', async () => {
  const page = 'https://github.com/owner/project';
  const url = page + '/archive/refs/heads/main.zip';
  const target = 'https://codeload.github.com/owner/project/zip/refs/heads/main';
  const requests = [];
  const app = load({ 'browser-capture': started, 'browser-transfer': transfer('active') }, {
    prefs: { useSignIn: true }, signInPermission: true,
    earlyFetch: async (address, options) => {
      requests.push([address, options.credentials, options.redirect]);
      return { status: 200, url: target, redirected: true, headers: new Headers({
        'Content-Type': 'application/zip', 'Content-Disposition': 'attachment; filename="project-main.zip"', ETag: 'W/"commit"'
      }) };
    }
  });
  try {
    const sender = { ...earlySender, url: page, tab: { ...earlySender.tab, url: page } };
    assert.equal((await app.message({ type: 'early-download', url }, sender)).ok, true);
    assert.deepEqual(requests, [[url, 'omit', 'follow']]);
    assert.equal(app.sent[0].items[0].url, target);
    assert.equal(app.sent[0].items[0].originalUrl, url);
    assert.equal(app.sent[0].browserProof.length, undefined);
    assert.equal(app.sent[0].browserProof.basis, 'github-archive');
    assert.deepEqual(app.cookieLookups, []);
    assert.deepEqual(app.downloadHistory, []);
  } finally { app.stopWorker(); }
});

test('direct codeload archives accept weak ETags without length, but not unrelated endpoints', async () => {
  const url='https://codeload.github.com/owner/project/zip/refs/heads/main';
  const sender={...earlySender,url:'https://github.com/owner/project'};
  for(const [address,final,expected] of [[url,url,true],[url,url+'/wrong',false],
    ['https://codeload.github.com/zip/file', 'https://codeload.github.com/zip/file', false],
    ['https://example.com/owner/project/zip/refs/heads/main','https://example.com/owner/project/zip/refs/heads/main',false]]){
    const app=load({'browser-capture':started,'browser-transfer':transfer('active')},{
      prefs:{useSignIn:true},signInPermission:true,earlyFetch:async()=>({status:200,url:final,headers:new Headers({
        'Content-Type':'application/zip','Content-Disposition':'attachment; filename="project-main.zip"',ETag:'W/"commit"'
      })})
    });
    try{
      const reply=await app.message({type:'early-download',url:address},sender);
      assert.equal(reply.ok===true,expected);
      assert.equal(app.sent.length,expected?2:0);
      if(expected){
        assert.equal(app.sent[0].browserProof.basis,'github-archive');
        assert.equal(app.sent[0].items[0].url,url);
        assert.deepEqual(app.cookieLookups,[]);
      }
    }finally{app.stopWorker()}
  }
});

test('a failed pre-click probe explains the resulting Chrome miss without reopening it', async () => {
  const file = { ...activeDownload(), url: earlyUrl, bytesReceived: 1 };
  const app = load({}, { download: file, earlyFetch: async () => { throw new Error('probe failed'); } });
  try {
    const reply = await app.message({ type: 'early-download', url: earlyUrl }, earlySender);
    assert.equal(reply.fallback, true);
    await app.onCreated(file);
    assert.equal(app.sessionData.vesperCaptureDecisions[0].reason, 'probe-failed');
    assert.equal(app.sessionData.vesperCaptureDecisions[0].received, 1);
    assert.deepEqual(app.actions, []);
  } finally { app.stopWorker(); }
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
test('explicit download labels admit only bounded attachment endpoints',async()=>{
  const url='https://example.com/download?id=45';
  for(const [attachment,expected] of [['attachment; filename="report.pdf"',true],['inline',false]]){
    const app=load({'browser-capture':started,'browser-transfer':transfer('active')},
      {earlyFetch:async()=>earlyHeaders(url,{'Content-Type':'application/pdf','Content-Disposition':attachment})});
    try{
      const cand=app.evaluate('VesperEarlyDownload.candidate');
      assert.equal(cand(url,earlySender.url),null);
      assert.equal(cand(url,earlySender.url,null,true),url);
      assert.equal(cand(url+'&token=opaque',earlySender.url,null,true),null);
      const click=app.evaluate('VesperEarlyDownload.clickCandidate');
      const anchor={tagName:'A',href:url,textContent:'Download file',hasAttribute:()=>false,getAttribute:()=>null};
      const event={isTrusted:true,defaultPrevented:false,cancelable:true,button:0,composedPath:()=>[anchor]};
      assert.equal(click(event,earlySender.url).explicitIntent,true);
      assert.equal(click({...event,composedPath:()=>[{...anchor,textContent:'Learn more'}]},earlySender.url),null);
      const reply=await app.message({type:'early-download',url,explicitIntent:true},earlySender);
      assert.equal(reply.ok,expected);
      assert.equal(app.sent.filter(x=>x.type==='browser-capture').length,expected?1:0);
    }finally{app.stopWorker();}
  }
});

test('explicit same-origin named exports admit extensionless URLs and safe query parameters',async()=>{
  for(const [url,name,mime] of [
    ['https://example.com/export','archive.zip','application/zip'],
    ['https://example.com/export?id=45&format=csv','report.csv','text/csv'],
    ['https://example.com/export?format=md','notes.md','text/markdown'],
    ['https://example.com/export?format=patch','fix.patch','text/x-diff'],
    ['https://example.com/export','unknown-format','application/x-custom'],
    ['https://example.com/export','', 'application/x-custom']
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
    ['https://example.com/export','../paper.pdf','application/pdf']
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
test('early handoff protects the referring document path across origins',async()=>{
  const source='https://example.com/private/report?access=internal';
  const url='https://cdn.example.net/report.zip';
  const app=load({'browser-capture':started,'browser-transfer':transfer('active')},{earlyFetch:async address=>earlyHeaders(address)});
  try{
    const policy=app.evaluate('VesperEarlyDownload.referrer');
    assert.equal(policy(source,url,''),'https://example.com/');
    assert.equal(policy(source,url,'same-origin'),undefined);
    assert.equal(policy(source,'https://example.com/report.zip','origin'),'https://example.com/');
    assert.equal(policy(source,'https://example.com/report.zip','no-referrer'),undefined);
    assert.equal(policy(source,'http://example.com/report.zip','unsafe-url'),undefined);
    const sender={...earlySender,url:source};
    assert.equal((await app.message({type:'early-download',url},sender)).ok,true);
    assert.equal(app.sent[0].items[0].referer,'https://example.com/');
  }finally{app.stopWorker()}
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

test('F-46: archive paths and repository query parameters are recognized as early download candidates', () => {
  const app = load({});
  try {
    const page = 'https://github.com/DarkOrbitWeaver/VESPER';
    const cand = app.evaluate('VesperEarlyDownload.candidate');
    // GitHub zip endpoint
    assert.equal(cand('https://github.com/DarkOrbitWeaver/VESPER/archive/refs/heads/main.zip', page),
      'https://github.com/DarkOrbitWeaver/VESPER/archive/refs/heads/main.zip');
    // Codeload extensionless zip path
    assert.equal(cand('https://codeload.github.com/DarkOrbitWeaver/VESPER/zip/refs/heads/main', page),
      'https://codeload.github.com/DarkOrbitWeaver/VESPER/zip/refs/heads/main');
    // GitLab archive with ref query parameter
    assert.equal(cand('https://gitlab.com/group/repo/-/archive/main/repo-main.tar.gz?ref=main', 'https://gitlab.com/group/repo'),
      'https://gitlab.com/group/repo/-/archive/main/repo-main.tar.gz?ref=main');
    // Tarball path
    assert.equal(cand('https://github.com/DarkOrbitWeaver/VESPER/tarball/main', page),
      'https://github.com/DarkOrbitWeaver/VESPER/tarball/main');
  } finally { app.stopWorker(); }
});

test('a Chrome-completed archive is never imported into VESPER history', async () => {
  const codeloadUrl = 'https://codeload.github.com/DarkOrbitWeaver/VESPER/zip/refs/heads/main';
  const download = {
    id: 1046, url: codeloadUrl, finalUrl: codeloadUrl,
    filename: 'C:\\Downloads\\VESPER-main.zip', state: 'in_progress',
    danger: 'file', startTime: '2026-11-26T18:00:00.000Z', totalBytes: 6882236,
    bytesReceived: 0, canResume: false
  };
  const app = load({ 'browser-managed': { ok: true } }, { download, activity: true });
  await app.onCreated(download);
  // Chrome downloads it and danger becomes safe once SafeBrowsing finishes
  download.danger = 'safe';
  await app.onChanged({ id: download.id, danger: { previous: 'file', current: 'safe' } });
  // Simulated long download finishing 30 seconds later (well past 15s window)
  Object.assign(download, { state: 'complete', fileSize: 6882236, exists: true });
  await app.onChanged({ id: download.id, state: { current: 'complete' } });
  await app.recover();
  assert.equal(app.sent.some(message => message.type === 'browser-managed' && message.operation === 'complete'), false);
  assert.equal(download.state, 'complete');
  assert.equal(download.filename, 'C:\\Downloads\\VESPER-main.zip');
});

test('player identity recognizes TikTok /video/ID, Facebook share/watch, Instagram stories, and Reanime', () => {
  const app = load({});
  try {
    const id = app.evaluate('VesperPlayerSource.identity');
    assert.equal(id('https://www.tiktok.com/video/7704503448573578527'), 'tiktok:7704503448573578527');
    assert.equal(id('https://vm.tiktok.com/video/7704503448573578527'), 'tiktok:7704503448573578527');
    assert.equal(id('https://fb.watch/xyz123'), 'facebook:watch:xyz123');
    assert.equal(id('https://www.facebook.com/share/r/abc789/'), 'facebook:share:abc789');
    assert.equal(id('https://www.facebook.com/stories/12345/UzpfTEST=/?bucket_count=5'), 'facebook:story:12345:UzpfTEST=');
    assert.equal(id('https://www.instagram.com/stories/creator/350123456789/'), 'instagram:story:creator:350123456789');
    assert.equal(id('https://reanime.to/watch/demon-slayer/episode-1'), 'reanime:demon-slayer:episode-1');
  } finally { app.stopWorker(); }
});

test('YouTube and Bilibili fall back to browser-observed streams when desktop lookup is rate-limited or blocked', async () => {
  for (const [pageUrl, streamUrl, failureCode] of [
    ['https://www.youtube.com/watch?v=EK3tCffac5k', 'https://cdn.example/yt-hls/master.m3u8', 'rate-limited'],
    ['https://www.bilibili.com/video/BV1xx411c7mD', 'https://cdn.example/bili/master.m3u8', 'blocked']
  ]) {
    const app = load({
      'media-options': { ok: false, code: failureCode, message: 'Blocked on VPN' },
      'media-source-options': { ...mediaOptionsReply, data: { ...mediaOptionsReply.data, playbackMatches: 1 } }
    });
    const sender = { url: pageUrl, tab: { id: 19 }, frameId: 0, documentId: 'vpn-doc' };
    const now = Date.now();
    try {
      await app.onMedia({
        url: streamUrl, tabId: 19, frameId: 0, documentId: sender.documentId,
        method: 'GET', statusCode: 200, type: 'xmlhttprequest', timeStamp: now,
        responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }]
      });
      const reply = await app.message(videoOptionsMessage(pageUrl, {
        playerUrl: `blob:${new URL(pageUrl).origin}/player`, playerPlaying: true, mediaChangedAt: now
      }), sender);
      assert.equal(reply.ok, true, `${pageUrl} should fall back to observed traffic after ${failureCode}`);
      assert.equal(app.sent[0].type, 'media-options');
      assert.equal(app.sent[1].type, 'media-source-options');
      assert.equal(app.sent[1].candidates[0].url, streamUrl);
    } finally { app.stopWorker(); }
  }
});

test('explicit click joins in-flight anonymous prefetch instead of starting a duplicate lookup', async () => {
  let resolveLookup;
  const app = load({ 'media-options': () => new Promise(resolve => { resolveLookup = resolve; }) });
  try {
    const p1 = app.message(videoOptionsMessage(videoPage, { prefetch: true }), videoSender);
    await waitFor(() => !!resolveLookup);
    const p2 = app.message(videoOptionsMessage(videoPage, { playerFacts: { durationSeconds: 120, height: 1080 } }), videoSender);
    assert.equal(app.sent.filter(m => m.type === 'media-options').length, 1);
    resolveLookup(mediaOptionsReply);
    const [r1, r2] = await Promise.all([p1, p2]);
    assert.equal(r1.ok, true);
    assert.equal(r2.ok, true);
    assert.deepEqual(r2.data.choices, mediaOptionsReply.data.choices);
  } finally { app.stopWorker(); }
});

test('early-download catches target=_blank, noreferrer privacy, and 405 HEAD -> 1-byte Range fallback', async () => {
  const methods = [];
  const app = load({ 'browser-capture': started, 'browser-transfer': transfer('active') }, {
    earlyFetch: async (url, opts) => {
      methods.push(opts.method);
      if (opts.method === 'HEAD') return { status: 405, url, headers: new Headers() };
      return {
        status: 206, url,
        headers: new Headers({
          'Content-Type': 'application/octet-stream',
          'Content-Range': 'bytes 0-0/500000',
          'ETag': '"ranged-v1"',
          'Content-Disposition': 'attachment'
        }),
        body: { cancel: async () => {} }
      };
    }
  });
  try {
    const click = app.evaluate('VesperEarlyDownload.clickCandidate');
    const anchor = {
      tagName: 'A', href: earlyUrl, target: '_blank', rel: 'noopener noreferrer',
      hasAttribute: () => false, getAttribute: n => n === 'download' ? null : null
    };
    const clicked = click({
      isTrusted: true, defaultPrevented: false, cancelable: true, button: 0,
      composedPath: () => [anchor]
    }, 'https://example.com/releases');
    assert.equal(clicked.url, earlyUrl);

    const reply = await app.message({ type: 'early-download', url: earlyUrl, omitReferrer: true }, earlySender);
    assert.equal(reply.ok, true);
    assert.deepEqual(methods, ['HEAD', 'GET']);
    assert.equal(app.sent[0].items[0].referer, undefined);
    assert.equal(app.sent[0].browserProof.length, 500000);
    assert.equal(app.sent[0].browserProof.etag, '"ranged-v1"');
  } finally { app.stopWorker(); }
});

test('an early file HEAD refusal leaves Chrome as owner without a second site request', async () => {
  for (const status of [401, 403, 404, 429]) {
    const methods = [];
    const app = load({}, { earlyFetch: async (url, options) => {
      methods.push(options.method);
      return { status, url, headers: new Headers() };
    } });
    try {
      const reply = await app.message({ type: 'early-download', url: earlyUrl }, earlySender);
      assert.equal(reply.fallback, true);
      assert.deepEqual(methods, ['HEAD'], `HTTP ${status}`);
      assert.deepEqual(app.sent, []);
    } finally { app.stopWorker(); }
  }
});

test('TikTok lookup returning source-changed falls back to observed CDN stream', async () => {
  const pageUrl = 'https://www.tiktok.com/@creator/video/7608248907960814879';
  const cdnUrl = 'https://v16-webapp-prime.tiktokcdn.com/video/tos/clip.mp4';
  const sender = { url: pageUrl, tab: { id: 44 }, frameId: 0, documentId: 'tt-doc' };
  const now = Date.now();
  const app = load({
    'media-options': { ok: false, code: 'source-changed', message: 'TikTok player state was not found.' },
    'media-source-options': checkedSource(18)
  });
  try {
    await app.onMedia({
      url: cdnUrl, tabId: 44, frameId: 0, documentId: sender.documentId,
      method: 'GET', statusCode: 200, type: 'media', timeStamp: now,
      responseHeaders: [{ name: 'Content-Type', value: 'video/mp4' }, { name: 'Content-Length', value: '800000' }]
    });
    const reply = await app.message(videoOptionsMessage(pageUrl, {
      playerUrl: cdnUrl, playerPlaying: true, mediaChangedAt: now
    }), sender);
    assert.equal(reply.ok, true);
    assert.equal(app.sent[0].type, 'media-options');
    assert.equal(app.sent[1].type, 'media-source-options');
    assert.equal(app.sent[1].candidates[0].url, cdnUrl);
  } finally { app.stopWorker(); }
});

test('Instagram and TikTok do not start automatic format lookups', async () => {
  for (const page of ['https://www.instagram.com/reels/VesperFixture/', 'https://www.tiktok.com/@owner/video/7849240376246100000']) {
    const app = load({ 'media-options': mediaOptionsReply });
    try {
      const request = videoOptionsMessage(page, { prefetch: true, playerPlaying: true });
      const reply = await app.message(request, { tab: { id: 8, active: true }, url: page, documentId: 'social-fixture' });
      assert.equal(reply.data.prefetchSkipped, true);
      assert.equal(app.sent.length, 0);
    } finally { app.stopWorker(); }
  }
});

test('social source access refusals stop, while no-media may try the post without leaking private details', async () => {
  for (const [page, code] of [
    ['https://www.instagram.com/reel/sample/', 'site-refused'],
    ['https://www.tiktok.com/@sample/video/7849240376246100000', 'no-media'],
    ['https://www.instagram.com/reel/sample/', 'blocked']
  ]) {
    const url = 'https://media.example/clip.mp4?token=private-source';
    const fingerprint = Buffer.from(await webcrypto.subtle.digest('SHA-256', new TextEncoder().encode(url))).toString('hex');
    const sender = { url: page, tab: { id: 73 }, frameId: 0, documentId: 'private-document' };
    const now = Date.now();
    const app = load({ 'media-options': checkedSource(18), 'media-source-options': { ok: false, code } });
    try {
      await app.onMedia({ url, tabId: 73, frameId: 0, documentId: sender.documentId,
        method: 'GET', statusCode: 200, type: 'media', timeStamp: now,
        responseHeaders: responseHeaders({ 'Content-Type': 'video/mp4' }) });
      const reply = await app.message(videoOptionsMessage(page, {
        playerUrl: 'blob:' + new URL(page).origin + '/media-id',
        playerPlaying: true, mediaChangedAt: now, playerResourceFingerprints: [fingerprint]
      }), sender);
      assert.equal(app.sent[0].type, 'media-source-options');
      if (code === 'site-refused') {
        assert.equal(app.sent.length, 1, 'a browser-only option makes no second native site request');
        assert.equal(reply.data.browserBodyAvailable, true);
        assert.equal(reply.data.browserBodyProof.url, url);
      } else if (code === 'blocked') {
        assert.equal(app.sent.length, 1);
        assert.equal(reply.code, code);
        const report = reply.data.mediaFailure;
        assert.equal(report.boundPost, true);
        assert.equal(report.exactMatches, 1);
        assert.deepEqual([...report.observedLookups], [code]);
        assert.equal(report.postLookup, null);
        assert.doesNotMatch(JSON.stringify(report), /https:|private-source|private-document|media-id/);
      } else {
        assert.equal(app.sent[1].type, 'media-options');
        assert.equal(app.sent[1].url, page);
        assert.equal(reply.ok, true);
      }
    } finally { app.stopWorker(); }
  }
});

test('an Instagram picker can use the current Chrome file after a source-changed refusal', async () => {
  const page = 'https://www.instagram.com/reels/VesperFixture/';
  const url = 'https://video.cdninstagram.com/reel.mp4';
  const now = Date.now();
  const sender = { url: page, tab: { id: 44 }, documentId: 'reel-document', frameId: 0 };
  const failure = { ok: false, code: 'source-changed' };
  const app = load({ 'media-options': failure, 'media-source-options': failure });
  try {
    await app.onMedia({ url, tabId: 44, documentId: sender.documentId, frameId: 0,
      method: 'GET', type: 'media', statusCode: 200, timeStamp: now,
      responseHeaders: responseHeaders({ 'Content-Type': 'video/mp4', 'Content-Length': '800000' }) });
    const request = videoOptionsMessage(page, { playerUrl: url, playerPlaying: true, mediaChangedAt: now });
    const result = await app.message(request, sender);
    assert.equal(result.data.browserBodyProof.url, url);
    assert.equal(result.data.browserBodyProof.length, 800000);
    assert.equal(result.data.browserBodyProof.workerFetch, true);
    assert.equal((await app.message(request, { ...sender, documentId: 'other-document' })).data?.browserBodyAvailable, undefined);
  } finally { app.stopWorker(); }
});
