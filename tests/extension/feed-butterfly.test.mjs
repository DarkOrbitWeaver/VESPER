// Regression tests for the signed-in feed Butterfly and picker evidence at fc20451.
// Deterministic DOM fixture; no network, no site mocks standing in for live proof.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const src = name => readFileSync(new URL(`../../extension/${name}.js`, import.meta.url), 'utf8');

function makeContentHarness({ href = 'https://www.tiktok.com/', videos = [] } = {}) {
  const docListeners = new Map();
  const winListeners = new Map();
  let rafQueue = [];
  let mountedHost = null;
  let lastPerch = null;
  let flyVisible = false;
  const fetchCalls = [];

  class Media {
    constructor(props = {}) {
      this.tagName = 'VIDEO';
      this.nodeType = 1;
      this.isConnected = true;
      this.readyState = 0;
      this.paused = true;
      this.ended = false;
      this.currentTime = 0;
      this.duration = 15;
      this.videoHeight = 1080;
      this.src = '';
      this.currentSrc = '';
      this.controls = false;
      this.autoplay = false;
      this.loop = false;
      this.muted = false;
      this.defaultMuted = false;
      this.poster = '';
      this.attributes = new Map();
      this._rect = { left: 200, top: 40, right: 600, bottom: 740, width: 400, height: 700 };
      this._visible = true;
      Object.assign(this, props);
    }
    getBoundingClientRect() { return this._rect; }
    checkVisibility() { return this._visible; }
    getAttribute(k) { return this.attributes.get(k) ?? null; }
    setAttribute(k, v) { this.attributes.set(k, String(v)); }
    removeAttribute(k) { this.attributes.delete(k); }
    hasAttribute(k) { return this.attributes.has(k); }
    querySelector() { return null; }
    querySelectorAll() { return []; }
    dispatchEvent() { return true; }
    matches() { return false; }
    closest() { return null; }
  }
  class Video extends Media {}
  class Elem {
    constructor(tag = 'div') {
      this.tagName = tag.toUpperCase();
      this.localName = tag.toLowerCase();
      this.nodeType = 1;
      this.isConnected = true;
      this.children = [];
      this.dataset = {};
      this.style = { cssText: '' };
      this.attributes = new Map();
      const set = new Set();
      this.classList = {
        add: (...c) => c.forEach(x => set.add(x)),
        remove: (...c) => c.forEach(x => set.delete(x)),
        toggle: (c, f) => (f === undefined ? !set.has(c) : f) ? (set.add(c), true) : (set.delete(c), false),
        contains: c => set.has(c)
      };
      this._listeners = new Map();
    }
    append(...nodes) { for (const n of nodes) this.appendChild(n); }
    appendChild(n) {
      n.parentElement = this;
      n.isConnected = true;
      this.children.push(n);
      if (n.dataset && 'vesperPlayer' in n.dataset) mountedHost = n;
      return n;
    }
    remove() {
      if (this === mountedHost) mountedHost = null;
      this.isConnected = false;
      this.parentElement = null;
    }
    attachShadow() { const s = new Elem('#shadow'); s.host = this; return s; }
    addEventListener(type, fn) {
      if (!this._listeners.has(type)) this._listeners.set(type, []);
      this._listeners.get(type).push(fn);
    }
    dispatchEvent(ev) { for (const fn of this._listeners.get(ev.type) || []) fn(ev); return true; }
    getAttribute(k) { return this.attributes.get(k) ?? null; }
    setAttribute(k, v) { this.attributes.set(k, String(v)); }
    toggleAttribute(k, f) { if (f) this.attributes.set(k, ''); else this.attributes.delete(k); }
    hasAttribute(k) { return this.attributes.has(k); }
    contains(other) { for (let n = other; n; n = n.parentElement) if (n === this) return true; return false; }
    getBoundingClientRect() { return this._rect || { left: 200, top: 40, right: 600, bottom: 740, width: 400, height: 700 }; }
    checkVisibility() { return this._visible ?? true; }
    matches(sel) { return sel.includes('article') && this.tagName === 'ARTICLE'; }
    closest() { return null; }
    focus() {}
    blur() {}
    setPointerCapture() {}
    replaceChildren(...nodes) { this.children = nodes; }
    querySelectorAll() { return []; }
  }

  let ioCallback = null;
  class IO {
    constructor(cb) { ioCallback = cb; }
    observe() {}
    unobserve() {}
    disconnect() {}
  }
  class RO { observe() {} disconnect() {} }
  const moCallbacks = [];
  class MO {
    constructor(cb) { moCallbacks.push(cb); }
    observe() {}
    disconnect() {}
  }

  const docEl = new Elem('html');
  const body = new Elem('body');
  docEl.appendChild(body);

  const doc = {
    documentElement: docEl,
    body,
    hidden: false,
    title: 'TikTok - Make Your Day',
    fullscreenElement: null,
    createElement: tag => new Elem(tag),
    getElementsByTagName: tag => tag === 'video' ? videos : [],
    querySelector: () => null,
    querySelectorAll: () => [],
    elementFromPoint: () => null,
    elementsFromPoint: () => [],
    createTreeWalker: root => {
      const nodes = [];
      const walk = n => { if (!n) return; nodes.push(n); for (const c of n.children || []) walk(c); };
      walk(root);
      let idx = 0;
      return { get currentNode() { return nodes[idx] || null; }, nextNode() { idx++; return nodes[idx] || null; } };
    },
    addEventListener(type, fn) {
      if (!docListeners.has(type)) docListeners.set(type, []);
      docListeners.get(type).push(fn);
    },
    removeEventListener(type, fn) {
      const list = docListeners.get(type);
      if (list) docListeners.set(type, list.filter(x => x !== fn));
    },
    dispatch(type, ev) { for (const fn of [...(docListeners.get(type) || [])]) fn({ type, ...ev }); }
  };

  const sentMessages = [];
  const context = vm.createContext({
    window: null,
    document: doc,
    Element: Elem,
    HTMLMediaElement: Media,
    HTMLVideoElement: Video,
    HTMLIFrameElement: class {},
    NodeFilter: { SHOW_ELEMENT: 1 },
    IntersectionObserver: IO,
    ResizeObserver: RO,
    MutationObserver: MO,
    AbortController,
    Event,
    CustomEvent: class extends Event { constructor(t, o) { super(t, o); this.detail = o?.detail; } },
    URL,
    Date,
    performance,
    innerWidth: 1280,
    innerHeight: 800,
    scrollX: 0,
    scrollY: 0,
    screen: { width: 1920, height: 1080 },
    getComputedStyle: () => ({ visibility: 'visible', opacity: '1', display: 'block', position: 'static' }),
    requestAnimationFrame: fn => { rafQueue.push(fn); return rafQueue.length; },
    cancelAnimationFrame: () => {},
    setTimeout,
    clearTimeout,
    fetch: (...args) => { fetchCalls.push(args); return Promise.reject(new Error('unexpected network fetch')); },
    location: { href, origin: new URL(href).origin, hostname: new URL(href).hostname },
    navigator: { userActivation: { isActive: false } },
    VesperTheme: {
      css: '',
      available: () => true,
      contextGone: () => false,
      watch: () => () => {},
      watchPreference: (k, d, cb) => { cb(d); return () => {}; }
    },
    VesperCompanion: {
      footY: 36,
      create: () => ({
        setPerch: (x, y, faceLeft) => { lastPerch = { x, y, faceLeft }; },
        setVisible: v => { flyVisible = v; },
        scheduleIdle: () => {}, stopIdle: () => {}, setPickerOpen: () => {},
        pointerMove: () => {}, setStill: () => {}, dispose: () => {}
      })
    },
    chrome: {
      runtime: {
        id: 'test-ext',
        getURL: p => 'chrome-extension://test-ext/' + p,
        getManifest: () => ({ version: '2.0.0' }),
        sendMessage: async msg => { sentMessages.push(msg); return { ok: true }; },
        onMessage: { addListener() {} }
      },
      storage: {
        local: { get: async () => ({ vesperPauseMigrated: true, vesperPaused: false, vesperHiddenButterflyHosts: [] }) },
        onChanged: { addListener() {}, removeListener() {} }
      }
    }
  });
  context.window = context;
  context.window.addEventListener = (type, fn) => {
    if (!winListeners.has(type)) winListeners.set(type, []);
    winListeners.get(type).push(fn);
  };
  context.window.dispatch = (type, ev = {}) => { for (const fn of [...(winListeners.get(type) || [])]) fn({ type, ...ev }); };
  vm.runInContext(src('player-source'), context);
  vm.runInContext(src('vesper-intelligence'), context);
  vm.runInContext(src('vesper-model'), context);
  vm.runInContext(src('vesper-inference'), context);
  vm.runInContext(src('browser-preferences'), context);
  vm.runInContext(src('browser-body'), context);
  vm.runInContext(src('content').replace(/\}\)\(\);\s*$/, 'globalThis.testWitness = playerFragmentWitness; globalThis.testButtonDisabled = () => !!button?.disabled; })();'), context);

  const flushRaf = () => {
    for (let i = 0; i < 10 && rafQueue.length; i++) {
      const q = rafQueue; rafQueue = [];
      for (const fn of q) fn(performance.now());
    }
  };
  const tick = (ms = 10) => new Promise(r => setTimeout(r, ms));
  return {
    Media, Video, Elem, doc, context, window: context.window, flushRaf, tick, sentMessages, fetchCalls,
    triggerMO: records => moCallbacks.forEach(cb => cb(records)),
    triggerIntersection: entries => ioCallback(entries),
    get host() { return mountedHost; },
    get perch() { return lastPerch; },
    get flyVisible() { return flyVisible; }
  };
}

