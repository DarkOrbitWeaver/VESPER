import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const script = name => readFileSync(new URL(`../../extension/${name}.js`, import.meta.url), 'utf8');
const nextTick = () => new Promise(done => setTimeout(done, 0));
function browser(restored = {}) {
  const callbacks = {};
  const state = structuredClone(restored);
  const storage = { session: {
    get: async key => ({ [key]: structuredClone(state[key] || {}) }),
    set: async items => Object.assign(state, structuredClone(items))
  }};
  const event = key => ({ addListener: fn => { callbacks[key] = fn; } });
  const chrome = { storage,
    webRequest: { onBeforeRequest: event('before'), onBeforeRedirect: event('redirect') },
    downloads: { onCreated: event('created'), onChanged: event('changed') } };
  const context = vm.createContext({ chrome, URL, Date, Math, Set, Map, Number, String });
  vm.runInContext(script('vesper-intelligence'), context);
  vm.runInContext(script('vesper-batch-events'), context);
  const engine = context.VesperBatchEvents;
  const sender = { tab: { id: 5 }, frameId: 0, documentId: 'document-trusted', url: 'https://site.test/page' };
  const request = { batchId: 'vesper-batch000001', itemId: 'vesper-batch000001-item00',
    expectedUrl: 'https://site.test/files/part1.zip' };
  return { engine, callbacks, sender, request, state };
}
test('exact same-tab request and browser download produce a correlated event', async () => {
  const b = browser();
  assert.equal((await b.engine.handle('batch-arm', b.request, b.sender)).ok, true);
  b.callbacks.before({ tabId: 5, frameId: 0, requestId: 'a', url: b.request.expectedUrl });
  b.callbacks.created({ id: 50, url: b.request.expectedUrl, danger: 'safe', state: 'in_progress' });
  await nextTick();
  const found = await b.engine.handle('batch-status', b.request, b.sender);
  assert.equal(found.matched, true);
  assert.equal(found.downloadId, 50);
  assert.equal(found.status, 'download-observed');
  b.callbacks.changed({ id: 50, state: { current: 'complete' } });
  await nextTick();
  const completed = await b.engine.handle('batch-status', b.request, b.sender);
  assert.equal(completed.status, 'chrome-completed');
  assert.equal(completed.completed, true);
});
test('download item without exact same-tab network witness remains unattributed', async () => {
  const b = browser();
  await b.engine.handle('batch-arm', b.request, b.sender);
  b.callbacks.before({ tabId: 7, frameId: 0, requestId: 'b', url: b.request.expectedUrl });
  b.callbacks.created({ id: 51, url: b.request.expectedUrl, danger: 'safe' });
  await nextTick();
  const status = await b.engine.handle('batch-status', b.request, b.sender);
  assert.equal(status.status, 'armed');
  assert.equal(status.matched, false);
});
test('download in another frame cannot be attributed to the approved action', async () => {
  const b = browser();
  await b.engine.handle('batch-arm', b.request, b.sender);
  b.callbacks.before({ tabId: 5, frameId: 1, requestId: 'b', url: b.request.expectedUrl });
  b.callbacks.created({ id: 51, url: b.request.expectedUrl, danger: 'safe' });
  await nextTick();
  assert.equal((await b.engine.handle('batch-status', b.request, b.sender)).matched, false);
});
test('request from a replacement document in the same frame is not attributed', async () => {
  const b = browser();
  await b.engine.handle('batch-arm', b.request, b.sender);
  b.callbacks.before({ tabId: 5, frameId: 0, documentId: 'replacement-document', requestId: 'new-doc',
    url: b.request.expectedUrl });
  b.callbacks.created({ id: 55, url: b.request.expectedUrl, danger: 'safe' });
  await nextTick();
  const status = await b.engine.handle('batch-status', b.request, b.sender);
  assert.equal(status.matched, false);
  assert.equal(status.status, 'armed');
});
test('exact redirect chain can be recognized without replaying its request', async () => {
  const b = browser();
  await b.engine.handle('batch-arm', b.request, b.sender);
  b.callbacks.before({ tabId: 5, frameId: 0, requestId: 'r', url: b.request.expectedUrl });
  b.callbacks.redirect({ requestId: 'r', redirectUrl: 'https://cdn.test/file-signed.zip?sig=3' });
  b.callbacks.created({ id: 54, url: 'https://cdn.test/file-signed.zip?sig=3', danger: 'safe' });
  await nextTick();
  assert.equal((await b.engine.handle('batch-status', b.request, b.sender)).matched, true);
});
test('worker restart restores approved watcher without page permission escalation', async () => {
  const b = browser();
  await b.engine.handle('batch-arm', b.request, b.sender);
  assert.ok(b.state.vesperApprovedBatchWatchesV1[`${b.request.batchId}:${b.request.itemId}`]);
  const wrongSender = { ...b.sender, tab: { id: 999 } };
  assert.equal((await b.engine.handle('batch-status', b.request, wrongSender)).ok, false);
  assert.equal((await b.engine.handle('batch-clear', b.request, b.sender)).ok, true);
  assert.equal((await b.engine.handle('batch-status', b.request, b.sender)).ok, false);
});
test('unknown Chrome interruption never implies VESPER accepted a transfer', async () => {
  const b = browser();
  await b.engine.handle('batch-arm', b.request, b.sender);
  b.callbacks.before({ tabId: 5, frameId: 0, requestId: 'a', url: b.request.expectedUrl });
  b.callbacks.created({ id: 52, url: b.request.expectedUrl, danger: 'safe' });
  await nextTick();
  b.callbacks.changed({ id: 52, state: { current: 'interrupted' } });
  await nextTick();
  const value = await b.engine.handle('batch-status', b.request, b.sender);
  assert.equal(value.status, 'ownership-unresolved');
  assert.equal(value.completed, false);
});
test('concurrent identical download from another tab is not misattributed', async () => {
  const b = browser();
  await b.engine.handle('batch-arm', b.request, b.sender);
  b.callbacks.before({ tabId: 5, frameId: 0, requestId: 'approved', url: b.request.expectedUrl });
  b.callbacks.before({ tabId: 8, frameId: 0, requestId: 'unrelated', url: b.request.expectedUrl });
  b.callbacks.created({ id: 70, url: b.request.expectedUrl, danger: 'safe' });
  await nextTick();
  const status = await b.engine.handle('batch-status', b.request, b.sender);
  assert.equal(status.matched, false);
});
test('batch arming requires browser-stamped document identity', async () => {
  const b = browser();
  const badSender = { ...b.sender, documentId: undefined };
  assert.equal((await b.engine.handle('batch-arm', b.request, badSender)).ok, false);
});
test('incognito pages cannot arm session-scoped batch watchers', async () => {
  const b = browser();
  const sender = { ...b.sender, tab: { id: 5, incognito: true } };
  assert.equal((await b.engine.handle('batch-arm', b.request, sender)).ok, false);
});
test('duplicate same-tab requests make attribution ambiguous', async () => {
  const b = browser();
  await b.engine.handle('batch-arm', b.request, b.sender);
  b.callbacks.before({ tabId: 5, frameId: 0, requestId: 'first', url: b.request.expectedUrl });
  b.callbacks.before({ tabId: 5, frameId: 0, requestId: 'duplicate', url: b.request.expectedUrl });
  b.callbacks.created({ id: 82, url: b.request.expectedUrl, danger: 'safe' });
  await nextTick();
  const status = await b.engine.handle('batch-status', b.request, b.sender);
  assert.equal(status.matched, false);
  assert.equal(status.status, 'armed');
});
test('disarming cannot erase an observed in-flight Chrome download', async () => {
  const b = browser();
  await b.engine.handle('batch-arm', b.request, b.sender);
  b.callbacks.before({ tabId: 5, frameId: 0, requestId: 'active', url: b.request.expectedUrl });
  b.callbacks.created({ id: 83, url: b.request.expectedUrl, danger: 'safe' });
  await nextTick();
  const result = await b.engine.handle('batch-disarm', b.request, b.sender);
  assert.equal(result.retained, true);
  assert.equal((await b.engine.handle('batch-status', b.request, b.sender)).downloadId, 83);
});
test('status and clear are scoped to the originating document', async () => {
  const b = browser();
  await b.engine.handle('batch-arm', b.request, b.sender);
  const otherDocument = { ...b.sender, documentId: 'different-document' };
  assert.equal((await b.engine.handle('batch-status', b.request, otherDocument)).ok, false);
  assert.equal((await b.engine.handle('batch-clear', b.request, otherDocument)).ok, true);
  assert.equal((await b.engine.handle('batch-status', b.request, b.sender)).ok, true);
});
test('service-worker restart retains a download ID and watches its real completion', async () => {
  const original = browser();
  await original.engine.handle('batch-arm', original.request, original.sender);
  original.callbacks.before({ tabId: 5, frameId: 0, requestId: 't1', url: original.request.expectedUrl });
  original.callbacks.created({ id: 81, url: original.request.expectedUrl, danger: 'safe' });
  await nextTick();
  const restarted = browser(original.state);
  const before = await restarted.engine.handle('batch-status', restarted.request, restarted.sender);
  assert.equal(before.downloadId, 81);
  restarted.callbacks.changed({ id: 81, state: { current: 'complete' } });
  await nextTick();
  const result = await restarted.engine.handle('batch-status', restarted.request, restarted.sender);
  assert.equal(result.status, 'chrome-completed');
  assert.equal(result.completed, true);
});
test('multiple approved items retain separate download IDs and final statuses', async () => {
  const b = browser();
  const other = { ...b.request, itemId: 'vesper-batch000001-item01', expectedUrl: 'https://site.test/files/part2.zip' };
  await b.engine.handle('batch-arm', b.request, b.sender);
  b.callbacks.before({ tabId: 5, frameId: 0, requestId: 'first', url: b.request.expectedUrl });
  b.callbacks.created({ id: 91, url: b.request.expectedUrl, danger: 'safe' });
  await nextTick();
  await b.engine.handle('batch-arm', other, b.sender);
  b.callbacks.before({ tabId: 5, frameId: 0, requestId: 'second', url: other.expectedUrl });
  b.callbacks.created({ id: 92, url: other.expectedUrl, danger: 'safe' });
  await nextTick();
  b.callbacks.changed({ id: 91, state: { current: 'complete' } });
  await nextTick();
  const s1 = await b.engine.handle('batch-status', b.request, b.sender);
  const s2 = await b.engine.handle('batch-status', other, b.sender);
  assert.equal(s1.downloadId, 91);
  assert.equal(s1.completed, true);
  assert.equal(s2.downloadId, 92);
  assert.equal(s2.completed, false);
});

