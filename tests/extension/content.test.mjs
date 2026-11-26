import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = name => readFileSync(new URL(`../../extension/${name}.js`, import.meta.url), 'utf8');
function runContent(context, content = source('content')) {
  const storage = context.chrome.storage ??= {};
  const local = storage.local ??= { get: async () => ({}) };
  const get = local.get;
  local.get = async keys => ({ vesperPauseMigrated: true, ...await get(keys) });
  storage.onChanged ??= { addListener() {}, removeListener() {} };
  vm.runInContext(source('player-source'), context);
  vm.runInContext(source('vesper-intelligence'), context);
  vm.runInContext(source('vesper-model'), context);
  vm.runInContext(source('vesper-inference'), context);
  vm.runInContext(source('browser-preferences'), context);
  vm.runInContext(source('browser-body'), context);
  vm.runInContext(content, context);
}

test('player visibility checks rendered containers and falls back safely on older browsers', () => {
  const content = source('content');
  const start = content.indexOf('  function renderedPlayerBox(');
  const end = content.indexOf('  function visiblePlayer(', start);
  assert.ok(start >= 0 && end > start);
  const context = vm.createContext({ getComputedStyle: el => el.style || {} });
  const rendered = vm.runInContext(content.slice(start, end) + '\nrenderedPlayerBox;', context);
  let options;
  assert.equal(rendered({ checkVisibility(value) { options = value; return false; } }), false);
  assert.equal(options.checkOpacity, true);
  assert.equal(options.checkVisibilityCSS, true);
  assert.equal(options.contentVisibilityAuto, true);
  const visible = { style: { visibility: 'visible', opacity: '1' } };
  for (const style of [{ opacity: '0' }, { display: 'none' }, { contentVisibility: 'hidden' }]) {
    const player = { ...visible, parentElement: { ...visible, parentElement: { style } } };
    assert.equal(rendered(player), false);
  }
  assert.equal(rendered({ ...visible, parentElement: visible }), true);
  assert.equal(rendered({ style: { visibility: 'hidden' } }), false);
});

test('only an explicit current picker survives a video opacity transition', () => {
  const content = source('content');
  const start = content.indexOf('  function pickerTransitionRect(');
  const end = content.indexOf('  function place(', start);
  assert.ok(start >= 0 && end > start);
  const rect = { left: 10, top: 10, right: 310, bottom: 210 };
  const player = { readyState: 4, src: 'blob:https://example.test/one', style: { opacity: '0', visibility: 'visible', display: 'block' }, getBoundingClientRect: () => rect };
  const panelMedia = { player, generation: 7, src: player.src, documentIdentity: 'page-a', targetKey: 'target-a' };
  const document = { fullscreenElement: null };
  const context = vm.createContext({
    perch: {}, panelMedia, panel: { classList: { contains: () => true } }, document,
    intersecting: new Map([[player, true]]), playback: new Map([[player, { generation: 7 }]]),
    VesperPlayerSource: { identity: value => value }, pageAddress: () => 'page-a',
    playerSource: p => p.src, activePreview: null, getComputedStyle: p => p.style,
    hiddenPlayerContainer: p => Boolean(p.hiddenAncestor), clippedPlayerRect: r => r
  });
  const transition = vm.runInContext(content.slice(start, end) + '\npickerTransitionRect;', context);
  assert.equal(transition(player), rect);
  for (const [key, value] of [['generation', 8], ['src', 'blob:https://example.test/two'], ['documentIdentity', 'page-b'], ['player', {}]]) {
    const original = panelMedia[key];
    panelMedia[key] = value;
    assert.equal(transition(player), null, key);
    panelMedia[key] = original;
  }
  player.hiddenAncestor = true;
  assert.equal(transition(player), null);
  player.hiddenAncestor = false;
  document.fullscreenElement = {};
  assert.equal(transition(player), null);
  document.fullscreenElement = null;
  context.intersecting.set(player, false);
  assert.equal(transition(player), null);
  context.intersecting.set(player, true);
  context.panelMedia = null;
  assert.equal(transition(player), null);
});