test('first feed video: src + loadstart without prior poster or metadata still raises the Butterfly', async () => {
  const h = makeContentHarness();
  const v = new h.Video({ paused: false });
  h.doc.body.appendChild(v);
  h.triggerMO([{ type: 'childList', target: h.doc.body, addedNodes: [v], removedNodes: [] }]);
  h.flushRaf();
  await h.tick();
  h.flushRaf();
  v.src = 'blob:https://www.tiktok.com/first-video';
  v.currentSrc = v.src;
  h.doc.dispatch('loadstart', { target: v });
  h.flushRaf();
  await h.tick();
  h.flushRaf();
  assert.ok(h.host && h.flyVisible, 'Butterfly must appear on the first loadstart, without a scroll or refresh');
});

test('mouse movement over the active feed card keeps the perch at the upper right', async () => {
  const h = makeContentHarness();
  const article = new h.Elem('article');
  const overlay = new h.Elem('div');
  const link = new h.Elem('a');
  link.href = 'https://www.tiktok.com/@creator/video/7400000000000000001';
  const v = new h.Video({
    src: 'blob:https://www.tiktok.com/vid1', currentSrc: 'blob:https://www.tiktok.com/vid1',
    readyState: 4, paused: false, autoplay: true, loop: true, defaultMuted: true
  });
  article.append(v, overlay, link);
  h.doc.body.appendChild(article);
  h.triggerMO([{ type: 'childList', target: h.doc.body, addedNodes: [article], removedNodes: [] }]);
  await h.tick();
  h.flushRaf();
  assert.ok(h.host && h.flyVisible, 'initial Butterfly visible');
  assert.equal(h.perch.faceLeft, true, 'initial perch is the upper-right corner');
  h.doc.dispatch('pointermove', { target: overlay, clientX: 450, clientY: 120, composedPath: () => [overlay, article, h.doc.body] });
  h.flushRaf();
  assert.equal(h.perch.faceLeft, true, 'the attached main player must not become a hover preview of itself');
  assert.ok(h.flyVisible, 'the control stays visible while the pointer moves over its own card');
});

