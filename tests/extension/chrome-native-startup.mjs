// Opt-in, isolated real-browser/native-helper latency benchmark. No registry or
// installed extension changes. The loopback relay substitutes only Chrome's
// registered native port transport; the actual native-session.js remains intact.
import assert from 'node:assert/strict';
import { cp, mkdir, readFile, writeFile, rm, readdir } from 'node:fs/promises';
import { spawn, execFileSync } from 'node:child_process';
import { createServer } from 'node:http';
import { randomUUID } from 'node:crypto';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const { CHROME_PATH: chrome, VESPER_QA_HOST: helper, VESPER_QA_PIPE: pipe,
  VESPER_QA_RESULT: result, VESPER_QA_DOWNLOADS: downloads } = process.env;
assert(chrome && helper && result && downloads && /^vesper-qa-[a-f0-9]{32}$/.test(pipe || ''));
assert.equal(helper.split(/[\\/]/).at(-1), 'vesper-qa-host.exe');
const scratch = join(dirname(result), 'chrome');
const ext = join(scratch, 'extension'), profile = join(scratch, 'profile');
const chromeFolder = join(scratch, 'chrome-downloads');
const secret = randomUUID();
const payload = Buffer.alloc(256 * 1024);
for (let i = 0; i < payload.length; i++) payload[i] = (i * 31 + (i >> 8)) & 255;
const delay = ms => new Promise(r => setTimeout(r, ms));
async function until(fn, limit = 10000) {
  const end = Date.now() + limit;
  while (Date.now() < end) {
    const v = await fn();
    if (v) return v;
    await delay(25);
  }
  throw Error('Fixture deadline');
}

const sessions = new Map(), calls = [], rows = [], requests = [], mediaRows = [], retainedRows = [], relayEvents = [], browserErrors = [];
const publicMedia = process.env.VESPER_QA_PUBLIC_MEDIA === '1';
const retainedMedia = process.env.VESPER_QA_RETAINED_MEDIA === '1';
const publicCampaign = publicMedia && !!process.env.VESPER_QA_PUBLIC_CASES;
const downloadVariants = process.env.VESPER_QA_DOWNLOAD_VARIANTS === '1';
const publicArchive = process.env.VESPER_QA_PUBLIC_ARCHIVE === '1';
assert([publicMedia,retainedMedia,publicArchive].filter(Boolean).length<=1);
const archiveAddresses = ['https://github.com/octocat/Hello-World/archive/refs/heads/master.zip',
  'https://codeload.github.com/octocat/Hello-World/zip/7fd1a60b01f91b314f59955a4e4d4e80d8edf11d'];
