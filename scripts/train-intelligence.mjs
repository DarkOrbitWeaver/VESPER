// Offline training, evaluation, promotion and rollback for VESPER Intelligence models.
// This script cannot load websites or authorize downloads. Runtime never retrains itself.
//   node scripts/train-intelligence.mjs fit [corpus.json] [outDir]
//   node scripts/train-intelligence.mjs review <reviewed-corpus.json>
//   node scripts/train-intelligence.mjs promote <model.json> <reviewed-corpus.json>
//   node scripts/train-intelligence.mjs rollback <schema>
//   node scripts/train-intelligence.mjs verify
import { readFileSync, writeFileSync, mkdirSync, existsSync, readdirSync, copyFileSync, statSync } from 'node:fs';
import { createHash, sign as cryptoSign } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';
import { generate } from '../tools/intelligence/corpus.mjs';

const root = new URL('../', import.meta.url);
const extension = name => readFileSync(new URL(`extension/${name}`, root), 'utf8');
function runtime({ withShippedModels = false } = {}) {
  const context = vm.createContext({ URL });
  vm.runInContext(extension('vesper-intelligence.js'), context);
  if (withShippedModels) vm.runInContext(extension('vesper-model.js'), context);
  vm.runInContext(extension('vesper-inference.js'), context);
  return context;
}
const ctx = runtime();
const Inference = ctx.VesperInference;
const LegacyIntelligence = ctx.VesperIntelligence;
const MAX_CASES = 8192, MAX_CANDIDATES = 32;
const SCHEMA_FILES = { 'vesper.player-intent/2': 'player-intent', 'vesper.source-association/1': 'source-association' };
const FIELDS = {
  'vesper.player-intent/2': new Set(['id', 'area', 'visibleFraction', 'centerDistance', 'playing', 'ready', 'progressed', 'controls',
    'muted', 'incidentalLoop', 'interacted', 'incumbent', 'duration', 'networkBound', 'declaredSource', 'adContext']),
  'vesper.source-association/1': new Set(['id', ...Inference.SCHEMAS['vesper.source-association/1'].features])
};

export function validateCases(schema, partitions) {
  if (!FIELDS[schema] || !partitions || !['train','validation','holdout'].every(split => Object.hasOwn(partitions, split))) throw Error('Missing training partitions');
  const groups = new Map();
  for (const [split, cases] of Object.entries(partitions)) {
    if (!Array.isArray(cases) || !cases.length || cases.length > MAX_CASES) throw Error('Invalid ' + split + ' size');
    for (const row of cases) {
      if (!row || typeof row.group !== 'string' || !/^[a-z0-9._-]{1,100}$/i.test(row.group)
          || !['fixture', 'verified', 'user', 'adapter-weak'].includes(row.label)
          || !Array.isArray(row.candidates) || !row.candidates.length || row.candidates.length > MAX_CANDIDATES
          || !Object.hasOwn(row, 'chosen')) throw Error('Invalid labeled case in ' + split);
      const group = row.group.toLowerCase();
      if (groups.has(group) && groups.get(group) !== split) throw Error('Group crosses partitions: ' + group);
      groups.set(group, split);
      if (split !== 'train' && row.label === 'adapter-weak') throw Error('Weak adapter labels cannot be evaluated: ' + group);
      const ids = new Set();
      for (const item of row.candidates) {
        if (!item || Object.keys(item).some(key => !FIELDS[schema].has(key)) || typeof item.id !== 'string'
            || !/^[a-z0-9_-]{1,128}$/i.test(item.id) || ids.has(item.id)) throw Error('Invalid candidate in ' + split);
        for (const [key, value] of Object.entries(item))
          if (key !== 'id' && !(value === null || typeof value === 'boolean' || Number.isFinite(value))) throw Error('Invalid candidate in ' + split);
        ids.add(item.id);
      }
      if (row.chosen !== null && !ids.has(row.chosen)) throw Error('Chosen candidate absent in ' + split);
    }
  }
  return true;
}

function vectors(schema, row) {
  const { rows } = Inference.featureRows(schema, row.candidates);
  const names = Inference.SCHEMAS[schema].features;
  return new Map(rows.map(r => [r.id, names.map(name => r.facts[name] ?? 0)]));
}