test('a playing muted feed loop outranks a paused neighbour before any post binding exists', async () => {
  const h = makeContentHarness();
  const playing = new h.Video({
    src: 'blob:https://www.tiktok.com/playing', currentSrc: 'blob:https://www.tiktok.com/playing',
    readyState: 4, paused: false, autoplay: true, loop: true, defaultMuted: true,
    _rect: { left: 300, top: 40, right: 700, bottom: 760, width: 400, height: 720 }
  });
  const neighbor = new h.Video({
    src: 'blob:https://www.tiktok.com/neighbor', currentSrc: 'blob:https://www.tiktok.com/neighbor',
    readyState: 2, paused: true,
    _rect: { left: 300, top: 640, right: 700, bottom: 900, width: 400, height: 260 }
  });
  h.doc.body.append(playing, neighbor);
  h.triggerMO([{ type: 'childList', target: h.doc.body, addedNodes: [playing, neighbor], removedNodes: [] }]);
  await h.tick();
  h.flushRaf();
  assert.ok(h.perch && h.perch.y < 100, `Butterfly belongs to the centred playing video, got y=${h.perch?.y}`);
});

test('request budget: TikTok load, hover, autoplay and scroll send zero format lookups and zero fetches', async () => {
  const h = makeContentHarness();
  const v = new h.Video({
    src: 'blob:https://www.tiktok.com/vid1', currentSrc: 'blob:https://www.tiktok.com/vid1',
    readyState: 4, paused: false, autoplay: true, loop: true, defaultMuted: true
  });
  h.doc.body.appendChild(v);
  h.triggerMO([{ type: 'childList', target: h.doc.body, addedNodes: [v], removedNodes: [] }]);
  await h.tick();
  h.flushRaf();
  h.doc.dispatch('pointermove', { target: v, clientX: 400, clientY: 300, composedPath: () => [v, h.doc.body] });
  h.doc.dispatch('playing', { target: v });
  h.window.dispatch('scroll');
  h.flushRaf();
  await h.tick(400); // longer than the 250 ms prefetch delay
  h.flushRaf();
  assert.equal(h.sentMessages.filter(m => m.type === 'media-options').length, 0,
    'no VESPER-initiated format probe on load/hover/autoplay/scroll');
  assert.equal(h.fetchCalls.length, 0, 'no page-context network fetch on passive paths');
});