test('initial metadata refreshes a provisional picker once but a replacement invalidates it', async () => {
  const content = source('content');
  const start = content.indexOf('  function canAdoptInitialBinding(');
  const end = content.indexOf('  function refreshPlayers(', start);
  assert.ok(start >= 0 && end > start);
  class Media {
    isConnected = true;
    readyState = 1;
    src = 'https://example.test/a.mp4';
    duration = 10;
  }
  const player = new Media(), page = 'https://example.test/watch/a';
  const context = vm.createContext({
    HTMLMediaElement: Media, HTMLVideoElement: Media, video: player,
    alive: () => true, observed: new Set([player]), nativeAudioPlayer: () => false,
    mediaLedger: { playback() {} }, evidenceKey: () => 'player-1',
    location: { origin: 'https://example.test' }, VesperPlayerSource: { identity: v => v },
    pageForPlayer: () => page, pageAddress: () => page, playback: new Map(),
    isCurrentEndedPlayer: () => false, endedPlayer: null, activePreview: null,
    generation: 0, mediaChangedAt: 0, retainedPickerDraft: null, panelRequest: 3,
    panel: { classList: { contains: () => true } },
    panelMedia: { player, generation: 0, src: player.src, pageUrl: page, documentIdentity: page },
    isCurrentRequest: n => n === context.panelRequest && !!context.panelMedia,
    pickerPlayerState: el => ({ current: el.src }),
    closePanel: () => { context.panelMedia = null; context.panelRequest++; context.closed++; },
    loadFormats: refresh => { assert.equal(refresh, true); context.reloaded++; },
    closed: 0, reloaded: 0
  });
  const consider = vm.runInContext(content.slice(start, end) + '\nconsider;', context);
  consider(player);
  consider(player);
  await Promise.resolve();
  assert.equal(context.closed, 0);
  assert.equal(context.reloaded, 1);
  assert.equal(context.panelRequest, 4);
  assert.equal(context.panelMedia.generation, 1);
  player.src = 'https://example.test/b.mp4';
  consider(player);
  await Promise.resolve();
  assert.equal(context.closed, 1);
  assert.equal(context.reloaded, 1);

  // A contradictory initial source must not be adopted.
  context.playback.clear();
  context.panelMedia = { player, generation: 0, src: 'https://example.test/other.mp4', pageUrl: page, documentIdentity: page };
  consider(player);
  await Promise.resolve();
  assert.equal(context.closed, 2);
  assert.equal(context.reloaded, 1);
});

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

test('browser sign-in is global, with only the browser permission gate', () => {
  const popup = readFileSync(new URL('../../extension/popup.html', import.meta.url), 'utf8');
  assert.match(popup, /Use my Chrome sign-in/);
  assert.doesNotMatch(popup, /id="signin-site"|id="signin-host"|id="file-sign-in"/);
});

test('Pause is the only browser download setting', () => {
  const popup = readFileSync(new URL('../../extension/popup.html', import.meta.url), 'utf8');
  assert.match(popup, /id="pause-vesper"/);
  assert.doesNotMatch(popup, /Browser downloads|After sending a file|Find video formats early/);
});