test('native completion requires matching download identity and cannot replace Chrome completion', async () => {
  const b = browser(), startTime = '2026-11-24T11:00:00Z';
  await b.engine.handle('batch-arm', b.request, b.sender);
  b.callbacks.before({tabId:5,frameId:0,requestId:'proof',url:b.request.expectedUrl});
  b.callbacks.created({id:90,startTime,url:b.request.expectedUrl,danger:'safe'});
  await nextTick();
  assert.equal(await b.engine.noteNativeCompletion(90,'wrong'),false);
  assert.equal(await b.engine.noteNativeCompletion(90,startTime),true);
  b.callbacks.changed({id:90,state:{current:'interrupted'}});
  await nextTick();
  assert.equal((await b.engine.handle('batch-status',b.request,b.sender)).nativeCompleted,true);
  b.callbacks.changed({id:90,state:{current:'complete'}});
  await nextTick();
  const result=await b.engine.handle('batch-status',b.request,b.sender);
  assert.equal(result.completed,true);
  assert.equal(result.nativeCompleted,false);
  assert.equal(await b.engine.noteNativeCompletion(90,startTime),false);
});
test('a pending batch action cannot be overwritten or followed by another armed action', async () => {
  const b=browser();
  assert.equal((await b.engine.handle('batch-arm',b.request,b.sender)).ok,true);
  assert.equal((await b.engine.handle('batch-arm',b.request,b.sender)).ok,false);
  assert.equal((await b.engine.handle('batch-arm',{...b.request,itemId:'vesper-batch000001-item02'},b.sender)).ok,false);
});

