// Offline model fitting. This script cannot load websites, deploy a model or authorize downloads.
import { readFileSync, writeFileSync, statSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const context = vm.createContext({ URL });
vm.runInContext(readFileSync(new URL('../extension/vesper-intelligence.js', import.meta.url), 'utf8'), context);
const { playerFeatures, createPlayerRanker, rankPlayers, PLAYER_FEATURES, PLAYER_POLICY } = context.VesperIntelligence;
const names = [...PLAYER_FEATURES];
const baseline = names.map(name => PLAYER_POLICY[name]);
const MAX_CASES = 4096, MAX_CANDIDATES = 32;
const binary = ['playing','ready','progressed','controls','muted','incidentalLoop','interacted','incumbent'];
const fields = new Set(['id','area','visibleFraction',...binary]);
const modelFor = (weights, margin) => ({ version: 1, weights: Object.fromEntries(names.map((name,i)=>[name,weights[i]])), margin });

export function validatePartitions(partitions) {
  const groups = new Map();
  for (const [split, cases] of Object.entries(partitions)) {
    if (!Array.isArray(cases) || !cases.length || cases.length > MAX_CASES) throw Error('Invalid '+split+' size');
    for (const row of cases) {
      if (!row || typeof row.group !== 'string' || !/^[a-z0-9._-]{1,100}$/i.test(row.group)
          || !Array.isArray(row.candidates) || row.candidates.length < 1 || row.candidates.length > MAX_CANDIDATES
          || !Object.hasOwn(row,'chosen')) throw Error('Invalid labeled case in '+split);
      const group = row.group.toLowerCase();
      if (groups.has(group) && groups.get(group) !== split) throw Error('Group crosses partitions: '+group);
      groups.set(group,split);
      const ids = new Set();
      for (const item of row.candidates) {
        if (!item || Object.keys(item).some(key=>!fields.has(key)) || typeof item.id !== 'string'
            || !/^[a-z0-9_-]{1,128}$/i.test(item.id) || ids.has(item.id)
            || !Number.isFinite(item.area) || item.area <= 0
            || !Number.isFinite(item.visibleFraction) || item.visibleFraction < 0 || item.visibleFraction > 1
            || binary.some(key=>typeof item[key] !== 'boolean')) throw Error('Invalid candidate in '+split);
        ids.add(item.id);
      }
      if (row.chosen !== null && !ids.has(row.chosen)) throw Error('Chosen candidate absent in '+split);
    }
  }
}
export function evaluate(cases, rank) {
  let labeled=0, ambiguous=0, correctAnchor=0, correctDecisive=0, wrongDecisive=0, ambiguousDecisive=0, abstained=0;
  for (const row of cases) {
    const result=rank(row.candidates);
    if (row.chosen === null) {
      ambiguous++;
      if (result.decisive) ambiguousDecisive++;
    } else {
      labeled++;
      if (result.selected === row.chosen) correctAnchor++;
      if (result.decisive) result.selected === row.chosen ? correctDecisive++ : wrongDecisive++;
    }
    if (!result.decisive) abstained++;
  }
  return { cases:cases.length,labeled,ambiguous,correctAnchor,correctDecisive,wrongDecisive,ambiguousDecisive,abstained,
    decisivePrecision:correctDecisive+wrongDecisive+ambiguousDecisive===0?null:correctDecisive/(correctDecisive+wrongDecisive+ambiguousDecisive),
    labeledRecall:labeled?correctDecisive/labeled:null };
}

export function fit({ train, validation, holdout }) {
  validatePartitions({train,validation,holdout});
  // Pairwise logistic ranking, with equal total weight per labeled page and
  // a conservative prior. Ambiguous examples are used for margin calibration.
  const pairs=[];
  for (const row of train) {
    if (row.chosen === null || row.candidates.length < 2) continue;
    const { rows }=playerFeatures(row.candidates);
    const chosen=rows.find(r=>r.id===row.chosen).features;
    for (const other of rows.filter(r=>r.id!==row.chosen))
      pairs.push({diff:names.map(name=>chosen[name]-other.features[name]),weight:1/(rows.length-1)});
  }
  if (!pairs.length) throw Error('No labeled ranking pairs');
  if (!validation.some(r=>r.chosen!==null) || !validation.some(r=>r.chosen===null)
      || !holdout.some(r=>r.chosen!==null) || !holdout.some(r=>r.chosen===null))
    throw Error('Validation and holdout must include both known intent and ambiguity');
  const weightSum=pairs.reduce((sum,p)=>sum+p.weight,0);
  const w=[...baseline];
  for (let epoch=0;epoch<80;epoch++) {
    const gradient=names.map(()=>0);
    for (const pair of pairs) {
      const logit=pair.diff.reduce((sum,d,i)=>sum+d*w[i],0);
      const scale=-pair.weight/(1+Math.exp(Math.max(-40,Math.min(40,logit))));
      for(let i=0;i<w.length;i++) gradient[i]+=scale*pair.diff[i];
    }
    for(let i=0;i<w.length;i++){
      const value=w[i]-0.6*(gradient[i]/weightSum+0.015*(w[i]-baseline[i]));
      w[i]=names[i]==='incidentalLoop'?Math.max(-16,Math.min(0,value)):Math.max(0,Math.min(16,value));
    }
  }
  const candidates=[0.75,1,1.25,1.5,2,3,4,6].map(margin=>{
    const model=modelFor(w,margin);
    return {model,metrics:evaluate(validation,createPlayerRanker(model))};
  }).sort((a,b)=>(a.metrics.wrongDecisive+a.metrics.ambiguousDecisive)-(b.metrics.wrongDecisive+b.metrics.ambiguousDecisive)
    || b.metrics.correctDecisive-a.metrics.correctDecisive || b.model.margin-a.model.margin);
  const candidate=candidates[0].model;
  return { model:candidate, baseline:evaluate(holdout,rankPlayers),
    candidate:evaluate(holdout,createPlayerRanker(candidate)), validation:candidates[0].metrics,
    deployed:false, note:'Candidate only. Independent real-world acceptance and review are required.' };
}

if (process.argv[1] && resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  const [trainPath,validationPath,holdoutPath,outPath]=process.argv.slice(2);
  if (!outPath) throw Error('Usage: node scripts/train-player-intent.mjs train.json validation.json holdout.json report.json');
  const read=p=>{if(statSync(p).size>32*1024*1024)throw Error('Dataset too large');const raw=readFileSync(p);if(raw.length>32*1024*1024)throw Error('Dataset too large');return JSON.parse(raw);};
  const report=fit({train:read(trainPath),validation:read(validationPath),holdout:read(holdoutPath)});
  // Exclusive creation prevents overwriting any reviewed artifact or bundled model.
  writeFileSync(outPath,JSON.stringify(report,null,2)+'\n',{flag:'wx'});
  console.log(JSON.stringify({baseline:report.baseline,candidate:report.candidate,deployed:false}));
}
