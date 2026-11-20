import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = name => readFileSync(new URL(`../../extension/${name}.js`, import.meta.url), 'utf8');

test('the butterfly motion picker has Animate and Off', () => {
  const popup = readFileSync(new URL('../../extension/popup.html', import.meta.url), 'utf8');
  const selectTag = popup.match(/<select id="motion"([^>]*)>([\s\S]*?)<\/select>/);
  const select = selectTag?.[2];
  assert.ok(select);
  const options = [...select.matchAll(/<option value="([^"]+)">([^<]+)<\/option>/g)]
    .map(([, value, label]) => [value, label]);
  assert.deepEqual(options, [
    ['animate', 'Animate'], ['still', 'Off']
  ]);
});

test('browser sign-in settings require approval for the current HTTPS host', () => {
  const popup = readFileSync(new URL('../../extension/popup.html', import.meta.url), 'utf8');
  assert.match(popup, /Use my sign-in/);
  assert.match(popup, /id="signin-site" disabled/);
  assert.match(popup, /id="signin-host"/);
});

test('opening the popup shows the installed extension version without checking the app', async () => {
  for (const versionName of [undefined, '1.0.2-firstflight.3']) {
    const elements = new Map();
    const messages = [];
    const preferences = [];
    const permissionRequests = [];
    const getElementById = id => {
      if (!elements.has(id)) elements.set(id, {});
      return elements.get(id);
    };
    const chrome = {
      runtime: {
        getManifest: () => ({ version: '1.0.2', version_name: versionName }),
        sendMessage: async message => { messages.push(message); return { ok: false }; }
      },
      storage: {
        sync: {
          get: async defaults => ({ ...defaults, route: 'chrome', handoff: 'default', motion: 'system' }),
          set: async value => preferences.push(structuredClone(value))
        },
        local: { get: async () => ({ vesperSeen: true, prefetchFormats: true }) },
        session: { get: async () => ({}) }
      },
      permissions: {
        contains: async () => permissionRequests.length > 0,
        request: async value => { permissionRequests.push(structuredClone(value)); return true; },
        onRemoved: { addListener() {} }
      },
      tabs: { query: async () => [] }
    };
    vm.runInNewContext(source('popup'), { chrome, document: { getElementById }, VesperTheme: { install() {} } });
    await new Promise(resolve => setImmediate(resolve));

    assert.equal(getElementById('version').textContent, `Extension ${versionName || '1.0.2'}`);
    const browserDownloads = getElementById('browser-downloads');
    assert.equal(browserDownloads.value, 'chrome');
    assert.equal(getElementById('handoff').value, 'default');
    assert.equal(getElementById('prefetch').checked, true);
    assert.equal(getElementById('motion').value, 'animate');
    assert.equal(getElementById('signin').checked, true);
    const allow = getElementById('signin-permission');
    assert.equal(allow.hidden, false);
    assert.deepEqual(messages, []);
    assert.deepEqual(permissionRequests, []);
    browserDownloads.value = 'manual';
    await browserDownloads.onchange();
    browserDownloads.value = 'automatic';
    await browserDownloads.onchange();
    await allow.onclick();
    assert.deepEqual(permissionRequests, [{ permissions: ['cookies'] }]);
    assert.equal(allow.hidden, true);
    assert.deepEqual(preferences, [
      { route: 'auto', intercept: false }, { route: 'auto', intercept: true }, { useSignIn: true }
    ]);
  }
});

