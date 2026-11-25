// Developer-only, public URL discovery comparison. No download, browser profile, cookies or config.
// Usage: node scripts/compare-live-discovery.mjs <corpus.json> <gitignored-output.json>
// Corpus: [{id, family, url}]. Discovery is not identity verification or acquisition.
import { readFile, writeFile, readdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { spawn, execFileSync } from 'node:child_process';
import { performance } from 'node:perf_hooks';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
const dll=join(root,'src','Vesper.MediaResolver.Benchmarks','bin','Release','net10.0','Vesper.MediaResolver.Benchmarks.dll');
const [input,output]=process.argv.slice(2);
if(!input||!output||process.argv.length!==4) throw new Error('Usage: compare-live-discovery.mjs <corpus.json> <new-output.json>');
const hash=bytes=>createHash('sha256').update(bytes).digest('hex');
const bytes=await readFile(input);
const corpus=JSON.parse(bytes);
if(!Array.isArray(corpus)||!corpus.length||corpus.length>500) throw new Error('Expected 1–500 explicit public cases');
const ids=new Set();
for(const c of corpus){
  if(typeof c.id!=='string'||!c.id||ids.has(c.id)||typeof c.family!=='string'||!c.family) throw new Error('Invalid or duplicate case ID');
  const url=new URL(c.url);
  if(url.protocol!=='https:'||url.username||url.password) throw new Error('Use public HTTPS URLs without credentials');
  ids.add(c.id);
}
const git=(...args)=>execFileSync('git',args,{cwd:root,encoding:'utf8',windowsHide:true}).trim();
function run(command,args,limitMs=25000){
  return new Promise(done=>{
    const started=performance.now();
    let chunks=[],size=0,timedOut=false,overflow=false,spawnError=false;
    const p=spawn(command,args,{cwd:root,windowsHide:true,stdio:['ignore','pipe','ignore']});
    const timer=setTimeout(()=>{timedOut=true;p.kill();},limitMs);
    p.stdout.on('data',chunk=>{
      size+=chunk.length;
      if(size>8*1024*1024){overflow=true;chunks=[];p.kill();return;}
      if(!overflow) chunks.push(chunk);
    });
    p.on('error',()=>{spawnError=true;});
    p.on('close',code=>{clearTimeout(timer);done({code,timedOut,overflow,spawnError,elapsedMs:Math.round(performance.now()-started),
      stdout:overflow?'':Buffer.concat(chunks).toString('utf8')});});
  });
}
function parsed(run){
  if(run.timedOut||run.overflow||run.spawnError||run.code!==0) return null;
  try{return JSON.parse(run.stdout.trim());}catch{return null;}
}
const version=await run('yt-dlp',['--ignore-config','--version']);
if(version.code!==0) throw new Error('yt-dlp is unavailable');
const metadata={schema:1,track:'public-url-discovery-only',utc:new Date().toISOString(),revision:git('rev-parse','HEAD'),
  trackedChanges:git('status','--porcelain','--untracked-files=no')!=='',
  runnerSha256:hash(await readFile(fileURLToPath(import.meta.url))),corpusSha256:hash(bytes),
  ytDlpVersion:version.stdout.trim(),nodeVersion:process.version,
  benchmarkAssemblies:Object.fromEntries(await Promise.all((await readdir(dirname(dll))).filter(n=>n.endsWith('.dll')).sort().map(async n=>[n,hash(await readFile(join(dirname(dll),n)))]))),
  authentication:'none',verification:'format metadata only; media identity and acquisition unverified'};
const results=[];
for(const [i,c] of corpus.entries()){
  for(const tool of i%2?['yt-dlp','vesper']:['vesper','yt-dlp']){
    const r=tool==='vesper'
      ?await run('dotnet',[dll,'--url',c.url,'--iterations','1','--cancel-ms','20000'])
      :await run('yt-dlp',['--ignore-config','--no-cache-dir','--skip-download','--no-playlist','--no-warnings','--socket-timeout','8','--retries','0','--extractor-retries','0','--dump-single-json','--',c.url]);
    const data=parsed(r);
    // Counts use each tool's own enumeration semantics; do not equate a format count to exact quality coverage.
    const formats=tool==='vesper'?data?.representations:Array.isArray(data?.formats)?data.formats.filter(f=>f.url&&f.vcodec!=='images'&&f.protocol!=='mhtml').length:0;
    const status=r.timedOut?'timeout':r.overflow?'output-limit':r.spawnError?'unavailable':r.code!==0?'error':!data?'invalid-output':
      tool==='vesper'&&data.status==='cancelled'?'timeout':Number.isInteger(formats)&&formats>0?'formats-observed':'no-formats';
    const row={id:c.id,family:c.family,tool,status,formats:status==='formats-observed'?formats:0,
      elapsedMs:r.elapsedMs,...(tool==='vesper'&&data?{resolverStatus:data.status,resolverError:data.error,firstRepresentationMs:data.firstSourceRepresentationMs,networkRequests:data.networkRequests}:{}),
      acquisitionVerified:false};
    results.push(row);console.log(JSON.stringify(row));
  }
}
const summary=Object.fromEntries(['vesper','yt-dlp'].map(tool=>[tool,{
  attempts:results.filter(r=>r.tool===tool).length,formatsObserved:results.filter(r=>r.tool===tool&&r.status==='formats-observed').length,
  timeouts:results.filter(r=>r.tool===tool&&r.status==='timeout').length}]));
await writeFile(output,JSON.stringify({metadata,results,summary},null,2)+'\n',{flag:'wx'});
console.log(JSON.stringify(summary));