// Pairwise logistic ranking with projected gradient descent: weights stay inside the schema's sign
// constraints and bounds, so a learned model cannot reward ads, loops, stale or hypothesized evidence.
export function fitWeights(schema, train, { epochs = 600, rate = 0.08, l2 = 0.002 } = {}) {
  const spec = Inference.SCHEMAS[schema];
  const names = spec.features;
  const baseline = Inference.BASELINES[schema].weights;
  let w = names.map(name => baseline[name]);
  const pairs = [];
  for (const row of train) {
    if (row.chosen === null) continue;
    const v = vectors(schema, row);
    const chosen = v.get(row.chosen);
    if (!chosen) continue;
    const weight = row.label === 'adapter-weak' ? 0.5 : 1;
    for (const [id, other] of v) if (id !== row.chosen) pairs.push({ diff: chosen.map((x, i) => x - other[i]), weight });
  }
  if (!pairs.length) throw Error('No trainable pairs');
  const total = pairs.reduce((a, p) => a + p.weight, 0);
  for (let epoch = 0; epoch < epochs; epoch++) {
    const grad = names.map((_, i) => l2 * (w[i] - baseline[names[i]]));
    for (const { diff, weight } of pairs) {
      const z = diff.reduce((a, x, i) => a + x * w[i], 0);
      const g = -weight / (1 + Math.exp(z)) / total;
      for (let i = 0; i < names.length; i++) grad[i] += g * diff[i];
    }
    w = w.map((value, i) => {
      let next = value - rate * grad[i] * 10;
      next = Math.max(-16, Math.min(16, next));
      return spec.negative.includes(names[i]) ? Math.min(0, next) : Math.max(0, next);
    });
  }
  return Object.fromEntries(names.map((name, i) => [name, Math.round(w[i] * 1e4) / 1e4]));
}

export function evaluate(rank, cases) {
  const scored = cases.filter(row => row.label !== 'adapter-weak');
  let decisive = 0, correct = 0, wrong = 0, ambiguousDecisive = 0, unambiguous = 0, top1 = 0, ambiguous = 0;
  const bins = Array.from({ length: 10 }, () => ({ n: 0, conf: 0, hit: 0 }));
  for (const row of scored) {
    const result = rank(row.candidates);
    if (row.chosen === null) { ambiguous++; if (result.decisive) ambiguousDecisive++; continue; }
    unambiguous++;
    const hit = result.selected === row.chosen;
    if (hit) top1++;
    if (result.decisive) { decisive++; if (hit) correct++; else wrong++; }
    if (Number.isFinite(result.confidence)) {
      const bin = bins[Math.min(9, Math.floor(result.confidence * 10))];
      bin.n++; bin.conf += result.confidence; bin.hit += hit ? 1 : 0;
    }
  }
  const ece = unambiguous ? bins.reduce((a, b) => a + (b.n ? Math.abs(b.conf / b.n - b.hit / b.n) * b.n : 0), 0) / unambiguous : null;
  const allDecisive = decisive + ambiguousDecisive;
  return { cases: scored.length, unambiguous, ambiguous, decisive: allDecisive, correctDecisive: correct, wrongDecisive: wrong,
    ambiguousDecisive, decisivePrecision: allDecisive ? correct / allDecisive : null,
    unambiguousRecall: unambiguous ? correct / unambiguous : null, top1Accuracy: unambiguous ? top1 / unambiguous : null,
    expectedCalibrationError: ece };
}

