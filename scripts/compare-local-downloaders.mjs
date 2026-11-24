// Developer-only benchmark. No Chrome launch, remote network, installed config or real library access.
// Compares exact bytes and elapsed wall time, including both command-line processes' startup.
// Fixtures are deliberately tiny; these results are not internet speed or site coverage.
import { createServer } from 'node:http';
import { readFile, mkdtemp, mkdir, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { performance } from 'node:perf_hooks';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const media = join(root, 'tests','Vesper.Tests','Fixtures','Media');
const dll = join(root,'src','Vesper.MediaResolver.Benchmarks','bin','Release','net10.0','Vesper.MediaResolver.Benchmarks.dll');
const source = Buffer.concat(await Promise.all(['av-init.mp4','av-1.m4s','av-2.m4s','av-3.m4s'].map(p=>readFile(join(media,p)))));
const sha = buffer => createHash('sha256').update(buffer).digest('hex');
const expected=sha(source);
const html = Buffer.from('<!doctype html><title>VESPER local fixture</title><video controls src="/asset.mp4"></video>');
const json = Buffer.from('<!doctype html><title>Structured player</title><script type="application/json" id="__NEXT_DATA__">'+JSON.stringify({props:{pageProps:{player:{sources:[{src:'/asset.mp4',type:'video/mp4'}]}}}})+'</script>');
const opaqueHtml=Buffer.from('<!doctype html><title>Opaque source</title><video controls src="/opaque-media?asset=42" type="video/mp4"></video>');
const cases=[['direct','/asset.mp4'],['html5','/html'],['structured-json','/json'],
  ['redirect','/redirect'],['extensionless','/opaque-media?asset=42'],
  ['extensionless-player','/opaque-page'],['delayed-direct','/delayed.mp4']];
let requests=0, transferred=0;
const server=createServer(async (req,res)=>{
  requests++;
  const path=new URL(req.url,'http://127.0.0.1').pathname;
  if(path==='/redirect'){res.writeHead(302,{Location:'/asset.mp4','Cache-Control':'no-store'}).end();return;}
  if(path==='/delayed.mp4') await new Promise(done=>setTimeout(done,150));
  const isMedia=['/asset.mp4','/opaque-media','/delayed.mp4'].includes(path);
  const data=isMedia?source:path==='/html'?html:path==='/json'?json:path==='/opaque-page'?opaqueHtml:null;
  if (!data || !['GET','HEAD'].includes(req.method)){res.writeHead(404).end();return;}
  const headers={'Content-Type':isMedia?'video/mp4':'text/html','Accept-Ranges':'bytes',
    'ETag':'"local-fixture-v1"','Cache-Control':'no-store'};
  let start=0,end=data.length-1,status=200;
  const match=/^bytes=(\d+)-(\d*)$/.exec(req.headers.range||'');
  if(match){
    start=Number(match[1]);end=match[2]?Math.min(end,Number(match[2])):end;
    if(start>end){res.writeHead(416,{'Content-Range':'bytes */'+data.length}).end();return;}
    status=206;headers['Content-Range']='bytes '+start+'-'+end+'/'+data.length;
  }
  const body=data.subarray(start,end+1);
  headers['Content-Length']=body.length;
  res.writeHead(status,headers);
  if(req.method==='HEAD')res.end();else{transferred+=body.length;res.end(body);}
});
await new Promise((ok,fail)=>{server.once('error',fail);server.listen(0,'127.0.0.1',ok);});
const base='http://127.0.0.1:'+server.address().port;
const scratch=await mkdtemp(join(tmpdir(),'vesper-comparison-'));
async function run(command,args){
  return new Promise(resolve=>{
    const start=performance.now();let stdout='',stderr='',done=false;
    const child=spawn(command,args,{cwd:root,windowsHide:true,stdio:['ignore','pipe','pipe'],env:{...process.env,NO_PROXY:'127.0.0.1',no_proxy:'127.0.0.1'}});
    const timer=setTimeout(()=>child.kill(),45000);
    const finish=code=>{if(done)return;done=true;clearTimeout(timer);resolve({code,elapsedMs:Math.round(performance.now()-start),stdout,stderr});};
    child.stdout.on('data',b=>{stdout=(stdout+b).slice(-100000);});child.stderr.on('data',b=>{stderr=(stderr+b).slice(-10000);});
    child.on('error',e=>{stderr+=e.message;finish(-1);});child.on('close',finish);
  });
}
const results=[];
try{
  const version=await run('yt-dlp',['--ignore-config','--version']);
  if(version.code!==0)throw new Error('yt-dlp unavailable: '+version.stderr);
  console.log(JSON.stringify({ytDlpVersion:version.stdout.trim(),fixtureBytes:source.length,fixtureSha256:expected,comparison:'loopback exact-source bytes; cold CLI; no live-site claims'}));
  for(const [name,path] of cases){
    for(let trial=0;trial<3;trial++){
      for(const tool of (trial%2===0?['vesper','yt-dlp']:['yt-dlp','vesper'])){
        const dir=join(scratch,name+'-'+trial+'-'+tool);await mkdir(dir);
        requests=0;transferred=0;
        const r=tool==='vesper'
          ?await run('dotnet',[dll,'--url',base+path,'--local-fixture','--iterations','1','--download',dir])
          :await run('yt-dlp',['--ignore-config','--no-cache-dir','--no-playlist','--no-progress','--no-mtime','--no-warnings','--proxy','','-o',join(dir,'output.%(ext)s'),base+path]);
        const files=(await readdir(dir,{withFileTypes:true})).filter(e=>e.isFile()&&!e.name.endsWith('.part'));
        const exact=files.length===1&&sha(await readFile(join(dir,files[0].name)))===expected;
        const row={case:name,trial:trial+1,tool,exitCode:r.code,exact,elapsedMs:r.elapsedMs,requests,transferredBytes:transferred,
          diagnostic:exact?undefined:(r.stderr||r.stdout).replaceAll(base,'[local]').slice(-400)};
        results.push(row);console.log(JSON.stringify(row));
      }
    }
  }
  for(const tool of ['vesper','yt-dlp'])for(const [name] of cases){
    const rows=results.filter(r=>r.tool===tool&&r.case===name), times=rows.filter(r=>r.exact).map(r=>r.elapsedMs).sort((a,b)=>a-b);
    console.log(JSON.stringify({summary:true,tool,case:name,exact:times.length,total:rows.length,medianMs:times.length?times[Math.floor(times.length/2)]:null}));
  }
}finally{
  await new Promise(ok=>server.close(ok));
  await rm(scratch,{recursive:true,force:true});
}
