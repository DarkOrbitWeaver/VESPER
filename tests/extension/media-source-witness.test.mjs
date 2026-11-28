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
    mode='segments'; timestampOffset=0; appendWindowStart=0; appendWindowEnd=Infinity;
    appendBuffer(value) { this.last = value; return undefined; }
    remove() {}
    changeType() {}
    buffered = { length: 1, start: () => 0, end: () => 10 };
  }
  class Source {
    readyState='ended';
    activeSourceBuffers = [];
    removeSourceBuffer(buffer) {
      this.activeSourceBuffers = this.activeSourceBuffers.filter(item => item !== buffer);
    }
    addSourceBuffer() {
      const buffer = new Buffer();
      this.activeSourceBuffers.push(buffer);
      return buffer;
    }
  }
  class Player {
    constructor(src) { this.currentSrc = src; this.attributes = new Map(); this.tagName = 'VIDEO'; this.readyState = 4; }
    setAttribute(key, value) { this.attributes.set(key, value); }
    getAttribute(key) { return this.attributes.get(key); }
    removeAttribute(key) { this.attributes.delete(key); }
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
  class Xhr extends EventTarget {
    readyState = 0;
    status = 0;
    open(method, url) { this.responseURL = url; this.readyState = 1; this.status = 0; this.value = null; }
    setRequestHeader() {}
    send() {}
    finish(value) { this.value = value; this.readyState = 4; this.status = 200; this.dispatchEvent(new Event('loadend')); }
    get responseType() { return 'arraybuffer'; }
    get response() { return this.value; }
    getResponseHeader(name) { return name === 'content-type' ? 'video/mp4' : ''; }
  }
  const context = vm.createContext({ URL: WitnessURL, MediaSource: Source, SourceBuffer: Buffer, btoa,
    HTMLMediaElement: Player, Response: FetchResponse, XMLHttpRequest: Xhr, WeakRef, ArrayBuffer, Blob,
    Date: class extends Date { static now() { return time; } },
    document: { addEventListener: (type, fn) => listeners.set(type, fn) } });
  vm.runInContext(script('media-source-witness'), context);
  const player = () => {
    const source = new Source();
    const el = new Player(WitnessURL.createObjectURL(source));
    return { source, el, buffer: source.addSourceBuffer('video/mp4') };
  };
  const provenance = el => {
    el.attributes.delete('data-vesper-media-provenance');
    listeners.get('vesper-media-provenance-request')({target:el});
    return JSON.parse(el.attributes.get('data-vesper-media-provenance') || '{"paths":[], "tracks":[]}');
  };
  const declaration = el => {
    listeners.get('vesper-youtube-identity-request')({target:el});
    return el.attributes.get('data-vesper-player-manifest');
  };
  const exportChunk = (el, query) => {
    el.removeAttribute('data-vesper-mse-export-chunk');
    el.setAttribute('data-vesper-mse-export-query', JSON.stringify(query));
    listeners.get('vesper-mse-export-request')({target:el});
    return JSON.parse(el.getAttribute('data-vesper-mse-export-chunk') || 'null');
  };
  return {player, declaration, request: el => provenance(el).paths, provenance, exportChunk, FetchResponse, Xhr, WitnessURL, MediaSource: Source, Player, context,
    advance: ms => time += ms};
}

test('attached declarations are unique, typed, and do not invoke accessors', () => {
  const b = setup(), a = b.player();
  let reads = 0;
  a.el.library = { currentSource: { src: 'https://cdn.test/extensionless', type: 'video/mp4' } };
  Object.defineProperty(a.el.library, 'url', { get() { reads++; throw new Error('getter'); } });
  assert.equal(b.declaration(a.el), 'https://cdn.test/extensionless');
  assert.equal(reads, 0);
  a.el.other = { manifestUrl: 'https://cdn.test/second.mpd' };
  assert.equal(b.declaration(a.el), undefined);
  delete a.el.library;
  assert.equal(b.declaration(a.el), 'https://cdn.test/second.mpd');
});

