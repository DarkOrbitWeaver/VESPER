// Development tool: distills reusable media-delivery concepts from the resolver adapters into a
// normalized catalog (vesper.adapter-knowledge/1). Static source analysis only; nothing is executed,
// fetched or trusted as ground truth. Output feeds corpus generation (weak teacher profiles) and the
// evidence graph's signed-URL expiry vocabulary.
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const root = new URL('../../', import.meta.url);
const extractors = new URL('src/Vesper.MediaResolver/Extractors/', root);
const has = (text, pattern) => pattern.test(text);

export function distill() {
  const adapters = [];
  for (const file of readdirSync(extractors).filter(name => /Adapter\.cs$/.test(name) && name !== 'AdapterBase.cs').sort()) {
    const text = readFileSync(new URL(file, extractors), 'utf8');
    const hosts = [...new Set([...text.matchAll(/"((?:[a-z0-9-]+\.)+(?:com|tv|net|org|be|us|io|ly|it|fm))"/g)].map(m => m[1]))].sort().slice(0, 16);
    adapters.push({
      adapter: file.replace(/Adapter\.cs$/, '').toLowerCase(),
      hosts,
      concepts: {
        programIdentity: has(text, /MediaId|VideoId|\bId\s*=|ExtractId|Match\(/),
        hlsManifests: has(text, /m3u8|Hls|TryAddManifestAsync/),
        dashManifests: has(text, /\.mpd|Dash|dash\+xml/),
        directFiles: has(text, /MediaRepresentation\(|BuildDirect|progressive/i),
        separateAudioVideo: has(text, /AudioOnly|audio_only|VideoOnly|adaptive/i),
        multipleAudioLanguages: has(text, /AudioTrack\(/),
        subtitles: has(text, /SubtitleTrack\(/),
        signedExpiry: has(text, /ExpiryParser|ExpiresAt/),
        authentication: has(text, /Cookie|Authorization|SignedIn|RequiresLogin|LoginRequired/),
        drmDetection: has(text, /DrmProtected|Widevine|PlayReady|FairPlay/i),
        refusalHandling: has(text, /RateLimited|Forbidden|SiteRefused|TooManyRequests|429/),
        liveStreams: has(text, /LiveStream|isLive|IsLive/)
      }
    });
  }
  const expiryText = (() => {
    try { return readFileSync(new URL('src/Vesper.MediaResolver/Utilities/ExpiryParser.cs', root), 'utf8'); } catch { return ''; }
  })();
  const expiryParams = [...new Set([...expiryText.matchAll(/"([A-Za-z][A-Za-z0-9-]{0,20})"/g)].map(m => m[1].toLowerCase()))]
    .filter(name => /exp|deadline|valid|hdn|policy/.test(name)).sort();
  // Concept prevalence becomes corpus archetype weights; no host or adapter name reaches a model feature.
  const prevalence = {};
  for (const { concepts } of adapters)
    for (const [name, value] of Object.entries(concepts)) prevalence[name] = (prevalence[name] || 0) + (value ? 1 : 0);
  return { schema: 'vesper.adapter-knowledge/1', adapters, expiryParams, prevalence, adapterCount: adapters.length };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const output = process.argv[2] || fileURLToPath(new URL('adapter-knowledge.json', import.meta.url));
  const knowledge = distill();
  writeFileSync(output, JSON.stringify(knowledge, null, 1) + '\n');
  console.log(`adapters=${knowledge.adapterCount} expiryParams=${knowledge.expiryParams.join(',')}`);
}
