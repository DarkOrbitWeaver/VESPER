import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const script = name => readFileSync(new URL('../../extension/'+name+'.js', import.meta.url), 'utf8');

function setup() {
  let time = 100_000, serial = 0;
  const listeners = new Map();
  class WitnessURL extends URL {
    static createObjectURL() { return 'blob:https://site.test/' + ++serial; }
  }
  class Buffer {
    appendBuffer(value) { this.last = value; return undefined; }
  }
  class Source {
    addSourceBuffer() { return new Buffer(); }
  }
  class Player {
    constructor(src) { this.currentSrc = src; this.attributes = new Map(); }
    setAttribute(key, value) { this.attributes.set(key, value); }
  }
  class FetchResponse {
    constructor(url, mime, buffer) {
      this.url = url;
      this.headers = { get: () => mime };
      this.promise = Promise.resolve(buffer);
    }
    arrayBuffer() { return this.promise; }
  }
  const context = vm.createContext({ URL: WitnessURL, MediaSource: Source, SourceBuffer: Buffer,
    HTMLMediaElement: Player, Response: FetchResponse, WeakRef, ArrayBuffer,
    Date: class extends Date { static now() { return time; } },
    document: { addEventListener: (type, fn) => listeners.set(type, fn) } });
  vm.runInContext(script('media-source-witness'), context);
  const player = () => {
    const source = new Source();
    const el = new Player(WitnessURL.createObjectURL(source));
    return { source, el, buffer: source.addSourceBuffer('video/mp4') };
  };
  const request = el => {
    el.attributes.delete('data-vesper-media-provenance');
    listeners.get('vesper-media-provenance-request')({target:el});
    return JSON.parse(el.attributes.get('data-vesper-media-provenance') || '{"paths":[]}').paths;
  };
  return {player, request, FetchResponse, advance: ms => time += ms};
}

test('exact response buffer is associated with only the SourceBuffer that receives it', async () => {
  const b = setup(), a = b.player(), neighbour = b.player();
  const bytes = new ArrayBuffer(16);
  const response = new b.FetchResponse('https://cdn.test/movie-1234/segment.m4s?private=token', 'video/mp4', bytes);
  const promise = response.arrayBuffer();
  assert.equal(promise, response.promise);
  assert.equal(await promise, bytes);
  a.buffer.appendBuffer(new Uint8Array(bytes));
  assert.equal(a.buffer.last.buffer, bytes);
  assert.deepEqual(b.request(a.el), [{path:'https://cdn.test/movie-1234/segment.m4s', seen:100000}]);
  assert.deepEqual(b.request(neighbour.el), []);
  b.advance(120001);
  assert.deepEqual(b.request(a.el), []);
});

test('equal-length unrelated buffers and non-media responses do not create provenance', async () => {
  const b = setup(), a = b.player();
  const bytes = new ArrayBuffer(16);
  await new b.FetchResponse('https://cdn.test/clip.m4s', 'video/mp4', bytes).arrayBuffer();
  a.buffer.appendBuffer(new ArrayBuffer(16));
  await new b.FetchResponse('https://cdn.test/account', 'application/json', bytes).arrayBuffer();
  a.buffer.appendBuffer(new ArrayBuffer(16));
  assert.deepEqual(b.request(a.el), []);
});

test('media evidence graph ranks without confusing duration or directory with asset identity', () => {
  const context = vm.createContext({ URL });
  vm.runInContext(script('media-evidence'), context);
  const graph = context.VesperMediaEvidence;
  const entry = (url, duration) => ({source:{url}, reply:{ok:true,data:{duration,choices:[{height:720,hasVideo:true}]}}});
  const first = entry('https://cdn.test/movie-1234/manifest.mpd',60);
  const second = entry('https://cdn.test/movie-5678/manifest.mpd',60);
  const input = {sources:[second,first], player:{durationSeconds:60,height:720},
    fragments:['https://cdn.test/movie-1234/seg1.m4s','https://cdn.test/movie-1234/seg2.m4s']};
  const result = graph.associate(input);
  assert.equal(result.nodes[0].entry,first);
  assert.equal(result.selected,null);
  assert.equal(graph.associate({...input,currentUrl:second.source.url}).selected,second);
  assert.equal(graph.associate({...input,sources:[first]}).selected,null);
});

 test('same CDN path and duration cannot outrank an exact player resource match', () => {
  const context = vm.createContext({ URL });
  vm.runInContext(script('media-evidence'), context);
  const graph = context.VesperMediaEvidence;
  const entry = url => ({source:{url},reply:{ok:true,data:{duration:60,choices:[{height:720,hasVideo:true}]}}});
  const other = entry('https://cdn.test/movie-1234/video.mp4?clip=other');
  const correct = entry('https://cdn.test/movie-1234/video.mp4?clip=current');
  const result = graph.associate({sources:[other,correct],player:{durationSeconds:60,height:720},
    fragments:['https://cdn.test/movie-1234/video.mp4'],exactResources:[correct.source.url]});
  assert.equal(result.nodes[0].entry,correct);
  assert.ok(result.nodes[0].edges.some(edge=>edge.kind==='player-resource'));
  assert.equal(result.selected,null,'a ranking hint must not authorize automatic selection');
});

 test('parsed manifest membership ranks verified streams without claiming ownership', () => {
  const context = vm.createContext({ URL });
  vm.runInContext(script('media-evidence'), context);
  const graph = context.VesperMediaEvidence;
  const entry = (url, matches) => ({source:{url},reply:{ok:true,data:{duration:60,playbackMatches:matches,choices:[{height:720,hasVideo:true}]}}});
  const unrelated = entry('https://cdn.test/nearby.mpd',0);
  const actual = entry('https://cdn.test/unfamiliar.mpd',2);
  const result = graph.associate({sources:[unrelated,actual],player:{durationSeconds:60,height:720}});
  assert.equal(result.nodes[0].entry,actual);
  assert.ok(result.nodes[0].edges.some(edge=>edge.kind==='manifest-segment'));
  assert.equal(result.selected,null);
  assert.equal(graph.associate({sources:[entry('https://cdn.test/bad.mpd',999),unrelated]}).nodes[0].edges.length,0);
});
