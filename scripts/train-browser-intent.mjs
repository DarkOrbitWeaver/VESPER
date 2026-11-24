// Deterministic, offline, synthetic-data logistic classifier. No browsing data or network.
import { readFileSync, writeFileSync } from 'node:fs';
import vm from 'node:vm';
const root = new URL('../', import.meta.url);
const ctx = vm.createContext({ URL });
vm.runInContext(readFileSync(new URL('extension/vesper-intent-features.js', root), 'utf8'), ctx);
const features = ctx.VesperIntentFeatures;
const examples = [];
const add = (labels, value, facts) => labels.forEach(label => examples.push({ label, value, ...facts }));
add(['Download','Save file','Télécharger','Descargar','Baixar','Herunterladen','Scarica','تحميل','下载','ダウンロード','Скачать','İndir','Pobierz','다운로드','Tải xuống'], 1, {tagName:'BUTTON',hasDownloadUrl:true});
add(['Get archive','Export CSV','Save report','Obtain file','Download PDF','Download video','1080p MP4','Original file','Save image','Download attachment','Download all'],1,{tagName:'A',downloadAttribute:true,href:'https://fixture.test/files/archive.zip'});
add(['Get installer','Save copy','Export results','Retrieve document','Télécharger le fichier','Descargar archivo','Download episode','Download audio'],1,{tagName:'BUTTON',stableBinding:true,href:'https://fixture.test/download?id=1'});
add(['Download','Save','Export','File','Get it','Download episode'],0,{tagName:'A',isNavigation:true,href:'https://fixture.test/help'});
add(['Download','Save file','Download now','Télécharger','Export'],0,{tagName:'BUTTON',isDisabled:true});
add(['Open PDF','View document','Read PDF','Preview','Download instructions'],0,{tagName:'A',isInlinePdf:true,href:'https://fixture.test/manual.pdf'});
add(['Play','Watch','Next episode','Previous','Continue','Buy now','Subscribe','Sign in','Delete','Remove','Log out','Share','Settings','Open menu','Read more','Search','Home','Back','Details','Help','Documentation','Contact'],0,{tagName:'BUTTON'});
add(['Watch','Episode 12','Next','Previous','Part 3','Page 2','Downloads','Files','Download history'],0,{tagName:'A',isNavigation:true,href:'https://fixture.test/episodes/12'});
add(['Download','Save video','1080p','Get segment','Play'],0,{tagName:'A',href:'https://fixture.test/segment.m4s'});
add(['Download','Get file','Export','Save'],0,{tagName:'BUTTON',isBackground:true});
add(['','File','Attachment','PDF','ZIP','MP4','Original','Get'],1,{tagName:'A',downloadAttribute:true,href:'https://fixture.test/file.zip'});
add(['Download now','Download file','Save attachment','Download 720p','Download MP3'],1,{tagName:'BUTTON',href:'https://fixture.test/download?id=9',hasDownloadUrl:true});
add(['Save changes','Export settings','Add to cart','Download app help','Start trial','Go to download page'],0,{tagName:'BUTTON',isFormSubmit:true});