function calibrate(schema, weights, validation) {
  const labeled = validation.filter(row => row.chosen !== null && row.label !== 'adapter-weak');
  let best = { temperature: 1, nll: Infinity };
  for (let t = 0.25; t <= 8.0001; t += 0.25) {
    const rank = Inference.createRanker(seal(schema, weights, t, 0, 0.34, '0.0.0-calibration')).rank;
    let nll = 0;
    for (const row of labeled) {
      const p = rank(row.candidates).ranked.find(item => item.id === row.chosen)?.probability ?? 1e-9;
      nll -= Math.log(Math.max(p, 1e-9));
    }
    if (nll < best.nll - 1e-9) best = { temperature: Math.round(t * 100) / 100, nll };
  }
  // Thresholds maximize recall subject to >= 99% decisive precision, ambiguous cases counted as errors.
  let choice = null;
  for (let margin = 0; margin <= 6.0001; margin += 0.25)
    for (let confidence = 0.5; confidence <= 0.951; confidence += 0.05) {
      const rank = Inference.createRanker(seal(schema, weights, best.temperature, margin, confidence, '0.0.0-calibration')).rank;
      const m = evaluate(rank, validation);
      if ((m.decisivePrecision ?? 1) < 0.99) continue;
      const key = [m.unambiguousRecall ?? 0, margin, confidence];
      if (!choice || key[0] > choice.key[0] + 1e-12 || (Math.abs(key[0] - choice.key[0]) <= 1e-12 && (key[1] > choice.key[1] || (key[1] === choice.key[1] && key[2] > choice.key[2]))))
        choice = { key, margin: Math.round(margin * 100) / 100, minConfidence: Math.round(confidence * 100) / 100, metrics: m };
    }
    return { temperature: best.temperature, nll: best.nll, ...(choice || { margin: 1.25, minConfidence: 0.62, metrics: null }) };
}

function seal(schema, weights, temperature, margin, minConfidence, version) {
  return Inference.sealModel({ format: Inference.MODEL_FORMAT, schema, kind: 'calibrated-linear', version,
    weights, temperature, margin, minConfidence });
}

// Fixed safety scenarios every candidate must pass; a learned model is rejected if any fails.
export function guardFailures(rank, schema) {
  const failures = [];
  if (schema === 'vesper.player-intent/2') {
    const main = { id: 'main', area: 90e3, visibleFraction: 1, playing: true, ready: true, progressed: true, controls: true, muted: false, duration: 1200 };
    const checks = {
      'large-ad-not-decisive': [main, { id: 'ad', area: 500e3, visibleFraction: 1, playing: true, ready: true, controls: false, muted: false, adContext: true, duration: 20 }],
      'loop-not-decisive': [{ id: 'loop', area: 600e3, visibleFraction: 1, playing: true, ready: true, muted: true, incidentalLoop: true }],
      'conflicting-interaction': [{ ...main, id: 'big', area: 400e3 }, { ...main, id: 'clicked', area: 60e3, interacted: true }],
      'duplicate-identity': [main, { ...main }]
    };
    // Each check names the outcome that must never be decisive.
    const forbidden = { 'large-ad-not-decisive': r => r.selected === 'ad', 'loop-not-decisive': () => true,
      'conflicting-interaction': r => r.selected !== 'clicked', 'duplicate-identity': () => true };
    for (const [name, candidates] of Object.entries(checks)) {
      const r = rank(candidates);
      if (r.decisive && forbidden[name](r)) failures.push(name);
    }
  } else {
    const checks = {
      'stale-not-decisive': [{ id: 'old', playerBound: true, stale: true, currentGeneration: false }],
      'hypothesized-not-decisive': [{ id: 'guess', declared: true, hypothesized: true }],
      'fragment-not-decisive': [{ id: 'seg', playerBound: true, fragmentOnly: true }]
    };
    for (const [name, candidates] of Object.entries(checks)) if (rank(candidates).decisive) failures.push(name);
  }
  return failures;
}