test('the media picker wraps long metadata and keeps horizontal overflow inside its bounds', () => {
  const content = source('content');
  assert.match(content, /\.panel\{[^}]*overflow-x:hidden;overflow-y:auto/);
  assert.match(content, /\.head>div:nth-child\(2\)\{flex:1 1 auto;min-width:0/);
  assert.match(content, /\.t\{display:-webkit-box;[^}]*-webkit-line-clamp:2;[^}]*overflow-wrap:anywhere/);
  assert.match(content, /\.output select\{max-width:100%;min-width:0/);
  assert.match(content, /\.opt>span:nth-child\(2\)\{flex:1 1 auto;min-width:0;overflow-wrap:anywhere/);
});

test('subtitle embedding stays opt-in and travels with the native media selection', () => {
  const content = source('content');
  const background = source('background');
  assert.match(content, /embedSubtitles: false/);
  assert.match(content, /Embed selected subtitles/);
  assert.match(content, /formats\.browserChoice \? \{ type: 'browser-media-select'[\s\S]*: \{ type: 'media-select',[\s\S]*\.\.\.intent/);
  assert.match(background, /const embedSubtitles = message\.embedSubtitles === true/);
  assert.match(background, /mediaSession, formatChoice, mode, range, audioTrack, subtitles, includeAutoSubtitles, embedSubtitles/);
});

test('browser playlist browsing only opens the native picker from resolver collection metadata', () => {
  const content = source('content');
  const background = source('background');
  const options = readFileSync(new URL('../../BrowserMediaOptions.cs', import.meta.url), 'utf8');
  assert.match(content, /result\.data\?\.hasRelatedCollection/);
  assert.match(content, /type: 'native-picker', title/);
  assert.match(content, /formats\.hasRelatedCollection && !formats\.browserChoice/);
  assert.match(background, /const trustedContentPage = sender\.tab \? mediaPage\(\{\}, sender\)\?\.href : null/);
  assert.match(options, /resolved\.Collection is \{ \} collection/);
  assert.match(options, /hasRelatedCollection = HasRelatedCollection\(item\)/);
});

test('butterfly motion responds to Animate and Off', () => {
  const document = new EventTarget();
  document.querySelectorAll = () => [];
  const window = new EventTarget();
  let updatePreference;
  const context = vm.createContext({
    window, document, AbortController, URL, location: { href: 'https://example.test/watch' },
    ResizeObserver: class { observe() {} disconnect() {} },
    IntersectionObserver: class { observe() {} unobserve() {} disconnect() {} },
    VesperTheme: {
      available: () => true,
      contextGone: () => false,
      watchPreference(key, fallback, apply) {
        assert.equal(key, 'motion');
        assert.equal(fallback, 'animate');
        updatePreference = apply;
        return () => {};
      }
    },
    chrome: { runtime: { id: 'vesper-test', onMessage: { addListener() {} } } }
  });
  const content = source('content').replace(/\}\)\(\);\s*$/, `window.__testMotion = {
    still, applyMotion, setTargets: (nextHost, nextCompanion) => { host = nextHost; companion = nextCompanion; }
  };\n})();`);
  vm.runInContext(content, context);

  const attributes = new Set();
  const stillModes = [];
  window.__testMotion.setTargets({
    toggleAttribute(name, value) { if (value) attributes.add(name); else attributes.delete(name); }
  }, { setStill: value => stillModes.push(value) });

  assert.equal(window.__testMotion.still(), false);

  updatePreference('still');
  assert.equal(window.__testMotion.still(), true);
  assert.equal(attributes.has('data-vesper-still'), true);
  assert.equal(stillModes.at(-1), true);

  updatePreference('animate');
  assert.equal(window.__testMotion.still(), false);
  assert.equal(attributes.has('data-vesper-still'), false);
  assert.equal(stillModes.at(-1), false);
});

test('the context-menu picker reports when its frame has no video player', async () => {
  const document = Object.assign(new EventTarget(), { querySelectorAll: () => [] });
  const window = new EventTarget();
  const motion = Object.assign(new EventTarget(), { matches: false });
  let messageListener;
  const context = vm.createContext({
    window, document, AbortController, URL, location: { href: 'https://example.test/page' },
    matchMedia: () => motion,
    ResizeObserver: class { observe() {} disconnect() {} },
    IntersectionObserver: class { observe() {} unobserve() {} disconnect() {} },
    VesperTheme: {
      available: () => true,
      contextGone: () => false,
      watchPreference: () => () => {}
    },
    chrome: {
      runtime: { id: 'vesper-test', onMessage: { addListener: fn => { messageListener = fn; } } },
      storage: { sync: { get: async defaults => defaults }, onChanged: { addListener() {}, removeListener() {} } }
    }
  });
  vm.runInContext(source('theme'), context);
  vm.runInContext(source('player-source'), context);
  vm.runInContext(source('content'), context);
  await Promise.resolve();

  let reply;
  messageListener({ type: 'vesper-open-picker' }, { id: 'vesper-test' }, value => { reply = value; });

  assert.equal(reply.ok, false);
  assert.equal(reply.code, 'no-player');
});

test('Smart Select associates only one clearly labelled SHA-256 with its sole nearby file link', () => {
  const smartSelect = readFileSync(new URL('../../extension/smart-select.js', import.meta.url), 'utf8')
    .replace(/\}\)\(\);\s*$/, 'globalThis.__publisherChecksum = publisherChecksum;\n})();');
  const context = vm.createContext({
    URL,
    location: { href: 'https://publisher.example/downloads/' },
    chrome: { runtime: { id: 'vesper-test', onMessage: { addListener() {} } } }
  });
  vm.runInContext(smartSelect, context);

  const url = 'https://cdn.example.test/archive.zip';
  const checksum = 'a1'.repeat(32);
  const link = { parentElement: null, dataset: {}, getAttribute: name => name === 'href' ? url : null };
  const row = { parentElement: null, innerText: `archive.zip\nSHA-256: ${checksum}`, querySelectorAll: () => [link] };
  link.parentElement = row;

  assert.equal(context.__publisherChecksum(link, url), checksum.toUpperCase());
  row.innerText = `archive.zip\nSHA-256 checksum = ${checksum}`;
  assert.equal(context.__publisherChecksum(link, url), checksum.toUpperCase());
  row.innerText = `archive.zip\nSHA-1: ${checksum}`;
  assert.equal(context.__publisherChecksum(link, url), null);
  row.innerText = `archive.zip\nSHA-256: ${checksum}\nSHA-256: ${checksum}`;
  assert.equal(context.__publisherChecksum(link, url), null);
  row.innerText = `archive.zip\nSHA-256: ${checksum}`;
  row.querySelectorAll = () => [link, { dataset: {}, getAttribute: () => 'https://cdn.example.test/other.zip' }];
  assert.equal(context.__publisherChecksum(link, url), null);
  row.querySelectorAll = () => [link];
  link.getClientRects = () => [];
  assert.equal(context.__publisherChecksum(link, url), null);
  link.getClientRects = () => [{}];
  row.getClientRects = () => [];
  assert.equal(context.__publisherChecksum(link, url), null);
});

