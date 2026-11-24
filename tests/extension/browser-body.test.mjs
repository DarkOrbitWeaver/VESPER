import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash, webcrypto } from 'node:crypto';
import vm from 'node:vm';

const context = vm.createContext({ URL, Uint8Array, btoa, crypto: webcrypto, Date,
  Number, String, Math, Set, Map, setTimeout, clearTimeout, AbortController });
for (const name of ['vesper-intelligence', 'browser-body'])
  vm.runInContext(readFileSync(new URL(`../../extension/${name}.js`, import.meta.url), 'utf8'), context);
const body = context.VesperBrowserBody;
const origin = 'https://media.test';
const blobUrl = 'blob:https://media.test/fixture';
const source = new Uint8Array(700_000).map((_, i) => (i * 23 + 17) % 251);

function events() {
  const handlers = [];
  return { addListener(fn) { handlers.push(fn); }, emit(value) { for (const fn of handlers) fn(value); } };
}

function connection() {
  let disconnected = false;
  const left = { name: body.PORT, onMessage: events(), onDisconnect: events(),
    postMessage: value => { if (disconnected) throw Error('closed'); queueMicrotask(() => right.onMessage.emit(value)); },
    disconnect: () => { if (!disconnected) { disconnected = true; left.onDisconnect.emit(); right.onDisconnect.emit(); } } };
  const right = { name: body.PORT, onMessage: events(), onDisconnect: events(),
    postMessage: value => { if (disconnected) throw Error('closed'); queueMicrotask(() => left.onMessage.emit(value)); },
    disconnect: left.disconnect };
  return [left, right];
}

function bridge({ deny = false, wrongOffset = false, loseFinish = false, sender = {} } = {}) {
  const [client, worker] = connection();
  const [native, host] = connection();
  worker.sender = { tab: { id: 2 }, frameId: 0, documentId: 'test-document', url: origin + '/watch', ...sender };
  const messages = [], chunks = [];
  let id = 0, offset = 0, length = 0, launched = false;
  host.onMessage.addListener(message => {
    messages.push(message);
    let data;
    if (message.operation === 'begin') { length = message.length; data = { offset: 0, accepted: false }; }
    if (message.operation === 'append') {
      const bytes = Buffer.from(message.chunk, 'base64');
      assert.equal(message.offset, offset);
      assert.equal(createHash('sha256').update(bytes).digest('hex'), message.digest);
      assert.ok(bytes.length <= body.CHUNK);
      chunks.push(bytes); offset += bytes.length;
      data = { offset: wrongOffset ? offset - 1 : offset, accepted: false };
    }
    if (message.operation === 'finish') {
      assert.equal(offset, length);
      data = { offset, accepted: true, jobId: 'native-job-12345' };
    }
    if (message.operation === 'abort') data = {};
    if (loseFinish && message.operation === 'finish') { host.disconnect(); return; }
    host.postMessage({ id: message.id, ok: true, data });
  });
  const valid = body.relay(worker, { nativeHost: 'test-helper', canBegin: async () => !deny,
    connectNative: () => { launched = true; return native; }, makeId: () => 'id-fixture-' + ++id });
  return { valid, messages, chunks, get launched() { return launched; }, connect: () => client };
}
function response(bytes = source, length = bytes.length, type = 'video/mp4') {
  return new Response(bytes, { headers: { 'Content-Type': type, 'Content-Length': String(length) } });
}
function copy(b, options = {}) {
  return body.copyBlob({ url: blobUrl, origin, title: 'Fixture / clip', connect: b.connect,
    fetcher: async () => response(), ...options });
}

test('same-origin finite blob streams byte-exact chunks through both sides of the relay', async () => {
  const b = bridge();
  const progress = [];
  const result = await copy(b, { onProgress: (received, total) => progress.push([received, total]) });
  assert.equal(result.jobId, 'native-job-12345');
  assert.deepEqual(Buffer.concat(b.chunks), Buffer.from(source));
  assert.equal(b.messages[0].origin, origin + '/');
  assert.equal(b.messages[0].fileName, 'Fixture clip.mp4');
  assert.deepEqual(b.messages.map(message => message.operation), ['begin', 'append', 'append', 'append', 'finish']);
  assert.deepEqual(progress.at(-1), [source.length, source.length]);
  assert.equal(b.messages.some(message => 'cookie' in message || 'authorization' in message || 'path' in message), false);
});

