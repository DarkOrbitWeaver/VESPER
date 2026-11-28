// Independent, opt-in public reference collection. Never use an installed
// browser profile, cookies, credentials, private URLs, or the production extension.
// The manifest declares the target before navigation; observations and labels
// are separate from VESPER inference. Direct playback is a narrow sanity set,
// not evidence of social feed or opaque-worker generalization.
// Usage: node tools/intelligence/collect-public-reference.mjs chrome.exe output.json [manifest.json]
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';
import vm from 'node:vm';
import { evaluate, validateCases, reviewCoverage } from '../../scripts/train-intelligence.mjs';

const sha = text => createHash('sha256').update(text).digest('hex');
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const opaque = value => typeof value === 'string' && /^[a-z0-9_-]{8,80}$/i.test(value);
const splits = ['train','validation','holdout'];
const publicUrl = value => {
  try {
    const u = new URL(value);
    return u.protocol === 'https:' && !u.username && !u.password && !u.search && !u.hash
      && u.hostname.includes('.') && !/^(?:\d{1,3}\.){3}\d{1,3}$/.test(u.hostname)
      && !/\.(?:localhost|local|internal|test|invalid)$/i.test(u.hostname) && !u.hostname.startsWith('[') ? u.href : null;
  } catch { return null; }
};

// Public demonstration programs already used by the VESPER lab, plus
// separate clips from Google's public media sample bucket. Do not count
// the MP4/WebM versions of one clip as separate programs.
export const defaults = [
  {group:'google-samples',split:'train',programId:'google-fun',page:'https://storage.googleapis.com/gtv-videos-bucket/sample/ForBiggerFun.mp4'},
  {group:'google-samples',split:'train',programId:'google-escape',page:'https://storage.googleapis.com/gtv-videos-bucket/sample/ForBiggerEscapes.mp4'},
  {group:'google-samples',split:'train',programId:'google-meltdown',page:'https://storage.googleapis.com/gtv-videos-bucket/sample/ForBiggerMeltdowns.mp4'},
  {group:'samplelib-public',split:'train',programId:'samplelib-5sec',page:'https://download.samplelib.com/mp4/sample-5s.mp4'},
  {group:'mdn-samples',split:'validation',programId:'mdn-flower',page:'https://mdn.github.io/shared-assets/videos/flower.mp4'},
  {group:'w3c-samples',split:'holdout',programId:'w3c-bunny',page:'https://media.w3.org/2010/05/bunny/trailer.mp4'},
  {group:'w3c-samples',split:'holdout',programId:'w3c-sintel',page:'https://media.w3.org/2010/05/sintel/trailer.mp4'}
];

export function validateManifest(input) {
  if (!Array.isArray(input) || !input.length || input.length > 512) throw Error('Invalid manifest size');
  const ids = new Set(), groups = new Map(), expectedUrls = new Set();
  return input.map(row => {
    if (!row || !opaque(row.programId) || !opaque(row.group) || !splits.includes(row.split)
        || !publicUrl(row.page) || (row.expected !== undefined && !publicUrl(row.expected)))
      throw Error('Invalid public reference manifest row');
    const id = row.programId.toLowerCase(), group = row.group.toLowerCase();
    if (ids.has(id)) throw Error('Duplicate program');
    ids.add(id);
    if (groups.has(group) && groups.get(group) !== row.split) throw Error('Family crosses partitions');
    groups.set(group,row.split);
    const expected = row.probeOnly === true ? null : publicUrl(row.expected || row.page);
    if (expected && expectedUrls.has(expected)) throw Error('Duplicate expected resource');
    if (expected) expectedUrls.add(expected);
    return {programId:id, group, split:row.split, page:publicUrl(row.page), expected,
      probeOnly:row.probeOnly === true};
  });
}

const loadRuntime = () => {
  const c = vm.createContext({URL});
  return Promise.all(['vesper-model.js','vesper-inference.js','vesper-evidence-graph.js'].map(async file =>
    [file,await readFile(new URL('../../extension/'+file,import.meta.url),'utf8')])).then(files => {
      for(const [file,code] of files) vm.runInContext(code,c,{filename:file});
      return c;
    });
};

