import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = name => readFileSync(new URL(`../../extension/${name}.js`, import.meta.url), 'utf8');

test('the Butterfly motion picker explains its motion choices', () => {
  const popup = readFileSync(new URL('../../extension/popup.html', import.meta.url), 'utf8');
  const selectTag = popup.match(/<select id="motion"([^>]*)>([\s\S]*?)<\/select>/);
  const attributes = selectTag?.[1] || '';
  const select = selectTag?.[2];
  assert.ok(select);
  const options = [...select.matchAll(/<option value="([^"]+)">([^<]+)<\/option>/g)]
    .map(([, value, label]) => [value, label]);
  assert.deepEqual(options, [
    ['system', 'Follow system'], ['animate', 'Always animate'], ['still', 'No animation']
  ]);
  assert.match(attributes, /aria-describedby="motion-hint"/);
  assert.match(popup, /<small id="motion-hint" class="hint">Follow system respects your reduced-motion setting\.<\/small>/);
});

test('System motion follows the reduced-motion preference and explicit choices override it', () => {
  const document = new EventTarget();
  document.querySelectorAll = () => [];
  const window = new EventTarget();
  const motion = Object.assign(new EventTarget(), { matches: true });
  let updatePreference;
  const context = vm.createContext({
    window, document, AbortController, URL, location: { href: 'https://example.test/watch' },
    matchMedia: query => {
      assert.equal(query, '(prefers-reduced-motion: reduce)');
      return motion;
    },
    ResizeObserver: class { observe() {} disconnect() {} },
    IntersectionObserver: class { observe() {} unobserve() {} disconnect() {} },
    VesperTheme: {
      available: () => true,
      contextGone: () => false,
      watchPreference(key, fallback, apply) {
        assert.equal(key, 'motion');
        assert.equal(fallback, 'system');
        updatePreference = apply;
        return () => {};
      }
    },
    chrome: { runtime: { id: 'vesper-test' } }
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

  assert.equal(window.__testMotion.still(), true);
  updatePreference('system');
  assert.equal(window.__testMotion.still(), true);
  assert.equal(attributes.has('data-vesper-still'), true);
  assert.equal(stillModes.at(-1), true);

  motion.matches = false;
  motion.dispatchEvent(new Event('change'));
  assert.equal(window.__testMotion.still(), false);
  assert.equal(attributes.has('data-vesper-still'), false);
  assert.equal(stillModes.at(-1), false);

  motion.matches = true;
  motion.dispatchEvent(new Event('change'));
  assert.equal(window.__testMotion.still(), true);
  updatePreference('animate');
  assert.equal(window.__testMotion.still(), false);
  assert.equal(attributes.has('data-vesper-still'), false);
  motion.matches = false;
  motion.dispatchEvent(new Event('change'));
  assert.equal(window.__testMotion.still(), false);

  updatePreference('still');
  assert.equal(window.__testMotion.still(), true);
  assert.equal(attributes.has('data-vesper-still'), true);
  assert.equal(stillModes.at(-1), true);
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
      runtime: { get id() { return live ? 'vesper-test' : undefined; } },
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
      runtime: { id: 'vesper-test' },
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

test('a feed preview that starts after the pointer settles uses its linked page', async () => {
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
  let videos = [];
  let hoverTarget = card;
  card.parentElement = body;
  card.querySelectorAll = selector => selector === 'a[href]' ? [{ href: item }]
    : selector === 'video' ? videos : [];
  video.parentElement = card;

  const document = Object.assign(new EventTarget(), {
    body,
    documentElement: {},
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
    matchMedia: () => motion,
    ResizeObserver: class { observe() {} disconnect() {} },
    IntersectionObserver: class { observe() {} unobserve() {} disconnect() {} },
    chrome: {
      runtime: { id: 'vesper-test' },
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
    activePreview: () => activePreview
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
});
