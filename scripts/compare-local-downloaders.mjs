// Developer-only benchmark. No Chrome launch, remote network, installed config or real library access.
// Compares exact bytes and elapsed wall time, including both command-line processes' startup.
// Fixtures are deliberately tiny; these results are not internet speed or site coverage.
import { createServer } from 'node:http';
import { readFile, mkdtemp, mkdir, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { spawn, execFileSync } from 'node:child_process';
import { performance } from 'node:perf_hooks';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const media = join(root, 'tests','Vesper.Tests','Fixtures','Media');
const dll = join(root,'src','Vesper.MediaResolver.Benchmarks','bin','Release','net10.0','Vesper.MediaResolver.Benchmarks.dll');
const options = new Map(process.argv.slice(2).map(arg => {
  const match = /^--(json|baseline|trials)=(.+)$/.exec(arg);
  if (!match) throw new Error('Unknown option: '+arg);
  return [match[1],match[2]];
}));
const baseline=options.has('baseline')?JSON.parse(await readFile(options.get('baseline'),'utf8')):null;
const trials=Number(options.get('trials') ?? 3);
if (!Number.isInteger(trials)||trials<1||trials>20) throw new Error('trials must be 1–20');
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
const git=(...args)=>execFileSync('git',args,{cwd:root,encoding:'utf8',windowsHide:true}).trim();
const median=values=>{const sorted=[...values].sort((a,b)=>a-b), i=Math.floor(sorted.length/2);return sorted.length?(sorted.length%2?sorted[i]:(sorted[i-1]+sorted[i])/2):null;};
try{
  const version=await run('yt-dlp',['--ignore-config','--version']);
  if(version.code!==0)throw new Error('yt-dlp unavailable: '+version.stderr);
  const metadata={schema:1,track:'loopback-url-exact-bytes',utc:new Date().toISOString(),
    revision:git('rev-parse','HEAD'),trackedChanges:git('status','--porcelain','--untracked-files=no')!=='',
    benchmarkAssemblies:Object.fromEntries(await Promise.all((await readdir(dirname(dll))).filter(name=>name.endsWith('.dll')).sort().map(async name=>[name,sha(await readFile(join(dirname(dll),name)))]))),
    runnerSha256:sha(await readFile(fileURLToPath(import.meta.url))),ytDlpVersion:version.stdout.trim(),nodeVersion:process.version,
    fixtureBytes:source.length,fixtureSha256:expected,trials,cases:cases.map(([name,path])=>({name,path})),
    comparison:'loopback exact-source bytes; cold CLI; no live-site claims'};
  if(baseline && (baseline.metadata?.schema!==1 || baseline.metadata.track!==metadata.track ||
    baseline.metadata.fixtureSha256!==expected || baseline.metadata.trials!==trials ||
    baseline.metadata.ytDlpVersion!==metadata.ytDlpVersion ||
    JSON.stringify(baseline.metadata.cases)!==JSON.stringify(metadata.cases)))
    throw new Error('Baseline uses a different fixture, corpus, trial count or competitor version');
  console.log(JSON.stringify(metadata));
  for(const [name,path] of cases){
    for(let trial=0;trial<trials;trial++){
      for(const tool of (trial%2===0?['vesper','yt-dlp']:['yt-dlp','vesper'])){
        const dir=join(scratch,name+'-'+trial+'-'+tool);await mkdir(dir);
        requests=0;transferred=0;
        const r=tool==='vesper'
          ?await run('dotnet',[dll,'--url',base+path,'--local-fixture','--iterations','1','--download',dir])
          :await run('yt-dlp',['--ignore-config','--no-cache-dir','--no-playlist','--no-progress','--no-mtime','--no-warnings','--proxy','','-o',join(dir,'output.%(ext)s'),base+path]);
        const entries=await readdir(dir,{withFileTypes:true});
        // VESPER's isolated database is not a published output. A staging directory
        // is ignorable only when empty; leftover partial files still fail the check.
        const files=[];
        for(const entry of entries){
          if(tool==='vesper'&&entry.isDirectory()&&/^\.vesper-benchmark-[0-9a-f]{32}$/.test(entry.name)) continue;
          if(tool==='vesper'&&entry.isDirectory()&&entry.name==='.vesper-partials'&&!(await readdir(join(dir,entry.name))).length) continue;
          files.push(entry);
        }
        const exact=files.length===1&&files[0].isFile()&&!files[0].name.endsWith('.part')&&sha(await readFile(join(dir,files[0].name)))===expected;
        const completed=tool==='yt-dlp'?r.code===0:r.stdout.split(/\r?\n/).some(line=>{
          try{return JSON.parse(line).transfer==='Completed';}catch{return false;}
        });
        const success=r.code===0&&exact&&completed;
        const row={case:name,trial:trial+1,tool,exitCode:r.code,exact,completed,success,outputCount:files.length,elapsedMs:r.elapsedMs,requests,transferredBytes:transferred,
          diagnostic:success?undefined:(r.stderr||r.stdout).replaceAll(base,'[local]').replaceAll(scratch,'[scratch]').replaceAll(scratch.replaceAll('\\','\\\\'),'[scratch]').slice(-400)};
        results.push(row);console.log(JSON.stringify(row));
      }
    }
  }
  const summaries=[];
  for(const tool of ['vesper','yt-dlp'])for(const [name] of cases){
    const rows=results.filter(r=>r.tool===tool&&r.case===name), passed=rows.filter(r=>r.success);
    const summary={summary:true,tool,case:name,exact:passed.length,total:rows.length,
      medianMs:median(passed.map(r=>r.elapsedMs)),
      medianTransferredBytes:median(passed.map(r=>r.transferredBytes))};
    summaries.push(summary);console.log(JSON.stringify(summary));
  }
  const changes=baseline?results.filter(r=>r.tool==='vesper').map(row=>{
    const prior=baseline.results?.find(r=>r.tool===row.tool&&r.case===row.case&&r.trial===row.trial);
    if(!prior || typeof prior.success!=='boolean') throw new Error('Baseline missing a matching verified trial');
    return {case:row.case,trial:row.trial,before:prior.success,after:row.success,
      regression:prior.success&&!row.success,improvement:!prior.success&&row.success};
  }):[];
  const report={metadata,results,summaries,changes};
  if(options.has('json')) await writeFile(options.get('json'),JSON.stringify(report,null,2)+'\n',{flag:'wx'});
  if(changes.length) console.log(JSON.stringify({regressions:changes.filter(r=>r.regression),improvements:changes.filter(r=>r.improvement)}));
  if(changes.some(r=>r.regression)) process.exitCode=1;
}finally{
  await new Promise(ok=>server.close(ok));
  await rm(scratch,{recursive:true,force:true});
}
