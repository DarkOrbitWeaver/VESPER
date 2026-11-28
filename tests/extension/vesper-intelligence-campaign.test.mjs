// Controlled verification campaign over generated lineages. These are synthetic structural scenarios,
// not observations of real websites; they verify decision safety, generalization to held-out
// archetypes and the path from a model decision to an acquisition plan.
import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';
import { generate } from '../../tools/intelligence/corpus.mjs';
import { validateCases, validateReviewIds, reviewCoverage, fitWeights, evaluate, promote, guardFailures, wilsonInterval, pairedSafety } from '../../scripts/train-intelligence.mjs';

const context = vm.createContext({ URL });
for (const name of ['vesper-intelligence.js', 'vesper-model.js', 'vesper-inference.js', 'vesper-planner.js'])
  vm.runInContext(readFileSync(new URL(`../../extension/${name}`, import.meta.url), 'utf8'), context);
const { VesperInference: I, VesperPlanner: P } = context;
const corpus = generate();

function planFor(rank) {
  return P.plan({ intent: { explicit: true, scoped: true, wants: 'media' },
    selection: { decisive: rank.decisive, ambiguous: !rank.decisive },
    sources: rank.decisive ? [{ key: rank.selected, kind: 'hls', provenance: 'observed', verified: true }] : [],
    capabilities: { adapter: true } });
}

for (const split of ['validation', 'holdout']) {
  const cases = corpus.player[split];
  test(`player campaign / ${split}: ${cases.length} scenarios never act on ambiguity`, () => {
    let wrong = 0;
    for (const row of cases) {
      const rank = I.rankPlayers(row.candidates);
      const step = planFor(rank);
      if (row.chosen === null) {
        assert.equal(rank.decisive, false, row.archetype);
        assert.notEqual(step.status, 'act', row.archetype);
      } else if (rank.decisive) {
        if (rank.selected !== row.chosen) wrong++;
        else assert.equal(step.next.route, 'native-hls');
      } else assert.equal(step.next.route, 'resolve-selection');
    }
    // The shipped conservative baseline is allowed at most 1% decisive error on any controlled split.
    assert.ok(wrong <= Math.ceil(cases.length * 0.01), `${wrong} wrong decisive selections`);
  });
}

for (const split of ['validation', 'holdout']) {
  const cases = corpus.source[split];
  test(`source campaign / ${split}: ${cases.length} scenarios, stale and hypothesized sources never decide`, () => {
    for (const row of cases) {
      const rank = I.rankSources(row.candidates);
      if (row.chosen === null) assert.equal(rank.decisive, false, row.archetype);
      else if (rank.decisive) assert.equal(rank.selected, row.chosen, row.archetype);
      const best = row.candidates.find(c => c.id === rank.selected);
      if (rank.decisive) assert.ok(!best.stale && !best.fragmentOnly && !(best.hypothesized && !best.exactPlayerUrl && !best.resourceWitness));
    }
  });
}

test('lineages never cross partitions and the holdout contains unseen archetypes', () => {
  for (const kind of ['player', 'source']) {
    validateCases(kind === 'player' ? 'vesper.player-intent/2' : 'vesper.source-association/1', corpus[kind]);
    const trainArchetypes = new Set(corpus[kind].train.map(row => row.archetype));
    for (const name of corpus.holdoutArchetypes[kind]) assert.equal(trainArchetypes.has(name), false, name);
  }
});

test('leakage, weak-label evaluation and private fields are rejected by the training schema', () => {
  const data = generate({ perLineage: 2, lineagesPerArchetype: 4 });
  const leak = structuredClone(data.player); leak.holdout[0].group = leak.train[0].group;
  assert.throws(() => validateCases('vesper.player-intent/2', leak), /crosses partitions/);
  const weak = structuredClone(data.player); weak.validation[0].label = 'adapter-weak';
  assert.throws(() => validateCases('vesper.player-intent/2', weak), /Weak adapter labels/);
  const priv = structuredClone(data.player); priv.train[0].candidates[0].url = 'https://example.com/?token=x';
  assert.throws(() => validateCases('vesper.player-intent/2', priv), /Invalid candidate/);
});

