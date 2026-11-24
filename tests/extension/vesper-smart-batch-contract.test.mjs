import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
const src = readFileSync(new URL('../../extension/vesper-smart-batch.js', import.meta.url), 'utf8');
const brainSrc = readFileSync(new URL('../../extension/vesper-intelligence.js', import.meta.url), 'utf8');
const evidenceSrc = readFileSync(new URL('../../extension/vesper-control-evidence.js', import.meta.url), 'utf8');
const constants = src.split(/\r?\n/).filter(line => /^  const (ITEM_ATTRIBUTES|ITEM_QUERY|CONTROL_SELECTOR) =/.test(line)).join('\n');
function subject(name, next, extras = {}) {
  const start = src.indexOf(`  function ${name}(`), end = src.indexOf(`  function ${next}(`, start + 10);
  assert.ok(start >= 0 && end > start, `${name} must remain a named, inspectable function`);
  const ctx = vm.createContext({ URL, location: { href: 'https://site.test/items', origin: 'https://site.test' }, ...extras });
  vm.runInContext(brainSrc, ctx);
  vm.runInContext(evidenceSrc, ctx);
  vm.runInContext(`${constants}\nconst evidence = VesperControlEvidence;\n${src.slice(start, end)}\nthis.subject=${name};`, ctx);
  return { fn: ctx.subject, ctx };
}
function el(attrs = {}) {
  return { tagName: attrs.tagName || 'BUTTON', id: attrs.id || '',
    getAttribute: attr => attrs[attr] || null,
    hasAttribute: attr => attr in attrs,
    innerText: attrs.text || '', textContent: attrs.text || '' };
}
test('shared Download button is not ready until it binds to intended item', () => {
  const brain = vm.createContext({ URL });
  vm.runInContext(brainSrc, brain);
  const ctx = { brain: brain.VesperIntelligence, actionUrl: button => brain.VesperIntelligence.http(button.getAttribute('data-download-url')) };
  const { fn } = subject('actionProvesItem', 'waitForChange', ctx);
  const episode = { id: 'episode-02' };
  const stale = el({ 'data-item-id': 'episode-01', 'data-download-url': 'https://cdn.test/episode-01.zip' });
  assert.equal(fn(stale, episode, null), false);
  const current = el({ 'data-item-id': 'episode-02', 'data-download-url': 'https://cdn.test/episode-02.zip' });
  assert.equal(fn(current, episode, null), true);
});
test('reused same button with no item proof always refuses automatic click', () => {
  const brain = vm.createContext({ URL });
  vm.runInContext(brainSrc, brain);
  const { fn } = subject('actionProvesItem', 'waitForChange', {
    brain: brain.VesperIntelligence, actionUrl: button => brain.VesperIntelligence.http(button.getAttribute('data-download-url'))
  });
  assert.equal(fn(el({ 'data-download-url': 'https://cdn.test/anonymous.zip' }), { id: 'episode-02' }), false);
  assert.equal(fn(el({ 'data-download-url': 'https://cdn.test/episode-01.zip' }), { id: 'episode-02' }), false);
});
test('explicit item ID cannot be overridden by a suggestive but stale URL', () => {
  const brain = vm.createContext({ URL });
  vm.runInContext(brainSrc, brain);
  const { fn } = subject('actionProvesItem', 'waitForChange', {
    brain: brain.VesperIntelligence, actionUrl: button => brain.VesperIntelligence.http(button.getAttribute('data-download-url'))
  });
  assert.equal(fn(el({ 'data-item-id': 'episode-01', 'data-download-url': 'https://cdn.test/episode-02.zip' }),
    { id: 'episode-02' }), false);
});
test('a button with an item-binding query parameter can be verified without CSS classes', () => {
  const brain = vm.createContext({ URL });
  vm.runInContext(brainSrc, brain);
  const { fn } = subject('actionProvesItem', 'waitForChange', {
    brain: brain.VesperIntelligence, actionUrl: button => brain.VesperIntelligence.http(button.getAttribute('href'))
  });
  assert.equal(fn(el({ href: 'https://cdn.test/api/download?episode=episode-02' }), { id: 'episode-02' }), true);
});