test('a queued offscreen entry uses the video position when the feed has already scrolled', async () => {
  const h = makeContentHarness();
  const v = new h.Video({ src: 'blob:https://www.tiktok.com/next', readyState: 4, paused: false, _visible: false });
  h.doc.body.appendChild(v);
  h.triggerMO([{ type: 'childList', target: h.doc.body, addedNodes: [v], removedNodes: [] }]);
  await h.tick();
  h.flushRaf();
  assert.equal(h.flyVisible, false);

  v._visible = true;
  h.triggerIntersection([{ target: v, isIntersecting: false }]);
  h.flushRaf();
  assert.ok(h.flyVisible);
});

test('scroll recovers a recycled feed player without a new intersection or playback event', async () => {
  const h = makeContentHarness();
  const first = new h.Video({ src: 'blob:https://www.tiktok.com/first', readyState: 4, paused: false });
  const second = new h.Video({ src: 'blob:https://www.tiktok.com/second', readyState: 4, paused: false, _visible: false });
  second._rect = { left: 500, top: 40, right: 900, bottom: 740, width: 400, height: 700 };
  h.doc.body.append(first, second);
  h.triggerIntersection([{ target: first, isIntersecting: true }, { target: second, isIntersecting: false }]);
  await h.tick();
  h.flushRaf();
  assert.ok(h.flyVisible);
  const firstPerch = h.perch;

  first._visible = false;
  second._visible = true;
  let hitTests = 0;
  h.doc.elementsFromPoint = (x, y) => {
    if (y !== h.context.innerHeight / 2) return [];
    hitTests++;
    return [second];
  };
  for (let i = 0; i < 8; i++) h.window.dispatch('scroll');
  h.flushRaf();
  assert.ok(h.flyVisible);
  assert.notEqual(h.perch.x, firstPerch.x);
  assert.equal(hitTests, 1);
  h.window.dispatch('scroll');
  h.flushRaf();
  assert.equal(hitTests, 1, 'a visible player does not trigger another recovery');
  assert.equal(h.sentMessages.filter(message => message.type === 'media-options').length, 0);
  assert.equal(h.fetchCalls.length, 0);
});

test('scroll discovers a playing card away from the middle row even with a paused incumbent', async () => {
  const h = makeContentHarness();
  const first = new h.Video({ src: 'blob:https://www.tiktok.com/first', readyState: 4, paused: true });
  first._rect = { left: 150, top: 20, right: 550, bottom: 280, width: 400, height: 260 };
  const next = new h.Video({ src: 'blob:https://www.tiktok.com/next', readyState: 4, paused: false, _visible: false });
  next._rect = { left: 650, top: 480, right: 1050, bottom: 790, width: 400, height: 310 };
  h.doc.body.append(first, next);
  h.triggerIntersection([{ target: first, isIntersecting: true }]);
  await h.tick();
  h.flushRaf();
  const old = h.perch;
  assert.ok(h.flyVisible);
  assert.ok(old.x < 550);
  next._visible = true;
  h.doc.elementsFromPoint = (x, y) => x >= 650 && x <= 1050 && y >= 480 ? [next] : [];
  h.window.dispatch('scroll');
  h.flushRaf();
  assert.ok(h.flyVisible);
  assert.notEqual(h.perch.x, old.x);
  assert.equal(h.sentMessages.filter(m => m.type === 'media-options').length, 0);
  assert.equal(h.fetchCalls.length, 0);
});

