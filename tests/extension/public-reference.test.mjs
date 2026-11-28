import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFileSync} from 'node:fs';
import {validateManifest,referenceLabel,sourceObservation,playerIntentLabel} from '../../tools/intelligence/collect-public-reference.mjs';

const url='https://cdn.example.org/video.mp4', other='https://cdn.example.org/other.mp4';
const spec={group:'public-family',split:'train',programId:'program-one',page:url};
const [normalized]=validateManifest([spec]);
const player={id:'player-0',src:url,ready:true,played:true,duration:5};
const response={url,kind:'direct',status:206};
const runtime=vm.createContext({URL});
for(const file of ['vesper-model.js','vesper-inference.js','vesper-evidence-graph.js'])
  vm.runInContext(readFileSync(new URL('../../extension/'+file,import.meta.url),'utf8'),runtime);

test('public reference manifest rejects signed URLs, local addresses, duplicate programs and family leakage',()=>{
  for(const page of ['http://example.org/a','https://example.org/a?token=x','https://127.0.0.1/a','https://user:pass@example.org/a','https://private.local/a'])
    assert.throws(()=>validateManifest([{...spec,page}]),/Invalid public/);
  assert.throws(()=>validateManifest([spec,{...spec,page:other}]),/Duplicate program/);
  assert.throws(()=>validateManifest([spec,{...spec,programId:'program-two',page:other,split:'holdout'}]),/Family crosses/);
  assert.throws(()=>validateManifest([spec,{...spec,programId:'program-two'}]),/Duplicate expected resource/);
  assert.throws(()=>validateManifest([{...spec,competitors:[url]}]),/Duplicate competing resource/);
  assert.throws(()=>validateManifest([{...spec,competitors:['https://private.local/a']}]),/Invalid competing resources/);
  assert.throws(()=>validateManifest([{...spec,competitors:[other]},{...spec,group:'second-family',programId:'program-two',page:other,split:'holdout'}]),/Resource crosses partitions/);
  assert.deepEqual(validateManifest([{...spec,competitors:[other]}])[0].competitors,[other]);
});

test('reference label requires predeclared identity, actual playback and a successful response, independent of model',()=>{
  assert.equal(referenceLabel(normalized,[player],[response]).status,'verified');
  for(const playback of [[{...player,played:false}],[{...player,src:other}],[player,{...player,id:'player-1'}]])
    assert.equal(referenceLabel(normalized,playback,[response]).status,'reference-not-established');
  assert.equal(referenceLabel(normalized,[player],[]).status,'reference-response-missing');
  assert.equal(referenceLabel(normalized,[player],[{...response,fromServiceWorker:true}]).status,'worker-mediated-reference-unproven');
  assert.equal(referenceLabel(normalized,[{...player,frame:'top'}],[{...response,frame:'child'}]).status,'reference-response-missing');
  assert.equal(referenceLabel(normalized,[{...player,frame:'top'}],[{...response,frame:'top'}]).status,'verified');
  assert.equal(referenceLabel({...normalized,probeOnly:true},[player],[response]).status,'probe-only');
});

test('unreviewed observations cannot become labels and exported model features contain no media URLs',()=>{
  const responses=[response,{url:other,kind:'direct',status:200}];
  const ref=referenceLabel(normalized,[player],responses);
  const observation=sourceObservation(runtime,normalized,[player],responses,ref);
  assert.equal(observation.established,true);
  assert.equal(observation.candidates.length,2);
  assert.equal(JSON.stringify(observation).includes('https:'),false);
  assert.equal(observation.candidates.find(c=>c.id===observation.chosen).exactPlayerUrl,true);
  const unreviewed=sourceObservation(runtime,normalized,[player],responses,{status:'probe-only'});
  assert.equal(unreviewed.established,false);
  assert.equal(unreviewed.chosen,null);
  const competing=[player,{...player,id:'player-1',src:other}];
  assert.equal(sourceObservation(runtime,normalized,competing,responses,ref),null);
  const scoped=sourceObservation(runtime,normalized,competing,responses,ref,player.id);
  assert.equal(scoped.established,true);
  assert.equal(scoped.candidates.length,2);
  const wrong=sourceObservation(runtime,normalized,competing,responses,ref,'player-1');
  assert.equal(wrong.established,false);
  assert.equal(JSON.stringify(scoped).includes('https:'),false);
});

test('scripted competing playback does not establish natural player intent',()=>{
  const ref=referenceLabel(normalized,[player],[response]);
  assert.deepEqual(playerIntentLabel(ref,[player],[player]),{status:'verified',chosen:player.id});
  const rival={...player,id:'player-1',src:other};
  assert.equal(playerIntentLabel(ref,[player,rival],[player,rival]).chosen,null);
  assert.equal(playerIntentLabel(ref,[player,rival],[player]).status,'requires-independent-intent-review');
  assert.equal(playerIntentLabel({status:'probe-only'},[player],[player]).chosen,null);
});