test('opening the popup shows the installed extension version without checking the app', async () => {
  for (const versionName of [undefined, '1.0.2-firstflight.3']) {
    const elements = new Map();
    const messages = [];
    const preferences = [];
    const permissionRequests = [];
    const local = { vesperPauseMigrated: true, vesperPaused: false };
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
          get: async defaults => ({ ...defaults, motion: 'system' }),
          set: async value => preferences.push(structuredClone(value))
        },
        local: { get: async () => ({ ...local }), set: async values => Object.assign(local, values) },
        onChanged: { addListener() {} }
      },
      permissions: {
        contains: async () => permissionRequests.length > 0,
        request: async value => { permissionRequests.push(structuredClone(value)); return true; },
        onRemoved: { addListener() {} }
      },
      tabs: { query: async () => [{ id: 3, url: 'https://example.com/download' }] }
    };
    vm.runInNewContext(source('browser-preferences') + '\n' + source('popup'),
      { chrome, URL, document: { getElementById }, VesperTheme: { install() {} } });
    await new Promise(resolve => setImmediate(resolve));

    assert.equal(getElementById('version').textContent, `Extension ${versionName || '1.0.2'}`);
    const pause = getElementById('pause-vesper');
    assert.equal(pause.disabled, false);
    assert.equal(pause.checked, false);
    assert.equal(getElementById('motion').value, 'animate');
    assert.equal(getElementById('signin').checked, true);
    const allow = getElementById('signin-permission');
    assert.equal(allow.hidden, false);
    assert.deepEqual(messages, []);
    assert.deepEqual(permissionRequests, []);
    pause.checked = true;
    await pause.onchange();
    assert.equal(local.vesperPaused, true);
    await allow.onclick();
    assert.deepEqual(permissionRequests, [{ permissions: ['cookies'] }]);
    assert.equal(allow.hidden, true);
    assert.deepEqual(preferences, [{ useSignIn: true }]);
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
  assert.match(background, /requestedPage\?\.origin === framePage\.origin/);
  assert.match(options, /collectionUrl = collection\.SourceUrl\.AbsoluteUri/);
  assert.match(options, /collectionUrl = HasRelatedCollection\(item\) \? item\.Related!\.Url\.AbsoluteUri : null/);
  assert.match(content, /\.more\{[^}]*margin:8px 8px 0/);
  assert.match(options, /resolved\.Collection is \{ \} collection/);
  assert.match(options, /hasRelatedCollection = HasRelatedCollection\(item\)/);
  assert.match(options, /isLive = item\.IsLive/);
  assert.match(content, /formats\.playerLive = playerFacts\?\.live === true \|\| formats\.isLive === true/);
});