test('foreign blobs and HTTP URLs cannot be opened by the finite-Blob route', async () => {
  for (const url of ['blob:https://other.test/one', origin + '/video.mp4', 'blob:null/id']) {
    let fetched = false;
    await assert.rejects(copy(bridge(), { url, fetcher: async () => { fetched = true; return response(); } }));
    assert.equal(fetched, false);
  }
});
test('unreadable, partial, unbounded or non-media bodies fail before any native connection', async () => {
  for (const make of [
    () => Promise.reject(new TypeError('MediaSource cannot be fetched')),
    () => new Response(source, { status: 206, headers: { 'Content-Type': 'video/mp4', 'Content-Length': String(source.length) } }),
    () => new Response(source, { headers: { 'Content-Type': 'video/mp4' } }),
    () => response(source, body.MAX_BYTES + 1),
    () => response(source, source.length, 'text/html')
  ]) {
    const b = bridge();
    await assert.rejects(copy(b, { fetcher: make }));
    assert.equal(b.launched, false);
  }
});
test('native refusal and incorrect acknowledgements never become acceptance', async () => {
  const denied = bridge({ deny: true });
  await assert.rejects(copy(denied));
  assert.equal(denied.launched, false);
  const corrupt = bridge({ wrongOffset: true });
  await assert.rejects(copy(corrupt));
  assert.equal(corrupt.messages.some(message => message.operation === 'finish'), false);
});
test('short or oversized body never sends finish', async () => {
  for (const length of [source.length - 1, source.length + 1]) {
    const b = bridge();
    await assert.rejects(copy(b, { fetcher: async () => response(source, length) }));
    assert.equal(b.messages.some(message => message.operation === 'finish'), false);
  }
});
test('cancel after first acknowledged chunk stops without accepting a partial', async () => {
  const b = bridge(), controller = new AbortController();
  await assert.rejects(copy(b, { signal: controller.signal,
    onProgress: received => { if (received > 0) controller.abort(); } }));
  assert.equal(b.messages.some(message => message.operation === 'finish'), false);
});
test('missing document identity and incognito cannot establish native body relay', () => {
  assert.equal(bridge({ sender: { documentId: undefined } }).valid, false);
  assert.equal(bridge({ sender: { tab: { id: 2, incognito: true } } }).valid, false);
});

test('generated ZIP and PDF blobs keep the selected original filename and exact bytes', async () => {
  for (const [name, mime] of [['export.zip','application/zip'],['report.pdf','application/pdf'],['snapshot.bin','application/octet-stream']]) {
    const b = bridge();
    const result = await copy(b,{generated:{fileName:name},fetcher:async()=>response(source,source.length,mime)});
    assert.equal(result.jobId,'native-job-12345');
    assert.equal(b.messages[0].fileName,name);
    assert.deepEqual(Buffer.concat(b.chunks),Buffer.from(source));
  }
});
test('generated fallback requires a matching live download anchor with a unique filename', () => {
  const link = (name,href=blobUrl,connected=true,download=true) => ({
    href,isConnected:connected,hasAttribute:()=>download,getAttribute:()=>name
  });
  assert.equal(body.generatedLink([link('archive.zip')],blobUrl,origin).fileName,'archive.zip');
  assert.equal(body.generatedLink([link('a.zip'),link('b.zip')],blobUrl,origin),null);
  assert.equal(body.generatedLink([link('a.zip',blobUrl,false)],blobUrl,origin),null);
  assert.equal(body.generatedLink([link('a.zip',blobUrl,true,false)],blobUrl,origin),null);
  assert.equal(body.generatedLink([link('a.zip')],blobUrl,'https://other.test'),null);
});
test('generated unnamed files use a safe MIME suffix without changing supplied filenames', () => {
  assert.equal(body.fileFacts(response(source,source.length,'application/zip'),'ignored',{fileName:''}).fileName,'download.zip');
  assert.equal(body.fileFacts(response(source,source.length,'application/octet-stream'),'ignored',{fileName:''}).fileName,'download.bin');
  assert.equal(body.fileFacts(response(source,source.length,'text/plain'),'ignored',{fileName:'../export.txt'}).fileName,'.._export.txt');
  assert.throws(()=>body.fileFacts(response(source,source.length,'application/zip'),'not media'));
});

test('lost finish acknowledgement resolves by durable receipt without a second transfer', async () => {
  const b = bridge({loseFinish:true});
  let queried = 0;
  const result = await copy(b,{queryReceipt: async id => {
    queried++;
    assert.equal(id,b.messages[0].transferId);
    return {ok:true,data:{accepted:true,offset:source.length,jobId:'recovered-job'}};
  }});
  assert.equal(result.jobId,'recovered-job');
  assert.equal(queried,1);
  assert.equal(b.messages.filter(m=>m.operation==='begin').length,1);
  assert.equal(b.messages.filter(m=>m.operation==='finish').length,1);
  assert.equal(b.messages.some(m=>m.operation==='abort'),false);
});
test('unknown finish receipt never restarts a copy', async () => {
  const b=bridge({loseFinish:true}), url='blob:https://media.test/uncertain-copy';
  const missing=async()=>({ok:false,code:'missing'});
  await assert.rejects(copy(b,{url,queryReceipt:missing}), error=>error.code==='uncertain-finish');
  const next=bridge(), fetcher=async()=>assert.fail('Uncertain copy must not fetch again');
  await assert.rejects(copy(next,{url,fetcher,queryReceipt:missing}),error=>error.code==='uncertain-finish');
  assert.equal(next.launched,false);
  const recovered=await copy(next,{url,fetcher,queryReceipt:async id=>{
    assert.equal(id,b.messages[0].transferId);
    return {ok:true,data:{accepted:true,offset:source.length,jobId:'recovered-job'}};
  }});
  assert.equal(recovered.jobId,'recovered-job');
  assert.equal(next.launched,false);
  assert.equal(b.messages.filter(m=>m.operation==='begin').length,1);
  assert.equal(b.messages.filter(m=>m.operation==='finish').length,1);
});