test('MSE keeps an identical init and refuses incomplete, inactive or edited tracks', () => {
  const fixture = setup();
  const { source, buffer, el } = fixture.player();
  const mp4Box = type => Uint8Array.of(0, 0, 0, 8, ...[...type].map(char => char.charCodeAt(0)));
  const init = Uint8Array.of(...mp4Box('ftyp'), ...mp4Box('moov'));
  el.duration = 10;
  el.buffered = buffer.buffered;
  buffer.appendBuffer(init);
  assert.equal(fixture.provenance(el).tracks[0].bufferedComplete, false);
  buffer.appendBuffer(mp4Box('mdat'));
  buffer.appendBuffer(init.slice());
  assert.equal(fixture.provenance(el).tracks[0].bufferedComplete, true);
  buffer.buffered = { length: 1, start: () => 0, end: () => 4 };
  assert.equal(fixture.provenance(el).tracks[0].bufferedComplete, false);
  buffer.buffered = el.buffered;
  buffer.remove(0, 1);
  assert.equal(fixture.provenance(el).tracks[0].bufferedComplete, false);
  source.activeSourceBuffers.length = 0;
  assert.equal(fixture.provenance(el).tracks.length, 0);
});

test('active object URL survives feed churn without crossing source replacement', async () => {
  const b=setup(), a=b.player(), bytes=fragment();
  await new b.FetchResponse('https://cdn.test/live.m4s','video/mp4',bytes.buffer).arrayBuffer();
  a.buffer.appendBuffer(bytes);
  assert.equal(b.request(a.el)[0].path,'https://cdn.test/live.m4s');
  const others=Array.from({length:40},()=>b.player());
  assert.equal(b.request(a.el)[0].path,'https://cdn.test/live.m4s');
  a.el.currentSrc=others.at(-1).el.currentSrc;
  assert.deepEqual(b.request(a.el),[]);
  a.el.currentSrc='https://site.test/new.mp4';
  assert.deepEqual(b.request(a.el),[]);
});

test('partitioned initialization and thousands of tiny appends preserve exact bytes in bounded chunks', () => {
  const b=setup(), {el,buffer}=b.player();
  el.duration=10;el.buffered=buffer.buffered;
  const input=readFileSync(new URL('../Vesper.Tests/Fixtures/Media/instinct-mse.mp4',import.meta.url));
  for(let start=0;start<input.length;start+=7) buffer.appendBuffer(new Uint8Array(input.subarray(start,start+7)));
  const track=b.provenance(el).tracks[0];
  assert.equal(track.bufferedComplete,true);
  assert.equal(track.retainedBytes,input.length);
  const tracks=[{id:track.id,revision:track.revision,length:track.retainedBytes}];
  const parts=[];let partIndex=0,subOffset=0;
  while(true){
    const next=b.exportChunk(el,{tracks,trackId:track.id,partIndex,subOffset});
    assert(next?.chunk);
    parts.push(Buffer.from(next.chunk,'base64'));
    if(next.finishedAfterThis)break;
    partIndex=next.nextPartIndex;subOffset=next.nextSubOffset;
    assert(parts.length<8,'tiny appends should be coalesced');
  }
  assert.deepEqual(Buffer.concat(parts),input);
});

test('open, timestamp-adjusted, window-clipped and sequence-mode MSE tracks cannot be exported as complete',()=>{
  for(const mutation of ['open','offset','window','sequence']){
    const b=setup(), {source,el,buffer}=b.player();
    el.duration=10;el.buffered=buffer.buffered;
    if(mutation==='open')source.readyState='open';
    if(mutation==='offset')buffer.timestampOffset=5;
    if(mutation==='window')buffer.appendWindowStart=0.5;
    if(mutation==='sequence')buffer.mode='sequence';
    const box=type=>Uint8Array.of(0,0,0,8,...[...type].map(c=>c.charCodeAt(0)));
    buffer.appendBuffer(Uint8Array.of(...box('ftyp'),...box('moov')));
    buffer.appendBuffer(box('mdat'));
    assert.equal(b.provenance(el).tracks[0].bufferedComplete,false,mutation);
  }
});

