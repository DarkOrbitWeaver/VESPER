import test from 'node:test';
import assert from 'node:assert/strict';
import {candidate,uniqueCandidates} from '../../tools/intelligence/discover-public-reference.mjs';

test('catalogue candidates are bounded and reject arbitrary or signed hosts',()=>{
  const make=(url,options)=>candidate('wikimedia-commons','train',42,url,options);
  const good=make('https://upload.wikimedia.org/a.webm?utm_source=example',{size:1024,duration:3});
  assert.equal(good.entry.expected,'https://upload.wikimedia.org/a.webm');
  for(const url of ['https://localhost/a.mp4','https://upload.wikimedia.org/a.mp4?token=private','https://user@upload.wikimedia.org/a.mp4','https://upload.wikimedia.org:444/a.mp4'])assert.equal(make(url),null);
  assert.equal(make(good.entry.expected,{size:9*1024*1024}),null);
  assert.equal(make(good.entry.expected,{duration:181}),null);
});

test('catalogue deduplication excludes repeated program, URL and content identity',()=>{
  const make=(id,url,identity)=>candidate('wikimedia-commons','train',id,'https://upload.wikimedia.org/'+url,{identity});
  const a=make(1,'a.webm','ABC'), b=make(2,'b.webm','DEF');
  assert.deepEqual(uniqueCandidates([a,make(1,'c.webm','xyz'),make(3,'a.webm','xyz'),make(4,'d.webm','abc'),b]),[a,b]);
});
