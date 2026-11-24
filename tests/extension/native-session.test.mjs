import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
const code = readFileSync(new URL('../../extension/native-session.js', import.meta.url),'utf8');
function setup({legacy=false}={}) {
  let connections=0, oneShots=0;
  const requests=[], ports=[], timers=new Set();
  const runtime={
    connectNative() {
      connections++;
      const port={
        onMessage:{addListener(fn){port.reply=fn;}},
        onDisconnect:{addListener(fn){port.closed=fn;}},
        postMessage(message){
          if(message.type==='session-open') queueMicrotask(()=>port.reply(legacy
            ? {id:message.id,ok:false,code:'invalid'}
            : {id:message.id,ok:true,data:{mux:true}}));
          else requests.push({message,port});
        },
        disconnect(){if(!port.disconnected){port.disconnected=true;port.closed?.();}}
      };
      ports.push(port);
      return port;
    },
    sendNativeMessage(host,message,cb){oneShots++;queueMicrotask(()=>cb({ok:true,id:message.id}));}
  };
  const context=vm.createContext({chrome:{runtime},crypto:{randomUUID},Date,
    setTimeout:(fn,ms)=>{let t=setTimeout(()=>{timers.delete(t);fn();},ms);timers.add(t);return t;},
    clearTimeout:t=>{timers.delete(t);clearTimeout(t);}});
  vm.runInContext(code,context);
  const tick=()=>new Promise(resolve=>setImmediate(resolve));
  return {session:context.VesperNativeSession,requests,ports,tick,
    get connections(){return connections;},get oneShots(){return oneShots;},
    stop(){context.VesperNativeSession.close();for(const timer of timers) clearTimeout(timer);}};
}
const message=id=>({v:1,id,type:'probe'});

test('one handshake, out-of-order replies correctly correlated, no one-shot requests', async () => {
  const b=setup();
  try {
    const pending=[1,2,3].map(i=>b.session.request('host',message('r'+i),1000));
    await b.tick();
    assert.equal(b.connections,1);assert.equal(b.requests.length,3);
    for(const {port,message:m} of [...b.requests].reverse()) port.reply({id:m.id,ok:true,data:m.id});
    assert.deepEqual((await Promise.all(pending)).map(x=>x.data),['r1','r2','r3']);
    assert.equal(b.oneShots,0);
  } finally {b.stop();}
});
test('unsupported handshake falls back once without reconnect recursion',async()=>{
  const b=setup({legacy:true});
  try{
    assert.equal((await b.session.request('host',message('a'),1000)).ok,true);
    assert.equal((await b.session.request('host',message('b'),1000)).ok,true);
    assert.equal(b.connections,1);assert.equal(b.oneShots,2);
  }finally{b.stop();}
});
test('queued work expires without being sent and disconnect never replays active requests',async()=>{
  const b=setup();
  try{
    const pending=Array.from({length:12},(_,i)=>b.session.request('host',message('p'+i),1000));
    await b.tick();
    const waiting=b.session.request('host',message('must-not-send'),10);
    assert.equal((await waiting).code,'timeout');
    b.ports[0].disconnect();
    const answers=await Promise.all(pending);
    assert.ok(answers.every(x=>x.code==='host-error'));
    assert.equal(b.requests.length,12);
    assert.equal(b.oneShots,0);
  }finally{b.stop();}
});

test('format discovery reserves slots for urgent transfer ownership and user actions', async () => {
  const b = setup();
  try {
    const lookups = Array.from({length:12}, (_,i) => b.session.request('host',
      {v:1,id:'lookup'+i,type:'media-source-options'}, 1000));
    await b.tick();
    assert.equal(b.requests.length,10);
    const transfer = b.session.request('host',{v:1,id:'transfer',type:'browser-capture'},1000);
    const selection = b.session.request('host',{v:1,id:'selection',type:'media-select'},1000);
    await b.tick();
    assert.equal(b.requests.length,12);
    assert.deepEqual(b.requests.slice(-2).map(x=>x.message.id),['transfer','selection']);
    for (const {port,message:m} of b.requests.slice(-2)) port.reply({id:m.id,ok:true});
    assert.ok((await transfer).ok && (await selection).ok);
    await b.tick();
    assert.equal(b.requests.length,12); // still no more than ten active lookups
    for (const {port,message:m} of b.requests.slice(0,2)) port.reply({id:m.id,ok:true});
    await b.tick();
    assert.deepEqual(b.requests.slice(-2).map(x=>x.message.id),['lookup10','lookup11']);
    for (const {port,message:m} of b.requests.filter(x=>x.message.type==='media-source-options').slice(2))
      port.reply({id:m.id,ok:true});
    assert.ok((await Promise.all(lookups)).every(x=>x.ok));
  } finally {b.stop();}
});
