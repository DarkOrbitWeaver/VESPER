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
  assert.match(popup, /Use my Chrome sign-in/);
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
      tabs: { query: async () => [{ id: 3, url: 'https://example.com/download' }] }
    };
    vm.runInNewContext(source('popup'), { chrome, URL, document: { getElementById }, VesperTheme: { install() {} } });
    await new Promise(resolve => setImmediate(resolve));

    assert.equal(getElementById('version').textContent, `Extension ${versionName || '1.0.2'}`);
    const browserDownloads = getElementById('browser-downloads');
    assert.equal(browserDownloads.value, 'chrome');
    assert.equal(getElementById('handoff-setting').hidden, true);
    assert.equal(getElementById('manual-downloads').hidden, true);
    assert.equal(getElementById('handoff').value, 'download');
    assert.equal(getElementById('prefetch').checked, true);
    assert.equal(getElementById('motion').value, 'animate');
    assert.equal(getElementById('signin').checked, true);
    const allow = getElementById('signin-permission');
    assert.equal(allow.hidden, false);
    assert.deepEqual(messages, []);
    assert.deepEqual(permissionRequests, []);
    browserDownloads.value = 'manual';
    await browserDownloads.onchange();
    assert.equal(getElementById('handoff-setting').hidden, false);
    assert.equal(getElementById('manual-downloads').hidden, false);
    browserDownloads.value = 'automatic';
    await browserDownloads.onchange();
    assert.equal(getElementById('manual-downloads').hidden, true);
    await allow.onclick();
    assert.deepEqual(permissionRequests, [{ permissions: ['cookies'] }]);
    assert.equal(allow.hidden, true);
    assert.equal(getElementById('signin-site').checked, false);
    assert.equal(getElementById('signin-host').textContent, 'example.com');
    assert.deepEqual(preferences, [
      { route: 'auto', intercept: false }, { route: 'auto', intercept: true }, { useSignIn: true }
    ]);
  }
});

