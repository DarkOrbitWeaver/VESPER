// Standalone use of the browser-side intelligence modules outside the extension (Node 20+):
//   node samples/standalone-intelligence.mjs
import vm from 'node:vm';
import { readFileSync } from 'node:fs';

const context = vm.createContext({ URL });
for (const name of ['vesper-model.js', 'vesper-inference.js', 'vesper-evidence-graph.js', 'vesper-planner.js'])
  vm.runInContext(readFileSync(new URL(`../extension/${name}`, import.meta.url), 'utf8'), context);
const { VesperInference, VesperEvidenceGraph, VesperPlanner } = context;

const players = VesperInference.rankPlayers([
  { id: 'main', area: 230e3, visibleFraction: 1, playing: true, ready: true, progressed: true, controls: true, muted: false, duration: 2400, networkBound: true, adContext: false },
  { id: 'sidebar-ad', area: 420e3, visibleFraction: 1, playing: true, ready: true, controls: false, muted: false, duration: 20, networkBound: true, adContext: true },
]);
console.log('player:', players.selected, players.decisive, players.reason, players.confidence.toFixed(3));

const scope = '1:0:document-sample';
const graph = VesperEvidenceGraph.create();
graph.observePlayer({ scope, playerKey: 'main', generation: 1, at: Date.now() - 1000 });
graph.observeRequest({ scope, url: 'https://media.example/show/master.m3u8', kind: 'hls', playerKey: 'main', generation: 1 });
graph.observeRequest({ scope, url: 'https://media.example/show/v720/segment1.m4s', kind: 'fragment' });
const { candidates } = graph.associate({ scope, playerKey: 'main' });
const source = VesperInference.rankSources(candidates);
console.log('source:', source.selected, source.decisive, source.reason);

const plan = VesperPlanner.plan({ intent: { explicit: true, scoped: true, wants: 'media' },
  selection: { decisive: players.decisive, ambiguous: !players.decisive },
  sources: candidates.map(c => ({ key: c.url, kind: c.kind, provenance: c.provenance, verified: false })),
  capabilities: { adapter: false, generic: true } });
console.log('plan:', plan.status, plan.next?.route, plan.reason);
