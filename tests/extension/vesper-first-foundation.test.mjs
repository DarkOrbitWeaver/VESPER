import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const extension = name => readFileSync(new URL(`../../extension/${name}`, import.meta.url), 'utf8');
const eventName = 'vesper-youtube-identity-request';

function fakeVideo(player) {
  const attributes = new Map();
  return {
    attributes, tagName: 'VIDEO', isConnected: true, readyState: 2,
    closest: selector => selector === '.html5-video-player' ? player : null,
    setAttribute: (key, value) => attributes.set(key, value),
    removeAttribute: key => attributes.delete(key),
    getAttribute: key => attributes.get(key) || null,
    dispatchEvent(event) { this._document?.notify(event.type, this); return true; }
  };
}

function witnessPage(videos) {
  const callbacks = new Map();
  const document = {
    addEventListener(type, callback) { callbacks.set(type, callback); },
    notify(type, target) { callbacks.get(type)?.({ type, target }); }
  };
  for (const video of videos) video._document = document;
  const ctx = vm.createContext({ document });
  vm.runInContext(extension('media-source-witness.js'), ctx);
  return { document, refresh: () => videos.forEach(video => video.dispatchEvent(new Event(eventName))) };
}

function contentFunction(first, next, context) {
  const content = extension('content.js');
  const begin = content.indexOf(`  function ${first}(`);
  const end = content.indexOf(`  function ${next}(`, begin + 2);
  assert.ok(begin > 0 && end > begin, `expected ${first} implementation in content.js`);
  const sandbox = vm.createContext(context);
  vm.runInContext(`${content.slice(begin, end)}\nglobalThis.__subject = ${first};`, sandbox);
  return sandbox.__subject;
}

test('YouTube page-world witness binds exact video elements, not nearby recommendations', () => {
  let firstId = 'abcdefghijk', secondId = 'lmnopqrstuv';
  let first, second;
  const firstPlayer = { getVideoData: () => ({ video_id: firstId }), contains: el => el === first };
  const secondPlayer = { getVideoData: () => ({ video_id: secondId }), contains: el => el === second };
  first = fakeVideo(firstPlayer);
  second = fakeVideo(secondPlayer);
  const witness = witnessPage([first, second]);
  witness.refresh();
  assert.equal(first.getAttribute('data-vesper-youtube-video-id'), firstId);
  assert.equal(second.getAttribute('data-vesper-youtube-video-id'), secondId);
  firstId = 'mnbvcxz1234';
  witness.refresh();
  assert.equal(first.getAttribute('data-vesper-youtube-video-id'), firstId);
  assert.equal(second.getAttribute('data-vesper-youtube-video-id'), secondId);
});

test('a reused player clears stale ID if page-world ID becomes absent or invalid', () => {
  let id = 'abcdefghijk', video;
  const player = { getVideoData: () => ({ video_id: id }), contains: el => el === video };
  video = fakeVideo(player);
  const { refresh } = witnessPage([video]);
  refresh();
  assert.equal(video.getAttribute('data-vesper-youtube-video-id'), id);
  id = null;
  refresh();
  assert.equal(video.getAttribute('data-vesper-youtube-video-id'), null);
  id = 'injected/invalid';
  refresh();
  assert.equal(video.getAttribute('data-vesper-youtube-video-id'), null);
});

test('the YouTube hint requires the player to contain its own video', () => {
  const player = { getVideoData: () => ({ video_id: 'abcdefghijk' }), contains: () => false };
  const video = fakeVideo(player);
  witnessPage([video]).refresh();
  assert.equal(video.getAttribute('data-vesper-youtube-video-id'), null);
});

