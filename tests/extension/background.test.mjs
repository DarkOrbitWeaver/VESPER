// Runs the real background.js in a sandbox with a mocked `chrome` API. Run: node --test tests/extension
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

function load(nativeReplies = {}, { download, signInPermission = false, prefs = {} } = {}) {
  const listeners = [];
  const noop = () => {};
  const event = { addListener: (fn) => listeners.push(fn) };
  const storage = () => {
    const data = {};
    return {
      get: async key => structuredClone({ [key]: data[key] }),
      set: async values => Object.assign(data, structuredClone(values)),
      remove: async key => { delete data[key]; }
    };
  };
  let onMessage, onCreated, onChanged;
  const sent = [];
  const actions = [];
  const cookieLookups = [];
  const local = storage();
  const chrome = {
    runtime: {
      onMessage: { addListener: fn => { onMessage = fn; } }, onInstalled: event, onStartup: event, lastError: undefined,
      sendNativeMessage: (host, message, cb) => {
        actions.push('native');
        sent.push(message);
        cb(nativeReplies[message.type] ?? { ok: false, code: 'host-error', message: 'no reply' });
      }
    },
    webRequest: { onResponseStarted: event }, tabs: { onUpdated: event, onRemoved: event, query: async () => [] },
    contextMenus: { create: noop, removeAll: async () => {}, onClicked: event },
    downloads: {
      onCreated: { addListener: fn => { onCreated = fn; } },
      onChanged: { addListener: fn => { onChanged = fn; } },
      search: async ({ id }) => download && (id === undefined || download.id === id) ? [{ ...download }] : [],
      pause: async () => { actions.push('pause'); download.paused = true; },
      cancel: async () => actions.push('cancel'),
      erase: async () => actions.push('erase')
    },
    action: { setBadgeText: async () => {}, setBadgeBackgroundColor: async () => {} },
    storage: { sync: { get: async (d) => ({ ...d, ...prefs }), set: async () => {} }, local, session: storage() },
    permissions: { contains: async () => signInPermission },
    cookies: { getAll: async ({ url }) => { cookieLookups.push(url); return [{ name: 'session', value: 'test-value' }]; } }
  };
  const context = { chrome, URL, crypto: { randomUUID: () => 'test-uuid-0001-aaaa' }, navigator: { userAgent: 'test' }, setTimeout, clearTimeout, console };
  vm.createContext(context);
  vm.runInContext(readFileSync(new URL('../../extension/background.js', import.meta.url), 'utf8') + '\n;globalThis.__describe = describe;', context);
  return { describe: context.__describe, sent, actions, cookieLookups, local, onCreated, onChanged,
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
  filename: 'C:\\Downloads\\archive.zip'
});

test('automatic handoff pauses Chrome only after VESPER confirms data, keeping the original entry', async () => {
  const file = activeDownload();
  const app = load({ submit: { ok: true, results: [{ status: 'started', transferReady: true }] } }, { download: file });
  await app.onCreated(file);
  assert.deepEqual(app.actions, ['native', 'pause']);
  assert.equal(app.sent[0].confirmTransfer, true);
  assert.equal(app.sent[0].items[0].fileName, 'archive.zip');
  assert.equal(file.paused, true);
});

test('automatic handoff includes extensionless files and starts them by default', async () => {
  const file = { ...activeDownload(), url: 'https://example.com/export?receipt=42', filename: '' };
  const app = load({}, { download: file });
  await app.onCreated(file);
  assert.equal(app.sent[0].items[0].url, file.url);
  assert.equal(app.sent[0].mode, 'download');
  assert.equal(file.paused, false);
  assert.deepEqual(app.actions, ['native']);
});

test('automatic handoff waits for Chrome to clear a download', async () => {
  const file = { ...activeDownload(), danger: 'asyncScanning' };
  const app = load({ submit: { ok: true, results: [{ status: 'started', transferReady: true }] } }, { download: file });
  await app.onCreated(file);
  assert.equal(app.sent.length, 0);
  file.danger = 'safe';
  await app.onChanged({ id: file.id, danger: { previous: 'asyncScanning', current: 'safe' } });
  assert.deepEqual(app.actions, ['native', 'pause']);
});

test('first-click discovery uses the actual player frame and its HTTP source', async () => {
  const options = { ok: true, data: { choices: [{ index: 0, label: 'Best quality' }] } };
  const app = load({ 'media-options': options });
  const sender = { url: 'https://player.example/embed', tab: { id: 5, url: 'https://example.com/watch' } };
  const result = await app.message({ type: 'media-options', title: 'Example clip',
    pageUrl: 'https://unrelated.example/', playerUrl: 'https://cdn.example/master.m3u8?key=1' }, sender);
  assert.deepEqual(result, options);
  assert.equal(app.sent.length, 1);
  assert.equal(app.sent[0].url, sender.url);
  assert.equal(app.sent[0].candidates[0].kind, 'hls');
  assert.equal(app.sent[0].pageTitle, 'Example clip');
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
    const app = load({ submit: reply }, { download: file, prefs: { intercept: false } });
    const result = await app.message({ type: 'move-download', downloadId: file.id });
    assert.equal(result.chromePaused, false);
    assert.equal(app.sent[0].mode, 'download');
    assert.deepEqual(app.actions, ['native']);
  }
});

test('downloads awaiting Chrome safety review cannot be moved', async () => {
  const file = { ...activeDownload(), danger: 'uncommon' };
  const app = load({}, { download: file });
  assert.equal((await app.message({ type: 'chrome-downloads' })).downloads.length, 0);
  assert.equal((await app.message({ type: 'move-download', downloadId: file.id })).ok, false);
  assert.equal(app.sent.length, 0);
});

test('sign-in defaults on after permission, retries reacquire cookies, and stored requests omit credentials', async () => {
  const file = activeDownload();
  const replies = { submit: { ok: false, code: 'host-error' } };
  const app = load(replies, { download: file, signInPermission: true });
  await app.message({ type: 'move-download', downloadId: file.id });
  const id = app.sent[0].id;
  assert.equal(app.sent[0].items[0].cookie, 'session=test-value');
  assert.equal((await app.local.get('pending')).pending[id].request.items[0].cookie, undefined);
  replies.submit = { ok: true, results: [{ status: 'already-submitted', transferReady: true }] };
  assert.equal((await app.message({ type: 'retry', id })).chromePaused, true);
  assert.equal(app.sent[1].id, id);
  assert.deepEqual(app.cookieLookups, [file.url, file.url]);

  for (const opts of [{ signInPermission: false }, { signInPermission: true, prefs: { useSignIn: false } }]) {
    const optedOut = load(replies, { download: activeDownload(), ...opts });
    await optedOut.message({ type: 'move-download', downloadId: file.id });
    assert.equal(optedOut.cookieLookups.length, 0);
    assert.equal(optedOut.sent[0].authorized, false);
  }
});
