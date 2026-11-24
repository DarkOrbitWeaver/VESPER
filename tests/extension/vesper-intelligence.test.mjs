import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const intelCode = readFileSync(new URL('../../extension/vesper-intelligence.js', import.meta.url), 'utf8');
function load() {
  const context = vm.createContext({ URL, Date, Math, Map, Set, Number, String, RegExp, Error });
  vm.runInContext(readFileSync(new URL('../../extension/vesper-control-evidence.js', import.meta.url), 'utf8'), context);
  vm.runInContext(intelCode, context);
  return context.VesperIntelligence;
}
const brain = load();
const eq = (actual, expected) => assert.equal(actual, expected);
const item = i => ({ key: `ep-${i}`, label: `Episode ${i}` });
const action = { label: 'Download', key: 'BUTTON|||download' };
const plan = (n = 10) => brain.makeBatchPlan({ items: Array.from({ length: n }, (_, i) => item(i + 1)),
  action, origin: 'https://example.test/episode', id: 'vesper-safebatch12345' });
function store() {
  const map = new Map();
  return { map, load: async id => map.get(id) || null, save: async value => map.set(value.id, structuredClone(value)) };
}
function executor(pl, adapter, journal = store()) { return brain.createBatchExecutor({ plan: pl, adapter, journal }); }
function normalAdapter(overrides = {}) {
  const clicked = [];
  return { clicked,
    activate: async step => ({ exact: true, item: step.key }),
    waitReady: async step => ({ verified: true, itemKey: step.key, actionKey: action.key }),
    trigger: async step => { clicked.push(step.key); },
    reconcile: async () => ({ effect: 'started-confirmed', verified: true }),
    ...overrides
  };
}
test('URL scope rejects credentials, scripts, and fragments', () => {
  eq(brain.http('javascript:alert(1)'), null);
  eq(brain.http('https://joe:pass@site.test/'), null);
  eq(brain.http('blob:https://site.test/id'), null);
  eq(brain.http('https://site.test/a#secret').href, 'https://site.test/a');
  eq(brain.http('//site.test/file', 'https://example.test').host, 'site.test');
});
test('source classifiers distinguish playlists, media and fragments', () => {
  eq(brain.sourceKind('https://cdn.test/playlist.m3u8?key=1'), 'manifest');
  eq(brain.sourceKind('https://cdn.test/manifest', 'application/dash+xml'), 'manifest');
  eq(brain.sourceKind('https://cdn.test/file.mp4'), 'direct');
  eq(brain.sourceKind('https://cdn.test/part.m4s'), 'fragment');
  eq(brain.sourceKind('https://cdn.test/video.mp4?segment=4'), 'fragment');
});
test('semantic scorer recognizes multiple download languages but cannot authorize', () => {
  for (const label of ['Download', 'Télécharger', 'Descargar', 'Herunterladen', 'تحميل', 'Export']) {
    const result = brain.proposeControl({ tagName: 'BUTTON', label });
    eq(result.classification, 'download-likely');
    eq(result.mayExecute, false);
  }
  eq(brain.proposeControl({ label: 'Play', tagName: 'BUTTON' }).classification, 'navigation-likely');
  eq(brain.proposeControl({ label: 'Watch PDF', isInlinePdf: true }).classification, 'navigation-likely');
});
test('predictions never become authorization by themselves', () => {
  const model = brain.proposeControl({ label: 'DOWNLOAD NOW', downloadAttribute: true });
  eq(brain.operationDecision({ model }), 'observe-only');
  eq(brain.operationDecision({ model, inlinePdf: true }), 'navigation');
  eq(brain.operationDecision({ model, chromeCreated: true }), 'chrome-download');
  eq(brain.operationDecision({ model, approval: { explicit: true, scoped: true } }), 'approved');
});
test('frame identity requires browser-stamped document id', () => {
  eq(brain.safeScope({ tabId: 2, frameId: 0, documentId: 'document-long-enough' }), '2:0:document-long-enough');
  eq(brain.safeScope({ tabId: 2, frameId: 0 }), null);
  eq(brain.safeScope({ tabId: -1, frameId: 0, documentId: 'document-long-enough' }), null);
});
test('media ledger rolls epochs for reused players, not resizes', () => {
  let now = 1000000;
  const graph = brain.createMediaLedger({ now: () => now });
  const a = graph.playback({ key: 'player', scope: 'site', pageUrl: 'https://site.test/a',
    mediaUrl: 'https://cdn.test/video.mp4', stableId: 'post1' });
  const same = graph.playback({ key: 'player', scope: 'site', pageUrl: 'https://site.test/a',
    mediaUrl: 'https://cdn.test/video.mp4', stableId: 'post1', event: 'durationchange', duration: 90 });
  eq(same.epoch, a.epoch);
  const next = graph.playback({ key: 'player', scope: 'site', pageUrl: 'https://site.test/a',
    mediaUrl: 'https://cdn.test/video.mp4', stableId: 'post2' });
  assert.ok(next.epoch > same.epoch);
});
test('exact-source evidence outranks plausible timing, requires independent verification', () => {
  let now = 2000000;
  const graph = brain.createMediaLedger({ now: () => now });
  graph.playback({ key: 'a', scope: 'frameA', pageUrl: 'https://site.test/a',
    mediaUrl: 'https://cdn.test/main.mp4' });
  graph.record({ scope: 'frameA', url: 'https://cdn.test/ad.m3u8', at: now - 1000 });
  graph.record({ scope: 'frameA', url: 'https://cdn.test/main.mp4', at: now - 20000 });
  graph.record({ scope: 'frameB', url: 'https://cdn.test/foreign.mp4' });
  const results = graph.matches('a');
  eq(results.length, 2);
  eq(results[0].exact, true);
  eq(results[0].verified, false);
  eq(results[0].needsIndependentCheck, true);
});
test('segment evidence does not pretend to be a full movie', () => {
  const graph = brain.createMediaLedger();
  eq(graph.record({ scope: 'frame', url: 'https://cdn.test/chunk.m4s' }), false);
  eq(graph.snapshot().evidence, 0);
});
test('media graph is bounded, clears on navigation, and expires', () => {
  let now = 2_000_000;
  const graph = brain.createMediaLedger({ now: () => now, maxPlayers: 3, maxEvidence: 4 });
  for (let i = 0; i < 20; i++) {
    graph.playback({ key: `p${i}`, scope: 'a', pageUrl: `https://site.test/${i}` });
    graph.record({ scope: 'a', url: `https://cdn.test/${i}.mp4` });
  }
  eq(graph.snapshot().players, 3);
  eq(graph.snapshot().evidence, 4);
  graph.forget('a');
  eq(graph.snapshot().players, 0);
  eq(graph.snapshot().evidence, 0);
  graph.record({ scope: 'b', url: 'https://cdn.test/valid.mp4' });
  now += 60 * 60_000 + 1;
  graph.playback({ key: 'new', scope: 'c', pageUrl: 'https://site.test/' });
  eq(graph.snapshot().evidence, 0);
});
test('media evidence from a previous reused-player epoch is not matched again', () => {
  let now = 3_000_000;
  const graph = brain.createMediaLedger({ now: () => now });
  graph.playback({ key: 'player', scope: 'frame', pageUrl: 'https://site.test/watch/1',
    mediaUrl: 'https://cdn.test/episode-1.m3u8', stableId: 'episode-1' });
  graph.record({ scope: 'frame', url: 'https://cdn.test/episode-1.m3u8', playerKey: 'player', at: now });
  now += 30_000;
  graph.playback({ key: 'player', scope: 'frame', pageUrl: 'https://site.test/watch/2',
    mediaUrl: 'https://cdn.test/episode-2.m3u8', stableId: 'episode-2' });
  eq(graph.matches('player').length, 0);
  graph.record({ scope: 'frame', url: 'https://cdn.test/episode-2.m3u8', playerKey: 'player', at: now });
  eq(graph.matches('player')[0].url, 'https://cdn.test/episode-2.m3u8');
});
test('batch approval refuses duplicate item identities and invalid origins', () => {
  assert.throws(() => brain.makeBatchPlan({ items: [item(1), item(1)], action, origin: 'https://x.test', id: 'vesper-repeat012' }));
  assert.throws(() => brain.makeBatchPlan({ items: [item(1)], action, origin: 'file:///tmp/x', id: 'vesper-repeat012' }));
  assert.throws(() => plan(51));
});
test('ten shared-button items are initiated exactly once with explicit per-item proof', async () => {
  const a = normalAdapter();
  const result = await executor(plan(), a).run();
  eq(a.clicked.length, 10);
  eq(new Set(a.clicked).size, 10);
  eq(result.filter(r => r.state === 'submitted').length, 10);
});
test('lag and reused button block the stale item and all later clicks', async () => {
  const a = normalAdapter({ waitReady: async step => ({ verified: true,
    itemKey: step.index === 2 ? 'previous-episode' : step.key, actionKey: action.key }) });
  const result = await executor(plan(), a).run();
  eq(a.clicked.length, 2);
  eq(result[2].state, 'needs-input');
  eq(result[3].state, 'cancelled');
});
test('wrong shared-button fingerprint cannot authorize an action', async () => {
  const a = normalAdapter({ waitReady: async step => ({ verified: true, itemKey: step.key, actionKey: 'different' }) });
  const result = await executor(plan(1), a).run();
  eq(a.clicked.length, 0);
  eq(result[0].state, 'needs-input');
});
test('unknown completion halts rather than clicking the next item', async () => {
  const a = normalAdapter({ reconcile: async () => ({ detail: 'No correlated download' }) });
  const result = await executor(plan(), a).run();
  eq(a.clicked.length, 1);
  eq(result[0].state, 'indeterminate');
  eq(result[1].state, 'cancelled');
});
test('a failed trigger cannot automatically repeat its side effect', async () => {
  const a = normalAdapter({ trigger: async step => { a.clicked.push(step.key); throw new Error('lost response'); } });
  const result = await executor(plan(), a).run();
  eq(a.clicked.length, 1);
  eq(result[0].state, 'indeterminate');
});
test('a crash after action-recorded resumes as indeterminate; never re-clicks', async () => {
  const j = store(), p = plan(2);
  await j.save({ id: p.id, origin: p.origin, at: 100, steps: [
    { index: 0, key: p.steps[0].key, state: 'action-recorded', at: 100 },
    { index: 1, key: p.steps[1].key, state: 'approved', at: 100 }
  ] });
  const a = normalAdapter();
  const result = await executor(p, a, j).run();
  eq(a.clicked.length, 0);
  eq(result[0].state, 'indeterminate');
  eq(result[1].state, 'cancelled');
});
test('cancellation after one item preserves the running item and stops remaining', async () => {
  const a = normalAdapter();
  let machine;
  a.reconcile = async () => { machine.cancel(); return { effect: 'started-confirmed', verified: true }; };
  machine = executor(plan(), a);
  const result = await machine.run();
  eq(a.clicked.length, 1);
  eq(result[0].state, 'submitted');
  eq(result[1].state, 'cancelled');
});
test('cancellation during readiness disarms its watcher and never clicks', async () => {
  const a = normalAdapter();
  let machine, disarmed = 0;
  a.waitReady = async step => {
    machine.cancel();
    return { verified: true, itemKey: step.key, actionKey: action.key };
  };
  a.disarm = async () => { disarmed++; };
  machine = executor(plan(2), a);
  const result = await machine.run();
  eq(a.clicked.length, 0);
  eq(disarmed, 1);
  eq(result[0].state, 'cancelled');
  eq(result[1].state, 'cancelled');
});
test('a saved cancelled step remains cancelled after restart', async () => {
  const j = store(), p = plan(2);
  await j.save({ id: p.id, origin: p.origin, at: 100, steps: p.steps.map(step => ({
    index: step.index, key: step.key, state: 'cancelled', at: 100, detail: 'Stopped by user'
  })) });
  const a = normalAdapter();
  const result = await executor(p, a, j).run();
  eq(a.clicked.length, 0);
  eq(result[0].state, 'cancelled');
  eq(result[1].state, 'cancelled');
});
test('restart after cancellation does not continue later approved items', async () => {
  const j = store(), p = plan(2);
  await j.save({ id: p.id, origin: p.origin, at: 100, steps: [
    { index: 0, key: p.steps[0].key, state: 'cancelled', at: 100, detail: 'Stopped by user' },
    { index: 1, key: p.steps[1].key, state: 'approved', at: 100, detail: '' }
  ] });
  const a = normalAdapter();
  const result = await executor(p, a, j).run();
  eq(a.clicked.length, 0);
  eq(result[0].state, 'cancelled');
  eq(result[1].state, 'cancelled');
});
test('browser fallback counts as interception miss but truthful download completion', async () => {
  const a = normalAdapter({ reconcile: async () => ({ ownership: 'chrome', verified: true }) });
  const result = await executor(plan(1), a).run();
  eq(result[0].state, 'verified-chrome');
});
test('native completion requires independent verification', async () => {
  const a = normalAdapter({ reconcile: async () => ({ ownership: 'native', verified: false }) });
  const result = await executor(plan(1), a).run();
  eq(result[0].state, 'indeterminate');
});
test('never replay one-use GET or POST because ETag/size happens to exist', () => {
  eq(brain.chooseAcquisition({ approved: true, isOneUse: true, nativeReplayVerified: true }).tier, 'chrome');
  eq(brain.chooseAcquisition({ approved: true, isPost: true, nativeReplayVerified: true }).tier, 'chrome');
  eq(brain.chooseAcquisition({ approved: true, isBlob: true, nativeReplayVerified: true }).tier, 'chrome');
  eq(brain.chooseAcquisition({ approved: true, nativeReplayVerified: true }).tier, 'native');
});
test('browser already receiving bytes prevents unproven takeover', () => {
  eq(brain.chooseAcquisition({ approved: true, nativeReplayVerified: true, chromeHasBytes: true }).tier, 'chrome');
  eq(brain.chooseAcquisition({ approved: true, isDrm: true, originalBytesProven: true }).tier, 'unsupported');
  eq(brain.chooseAcquisition({ approved: true, originalBytesProven: true }).tier, 'original-stream');
});