export function fitSchema(schema, partitions, version) {
  validateCases(schema, partitions);
  const weights = fitWeights(schema, partitions.train);
  const cal = calibrate(schema, weights, partitions.validation);
  const model = { ...seal(schema, weights, cal.temperature, cal.margin, cal.minConfidence, version),
    provenance: { labels: 'synthetic-fixture-and-adapter-weak', independentRealLabels: 0 } };
  const candidate = Inference.createRanker(model).rank;
  const authored = Inference.BASELINES[schema];
  const baseline = Inference.createRanker(authored).rank;
  // Shippable without independent labels: authored weights, abstention thresholds chosen on validation lineages.
  const baseCal = calibrate(schema, { ...authored.weights }, partitions.validation);
  const patch = authored.version.replace(/^(\d+)\.(\d+)\.\d+-baseline$/, (_, major, minor) => `${major}.${minor}.1-baseline`);
  const calibratedBaseline = { ...seal(schema, { ...authored.weights }, baseCal.temperature, baseCal.margin, baseCal.minConfidence, patch),
    provenance: { labels: 'authored-weights-validation-thresholds', independentRealLabels: 0 } };
  const holdout = { candidate: evaluate(candidate, partitions.holdout), baseline: evaluate(baseline, partitions.holdout),
    calibratedBaseline: evaluate(Inference.createRanker(calibratedBaseline).rank, partitions.holdout) };
  if (schema === 'vesper.player-intent/2') holdout.legacyV1 = evaluate(LegacyIntelligence.rankPlayers, partitions.holdout);
  const guards = guardFailures(candidate, schema);
  const gates = {
    precision: (holdout.candidate.decisivePrecision ?? 0) >= 0.99,
    recall: (holdout.candidate.unambiguousRecall ?? 0) >= 0.95,
    // Recall is only comparable at equal safety: against a baseline below the precision target the
    // candidate must instead not be less precise.
    noRegression: (holdout.baseline.decisivePrecision ?? 1) >= 0.99
      ? (holdout.candidate.unambiguousRecall ?? 0) >= (holdout.baseline.unambiguousRecall ?? 0)
      : (holdout.candidate.decisivePrecision ?? 1) >= (holdout.baseline.decisivePrecision ?? 1),
    guards: guards.length === 0,
    independentRealData: false
  };
  return { schema, model, calibratedBaseline, calibratedBaselineGuards: guardFailures(Inference.createRanker(calibratedBaseline).rank, schema), calibration: { temperature: cal.temperature, margin: cal.margin, minConfidence: cal.minConfidence,
    validation: cal.metrics }, holdout, guards, gates,
    // Synthetic fixtures cannot qualify a production model; promotion requires independent labels.
    promotable: Object.values(gates).every(Boolean), deployed: false,
    sizes: Object.fromEntries(Object.entries(partitions).map(([k, v]) => [k, v.length])) };
}

function modelsFile(models) {
  const body = JSON.stringify({ models }, null, 1);
  return `// Generated by scripts/train-intelligence.mjs promote. Checksums are verified at load; edit the JSON\n`
    + `// artifacts under intelligence/models and re-promote instead of editing this file.\n'use strict';\n`
    + `globalThis.VesperModels = Object.freeze(${body});\n`;
}

function activeModels() {
  const dir = new URL('intelligence/models/active/', root);
  const models = {};
  if (existsSync(dir))
    for (const file of readdirSync(dir).filter(name => name.endsWith('.json')).sort()) {
      const model = JSON.parse(readFileSync(new URL(file, dir), 'utf8'));
      models[model.schema] = model;
    }
  return models;
}

function boundedJson(path, maxBytes) {
  if (!path || statSync(path).size > maxBytes) throw Error('Missing or oversized input');
  const raw = readFileSync(path);
  if (raw.length > maxBytes) throw Error('Oversized input');
  return { data: JSON.parse(raw.toString('utf8')), sha256: createHash('sha256').update(raw).digest('hex') };
}

// The operator must assign one opaque, stable review ID per independently
// collected case. Repeated copies of a case must not inflate the release gate.
// An ID is bookkeeping, not proof of real-world ground truth.
export function validateReviewIds(partitions) {
  const seen = new Set();
  for (const split of ['train', 'validation', 'holdout']) {
    for (const row of partitions[split]) {
      if (row.label !== 'verified' && row.label !== 'user') continue;
      if (typeof row.caseId !== 'string' || !/^[a-z0-9_-]{8,80}$/i.test(row.caseId))
        throw Error('Missing opaque independent review caseId in ' + split);
      const id = row.caseId.toLowerCase();
      if (seen.has(id)) throw Error('Repeated independent review caseId: ' + id);
      seen.add(id);
    }
  }
}

// Review is read-only. Label provenance remains the reviewer’s responsibility;
// fixture results and predictions cannot be substituted for reviewed cases.
export function reviewCoverage(schema, partitions) {
  validateCases(schema, partitions);
  validateReviewIds(partitions);
  const splits = {};
  for (const split of ['train', 'validation', 'holdout']) {
    const rows = partitions[split];
    const independent = rows.filter(row => row.label === 'verified' || row.label === 'user');
    const chosen = independent.filter(row => row.chosen !== null).length;
    const ambiguous = independent.length - chosen;
    const families = new Set(independent.map(row => row.group.toLowerCase())).size;
    splits[split] = { total: rows.length, independent: independent.length, chosen, ambiguous, families,
      missingChosen: Math.max(0, 200 - chosen), missingAmbiguous: Math.max(0, 30 - ambiguous),
      missingFamilies: Math.max(0, 10 - families),
      ready: independent.length === rows.length && chosen >= 200 && ambiguous >= 30 && families >= 10 };
  }
  return { splits, ready: splits.validation.ready && splits.holdout.ready };
}

