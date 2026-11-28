import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';

const context = vm.createContext({ URL });
for (const name of ['vesper-evidence-graph.js', 'vesper-planner.js'])
  vm.runInContext(readFileSync(new URL(`../../extension/${name}`, import.meta.url), 'utf8'), context);
const { VesperEvidenceGraph: G, VesperPlanner: P } = context;
const knowledge = JSON.parse(readFileSync(new URL('../../tools/intelligence/adapter-knowledge.json', import.meta.url), 'utf8'));
const scope = '7:0:document-abcdef';

function graphAt(start = 1_000_000) {
  let now = start;
  return { graph: G.create({ now: () => now }), advance: ms => { now += ms; return now; }, at: () => now };
}

test('acquisition verdict never upgrades a fragment or contradictory current generation', () => {
  const url='https://cdn.example/clip.mp4', args={scope,playerKey:'v',generation:1,sourceUrl:url};
  const candidate={url,kind:'direct',direct:true,witnessed:true,verified:true,compatible:true};
  const partial=G.create().assessPicker({...args,candidates:[{...candidate,kind:'fragment'}]});
  assert.equal(partial.verdict,'abstain');
  assert.equal(partial.rows[0].recoverability,'partial-only');
  assert.equal(partial.rows[0].compatible,false);
  const conflict=G.create();
  conflict.observePlayer({...args,sourceUrl:'https://cdn.example/other.mp4'});
  assert.equal(conflict.assessPicker({...args,candidates:[candidate]}).verdict,'abstain');
  assert.equal(G.create().assessPicker({...args,candidates:[{...candidate,conflictingValidator:true}]}).verdict,'abstain');
});

test('typed acquisition graph separates recoverability from playback identity', () => {
  const url = 'https://cdn.example/active.mp4';
  const candidate = {url,kind:'direct',direct:true,witnessed:true,verified:true,compatible:true};
  const args = {scope,playerKey:'v',generation:1,sourceUrl:url};
  const exact = G.create().assessPicker({...args,candidates:[candidate]});
  assert.equal(exact.verdict,'exact');
  assert.equal(exact.selected,0);
  const worker = G.create().assessPicker({...args,worker:true,candidates:[candidate]});
  assert.equal(worker.verdict,'confirm');
  assert.equal(worker.selected,null);
  assert.equal(worker.rows[0].witnessed,false);
  const incompatible = G.create().assessPicker({...args,candidates:[{...candidate,compatible:false}]});
  assert.equal(incompatible.verdict,'abstain');
  const duplicate = G.create().assessPicker({...args,candidates:[candidate,candidate]});
  assert.equal(duplicate.verdict,'confirm');
  assert.equal(duplicate.selected,null);
  const overflow = G.create({limits:{nodes:4,edges:4}}).assessPicker({...args,candidates:[candidate]});
  assert.equal(overflow.verdict,'abstain');
  assert.equal(G.create().assessPicker({...args,candidates:[candidate,{...candidate,url:'bad'}]}).verdict,'abstain');
});

test('an expired signed source cannot survive the graph acquisition verdict', () => {
  const { graph, advance } = graphAt(1700000000000);
  const url = 'https://cdn.example/clip.mp4?expires=1700000001';
  const args = {scope,playerKey:'v',generation:1,sourceUrl:url,
    candidates:[{url,kind:'direct',direct:true,verified:true,compatible:true}]};
  assert.equal(graph.assessPicker(args).verdict,'exact');
  advance(2000);
  assert.equal(graph.assessPicker(args).verdict,'abstain');
});


test('requests bind only to the player generation that was current when they happened', () => {
  const { graph, advance, at } = graphAt();
  graph.observePlayer({ scope, playerKey: 'v1', generation: 1, at: at() });
  graph.observeRequest({ scope, url: 'https://cdn.example/a/master.m3u8', kind: 'hls', playerKey: 'v1', generation: 1, at: advance(100) });
  // The same element is reused for a different program (SPA navigation, feed recycling).
  graph.observePlayer({ scope, playerKey: 'v1', generation: 2, at: advance(5000) });
  graph.observeRequest({ scope, url: 'https://cdn.example/b/master.m3u8', kind: 'hls', playerKey: 'v1', generation: 2, at: advance(100) });
  const { generation, candidates } = graph.associate({ scope, playerKey: 'v1' });
  assert.equal(generation, 2);
  const old = candidates.find(c => c.url.includes('/a/')), fresh = candidates.find(c => c.url.includes('/b/'));
  assert.equal(old.stale, true); assert.equal(fresh.stale, false); assert.equal(fresh.currentGeneration, true);
});