test('a request arriving before worker restore completes is still witnessed', async () => {
  const original=browser();
  await original.engine.handle('batch-arm',original.request,original.sender);
  const restarted=browser(original.state);
  restarted.callbacks.before({tabId:5,frameId:0,documentId:original.sender.documentId,
    requestId:'after-wake',url:original.request.expectedUrl});
  restarted.callbacks.created({id:88,url:original.request.expectedUrl,danger:'safe',state:'in_progress'});
  await nextTick();
  assert.equal((await restarted.engine.handle('batch-status',original.request,original.sender)).matched,true);
});
test('same URL ambiguity is retained through worker restart instead of becoming a match', async () => {
  const original=browser();
  await original.engine.handle('batch-arm',original.request,original.sender);
  original.callbacks.before({tabId:5,frameId:0,requestId:'approved',url:original.request.expectedUrl});
  original.callbacks.before({tabId:6,frameId:0,requestId:'competing',url:original.request.expectedUrl});
  await nextTick();
  const restarted=browser(original.state);
  restarted.callbacks.created({id:89,url:original.request.expectedUrl,danger:'safe',state:'complete'});
  await nextTick();
  assert.equal((await restarted.engine.handle('batch-status',original.request,original.sender)).matched,false);
});
test('already completed browser downloads are reported as Chrome-owned immediately',async()=>{
  const b=browser();
  await b.engine.handle('batch-arm',b.request,b.sender);
  b.callbacks.before({tabId:5,frameId:0,requestId:'fast',url:b.request.expectedUrl});
  b.callbacks.created({id:91,url:b.request.expectedUrl,danger:'safe',state:'complete'});
  await nextTick();
  const result=await b.engine.handle('batch-status',b.request,b.sender);
  assert.equal(result.status,'chrome-completed');
  assert.equal(result.completed,true);
});