// Contrast examples share vocabulary across positive and negative controls.
// Training remains synthetic; no browsing telemetry or website success claims.
const languages = [
  ['Download','Download file','Download history','Download settings'],
  ['Télécharger','Enregistrer le fichier','Historique des téléchargements','Paramètres'],
  ['Descargar','Guardar archivo','Historial de descargas','Configuración'],
  ['Baixar','Salvar arquivo','Histórico de downloads','Configurações'],
  ['Herunterladen','Datei speichern','Downloadverlauf','Einstellungen'],
  ['Scaricare','Salva file','Cronologia download','Impostazioni'],
  ['تحميل','تنزيل الملف','سجل التنزيلات','الإعدادات'],
  ['下载','保存文件','下载记录','设置'],
  ['ダウンロード','ファイルを保存','ダウンロード履歴','設定'],
  ['Скачать','Сохранить файл','История загрузок','Настройки'],
  ['İndir','Dosyayı kaydet','İndirme geçmişi','Ayarlar'],
  ['Pobierz','Zapisz plik','Historia pobierania','Ustawienia'],
  ['다운로드','파일 저장','다운로드 기록','설정'],
  ['Tải xuống','Lưu tệp','Lịch sử tải xuống','Cài đặt'],
  ['ดาวน์โหลด','บันทึกไฟล์','ประวัติการดาวน์โหลด','การตั้งค่า'],
  ['डाउनलोड','फ़ाइल सहेजें','डाउनलोड इतिहास','सेटिंग्स']
];
for (const [download,save,history,settings] of languages) {
  for (const label of [download,save]) {
    add([label],1,{tagName:'A',downloadAttribute:true,href:'https://fixture.test/assets/payload.bin',sameOrigin:true});
    add([label],1,{tagName:'BUTTON',hasDownloadUrl:true,stableBinding:true});
    add([label],0,{tagName:'BUTTON',isDisabled:true,hasDownloadUrl:true});
    add([label],0,{tagName:'A',isNavigation:true,href:'https://fixture.test/account/downloads'});
    add([label],0,{tagName:'A',href:'https://fixture.test/media/init.m4s'});
    add([label],0,{tagName:'BUTTON',isBackground:true,hasDownloadUrl:true});
  }
  add([history,settings],0,{tagName:'A',isNavigation:true,href:'https://fixture.test/account'});
}
add(['4K','Original','Full size','Source','Lossless','⬇','↓',''],1,{tagName:'A',downloadAttribute:true,href:'https://fixture.test/object?id=5'});
add(['Open','Preview','Read','View','原文','Lire'],0,{tagName:'A',isInlinePdf:true,href:'https://fixture.test/files/read.pdf'});
add(['Export profile','Save preference','Download subscription'],0,{tagName:'BUTTON',isFormSubmit:true,stableBinding:true});

const validation = [
  {label:'Recuperar archivo',tagName:'BUTTON',hasDownloadUrl:true,stableBinding:true,value:1},
  {label:'Get the original',tagName:'A',downloadAttribute:true,href:'https://fixture.test/asset.bin',value:1},
  {label:'Enregistrer une copie',tagName:'A',downloadAttribute:true,href:'https://fixture.test/asset.pdf',value:1},
  {label:'Full resolution 2160p',tagName:'BUTTON',hasDownloadUrl:true,href:'https://fixture.test/download?v=42',value:1},
  {label:'حفظ الملف',tagName:'A',downloadAttribute:true,href:'https://fixture.test/asset.zip',value:1},
  {label:'Download catalogue',tagName:'A',isNavigation:true,href:'https://fixture.test/catalogue',value:0},
  {label:'View original',tagName:'A',isInlinePdf:true,href:'https://fixture.test/item.pdf',value:0},
  {label:'Download file',tagName:'BUTTON',isDisabled:true,value:0},
  {label:'Next chapter',tagName:'A',isNavigation:true,href:'https://fixture.test/chapter/42',value:0},
  {label:'Save this segment',tagName:'A',href:'https://fixture.test/part.cmfv',value:0},
  {label:'Export account settings',tagName:'BUTTON',isFormSubmit:true,value:0},
  {label:'Purchase credits',tagName:'BUTTON',value:0}
];