test('a request that predates a generation cannot bind to it', () => {
  const { graph, at } = graphAt();
  graph.observePlayer({ scope, playerKey: 'v', generation: 3, at: at() });
  graph.observeRequest({ scope, url: 'https://cdn.example/early.mp4', kind: 'direct', playerKey: 'v', generation: 3, at: at() - 10 });
  assert.equal(graph.associate({ scope, playerKey: 'v' }).candidates.length, 0);
});

test('two players on the same CDN keep separate programs', () => {
  const { graph, advance, at } = graphAt();
  graph.observePlayer({ scope, playerKey: 'left', generation: 1, sourceUrl: 'https://cdn.example/v/1080/clip.mp4', at: at() });
  graph.observePlayer({ scope, playerKey: 'right', generation: 1, sourceUrl: 'https://cdn.example/v/1080/other.mp4', at: advance(1) });
  const left = [...graph.associate({ scope, playerKey: 'left' }).candidates.map(c => c.url)];
  const right = [...graph.associate({ scope, playerKey: 'right' }).candidates.map(c => c.url)];
  assert.deepEqual(left, ['https://cdn.example/v/1080/clip.mp4']);
  assert.deepEqual(right, ['https://cdn.example/v/1080/other.mp4']);
});

test('shared directories do not establish manifest membership; markup sources stay hypothesized', () => {
  const { graph, advance, at } = graphAt();
  graph.observePlayer({ scope, playerKey: 'v', generation: 1, at: at() });
  graph.observeRequest({ scope, url: 'https://cdn.example/show/hls/master.m3u8', kind: 'hls', playerKey: 'v', generation: 1, at: advance(5) });
  graph.observeRequest({ scope, url: 'https://cdn.example/show/hls/720/seg1.ts', kind: 'fragment', at: advance(5) });
  graph.observeDeclared({ scope, url: 'https://cdn.example/show/preview.mp4' });
  const [manifest] = graph.associate({ scope, playerKey: 'v' }).candidates;
  assert.equal(manifest.manifestParent, false);
  assert.equal(manifest.provenance, 'associated');
});

test('graph memory is bounded under long sessions and forgetting a document removes its evidence', () => {
  const { graph, advance } = graphAt();
  for (let i = 0; i < 5000; i++) {
    graph.observePlayer({ scope: 's' + (i % 40), playerKey: 'p' + i, generation: 1, at: advance(1) });
    graph.observeRequest({ scope: 's' + (i % 40), url: `https://cdn.example/${i}.mp4`, kind: 'direct', playerKey: 'p' + i, generation: 1, at: advance(1) });
  }
  const snap = graph.snapshot();
  assert.ok(snap.nodes <= G.LIMITS.nodes && snap.edges <= G.LIMITS.edges, JSON.stringify(snap));
  assert.ok(snap.evicted > 0);
  graph.forget('s1');
  assert.equal(graph.associate({ scope: 's1', playerKey: 'p4961' }).candidates.length, 0);
});

test('signed URL expiry is interpreted from distilled adapter vocabulary', () => {
  for (const name of knowledge.expiryParams) assert.ok(G.EXPIRY_PARAMS.includes(name), name);
  assert.equal(G.expiresAt('https://cdn.example/v.mp4?expire=1900000000&sig=x'), 1900000000 * 1000);
  assert.equal(G.expiresAt('https://cdn.example/v.mp4?hdnts=st=1~exp=1800000000~acl=*'), 1800000000 * 1000);
  assert.equal(G.expiresAt('https://s3.example/v.mp4?X-Amz-Date=20260101T000000Z&X-Amz-Expires=600'), Date.UTC(2026, 0, 1) + 600_000);
  assert.equal(G.expiresAt('https://cdn.example/v.mp4'), null);
});