const mediaKind = mime => /^video\/|^audio\//i.test(mime) ? 'direct' :
  /mpegurl/i.test(mime) ? 'hls' : /dash\+xml/i.test(mime) ? 'dash' : null;

// Reference resolution does not call VESPER. A label is emitted only for a
// predeclared, independently observed, playable HTTP resource. No guessed
// labels for blob handles, signed traffic, inaccessible or ambiguous pages.
export function referenceLabel(spec, playback, responses) {
  if (spec.probeOnly || !spec.expected) return {status:'probe-only'};
  const matches = playback.filter(p=>p.src===spec.expected && p.ready && p.played === true && Number.isFinite(p.duration) && p.duration>0);
  if (matches.length !== 1) return {status:'reference-not-established'};
  if (!responses.some(r=>r.url===spec.expected && r.kind==='direct' && r.status>=200 && r.status<300
      && r.frame===matches[0].frame && r.execution===matches[0].execution))
    return {status:'reference-response-missing'};
  return {status:'verified',playerId:matches[0].id,resource:spec.expected};
}

export function sourceObservation(runtime, spec, playback, responses, reference) {
  const player = playback.length === 1 ? playback[0] : null;
  if (!player || !player.ready || !publicUrl(player.src) || responses.length > 32) return null;
  const graph = runtime.VesperEvidenceGraph.create();
  const scope='public-reference', playerKey='player', generation=1;
  const candidates=[...new Map(responses.filter(item=>publicUrl(item.url)&&item.frame===player.frame
    && item.execution===player.execution)
    .map(item=>[item.url,item])).values()];
  // The same pre-verification representation as live acquisition. Neither a
  // reference label nor a successful native transfer supplies model features.
  const decision=graph.assessPicker({scope,playerKey,generation,sourceUrl:player.src,candidates:candidates.map(item=>({
    url:item.url,kind:item.kind,direct:item.url===player.src,witnessed:false,verified:false,
    compatible:true,wholeFile:item.wholeFile??null
  }))});
  const admitted=decision.rows.filter(row=>row.compatible);
  const rows=admitted.map(row=>row.features);
  // The reference joins only after observations are constructed. It never
  // selects candidates, changes features, or overrides an abstention.
  const chosen=reference.status==='verified' ? admitted.find(row=>candidates[row.index].url===reference.resource)?.features.id : null;
  return {candidates:rows,chosen:chosen??null,established:!!chosen};
}