test('an orphaned content script disconnects observers and storage listeners on the next page event', async () => {
  const document = Object.assign(new EventTarget(), { querySelectorAll: () => [] });
  const window = new EventTarget();
  const motion = Object.assign(new EventTarget(), { matches: false });
  let live = true, disconnected = 0, unsubscribed = 0;
  const context = vm.createContext({
    window, document, AbortController, URL, location: { href: 'https://example.test/watch' },
    matchMedia: () => motion, clearTimeout() {}, cancelAnimationFrame() {},
    ResizeObserver: class { disconnect() { disconnected++; } },
    IntersectionObserver: class { disconnect() { disconnected++; } },
    chrome: {
      runtime: { get id() { return live ? 'vesper-test' : undefined; }, onMessage: { addListener() {} } },
      storage: { sync: { get: async defaults => defaults }, onChanged: {
        addListener() {},
        removeListener() { unsubscribed++; throw new Error('Extension context invalidated.'); }
      } }
    }
  });
  vm.runInContext(source('theme'), context);
  vm.runInContext(source('player-source'), context);
  vm.runInContext(source('content'), context);
  await Promise.resolve();
  assert.equal(window.__vesperContent, true);
  live = false;
  document.dispatchEvent(new Event('fullscreenchange'));
  assert.equal(window.__vesperContent, undefined);
  assert.equal(disconnected, 2);
  assert.equal(unsubscribed, 1);
  document.dispatchEvent(new Event('fullscreenchange'));
  assert.equal(unsubscribed, 1);
});

test('a feed player uses the unique page linked from its card', async () => {
  class FakeElement {
    parentElement = null;
    querySelectorAll() { return []; }
  }

  const home = 'https://www.youtube.com/';
  const item = 'https://www.youtube.com/watch?v=abcdefghijk';
  const video = new FakeElement();
  const card = new FakeElement();
  const document = Object.assign(new EventTarget(), {
    body: {},
    documentElement: {},
    querySelectorAll: () => []
  });
  const link = { href: item };
  card.parentElement = document.body;
  card.querySelectorAll = selector => selector === 'a[href]' ? [link] : [video];
  video.parentElement = card;

  const window = new EventTarget();
  const motion = Object.assign(new EventTarget(), { matches: false });
  const context = vm.createContext({
    window, document, location: { href: home }, Element: FakeElement, AbortController, URL,
    matchMedia: () => motion,
    ResizeObserver: class { disconnect() {} },
    IntersectionObserver: class { disconnect() {} unobserve() {} },
    chrome: {
      runtime: { id: 'vesper-test', onMessage: { addListener() {} } },
      storage: {
        sync: { get: async defaults => defaults },
        onChanged: { addListener() {}, removeListener() {} }
      }
    }
  });
  vm.runInContext(source('theme'), context);
  vm.runInContext(source('player-source'), context);
  const content = source('content').replace(/\}\)\(\);\s*$/, 'window.__testPageForPlayer = pageForPlayer;\n})();');
  vm.runInContext(content, context);

  assert.equal(window.__testPageForPlayer(video), item);
  assert.equal(window.__testPageForPlayer(new FakeElement()), home);
});