test('promotion review IDs prevent duplicated independent samples across or within partitions', () => {
  const data = {
    train: [{ label: 'verified', caseId: 'case_0001' }],
    validation: [{ label: 'user', caseId: 'case_0002' }],
    holdout: [{ label: 'verified', caseId: 'case_0003' }]
  };
  assert.doesNotThrow(() => validateReviewIds(data));
  data.holdout.push({ label: 'verified', caseId: 'CASE_0003' });
  assert.throws(() => validateReviewIds(data), /Repeated independent review caseId/);
  data.holdout.pop();
  data.holdout[0].caseId = 'case_0001';
  assert.throws(() => validateReviewIds(data), /Repeated independent review caseId/);
  data.holdout[0].caseId = 'https://example.com/private';
  assert.throws(() => validateReviewIds(data), /Missing opaque independent review caseId/);
});

test('review coverage counts only independently labeled examples and reports missing coverage', () => {
  const schema = 'vesper.player-intent/2';
  const data = structuredClone(corpus.player);
  const coverage = reviewCoverage(schema, data);
  assert.equal(coverage.ready, false);
  assert.equal(coverage.splits.holdout.independent, 0);
  assert.equal(coverage.splits.holdout.missingChosen, 200);
  const row = data.holdout.find(item => item.chosen !== null);
  row.label = 'verified';
  row.caseId = 'review_0001';
  const reviewed = reviewCoverage(schema, data);
  assert.equal(reviewed.splits.holdout.independent, 1);
  assert.equal(reviewed.splits.holdout.missingChosen, 199);
  assert.equal(reviewed.ready, false);
});

test('evaluation reports uncertainty, family imbalance and missing calibration coverage', () => {
  const rows = [
    ...Array.from({length: 200}, () => ({group:'large',label:'fixture',chosen:'a',candidates:[{id:'a'}]})),
    {group:'small',label:'fixture',chosen:'b',candidates:[{id:'a'}]},
    {group:'small',label:'fixture',chosen:null,candidates:[{id:'a'}]}
  ];
  const metrics = evaluate(() => ({decisive:true,selected:'a'}), rows);
  assert.equal(metrics.correctDecisive, 200);
  assert.equal(metrics.familyMacroRecall, 0.5);
  assert.equal(metrics.familiesWithErrors, 1);
  assert.equal(metrics.calibratedCases, 0);
  assert.equal(metrics.expectedCalibrationError, null);
  assert.ok(wilsonInterval(200,200).lower < 0.99);
  assert.equal(wilsonInterval(0,0), null);
});

test('paired safety detects a newly wrong choice even if aggregate accuracy is unchanged', () => {
  const rows = ['a','b'].map(id => ({group:'fixture',label:'fixture',chosen:id,candidates:[{id}]}));
  const before = () => ({decisive:true,selected:'a'});
  const after = () => ({decisive:true,selected:'b'});
  assert.equal(evaluate(before,rows).decisivePrecision,evaluate(after,rows).decisivePrecision);
  assert.deepEqual(pairedSafety(after,before,rows),{newWrong:1,correctedWrong:1,lostCorrect:1,gainedCorrect:1});
});

test('fitting is deterministic and respects sign constraints', () => {
  const a = fitWeights('vesper.player-intent/2', corpus.player.train, { epochs: 50 });
  const b = fitWeights('vesper.player-intent/2', corpus.player.train, { epochs: 50 });
  assert.deepEqual(a, b);
  assert.ok(a.adContext <= 0 && a.incidentalLoop <= 0 && a.area >= 0);
});

test('the shipped models pass the safety guards and synthetic candidates cannot be promoted by default', () => {
  assert.deepEqual(guardFailures(I.rankPlayers, 'vesper.player-intent/2'), []);
  assert.deepEqual(guardFailures(I.rankSources, 'vesper.source-association/1'), []);
  const candidate = new URL('../../intelligence/models/candidates/player-intent-candidate.json', import.meta.url);
  assert.throws(() => promote(candidate), /independent real labels/);
});

test('the shipped player model is at least as precise as v1 on the untouched holdout', () => {
  const shipped = evaluate(I.rankPlayers, corpus.player.holdout);
  const v1 = evaluate(context.VesperIntelligence.rankPlayers, corpus.player.holdout);
  assert.ok(shipped.decisivePrecision >= v1.decisivePrecision);
  assert.equal(shipped.ambiguousDecisive, 0);
});