test('active media page uses only the exact witnessed YouTube ID', () => {
  let id = 'abcdefghijk', video;
  const player = { getVideoData: () => ({ video_id: id }), contains: el => el === video };
  video = fakeVideo(player);
  const { document } = witnessPage([video]);
  const hostname = { hostname: 'www.youtube.com', origin: 'https://www.youtube.com' };
  const inspectPlayerWitnessAttributes = contentFunction('inspectPlayerWitnessAttributes', 'activeYouTubePage', { Event });
  const activeYouTubePage = contentFunction('activeYouTubePage', 'pageForPlayer',
    { document, location: hostname, Event, URL, inspectPlayerWitnessAttributes });
  assert.equal(activeYouTubePage(video), 'https://www.youtube.com/watch?v=abcdefghijk');
  assert.equal(activeYouTubePage(video, 'https://www.youtube.com/watch?v=abcdefghijk&list=PLabc123456'),
    'https://www.youtube.com/watch?v=abcdefghijk&list=PLabc123456');
  assert.equal(activeYouTubePage(video, 'https://www.youtube.com/watch?v=other-video1&list=PLabc123456'),
    'https://www.youtube.com/watch?v=abcdefghijk');
  assert.equal(activeYouTubePage(video, 'https://attacker.example/watch?v=abcdefghijk&list=PLabc123456'),
    'https://www.youtube.com/watch?v=abcdefghijk');
  id = 'lmnopqrstuv';
  assert.equal(activeYouTubePage(video), 'https://www.youtube.com/watch?v=lmnopqrstuv');
  id = '';
  assert.equal(activeYouTubePage(video), null);
  hostname.hostname = 'notyoutube.com';
  id = 'abcdefghijk';
  assert.equal(activeYouTubePage(video), null);
});

test('a manually pinned small player remains draggable', () => {
  const video = {};
  video.getBoundingClientRect = () => ({ left: 0, top: 0, right: 150, bottom: 90 });
  const drag = { id: 4, player: video, x: 0, y: 0, grabX: 4, grabY: 5, moved: false };
  const sizes = [];
  const moveDrag = contentFunction('moveDrag', 'dropTarget', {
    manualPlayer: video,
    video,
    drag,
    Math,
    playerAnchor: player => player,
    visiblePlayer: (...args) => { sizes.push(args); return { left: 0, top: 0, right: 150, bottom: 90 }; },
    button: { classList: { add() {} } },
    dragPerch: () => ({ x: 0.5, y: 0.5 }),
    place() {}
  });

  moveDrag({ pointerId: 4, clientX: 20, clientY: 20 });

  assert.equal(sizes.length, 1);
  assert.equal(sizes[0][0], video);
  assert.equal(sizes[0][1], 110);
  assert.equal(sizes[0][2], 70);
  assert.equal(drag.moved, true);
});

test('manual perch can be placed well beyond original player border but remains clickable', () => {
  const view = { innerWidth: 1200, innerHeight: 750, FLY_SIZE: 44 };
  const dragPerch = contentFunction('dragPerch', 'moveDrag', view);
  const rect = { left: 160, top: 120, right: 560, bottom: 440 };
  const target = dragPerch(rect, 850, 500, 5, 5);
  assert.ok(target.x > 1.2, 'free-space perch extends beyond player by more than 44px');
  assert.ok(target.y > 1);
  const farEdge = dragPerch(rect, 4000, 3000, 5, 5);
  const left = rect.left + farEdge.x * (rect.right - rect.left - 44);
  const top = rect.top + farEdge.y * (rect.bottom - rect.top - 44);
  assert.ok(left >= 8 && left <= 1200 - 44 - 8);
  assert.ok(top >= 8 && top <= 750 - 44 - 8);
});

test('small nearby player wins when dropping over it; distant preview is ignored', () => {
  const first = { isConnected: true, rect: { left: 50, top: 80, right: 540, bottom: 440 } };
  const second = { isConnected: true, rect: { left: 650, top: 100, right: 800, bottom: 200 } };
  const distant = { isConnected: true, rect: { left: 1100, top: 100, right: 1400, bottom: 300 } };
  const visiblePlayer = (player, width, height) => {
    const rect = player.rect;
    return rect.right - rect.left >= width && rect.bottom - rect.top >= height ? rect : null;
  };
  const dropTarget = contentFunction('dropTarget', 'finishDrag', {
    nearby: new Set([first, second, distant]), video: first, visiblePlayer, Math, Number, fullscreenTarget: () => null
  });
  assert.equal(dropTarget(730, 150), second);
  assert.equal(dropTarget(620, 145), second, 'a near-edge perch can attach');
  assert.equal(dropTarget(580, 570), null, 'a distant free-space move stays with current player');
  second.isConnected = false;
  assert.equal(dropTarget(730, 150), null, 'disconnected preview cannot win');
});