test('the final scroll event gets one delayed recovery when the next card settles late', async () => {
  const h = makeContentHarness();
  const first = new h.Video({ src: 'blob:https://www.tiktok.com/first', readyState: 4, paused: false });
  const next = new h.Video({ src: 'blob:https://www.tiktok.com/next', readyState: 4, paused: false, _visible: false });
  next._rect = { left: 650, top: 40, right: 1050, bottom: 740, width: 400, height: 700 };
  h.doc.body.append(first, next);
  h.triggerIntersection([{ target: first, isIntersecting: true }]);
  await h.tick();
  h.flushRaf();
  const old = h.perch;
  first._visible = false;
  h.window.dispatch('scroll');
  h.flushRaf();
  assert.ok(h.flyVisible, 'presentation survives the transition');
  assert.equal(h.context.testButtonDisabled(), true, 'old media cannot be acquired during transition');
  next._visible = true;
  h.doc.elementsFromPoint = () => [next];
  await h.tick(180);
  h.flushRaf();
  assert.ok(h.flyVisible);
  assert.equal(h.context.testButtonDisabled(), false);
  assert.notEqual(h.perch.x, old.x);
  assert.equal(h.sentMessages.filter(m => m.type === 'media-options').length, 0);
  assert.equal(h.fetchCalls.length, 0);
});

test('a Facebook story with decoded video gets the Butterfly without a URL source', async () => {
  const h = makeContentHarness({ href: 'https://www.facebook.com/stories/12345/UzpfTEST=/' });
  const v = new h.Video({ srcObject: {}, readyState: 4, paused: false, autoplay: true, defaultMuted: true, loop: true });
  h.doc.body.appendChild(v);
  h.triggerMO([{ type: 'childList', target: h.doc.body, addedNodes: [v], removedNodes: [] }]);
  await h.tick();
  h.flushRaf();
  assert.ok(h.flyVisible);
  assert.equal(h.sentMessages.some(message => message.type === 'media-options'), false);
});

test('content requests object-backed provenance only for the current bound object', () => {
  const h = makeContentHarness();
  const object = {};
  const v = new h.Video({ srcObject: object, readyState: 4 });
  let requests = 0;
  v.dispatchEvent = () => {
    requests++;
    v.setAttribute('data-vesper-media-provenance', JSON.stringify({ paths: [{ path: 'https://cdn.test/media', seen: Date.now() }], tracks: [{ id: 'sb-1' }] }));
  };
  const binding = { src: '', srcObject: object };
  assert.equal(h.context.testWitness(v, binding).tracks.length, 1);
  assert.equal(requests, 1);
  v.srcObject = {};
  assert.equal(h.context.testWitness(v, binding).tracks.length, 0);
  assert.equal(requests, 1);
  v.srcObject = object;
  v.mediaKeys = {};
  assert.equal(h.context.testWitness(v, binding).tracks.length, 0);
  assert.equal(requests, 1);
});

test('Instagram reel alias is preferred to the generic post link in either order', () => {
  const h = makeContentHarness();
  const origin = 'https://www.instagram.com';
  const reel = { href: origin + '/reels/AbC123/' };
  const post = { href: origin + '/p/AbC123/' };
  assert.equal(h.context.VesperPlayerSource.cardPage([reel, post], origin), reel.href);
  assert.equal(h.context.VesperPlayerSource.cardPage([post, reel], origin), reel.href);
  assert.equal(h.context.VesperPlayerSource.cardPage([post, {href: origin + '/reel/different/'}], origin), null);
});

test('Instagram permalinks with a username prefix keep their post identity', () => {
  const ctx = vm.createContext({ URL });
  vm.runInContext(src('player-source'), ctx);
  const id = ctx.VesperPlayerSource.identity;
  assert.equal(id('https://www.instagram.com/creator.name/reel/CxYz123AbC/'), 'instagram:CxYz123AbC');
  assert.equal(id('https://www.instagram.com/creator.name/p/DpQr987XyZ/'), 'instagram:DpQr987XyZ');
  assert.equal(id('https://www.instagram.com/reel/CxYz123AbC/?img_index=2'), 'instagram:CxYz123AbC:2');
  assert.equal(id('https://www.instagram.com/stories/user/123/'), 'instagram:story:user:123');
  assert.equal(id('https://www.instagram.com/reel/CxYz123AbC/?img_index=abc'),
    'https://www.instagram.com/reel/CxYz123AbC/?img_index=abc');
});