test('resources are interchangeable only with strong validators; names, hosts and resolution are not proof', () => {
  assert.equal(G.interchangeable({ etag: '"a"', length: 10 }, { etag: '"a"', length: 10 }).verdict, 'verify');
  const same = { url: 'https://cdn.example/a?token=x', etag: '"a"', length: 10 };
  assert.equal(G.interchangeable(same, { ...same }).verdict, 'exact');
  assert.equal(G.interchangeable(same, { ...same, url: 'https://cdn.example/a?token=y' }).verdict, 'verify');
  assert.equal(G.interchangeable(same, { ...same, url: 'https://other.example/a?token=x' }).verdict, 'verify');
  assert.equal(G.interchangeable({ ...same, etag: 'unquoted' }, { ...same, etag: 'unquoted' }).verdict, 'verify');
  assert.equal(G.interchangeable({ etag: '"a"', length: 10 }, { etag: '"b"', length: 10 }).verdict, 'incompatible');
  assert.equal(G.interchangeable({ length: 10 }, { length: 11 }).verdict, 'incompatible');
  assert.equal(G.interchangeable({ etag: 'W/"a"', length: 10 }, { etag: 'W/"a"', length: 10 }).verdict, 'verify');
  assert.equal(G.interchangeable({ programId: 'x', formatId: '720' }, { programId: 'x', formatId: '1080' }).verdict, 'incompatible');
  assert.equal(G.interchangeable({ url: 'https://cdn/a/clip.mp4' }, { url: 'https://cdn/b/clip.mp4' }).verdict, 'verify');
});

const verified = (kind, extra = {}) => ({ key: kind + '-src', kind, provenance: 'observed', verified: true, ...extra });
const explicit = { explicit: true, scoped: true, wants: 'media' };

test('planner routes verified sources to the matching native engine path', () => {
  assert.equal(P.plan({ intent: explicit, sources: [verified('hls')] }).next.route, 'native-hls');
  assert.equal(P.plan({ intent: explicit, sources: [verified('dash')] }).next.route, 'native-dash');
  assert.equal(P.plan({ intent: explicit, sources: [verified('adaptive')] }).next.route, 'native-adaptive-merge');
  assert.equal(P.plan({ intent: explicit, sources: [verified('direct', { acceptsRanges: true, length: 1e9 })] }).next.route, 'native-segmented');
  assert.equal(P.plan({ intent: explicit, sources: [verified('direct')] }).next.route, 'native-http');
});

test('one-use, POST and blob sources never replay natively', () => {
  for (const flag of ['oneUse', 'post', 'blob']) {
    assert.equal(P.plan({ intent: explicit, sources: [verified('direct', { [flag]: true, originalBytesProven: true })], capabilities: { browserBody: true } }).next.route, 'browser-body');
    const noBody = P.plan({ intent: explicit, sources: [verified('direct', { [flag]: true })], capabilities: { chromeDownload: { exists: true } } });
    assert.equal(noBody.next.route, 'chrome-keep');
  }
});

test('protected, ambiguous and unauthorized situations never produce an acquisition step', () => {
  assert.equal(P.plan({ intent: explicit, sources: [verified('dash', { drm: true })] }).status, 'unsupported');
  const ambiguous = P.plan({ intent: explicit, selection: { ambiguous: true }, sources: [verified('hls')] });
  assert.equal(ambiguous.status, 'needs-user'); assert.equal(ambiguous.next.route, 'resolve-selection');
  const passive = P.plan({ intent: { explicit: false }, sources: [verified('hls')], capabilities: { adapter: true } });
  assert.equal(passive.status, 'observe'); assert.notEqual(P.ROUTES[passive.next?.route]?.role, 'acquire');
});

test('Chrome keeps a download it is already receiving', () => {
  const step = P.plan({ intent: explicit, sources: [verified('direct')], capabilities: { chromeDownload: { exists: true, receivingBytes: true } } });
  assert.equal(step.next.route, 'chrome-keep'); assert.equal(step.reason, 'chrome-owns-bytes');
});

test('expired sources refresh once, then stop without appending from an unverified source', () => {
  const expired = verified('hls', { expiresAt: 1 });
  const first = P.plan({ intent: explicit, sources: [expired] }, 10);
  assert.equal(first.next.route, 'refresh-source');
  const after = P.plan({ intent: explicit, sources: [expired], attempts: [{ route: 'refresh-source', outcome: 'failed' }] }, 10);
  assert.notEqual(after.next?.route, 'refresh-source');
  const partial = P.plan({ intent: explicit, sources: [verified('direct')], partial: { exists: true, identityVerdict: 'incompatible' } });
  assert.equal(partial.status, 'needs-user'); assert.equal(partial.reason, 'replacement-identity-differs');
});