test('export snapshot rejects equal-length replacement and added or removed active tracks', () => {
  const b=setup(), {source,el,buffer}=b.player();
  el.duration=10;el.buffered=buffer.buffered;
  const box=type=>Uint8Array.of(0,0,0,8,...[...type].map(c=>c.charCodeAt(0)));
  const init=Uint8Array.of(...box('ftyp'),...box('moov'));
  const append=(track)=>{track.appendBuffer(init);track.appendBuffer(box('mdat'));};
  const snapshot=()=>b.provenance(el).tracks.map(t=>({id:t.id,revision:t.revision,length:t.retainedBytes}));
  append(buffer);
  const before=snapshot();
  assert.equal(b.exportChunk(el,{tracks:before,validateOnly:true}).valid,true);
  assert.ok(b.exportChunk(el,{tracks:before,trackId:before[0].id,partIndex:0,subOffset:0}).chunk);
  buffer.remove(0,Infinity);
  buffer.buffered={length:0};append(buffer);
  buffer.buffered=el.buffered;
  const after=snapshot();
  assert.equal(after[0].length,before[0].length);
  assert.notEqual(after[0].revision,before[0].revision);
  assert.equal(b.exportChunk(el,{tracks:before,validateOnly:true}),null);
  assert.equal(b.exportChunk(el,{tracks:after,validateOnly:true}).valid,true);
  const audio=source.addSourceBuffer('audio/mp4');append(audio);
  assert.equal(b.exportChunk(el,{tracks:after,validateOnly:true}),null);
  const both=snapshot();
  assert.equal(b.exportChunk(el,{tracks:both,validateOnly:true}).valid,true);
  source.removeSourceBuffer(audio);
  assert.equal(b.exportChunk(el,{tracks:both,validateOnly:true}),null);
});

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

