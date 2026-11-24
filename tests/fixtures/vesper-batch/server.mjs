// Disposable loopback-only fixture server for exact-byte browser integration checks.
import http from 'node:http';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
const html=readFileSync(new URL('./fixture.html',import.meta.url));
const accesses=[];
const name=/^\/files\/(episode-(?:0[1-9]|10))\.zip$/;
const fileFor = id => Buffer.from(`VESPER_FIXTURE_${id}\n`.repeat(256));
const server=http.createServer((request,response)=>{
 const u=new URL(request.url,'http://127.0.0.1');
 if(u.pathname==='/reset' && request.method==='POST'){
  accesses.length=0;response.writeHead(204,{'Cache-Control':'no-store'});response.end();return;
 }
 if(u.pathname==='/' || u.pathname==='/fixture.html'){
  response.writeHead(200,{'Content-Type':'text/html;charset=utf-8','Cache-Control':'no-store'});response.end(html);return;
 }
 const matched=name.exec(u.pathname);
 if(matched){
  accesses.push({name:matched[1],method:request.method});
  const content=fileFor(matched[1]);
  response.writeHead(200,{'Content-Type':'application/zip','Content-Disposition':`attachment; filename="${matched[1]}.zip"`,
   'Content-Length':content.length,'Cache-Control':'no-store','ETag':'"'+createHash('sha256').update(content).digest('hex')+'"'});
  response.end(content);return;
 }
 if(u.pathname==='/results'){
  const expected=Object.fromEntries(Array.from({length:10},(_,i)=>{
   const id='episode-'+String(i+1).padStart(2,'0');return [id,createHash('sha256').update(fileFor(id)).digest('hex')];}));
  response.writeHead(200,{'Content-Type':'application/json','Cache-Control':'no-store'});
  response.end(JSON.stringify({accesses,expected},null,2));return;
 }
 response.writeHead(404);response.end('Not found');
});
server.listen(0,'127.0.0.1',()=>{
 console.log('VESPER guided-batch fixture:',`http://127.0.0.1:${server.address().port}/`);
 console.log('Expected file SHA-256 and request counts:',`http://127.0.0.1:${server.address().port}/results`);
});