test('manual control picking uses an extension-owned hit target rather than clicking the page button', () => {
  const start = src.indexOf('    function chooseControl() {');
  const end = src.indexOf('    async function refreshResults()', start);
  const code = src.slice(start, src.indexOf('    function outcomeText(', start));
  const listeners = new Map(), removed = [], pageButton = {
    disabled:false, getAttribute:()=>null, closest(){return this;}, click(){assert.fail('The page button must not be clicked while selecting');}
  };
  const host = {style:{}, isConnected:true, contains:()=>false, focus(){},
    addEventListener:(type,fn)=>listeners.set(type,fn),
    removeEventListener:(type)=>{removed.push(type);listeners.delete(type);}};
  const section={style:{},hidden:false}, actionInfo={};
  const hint={style:{},remove(){}};
  const ctx=vm.createContext({host,section,actionInfo,shadow:{append(){}},
    document:{createElement:()=>hint,elementsFromPoint:()=>[host,pageButton]},
    shown:()=>true,labelFor:()=> 'Download',controlFacts:()=>({tagName:'BUTTON',label:'Download'}),update(){},write(){},choose:{focus(){}},
    setTimeout:()=>1,clearTimeout(){},selectionMode:false,picked:null,finishSelection:null});
  vm.runInContext(evidenceSrc, ctx);
  vm.runInContext(constants+'\nconst evidence = VesperControlEvidence;\n'+code+'\nchooseControl();',ctx);
  assert.equal(section.hidden,true);
  assert.notEqual(host.style.pointerEvents,'none');
  let prevented=false, stopped=false;
  listeners.get('click')({button:0,clientX:10,clientY:10,preventDefault(){prevented=true;},stopImmediatePropagation(){stopped=true;}});
  assert.equal(prevented,true);
  assert.equal(stopped,true);
  assert.equal(ctx.picked,pageButton);
  assert.equal(section.hidden,false);
  assert.equal(listeners.size,0);
  assert.ok(removed.includes('pointerdown'));
});

test('countdown text can change without invalidating the approved action identity', () => {
  const { fn }=subject('actionKey','matchingAction',{clean:value=>String(value||''),idValue:value=>value||'',labelFor:e=>e.textContent});
  assert.equal(fn(el({text:'Download in 30 seconds',id:'shared-download'})),fn(el({text:'Download',id:'shared-download'})));
  assert.equal(fn(el({text:'Download (5s)',id:'shared-download'})),fn(el({text:'Download',id:'shared-download'})));
  assert.notEqual(fn(el({text:'Delete',id:'shared-download'})),fn(el({text:'Download',id:'shared-download'})));
});
test('contradictory URL item binding blocks a stale DOM id', () => {
  const brain=vm.createContext({URL});vm.runInContext(brainSrc,brain);
  const {fn}=subject('actionProvesItem','waitForChange',{
    brain:brain.VesperIntelligence,actionUrl:button=>brain.VesperIntelligence.http(button.getAttribute('href'))
  });
  assert.equal(fn(el({'data-item-id':'episode-02',href:'https://cdn.test/download?episode=episode-01'}),{id:'episode-02'}),false);
  assert.equal(fn(el({'data-item-id':'episode-02',href:'https://cdn.test/download?episode=episode-02'}),{id:'episode-02'}),true);
});
test('readiness observes disabled changes and cancellation removes its timers immediately', async () => {
  const start=src.indexOf('  function waitForChange('),end=src.indexOf('  const sleep =',start);
  let observed, observerCallback, disconnected=false, ready=false, id=0;
  const timers=new Map();
  const ctx=vm.createContext({
    READY_TIMEOUT:90000,document:{body:{}},
    MutationObserver:class {constructor(fn){observerCallback=fn;}observe(el,opts){observed=opts;}disconnect(){disconnected=true;}},
    setTimeout:(fn,ms)=>{timers.set(++id,{fn,ms});return id;},clearTimeout:i=>timers.delete(i)
  });
  vm.runInContext(constants+'\n'+src.slice(start,end)+'\nthis.wait=waitForChange;',ctx);
  const controller=new AbortController();
  const result=ctx.wait(()=>ready?{verified:true}:null,90000,controller.signal);
  assert.ok(observed.attributeFilter.includes('disabled'));
  assert.ok(observed.attributeFilter.includes('aria-disabled'));
  assert.equal(observed.characterData,true);
  ready=true;observerCallback();
  assert.equal((await result).verified,true);
  assert.equal(timers.size,0);assert.equal(disconnected,true);
  disconnected=false;ready=false;
  const next=ctx.wait(()=>null,90000,controller.signal);
  controller.abort();
  assert.equal(await next,null);
  assert.equal(timers.size,0);assert.equal(disconnected,true);
});

