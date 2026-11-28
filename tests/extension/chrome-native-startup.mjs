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

const sessions = new Map(), calls = [], rows = [], requests = [];
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
  if (response.exceptionDetails) throw Error(response.exceptionDetails.text);
  return response.result?.value;
}
const allowed = new Set(['session-open','hello','capture-preflight','browser-capture','browser-submit','browser-transfer']);
try {
  await mkdir(chromeFolder,{recursive:true});
  await cp(join(root,'extension'),ext,{recursive:true});
  server = createServer(async (req,res) => {
    try {
      const url = new URL(req.url,'http://127.0.0.1');
      const name = url.searchParams.get('name');
      if (url.pathname === '/fixture' && /^fixture-\d+\.bin$/.test(name || '')) {
        res.writeHead(200,{'Content-Type':'text/html'});
        return res.end('<a id="download" href="/'+name+'">Download fixture</a>');
      }
      if (/^\/fixture-\d+\.bin$/.test(url.pathname) && ['GET','HEAD'].includes(req.method)) {
        requests.push({name:url.pathname.slice(1),method:req.method,range:!!req.headers.range});
        await delay(active?.delayMs || 0);
        const range = /^bytes=(\d+)-(\d*)$/.exec(req.headers.range || '');
        const from = range ? Number(range[1]) : 0;
        const to = range?.[2] ? Math.min(Number(range[2]),payload.length-1) : payload.length-1;
        if (from > to) { res.writeHead(416); return res.end(); }
        res.writeHead(range?206:200,{'Content-Type':'application/octet-stream','Content-Length':to-from+1,
          'Content-Disposition':'attachment; filename="'+url.pathname.slice(1)+'"','Accept-Ranges':'bytes','ETag':'"fixture-v1"',
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
      const host = sessions.get(key) || openHost(key);
      const reply = await host.send(message);
      calls.push({trial,type:message.type,operation:message.operation,ok:reply.ok,key,ms:performance.now()-started,
        jobId:reply.results?.[0]?.jobId});
      return json(reply);
    } catch (e) { res.writeHead(500); res.end(JSON.stringify({ok:false,code:'qa-error',message:String(e)})); }
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
    const p=pending.get(m.id); if(!p)return; pending.delete(m.id); clearTimeout(p.timer);
    if(m.error)p.reject(Error(m.error.message));else p.resolve(m.result||{});
  });
  const loaded=await send('Extensions.loadUnpacked',{path:ext});
  await send('Browser.setDownloadBehavior',{behavior:'allow',downloadPath:chromeFolder,eventsEnabled:true});
  const workerTarget=await until(async()=> (await send('Target.getTargets')).targetInfos.find(t=>t.type==='service_worker'&&t.url.includes(loaded.id)));
  const worker=(await send('Target.attachToTarget',{targetId:workerTarget.targetId,flatten:true})).sessionId;
  const target=(await send('Target.createTarget',{url:'about:blank'})).targetId;
  const page=(await send('Target.attachToTarget',{targetId:target,flatten:true})).sessionId;
  await send('Page.enable',{},page);
  for(let trial=0;trial<12;trial++){
    const mode=trial%2?'warm':'cold', delayMs=trial<6?0:80, name='fixture-'+trial+'.bin';
    active={trial,delayMs};
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
    rows.push({trial,mode,delayMs,jobId:stage.jobId,clickEpochMs,timing,handshakeMs:handshake?.ms??0,requests:http.length});
  }
  assert.equal(chromeDownloads,0);
  assert.deepEqual(await readdir(chromeFolder),[]);
  await writeFile(result,JSON.stringify({rows,chromeDownloads}));
} finally {
  if(socket){try{await send('Browser.close')}catch{}socket.close()}
  if(proc&&proc.exitCode===null){try{execFileSync('taskkill',['/PID',String(proc.pid),'/T','/F'],{stdio:'ignore'})}catch{}}
  for(const host of sessions.values()){host.child.stdin.end();if(host.child.exitCode===null)host.child.kill()}
  if(server){server.closeAllConnections();await new Promise(ok=>server.close(ok))}
  await rm(scratch,{recursive:true,force:true,maxRetries:8,retryDelay:200});
}