test('collection controls show a browse action and forward the resolved collection', async () => {
  const sent = [];
  const document = Object.assign(new EventTarget(), {
    querySelectorAll: () => [],
    createElement: tag => ({
      tagName: tag, isConnected: true, children: [], attributes: {},
      setAttribute(name, value) { this.attributes[name] = value; },
      append(...nodes) { this.children.push(...nodes); },
      classList: { add() {}, remove() {}, toggle() {} }
    })
  });
  const window = new EventTarget();
  const location = { href: 'https://www.youtube.com/', hostname: 'www.youtube.com', origin: 'https://www.youtube.com' };
  const context = vm.createContext({
    window, document, location, AbortController, URL, Event, Element: class {},
    ResizeObserver: class { observe() {} disconnect() {} },
    IntersectionObserver: class { observe() {} disconnect() {} },
    VesperTheme: { available: () => true, watchPreference: () => () => {} },
    chrome: { runtime: { id: 'vesper-test', onMessage: { addListener() {} },
      getURL: path => 'chrome-extension://vesper-test/' + path,
      sendMessage: async message => { sent.push(message); return { ok: true, results: [{ status: 'queued' }] }; } } }
  });
  vm.runInContext(source('player-source'), context);
  const content = source('content').replace(/\}\)\(\);\s*$/, `window.testControls = (formats, selected = location.href, documentPage = location.href) => {
    const player = { isConnected: true, querySelectorAll: () => [], dispatchEvent() {}, getAttribute() { return null; } };
    panelMedia = { pageUrl: selected, documentPage, documentIdentity: VesperPlayerSource.identity(pageAddress()), player, playerState: pickerPlayerState(player) };
    closePanel = () => { panelMedia = null; };
    place = () => {};
    return collectionPickerControls('Example', panelRequest, formats);
  };
  window.testYouTubePage = activeYouTubePage;
  })();`);
  runContent(context, content);
  const url = 'https://www.youtube.com/watch?v=abcdefghijk&list=PLexample';
  for (const [kind, label] of [['playlist', 'playlist'], ['dynamicmix', 'mix'], ['album', 'album']]) {
    const { browse, status } = window.testControls({ collectionKind: kind, collectionUrl: url });
    assert.equal(browse.tagName, 'button');
    assert.equal(browse.title, 'Browse ' + label);
    assert.equal(browse.attributes['aria-label'], browse.title);
    assert.equal(browse.textContent, browse.title);
    assert.equal(status.hidden, true);
    await browse.onclick();
    assert.equal(sent.at(-1).pageUrl, url);
    assert.equal(status.textContent, '1 item added.');
    assert.equal(browse.disabled, false);
  }
  location.href = url;
  const stale = window.testControls({ collectionKind: 'playlist', collectionUrl: url });
  location.href = url.replace('PLexample', 'PLanother');
  const count = sent.length;
  await stale.browse.onclick();
  assert.equal(sent.length, count);
  const late = window.testControls({ collectionKind: 'playlist', collectionUrl: url }, url, url);
  await late.browse.onclick();
  assert.equal(sent.length, count, 'a late response cannot open the old collection');
  location.href = url;
  const player = { dispatchEvent() {}, getAttribute: () => 'abcdefghijk' };
  assert.equal(window.testYouTubePage(player, 'https://www.youtube.com/watch?v=abcdefghijk'), url);
  const fallback = window.testControls({ collectionKind: 'playlist', collectionUrl: 'https://other.example/list' }, url);
  await fallback.browse.onclick();
  assert.equal(sent.at(-1).pageUrl, url);
});

test('butterfly motion responds to Animate and Off', async () => {
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
  runContent(context, content);

  const attributes = new Set();
  const stillModes = [];
  await new Promise(resolve => setImmediate(resolve));
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
  runContent(context);
  await Promise.resolve();

  let reply;
  messageListener({ type: 'vesper-open-picker' }, { id: 'vesper-test' }, value => { reply = value; });

  assert.equal(reply.ok, false);
  assert.equal(reply.code, 'no-player');
});