test('accessible active tab must uniquely identify a visible panel containing the download control', () => {
  const button=el({href:'https://site.test/get/archive.zip'});
  const tab=el({id:'tab-2','aria-controls':'panel-2','aria-selected':'true'});
  const panel={...el({role:'tabpanel','aria-labelledby':'tab-2'}),contains:x=>x===button,hidden:false};
  let panels=[panel], tabs=[tab];
  const brain=vm.createContext({URL});vm.runInContext(brainSrc,brain);
  const {fn}=subject('actionProvesItem','waitForChange',{
    brain:brain.VesperIntelligence,actionUrl:e=>brain.VesperIntelligence.http(e.getAttribute('href')),
    document:{querySelectorAll:s=>s.includes('panel-2')?panels:tabs}
  });
  assert.equal(fn(button,{id:'2'},tab),true);
  panels=[panel,panel];assert.equal(fn(button,{id:'2'},tab),false);
  panels=[panel];tabs=[tab,tab];assert.equal(fn(button,{id:'2'},tab),false);
  tabs=[tab];panel.hidden=true;assert.equal(fn(button,{id:'2'},tab),false);
  panel.hidden=false;panel.contains=()=>false;assert.equal(fn(button,{id:'2'},tab),false);
});
test('all explicit item claims must agree, including chapter and file IDs',()=>{
  const brain=vm.createContext({URL});vm.runInContext(brainSrc,brain);
  const {fn}=subject('actionProvesItem','waitForChange',{brain:brain.VesperIntelligence,
    actionUrl:e=>brain.VesperIntelligence.http(e.getAttribute('href'))});
  assert.equal(fn(el({'data-item-id':'2','data-file-id':'3'}),{id:'2'}),false);
  assert.equal(fn(el({href:'https://site.test/download?chapter=2'}),{id:'2'}),true);
  assert.equal(fn(el({href:'https://site.test/download?chapter=2&chapter=3'}),{id:'2'}),false);
});
test('placeholder href is allowed but conflicting real destinations are refused',()=>{
  const brain=vm.createContext({URL});vm.runInContext(brainSrc,brain);
  const {fn}=subject('actionUrl','actionKey',{brain:brain.VesperIntelligence});
  assert.equal(fn(el({href:'#','data-download-url':'/archive.zip'})).href,'https://site.test/archive.zip');
  assert.equal(fn(el({href:'/other.zip','data-download-url':'/archive.zip'})),null);
  assert.equal(fn(el({href:'#'})),null);
});
test('duplicate live download controls cannot resolve an approved action key',()=>{
  const a=el(),b=el();let nodes=[a,b];
  const {fn}=subject('matchingAction','selectedItem',{document:{querySelectorAll:()=>nodes},
    dialog:null,shown:()=>true,actionKey:()=> 'key'});
  assert.equal(fn('key'),null);
  nodes=[a];assert.equal(fn('key'),a);
});
