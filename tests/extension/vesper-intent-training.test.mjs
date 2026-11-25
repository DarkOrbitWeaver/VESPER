import test from 'node:test';
import assert from 'node:assert/strict';
import { fit, validatePartitions, evaluate } from '../../scripts/train-player-intent.mjs';

const facts=(id, overrides={})=>({id,area:100000,visibleFraction:1,playing:true,ready:true,
  progressed:true,controls:true,muted:false,incidentalLoop:false,interacted:false,incumbent:false,...overrides});
const page=(group,known=true)=>({group,chosen:known?'main':null,candidates:[
  facts('main',known?{interacted:true,area:40000}:{}),
  facts('other',known?{incidentalLoop:true,muted:true,controls:false}:{}),
]});
const sample=()=>({train:[page('train-family')],validation:[page('validation-family'),page('validation-family',false)],
  holdout:[page('holdout-family'),page('holdout-family',false)]});

test('offline fitting produces a deterministic candidate without deploying it',()=>{
  const data=sample(), first=fit(data), second=fit(data);
  assert.deepEqual(first,second);
  assert.equal(first.deployed,false);
  assert.equal(first.candidate.correctDecisive,1);
  assert.equal(first.candidate.ambiguousDecisive,0);
  assert.equal(first.candidate.wrongDecisive,0);
  assert.equal(first.model.version,1);
});
test('site-family leakage across train and evaluation splits is rejected',()=>{
  const data=sample();
  data.holdout[0].group='TRAIN-FAMILY';
  assert.throws(()=>fit(data),/Group crosses partitions/);
});
test('candidate identities must be unique and labels must refer to present candidates',()=>{
  const data=sample();
  data.train[0].candidates[1].id='main';
  assert.throws(()=>validatePartitions(data),/Invalid candidate/);
  const missing=sample();missing.train[0].chosen='missing';
  assert.throws(()=>validatePartitions(missing),/Chosen candidate absent/);
});
test('the training schema rejects URLs, credentials and unbounded free-form evidence',()=>{
  const data=sample();
  data.train[0].candidates[0].url='https://example.com/?secret=token';
  assert.throws(()=>validatePartitions(data),/Invalid candidate/);
});
test('evaluation counts wrong decisive and ambiguous decisive independently',()=>{
  const cases=[page('fixture'),page('fixture',false)];
  const metrics=evaluate(cases,()=>({selected:'other',decisive:true}));
  assert.equal(metrics.wrongDecisive,1);
  assert.equal(metrics.ambiguousDecisive,1);
  assert.equal(metrics.decisivePrecision,0);
});
