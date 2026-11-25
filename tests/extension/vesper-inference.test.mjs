import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';

const code = name => readFileSync(new URL(`../../extension/${name}`, import.meta.url), 'utf8');
function load({ models = true, tamper } = {}) {
  const context = vm.createContext({ URL });
  vm.runInContext(code('vesper-intelligence.js'), context);
  if (models) vm.runInContext(code('vesper-model.js'), context);
  if (tamper) vm.runInContext(tamper, context);
  vm.runInContext(code('vesper-inference.js'), context);
  return context;
}
const { VesperInference: I, VesperIntelligence: legacy } = load();
const clone = value => JSON.parse(JSON.stringify(value));
const player = (id, overrides = {}) => ({ id, area: 200e3, visibleFraction: 1, playing: true, ready: true, progressed: true,
  controls: true, muted: false, duration: 1800, networkBound: true, adContext: false, ...overrides });

test('shipped models load, validate and are reported with their versions', () => {
  const status = I.status();
  assert.equal(status.player.fallback, false);
  assert.equal(status.source.fallback, false);
  assert.match(status.player.version, /^2\.\d+\.\d+/);
  assert.match(status.source.version, /^1\.\d+\.\d+/);
});

test('model serialization round-trips and the checksum detects any weight change', () => {
  const model = clone(I.BASELINES['vesper.player-intent/2']);
  assert.doesNotThrow(() => I.validateModel(JSON.parse(JSON.stringify(model))));
  model.weights.area += 0.5;
  assert.throws(() => I.validateModel(model), /checksum/);
});

test('sign constraints reject a model that rewards advertisements, loops, stale or hypothesized sources', () => {
  for (const [schema, feature] of [['vesper.player-intent/2', 'adContext'], ['vesper.player-intent/2', 'incidentalLoop'],
    ['vesper.source-association/1', 'stale'], ['vesper.source-association/1', 'hypothesized']]) {
    const model = clone(I.BASELINES[schema]);
    model.weights[feature] = 2;
    assert.throws(() => I.validateModel(I.sealModel(model)), /sign constraint/);
  }
});

test('unknown schema, kind, feature set, version and threshold ranges are rejected', () => {
  const base = clone(I.BASELINES['vesper.player-intent/2']);
  const variants = [m => { m.schema = 'vesper.player-intent/9'; }, m => { m.kind = 'neural'; }, m => { delete m.weights.area; },
    m => { m.weights.extra = 1; }, m => { m.version = 'latest'; }, m => { m.temperature = 0; }, m => { m.minConfidence = 1; },
    m => { m.weights.area = 99; }, m => { m.format = 'other'; }];
  for (const change of variants) {
    const model = clone(base); change(model);
    assert.throws(() => I.validateModel(model.schema in I.SCHEMAS ? I.sealModel(model) : model));
  }
});

test('a corrupt shipped model falls back to the authored baseline and says so', () => {
  const broken = load({ tamper: "globalThis.VesperModels = { models: { 'vesper.player-intent/2': { format: 'vesper.model/1', schema: 'vesper.player-intent/2', kind: 'calibrated-linear', version: '9.0.0', weights: {}, temperature: 1, margin: 1, minConfidence: 0.5, checksum: 'x' } } };" });
  const status = broken.VesperInference.status();
  assert.equal(status.player.fallback, true);
  assert.match(status.player.error, /features/);
  assert.equal(status.player.version, '2.0.0-baseline');
  const missing = load({ models: false }).VesperInference.status();
  assert.equal(missing.player.fallback, true);
  assert.equal(missing.source.fallback, true);
});

test('inference is deterministic and independent of candidate order', () => {
  const candidates = [player('a', { area: 90e3 }), player('b'), player('c', { playing: false, area: 40e3 })];
  const first = I.rankPlayers(candidates);
  for (let i = 0; i < 20; i++) assert.deepEqual(I.rankPlayers([...candidates].reverse()), first);
  assert.equal(first.selected, 'b');
});

test('features nobody reported are absent evidence, partially reported features are uncertainty', () => {
  const none = I.rankPlayers([{ id: 'main', area: 300e3, visibleFraction: 1, playing: true, controls: true, progressed: true, muted: false },
    { id: 'thumb', area: 20e3, visibleFraction: 1 }]);
  assert.equal(none.ranked[0].missing.length, 0);
  // networkBound known only for the leader, which leads by more than the margin but by less than the
  // weight the runner-up's unreported value could add. The decision must wait for that evidence.
  const partial = I.createRanker(I.BASELINES['vesper.player-intent/2']).rank([
    player('a', { networkBound: true }), player('b', { networkBound: undefined, area: 210e3 })]);
  assert.equal(partial.selected, 'a');
  assert.equal(partial.decisive, false);
  assert.equal(partial.reason, 'missing-evidence');
  assert.ok(partial.ranked.find(row => row.id === 'b').missing.includes('networkBound'));
});

test('safety vetoes override any model score', () => {
  const rank = I.createRanker(I.sealModel({ ...clone(I.BASELINES['vesper.player-intent/2']), margin: 0, minConfidence: 0.34 })).rank;
  const ad = rank([player('ad', { area: 800e3, adContext: true }), player('main', { area: 50e3, playing: false })]);
  assert.equal(ad.decisive, false); assert.equal(ad.reason, 'advertisement-context');
  const loop = rank([player('loop', { incidentalLoop: true, muted: true })]);
  assert.equal(loop.decisive, false); assert.equal(loop.reason, 'incidental-loop');
  const conflict = rank([player('big', { area: 900e3 }), player('clicked', { area: 30e3, playing: false, interacted: true })]);
  if (conflict.selected === 'big') assert.equal(conflict.reason, 'conflicting-interaction');
  assert.equal(rank([player('x'), player('x')]).reason, 'duplicate-identity');
  assert.equal(rank(Array.from({ length: 257 }, (_, i) => player('p' + i))).reason, 'candidate-limit');
});

