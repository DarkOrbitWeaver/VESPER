// Development tool: deterministic controlled corpus for player-intent and source-association models.
// Every case belongs to a lineage (structural archetype). Lineages, never individual cases, are assigned
// to train / validation / holdout, and the holdout contains archetypes the model never sees in training.
// Labels are fixture truth by construction ("fixture") or adapter-derived ("adapter-weak"). Weak labels
// may help training but are excluded from every evaluation metric. No URLs, text or identities are stored.
import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { distill } from './adapter-knowledge.mjs';

function rng(seed) {
  let state = seed >>> 0 || 1;
  return () => { state ^= state << 13; state >>>= 0; state ^= state >>> 17; state ^= state << 5; state >>>= 0; return state / 4294967296; };
}
const pick = (random, list) => list[Math.floor(random() * list.length)];
const between = (random, low, high) => low + (high - low) * random();
const chance = (random, p) => random() < p;

// Player roles: realistic fact distributions, varied independently of markup.
const PLAYER_ROLES = {
  main: r => ({ area: between(r, 120e3, 600e3), visibleFraction: between(r, 0.7, 1), centerDistance: between(r, 0, 0.45),
    playing: chance(r, 0.8), ready: true, progressed: chance(r, 0.7), controls: chance(r, 0.85), muted: chance(r, 0.15),
    incidentalLoop: false, interacted: false, incumbent: chance(r, 0.5), duration: between(r, 90, 5400),
    networkBound: chance(r, 0.85), adContext: false }),
  ad: r => ({ area: between(r, 80e3, 700e3), visibleFraction: between(r, 0.6, 1), centerDistance: between(r, 0.1, 0.8),
    playing: true, ready: true, progressed: chance(r, 0.5), controls: chance(r, 0.25), muted: chance(r, 0.5),
    incidentalLoop: false, interacted: false, incumbent: false, duration: between(r, 6, 32),
    networkBound: chance(r, 0.7), adContext: chance(r, 0.75) }),
  loop: r => ({ area: between(r, 150e3, 900e3), visibleFraction: between(r, 0.5, 1), centerDistance: between(r, 0, 0.7),
    playing: true, ready: true, progressed: true, controls: false, muted: true, incidentalLoop: true, interacted: false,
    incumbent: false, duration: between(r, 4, 30), networkBound: chance(r, 0.6), adContext: false }),
  preview: r => ({ area: between(r, 20e3, 90e3), visibleFraction: between(r, 0.6, 1), centerDistance: between(r, 0.3, 1),
    playing: true, ready: true, progressed: chance(r, 0.4), controls: false, muted: true, incidentalLoop: chance(r, 0.5),
    interacted: false, incumbent: false, duration: between(r, 5, 60), networkBound: chance(r, 0.5), adContext: false }),
  recommendation: r => ({ area: between(r, 20e3, 110e3), visibleFraction: between(r, 0.3, 1), centerDistance: between(r, 0.4, 1),
    playing: false, ready: chance(r, 0.6), progressed: false, controls: chance(r, 0.3), muted: chance(r, 0.5),
    incidentalLoop: false, interacted: false, incumbent: false, duration: chance(r, 0.5) ? between(r, 60, 1800) : null,
    networkBound: false, adContext: false }),
  mini: r => ({ area: between(r, 30e3, 90e3), visibleFraction: 1, centerDistance: between(r, 0.6, 1),
    playing: true, ready: true, progressed: true, controls: true, muted: false, incidentalLoop: false, interacted: true,
    incumbent: true, duration: between(r, 120, 3600), networkBound: true, adContext: false })
};