test('a delayed hover preview keeps its linked page after the player is reset', async () => {
  class FakeElement {
    parentElement = null;
    isConnected = true;
    querySelectorAll() { return []; }
    getBoundingClientRect() { return { left: 0, top: 0, right: 480, bottom: 270, width: 480, height: 270 }; }
  }
  class FakeVideo extends FakeElement {
    currentSrc = 'https://www.youtube.com/videoplayback?id=preview';
    src = this.currentSrc;
    readyState = 1;
    paused = false;
  }

  const home = 'https://www.youtube.com/';
  const item = 'https://www.youtube.com/watch?v=abcdefghijk';
  const body = {};
  const card = new FakeElement();
  const video = new FakeVideo();
  const unrelatedVideo = new FakeVideo();
  const sent = [];
  const timers = [];
  const node = () => ({
    setAttribute() {}, matches: () => false,
    classList: { add() {}, remove() {}, toggle() {}, contains: () => false }
  });
  let videos = [];
  let hoverTarget = card;
  card.parentElement = body;
  card.querySelectorAll = selector => selector === 'a[href]' ? [{ href: item }]
    : selector === 'video' ? videos : [];
  video.parentElement = card;

  const document = Object.assign(new EventTarget(), {
    body,
    documentElement: {},
    createElement: node,
    querySelectorAll: () => [],
    elementFromPoint: () => hoverTarget
  });
  const window = new EventTarget();
  const motion = Object.assign(new EventTarget(), { matches: false });
  const context = vm.createContext({
    window, document, location: { href: home }, Element: FakeElement,
    HTMLMediaElement: FakeVideo, HTMLVideoElement: FakeVideo,
    AbortController, URL, innerWidth: 1280, innerHeight: 720,
    getComputedStyle: () => ({ visibility: 'visible' }),
    clearTimeout() {},
    setTimeout: (_handler, delay) => timers.push(delay),
    matchMedia: () => motion,
    ResizeObserver: class { observe() {} disconnect() {} },
    IntersectionObserver: class { observe() {} unobserve() {} disconnect() {} },
    chrome: {
      runtime: { id: 'vesper-test', onMessage: { addListener() {} },
        sendMessage: async message => { sent.push(message); return { ok: false, code: 'protected' }; } },
      storage: {
        sync: { get: async defaults => defaults },
        onChanged: { addListener() {}, removeListener() {} }
      }
    }
  });
  vm.runInContext(source('theme'), context);
  vm.runInContext(source('player-source'), context);
  const content = source('content').replace(/\}\)\(\);\s*$/, `window.__testContent = {
    onPointer, consider, handlePlayerPlaying, setSelectPlayer: fn => { selectPlayer = fn; },
    activePreview: () => activePreview, holdMissingPreview, loadFormats,
    attachForTest: (player, controls) => {
      video = player; perch = { x: 10, y: 10 };
      ({ button, panel, receipt } = controls);
      companion = { setPickerOpen() {}, setVisible() {} };
      renderShell = () => {};
    }
  };\n})();`);
  vm.runInContext(content, context);
  await Promise.resolve();

  window.__testContent.onPointer({ target: card, clientX: 220, clientY: 100 });
  assert.equal(window.__testContent.activePreview(), null);

  videos = [video];
  window.__testContent.consider(video);
  window.__testContent.consider(unrelatedVideo);
  window.__testContent.setSelectPlayer(() => {});
  window.__testContent.handlePlayerPlaying(unrelatedVideo);
  assert.equal(window.__testContent.activePreview(), null);
  window.__testContent.handlePlayerPlaying(video);

  assert.equal(window.__testContent.activePreview()?.player, video);
  assert.equal(window.__testContent.activePreview()?.page, item);
  window.__testContent.attachForTest(video, { button: node(), panel: node(), receipt: node() });
  video.readyState = 0;
  assert.equal(window.__testContent.holdMissingPreview(), true);
  assert.deepEqual(timers, [900]);

  await window.__testContent.loadFormats();
  assert.equal(sent[0].pageUrl, item);
  assert.equal(sent[0].playerUrl, '');
  assert.equal(sent[0].playerPlaying, false);
});
