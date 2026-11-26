// Development-only browser-measured fixture corpus. Never opens a user's profile or public URLs.
// Usage: node tools/intelligence/collect-browser-fixtures.mjs <chrome.exe> <new-output.json>
// Ground truth is specified by scenario authors BEFORE inference; no model prediction becomes a label.
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { fitSchema } from '../../scripts/train-intelligence.mjs';
const [chrome, output] = process.argv.slice(2);
if (!chrome || !output) throw Error('Specify an isolated Chromium executable and output JSON');
const media = await readFile(new URL('../../tests/Vesper.Tests/Fixtures/Media/muxed.webm', import.meta.url));
const root = await mkdtemp(join(tmpdir(), 'vesper-intent-fixture-'));
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const cases = [
  {group:'article',split:'train',chosen:'a',players:[[40,100,620,350,1,1,0],[710,130,300,170,0,1,1]]},
  {group:'hero-loop',split:'train',chosen:'b',players:[[20,20,1000,250,0,1,0],[280,300,560,350,1,1,0]],loop:'a'},
  {group:'clicked-small',split:'train',chosen:'b',players:[[30,40,700,420,1,1,0],[780,350,280,180,1,1,0]],interact:'b'},
  {group:'balanced-pair',split:'train',chosen:null,players:[[20,160,520,320,1,0,0],[580,160,520,320,1,0,0]]},
  {group:'watch-sidebar',split:'validation',chosen:'a',players:[[30,100,740,430,1,1,0],[800,100,250,150,0,0,0],[800,280,250,150,0,0,0]]},
  {group:'gallery',split:'validation',chosen:null,players:[[30,100,320,200,1,0,0],[390,100,320,200,1,0,0],[750,100,320,200,1,0,0]]},
  {group:'floating-mini',split:'holdout',chosen:'b',players:[[20,80,1000,390,0,1,0],[800,525,280,190,1,1,0]],loop:'a',interact:'b'},
  {group:'interrupted-program',split:'holdout',chosen:'a',players:[[50,120,620,370,1,0,0],[720,60,360,240,0,1,1]]},
  {group:'symmetric-reels',split:'holdout',chosen:null,players:[[140,80,360,640,1,1,0],[620,80,360,640,1,1,0]]}
];
const server = createServer((req,res) => {
  const u = new URL(req.url, 'http://127.0.0.1');
  if (u.pathname === '/clip.webm') {
    const range = /^bytes=(\d+)-(\d*)$/.exec(req.headers.range || '');
    const start = range ? Number(range[1]) : 0;
    const end = range?.[2] ? Math.min(Number(range[2]), media.length-1) : media.length-1;
    if(start>end){res.writeHead(416,{'Content-Range':'bytes */'+media.length}).end();return;}
    res.writeHead(range?206:200, {'Content-Type':'video/webm','Content-Length':end-start+1,'Accept-Ranges':'bytes',
      ...(range?{'Content-Range':'bytes '+start+'-'+end+'/'+media.length}:{})});
    res.end(media.subarray(start,end+1));return;
  }
  const c = cases[Number(u.searchParams.get('case'))], variant=Number(u.searchParams.get('variant'));
  if (!c || !Number.isInteger(variant) || variant<0 || variant>5) {res.writeHead(404).end();return;}
  let markup = c.players.map(([x,y,w,h,controls,playing,ad],i) => {
    const id=String.fromCharCode(97+i), loop=c.loop===id;
    const scale=1+(variant-2)*0.035;
    return '<video id="'+id+'" '+(controls?'controls ':'')+(playing?'autoplay ':'')+(loop?'loop ':'')+'muted preload="auto" '
      +(ad?'class="ad-slot" ':'')+'style="position:absolute;z-index:'+(c.interact===id?2:1)+';left:'+(x+variant*2)+'px;top:'+y+'px;width:'+Math.round(w*scale)+'px;height:'+Math.round(h*scale)+'px" src="/clip.webm?player='+id+'"></video>';
  });
  if(variant%2) markup.reverse();
  res.writeHead(200,{'Content-Type':'text/html;charset=utf-8'});
  res.end('<!doctype html><style>body{margin:0;background:black}video{object-fit:contain}</style>'+markup.join(''));
});
let proc, socket, serial=0, pending=new Map();
function send(method,params={},sessionId){
  return new Promise((ok,fail)=>{
    const id=++serial,timer=setTimeout(()=>{pending.delete(id);fail(Error(method+' timeout'))},6000);
    pending.set(id,{ok,fail,timer});socket.send(JSON.stringify({id,method,params,...(sessionId?{sessionId}:{})}));
  });
}
async function until(fn,ms){const end=Date.now()+ms;while(Date.now()<end){try{const v=await fn();if(v)return v;}catch{}await sleep(50)}throw Error('fixture deadline')}
const partitions={train:[],validation:[],holdout:[]}, failures=[];
try {
  server.listen(0,'127.0.0.1');await once(server,'listening');
  proc=spawn(chrome,['--headless=new','--no-first-run','--no-default-browser-check','--disable-background-networking',
    '--mute-audio','--autoplay-policy=no-user-gesture-required','--window-size=1120,800','--user-data-dir='+root,
    '--remote-debugging-port=0','about:blank'],{stdio:'ignore',windowsHide:true});
  const [port,path]=(await until(async()=>(await readFile(join(root,'DevToolsActivePort'),'utf8')).trim(),15000)).split(/\r?\n/);
  socket=new WebSocket('ws://127.0.0.1:'+port+path);
  await new Promise((ok,fail)=>{socket.addEventListener('open',ok,{once:true});socket.addEventListener('error',fail,{once:true})});
  socket.addEventListener('message',e=>{const m=JSON.parse(e.data),p=pending.get(m.id);if(!p)return;pending.delete(m.id);clearTimeout(p.timer);if(m.error)p.fail(Error(m.error.message));else p.ok(m.result||{})});
  const browser=(await send('Browser.getVersion')).product;
  const target=(await send('Target.createTarget',{url:'about:blank'})).targetId;
  const session=(await send('Target.attachToTarget',{targetId:target,flatten:true})).sessionId;
  const ev=async expression=>(await send('Runtime.evaluate',{expression,returnByValue:true},session)).result?.value;
  await send('Page.enable',{},session);
  for (const [i,c] of cases.entries()) for(let variant=0;variant<6;variant++){
    const url='http://127.0.0.1:'+server.address().port+'/?case='+i+'&variant='+variant;
    try{
      const nav=await send('Page.navigate',{url},session);if(nav.errorText)throw Error(nav.errorText);
      await until(()=>ev('location.href==='+JSON.stringify(url)+' && [...document.querySelectorAll("video")].length==='+c.players.length+' && [...document.querySelectorAll("video")].every(v=>v.readyState>=1)'),3500);
      if(c.interact){
        const pos=await ev('(()=>{const r=document.getElementById('+JSON.stringify(c.interact)+').getBoundingClientRect();return{x:r.left+15,y:r.top+15}})()');
        await ev('window.qaInteracted=null;document.addEventListener("pointerdown",e=>{if(e.isTrusted&&e.target.tagName==="VIDEO")window.qaInteracted=e.target.id},{capture:true});true');
        await send('Input.dispatchMouseEvent',{type:'mousePressed',...pos,button:'left',clickCount:1},session);
        await send('Input.dispatchMouseEvent',{type:'mouseReleased',...pos,button:'left',clickCount:1},session);
        if(await ev('window.qaInteracted')!==c.interact)throw Error('trusted target gesture was not observed');
      }
      const candidates=await ev(`[...document.querySelectorAll('video')].map(v=>{
        const b=v.getBoundingClientRect(),r={left:Math.max(0,b.left),right:Math.min(innerWidth,b.right),top:Math.max(0,b.top),bottom:Math.min(innerHeight,b.bottom)};
        const area=Math.max(0,r.right-r.left)*Math.max(0,r.bottom-r.top);
        return{id:v.id,area,visibleFraction:area/(b.width*b.height),centerDistance:Math.min(1,Math.hypot((r.left+r.right-innerWidth)/2,(r.top+r.bottom-innerHeight)/2)/Math.hypot(innerWidth/2,innerHeight/2)),
          playing:!v.paused&&!v.ended,ready:v.readyState>=1,progressed:v.currentTime>1,controls:v.controls,muted:v.muted,
          incidentalLoop:v.defaultMuted&&v.autoplay&&v.loop&&!v.controls,interacted:window.qaInteracted===v.id,
          duration:Number.isFinite(v.duration)?v.duration:null,declaredSource:!!v.getAttribute('src'),adContext:v.classList.contains('ad-slot')};
      }).filter(v=>v.area>0)`);
      if(!candidates?.length || c.chosen&&!candidates.some(v=>v.id===c.chosen))throw Error('fixture target missing');
      partitions[c.split].push({group:'browser-'+c.group,lineage:'browser-'+c.group,archetype:c.group,label:'fixture',chosen:c.chosen,candidates});
    }catch(e){failures.push({group:c.group,variant,error:String(e).slice(0,200)})}
  }
  const result=fitSchema('vesper.player-intent/2',partitions,'2.1.0-candidate');
  await writeFile(output,JSON.stringify({browser,provenance:'browser-measured controlled fixtures; not public-site ground truth',partitions,failures,result},null,2)+'\n',{flag:'wx'});
  console.log(JSON.stringify({sizes:result.sizes,failures,holdout:result.holdout,gates:result.gates,promotable:result.promotable}));
} finally {
  if(socket){try{await send('Browser.close')}catch{}socket.close()}
  if(proc&&proc.exitCode===null){proc.kill();await Promise.race([once(proc,'exit'),sleep(2000)]);}
  server.closeAllConnections();if(server.listening)await new Promise(ok=>server.close(ok));
  await rm(root,{recursive:true,force:true,maxRetries:8,retryDelay:300});
}
