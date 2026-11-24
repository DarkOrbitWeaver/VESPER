// Runs the real player-source.js. Run: node --test tests/extension/player-source.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const context = { URL };
vm.createContext(context);
vm.runInContext(readFileSync(new URL('../../extension/player-source.js', import.meta.url), 'utf8'), context);
const { cardPage } = context.VesperPlayerSource;

test('a card with one site identity names that post, whatever else it links to', () => {
  const origin = 'https://www.reddit.com';
  assert.equal(cardPage([
    { href: 'https://www.reddit.com/r/videos/' },
    { href: 'https://www.reddit.com/user/someone/' },
    { href: 'https://www.reddit.com/r/videos/comments/abc123/a_title/' },
    { href: 'https://www.youtube.com/watch?v=abcdefghijk' }
  ], origin), 'https://www.reddit.com/r/videos/comments/abc123/a_title/');
});

test('sites with a native adapter are recognised in cards, not only the first four', () => {
  for (const [origin, href] of [
    ['https://www.dailymotion.com', 'https://www.dailymotion.com/video/x8abcde'],
    ['https://vimeo.com', 'https://vimeo.com/123456789'],
    ['https://www.bilibili.com', 'https://www.bilibili.com/video/BV1xx411c7mD'],
    ['https://x.com', 'https://x.com/someone/status/1234567890123'],
    ['https://tube.example', 'https://tube.example/w/9c9de5e8-0a1e-484a']])
    assert.equal(cardPage([{ href: origin + '/' }, { href }], origin), href, href);
});

test('two different posts in one card name neither', () => {
  const origin = 'https://www.youtube.com';
  assert.equal(cardPage([{ href: 'https://www.youtube.com/watch?v=aaaaaaaaaaa' },
    { href: 'https://www.youtube.com/watch?v=bbbbbbbbbbb' }], origin), null);
});

test('the same post linked twice is still one post', () => {
  const origin = 'https://www.youtube.com';
  assert.equal(cardPage([{ href: 'https://www.youtube.com/watch?v=aaaaaaaaaaa' },
    { href: 'https://www.youtube.com/watch?v=aaaaaaaaaaa&t=10s' }], origin), 'https://www.youtube.com/watch?v=aaaaaaaaaaa&t=10s');
});

test('on an unknown site a timestamp link or permalink attribute names the post', () => {
  const origin = 'https://videos.example';
  assert.equal(cardPage([
    { href: 'https://videos.example/users/ana' },
    { href: 'https://videos.example/posts/8841', permalink: true },
    { href: 'https://videos.example/tags/cats' }
  ], origin), 'https://videos.example/posts/8841');
});

test('ordinary links on an unknown site name nothing', () => {
  const origin = 'https://videos.example';
  assert.equal(cardPage([{ href: 'https://videos.example/posts/8841' }, { href: 'https://videos.example/users/ana' }], origin), null);
});

test('two different permalinks, a permalink to the site root, or one on another origin name nothing', () => {
  const origin = 'https://videos.example';
  assert.equal(cardPage([{ href: 'https://videos.example/posts/1', permalink: true },
    { href: 'https://videos.example/posts/2', permalink: true }], origin), null);
  assert.equal(cardPage([{ href: 'https://videos.example/', permalink: true }], origin), null);
  assert.equal(cardPage([{ href: 'https://elsewhere.example/posts/1', permalink: true }], origin), null);
});

test('a generic permalink query can identify another post and must remain ambiguous', () => {
  const origin = 'https://videos.example';
  assert.equal(cardPage([{ href: 'https://videos.example/posts/1', permalink: true },
    { href: 'https://videos.example/posts/1?ref=feed', permalink: true }], origin), null);
});

test('generic query-only identifiers are not collapsed into the wrong post', () => {
  const origin = 'https://videos.example';
  assert.equal(cardPage([{href: origin + '/watch?id=one', permalink:true},
    {href: origin + '/watch?id=two', permalink:true}], origin), null);
});

test('a site identity outranks a generic permalink, and malformed input is ignored', () => {
  const origin = 'https://www.dailymotion.com';
  assert.equal(cardPage([{ href: 'https://www.dailymotion.com/about', permalink: true },
    { href: 'https://www.dailymotion.com/video/x8abcde' }, { href: 'javascript:void(0)' }, null, { href: 42 }], origin),
  'https://www.dailymotion.com/video/x8abcde');
  assert.equal(cardPage(undefined, origin), null);
});