async function run(chrome,output,manifest) {
  const specs=validateManifest(manifest);
  const runtime=await loadRuntime();
  const root=await mkdtemp(join(tmpdir(),'vesper-public-reference-'));
  const corpus={player:{train:[],validation:[],holdout:[]},source:{train:[],validation:[],holdout:[]}};
  const observations=[],labels=[],decisions=[],failures=[];
  let proc,socket,serial=0,current=null;
  const pending=new Map();
  function send(method,params={},sessionId){
    return new Promise((resolve,reject)=>{
      const id=++serial,timer=setTimeout(()=>{pending.delete(id);reject(Error('CDP deadline'))},8000);
      pending.set(id,{resolve,reject,timer});socket.send(JSON.stringify({id,method,params,...(sessionId?{sessionId}:{})}));
    });
  }
  async function until(fn,ms){const deadline=Date.now()+ms;while(Date.now()<deadline){try{const v=await fn();if(v)return v;}catch{}await sleep(100)}throw Error('deadline')}
  try {
    proc=spawn(chrome,['--headless=new','--no-first-run','--no-default-browser-check','--disable-background-networking',
      '--mute-audio','--autoplay-policy=no-user-gesture-required','--window-size=1120,800','--user-data-dir='+root,
      '--remote-debugging-port=0','about:blank'],{stdio:'ignore',windowsHide:true});
    const [port,path]=(await until(async()=>(await readFile(join(root,'DevToolsActivePort'),'utf8')).trim(),15000)).split(/\r?\n/);
    socket=new WebSocket('ws://127.0.0.1:'+port+path);
    await new Promise((resolve,reject)=>{socket.addEventListener('open',resolve,{once:true});socket.addEventListener('error',reject,{once:true})});
    socket.addEventListener('message',event=>{
      const m=JSON.parse(event.data),p=pending.get(m.id);
      if(p){pending.delete(m.id);clearTimeout(p.timer);m.error?p.reject(Error('CDP error')):p.resolve(m.result||{});return;}
      if(!current||!current.sessions.has(m.sessionId))return;
      if(m.method==='Target.attachedToTarget'){
        const state=current, child=m.params.sessionId, kind=m.params.targetInfo.type;
        if(state.sessions.size>=16||state.attachments.length>=16){state.overflow=true;return;}
        if(!['iframe','worker','service_worker','shared_worker'].includes(kind))return;
        state.sessions.set(child,kind);
        state.attachments.push((async()=>{
          try{
            await send('Network.enable',{},child);
            // Observe child targets; never evaluate or rewrite a worker.
            if(kind==='iframe')await send('Page.enable',{},child);
            await send('Target.setAutoAttach',{autoAttach:true,waitForDebuggerOnStart:false,flatten:true},child);
          }catch{state.incomplete=true;}
        })());
        return;
      }
      if(m.method==='Target.detachedFromTarget'){
        current.sessions.delete(m.params.sessionId);
        return;
      }
      if(m.method!=='Network.responseReceived')return;
      const r=m.params.response,kind=mediaKind(r.mimeType||''),url=publicUrl(r.url);
      if(!kind||!url||r.status<200||r.status>=300)return;
      const frame=m.params.frameId;
      const key=JSON.stringify([m.sessionId,frame,url]);
      if(!current.responses.has(key)&&current.responses.size>=32){current.overflow=true;return;}
      const length=Number(Object.entries(r.headers||{}).find(([name])=>name.toLowerCase()==='content-length')?.[1]);
      current.responses.set(key,{url,kind,status:r.status,frame,execution:m.sessionId,wholeFile:r.status===200&&kind==='direct'&&Number.isSafeInteger(length)&&length>0?true:null});
    });
    const browser=(await send('Browser.getVersion')).product;
    await send('Browser.setDownloadBehavior',{behavior:'deny'});
    // A separate browser context per program prevents cross-case cookies and
    // cache reuse. No persistent personal account or extension is loaded.
    for(const spec of specs){
      let context,target;
      const caseId=sha('case:'+spec.programId).slice(0,24),sessionId=sha('session:'+spec.programId).slice(0,24);
      const common={group:spec.group,programId:spec.programId,caseId,sessionId};
      try{
        context=(await send('Target.createBrowserContext')).browserContextId;
        await send('Browser.setDownloadBehavior',{behavior:'deny',browserContextId:context});
        target=(await send('Target.createTarget',{url:'about:blank',browserContextId:context})).targetId;
        const session=(await send('Target.attachToTarget',{targetId:target,flatten:true})).sessionId;
        current={session,sessions:new Map([[session,'page']]),attachments:[],responses:new Map(),overflow:false,incomplete:false};
        await send('Network.enable',{},session);
        await send('Page.enable',{},session);
        await send('Target.setAutoAttach',{autoAttach:true,waitForDebuggerOnStart:false,flatten:true},session);
        const nav=await send('Page.navigate',{url:spec.page},session);
        if(nav.errorText)throw Error('navigation failed');
        // A WeakMap in a QA-only isolated world binds progression to the same
        // DOM element. Reordered/recycled elements must not inherit old IDs.
        const expression=`(()=>{const state=globalThis.__vesperReference??=( {ids:new WeakMap(),next:0} );
          return [...document.querySelectorAll('video,audio')].slice(0,33).map(v=>{
            if(!state.ids.has(v))state.ids.set(v,++state.next);
            const b=v.getBoundingClientRect(),w=Math.max(0,Math.min(innerWidth,b.right)-Math.max(0,b.left)),h=Math.max(0,Math.min(innerHeight,b.bottom)-Math.max(0,b.top));
            return {id:state.ids.get(v),src:v.currentSrc,area:w*h,visibleFraction:b.width*b.height>0?w*h/(b.width*b.height):0,
              centerDistance:Math.min(1,Math.hypot(b.left+b.width/2-innerWidth/2,b.top+b.height/2-innerHeight/2)/Math.hypot(innerWidth/2,innerHeight/2)),
              ready:v.readyState>=1,played:false,position:v.currentTime,seeking:v.seeking,playing:!v.paused&&!v.ended,progressed:v.currentTime>1,
              controls:v.controls,muted:v.muted,incidentalLoop:v.defaultMuted&&v.autoplay&&v.loop&&!v.controls,
              duration:Number.isFinite(v.duration)?v.duration:null,declaredSource:!!v.getAttribute('src')};
          });
        })()`;
        const frame=(await send('Page.getFrameTree',{},session)).frameTree.frame.id;
        const worlds=new Map();
        const readPlayback=async()=>{
          await Promise.allSettled(current.attachments);
          const rows=[],seenFrames=new Set();
          for(const [execution,kind] of current.sessions){
            if(!['page','iframe'].includes(kind))continue;
            let tree;
            try{tree=(await send('Page.getFrameTree',{},execution)).frameTree;}catch{current.incomplete=true;continue;}
            const frames=[tree];
            for(const node of frames){
              frames.push(...(node.childFrames||[]));
              const id=node.frame.id,key=JSON.stringify([execution,id]);
              if(seenFrames.has(id))continue;
              if(seenFrames.size>=16){current.overflow=true;break;}
              try{
                if(!worlds.has(key)){
                  const world=await send('Page.createIsolatedWorld',{frameId:id,worldName:'vesper-reference'},execution);
                  worlds.set(key,world.executionContextId);
                }
                const contextId=worlds.get(key);
                const result=await send('Runtime.evaluate',{expression,contextId,returnByValue:true},execution);
                if(result.exceptionDetails)throw Error('world unavailable');
                seenFrames.add(id);
                const prefix=sha(execution+':'+id+':'+contextId).slice(0,12);
                for(const p of result.result?.value||[])rows.push({...p,id:prefix+'-'+p.id,frame:id,execution,contextId});
              }catch{worlds.delete(key);if(id===frame)current.incomplete=true;}
            }
          }
          if(rows.length>32)current.overflow=true;
          return rows.slice(0,33);
        };
        let playback=[];
        try{playback=await until(async()=>{const p=await readPlayback();return p.some(v=>v.ready)?p:null},spec.probeOnly?6000:12000);}
        catch{playback=await readPlayback();}
        const beforeVerification=playback;
        if(!spec.probeOnly&&playback.some(v=>v.src===spec.expected&&v.ready)){
          // Prove actual playback, not merely a metadata header. Only play the
          // predeclared public sample, muted in the isolated QA context.
          const expected=JSON.stringify(spec.expected);
          const before=new Map(playback.map(p=>[p.id,p]));
          const target=playback.find(p=>p.src===spec.expected&&p.ready);
          await send('Runtime.evaluate',{expression:"(()=>{const v=[...document.querySelectorAll('video,audio')].find(v=>v.currentSrc==="+expected+");if(v){v.muted=true;v.play().catch(()=>{})}})()",contextId:target.contextId},target.execution);
          const measure=async()=> (await readPlayback()).map(p=>{
            const prior=before.get(p.id);
            return {...p,played:!!prior&&prior.src===p.src&&prior.frame===p.frame&&prior.execution===p.execution&&prior.contextId===p.contextId&&p.ready
              && !p.seeking&&p.position-prior.position>0.1};
          });
          try{playback=await until(async()=>{const p=await measure();return p.some(v=>v.src===spec.expected&&v.played)?p:null},8000);}
          catch{playback=await readPlayback();}
        }
        const responses=[...current.responses.values()];
        const ref=current.overflow||playback.length>32?{status:'observation-overflow'}:current.incomplete?{status:'observer-incomplete'}:referenceLabel(spec,playback,responses);
        // Nested viewport geometry is not top-level visibility; do not train player intent from it.
        // Do not train a competing-player ranker on the gesture used to establish its reference.
        const playerFeatures=beforeVerification.length>1?beforeVerification:playback;
        const playerRows=playerFeatures.filter(p=>p.area>0&&p.frame===frame&&p.execution===session)
          .map(({src,played,frame,execution,contextId,position,seeking,...row})=>row);
        const source=sourceObservation(runtime,spec,playback,responses,ref);
        observations.push({...common,split:spec.split,players:playerRows,sourceCandidates:source?.candidates||[],
          mediaResponses:responses.length,overflow:current.overflow||playback.length>32,
          contexts:[...current.sessions.values()],frameCount:new Set(playback.map(p=>p.frame)).size,incomplete:current.incomplete});
        labels.push({...common,status:ref.status,player:ref.playerId||null,source:source?.established?source.chosen:null});
        if(ref.status==='verified'&&playback.every(p=>p.frame===frame&&p.execution===session)
            &&playerFeatures.every(p=>p.frame===frame&&p.execution===session)
            &&playerFeatures.some(p=>p.id===ref.playerId&&p.src===ref.resource)&&playerRows.some(p=>p.id===ref.playerId))
          corpus.player[spec.split].push({...common,label:'verified',chosen:ref.playerId,candidates:playerRows});
        if(source?.established)
          corpus.source[spec.split].push({...common,label:'verified',chosen:source.chosen,candidates:source.candidates});
        const summarize=rank=>({selected:rank.selected,decisive:rank.decisive,confidence:rank.confidence,reason:rank.reason});
        decisions.push({caseId,player:summarize(runtime.VesperInference.rankPlayers(playerRows)),
          source:summarize(runtime.VesperInference.rankSources(source?.candidates||[]))});
        console.log(JSON.stringify({program:spec.programId,status:ref.status,players:playback.length,resources:responses.length}));
      }catch(e){
        // Do not persist CDP errors that may contain URLs or private values.
        failures.push({...common,status:'collection-failed'});
        console.log(JSON.stringify({program:spec.programId,status:'collection-failed'}));
      }finally{
        current=null;
        if(target)try{await send('Target.closeTarget',{targetId:target})}catch{}
        if(context)try{await send('Target.disposeBrowserContext',{browserContextId:context})}catch{}
      }
    }
    const coverage={},metrics={};
    for(const [kind,schema,rank] of [['player','vesper.player-intent/2',runtime.VesperInference.rankPlayers],['source','vesper.source-association/1',runtime.VesperInference.rankSources]]){
      const partitions=corpus[kind];
      metrics[kind]=Object.fromEntries(splits.map(split=>[split,evaluate(rank,partitions[split])]));
      if(splits.every(split=>partitions[split].length)){
        validateCases(schema,partitions);
        coverage[kind]=reviewCoverage(schema,partitions);
      }else coverage[kind]={ready:false,reason:'missing-partition'};
    }
    const result={format:'vesper.public-reference/1',browser,manifestSha256:sha(JSON.stringify(specs)),
      provenance:'independent predeclared public direct playback reference; not a representative social/worker corpus',
      featureSchemaSha256:sha(await readFile(new URL('../../extension/vesper-inference.js',import.meta.url))),
      graphSha256:sha(await readFile(new URL('../../extension/vesper-evidence-graph.js',import.meta.url))),
      shippedModelSha256:sha(await readFile(new URL('../../extension/vesper-model.js',import.meta.url))),
      observations,labels,decisions,failures,coverage,metrics,...corpus};
    await writeFile(output,JSON.stringify(result,null,2)+'\n',{flag:'wx'});
    console.log(JSON.stringify({verified:labels.filter(l=>l.status==='verified').length,attempted:specs.length,coverage,failures:failures.length}));
  }finally{
    if(socket){try{await send('Browser.close')}catch{}socket.close()}
    if(proc&&proc.exitCode===null){proc.kill();await Promise.race([once(proc,'exit'),sleep(2000)]);}
    await rm(root,{recursive:true,force:true,maxRetries:8,retryDelay:300});
  }
}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){
  const [chrome,output,manifestPath]=process.argv.slice(2);
  if(!chrome||!output)throw Error('Specify isolated Chrome executable and new output JSON');
  await run(chrome,output,manifestPath?JSON.parse(await readFile(manifestPath,'utf8')):defaults);
}