const archiveRows=[];
const retainedBytes = retainedMedia ? await readFile(join(root,'tests/Vesper.Tests/Fixtures/Media/instinct-mse.mp4')) : null;
let proc, socket, server, seq = 0, active, chromeDownloads = 0;
const pending = new Map();
function openHost(key) {
  const child = spawn(helper, [], { windowsHide: true, env: {...process.env, VESPER_QA_PIPE:pipe}, stdio:['pipe','pipe','pipe'] });
  const waiting = new Map();
  let buffer = Buffer.alloc(0);
  child.stderr.resume();
  const fail = e => { for (const p of waiting.values()) { clearTimeout(p.timer); p.reject(e); } waiting.clear(); };
  child.on('error', fail);
  child.on('close', () => fail(Error('QA native helper closed')));
  child.stdout.on('data', chunk => {
    buffer = Buffer.concat([buffer, chunk]);
    while (buffer.length >= 4) {
      const len = buffer.readUInt32LE(0);
      if (len > 1024 * 1024) { child.kill(); return; }
      if (buffer.length < len + 4) return;
      const reply = JSON.parse(buffer.subarray(4, len + 4));
      buffer = buffer.subarray(len + 4);
      const p = waiting.get(reply.id);
      if (p) { waiting.delete(reply.id); clearTimeout(p.timer); p.resolve(reply); }
    }
  });
  const host = { child, send(message) {
    return new Promise((resolve, reject) => {
      if (waiting.size >= 16 || waiting.has(message.id)) return reject(Error('QA host busy'));
      const bytes = Buffer.from(JSON.stringify(message)), header = Buffer.alloc(4);
      header.writeUInt32LE(bytes.length);
      const timer = setTimeout(() => { waiting.delete(message.id); reject(Error('QA native deadline')); }, 20000);
      waiting.set(message.id, {resolve,reject,timer});
      child.stdin.write(Buffer.concat([header, bytes]));
    });
  }};
  sessions.set(key, host);
  return host;
}
function send(method, params = {}, sessionId) {
  return new Promise((resolve, reject) => {
    const id = ++seq, timer = setTimeout(() => { pending.delete(id); reject(Error(method+' deadline')); }, 10000);
    pending.set(id,{resolve,reject,timer});
    socket.send(JSON.stringify({id,method,params,...(sessionId ? {sessionId} : {})}));
  });
}
async function evaluate(sessionId, expression) {
  const response = await send('Runtime.evaluate', {expression,returnByValue:true,awaitPromise:true}, sessionId);
  if (response.exceptionDetails) throw Error(response.exceptionDetails.exception?.description || response.exceptionDetails.text);
  return response.result?.value;
}
const allowed = new Set(['session-open','hello','capture-preflight','browser-capture','browser-submit','browser-transfer','browser-diagnostics']);
if (publicMedia || retainedMedia) for (const type of ['media-options','media-source-options','media-select','media-selection-status','browser-diagnostics','browser-body']) allowed.add(type);
try {
  await mkdir(chromeFolder,{recursive:true});
  await cp(join(root,'extension'),ext,{recursive:true});
  server = createServer(async (req,res) => {
    try {
      const url = new URL(req.url,'http://127.0.0.1');
      const name = url.searchParams.get('name');
      if (retainedMedia && url.pathname === '/retained-sw.js') {
        res.writeHead(200,{'Content-Type':'text/javascript','Cache-Control':'no-store'});
        return res.end("self.addEventListener('install',e=>e.waitUntil(self.skipWaiting()));self.addEventListener('activate',e=>e.waitUntil(self.clients.claim()));");
      }
      if (retainedMedia && url.pathname === '/retained-mse') {
        res.writeHead(200,{'Content-Type':'text/html','Cache-Control':'no-store'});
        return res.end(`<!doctype html><title>Retained playback fixture</title><video width="640" height="360" controls muted autoplay></video><script>
          window.retained={ready:false,error:null};
          (async()=>{
            // The real service-worker guard takes the browser-context route;
            // never relax the native resolver's private-network policy.
            await navigator.serviceWorker.register('/retained-sw.js');
            await navigator.serviceWorker.ready;
            if(!navigator.serviceWorker.controller)await new Promise(resolve=>navigator.serviceWorker.addEventListener('controllerchange',resolve,{once:true}));
            const video=document.querySelector('video'),ms=new MediaSource();
            video.src=URL.createObjectURL(ms);
            await new Promise(resolve=>ms.addEventListener('sourceopen',resolve,{once:true}));
            const track=ms.addSourceBuffer('video/mp4; codecs="avc1.42c00a,mp4a.40.2"');
            const bytes=Uint8Array.from(atob(${JSON.stringify(retainedBytes.toString('base64'))}),c=>c.charCodeAt(0));
            // Actual browser parser receives split MP4 box headers and data.
            for(let start=0;start<bytes.length;){
              const length=start<24?7:1531;
              const chunk=bytes.subarray(start,start+length);start+=chunk.length;
              await new Promise((resolve,reject)=>{track.addEventListener('updateend',resolve,{once:true});track.addEventListener('error',reject,{once:true});track.appendBuffer(chunk);});
            }
            ms.endOfStream();retained.ready=true;
          })().catch(e=>retained.error=String(e));
        </script>`);
      }
      if (publicArchive && url.pathname === '/archive-fixture') {
        const address=archiveAddresses[Number(url.searchParams.get('case'))];
        if(!address){res.writeHead(404);return res.end();}
        res.writeHead(200,{'Content-Type':'text/html','Cache-Control':'no-store'});
        return res.end('<a id="download" href="'+address+'">Download ZIP</a>');
      }
      if (url.pathname === '/fixture' && /^fixture-\d+\.bin$/.test(name || '')) {
        res.writeHead(200,{'Content-Type':'text/html'});
        if(active?.scenario==='labelled')
          return res.end('<a id="download" href="/download?id='+name+'">Download file</a>');
        if(active?.scenario==='delayed')
          return res.end('<button id="download">Export file</button><script>document.querySelector("#download").addEventListener("click",()=>setTimeout(()=>{const a=document.createElement("a");a.href="/export?file='+name+'";a.download="'+name+'";a.click()},500));</script>');
        return res.end('<a id="download" href="/'+name+'">Download fixture</a>');
      }
      const fileName = url.pathname === '/download' ? url.searchParams.get('id')
        : url.pathname === '/export' ? url.searchParams.get('file') : url.pathname.slice(1);
      if (/^fixture-\d+\.bin$/.test(fileName || '') && ['GET','HEAD'].includes(req.method)) {
        requests.push({name:fileName,method:req.method,range:!!req.headers.range});
        await delay(active?.delayMs || 0);
        const range = /^bytes=(\d+)-(\d*)$/.exec(req.headers.range || '');
        const from = range ? Number(range[1]) : 0;
        const to = range?.[2] ? Math.min(Number(range[2]),payload.length-1) : payload.length-1;
        if (from > to) { res.writeHead(416); return res.end(); }
        res.writeHead(range?206:200,{'Content-Type':'application/octet-stream','Content-Length':to-from+1,
          'Content-Disposition':'attachment; filename="'+fileName+'"','Accept-Ranges':'bytes','ETag':'"fixture-v1"',
          ...(range?{'Content-Range':'bytes '+from+'-'+to+'/'+payload.length}:{})});
        return res.end(req.method === 'HEAD' ? undefined : payload.subarray(from,to+1));
      }
      if (req.method !== 'POST' || url.pathname !== '/'+secret) { res.writeHead(404); return res.end(); }
      const chunks = []; let size = 0;
      for await (const chunk of req) { size += chunk.length; if(size>1024*1024){res.writeHead(413);return res.end();} chunks.push(chunk); }
      const {key,message,close} = JSON.parse(Buffer.concat(chunks).toString());
      if (!/^[a-f0-9-]{36}$/.test(key)) { res.writeHead(400); return res.end(); }
      const json = value => { res.writeHead(200,{'Content-Type':'application/json','Cache-Control':'no-store'}); res.end(JSON.stringify(value)); };
      if (close) { sessions.get(key)?.child.stdin.end(); sessions.delete(key); return json({ok:true}); }
      if (!allowed.has(message?.type)) return json({id:message?.id,ok:false,code:'qa-denied'});
      const started = performance.now(), trial = active?.trial;
      relayEvents.push({trial,type:message.type,stage:'received'});
      const host = sessions.get(key) || openHost(key);
      const reply = await host.send(message);
      calls.push({trial,type:message.type,operation:message.operation,ok:reply.ok,key,ms:performance.now()-started,
        jobId:reply.results?.[0]?.jobId||reply.data?.jobId,code:reply.code});
      return json(reply);
    } catch (e) { relayEvents.push({trial:active?.trial,stage:'error',message:String(e)}); res.writeHead(500); res.end(JSON.stringify({ok:false,code:'qa-error',message:String(e)})); }
  });
  await new Promise(ok=>server.listen(0,'127.0.0.1',ok));
  const base = 'http://127.0.0.1:'+server.address().port, relay=base+'/'+secret;
  // This prelude is test-only. Session negotiation, queueing and deadlines
  // continue through the unmodified production VesperNativeSession.
  const prelude = '(()=>{const endpoint='+JSON.stringify(relay)+
    ';const exchange=async body=>(await fetch(endpoint,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(body)})).json();'+
    'chrome.runtime.connectNative=()=>{const key=crypto.randomUUID(),receive=[],lost=[];let closed=false;return {onMessage:{addListener:f=>receive.push(f)},onDisconnect:{addListener:f=>lost.push(f)},disconnect(){if(closed)return;closed=true;void exchange({key,close:true}).catch(()=>{});lost.forEach(f=>f())},postMessage(message){void exchange({key,message}).then(r=>{if(!closed)receive.forEach(f=>f(r))},()=>{if(!closed){closed=true;lost.forEach(f=>f())}})}}};'+
    'chrome.runtime.sendNativeMessage=(_host,message,cb)=>{const key=crypto.randomUUID();void exchange({key,message}).then(cb,()=>cb({ok:false,code:"qa-error"})).finally(()=>exchange({key,close:true}).catch(()=>{}))};})();\n';
  const nativePath=join(ext,'native-session.js');
  await writeFile(nativePath,prelude+await readFile(nativePath,'utf8'));
  const backgroundPath=join(ext,'background.js');
  // Target discovery can precede completion of importScripts and top-level initialization.
  // Signal only after the copied production worker has fully evaluated.
  await writeFile(backgroundPath,(await readFile(backgroundPath,'utf8'))+'\n;globalThis.__vesperQaReady=true;\n');
  proc=spawn(chrome,['--headless=new','--enable-unsafe-extension-debugging','--no-first-run','--no-default-browser-check',
    '--disable-background-networking','--disable-default-apps','--window-size=1100,800','--user-data-dir='+profile,
    '--remote-debugging-port=0','--remote-allow-origins=*','about:blank'],{stdio:'ignore',windowsHide:true});
  const info=await until(async()=>{try{return (await readFile(join(profile,'DevToolsActivePort'),'utf8')).trim()}catch{return null}},15000);
  const [port,ws]=info.split(/\r?\n/);
  socket=new WebSocket('ws://127.0.0.1:'+port+ws);
  await new Promise((ok,fail)=>{socket.addEventListener('open',ok,{once:true});socket.addEventListener('error',fail,{once:true})});
  socket.addEventListener('message',e=>{
    const m=JSON.parse(e.data);
    if(m.method==='Browser.downloadWillBegin') chromeDownloads++;
    if(m.method==='Runtime.exceptionThrown' && browserErrors.length<30)
      browserErrors.push(m.params.exceptionDetails?.exception?.description||m.params.exceptionDetails?.text);
    const p=pending.get(m.id); if(!p)return; pending.delete(m.id); clearTimeout(p.timer);
    if(m.error)p.reject(Error(m.error.message));else p.resolve(m.result||{});
  });
  const loaded=await send('Extensions.loadUnpacked',{path:ext});
  await send('Browser.setDownloadBehavior',{behavior:'allow',downloadPath:chromeFolder,eventsEnabled:true});
  const workerTarget=await until(async()=> (await send('Target.getTargets')).targetInfos.find(t=>t.type==='service_worker'&&t.url.includes(loaded.id)));
  const worker=(await send('Target.attachToTarget',{targetId:workerTarget.targetId,flatten:true})).sessionId;
  await send('Runtime.enable',{},worker);
  await until(()=>evaluate(worker,'globalThis.__vesperQaReady===true'),15000);
  const preflight=await evaluate(worker,"nativeRequest(envelope('hello'),12000)");
  assert(preflight?.ok,'QA transport preflight failed: '+JSON.stringify({preflight,relayEvents,browserErrors}));
  const diagnostic=await evaluate(worker,"recordFailure('media-failed','qa-controlled-source','https://www.facebook.com/reel/123456789?token=SECRET','blob:https://www.facebook.com/private',{stage:'formats',contentBuild:'2.0.0-aaa.30',objectKind:'MediaSourceHandle',serviceWorker:true,witnessPaths:10,dispatchMs:8,probeMs:15,acceptMs:42,cookie:'SECRET'})");
  assert(diagnostic?.id);
  await evaluate(worker,'exportPendingFailures()');
  await until(()=>evaluate(worker,'(async()=>((await chrome.storage.local.get("vesperFailureLog")).vesperFailureLog||[]).some(e=>e.id==='+JSON.stringify(diagnostic.id)+'&&e.exported))()'),15000);
  await evaluate(worker,'exportPendingFailures()');
  await evaluate(worker,'VesperNativeSession.close()');
  calls.length=0;
  relayEvents.length=0;
  const target=(await send('Target.createTarget',{url:'about:blank'})).targetId;
  const page=(await send('Target.attachToTarget',{targetId:target,flatten:true})).sessionId;
  await send('Page.enable',{},page);
  await send('Runtime.enable',{},page);
  for(let trial=0;trial<(publicMedia||retainedMedia||publicArchive?0:12);trial++){
    const mode=trial%2?'warm':'cold', delayMs=trial<6?0:80, name='fixture-'+trial+'.bin';
    const scenario=downloadVariants?['direct','labelled','delayed'][trial%3]:'direct';
    active={trial,delayMs,scenario};
    await send('Page.navigate',{url:base+'/fixture?name='+name},page);
    await until(()=>evaluate(page,'!!document.querySelector("#download")'));
    await delay(250);
    const previous=await evaluate(worker,'(async()=>((await chrome.storage.local.get("vesperStartupTimings")).vesperStartupTimings||[]).length)()');
    if(mode==='cold'){
      await evaluate(worker,'VesperNativeSession.close()');
      await delay(100);
    } else assert.equal(await evaluate(worker,'VesperNativeSession.snapshot().connected'),true);
    await evaluate(page,'window.qaClickEpochMs=0;document.addEventListener("click",()=>window.qaClickEpochMs=Date.now(),{capture:true,once:true})');
    const rect=await evaluate(page,'(()=>{const r=document.querySelector("#download").getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()');
    await send('Input.dispatchMouseEvent',{type:'mouseMoved',...rect},page);
    await send('Input.dispatchMouseEvent',{type:'mousePressed',button:'left',clickCount:1,...rect},page);
    await send('Input.dispatchMouseEvent',{type:'mouseReleased',button:'left',clickCount:1,...rect},page);
    const clickEpochMs=await evaluate(page,'window.qaClickEpochMs');
    assert(clickEpochMs>0);
    const timing=await until(async()=>{
      const log=await evaluate(worker,'(async()=>((await chrome.storage.local.get("vesperStartupTimings")).vesperStartupTimings||[]))()');
      return log.length>previous?log.at(-1).facts:null;
    },15000);
    await until(async()=>{try{return (await readFile(join(downloads,name))).equals(payload)}catch{return false}},15000);
    const trialCalls=calls.filter(c=>c.trial===trial);
    const stage=trialCalls.find(c=>['browser-submit','browser-capture'].includes(c.type)&&c.ok);
    assert(stage?.jobId);
    const accept=trialCalls.find(c=>c.type==='browser-transfer'&&c.operation==='accept'&&c.ok);
    assert(accept && accept.key===stage.key);
    const handshake=trialCalls.find(c=>c.type==='session-open'&&c.key===stage.key);
    assert.equal(!!handshake,mode==='cold','cold/warm classification must be observed, not assumed');
    const http=requests.filter(r=>r.name===name);
    rows.push({trial,mode,delayMs,scenario,jobId:stage.jobId,clickEpochMs,timing,handshakeMs:handshake?.ms??0,requests:http.length});
  }
  if(publicArchive){
    for(const [trial,address] of archiveAddresses.entries()){
      active={trial,delayMs:0};
      const prior=(await readdir(downloads)).length;
      await send('Page.navigate',{url:base+'/archive-fixture?case='+trial},page);
      await until(()=>evaluate(page,'!!document.querySelector("#download")'));
      await delay(350);
      const rect=await evaluate(page,'(()=>{const r=document.querySelector("#download").getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()');
      await send('Input.dispatchMouseEvent',{type:'mouseMoved',...rect},page);
      await send('Input.dispatchMouseEvent',{type:'mousePressed',button:'left',clickCount:1,...rect},page);
      await send('Input.dispatchMouseEvent',{type:'mouseReleased',button:'left',clickCount:1,...rect},page);
      const staged=await until(()=>calls.find(c=>c.trial===trial&&c.type==='browser-capture'&&c.ok&&c.jobId),20000);
      await until(()=>calls.some(c=>c.trial===trial&&c.type==='browser-transfer'&&c.operation==='accept'&&c.ok),20000);
      await until(async()=> (await readdir(downloads)).length>prior,20000);
      archiveRows.push({trial,address,jobId:staged.jobId});
    }
  }
  if(publicMedia||retainedMedia){
    // These exact public sample URLs are independently referenced by the
    // C# test. Do not infer the expected source from VESPER's own result.
    const addresses=publicCampaign ? JSON.parse((await readFile(process.env.VESPER_QA_PUBLIC_CASES,'utf8')).replace(/^\uFEFF/,'')) :
      ['https://mdn.github.io/shared-assets/videos/flower.mp4','https://mdn.github.io/shared-assets/videos/flower.webm'];
    assert(Array.isArray(addresses)&&addresses.length>0&&addresses.length<=24&&new Set(addresses).size===addresses.length);
    for(const address of addresses){
      const u=new URL(address);
      assert(u.protocol==='https:'&&!u.username&&!u.password&&!u.search&&!u.hash&&!u.port);
    }
    async function button(className){
      return until(async()=>{
        const {root:dom}=await send('DOM.getDocument',{depth:-1,pierce:true},page);
        const nodes=[dom];
        for(const node of nodes){
          nodes.push(...(node.children||[]),...(node.shadowRoots||[]));
          const i=node.attributes?.indexOf('class')??-1;
          if(node.nodeName==='BUTTON'&&i>=0&&node.attributes[i+1].split(' ').includes(className)){
            try{
              const {object}=await send('DOM.resolveNode',{nodeId:node.nodeId},page);
              const {result}=await send('Runtime.callFunctionOn',{objectId:object.objectId,functionDeclaration:'function(){const r=this.getBoundingClientRect();const s=getComputedStyle(this);return !this.disabled&&!this.hidden&&s.visibility==="visible"&&s.display!=="none"&&Number(s.opacity)>0&&s.pointerEvents!=="none"&&r.width>0&&r.height>0&&(this.classList.contains("fly")?this.classList.contains("show"):true)}',returnByValue:true},page);
              await send('Runtime.releaseObject',{objectId:object.objectId},page);
              if(!result.value)continue;
              const {model}=await send('DOM.getBoxModel',{nodeId:node.nodeId},page);
              const x=(model.border[0]+model.border[4])/2,y=(model.border[1]+model.border[5])/2;
              if(x>0&&y>0)return {x,y};
            }catch{}
          }
        }
      },20000);
    }
    async function click(className){
      let rect=await button(className);
      await send('Input.dispatchMouseEvent',{type:'mouseMoved',...rect},page);
      await new Promise(resolve=>setTimeout(resolve,100));
      rect=await button(className);
      await send('Input.dispatchMouseEvent',{type:'mousePressed',button:'left',clickCount:1,...rect},page);
      await send('Input.dispatchMouseEvent',{type:'mouseReleased',button:'left',clickCount:1,...rect},page);
    }
    async function selectOriginal(){
      // Exercise the actual format control. No direct native submission or
      // source returned by VESPER is used as the independent reference.
      const {root:dom}=await send('DOM.getDocument',{depth:-1,pierce:true},page);
      const nodes=[dom];
      for(const node of nodes){
        nodes.push(...(node.children||[]),...(node.shadowRoots||[]));
        if(node.nodeName!=='SELECT')continue;
        let object;
        try{
          ({object}=await send('DOM.resolveNode',{nodeId:node.nodeId},page));
          const reply=await send('Runtime.callFunctionOn',{objectId:object.objectId,
            functionDeclaration:'function(){if(!this.closest(".output")||this.closest(".output").hidden)return false;const index=[...this.options].findIndex(o=>/^Original (?:audio|format)$/.test(o.textContent));if(index<0)return false;this.selectedIndex=index;this.dispatchEvent(new Event("change",{bubbles:true}));return true}',returnByValue:true},page);
          if(reply.result?.value===true)return;
        }catch{}finally{if(object)await send('Runtime.releaseObject',{objectId:object.objectId},page).catch(()=>{});}
      }
    }
    for(const [trial,address] of (publicMedia?addresses:[]).entries()){
      active={trial,delayMs:0};
      let stage='navigate';
      try{
      const nav=await send('Page.navigate',{url:address},page);
      assert(!nav.errorText,nav.errorText);
      await send('Page.bringToFront',{},page);
      stage='playback';
      const playback=await until(()=>evaluate(page,'(()=>{const v=document.querySelector("video,audio");return v?.readyState>=1&&v.currentSrc==='+JSON.stringify(address)+'?{src:v.currentSrc,duration:v.duration,width:v.videoWidth||0,height:v.videoHeight||0}:null})()'),15000);
      assert(playback.duration>0&&Number.isFinite(playback.duration));
      stage='picker';
      await click('fly');
      try {
        await button('primary');
        if(publicCampaign)await selectOriginal();
        stage='submission';
        await click('primary');
      }
      catch (error) {
        const {root:dom}=await send('DOM.getDocument',{depth:-1,pierce:true},page);
        const nodes=[dom];
        for(const node of nodes) nodes.push(...(node.children||[]),...(node.shadowRoots||[]));
        const messages=nodes.filter(n=>n.nodeName==='#text' && /VESPER|media|format|download|stream|expired/i.test(n.nodeValue))
          .map(n=>n.nodeValue).filter(v=>v.length<=200);
        throw Error(error.message+' '+JSON.stringify({messages,calls:calls.map(({key,...row})=>row)}));
      }
      const submitted=await until(()=>calls.find(c=>c.trial===trial&&c.ok&&c.jobId
        &&(c.type==='media-select'||(c.type==='browser-body'&&c.operation==='finish'))),30000);
      mediaRows.push({trial,address,status:'submitted',jobId:submitted.jobId,route:submitted.type,playback});
      }catch(error){
        if(!publicCampaign)throw error;
        mediaRows.push({trial,address,status:'failed',stage,reason:String(error.message).slice(0,1200),
          codes:calls.filter(c=>c.trial===trial).map(c=>({type:c.type,code:c.code,ok:c.ok})).slice(-12)});
      }
      if(publicCampaign)console.log(JSON.stringify({trial,status:mediaRows.at(-1).status,stage:mediaRows.at(-1).stage}));
    }
    if(retainedMedia){
      active={trial:0,delayMs:0};
      await send('Page.navigate',{url:base+'/retained-mse'},page);
      await send('Page.bringToFront',{},page);
      const playback=await until(()=>evaluate(page,`(()=>{
        if(window.retained?.error)return {error:retained.error};
        const v=document.querySelector('video');
        if(!window.retained?.ready||v?.readyState<1)return null;
        v.removeAttribute('data-vesper-media-provenance');
        v.dispatchEvent(new Event('vesper-media-provenance-request',{bubbles:true}));
        const witness=JSON.parse(v.getAttribute('data-vesper-media-provenance')||'null');
        return {duration:v.duration,width:v.videoWidth,height:v.videoHeight,witness};
      })()`),15000);
      assert(!playback.error,JSON.stringify(playback));
      assert(playback.witness?.tracks?.length===1&&playback.witness.tracks[0].bufferedComplete,JSON.stringify(playback));
      assert.deepEqual(playback.witness.paths,[]);
      await click('fly');await click('primary');
      const accepted=await until(()=>calls.find(c=>c.type==='browser-body'&&c.operation==='finish'&&c.ok&&c.jobId),20000);
      assert.equal(calls.filter(c=>c.type==='browser-body'&&c.operation==='begin').length,1);
      assert.equal(calls.filter(c=>c.type==='media-select').length,0);
      retainedRows.push({jobId:accepted.jobId,duration:playback.duration,width:playback.width,height:playback.height,
        bytes:retainedBytes.length});
    }
  }
  if(!publicCampaign){
    assert.equal(chromeDownloads,0);
    assert.deepEqual(await readdir(chromeFolder),[]);
  }
  await writeFile(result,JSON.stringify({rows,mediaRows,retainedRows,archiveRows,chromeDownloads,diagnosticId:diagnostic.id}));
} catch(error) {
  throw Error(error.message+' '+JSON.stringify({relayEvents:relayEvents.slice(-30),browserErrors,chromeDownloads,calls:calls.slice(-30).map(({key,...row})=>row)}),{cause:error});
} finally {
  if(socket){try{await send('Browser.close')}catch{}socket.close()}
  if(proc&&proc.exitCode===null){try{execFileSync('taskkill',['/PID',String(proc.pid),'/T','/F'],{stdio:'ignore'})}catch{}}
  for(const host of sessions.values()){host.child.stdin.end();if(host.child.exitCode===null)host.child.kill()}
  if(server){server.closeAllConnections();await new Promise(ok=>server.close(ok))}
  try { await rm(scratch,{recursive:true,force:true,maxRetries:16,retryDelay:200}); }
  catch(error) { console.error('QA scratch cleanup deferred: '+error.code); }
}
