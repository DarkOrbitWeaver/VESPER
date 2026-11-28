// Opt-in public catalogue discovery for the isolated reference observer.
// Bounded metadata/HEAD requests only; no private cookies, signed URLs, bulk files,
// or manufactured site families. Direct samples are a narrow sanity stratum,
// not a representative release or promotion corpus.
// Usage: node tools/intelligence/discover-public-reference.mjs manifest.json
import {writeFile} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import {pathToFileURL} from 'node:url';
import {validateManifest} from './collect-public-reference.mjs';

const LIMIT=8*1024*1024, MAX_JSON=2*1024*1024;
const sha=value=>createHash('sha256').update(String(value)).digest('hex');
const opaque=(family,id)=>family+'-'+sha(id).slice(0,20);
const headers={'User-Agent':'VESPER-QA/2 public-media reference validation (bounded metadata only)'};
const safeUrl=raw=>{
  try{
    const url=new URL(raw);
    // Remove only the known public catalogue analytics parameters.
    if(url.hostname==='upload.wikimedia.org')
      for(const k of ['utm_source','utm_campaign','utm_content'])url.searchParams.delete(k);
    if(url.protocol==='http:'&&url.hostname==='svs.gsfc.nasa.gov')url.protocol='https:';
    if(url.protocol!=='https:'||url.username||url.password||url.search||url.hash||url.port
      ||!['upload.wikimedia.org','cdn.freesound.org','svs.gsfc.nasa.gov'].includes(url.hostname))return null;
    return url.href;
  }catch{return null;}
};
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
async function json(address){
  const response=await fetch(address,{headers,signal:AbortSignal.timeout(12000)});
  if(!response.ok)throw Error('metadata-http-'+response.status);
  const reader=response.body.getReader(),chunks=[];let size=0;
  try{
    while(true){const {done,value}=await reader.read();if(done)break;
      size+=value.length;if(size>MAX_JSON)throw Error('metadata-limit');chunks.push(value);}
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
  }finally{await reader.cancel().catch(()=>{});}
}
export function candidate(group,split,id,raw,{page,identity,size,duration}={}){
  const url=safeUrl(raw);
  if(!url||size!=null&&(!Number.isSafeInteger(size)||size<1||size>LIMIT)
    ||duration!=null&&(!Number.isFinite(duration)||duration<1||duration>180))return null;
  const entry={group,split,programId:opaque(group,id),page:page?safeUrl(page):url,expected:url};
  if(!entry.page)return null;
  return {entry,identity:identity?String(identity).toLowerCase():null,size};
}
export function uniqueCandidates(candidates){
  const programs=new Set(),urls=new Set(),identities=new Set(),result=[];
  for(const c of candidates){
    if(!c||programs.has(c.entry.programId)||urls.has(c.entry.expected)
      ||c.identity&&identities.has(c.identity))continue;
    programs.add(c.entry.programId);urls.add(c.entry.expected);
    if(c.identity)identities.add(c.identity);
    result.push(c);
  }
  return result;
}
async function commons(){
  const result=[];
  for(const cat of ['Videos_of_animals','Videos_of_birds','Videos_of_waterfalls']){
    const u=new URL('https://commons.wikimedia.org/w/api.php');
    for(const [k,v] of Object.entries({action:'query',generator:'categorymembers',gcmtitle:'Category:'+cat,gcmlimit:'50',gcmtype:'file',prop:'imageinfo',iiprop:'url|mime|sha1|size',format:'json'}))u.searchParams.set(k,v);
    const j=await json(u);
    for(const p of Object.values(j.query?.pages||{})){
      const i=p.imageinfo?.[0];
      if(!i||!/^video\/(?:webm|mp4)$/.test(i.mime||''))continue;
      result.push(candidate('wikimedia-commons','train',p.pageid,i.url,{size:i.size,duration:i.duration,identity:i.sha1}));
    }
    if(uniqueCandidates(result).length>=16)break;
    await pause(300);
  }
  return uniqueCandidates(result).slice(0,16);
}
async function freesound(){
  const result=[];
  for(const term of ['bird','water','wind']){
    const u=new URL('https://api.openverse.org/v1/audio/');
    u.searchParams.set('q',term);u.searchParams.set('page_size','20');
    const j=await json(u);
    for(const x of j.results||[]){
      if(x.source!=='freesound'||!['cc0','by','by-sa','pdm'].includes(x.license)
        ||!/^https:\/\/cdn\.freesound\.org\/previews\//.test(x.url||''))continue;
      // A preview encoding of the same original sound is not another program.
      const id=/\/sounds\/(\d+)/.exec(x.foreign_landing_url||'')?.[1];
      if(!id)continue;
      result.push(candidate('freesound-public','validation',id,x.url,{duration:x.duration/1000}));
    }
    if(uniqueCandidates(result).length>=16)break;
    await pause(300);
  }
  return uniqueCandidates(result).slice(0,16);
}
async function nasa(){
  const result=[],seen=new Set(),queue=[5656,5101,5212,5000,5200,5300,5400,5500];
  while(queue.length&&seen.size<14&&result.length<12){
    const id=queue.shift();if(seen.has(id))continue;seen.add(id);
    try{
      const j=await json('https://svs.gsfc.nasa.gov/api/'+id+'/');
      const related=(j.related||[]).map(x=>x.id).filter(Number.isSafeInteger);
      queue.push(...related.slice(0,4));
      const v=j.main_video;
      if(v?.media_type==='Movie'&&/\.mp4$/i.test(v.filename||''))
        result.push(candidate('nasa-svs-public','holdout',j.id,v.url));
    }catch(e){console.log(JSON.stringify({family:'nasa-svs-public',metadataId:id,status:e.message}));}
    await pause(250);
  }
  return uniqueCandidates(result).slice(0,12);
}
async function probe(c){
  try{
    const response=await fetch(c.entry.expected,{method:'HEAD',headers,redirect:'error',signal:AbortSignal.timeout(10000)});
    const size=Number(response.headers.get('content-length'));
    const url=safeUrl(response.url);
    if(!response.ok||!url||!/^audio\/|^video\//i.test(response.headers.get('content-type')||'')
      ||!Number.isSafeInteger(size)||size<1||size>LIMIT)
      return {status:'not-bounded-media',programId:c.entry.programId};
    // Direct navigation uses the independently observed canonical response URL.
    // A redirect does not become an unverified source association.
    return {status:'candidate',entry:{...c.entry,page:c.entry.page===c.entry.expected?url:c.entry.page,expected:url},
      identity:c.identity,size};
  }catch(e){return {status:'unreachable',programId:c.entry.programId};}
}
export async function discover(output){
  const groups=await Promise.allSettled([commons(),freesound(),nasa()]);
  const raw=uniqueCandidates(groups.flatMap(g=>g.status==='fulfilled'?g.value:[]));
  const attempts=[];
  // Bound active requests and avoid hammering one origin.
  for(let start=0;start<raw.length;start+=3){
    const batch=await Promise.all(raw.slice(start,start+3).map(probe));
    attempts.push(...batch);
    await pause(250);
  }
  const selected=uniqueCandidates(attempts.filter(x=>x.status==='candidate')).map(x=>x.entry);
  const manifest=validateManifest(selected).map(({expected,probeOnly,...row})=>({...row,expected}));
  await writeFile(output,JSON.stringify(manifest,null,2)+'\n',{flag:'wx'});
  const counts=Object.fromEntries(['train','validation','holdout'].map(split=>[split,manifest.filter(x=>x.split===split).length]));
  console.log(JSON.stringify({catalogueCandidates:raw.length,manifest:manifest.length,counts,attempts:attempts.map(x=>({status:x.status,programId:x.entry?.programId??x.programId})),
    catalogueErrors:groups.map(x=>x.status==='rejected'?String(x.reason?.message||'unavailable'):null)}));
}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){
  if(!process.argv[2])throw Error('Specify new manifest output');
  await discover(process.argv[2]);
}
