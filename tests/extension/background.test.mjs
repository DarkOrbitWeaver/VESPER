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
  let onMessage, onCreated, onChanged, onHeaders, onMedia, onAlarm, onContextClicked;
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
        cb((typeof reply === 'function' ? reply(message) : reply)
          ?? { ok: false, code: 'host-error', message: 'no reply' });
      }
    },
    webRequest: { onResponseStarted: { addListener: fn => { onMedia = fn; } }, onHeadersReceived: { addListener: fn => { onHeaders = fn; } } },
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
  const context = { chrome, URL, crypto: { randomUUID: () => `test-uuid-${++sequence}-aaaa` }, navigator: { userAgent: 'test' }, setTimeout, clearTimeout, console };
  vm.createContext(context);
  context.importScripts = (...files) => {
    for (const file of files) vm.runInContext(readFileSync(new URL(`../../extension/${file}`, import.meta.url), 'utf8'), context);
  };
  vm.runInContext(readFileSync(new URL('../../extension/background.js', import.meta.url), 'utf8') + '\n;globalThis.__describe = describe;', context);
  return { describe: context.__describe, sent, actions, cookieLookups, badges, local, sessionData, downloadHistory, onCreated, onChanged, onHeaders, onMedia, onContextClicked, ports,
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

function observe(app, file, { method = 'GET', type = 'main_frame', contentType = 'application/zip', attachment = true } = {}) {
  const headers = { 'Content-Length': String(file.fileSize), ETag: '"version-1"' };
  if (contentType) headers['Content-Type'] = contentType;
  if (attachment) headers['Content-Disposition'] = 'attachment; filename="archive.zip"';
  return app.onHeaders({ url: file.finalUrl || file.url, method, type, statusCode: 200,
    responseHeaders: Object.entries(headers).map(([name, value]) => ({ name, value })) });
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