test('MSE: a full-buffer quality switch rebuilds the track; a partial remove still invalidates it', () => {
  let time = 100000, serial = 0;
  const listeners = new Map();
  class WitnessURL extends URL { static createObjectURL() { return 'blob:https://www.instagram.com/' + ++serial; } }
  class Buffer {
    appendBuffer() {}
    remove(start, end) {
      if (start === 0 && end === Infinity) this.buffered = { length: 0 };
    }
    changeType() {}
    buffered = { length: 1, start: () => 0, end: () => 14.78 };
  }
  class Source {
    activeSourceBuffers = [];
    addSourceBuffer() { const b = new Buffer(); this.activeSourceBuffers.push(b); return b; }
  }
  class Player {
    constructor(s) { this.currentSrc = s; this.attributes = new Map(); this.duration = 15; this.buffered = { length: 1, start: () => 0, end: () => 15 }; }
    setAttribute(k, v) { this.attributes.set(k, v); }
  }
  const ctx = vm.createContext({
    URL: WitnessURL, MediaSource: Source, SourceBuffer: Buffer, HTMLMediaElement: Player,
    WeakRef, ArrayBuffer, Blob, Date: class extends Date { static now() { return time; } },
    document: { addEventListener: (t, f) => listeners.set(t, f) }
  });
  vm.runInContext(src('media-source-witness'), ctx);
  const ms = new Source();
  const el = new Player(WitnessURL.createObjectURL(ms));
  const audio = ms.addSourceBuffer('audio/mp4; codecs="mp4a.40.2"');
  const box = t => Uint8Array.of(0, 0, 0, 8, ...[...t].map(c => c.charCodeAt(0)));
  const init1 = Uint8Array.of(...box('ftyp'), ...box('moov'));
  const init2 = Uint8Array.of(0, 0, 0, 12, ...[...'ftyp'].map(c => c.charCodeAt(0)), 1, 2, 3, 4, ...box('moov'));
  const provenance = () => {
    el.attributes.delete('data-vesper-media-provenance');
    listeners.get('vesper-media-provenance-request')({ target: el });
    return JSON.parse(el.attributes.get('data-vesper-media-provenance'));
  };
  audio.appendBuffer(init1);
  audio.appendBuffer(box('mdat'));
  assert.equal(provenance().tracks[0].bufferedComplete, true, 'AAC tail up to 0.35 s short of the video is still a complete audio buffer');
  const wholeRange = audio.buffered;
  audio.changeType('audio/mp4; codecs="mp4a.40.2"');
  audio.appendBuffer(init2);
  audio.appendBuffer(box('mdat'));
  assert.equal(provenance().tracks[0].bufferedComplete, false, 'old buffered frames cannot verify a changed track');
  audio.remove(0, Infinity);
  audio.appendBuffer(init2);
  audio.appendBuffer(box('mdat'));
  audio.buffered = wholeRange;
  assert.equal(provenance().tracks[0].bufferedComplete, true, 'a full-buffer ABR reset must not permanently evict the track');
  audio.remove(0, 1);
  assert.equal(provenance().tracks[0].bufferedComplete, false, 'a partial remove still invalidates the retained copy');

  // A SourceBuffer can advertise both codecs while its init holds only video.
  const makeBox = (type, payload) => Uint8Array.of(0, 0, 0, payload.length + 8, ...box(type).slice(4), ...payload);
  const handler = makeBox('hdlr', Uint8Array.of(0, 0, 0, 0, 0, 0, 0, 0, 118, 105, 100, 101));
  const moov = makeBox('moov', makeBox('trak', makeBox('mdia', handler)));
  const video = ms.addSourceBuffer('video/mp4; codecs="avc1.640028,mp4a.40.2"');
  video.appendBuffer(moov);
  video.appendBuffer(box('mdat'));
  assert.equal(provenance().tracks[1].role, 'video');
  assert.equal(provenance().tracks[1].mimeType, 'video/mp4');
});