test('evidence routes follow experience and stop when exhausted or over budget', () => {
  const observed = [{ key: 'm', kind: 'hls', provenance: 'associated', verified: false }];
  assert.equal(P.plan({ intent: explicit, sources: observed, capabilities: { adapter: true, generic: true }, experience: { adapterPromising: true } }).next.route, 'adapter-lookup');
  assert.equal(P.plan({ intent: explicit, sources: observed, capabilities: { adapter: true, generic: true }, experience: { adapterPromising: false } }).next.route, 'verify-observed');
  const exhausted = P.plan({ intent: explicit, sources: observed, capabilities: { adapter: true, generic: true },
    attempts: ['adapter-lookup', 'verify-observed', 'generic-discovery'].map(route => ({ route, outcome: 'failed' })) });
  assert.equal(exhausted.status, 'stopped');
  const budget = P.plan({ intent: explicit, attempts: Array.from({ length: 6 }, (_, i) => ({ route: 'native-http', key: 's' + i, outcome: 'failed' })) });
  assert.equal(budget.reason, 'attempt-budget');
  assert.equal(P.plan({ intent: explicit, attempts: [{ route: 'native-hls', outcome: 'refused' }] }).status, 'stopped');
  assert.equal(P.plan({ intent: explicit, attempts: [{ route: 'native-hls', outcome: 'succeeded' }] }).status, 'done');
});

test('a plan run revises after evidence changes and falls back to the next verified route', () => {
  const run = P.createRun({ intent: explicit, sources: [], capabilities: { adapter: true } }, { now: () => 0 });
  assert.equal(run.next().next.route, 'adapter-lookup');
  run.report({ route: 'adapter-lookup', outcome: 'succeeded', sources: [verified('dash'), verified('hls')] });
  const act = run.next();
  assert.equal(act.next.route, 'native-dash');
  run.report({ route: 'native-dash', key: 'dash-src', outcome: 'failed' });
  assert.equal(run.next().next.route, 'native-hls');
  assert.throws(() => run.report({ route: 'teleport', outcome: 'x' }));
});

test('ownership route preserves the consolidated chooseAcquisition contract', () => {
  assert.equal(P.ownershipRoute({ approved: false }).tier, 'chrome');
  assert.equal(P.ownershipRoute({ approved: true, nativeReplayVerified: true }).route, 'native-http');
  assert.equal(P.ownershipRoute({ approved: true, chromeHasBytes: true, nativeReplayVerified: true }).route, 'chrome-keep');
});

test('prefetch governor enforces cooldowns, concurrency, Retry-After and escalating refusal backoff', () => {
  let now = 0;
  const g = P.createPrefetchGovernor({ now: () => now });
  const first = g.admit({ tab: 1, site: 'a.example' });
  assert.equal(first.allow, true);
  assert.equal(g.admit({ tab: 1, site: 'b.example' }).allow, false); // per-tab cooldown
  now = 6000;
  const second = g.admit({ tab: 2, site: 'b.example' });
  assert.equal(second.allow, true);
  assert.equal(g.admit({ tab: 3, site: 'c.example' }).allow, false); // two active
  g.settle({ ticket: first.ticket, code: 'rate-limited', retryAt: now + 120_000 });
  g.settle({ ticket: first.ticket, code: 'ok' });
  assert.equal(g.snapshot().active, 1);
  g.settle({ ticket: second.ticket, code: 'ok' });
  now = 30_000;
  assert.equal(g.admit({ tab: 4, site: 'a.example' }).allow, false);
  assert.ok(g.admit({ tab: 5, site: 'a.example' }).retryAfterMs >= 90_000);
  now = 200_000;
  const retry = g.admit({ tab: 6, site: 'a.example' });
  assert.equal(retry.allow, true);
  g.settle({ ticket: retry.ticket, code: 'site-refused' });
  assert.ok(g.admit({ tab: 7, site: 'a.example' }).retryAfterMs >= 119_000); // second refusal doubles the backoff
  assert.ok(g.snapshot().active === 0);
});