test('dragging can perch outside the player and fullscreen modes wake again after idle', async () => {
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
    startDrag, moveDrag, finishDrag, onPointer, place, fillsFullscreen, moved: () => suppressDragClick,
    setup: (player, action, picker, sprite) => {
      video = player; button = action; panel = picker; companion = sprite;
      perch = { x: 20, y: 30, faceLeft: false };
    }
  };\n})();`);
  runContent(context, content);

  const calls = [];
  await new Promise(resolve => setImmediate(resolve));
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
  assert.deepEqual(calls[0], [105, 65]); // No longer clamped to one butterfly-width outside.
  assert.equal(window.testPlayer.moved(), true);

  function advance() {
    now += 3000;
    for (const [id, { callback, due }] of [...timers]) {
      if (due <= now && timers.delete(id)) callback();
    }
  }
  const fullBox = () => ({ left: 0, top: 0, right: 800, bottom: 600 });
  player.parentElement = { position: 'fixed', getBoundingClientRect: fullBox };
  assert.equal(window.testPlayer.fillsFullscreen({ left: 200, top: 0, right: 800, bottom: 600 }), false,
    'a feed with a sidebar is not fullscreen even inside a fixed app shell');
  delete player.parentElement;
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

test('opening the picker pins the chosen feed player despite a larger autoplaying neighbour', () => {
  const document = Object.assign(new EventTarget(), { querySelectorAll: () => [] });
  const window = new EventTarget();
  const context = vm.createContext({
    window, document, URL, AbortController, location: { href: 'https://www.tiktok.com/' },
    ResizeObserver: class {}, IntersectionObserver: class {},
    VesperTheme: { available: () => true, watchPreference: () => () => {} },
    chrome: { runtime: { id: 'vesper-test', onMessage: { addListener() {} } } }
  });
  const content = source('content').replace(/\}\)\(\);\s*$/, `window.testPin = (selected, neighbour, overlay) => {
    extensionPaused = false;
    hiddenButterflyHosts = new Set();
    video = selected;
    host = overlay;
    panelMedia = { player: selected };
    observed.add(selected); observed.add(neighbour);
    const calls = [];
    place = () => calls.push('place');
    attach = player => calls.push(player);
    visiblePlayer = player => player === selected ? {left:0,top:0,right:300,bottom:300} : {left:0,top:0,right:800,bottom:800};
    cardPreview = () => { throw Error('Overlay interaction searched a different card'); };
    updateFlyVisibility = () => {};
    selectPlayer();
    onPointer({target:overlay,clientX:10,clientY:10,composedPath:()=>[overlay]});
    panelMedia = null;
    selectPlayer();
    manualPlayer = selected;
    movedPerch = { x: 0.2, y: 0.2 };
    visiblePlayer = player => player === selected ? null : {left:0,top:0,right:800,bottom:800};
    selectPlayer();
    if (manualPlayer || movedPerch) throw Error('Off-screen pin survived feed transition');
    return calls;
  };\n})();`);
  runContent(context, content);
  const selected={isConnected:true,paused:true}, neighbour={isConnected:true,paused:false};
  const calls=window.testPin(selected,neighbour,{});
  assert.equal(calls.length,3);
  assert.equal(calls[0],'place');
  assert.equal(calls[1],neighbour);
  assert.equal(calls[2],neighbour);
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

test('normal Smart Select opens the guided batch panel on request', () => {
  const smartSelect = source('smart-select');
  const guidedBatch = source('vesper-smart-batch');
  assert.match(smartSelect, /Guided batch/);
  assert.match(smartSelect, /VesperSmartBatch\?\.open\(\)/);
  assert.match(guidedBatch, /dataset\.vesperBatch/);
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
  runContent(context);
  await Promise.resolve();
  assert.equal(window.__vesperContent, true);
  live = false;
  document.dispatchEvent(new Event('fullscreenchange'));
  assert.equal(window.__vesperContent, undefined);
  assert.equal(disconnected, 2);
  assert.equal(unsubscribed, 3);
  document.dispatchEvent(new Event('fullscreenchange'));
  assert.equal(unsubscribed, 3);
});

test('Pause keeps browser copies running and page departure still stops them', async () => {
  const document = Object.assign(new EventTarget(), { querySelectorAll: () => [] });
  const window = new EventTarget();
  const changes = new Set();
  const context = vm.createContext({
    window, document, AbortController, URL, location: { href: 'https://example.test/file' },
    ResizeObserver: class {}, IntersectionObserver: class {},
    VesperTheme: { available: () => true, watchPreference: () => () => {} },
    chrome: {
      runtime: { id: 'vesper-test', onMessage: { addListener() {} } },
      storage: { onChanged: { addListener: listener => changes.add(listener), removeListener: listener => changes.delete(listener) } }
    }
  });
  const content = source('content').replace(/\}\)\(\);\s*$/, `window.startCopies = () => {
    activeBrowserCopy = { controller: new AbortController() };
    generatedCopy = { controller: new AbortController() };
    return [activeBrowserCopy.controller.signal, generatedCopy.controller.signal];
  };
  window.isPaused = () => extensionPaused;
  })();`);
  runContent(context, content);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(window.isPaused(), false);
  const copies = window.startCopies();
  for (const listener of changes) listener({ vesperPaused: { newValue: true } }, 'local');
  assert.equal(window.isPaused(), true);
  assert.ok(copies.every(copy => !copy.aborted));
  window.dispatchEvent(Object.assign(new Event('pagehide'), { persisted: true }));
  assert.ok(copies.every(copy => copy.aborted));
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
  runContent(context, content);

  assert.equal(window.__testPageForPlayer(video), item);
  assert.equal(window.__testPageForPlayer(new FakeElement()), home);
});

test('an in-page navigation finds an already loaded reel without waiting for playback', async () => {
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
  runContent(context, content);

  const reel = new Video();
  await new Promise(resolve => setImmediate(resolve));
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
  runContent(context, content);
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
  assert.equal(trim({ canClip: true, clipUsesWholeFile: true }, 1200).reason, '');
  assert.match(trim({ canClip: true, clipUsesWholeFile: true }, 1201).reason, /full video, then trims/);
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
  assert.equal(changed.audioNeedsChoice, true);
  assert.equal(changed.subtitlesNeedChoice, true);
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
  runContent(context, content);
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

test('feed DOM discovery coalesces inserted players and recovers a removed overlay', () => {
  const content = source('content');
  const start = content.indexOf('  function mediaTreeChanged(');
  const end = content.indexOf('  function refreshPlayers(', start);
  let passes = 0;
  const frames = [];
  const context = vm.createContext({ alive: () => true, host: null, observed: new Set(), discoveryFrame: 0,
    requestAnimationFrame: callback => (frames.push(callback), frames.length),
    refreshPlayers: () => passes++ });
  const changed = vm.runInContext(content.slice(start, end) + '\nmediaTreeChanged;', context);
  const unrelated = {nodeType: 1, matches: () => false, querySelector: () => null};
  const media = {nodeType: 1, matches: () => true};
  changed([{type:'childList', addedNodes:[unrelated]}]);
  assert.equal(frames.length, 0);
  changed([{type:'childList', addedNodes:[media]}]);
  changed([{type:'attributes', target:media}]);
  assert.equal(frames.length, 1);
  frames[0]();
  assert.equal(passes, 1);
  context.host = {isConnected:false};
  changed([{type:'childList', addedNodes:[]}]);
  assert.equal(frames.length, 2);
  frames[1]();
  assert.equal(passes, 2);
  context.host = null;
  context.observed.add({isConnected:false});
  changed([{type:'childList', addedNodes:[]}]);
  assert.equal(frames.length, 3);
});

test('scroll re-ranks a competing visible feed player instead of merely moving the old overlay', () => {
  const content = source('content');
  const start = content.indexOf('  function schedule()');
  const end = content.indexOf('  function stopFullscreenIdle(', start);
  let ranked = 0, placed = 0, callback;
  const player = {};
  const context = vm.createContext({ alive: () => true, frame:0, manualPlayer:null, video:player,
    observed: new Set([player, {}]), visiblePlayer: () => ({}),
    place: () => placed++, selectPlayer: () => ranked++, requestAnimationFrame: fn => (callback=fn,1) });
  const schedule = vm.runInContext(content.slice(start,end)+'\nschedule;', context);
  schedule(); callback();
  assert.equal(ranked,1); assert.equal(placed,0);
  context.observed = new Set([player]);
  schedule(); callback();
  assert.equal(placed,1);
});

test('TikTok feed identity comes from the exact live player wrapper, not the home page or adjacent card', () => {
  const content = source('content');
  const start = content.indexOf('  function activeTikTokPage(');
  const end = content.indexOf('  function setIntentCertainty(', start);
  const location = { hostname:'www.tiktok.com', origin:'https://www.tiktok.com' };
  const context = vm.createContext({location, URL});
  vm.runInContext(source('player-source'),context);
  const identify = vm.runInContext(content.slice(start,end)+'\nactiveTikTokPage;',context);
  const player = {tagName:'VIDEO'};
  const wrapper = {id:'xgwrapper-0-7608248907960814879', querySelectorAll:()=>[player]};
  player.parentElement = wrapper;
  const root = location.origin+'/';
  assert.equal(identify(player,root),location.origin+'/embed/v2/7608248907960814879');
  const permalink = location.origin+'/@creator/video/7608248907960814879';
  assert.equal(identify(player,permalink),permalink);
  wrapper.id = 'xgwrapper-0-7608248907960814880';
  assert.equal(identify(player,permalink),location.origin+'/embed/v2/7608248907960814880');
  wrapper.querySelectorAll = ()=>[player,{}];
  assert.equal(identify(player,root),null);
  wrapper.querySelectorAll = ()=>[player];
  wrapper.id = 'xgwrapper-0-invalid';
  assert.equal(identify(player,root),null);
  location.hostname='not-tiktok.com';
  wrapper.id='xgwrapper-0-7608248907960814879';
  assert.equal(identify(player,root),null);
});
test('prefetch and picker share exact-post metadata and never borrow a social feed title',()=>{
  const content=source('content'), start=content.indexOf('  function titleForPlayer('), end=content.indexOf('  function activeTikTokPage(',start);
  let page='https://x.com/outer/status/123456789';
  const location={hostname:'x.com'};
  const context=vm.createContext({URL,location,pageAddress:()=>page,
    document:{title:'Outer quoted post',querySelectorAll:()=>[{}]},nativeAudioPlayer:()=>false});
  vm.runInContext(source('player-source'),context);
  const title=vm.runInContext(content.slice(start,end)+'\ntitleForPlayer;',context);
  const player={getAttribute:()=>null};
  assert.equal(title(player,'https://x.com/inner/status/987654321',true),'This video');
  assert.equal(title(player,page,true),'Outer quoted post');
  page='https://x.com/home';
  assert.equal(title(player,page,true),'This video');
  assert.equal(title({getAttribute:()=> 'Clip label'},page,true),'Clip label');
  page='https://example.com/watch';location.hostname='example.com';
  assert.equal(title(player,page,true),'Outer quoted post');
});

test('mediaTreeChanged refreshes players when a recycled container swaps post links', () => {
  const content = source('content');
  const start = content.indexOf('  function mediaTreeChanged(');
  const end = content.indexOf('  function refreshPlayers(', start);
  const video = { isConnected: true, matches: () => true };
  const container = { nodeType: 1, matches: () => false, querySelector: () => null, contains: el => el === video };
  const link = { nodeType: 1, matches: () => false, querySelector: () => null };
  let scheduled = 0;
  const context = vm.createContext({
    alive: () => true,
    discoveryFrame: 0,
    host: null,
    observed: new Set([video]),
    audioControls: new WeakMap(),
    requestAnimationFrame: fn => (scheduled++, 1),
    refreshPlayers: () => {}
  });
  const mediaTreeChanged = vm.runInContext(content.slice(start, end) + '\nmediaTreeChanged;', context);
  mediaTreeChanged([{ type: 'childList', target: container, addedNodes: [link] }]);
  assert.equal(scheduled, 1);
});

test('mediaTreeChanged refreshes when an observed card updates an anchor href', () => {
  const content = source('content');
  const start = content.indexOf('  function mediaTreeChanged(');
  const end = content.indexOf('  function refreshPlayers(', start);
  const video = { isConnected: true, matches: () => true };
  const article = { nodeType: 1, contains: el => el === video };
  const anchor = { tagName: 'A', parentElement: article };
  let scheduled = 0;
  const context = vm.createContext({
    alive: () => true,
    discoveryFrame: 0,
    host: null,
    observed: new Set([video]),
    audioControls: new WeakMap(),
    requestAnimationFrame: fn => (scheduled++, 1),
    refreshPlayers: () => {}
  });
  const mediaTreeChanged = vm.runInContext(content.slice(start, end) + '\nmediaTreeChanged;', context);
  mediaTreeChanged([{ type: 'attributes', attributeName: 'href', target: anchor }]);
  assert.equal(scheduled, 1);
});
