// Development tool: live probe of public test media through the evidence graph, source model and planner.
// Reads at most 64 KiB per resource, sends no cookies or credentials, and writes nothing to disk.
//   node tools/intelligence/live-probe.mjs
import vm from 'node:vm';
import { readFileSync } from 'node:fs';

const context = vm.createContext({ URL });
for (const name of ['vesper-model.js', 'vesper-inference.js', 'vesper-evidence-graph.js', 'vesper-planner.js'])
  vm.runInContext(readFileSync(new URL(`../../extension/${name}`, import.meta.url), 'utf8'), context);
const { VesperInference, VesperEvidenceGraph, VesperPlanner } = context;

const CASES = [
  { name: 'apple-bipbop-hls', url: 'https://devstreaming-cdn.apple.com/videos/streaming/examples/bipbop_4x3/bipbop_4x3_variant.m3u8', kind: 'hls', expect: 'native-hls' },
  { name: 'akamai-envivio-dash', url: 'https://dash.akamaized.net/envivio/EnvivioDash3/manifest.mpd', kind: 'dash', expect: 'native-dash' },
  { name: 'mux-test-hls', url: 'https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8', kind: 'hls', expect: 'native-hls' },
  { name: 'w3c-mp4-direct', url: 'https://media.w3.org/2010/05/sintel/trailer.mp4', kind: 'direct', expect: 'native-segmented' },
  { name: 'w3c-webm-direct', url: 'https://media.w3.org/2010/05/sintel/trailer.webm', kind: 'direct', expect: 'native-segmented' },
  { name: 'html-page-not-media', url: 'https://example.com/', kind: 'direct', expect: null }
];

async function head(url) {
  const response = await fetch(url, { headers: { Range: 'bytes=0-65535' }, redirect: 'follow', signal: AbortSignal.timeout(15000) });
  const reader = response.body.getReader();
  const chunks = []; let size = 0;
  while (size < 65536) { const { done, value } = await reader.read(); if (done) break; chunks.push(value); size += value.length; }
  await reader.cancel().catch(() => {});
  const bytes = Buffer.concat(chunks).subarray(0, 65536);
  const total = /\/(\d+)$/.exec(response.headers.get('content-range') || '')?.[1];
  return { status: response.status, type: response.headers.get('content-type') || '', ranges: response.status === 206,
    length: total ? Number(total) : Number(response.headers.get('content-length')) || null, etag: response.headers.get('etag'), bytes };
}

function verify(kind, probe) {
  const text = probe.bytes.subarray(0, 4096).toString('utf8');
  if (kind === 'hls') return text.startsWith('#EXTM3U') ? { ok: true, detail: (text.match(/#EXT-X-STREAM-INF/g) || []).length + ' variants' } : { ok: false, detail: 'not an HLS playlist' };
  if (kind === 'dash') return /<MPD[\s>]/.test(text) ? { ok: true, detail: (text.match(/<Representation/g) || []).length + ' representations' } : { ok: false, detail: 'not a DASH manifest' };
  const b = probe.bytes;
  if (b.subarray(4, 8).toString('latin1') === 'ftyp') return { ok: true, detail: 'ISO BMFF ' + b.subarray(8, 12).toString('latin1') };
  if (b[0] === 0x1a && b[1] === 0x45 && b[2] === 0xdf && b[3] === 0xa3) return { ok: true, detail: 'EBML/WebM' };
  return { ok: false, detail: /html/i.test(probe.type) || /<html/i.test(text) ? 'HTML document, not media' : 'unrecognized bytes' };
}

const results = [];
for (const item of CASES) {
  const scope = '1:0:document-live-' + item.name;
  try {
    const probe = await head(item.url);
    const verdict = verify(item.kind, probe);
    const graph = VesperEvidenceGraph.create();
    graph.observePlayer({ scope, playerKey: 'p', generation: 1, sourceUrl: item.kind === 'direct' ? item.url : '', at: Date.now() - 1000 });
    if (item.kind !== 'direct') graph.observeRequest({ scope, url: item.url, kind: item.kind, playerKey: 'p', generation: 1 });
    const { candidates } = graph.associate({ scope, playerKey: 'p' });
    const association = VesperInference.rankSources(candidates);
    const plan = VesperPlanner.plan({ intent: { explicit: true, scoped: true, wants: 'media' },
      selection: { decisive: association.decisive, ambiguous: !association.decisive },
      sources: candidates.map(c => ({ key: c.url, kind: item.kind, provenance: c.provenance, verified: verdict.ok,
        acceptsRanges: probe.ranges, length: probe.length })),
      capabilities: { generic: true } });
    const route = plan.status === 'act' ? plan.next.route : null;
    results.push({ name: item.name, http: probe.status, type: probe.type.split(';')[0], verified: verdict.ok, detail: verdict.detail,
      association: association.reason, plan: plan.status, route, expected: item.expect, pass: route === item.expect });
  } catch (error) {
    results.push({ name: item.name, error: String(error.message || error).slice(0, 120), pass: false, unreachable: true });
  }
}
console.log(JSON.stringify(results, null, 1));
const reachable = results.filter(r => !r.unreachable);
console.log(`live: ${reachable.filter(r => r.pass).length}/${reachable.length} reachable cases planned as expected; ${results.length - reachable.length} unreachable`);