for (const variant of [0, 1, 2, 3]) {
  test(`scope and generation isolation / ${variant}`, () => {
    const { graph, advance, at } = graphAt();
    const url = 'https://cdn.example/show/video.mp4?token=' + 'a'.repeat(variant * 200 + 200);
    graph.observePlayer({ scope, playerKey: 'p', generation: 1, sourceUrl: url, at: at() });
    graph.observePlayer({ scope: scope + '-other', playerKey: 'p', generation: 1, sourceUrl: url, at: advance(1) });
    graph.observePlayer({ scope, playerKey: 'p', generation: 2, at: advance(1000) });
    graph.observeRequest({ scope, playerKey: 'p', generation: 2, url, kind: 'direct', at: advance(1) });
    assert.equal(graph.observePlayer({ scope, playerKey: 'p', generation: 1, sourceUrl: url, at: at() }), null);
    const [candidate] = graph.associate({ scope, playerKey: 'p' }).candidates;
    assert.equal(candidate.exactPlayerUrl, false);
    assert.equal(candidate.resourceWitness, false);
    assert.equal(candidate.stale, false);
    assert.equal(candidate.provenance, 'associated');
    assert.ok(candidate.id.length <= 128);
    graph.forget(scope + '-other');
    assert.equal(graph.associate({ scope, playerKey: 'p' }).candidates.length, 1);
  });
}

test('a manifest and a same-directory segment in a different document cannot link', () => {
  const { graph, at } = graphAt();
  graph.observePlayer({ scope, playerKey: 'p', generation: 1, at: at() });
  const manifest = graph.observeRequest({ scope, url: 'https://cdn.example/a/master.m3u8', kind: 'hls', playerKey: 'p', generation: 1, at: at() });
  const foreign = graph.observeRequest({ scope: 'other', url: 'https://cdn.example/a/seg.ts', kind: 'fragment', at: at() });
  assert.equal(graph.link(foreign, manifest, 'segment-of'), false);
  assert.equal(graph.associate({ scope, playerKey: 'p' }).candidates[0].manifestParent, false);
  const local = graph.observeRequest({ scope, url: 'https://cdn.example/a/seg.ts', kind: 'fragment', at: at() });
  assert.equal(graph.link(local, manifest, 'segment-of'), true);
  assert.equal(graph.associate({ scope, playerKey: 'p' }).candidates[0].manifestParent, true);
});

test('same generation with incompatible observed player URLs has no decisive strong witness', () => {
  const { graph, at } = graphAt();
  graph.observePlayer({ scope, playerKey: 'p', generation: 1, sourceUrl: 'https://cdn.example/a.mp4', at: at() });
  graph.observePlayer({ scope, playerKey: 'p', generation: 1, sourceUrl: 'https://cdn.example/b.mp4', at: at() });
  assert.ok(graph.associate({ scope, playerKey: 'p' }).candidates.every(c => !c.exactPlayerUrl && c.hypothesized));
});

for (const verdict of [undefined, 'verify', 'exact']) {
  for (const key of [undefined, 'other-src']) {
    test(`partial recovery cannot acquire a different source / ${verdict} / ${key}`, () => {
      const step = P.plan({ intent: explicit, sources: [verified('direct')], partial: { exists: true, identityVerdict: verdict, verifiedSourceKey: key } });
      assert.equal(step.next.route, 'verify-replacement');
      assert.notEqual(step.status, 'act');
    });
  }
}

test('verified partial identity binds only the exact route, never unrelated alternatives', () => {
  const scenario = { intent: explicit, sources: [verified('dash'), verified('direct')], partial: { exists: true, identityVerdict: 'exact', verifiedSourceKey: 'direct-src' } };
  const next = P.plan(scenario);
  assert.equal(next.next.source, 'direct-src');
  assert.equal(next.alternatives.length, 0);
  assert.notEqual(P.plan({ ...scenario, attempts: [{ route: 'native-http', key: 'direct-src', outcome: 'failed' }], capabilities: { chromeHandoff: true } }).status, 'fallback');
});

for (const flag of ['oneUse', 'post', 'blob']) {
  test(`original body permission is not original byte evidence / ${flag}`, () => {
    const next = P.plan({ intent: explicit, sources: [verified('direct', { [flag]: true })], capabilities: { browserBody: true, chromeDownload: { exists: true } } });
    assert.equal(next.next.route, 'chrome-keep');
  });
}

test('ownership still requires replay proof and preserves unresumable Chrome bytes', () => {
  assert.equal(P.ownershipRoute({ approved: true, nativeReplayVerified: true, chromeHasBytes: true }).route, 'chrome-keep');
  assert.equal(P.ownershipRoute({ approved: true, nativeReplayVerified: true, chromeHasBytes: true, chromeReplaySafe: true }).route, 'native-http');
  assert.equal(P.ownershipRoute({ approved: true, chromeReplaySafe: true, chromeHasBytes: true }).route, 'chrome-keep');
});