test('unresolved batch journal survives worker restart and blocks another batch in the same document', async () => {
  const b = browser();
  const record = {id:'vesper-batch000001',steps:[{index:0,key:'id-ab12',state:'action-recorded',detail:'private text'}]};
  assert.equal((await b.engine.handle('batch-journal-write',{record},b.sender)).ok,true);
  assert.equal(JSON.stringify(b.state).includes('private text'),false);
  const restarted=browser(b.state);
  assert.equal((await restarted.engine.handle('batch-journal-read',{},b.sender)).record.id,record.id);
  assert.equal((await restarted.engine.handle('batch-journal-write',{record:{...record,id:'vesper-batch000002'}},b.sender)).ok,false);
  const other={...b.sender,documentId:'document-other'};
  assert.equal((await restarted.engine.handle('batch-journal-read',{},other)).record,null);
  assert.equal((await restarted.engine.handle('batch-journal-ack',{id:record.id},other)).ok,false);
  assert.equal((await restarted.engine.handle('batch-journal-ack',{id:record.id},b.sender)).ok,true);
  assert.equal((await restarted.engine.handle('batch-journal-write',{record:{...record,id:'vesper-batch000002'}},b.sender)).ok,true);
});

test('journal rejects stale or contradictory writes for an in-flight batch',async()=>{
  const b=browser(),id='vesper-batch000001';
  const write=(state,key='id-a')=>b.engine.handle('batch-journal-write',{record:{id,steps:[{index:0,key,state}]}},b.sender);
  assert.equal((await write('approved')).ok,true);
  assert.equal((await write('activating')).ok,true);
  assert.equal((await write('approved')).ok,false);
  assert.equal((await write('waiting','id-b')).ok,false);
  assert.equal((await b.engine.handle('batch-journal-write',{record:{id:'vesper-batch000002',steps:[{index:0,key:'id-a',state:'approved'}]}},b.sender)).ok,false);
});
test('a request timestamp before arm cannot prove a new batch action',async()=>{
  const b=browser();
  await b.engine.handle('batch-arm',b.request,b.sender);
  b.callbacks.before({tabId:5,frameId:0,requestId:'old',url:b.request.expectedUrl,timeStamp:Date.now()-10000});
  b.callbacks.created({id:99,url:b.request.expectedUrl,danger:'safe'});
  await nextTick();
  assert.equal((await b.engine.handle('batch-status',b.request,b.sender)).matched,false);
});