test('the media picker wraps long metadata and keeps horizontal overflow inside its bounds', () => {
  const content = source('content');
  assert.match(content, /\.panel\{[^}]*overflow-x:hidden;overflow-y:auto/);
  assert.match(content, /\.panel\{position:fixed;width:340px/);
  assert.match(content, /\.head>div:nth-child\(2\)\{flex:1 1 auto;min-width:0/);
  assert.match(content, /\.t\{display:-webkit-box;[^}]*-webkit-line-clamp:2;[^}]*overflow-wrap:anywhere/);
  assert.match(content, /:is\(\.output,\.audio-track\) select\{max-width:100%;min-width:0/);
  assert.match(content, /\.opt>span:nth-child\(2\)\{flex:1 1 auto;min-width:0;overflow-wrap:anywhere/);
  assert.match(content, /\.clip\{box-sizing:border-box;display:grid;gap:9px;[^}]*border-radius:8px/);
});

test('subtitle embedding stays opt-in and travels with the native media selection', () => {
  const content = source('content');
  const background = source('background');
  assert.match(content, /embedSubtitles: false/);
  assert.match(content, /Add subtitles to video/);
  assert.match(content, /formats\.browserChoice \? \{ type: 'browser-media-select'[\s\S]*: \{ type: 'media-select',[\s\S]*\.\.\.intent/);
  assert.match(background, /const embedSubtitles = message\.embedSubtitles === true/);
  assert.match(background, /mediaSession, formatChoice, mode, range, audioTrack, subtitles, includeAutoSubtitles, embedSubtitles/);
});

test('ended media stays bound to its player and selected range notice explains what is fetched', () => {
  const content = source('content');
  assert.match(content, /document\.addEventListener\('ended'/);
  assert.match(content, /pageIdentity === VesperPlayerSource\.identity\(pageForPlayer\(el\)\)/);
  assert.match(content, /playerHasMedia: current &&/);
  assert.match(content, /newPlayerPlaying/);
  assert.match(content, /overlapsFrame >= frameArea \* 0\.5/);
  assert.match(content, /The cut follows segment boundaries and may shift slightly/);
  assert.doesNotMatch(content, /Downloads the full file, then trims to this range/);
  assert.match(content, /clip\.hidden = false/);
  assert.match(content, /draft\.enabled && !canClip/);
  assert.match(content, /playerFacts: playerFacts && Object\.keys\(playerFacts\)\.length \? playerFacts : undefined/);
});

test('the butterfly clears fullscreen focus and tries safer edge perches first', () => {
  const content = source('content');
  assert.match(content, /button\?\.blur\(\)/);
  assert.match(content, /rect\.top \+ 56/);
  assert.match(content, /const middle = rect\.top \+ \(rect\.bottom - rect\.top - FLY_SIZE\) \/ 2/);
  assert.match(content, /control\.contains\?\.\(video\)/);
});

test('browser playlist browsing only opens the native picker from resolver collection metadata', () => {
  const content = source('content');
  const background = source('background');
  const options = readFileSync(new URL('../../BrowserMediaOptions.cs', import.meta.url), 'utf8');
  assert.match(content, /result\.data\?\.hasRelatedCollection/);
  assert.match(content, /type: 'native-picker', pageUrl, title/);
  assert.match(content, /formats\.hasRelatedCollection && !formats\.browserChoice/);
  assert.match(background, /const trustedContentPage = sender\.tab \? mediaPage\(message, sender\)\?\.href : null/);
  assert.match(options, /resolved\.Collection is \{ \} collection/);
  assert.match(options, /hasRelatedCollection = HasRelatedCollection\(item\)/);
  assert.match(options, /isLive = item\.IsLive/);
  assert.match(content, /formats\.playerLive = playerFacts\?\.live === true \|\| formats\.isLive === true/);
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

test('dragging can perch outside the player and fullscreen modes wake again after idle', () => {
  const document = Object.assign(new EventTarget(), { querySelectorAll: () => [] });
  const window = new EventTarget();
  let now = 10000, nextTimer = 0;
  const timers = new Map();
  const context = vm.createContext({
    window, document, URL, AbortController, location: { href: 'https://example.test/watch' },
    Date: class extends Date { static now() { return now; } },
    innerWidth: 800, innerHeight: 600, getComputedStyle: element => ({ visibility: 'visible', position: element.position }),
    HTMLVideoElement: class {}, HTMLIFrameElement: class {},
    ResizeObserver: class {}, IntersectionObserver: class {},
    setTimeout: (callback, delay) => {
      timers.set(++nextTimer, { callback, due: now + delay });
      return nextTimer;
    },
    clearTimeout: id => timers.delete(id),
    VesperTheme: { available: () => true, watchPreference: () => () => {} },
    chrome: { runtime: { id: 'vesper-test', onMessage: { addListener() {} } } }
  });
  const content = source('content').replace(/\}\)\(\);\s*$/, `window.testPlayer = {
    startDrag, moveDrag, finishDrag, onPointer, place, moved: () => suppressDragClick,
    setup: (player, action, picker, sprite) => {
      video = player; button = action; panel = picker; companion = sprite;
      perch = { x: 20, y: 30, faceLeft: false };
    }
  };\n})();`);
  vm.runInContext(content, context);

  const calls = [];
  const classList = { add() {}, remove() {}, toggle() {}, contains: () => false };
  const player = { isConnected: true, getBoundingClientRect: () => ({ left: 160, top: 120, right: 640, bottom: 420 }) };
  let focused = false;
  const button = {
    classList, matches: selector => focused && selector === ':focus-visible',
    blur: () => { focused = false; }, setPointerCapture() {},
    getBoundingClientRect: () => ({ left: 170, top: 130 })
  };
  window.testPlayer.setup(player, button, { classList }, {
    setPerch: (x, y) => calls.push([x, y]), setVisible: shown => calls.push(shown), pointerMove() {}
  });
  const down = { button: 0, isPrimary: true, pointerId: 1, clientX: 175, clientY: 135 };
  window.testPlayer.startDrag(down);
  window.testPlayer.moveDrag({ ...down, clientX: 110, clientY: 70 });
  window.testPlayer.finishDrag(down);
  assert.deepEqual(calls[0], [116, 76]);
  assert.equal(window.testPlayer.moved(), true);

  function advance() {
    now += 3000;
    for (const [id, { callback, due }] of [...timers]) {
      if (due <= now && timers.delete(id)) callback();
    }
  }
  const fullBox = () => ({ left: 0, top: 0, right: 800, bottom: 600 });
  for (const mode of ['native', 'fullscreen frame', 'expanded player']) {
    document.fullscreenElement = mode === 'native' ? { contains: () => true } : null;
    window.screen = mode === 'fullscreen frame' ? { width: 800, height: 600 } : { width: 1200, height: 900 };
    player.position = mode === 'expanded player' ? 'fixed' : 'static';
    player.getBoundingClientRect = fullBox;
    window.testPlayer.place();
    assert.equal(calls.at(-1), true, mode);
    advance();
    assert.equal(calls.at(-1), false, mode);
    focused = true;
    window.testPlayer.onPointer({ target: null, clientX: 210 + now, clientY: 190 });
    assert.equal(calls.at(-1), true, mode);
    assert.equal(focused, false, mode);
    advance();
    document.fullscreenElement = null;
    window.screen = { width: 1200, height: 900 };
    player.position = 'static';
    window.testPlayer.place();
    assert.equal(calls.at(-1), true, 'a normal embedded player stays visible');
  }
});

test('Smart Select filters navigation links and reports direct file details', () => {
  const smartSelect = readFileSync(new URL('../../extension/smart-select.js', import.meta.url), 'utf8')
    .replace(/\}\)\(\);\s*$/, 'globalThis.__details = fileDetails;\n})();');
  const context = vm.createContext({ URL, chrome: { runtime: { onMessage: { addListener() {} } } } });
  vm.runInContext(smartSelect, context);
  const link = { dataset: {}, getAttribute: () => null, hasAttribute: () => false };
  for (const path of ['/channel/abc', '/watch?v=123', '/playlist?list=123', '/about/']) {
    assert.equal(context.__details(link, `https://youtube.com${path}`), null);
  }
  assert.equal(context.__details(link, 'https://example.test/setup.zip?signature=hidden').type, 'ZIP archive');
  assert.equal(context.__details({ ...link, hasAttribute: key => key === 'download' }, 'https://example.test/export').type, 'File');
  const movie = context.__details(link, 'https://example.test/movie.mp4', { tagName: 'VIDEO', duration: 95 });
  assert.equal(movie.type, 'MP4 video');
  assert.equal(movie.duration, 95);
  assert.equal(context.__details(link, 'https://example.test/movie.mp4').duration, undefined);
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
  assert.equal(unsubscribed, 2);
  document.dispatchEvent(new Event('fullscreenchange'));
  assert.equal(unsubscribed, 2);
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
        local: { get: async () => ({ vesperPaused: false, vesperHiddenButterflyHosts: [] }) },
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

test('an in-page navigation finds an already loaded reel without waiting for playback', () => {
  class Video {
    isConnected = true;
    src = 'blob:https://www.instagram.com/reel';
    readyState = 1;
    paused = true;
    querySelectorAll() { return []; }
    getBoundingClientRect() { return { left: 10, top: 15, right: 430, bottom: 680 }; }
  }

  const window = Object.assign(new EventTarget(), { navigation: new EventTarget() });
  const location = { href: 'https://www.instagram.com/' };
  const videos = [];
  const document = Object.assign(new EventTarget(), { querySelectorAll: () => videos });
  const context = vm.createContext({
    window, document, location, URL, AbortController, innerWidth: 1200, innerHeight: 800,
    Element: Video, HTMLMediaElement: Video, HTMLVideoElement: Video,
    getComputedStyle: () => ({ visibility: 'visible' }),
    setTimeout: () => 1, clearTimeout() {},
    ResizeObserver: class {},
    IntersectionObserver: class { observe() {} },
    VesperTheme: { available: () => true, watchPreference: () => () => {} },
    chrome: { runtime: { id: 'vesper-test', onMessage: { addListener() {} } } }
  });
  vm.runInContext(source('player-source'), context);
  const content = source('content').replace(/\}\)\(\);\s*$/, `attach = el => { window.testPlayer = el; };\n})();`);
  vm.runInContext(content, context);

  const reel = new Video();
  videos.push(reel);
  location.href = 'https://www.instagram.com/reels/';
  window.navigation.dispatchEvent(new Event('currententrychange'));

  assert.equal(window.testPlayer, reel);
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
  let emptiedHandler;
  const addDocumentListener = document.addEventListener.bind(document);
  document.addEventListener = (type, handler, options) => {
    if (type === 'emptied') emptiedHandler = handler;
    return addDocumentListener(type, handler, options);
  };
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
    onPointer, consider, refreshPlayer, setSelectPlayer: fn => { selectPlayer = fn; },
    activePreview: () => activePreview, holdMissingPreview, loadFormats,
    choiceKey: pickerOptionKey, restorePickerDraft, clipAvailability,
    setRetainedPickerDraft: draft => { retainedPickerDraft = draft; },
    setPanelForTest: player => {
      const binding = playback.get(player);
      panelMedia = { documentIdentity: VesperPlayerSource.identity(pageAddress()), targetKey: 'test',
        player, pageUrl: pageAddress(), src: playerSource(player), generation: binding?.generation || 0 };
      panelRequest++;
      panel.classList.add('show');
      return panelRequest;
    },
    isCurrentRequest, panelOpenForTest: () => !!panelMedia,
    clearPlaybackForTest: player => playback.delete(player),
    retainedDraftForTest: () => retainedPickerDraft,
    attachForTest: (player, controls) => {
      video = player; perch = { x: 10, y: 10 };
      ({ button, panel, receipt } = controls);
      companion = { setPickerOpen() {}, setVisible() {} };
      renderShell = () => {};
    }
  };\n})();`);
  vm.runInContext(content, context);
  await new Promise(resolve => setImmediate(resolve));

  window.__testContent.onPointer({ target: card, clientX: 220, clientY: 100 });
  assert.equal(window.__testContent.activePreview(), null);

  videos = [video];
  window.__testContent.consider(video);
  window.__testContent.consider(unrelatedVideo);
  window.__testContent.setSelectPlayer(() => {});
  window.__testContent.refreshPlayer(unrelatedVideo);
  assert.equal(window.__testContent.activePreview(), null);
  window.__testContent.refreshPlayer(video);

  assert.equal(window.__testContent.activePreview()?.player, video);
  assert.equal(window.__testContent.activePreview()?.page, item);
  window.__testContent.attachForTest(video, { button: node(), panel: node(), receipt: node() });
  video.readyState = 0;
  assert.equal(window.__testContent.holdMissingPreview(), true);
  assert.deepEqual(timers, [900]);

  await window.__testContent.loadFormats();
  const formatRequest = sent.find(message => message.type === 'media-options');
  assert.equal(formatRequest.pageUrl, item);
  assert.equal(formatRequest.playerUrl, '');
  assert.equal(formatRequest.playerPlaying, false);
  assert.equal(formatRequest.playerFacts, undefined, 'a stale player binding sends no player facts');

  video.readyState = 1;
  video.duration = 95;
  video.videoHeight = 720;
  await window.__testContent.loadFormats();
  assert.deepEqual(JSON.parse(JSON.stringify(sent.at(-1).playerFacts)), { durationSeconds: 95, height: 720 });

  video.duration = Infinity;
  await window.__testContent.loadFormats();
  assert.deepEqual(JSON.parse(JSON.stringify(sent.at(-1).playerFacts)), { height: 720, live: true });

  const trim = window.__testContent.clipAvailability;
  const audioAac = trim({ canClip: true, clipUsesWholeFile: true, container: 'M4A' }, 95);
  assert.equal(audioAac.available, true);
  assert.equal(audioAac.reason, '', 'a supported AAC trim does not show a generic download paragraph');
  assert.match(trim({ canClip: true }, 0).reason, /known media length/);
  assert.match(trim({ canClip: true }, Infinity, true).reason, /live streams/);
  assert.match(trim({ canClip: false }, 95).reason, /can't be trimmed/);

  const best = { index: 0, label: 'Best quality', detail: '1080p H264 + audio', container: 'MP4', canClip: true, clipUsesWholeFile: true };
  const balanced = { index: 1, label: 'Balanced', detail: '720p H264 + audio', container: 'MP4', canClip: true, clipUsesWholeFile: true };
  window.__testContent.setRetainedPickerDraft({
    targetKey: 'same-video', choiceKey: window.__testContent.choiceKey(balanced), useOriginal: false, useM4a: true,
    intent: { audioTrack: 'es', subtitles: ['en', 'es'], includeAutoSubtitles: false, embedSubtitles: true },
    trimDraft: { enabled: true, start: '00:12', end: '00:20' }
  });
  const refreshed = {
    choices: [best, { ...balanced, index: 7 }],
    audioTracks: [{ id: 'en' }, { id: 'es' }],
    subtitleOptions: [{ language: 'en', embedSupported: true }, { language: 'es', embedSupported: true }]
  };
  const restored = window.__testContent.restorePickerDraft(refreshed, 'same-video');
  assert.equal(restored.choice.index, 7, 'a fresh format index may differ while the same choice remains available');
  assert.deepEqual(refreshed.intent.subtitles, ['en', 'es']);
  assert.equal(refreshed.intent.audioTrack, 'es');
  assert.equal(refreshed.intent.embedSubtitles, true);
  assert.equal(JSON.stringify(refreshed.trimDraft), JSON.stringify({ enabled: true, start: '00:12', end: '00:20' }));

  const changed = { choices: [best], audioTracks: [{ id: 'en' }], subtitleOptions: [{ language: 'en' }] };
  const changedRestore = window.__testContent.restorePickerDraft(changed, 'same-video');
  assert.equal(changed.selectionNeedsChoice, true);
  assert.match(changed.selectionNotice, /no longer available/);
  assert.match(changed.trackNotice, /selected tracks/);
  assert.equal(changedRestore.choice.index, 0);

  window.__testContent.attachForTest(video, { button: node(), panel: node(), receipt: node() });
  const request = window.__testContent.setPanelForTest(unrelatedVideo);
  window.__testContent.clearPlaybackForTest(unrelatedVideo);
  assert.equal(window.__testContent.isCurrentRequest(request), false,
    'a response for a player without its original source binding is stale');
  assert.equal(window.__testContent.panelOpenForTest(), false);

  window.__testContent.refreshPlayer(unrelatedVideo);
  window.__testContent.setPanelForTest(unrelatedVideo);
  window.__testContent.setRetainedPickerDraft({ targetKey: 'test' });
  emptiedHandler({ target: unrelatedVideo });
  assert.equal(window.__testContent.panelOpenForTest(), false,
    'emptying the active player closes its pending picker request');
  assert.equal(window.__testContent.retainedDraftForTest(), null);
});

test('a butterfly left behind by an ended hover preview never leaves from under the pointer', async () => {
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
  const item = 'https://www.youtube.com/watch?v=abcdefghijk';
  const body = {};
  const card = new FakeElement();
  const video = new FakeVideo();
  const elsewhere = new FakeElement();
  let hovering = false;
  let now = 1_000_000;
  const scheduled = [];
  const node = () => ({
    setAttribute() {}, matches: selector => hovering && selector.includes(':hover'),
    classList: { add() {}, remove() {}, toggle() {}, contains: () => false }
  });
  card.parentElement = body;
  elsewhere.parentElement = body;
  card.querySelectorAll = selector => selector === 'a[href]' ? [{ href: item }] : selector === 'video' ? [video] : [];
  video.parentElement = card;
  let hoverTarget = card;
  const document = Object.assign(new EventTarget(), {
    body, documentElement: {}, createElement: node, querySelectorAll: () => [], elementFromPoint: () => hoverTarget
  });
  const window = new EventTarget();
  const context = vm.createContext({
    window, document, location: { href: 'https://www.youtube.com/' }, Element: FakeElement,
    HTMLMediaElement: FakeVideo, HTMLVideoElement: FakeVideo,
    AbortController, URL, innerWidth: 1280, innerHeight: 720,
    Date: class extends Date { static now() { return now; } },
    getComputedStyle: () => ({ visibility: 'visible' }),
    clearTimeout() {},
    setTimeout: (handler, delay) => scheduled.push({ handler, delay }),
    matchMedia: () => Object.assign(new EventTarget(), { matches: false }),
    ResizeObserver: class { observe() {} disconnect() {} },
    IntersectionObserver: class { observe() {} unobserve() {} disconnect() {} },
    chrome: {
      runtime: { id: 'vesper-test', onMessage: { addListener() {} }, sendMessage: async () => ({ ok: false, code: 'protected' }) },
      storage: { sync: { get: async defaults => defaults }, onChanged: { addListener() {}, removeListener() {} } }
    }
  });
  vm.runInContext(source('theme'), context);
  vm.runInContext(source('player-source'), context);
  let detached = 0;
  const content = source('content').replace(/\}\)\(\);\s*$/, `window.__testContent = {
    onPointer, consider, refreshPlayer, holdMissingPreview, activePreview: () => activePreview,
    stubMovement: onDetach => { selectPlayer = () => {}; detach = onDetach; },
    attachForTest: (player, controls) => {
      video = player; perch = { x: 10, y: 10 };
      ({ button, panel, receipt } = controls);
      companion = { setPickerOpen() {}, setVisible() {}, pointerMove() {} };
    }
  };\n})();`);
  vm.runInContext(content, context);
  await new Promise(resolve => setImmediate(resolve));
  const page = window.__testContent;
  const runLatestTimer = afterMs => { now += afterMs; scheduled.at(-1).handler(); };

  page.onPointer({ target: card, clientX: 220, clientY: 100 });
  page.consider(video);
  page.stubMovement(() => { detached++; });
  page.refreshPlayer(video);
  assert.equal(page.activePreview()?.player, video);
  page.attachForTest(video, { button: node(), panel: node(), receipt: node() });

  // The site ends the preview as the pointer reaches the butterfly.
  hoverTarget = elsewhere;
  page.onPointer({ target: elsewhere, clientX: 30, clientY: 30 });
  video.readyState = 0;
  hovering = true;
  assert.equal(page.holdMissingPreview(), true);
  for (let check = 0; check < 6; check++) runLatestTimer(900);
  assert.equal(detached, 0, 'still there after 5.4 seconds of resting on it');
  assert.equal(page.activePreview()?.player, video);
  assert.ok(scheduled.every(timer => timer.delay === 900), 'the wait never turns into a zero-delay loop');

  // The pointer moves well away. The butterfly leaves at the next check.
  hovering = false;
  page.onPointer({ target: elsewhere, clientX: 900, clientY: 600 });
  runLatestTimer(900);
  assert.equal(detached, 1);
  assert.equal(page.activePreview(), null);
});

test('a pointer still heading for the butterfly gets extra time, with a limit', async () => {
  assert.match(source('content'), /const approaching = distance < previewPointerDistance - 6;/);
  assert.match(source('content'), /\(previewLatched\(\) \|\| approaching\) && Date\.now\(\) - previewRemovedAt < PREVIEW_MAX_LATCH/);
});