// Archetypes: roles present and which role is the intended target (null = genuinely ambiguous).
const PLAYER_ARCHETYPES = {
  'article-sidebar-ad': { roles: ['main', 'ad'], target: 'main' },
  'hero-loop-with-program': { roles: ['loop', 'main'], target: 'main' },
  'feed-with-previews': { roles: ['main', 'preview', 'preview', 'recommendation'], target: 'main' },
  'watch-page-recommendations': { roles: ['main', 'recommendation', 'recommendation', 'recommendation'], target: 'main' },
  'outstream-ad-in-article': { roles: ['main', 'ad', 'recommendation'], target: 'main' },
  'mini-player-over-feed': { roles: ['mini', 'preview', 'recommendation'], target: 'mini' },
  'interacted-small-player': { roles: ['main', 'main'], target: 'interacted', interact: 1 },
  'twin-equal-players': { roles: ['main', 'main'], target: null, twin: true },
  'gallery-of-paused-clips': { roles: ['recommendation', 'recommendation', 'recommendation'], target: null },
  'loop-only-landing': { roles: ['loop'], target: null },
  'preroll-ad-over-program': { roles: ['ad', 'main'], target: 'main', paused: 'main' },
  'background-loop-and-mini': { roles: ['loop', 'mini'], target: 'mini' },
  'lecture-with-previews': { roles: ['main', 'preview', 'preview'], target: 'main' },
  'live-stream-with-ad': { roles: ['main', 'ad'], target: 'main', live: true },
  'split-screen-equal-programs': { roles: ['main', 'main', 'recommendation'], target: null, twin: true },
  'muted-loop-pair': { roles: ['loop', 'loop'], target: null }
};
// Archetypes never present in training: they measure generalization to unfamiliar structures.
const HOLDOUT_ARCHETYPES = new Set(['outstream-ad-in-article', 'background-loop-and-mini', 'lecture-with-previews', 'live-stream-with-ad',
  'split-screen-equal-programs', 'muted-loop-pair']);

function playerCase(name, spec, random, lineage) {
  const candidates = spec.roles.map((role, index) => ({ role, facts: PLAYER_ROLES[role](random), index }));
  if (spec.twin) {
    const base = candidates[0].facts;
    candidates[1].facts = { ...base, area: base.area * between(random, 0.92, 1.08), incumbent: false };
  }
  if (spec.paused) for (const c of candidates) if (c.role === spec.paused) Object.assign(c.facts, { playing: false, progressed: false });
  if (spec.live) for (const c of candidates) if (c.role === 'main') c.facts.duration = 600;
  let targetIndex = spec.target === null ? null : candidates.findIndex(c => c.role === spec.target);
  if (spec.target === 'interacted') {
    const small = candidates[spec.interact];
    Object.assign(small.facts, { interacted: true, area: candidates[0].facts.area * between(random, 0.25, 0.6) });
    targetIndex = spec.interact;
  }
  // Structural mutations: renamed identities, reordered DOM, missing late metadata, partial network evidence.
  for (const c of candidates) {
    if (chance(random, 0.2)) c.facts.duration = null;
    if (chance(random, 0.15)) delete c.facts.networkBound;
    if (chance(random, 0.1)) delete c.facts.centerDistance;
  }
  const order = candidates.map((c, i) => [random(), i]).sort((a, b) => a[0] - b[0]).map(([, i]) => i);
  const ids = candidates.map(() => 'p' + Math.floor(random() * 1e9).toString(36));
  return { lineage, group: lineage, archetype: name, label: 'fixture',
    chosen: targetIndex === null ? null : ids[targetIndex],
    candidates: order.map(i => ({ id: ids[i], ...candidates[i].facts })) };
}

const SOURCE_ROLES = {
  exact: () => ({ exactPlayerUrl: true, playerBound: true, currentGeneration: true, wholeFile: true, kind: 'direct' }),
  bound: r => ({ playerBound: true, currentGeneration: true, manifestParent: chance(r, 0.8), kind: 'hls' }),
  witnessed: () => ({ playerBound: true, currentGeneration: true, resourceWitness: true, kind: 'dash' }),
  adManifest: r => ({ playerBound: chance(r, 0.6), currentGeneration: true, manifestParent: chance(r, 0.4), adContext: chance(r, 0.7), kind: 'hls' }),
  stale: r => ({ playerBound: true, currentGeneration: false, stale: true, manifestParent: chance(r, 0.7), kind: 'hls' }),
  declared: r => ({ declared: true, hypothesized: true, wholeFile: chance(r, 0.5), kind: 'direct' }),
  fragment: () => ({ playerBound: true, currentGeneration: true, fragmentOnly: true, wholeFile: false, kind: 'fragment' }),
  unboundTwin: r => ({ playerBound: true, currentGeneration: true, manifestParent: null, kind: pick(r, ['hls', 'dash']) })
};
const SOURCE_ARCHETYPES = {
  'progressive-with-declared': { roles: ['exact', 'declared'], target: 'exact' },
  'hls-with-ad-manifest': { roles: ['bound', 'adManifest'], target: 'bound', clearAd: true },
  'reused-player-new-generation': { roles: ['bound', 'stale'], target: 'bound' },
  'mse-witnessed-dash': { roles: ['witnessed', 'fragment', 'declared'], target: 'witnessed' },
  'two-unbound-manifests': { roles: ['unboundTwin', 'unboundTwin'], target: null },
  'declared-only': { roles: ['declared', 'declared'], target: null },
  'stale-only': { roles: ['stale'], target: null },
  'exact-with-stale-and-ad': { roles: ['exact', 'stale', 'adManifest'], target: 'exact' },
  'witnessed-beside-bound-ad': { roles: ['witnessed', 'adManifest', 'fragment'], target: 'witnessed' },
  'declared-beside-stale': { roles: ['declared', 'stale'], target: null }
};
const SOURCE_HOLDOUT = new Set(['exact-with-stale-and-ad', 'witnessed-beside-bound-ad', 'declared-beside-stale']);