// Held-out hand-authored scenarios: lexical overlap alone is deliberately misleading.
const holdout = [
  ['Hent filen',1,{downloadAttribute:true,tagName:'A',href:'https://unseen.test/report.bin'}],
  ['Lataa tiedosto',1,{tagName:'BUTTON',hasDownloadUrl:true,stableBinding:true}],
  ['Λήψη αρχείου',1,{tagName:'A',downloadAttribute:true,href:'https://unseen.test/files/asset.dat'}],
  ['Завантажити оригінал',1,{tagName:'BUTTON',hasDownloadUrl:true}],
  ['دانلود نسخه اصلی',1,{tagName:'A',downloadAttribute:true,href:'https://unseen.test/archive.7z'}],
  ['Download (8 seconds)',0,{tagName:'BUTTON',isDisabled:true,hasDownloadUrl:true}],
  ['Download original',0,{tagName:'A',isNavigation:true,href:'https://unseen.test/download/catalog'}],
  ['Save invoice PDF',0,{tagName:'A',isInlinePdf:true,href:'https://unseen.test/invoice.pdf'}],
  ['Download high quality',0,{tagName:'A',href:'https://unseen.test/content/segment.m4s'}],
  ['Export all',0,{tagName:'BUTTON',isFormSubmit:true,stableBinding:true}],
  ['Scarica tutto',0,{tagName:'BUTTON',isDisabled:true,hasDownloadUrl:true}],
  ['Téléchargements récents',0,{tagName:'A',isNavigation:true,href:'https://unseen.test/history'}],
  ['Obtener el fichero',1,{tagName:'A',downloadAttribute:true,href:'https://unseen.test/d/42'}],
  ['データを取得',1,{tagName:'BUTTON',hasDownloadUrl:true,stableBinding:true}],
  ['附件',1,{tagName:'A',downloadAttribute:true,href:'https://unseen.test/receipt.pdf'}],
  ['حفظ الإعدادات',0,{tagName:'BUTTON',isFormSubmit:true}],
  ['Acessar biblioteca',0,{tagName:'A',isNavigation:true,href:'https://unseen.test/library'}],
  ['Tải video',0,{tagName:'BUTTON',isBackground:true,hasDownloadUrl:true}]
].map(([label,value,facts])=>({label,value,...facts}));
validation.push(...holdout);
const weights = new Float64Array(features.SIZE);
const sigmoid = x => 1/(1+Math.exp(-x));
const vectorized = examples.map(e=>({x:[...features.vector(e)],y:e.value}));
let seed=734189;
const rand=()=>{seed=(Math.imul(seed,1664525)+1013904223)>>>0;return seed/4294967296;};
for(let epoch=0;epoch<220;epoch++){
  const order=vectorized.slice();
  for(let i=order.length-1;i>0;i--){const j=Math.floor(rand()*(i+1));[order[i],order[j]]=[order[j],order[i]];}
  for(const {x,y} of order){
    let z=0;for(const [i,v] of x)z+=weights[i]*v;
    const error=y-sigmoid(z);
    for(const [i,v] of x)weights[i]+=0.075*(error*v-0.001*weights[i]);
  }
}
const quantized=Array.from(weights,w=>Math.round(w*1024));
const probability=f=>sigmoid([...features.vector(f)].reduce((sum,[i,v])=>sum+quantized[i]*v/1024,0));
const metrics={syntheticOnly:true,positive:validation.filter(f=>f.value).length,negative:validation.filter(f=>!f.value).length,abstained:validation.filter(f=>probability(f)>0.15&&probability(f)<0.85).length,training:examples.length,validation:validation.length,correct:validation.filter(f=>(probability(f)>=0.5)===Boolean(f.value)).length,highConfidencePositive:validation.filter(f=>f.value&&probability(f)>=0.85).length,highConfidenceFalsePositive:validation.filter(f=>!f.value&&probability(f)>=0.85).length};
// Pairwise learning-to-rank for a selected item and candidate Download
// control. Synthetic training contrasts the correct action with plausible
// ads, unrelated downloads and stale controls on the same page.
const relationPairs = [];
for (const evidence of ['explicitMatch','queryMatch','panelMatch','pathMatch']) {
  for (const intent of [0.35,0.60,0.85]) {
    for (const distractor of [
      {intent:0.98,structuralDownload:true,hasUrl:true},
      {intent:0.90,sameGroup:true,hasUrl:true,contradiction:true},
      {intent:0.75,structuralDownload:true,hasUrl:true,differentPanel:true},
      {intent:0.90,explicitMatch:true,blocked:true,hasUrl:true},
      {intent:0.95,queryMatch:true,contradiction:true,hasUrl:true}
    ]) relationPairs.push([
      {intent,active:true,hasUrl:true,structuralDownload:true,[evidence]:true}, distractor
    ]);
  }
}
const relationHoldout = [
  [{intent:0.34,panelMatch:true,sameGroup:true,active:true,hasUrl:true},{intent:0.99,structuralDownload:true,hasUrl:true}],
  [{intent:0.52,queryMatch:true,active:true,hasUrl:true},{intent:0.95,explicitMatch:true,contradiction:true,hasUrl:true}],
  [{intent:0.72,pathMatch:true,active:true,hasUrl:true},{intent:0.92,sameGroup:true,differentPanel:true,hasUrl:true}],
  [{intent:0.47,explicitMatch:true,active:true,hasUrl:true},{intent:0.99,queryMatch:true,blocked:true,hasUrl:true}],
  [{intent:0.62,queryMatch:true,panelMatch:true,active:true,hasUrl:true},{intent:0.97,structuralDownload:true,differentPanel:true,hasUrl:true}],
  [{intent:0.79,panelMatch:true,active:true,hasUrl:true},{intent:0.96,pathMatch:true,contradiction:true,hasUrl:true}],
  [{intent:0.82,explicitMatch:true,active:true,hasUrl:true},{intent:0.83,structuralDownload:true,sameGroup:true,hasUrl:true}],
  [{intent:0.55,pathMatch:true,active:true,hasUrl:true},{intent:0.99,structuralDownload:true,blocked:true,hasUrl:true}]
];
const relationWeights = new Float64Array(features.relationshipVector().length);
for(let epoch=0;epoch<300;epoch++) {
  for(const [good,bad] of relationPairs) {
    const x=features.relationshipVector(good).map((value,i)=>value-features.relationshipVector(bad)[i]);
    const margin=x.reduce((sum,value,i)=>sum+value*relationWeights[i],0);
    const error=1-sigmoid(margin);
    for(let i=0;i<x.length;i++)relationWeights[i]+=0.06*(error*x[i]-0.003*relationWeights[i]);
  }
}
const relationQuantized=Array.from(relationWeights,w=>Math.round(w*1024));
const relationScore=f=>features.relationshipVector(f).reduce((sum,value,i)=>sum+value*relationQuantized[i]/1024,0);
metrics.relationshipTrainingPairs=relationPairs.length;
metrics.relationshipHoldout=relationHoldout.length;
metrics.intentOnlyPairWins=relationHoldout.filter(([good,bad])=>good.intent>bad.intent).length;
metrics.relationshipPairWins=relationHoldout.filter(([good,bad])=>relationScore(good)>relationScore(bad)).length;
const output="// Generated by scripts/train-browser-intent.mjs. Synthetic fixture model; advisory only.\n'use strict';\nglobalThis.VesperIntentModel = (() => {\n  const weights = new Int16Array("+JSON.stringify(quantized)+");\n  const relationshipWeights = new Int16Array("+JSON.stringify(relationQuantized)+");\n  function probability(facts) {\n    const features = globalThis.VesperIntentFeatures;\n    if (!features) return 0;\n    let score = 0;\n    for (const [index, value] of features.vector(facts)) score += weights[index] * value / 1024;\n    return 1 / (1 + Math.exp(-score));\n  }\n  function relationshipScore(facts) {\n    const features = globalThis.VesperIntentFeatures;\n    if (!features) return 0;\n    return features.relationshipVector(facts).reduce((sum, value, index) => sum + value * relationshipWeights[index] / 1024, 0);\n  }\n  return Object.freeze({ version: 2, probability, relationshipScore });\n})();\n";
const destination=new URL('extension/vesper-intent-model.js',root);
if(process.argv.includes('--write'))writeFileSync(destination,output);
else if(readFileSync(destination,'utf8').replace(/\r\n/g,'\n')!==output)throw Error('Model differs from deterministic training output');
console.log(JSON.stringify(metrics));