test('same-URL loadstart invalidates previous bound resource evidence', () => {
  let now = 100000;
  const graph = brain.createMediaLedger({ now: () => now });
  const player = { key: 'p', scope: 'f', pageUrl: 'https://example.test/watch', mediaUrl: 'blob:https://example.test/a' };
  const first = graph.playback(player);
  graph.record({ scope: 'f', playerKey: 'p', url: 'https://cdn.test/old.m3u8' });
  now += 1000;
  const next = graph.playback({ ...player, event: 'loadstart' });
  assert.ok(next.epoch > first.epoch);
  eq(graph.matches('p').length, 0);
  graph.record({ scope: 'f', playerKey: 'p', url: 'https://cdn.test/old.m3u8', at: now - 1000 });
  eq(graph.matches('p').length, 0);
  graph.record({ scope: 'f', playerKey: 'p', url: 'https://cdn.test/new.m3u8' });
  eq(graph.matches('p')[0].url, 'https://cdn.test/new.m3u8');
});
test('duplicate resource observations do not evict useful manifest evidence', () => {
  const graph = brain.createMediaLedger({ maxEvidence: 4 });
  graph.playback({ key: 'p', scope: 'f', pageUrl: 'https://example.test/watch' });
  graph.record({ scope: 'f', url: 'https://cdn.test/master.m3u8' });
  for (let i = 0; i < 30; i++) graph.record({ scope: 'f', url: 'https://cdn.test/movie.mp4' });
  eq(graph.snapshot().evidence, 2);
});
test('empty relative URL is not the current page', () => {
  eq(brain.http('', 'https://example.test/watch'), null);
});

test('buffered exact media can bind on first discovery but never to a reused player epoch',()=>{
  let now=100000;
  const graph=brain.createMediaLedger({now:()=>now});
  const player={key:'p',scope:'f',pageUrl:'https://site.test/watch',mediaUrl:'https://cdn.test/movie.mp4'};
  graph.playback(player);
  graph.record({scope:'f',playerKey:'p',url:player.mediaUrl,at:now-5000,type:'resource'});
  eq(graph.matches('p').length,1);
  now+=1000;
  graph.playback({...player,event:'loadstart'});
  graph.record({scope:'f',playerKey:'p',url:player.mediaUrl,at:now-5000,type:'resource'});
  eq(graph.matches('p').length,0);
});