export function promotionEvaluation(model, partitions, baseline) {
  Inference.validateModel(model);
  const coverage = reviewCoverage(model.schema, partitions);
  if (!coverage.ready)
    throw Error('Refusing to promote without sufficient independent real labels: ' + JSON.stringify(coverage.splits));
  const rank = Inference.createRanker(model).rank;
  const previous = Inference.createRanker(baseline).rank;
  const gates = {};
  for (const split of ['validation', 'holdout']) {
    const candidate = evaluate(rank, partitions[split]), reference = evaluate(previous, partitions[split]);
    const passed = (candidate.decisivePrecision ?? 0) >= 0.99 && (candidate.unambiguousRecall ?? 0) >= 0.95
      && candidate.ambiguousDecisive === 0 && (candidate.decisivePrecision ?? 0) >= (reference.decisivePrecision ?? 0)
      && (candidate.unambiguousRecall ?? 0) >= (reference.unambiguousRecall ?? 0);
    gates[split] = { candidate, reference, passed };
  }
  const failures = guardFailures(rank, model.schema);
  const { candidate, reference } = gates.holdout;
  // Merely reproducing the active model is not a demonstrated improvement.
  // Calibration may improve in isolation only when safety and recall do not regress.
  const improved = candidate.correctDecisive > reference.correctDecisive
    || candidate.wrongDecisive + candidate.ambiguousDecisive < reference.wrongDecisive + reference.ambiguousDecisive
    || (candidate.expectedCalibrationError !== null && reference.expectedCalibrationError !== null
      && reference.expectedCalibrationError - candidate.expectedCalibrationError >= 0.01);
  if (!improved) failures.push('no-measurable-holdout-improvement');
  if (failures.length || Object.values(gates).some(gate => !gate.passed))
    throw Error('Promotion gates failed: ' + JSON.stringify({ gates, failures }));
  return gates;
}

export function promote(path, { corpusPath } = {}) {
  const { data: model } = boundedJson(path, 64 * 1024);
  Inference.validateModel(model);
  if (!corpusPath) throw Error('Refusing to promote a model without independent real labels and a reviewed corpus');
  const { data: corpus, sha256 } = boundedJson(corpusPath, 32 * 1024 * 1024);
  const previousModel = activeModels()[model.schema];
  if (!previousModel) throw Error('Active baseline missing');
  const partitions = model.schema === 'vesper.player-intent/2' ? corpus.player : corpus.source;
  const gates = promotionEvaluation(model, partitions, previousModel);
  model.provenance = { labels: 'reviewed-independent-corpus', independentRealLabels: partitions.holdout.length,
    corpusSha256: sha256, evaluation: gates };
  const name = SCHEMA_FILES[model.schema];
  const active = new URL(`intelligence/models/active/${name}.json`, root);
  const history = new URL('intelligence/models/history/', root);
  mkdirSync(history, { recursive: true });
  mkdirSync(new URL('intelligence/models/active/', root), { recursive: true });
  if (existsSync(active)) {
    const previous = JSON.parse(readFileSync(active, 'utf8'));
    copyFileSync(active, new URL(`${name}@${previous.version}.json`, history));
  }
  writeFileSync(active, JSON.stringify(model, null, 1) + '\n');
  writeFileSync(new URL('extension/vesper-model.js', root), modelsFile(activeModels()));
  return model.version;
}