test('only current active tracks contribute source witnesses after replacement or changeType', async () => {
  const b = setup(), a = b.player();
  const audio = a.source.addSourceBuffer('audio/mp4');
  const attach = async (buffer, name) => {
    const bytes = new ArrayBuffer(16);
    await new b.FetchResponse('https://cdn.test/' + name + '.mp4', 'video/mp4', bytes).arrayBuffer();
    buffer.appendBuffer(bytes);
  };
  await attach(a.buffer, 'old-video');
  await attach(audio, 'audio');
  assert.equal(b.request(a.el).length, 2);
  a.buffer.changeType('video/webm');
  assert.deepEqual(b.request(a.el).map(item => item.path), ['https://cdn.test/audio.mp4']);
  await attach(a.buffer, 'new-video');
  assert.equal(b.request(a.el).length, 2);
  a.source.removeSourceBuffer(audio);
  assert.deepEqual(b.request(a.el).map(item => item.path), ['https://cdn.test/new-video.mp4']);
  a.buffer.remove(0, Infinity);
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

test('object-backed MSE emits provenance for its own active buffers only', async () => {
  const b = setup();
  const source = new b.MediaSource(), neighbour = new b.MediaSource();
  const a = new b.Player(''), other = new b.Player('');
  a.srcObject = source;
  other.srcObject = neighbour;
  const buffer = source.addSourceBuffer('video/mp4');
  neighbour.addSourceBuffer('video/mp4');
  const bytes = new ArrayBuffer(16);
  await new b.FetchResponse('https://cdn.test/clip.mp4?session=private', 'video/mp4', bytes).arrayBuffer();
  buffer.appendBuffer(bytes);
  assert.equal(b.provenance(a).kind, 'mse');
  assert.deepEqual(b.request(a), [{path:'https://cdn.test/clip.mp4',seen:100000}]);
  assert.deepEqual(b.request(other), []);
  a.mediaKeys = {};
  assert.deepEqual(b.request(a), []);
  a.mediaKeys = null;
  const stale = b.player();
  a.currentSrc = stale.el.currentSrc;
  a.srcObject = {};
  assert.deepEqual(b.request(a), []);
  assert.deepEqual(b.provenance(a).tracks, []);
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
  vm.runInContext(script('vesper-evidence-graph'), context);
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
  vm.runInContext(script('vesper-evidence-graph'), context);
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
  vm.runInContext(script('vesper-evidence-graph'), context);
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

test('mid-payload mutations and subsequent ArrayBuffer copies cannot restore source evidence', async () => {
  const b=setup(), a=b.player(), other=b.player(), bytes=fragment();
  await new b.FetchResponse('https://cdn.test/clip.m4s','video/mp4',bytes.buffer).arrayBuffer();
  bytes[136]^=0xff;
  a.buffer.appendBuffer(bytes);
  other.buffer.appendBuffer(bytes.buffer.slice(0));
  assert.deepEqual(b.request(a.el),[]);
  assert.deepEqual(b.request(other.el),[]);
});

test('a derived buffer does not renew expired network provenance', async () => {
  const b=setup(), a=b.player(), bytes=fragment();
  await new b.FetchResponse('https://cdn.test/clip.m4s','video/mp4',bytes.buffer).arrayBuffer();
  b.advance(119000);
  const copy=bytes.buffer.slice(0);
  b.advance(1001);
  a.buffer.appendBuffer(copy);
  assert.deepEqual(b.request(a.el),[]);
});

test('late appends cannot renew the original network evidence lifetime', async () => {
  const b=setup(), a=b.player(), bytes=fragment();
  await new b.FetchResponse('https://cdn.test/old.m4s','video/mp4',bytes.buffer).arrayBuffer();
  b.advance(119000);
  a.buffer.appendBuffer(bytes);
  assert.equal(b.request(a.el).length,1);
  b.advance(1001);
  assert.deepEqual(b.request(a.el),[]);
});

test('XHR rereads and delayed first access cannot restore old response authority; new requests can', () => {
  const b=setup(), a=b.player(), xhr=new b.Xhr(), bytes=fragment();
  xhr.open('GET','https://cdn.test/old.m4s'); xhr.send(); xhr.finish(bytes.buffer);
  a.buffer.appendBuffer(xhr.response);
  assert.equal(b.request(a.el).length,1);
  b.advance(120001);
  a.buffer.appendBuffer(xhr.response);
  assert.deepEqual(b.request(a.el),[]);
  xhr.open('GET','https://cdn.test/delayed.m4s'); xhr.send(); xhr.finish(fragment(2).buffer);
  b.advance(120001);
  a.buffer.appendBuffer(xhr.response);
  assert.deepEqual(b.request(a.el),[]);
  xhr.open('GET','https://cdn.test/new.m4s'); xhr.send(); xhr.finish(fragment(3).buffer);
  a.buffer.appendBuffer(xhr.response);
  assert.equal(b.request(a.el)[0].path,'https://cdn.test/new.m4s');
});

test('a shared last mdat cannot identify a copied multi-payload response', async () => {
  const b=setup(), a=b.player();
  const join=(first,last)=>{const result=new Uint8Array(first.length+last.length);
    result.set(first); result.set(last,first.length); return result;};
  const bytes=join(fragment(1),fragment(3));
  await new b.FetchResponse('https://cdn.test/clip.m4s','video/mp4',bytes.buffer).arrayBuffer();
  a.buffer.appendBuffer(join(fragment(2),fragment(3)));
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
  class StreamMS {
    activeSourceBuffers = [];
    addSourceBuffer() {
      const buffer = new StreamBuf();
      this.activeSourceBuffers.push(buffer);
      return buffer;
    }
  }
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
    [{ path: 'https://cdninstagram.com/v/clip.mp4', seen: 100_000, canonicalUrl: 'https://cdninstagram.com/v/clip.mp4' }]);
  assert.ok(a && neighbour && b);
});
