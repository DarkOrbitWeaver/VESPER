import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';

const context = vm.createContext({ URL });
vm.runInContext(readFileSync(new URL('../../extension/vesper-intelligence.js', import.meta.url), 'utf8'), context);
const { rankPlayers, operationDecision, PLAYER_POLICY } = context.VesperIntelligence;
const video = (id, area, overrides = {}) => ({ id, area, visibleFraction: 1, ready: true,
  controls: true, playing: false, muted: false, ...overrides });

// Decision invariants, varied independently of any site, selector or URL.
// These are synthetic holdout-style regressions, not observations of hundreds of websites.
for (const area of [24000, 42000, 80000, 160000, 320000, 640000]) {
  for (const ratio of [0.5, 0.75, 1, 1.25, 1.5, 2]) {
    for (const reverse of [false, true]) {
      test(`primary versus muted autoplay loop / ${area} / ${ratio} / ${reverse}`, () => {
        const main = video('main', area, { playing: true, progressed: true });
        const incidental = video('loop', area * ratio, { playing: true, muted: true,
          controls: false, incidentalLoop: true });
        const result = rankPlayers(reverse ? [incidental, main] : [main, incidental]);
        assert.equal(result.selected, 'main');
        assert.equal(result.decisive, true);
        assert.notEqual(operationDecision({ model: result }), 'approved');
      });
    }
  }
}
for (const area of [25000, 60000, 140000, 280000, 560000]) {
  for (const fraction of [0.2, 0.45, 0.7, 1]) {
    for (const reverse of [false, true]) {
      test(`equally plausible players abstain / ${area} / ${fraction} / ${reverse}`, () => {
        const a = video('a', area, { visibleFraction: fraction, playing: true, progressed: true });
        const b = video('b', area, { visibleFraction: fraction, playing: true, progressed: true, incumbent: true });
        const result = rankPlayers(reverse ? [b,a] : [a,b]);
        assert.equal(result.selected, 'b');
        assert.equal(result.decisive, false);
        assert.equal(result.reason, 'competing-players');
      });
    }
  }
}
for (const area of [30000, 70000, 120000, 250000, 500000]) {
  for (const ratio of [0.25, 0.5, 0.75, 1]) {
    for (const reverse of [false, true]) {
      test(`deliberate player interaction outweighs size / ${area} / ${ratio} / ${reverse}`, () => {
        const intended = video('chosen', area * ratio, { interacted: true, playing: true, progressed: true });
        const neighbor = video('neighbor', area, { playing: true, incumbent: true });
        const result = rankPlayers(reverse ? [neighbor,intended] : [intended,neighbor]);
        assert.equal(result.selected, 'chosen');
        assert.equal(result.decisive, true);
      });
    }
  }
}
for (const area of [30000, 60000, 100000, 200000, 400000, 800000]) {
  for (const muted of [true, false]) {
    for (const progressed of [true, false]) {
      test(`only incidental loop never prefetches / ${area} / ${muted} / ${progressed}`, () => {
        const result = rankPlayers([video('loop', area, { incidentalLoop: true, controls: false, muted, progressed })]);
        assert.equal(result.selected, 'loop');
        assert.equal(result.decisive, false);
      });
    }
  }
}
for (const area of [30000, 70000, 120000, 250000, 500000]) {
  for (const ratio of [0.05, 0.1, 0.2, 0.3]) {
    for (const reverse of [false, true]) {
      test(`large paused primary and incidental tiny preview / ${area} / ${ratio} / ${reverse}`, () => {
        const main = video('main', area, { incumbent: true, progressed: true });
        const loop = video('loop', area * ratio, { playing: true, muted: true, controls: false, incidentalLoop: true });
        const result = rankPlayers(reverse ? [loop,main] : [main,loop]);
        assert.equal(result.selected, 'main');
        assert.equal(result.decisive, true);
      });
    }
  }
}
test('empty and malformed observations cannot produce a decision', () => {
  for (const input of [null, [], [null], [video('x', NaN)], [video('x', Infinity)], [video('x', 0)]])
    assert.equal(rankPlayers(input).selected, null);
});
test('candidate cap prevents overconfident ranking on an exceptionally crowded page', () => {
  const candidates = Array.from({length: PLAYER_POLICY.limit + 1}, (_, i) => video('p'+i, 100000));
  candidates[0].interacted = true;
  const result = rankPlayers(candidates);
  assert.equal(result.decisive, false);
  assert.equal(result.reason, 'candidate-limit');
  assert.equal(result.ranked.length, PLAYER_POLICY.limit);
});
test('ranking does not mutate input or authorize a download', () => {
  const candidates = Object.freeze([Object.freeze(video('a', 100000, { interacted: true }))]);
  const result = rankPlayers(candidates);
  assert.equal(result.decisive, true);
  assert.equal(operationDecision({ model: result }), 'ignore');
  assert.equal(operationDecision({ model: result, requestKind: 'drm' }), 'unsupported');
});