export function rollback(schema) {
  const name = SCHEMA_FILES[schema];
  if (!name) throw Error('Unknown schema');
  const history = new URL('intelligence/models/history/', root);
  const active = JSON.parse(readFileSync(new URL(`intelligence/models/active/${name}.json`, root), 'utf8'));
  const versions = existsSync(history) ? readdirSync(history).filter(file => file.startsWith(name + '@') && file !== `${name}@${active.version}.json`).sort() : [];
  if (!versions.length) throw Error('No rollback version');
  const previous = new URL(versions.at(-1), history);
  const restored = JSON.parse(readFileSync(previous, 'utf8'));
  Inference.validateModel(restored);
  if (restored.schema !== schema || guardFailures(Inference.createRanker(restored).rank, schema).length) throw Error('Invalid rollback artifact');
  writeFileSync(new URL(`intelligence/models/active/${name}.json`, root), readFileSync(previous));
  writeFileSync(new URL('extension/vesper-model.js', root), modelsFile(activeModels()));
  return JSON.parse(readFileSync(previous, 'utf8')).version;
}

export function verifyShipped() {
  const shipped = runtime({ withShippedModels: true }).VesperInference.status();
  if (shipped.player.fallback || shipped.source.fallback) throw Error('Shipped model invalid: ' + JSON.stringify(shipped));
  return shipped;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [command = 'fit', ...args] = process.argv.slice(2);
  if (command === 'fit') {
    const corpus = args[0] && args[0] !== '-' ? JSON.parse(readFileSync(args[0], 'utf8')) : generate();
    const out = args[1] || fileURLToPath(new URL('release/qa-intelligence/', root));
    mkdirSync(out, { recursive: true });
    const player = fitSchema('vesper.player-intent/2', corpus.player, '2.1.0-candidate');
    const source = fitSchema('vesper.source-association/1', corpus.source, '1.1.0-candidate');
    for (const result of [player, source])
    {
      writeFileSync(`${out}/${SCHEMA_FILES[result.schema]}-candidate.json`, JSON.stringify(result.model, null, 1) + '\n');
      writeFileSync(`${out}/${SCHEMA_FILES[result.schema]}-baseline-calibrated.json`, JSON.stringify(result.calibratedBaseline, null, 1) + '\n');
    }
    const report = { corpus: { schema: corpus.schema, seed: corpus.seed, holdoutArchetypes: corpus.holdoutArchetypes }, player, source };
    writeFileSync(`${out}/report.json`, JSON.stringify(report, null, 1) + '\n');
    console.log(JSON.stringify({ out, player: { holdout: player.holdout, gates: player.gates }, source: { holdout: source.holdout, gates: source.gates } }, null, 1));
  } else if (command === 'review') {
    const { data: corpus } = boundedJson(args[0], 32 * 1024 * 1024);
    const models = activeModels(), report = {};
    for (const [kind, schema] of [['player', 'vesper.player-intent/2'], ['source', 'vesper.source-association/1']]) {
      const partitions = corpus[kind];
      const coverage = reviewCoverage(schema, partitions);
      const rank = Inference.createRanker(models[schema]).rank;
      report[kind] = { ...coverage, baseline: Object.fromEntries(['validation', 'holdout'].map(split =>
        [split, evaluate(rank, partitions[split].filter(row => row.label === 'verified' || row.label === 'user'))])) };
    }
    console.log(JSON.stringify(report, null, 2));
  } else if (command === 'promote') console.log('promoted ' + promote(args[0], { corpusPath: args[1] }));
  else if (command === 'rollback') console.log('restored ' + rollback(args[0]));
  else if (command === 'verify') console.log(JSON.stringify(verifyShipped()));
  else if (command === 'sign-pack') {
    const [packPath, privateKeyPemPath] = args;
    const pack = JSON.parse(readFileSync(packPath, 'utf8'));
    const keyPem = readFileSync(privateKeyPemPath, 'utf8');
    if (pack.schema !== 'vesper.extractor-pack/1' || !Number.isSafeInteger(pack.version))
      throw Error('Expected an unsigned VESPER extractor pack.');
    const payload = Buffer.from(JSON.stringify(pack));
    const signature = cryptoSign('sha256', payload, { key: keyPem, dsaEncoding: 'ieee-p1363' });
    if (signature.length !== 64) throw Error('Use a P-256 signing key.');
    const signed = { payloadBase64: payload.toString('base64'), signatureBase64: signature.toString('base64') };
    writeFileSync(packPath, JSON.stringify(signed, null, 2) + '\n');
    console.log('signed ' + packPath + ' v' + pack.version);
  }
  else throw Error('Unknown command ' + command);
}
