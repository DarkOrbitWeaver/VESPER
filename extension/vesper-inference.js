// VESPER local inference runtime: versioned feature schemas, validated calibrated-linear models,
// multi-candidate ranking with explicit abstention. Pure functions; no DOM, network, timers or storage.
// A result can order candidates or anchor presentation. It never authorizes acquisition.
'use strict';
globalThis.VesperInference = (() => {
  const MODEL_FORMAT = 'vesper.model/1';
  const MAX_CANDIDATES = 256;
  const MAX_ID = 128;
  const WEIGHT_LIMIT = 16;

  const unit = value => Number.isFinite(value) ? Math.max(0, Math.min(1, value)) : null;
  const flag = value => value === true ? 1 : value === false ? 0 : null;
  const known = value => value !== null && value !== undefined;

  // Duration is a weak long-form signal: 15 s or less contributes nothing, 10 min or more contributes fully.
  // A live stream (Infinity) is long-form by construction.
  function longForm(duration) {
    if (duration === Infinity) return 1;
    if (!Number.isFinite(duration) || duration <= 0) return null;
    if (duration <= 15) return 0;
    return Math.min(1, Math.log(duration / 15) / Math.log(40));
  }

  // Sign constraints are part of the schema, not the model. They stop a learned model from
  // rewarding advertisements, incidental loops, stale generations or hypothesized sources.
  const SCHEMAS = Object.freeze({
    'vesper.player-intent/2': Object.freeze({
      features: Object.freeze(['area', 'visible', 'central', 'playing', 'ready', 'progressed', 'controls',
        'audiblePlayback', 'incidentalLoop', 'interaction', 'incumbent', 'networkBound', 'declaredSource',
        'longForm', 'adContext']),
      negative: Object.freeze(['incidentalLoop', 'adContext']),
      row(candidate, context) {
        const incidental = candidate.incidentalLoop === true && candidate.interacted !== true;
        const playing = flag(candidate.playing);
        return {
          area: Math.sqrt(candidate.area / context.largestArea),
          visible: unit(candidate.visibleFraction) ?? 0,
          central: Number.isFinite(candidate.centerDistance) ? 1 - Math.min(1, Math.max(0, candidate.centerDistance)) : null,
          playing: playing ?? 0,
          ready: flag(candidate.ready) ?? 0,
          progressed: flag(candidate.progressed) ?? 0,
          controls: flag(candidate.controls) ?? 0,
          audiblePlayback: candidate.playing === true && candidate.muted === false ? 1 : 0,
          incidentalLoop: incidental ? 1 : 0,
          interaction: flag(candidate.interacted) ?? 0,
          incumbent: flag(candidate.incumbent) ?? 0,
          networkBound: flag(candidate.networkBound),
          declaredSource: flag(candidate.declaredSource),
          longForm: longForm(candidate.duration),
          adContext: flag(candidate.adContext)
        };
      },
      usable: candidate => Number.isFinite(candidate.area) && candidate.area > 0,
      veto(best, rows) {
        if (best.facts.incidentalLoop === 1) return 'incidental-loop';
        if (best.facts.adContext === 1 && best.facts.interaction !== 1) return 'advertisement-context';
        if (best.facts.interaction !== 1 && rows.some(row => row !== best && row.facts.interaction === 1))
          return 'conflicting-interaction';
        return null;
      },
      // A unique explicit gesture can resolve a narrow ranking margin, but never a veto.
      explicit: (best, rows) => best.facts.interaction === 1 && rows.filter(row => row.facts.interaction === 1).length === 1
    }),
    'vesper.source-association/1': Object.freeze({
      features: Object.freeze(['exactPlayerUrl', 'playerBound', 'currentGeneration', 'manifestParent',
        'resourceWitness', 'wholeFile', 'declared', 'durationAgreement', 'adContext', 'hypothesized',
        'stale', 'fragmentOnly']),
      negative: Object.freeze(['adContext', 'hypothesized', 'stale', 'fragmentOnly']),
      row(candidate) {
        return {
          exactPlayerUrl: flag(candidate.exactPlayerUrl) ?? 0,
          playerBound: flag(candidate.playerBound) ?? 0,
          currentGeneration: flag(candidate.currentGeneration),
          manifestParent: flag(candidate.manifestParent),
          resourceWitness: flag(candidate.resourceWitness) ?? 0,
          wholeFile: flag(candidate.wholeFile),
          declared: flag(candidate.declared) ?? 0,
          durationAgreement: unit(candidate.durationAgreement),
          adContext: flag(candidate.adContext),
          hypothesized: flag(candidate.hypothesized) ?? 0,
          stale: flag(candidate.stale) ?? 0,
          fragmentOnly: flag(candidate.fragmentOnly) ?? 0
        };
      },
      usable: () => true,
      veto(best) {
        if (best.facts.stale === 1 || best.facts.currentGeneration === 0) return 'stale-generation';
        if (best.facts.fragmentOnly === 1) return 'fragment-only';
        if (best.facts.adContext === 1) return 'advertisement-context';
        // A source inferred from markup alone may be shown, never chosen automatically.
        if (best.facts.hypothesized === 1 && best.facts.exactPlayerUrl !== 1 && best.facts.resourceWitness !== 1)
          return 'hypothesized-source';
        if (best.facts.exactPlayerUrl !== 1 && best.facts.resourceWitness !== 1 && best.facts.manifestParent !== 1)
          return 'unproven-association';
        return null;
      }
    })
  });

  function fixed(value) { return (Math.round(value * 1e6) / 1e6).toFixed(6); }
  // FNV-1a over a canonical ASCII description. Detects corruption and accidental edits; it is not a signature.
  function checksum(model) {
    const schema = SCHEMAS[model.schema];
    const text = [model.schema, model.version, model.kind,
      schema.features.map(name => name + '=' + fixed(model.weights[name])).join(','),
      't=' + fixed(model.temperature), 'm=' + fixed(model.margin), 'c=' + fixed(model.minConfidence)].join('|');
    let hash = 0x811c9dc5;
    for (let i = 0; i < text.length; i++) {
      hash ^= text.charCodeAt(i) & 0xff;
      hash = Math.imul(hash, 0x01000193) >>> 0;
    }
    return hash.toString(16).padStart(8, '0');
  }

  function validateModel(model) {
    if (!model || model.format !== MODEL_FORMAT) throw new Error('Unsupported model format');
    const schema = SCHEMAS[model.schema];
    if (!schema) throw new Error('Unknown feature schema');
    if (model.kind !== 'calibrated-linear') throw new Error('Unsupported model kind');
    if (typeof model.version !== 'string' || !/^[0-9]+\.[0-9]+\.[0-9]+(?:-[a-z0-9.]{1,40})?$/.test(model.version))
      throw new Error('Invalid model version');
    if (!model.weights || typeof model.weights !== 'object') throw new Error('Missing weights');
    const names = Object.keys(model.weights);
    if (names.length !== schema.features.length || names.some(name => !schema.features.includes(name)))
      throw new Error('Model features do not match schema');
    const weights = {};
    for (const name of schema.features) {
      const weight = model.weights[name];
      if (!Number.isFinite(weight) || Math.abs(weight) > WEIGHT_LIMIT) throw new Error('Invalid weight: ' + name);
      if (schema.negative.includes(name) ? weight > 0 : weight < 0) throw new Error('Weight violates sign constraint: ' + name);
      weights[name] = weight;
    }
    const { temperature, margin, minConfidence } = model;
    if (!Number.isFinite(temperature) || temperature < 0.05 || temperature > 20) throw new Error('Invalid temperature');
    if (!Number.isFinite(margin) || margin < 0 || margin > WEIGHT_LIMIT) throw new Error('Invalid margin');
    if (!Number.isFinite(minConfidence) || minConfidence < 0.34 || minConfidence > 0.999) throw new Error('Invalid confidence threshold');
    if (model.checksum !== checksum(model)) throw new Error('Model checksum mismatch');
    return Object.freeze({ format: MODEL_FORMAT, schema: model.schema, kind: model.kind, version: model.version,
      weights: Object.freeze(weights), temperature, margin, minConfidence, checksum: model.checksum });
  }

  function featureRows(schemaName, candidates) {
    const schema = SCHEMAS[schemaName];
    if (!schema || !Array.isArray(candidates)) return { rows: [], truncated: false, duplicate: false };
    const truncated = candidates.length > MAX_CANDIDATES;
    const seen = new Set(), usable = [];
    let duplicate = false, largestArea = 0;
    for (const candidate of candidates.slice(0, MAX_CANDIDATES)) {
      if (!candidate || typeof candidate.id !== 'string' || !candidate.id || candidate.id.length > MAX_ID
          || !schema.usable(candidate)) continue;
      if (seen.has(candidate.id)) { duplicate = true; continue; }
      seen.add(candidate.id);
      usable.push(candidate);
      if (Number.isFinite(candidate.area)) largestArea = Math.max(largestArea, candidate.area);
    }
    const context = { largestArea: largestArea || 1 };
    return { rows: usable.map(candidate => ({ id: candidate.id, facts: schema.row(candidate, context) })), truncated, duplicate };
  }

  function createRanker(input) {
    const model = validateModel(input);
    const schema = SCHEMAS[model.schema];
    function rank(candidates) {
      const { rows, truncated, duplicate } = featureRows(model.schema, candidates);
      const base = { model: model.version, schema: model.schema };
      if (!rows.length) return { ...base, selected: null, decisive: false, abstained: true, confidence: 0,
        reason: 'no-candidate', ranked: [] };
      // A feature that nobody reported is absent evidence. A feature reported for only some
      // candidates is uncertainty: an unreported value could still change the order.
      const active = schema.features.filter(name => rows.some(row => known(row.facts[name])));
      const scored = rows.map(row => {
        const terms = {}, missing = [];
        let score = 0, upside = 0, downside = 0;
        for (const name of active) {
          const weight = model.weights[name];
          const value = row.facts[name];
          if (!known(value)) {
            missing.push(name);
            if (weight > 0) upside += weight; else downside += weight;
            continue;
          }
          terms[name] = value * weight;
          score += terms[name];
        }
        return { id: row.id, facts: row.facts, score, upside, downside, terms, missing };
      }).sort((a, b) => b.score - a.score || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
      const top = scored[0].score;
      const exps = scored.map(row => Math.exp((row.score - top) / model.temperature));
      const total = exps.reduce((a, b) => a + b, 0);
      const ranked = scored.map((row, i) => ({ id: row.id, score: row.score, probability: exps[i] / total,
        terms: row.terms, missing: row.missing }));
      const [best, second] = scored;
      const confidence = ranked[0].probability;
      const margin = second ? best.score - second.score : Infinity;
      const robust = scored.slice(1).every(row => best.score + best.downside >= row.score + row.upside);
      const veto = truncated ? 'candidate-limit' : duplicate ? 'duplicate-identity' : schema.veto(best, scored);
      const reason = veto || (schema.explicit?.(best, scored) ? 'explicit-interaction' : !robust ? 'missing-evidence'
        : margin < model.margin || confidence < model.minConfidence ? 'competing-candidates' : 'leading-evidence');
      const decisive = reason === 'leading-evidence' || reason === 'explicit-interaction';
      const conflicts = second && margin < model.margin
        ? Object.keys(best.terms).filter(name => (second.terms[name] ?? 0) > best.terms[name]) : [];
      return { ...base, selected: best.id, decisive, abstained: !decisive, confidence, margin: Number.isFinite(margin) ? margin : null,
        reason, conflicts, ranked };
    }
    return Object.freeze({ rank, model });
  }

  // Loads a deployable model and falls back to the authored baseline if it is missing, corrupt or
  // incompatible. The fallback is reported so diagnostics never mistake it for the shipped candidate.
  function loadRanker(model, baseline) {
    try { return { ...createRanker(model), fallback: false, error: null }; }
    catch (error) {
      const safe = createRanker(baseline);
      return { ...safe, rank: candidates => {
        const result = safe.rank(candidates);
        return { ...result, decisive: false, abstained: true, reason: 'model-fallback' };
      }, fallback: true, error: error.message };
    }
  }

  function sealModel(model) { return { ...model, checksum: checksum({ ...model, format: MODEL_FORMAT }) }; }

  const BASELINES = Object.freeze({
    'vesper.player-intent/2': sealModel({ format: MODEL_FORMAT, schema: 'vesper.player-intent/2', kind: 'calibrated-linear',
      version: '2.0.0-baseline', temperature: 1.5, margin: 1.25, minConfidence: 0.6, weights: {
        area: 5, visible: 1.25, central: 0.75, playing: 2, ready: 0.5, progressed: 0.6, controls: 1,
        audiblePlayback: 1.25, incidentalLoop: -5, interaction: 6, incumbent: 0.75, networkBound: 1.5,
        declaredSource: 1, longForm: 1, adContext: -4 } }),
    'vesper.source-association/1': sealModel({ format: MODEL_FORMAT, schema: 'vesper.source-association/1', kind: 'calibrated-linear',
      version: '1.0.0-baseline', temperature: 2, margin: 2, minConfidence: 0.6, weights: {
        exactPlayerUrl: 8, playerBound: 4, currentGeneration: 3, manifestParent: 3, resourceWitness: 8,
        wholeFile: 1, declared: 1.5, durationAgreement: 2, adContext: -6, hypothesized: -3, stale: -8, fragmentOnly: -6 } })
  });

  const shipped = globalThis.VesperModels?.models || {};
  const player = loadRanker(shipped['vesper.player-intent/2'], BASELINES['vesper.player-intent/2']);
  const source = loadRanker(shipped['vesper.source-association/1'], BASELINES['vesper.source-association/1']);

  return Object.freeze({ MODEL_FORMAT, SCHEMAS, BASELINES, checksum, sealModel, validateModel, featureRows,
    createRanker, loadRanker, longForm,
    rankPlayers: player.rank, rankSources: source.rank,
    status: () => ({ player: { version: player.model.version, fallback: player.fallback, error: player.error },
      source: { version: source.model.version, fallback: source.fallback, error: source.error } }) });
})();