function sourceCase(name, spec, random, lineage) {
  const candidates = spec.roles.map(role => ({ role, facts: SOURCE_ROLES[role](random) }));
  if (spec.clearAd) for (const c of candidates) if (c.role === 'adManifest') c.facts.manifestParent = false;
  const ids = candidates.map(() => 's' + Math.floor(random() * 1e9).toString(36));
  const target = spec.target === null ? null : candidates.findIndex(c => c.role === spec.target);
  const order = candidates.map((c, i) => [random(), i]).sort((a, b) => a[0] - b[0]).map(([, i]) => i);
  return { lineage, group: lineage, archetype: name, label: 'fixture', chosen: target === null ? null : ids[target],
    candidates: order.map(i => { const { kind, ...facts } = candidates[i].facts; return { id: ids[i], ...facts }; }) };
}

// Adapter knowledge as weak teacher: each adapter's concept profile yields a lineage whose label is the
// adapter's expected program. These labels never enter validation or holdout.
function adapterCases(knowledge, random, perAdapter) {
  const cases = [];
  for (const { adapter, concepts } of knowledge.adapters) {
    const roles = ['main'];
    if (concepts.hlsManifests || concepts.dashManifests) roles.push('preview');
    if (concepts.programIdentity) roles.push('recommendation');
    if (concepts.refusalHandling) roles.push('ad');
    for (let i = 0; i < perAdapter; i++) {
      const c = playerCase('adapter-profile', { roles, target: 'main' }, random, 'adapter-' + adapter);
      c.label = 'adapter-weak';
      cases.push(c);
    }
  }
  return cases;
}

function assign(lineage, holdoutArchetype, index) {
  if (holdoutArchetype) return 'holdout';
  return index % 4 === 3 ? 'validation' : 'train';
}

export function generate({ seed = 20260705, perLineage = 12, lineagesPerArchetype = 4, includeAdapterTeachers = true } = {}) {
  const random = rng(seed);
  const player = { train: [], validation: [], holdout: [] };
  const source = { train: [], validation: [], holdout: [] };
  for (const [name, spec] of Object.entries(PLAYER_ARCHETYPES))
    for (let l = 0; l < lineagesPerArchetype; l++) {
      const lineage = `${name}.lineage${l}`;
      const split = assign(lineage, HOLDOUT_ARCHETYPES.has(name), l);
      for (let i = 0; i < perLineage; i++) player[split].push(playerCase(name, spec, random, lineage));
    }
  for (const [name, spec] of Object.entries(SOURCE_ARCHETYPES))
    for (let l = 0; l < lineagesPerArchetype; l++) {
      const lineage = `${name}.lineage${l}`;
      const split = assign(lineage, SOURCE_HOLDOUT.has(name), l);
      for (let i = 0; i < perLineage; i++) source[split].push(sourceCase(name, spec, random, lineage));
    }
  if (includeAdapterTeachers) player.train.push(...adapterCases(distill(), random, 6));
  return { schema: 'vesper.intelligence-corpus/1', seed, player, source,
    holdoutArchetypes: { player: [...HOLDOUT_ARCHETYPES], source: [...SOURCE_HOLDOUT] } };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const corpus = generate();
  const output = process.argv[2] || 'intelligence-corpus.json';
  writeFileSync(output, JSON.stringify(corpus));
  const count = set => Object.fromEntries(Object.entries(set).map(([k, v]) => [k, v.length]));
  console.log(JSON.stringify({ output, player: count(corpus.player), source: count(corpus.source) }));
}