test('explicit interaction wins and the explanation names the deciding terms', () => {
  const result = I.rankPlayers([player('big', { area: 500e3, playing: false, progressed: false }),
    player('chosen', { area: 120e3, interacted: true })]);
  assert.equal(result.selected, 'chosen');
  assert.ok(result.ranked[0].terms.interaction > 0);
  assert.ok(result.confidence > 0 && result.confidence <= 1);
});

test('source association: stale, fragment, advertisement and hypothesized sources never decide', () => {
  for (const [facts, reason] of [[{ playerBound: true, stale: true }, 'stale-generation'], [{ playerBound: true, fragmentOnly: true }, 'fragment-only'],
    [{ playerBound: true, adContext: true }, 'advertisement-context'], [{ declared: true, hypothesized: true }, 'hypothesized-source']]) {
    const result = I.rankSources([{ id: 's', ...facts }]);
    assert.equal(result.decisive, false); assert.equal(result.reason, reason);
  }
  const exact = I.rankSources([{ id: 'exact', exactPlayerUrl: true, playerBound: true, currentGeneration: true, wholeFile: true },
    { id: 'guess', declared: true, hypothesized: true }]);
  assert.equal(exact.selected, 'exact'); assert.equal(exact.decisive, true);
});

test('regression versus the v1 ranker on its own invariant families: v2 is never decisively wrong where v1 was right', () => {
  let compared = 0;
  for (const area of [24e3, 80e3, 320e3, 640e3]) for (const ratio of [0.5, 1, 2]) for (const reverse of [false, true]) {
    const main = { id: 'main', area, visibleFraction: 1, ready: true, controls: true, playing: true, progressed: true, muted: false };
    const loop = { id: 'loop', area: area * ratio, visibleFraction: 1, ready: true, controls: false, playing: true, muted: true, incidentalLoop: true };
    const input = reverse ? [loop, main] : [main, loop];
    const v1 = legacy.rankPlayers(input), v2 = I.rankPlayers(input);
    if (v1.decisive && v1.selected === 'main') { compared++; assert.ok(!v2.decisive || v2.selected === 'main'); }
    assert.ok(!(v2.decisive && v2.selected === 'loop'));
  }
  assert.ok(compared > 0);
});

test('ranking stays bounded: 256 candidates rank in well under a frame budget', () => {
  const candidates = Array.from({ length: 256 }, (_, i) => player('p' + i, { area: 10e3 + i * 997 }));
  // Best of 20 measures the ranking cost itself, not scheduler noise from parallel test files.
  let perCallMs = Infinity;
  for (let i = 0; i < 20; i++) {
    const start = process.hrtime.bigint();
    I.rankPlayers(candidates);
    perCallMs = Math.min(perCallMs, Number(process.hrtime.bigint() - start) / 1e6);
  }
  assert.ok(perCallMs < 16, `ranking took ${perCallMs} ms`);
});


test('a lower-ranked rival with missing evidence can still overturn the observed leader', () => {
  const model = I.sealModel({ ...clone(I.BASELINES['vesper.source-association/1']), margin: 0, minConfidence: 0.34 });
  const result = I.createRanker(model).rank([
    { id: 'a', exactPlayerUrl: true, resourceWitness: false, manifestParent: false, currentGeneration: true, playerBound: true, wholeFile: false, declared: false, durationAgreement: 0 },
    { id: 'b', exactPlayerUrl: false, resourceWitness: true, manifestParent: false, currentGeneration: true, playerBound: false, wholeFile: false, declared: false, durationAgreement: 0 },
    { id: 'c', exactPlayerUrl: false, resourceWitness: true, manifestParent: null, currentGeneration: null, playerBound: false, wholeFile: null, declared: null, durationAgreement: null }
  ]);
  assert.equal(result.selected, 'a');
  assert.deepEqual(Array.from(result.ranked, row => row.id), ['a', 'b', 'c']);
  assert.equal(result.decisive, false);
  assert.equal(result.reason, 'missing-evidence');
});

test('fallback models and timing alone cannot authorize a decisive association', () => {
  const missing = load({ models: false }).VesperInference;
  assert.equal(missing.rankPlayers([player('x')]).decisive, false);
  assert.equal(I.rankSources([{ id: 'x', playerBound: true, currentGeneration: true }]).reason, 'unproven-association');
});

test('a unique explicit interaction resolves a narrow ranking margin without overriding vetoes', () => {
  const feed = { area:230400, visibleFraction:1, centerDistance:0, playing:true, ready:true, progressed:true,
    controls:false, muted:false, incidentalLoop:false, incumbent:true, duration:40, adContext:false };
  const chosen = {...feed, id:'chosen', area:92160, visibleFraction:0.4, centerDistance:0.6,
    progressed:false, incumbent:false, interacted:true};
  const result = I.rankPlayers([{...feed,id:'big',interacted:false}, chosen]);
  assert.equal(result.selected,'chosen');
  assert.equal(result.reason,'explicit-interaction');
  assert.equal(result.decisive,true);
  const both=I.rankPlayers([{...feed,id:'big',interacted:true},chosen]);
  assert.notEqual(both.reason,'explicit-interaction');
  const duplicate=I.rankPlayers([chosen,chosen]);
  assert.equal(duplicate.decisive,false);
  assert.equal(duplicate.reason,'duplicate-identity');
});
