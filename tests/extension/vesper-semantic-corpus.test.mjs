import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const context = vm.createContext({ URL });
vm.runInContext(readFileSync(new URL('../../extension/vesper-intelligence.js', import.meta.url), 'utf8'), context);
const corpus = JSON.parse(readFileSync(new URL('../fixtures/vesper-intent-corpus.json', import.meta.url), 'utf8'));
test('the multilingual intent regression corpus has no unsafe auto-approval and remains reproducible', () => {
  let mistakes = [];
  for (const [i, fact] of corpus.entries()) {
    const result = context.VesperIntelligence.proposeControl(fact);
    if (result.classification !== fact.want) mistakes.push(`${i}: ${fact.label}: ${result.classification} != ${fact.want}`);
    assert.equal(result.mayExecute, false);
    assert.notEqual(context.VesperIntelligence.operationDecision({ model: result }), 'approved');
  }
  assert.deepEqual(mistakes, []);
});
