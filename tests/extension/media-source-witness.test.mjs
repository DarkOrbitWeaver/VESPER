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
    blob() { return this.promise; }
  }
  const context = vm.createContext({ URL: WitnessURL, MediaSource: Source, SourceBuffer: Buffer,
    HTMLMediaElement: Player, Response: FetchResponse, WeakRef, ArrayBuffer, Blob,
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
  return {player, request, FetchResponse, WitnessURL, Player, advance: ms => time += ms};
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

test('a media response fetched before MediaSource creation retains exact object provenance', async () => {
  const b = setup(), bytes = new ArrayBuffer(16);
  await new b.FetchResponse('https://cdn.test/early.mp4?session=private', 'video/mp4', bytes).arrayBuffer();
  const a = b.player(), neighbour = b.player();
  a.buffer.appendBuffer(bytes);
  assert.deepEqual(b.request(a.el), [{path:'https://cdn.test/early.mp4',seen:100000}]);
  assert.deepEqual(b.request(neighbour.el), []);
});

test('direct media Blob URL retains only its own response provenance', async () => {
  const b = setup(), blob = new Blob(['media'], {type:'video/mp4'});
  const response = new b.FetchResponse('https://cdn.test/clip.mp4?session=private', 'video/mp4', blob);
  assert.equal(response.blob(),response.promise);
  assert.equal(await response.promise,blob);
  const player = new b.Player(b.WitnessURL.createObjectURL(blob));
  const unrelated = new b.Player(b.WitnessURL.createObjectURL(new Blob(['media'],{type:'video/mp4'})));
  assert.deepEqual(b.request(player), [{path:'https://cdn.test/clip.mp4',seen:100000}]);
  assert.deepEqual(b.request(unrelated),[]);
  b.advance(120001);
  assert.deepEqual(b.request(player),[]);
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

function fragment(seed = 1) {
  const bytes = new Uint8Array(264);
  new DataView(bytes.buffer).setUint32(0, bytes.length);
  bytes.set([109,100,97,116],4);
  for (let i=8;i<bytes.length;i++) bytes[i]=(i*37+seed*13+i*i)%251;
  return bytes;
}

test('copied media buffers yield a scoped path hint without exact resource authority', async () => {
  const b=setup(), a=b.player(), other=b.player(), bytes=fragment();
  await new b.FetchResponse('https://cdn.test/clip.m4s?secret=1','video/mp4',bytes.buffer).arrayBuffer();
  const copied=bytes.slice();
  a.buffer.appendBuffer(copied);
  assert.deepEqual(b.request(a.el),[{path:'https://cdn.test/clip.m4s',seen:100000}]);
  assert.deepEqual(b.request(other.el),[]);
  a.el.mediaKeys={};
  assert.deepEqual(b.request(a.el),[]);
});

test('distinct request instances with identical samples remain ambiguous', async () => {
  const b=setup(), a=b.player(), bytes=fragment();
  await new b.FetchResponse('https://cdn.test/a.m4s','video/mp4',bytes.buffer).arrayBuffer();
  await new b.FetchResponse('https://cdn.test/b.m4s','video/mp4',bytes.slice().buffer).arrayBuffer();
  a.buffer.appendBuffer(bytes.slice());
  assert.deepEqual(b.request(a.el),[]);
});

test('sample evidence expires and unrelated or low-entropy payloads are not associated', async () => {
  const b=setup(), a=b.player(), bytes=fragment();
  await new b.FetchResponse('https://cdn.test/a.m4s','video/mp4',bytes.buffer).arrayBuffer();
  a.buffer.appendBuffer(fragment(2));
  assert.deepEqual(b.request(a.el),[]);
  b.advance(120001);
  a.buffer.appendBuffer(bytes.slice());
  assert.deepEqual(b.request(a.el),[]);
});

test('header-only transformations retain the media sample but flat payloads do not', async () => {
  const b=setup(), a=b.player(), bytes=fragment();
  await new b.FetchResponse('https://cdn.test/transformed.m4s','video/mp4',bytes.buffer).arrayBuffer();
  const transformed=new Uint8Array(bytes.length+8);
  new DataView(transformed.buffer).setUint32(0,8);
  transformed.set([102,114,101,101],4); // extra free box
  transformed.set(bytes,8);
  a.buffer.appendBuffer(transformed);
  a.buffer.appendBuffer(transformed.slice());
  assert.deepEqual(b.request(a.el),[{path:'https://cdn.test/transformed.m4s',seen:100000}]);
  const flat=fragment(); flat.fill(0,8);
  await new b.FetchResponse('https://cdn.test/flat.m4s','video/mp4',flat.buffer).arrayBuffer();
  const other=b.player();other.buffer.appendBuffer(flat.slice());
  assert.deepEqual(b.request(other.el),[]);
});

test('copied WebM and TS observations are scoped hints, not exact fingerprints', async () => {
  for (const kind of ['webm','ts']) {
    const b=setup(), a=b.player(), other=b.player();
    const bytes=new Uint8Array(kind==='ts'?188*5:512);
    for(let i=0;i<bytes.length;i++) bytes[i]=(i*i+37*i)%251;
    if(kind==='ts') for(let i=0;i<bytes.length;i+=188){bytes[i]=0x47;bytes[i+3]=0x10}
    else new DataView(bytes.buffer).setUint32(0,0x1f43b675);
    await new b.FetchResponse('https://cdn.test/clip.'+kind,'video/'+(kind==='ts'?'mp2t':'webm'),bytes.buffer).arrayBuffer();
    a.buffer.appendBuffer(bytes.slice());
    assert.deepEqual(b.request(a.el),[{path:'https://cdn.test/clip.'+kind,seen:100000}]);
    assert.deepEqual(b.request(other.el),[]);
  }
});
test('mutated sampled buffers lose exact provenance instead of certifying altered media',async()=>{
  const b=setup(), a=b.player(), bytes=fragment();
  await new b.FetchResponse('https://cdn.test/clip.m4s','video/mp4',bytes.buffer).arrayBuffer();
  bytes[8]^=0xff;
  a.buffer.appendBuffer(bytes);
  assert.deepEqual(b.request(a.el),[]);
});

test('streaming ReadableStream reader chunks associate with the SourceBuffer that appends them', async () => {
  const b = setup(), a = b.player(), neighbour = b.player();
  class MockReader {
    constructor(chunks, stream) { this.chunks = chunks; this.stream = stream; }
    async read() { return this.chunks.length ? { done: false, value: this.chunks.shift() } : { done: true, value: undefined }; }
  }
  class MockStream {
    constructor(chunks) { this.chunks = chunks; }
    getReader() { return new MockReader(this.chunks, this); }
  }
  const chunk = new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8]);
  class StreamResponse extends b.FetchResponse {
    get body() { return new MockStream([chunk]); }
  }
  // Re-run witness inside a context providing ReadableStream globals.
  let time = 100_000, serial = 0;
  const listeners = new Map();
  class StreamURL extends URL { static createObjectURL() { return 'blob:https://site.test/' + ++serial; } }
  class StreamBuf { appendBuffer(value) { this.last = value; } }
  class StreamMS { addSourceBuffer() { return new StreamBuf(); } }
  class StreamPlayer { constructor(src) { this.currentSrc = src; this.attributes = new Map(); } setAttribute(k, v) { this.attributes.set(k, v); } }
  class FetchRes {
    constructor(url, mime, stream) { this._url = url; this._mime = mime; this._stream = stream; this.headers = { get: () => mime }; }
    get url() { return this._url; }
    get body() { return this._stream; }
    arrayBuffer() { return Promise.resolve(new ArrayBuffer(0)); }
    blob() { return Promise.resolve(new ArrayBuffer(0)); }
  }
  const ctx = vm.createContext({
    URL: StreamURL, MediaSource: StreamMS, SourceBuffer: StreamBuf, HTMLMediaElement: StreamPlayer,
    Response: FetchRes, ReadableStream: MockStream, ReadableStreamDefaultReader: MockReader,
    WeakRef, ArrayBuffer, Blob, Date: class extends Date { static now() { return time; } },
    document: { addEventListener: (t, fn) => listeners.set(t, fn) }
  });
  vm.runInContext(script('media-source-witness'), ctx);
  const ms = new StreamMS();
  const el = new StreamPlayer(StreamURL.createObjectURL(ms));
  const buf = ms.addSourceBuffer('video/mp4');
  const reader = new FetchRes('https://cdninstagram.com/v/clip.mp4?bytestart=0&byteend=999', 'video/mp4', new MockStream([chunk])).body.getReader();
  const res = await reader.read();
  buf.appendBuffer(res.value);
  listeners.get('vesper-media-provenance-request')({ target: el });
  assert.deepEqual(JSON.parse(el.attributes.get('data-vesper-media-provenance')).paths,
    [{ path: 'https://cdninstagram.com/v/clip.mp4', seen: 100_000 }]);
  assert.ok(a && neighbour && b);
});