test('conditional same-origin media copy verifies the observed representation and keeps credentials in browser', async () => {
  const b=bridge(), url=origin+'/movie.mp4';
  const proof={url,etag:'"movie-v1"',length:source.length,mime:'video/mp4'};
  const result=await copy(b,{url,mediaProof:proof,fetcher:async (actual,options)=>{
    assert.equal(actual,url);
    assert.equal(options.headers['If-Match'],proof.etag);
    assert.equal(options.credentials,'same-origin');
    assert.equal(options.mode,'same-origin');
    assert.equal(options.redirect,'error');
    return new Response(source,{headers:{'Content-Type':'video/mp4','Content-Length':String(source.length),'ETag':proof.etag}});
  }});
  assert.equal(result.jobId,'native-job-12345');
  assert.deepEqual(Buffer.concat(b.chunks),Buffer.from(source));
  assert.ok(b.messages.every(message=>message.session===true));
  assert.equal(b.messages.some(message=>'cookie' in message||'authorization' in message||'url' in message),false);
});
test('conditional copy rejects weak validators, wrong origin, segments, changed or partial responses', async () => {
  const url=origin+'/movie.mp4', proof={url,etag:'"v1"',length:source.length,mime:'video/mp4'};
  for(const bad of [
    {...proof,etag:'W/"v1"'}, {...proof,url:'https://other.test/movie.mp4'},
    {...proof,url:origin+'/segment.m4s'}, {...proof,url:origin+'/movie.mp4?range=0-2'}
  ]) {
    let fetched=false;
    await assert.rejects(copy(bridge(),{url:bad.url,mediaProof:bad,fetcher:async()=>{fetched=true;return response();}}));
    assert.equal(fetched,false);
  }
  for(const headers of [
    {'Content-Type':'video/mp4','Content-Length':String(source.length),'ETag':'"changed"'},
    {'Content-Type':'video/webm','Content-Length':String(source.length),'ETag':'"v1"'},
    {'Content-Type':'video/mp4','Content-Length':String(source.length-1),'ETag':'"v1"'},
    {'Content-Type':'video/mp4','Content-Length':String(source.length),'ETag':'"v1"','Content-Range':'bytes 0-699999/700000'}
  ]) {
    const b=bridge();
    await assert.rejects(copy(b,{url,mediaProof:proof,fetcher:async()=>new Response(source,{headers})}));
    assert.equal(b.launched,false);
  }
});
test('cross-origin browser media is copied only with an explicit CORS proof and enforced fetch mode',async()=>{
  const url='https://cdn.test/movie.mp4';
  for(const credentials of ['omit','include']){
    const b=bridge();
    const proof={url,etag:'"movie-v1"',length:source.length,mime:'video/mp4',corsOrigin:origin,credentials};
    await copy(b,{url,mediaProof:proof,fetcher:async (actual,options)=>{
      assert.equal(actual,url);assert.equal(options.mode,'cors');
      assert.equal(options.credentials,credentials);assert.equal(options.redirect,'error');
      return new Response(source,{headers:{'Content-Type':'video/mp4','Content-Length':String(source.length),'ETag':proof.etag}});
    }});
    assert.deepEqual(Buffer.concat(b.chunks),Buffer.from(source));
  }
  const b=bridge();
  await assert.rejects(copy(b,{url,mediaProof:{url,etag:'"movie-v1"',length:source.length,mime:'video/mp4',
    corsOrigin:origin,credentials:'omit'},fetcher:async()=>new Response(source,{headers:{
      'Content-Type':'video/mp4','Content-Length':String(source.length)}})}));
  assert.equal(b.launched,false);
});
test('small stream reads are coalesced to the minimum native chunk count', async()=>{
  const b=bridge(); let offset=0;
  const stream=new ReadableStream({pull(controller){
    if(offset===source.length){controller.close();return;}
    const end=Math.min(offset+8192,source.length);
    controller.enqueue(source.subarray(offset,end));offset=end;
  }});
  await copy(b,{fetcher:async()=>new Response(stream,{headers:{'Content-Type':'video/mp4','Content-Length':String(source.length)}})});
  assert.equal(b.chunks.length,Math.ceil(source.length/body.CHUNK));
  assert.deepEqual(Buffer.concat(b.chunks),Buffer.from(source));
});
