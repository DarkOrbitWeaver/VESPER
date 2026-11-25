// Program identity and evidence association. A bounded, typed graph of what the browser actually
// observed: documents, frames, players, source generations, requests, manifests, representations,
// tracks, declared configuration and user interaction. It answers association questions with
// explicit provenance and never treats a shared host, filename, resolution, codec, model score
// or temporal proximity alone as proof that two resources are the same program.
'use strict';
globalThis.VesperEvidenceGraph = (() => {
  const SCHEMA = 'vesper.evidence/1';
  const NODE_TYPES = new Set(['document', 'frame', 'player', 'generation', 'request', 'manifest',
    'representation', 'track', 'config', 'control', 'interaction']);
  // Relation strength is ordinal. Only 'strong' relations can make a source decisive on their own.
  const RELATIONS = Object.freeze({
    contains: 'structural', hosts: 'structural', generation: 'structural',
    plays: 'strong', fingerprint: 'strong', 'selected-by-user': 'strong',
    'requested-during': 'medium', 'segment-of': 'medium', 'representation-of': 'medium', 'track-of': 'medium',
    declares: 'weak', 'controls': 'weak', hypothesizes: 'weak'
  });
  const LIMITS = Object.freeze({ nodes: 384, edges: 768, keyLength: 4608, lifetimeMs: 60 * 60_000 });
  // Signed-URL expiry parameters distilled from the resolver adapters and common CDN conventions
  // (see tools/intelligence/adapter-knowledge.json). Values are interpreted, never trusted for identity.
  const EXPIRY_PARAMS = Object.freeze(['expire', 'expires', 'exp', 'e', 'x-amz-expires', 'deadline', 'validto', 'hdnts', 'hdnea', 'policy']);

  function parseHttp(value) {
    if (typeof value !== 'string' || value.length > 4096 || /[\r\n\0]/.test(value)) return null;
    try {
      const url = new URL(value);
      if (!/^https?:$/.test(url.protocol) || url.username || url.password) return null;
      url.hash = '';
      return url;
    } catch { return null; }
  }

  // Milliseconds since epoch from a signed URL, or null. Akamai-style tokens embed exp= inside a value.
  function expiresAt(value, now = Date.now()) {
    const url = parseHttp(value);
    if (!url) return null;
    for (const [rawName, rawValue] of url.searchParams) {
      const name = rawName.toLowerCase();
      if (!EXPIRY_PARAMS.includes(name)) continue;
      const embedded = /(?:^|[~&])exp=(\d{9,11})/.exec(rawValue);
      const digits = embedded ? embedded[1] : /^\d{9,11}$/.test(rawValue) ? rawValue : null;
      if (digits) return Number(digits) * 1000;
      if (name === 'x-amz-expires' && /^\d{1,7}$/.test(rawValue)) {
        const date = url.searchParams.get('X-Amz-Date') || url.searchParams.get('x-amz-date');
        const parsed = date && /^(\d{4})(\d{2})(\d{2})T(\d{2})(\d{2})(\d{2})Z$/.exec(date);
        if (parsed) return Date.UTC(+parsed[1], parsed[2] - 1, +parsed[3], +parsed[4], +parsed[5], +parsed[6]) + Number(rawValue) * 1000;
      }
    }
    return null;
  }

  function directoryOf(url) {
    const parsed = parseHttp(url);
    if (!parsed) return null;
    return parsed.origin + parsed.pathname.slice(0, parsed.pathname.lastIndexOf('/') + 1);
  }

  function create({ now = Date.now, limits = {} } = {}) {
    const maxNodes = Math.min(Number.isSafeInteger(limits.nodes) && limits.nodes > 0 ? limits.nodes : LIMITS.nodes, LIMITS.nodes);
    const maxEdges = Math.min(Number.isSafeInteger(limits.edges) && limits.edges > 0 ? limits.edges : LIMITS.edges, LIMITS.edges);
    const nodes = new Map();
    const edges = new Map();
    let evicted = 0;

    const id = (type, key) => type + ':' + key;
    const scopedKey = (...parts) => JSON.stringify(parts);
    const playerId = (scope, key) => id('player', scopedKey(scope, key));
    const generationId = (scope, key, generation) => id('generation', scopedKey(scope, key, generation));
    const validScope = scope => typeof scope === 'string' && scope.length > 0 && scope.length <= 256;
    const validPlayer = key => typeof key === 'string' && key.length > 0 && key.length <= 128;
    const addSource = (scope, url, kind, attrs = {}) => {
      const type = kind === 'hls' || kind === 'dash' ? 'manifest' : 'request';
      return add(type, scopedKey(scope, url), { ...attrs, scope, url, kind });
    };
    function touch(nodeId) {
      const node = nodes.get(nodeId);
      if (!node) return;
      nodes.delete(nodeId); nodes.set(nodeId, node);
    }
    function removeNode(nodeId) {
      nodes.delete(nodeId);
      for (const [edgeId, edge] of edges) if (edge.from === nodeId || edge.to === nodeId) edges.delete(edgeId);
    }
    function expire() {
      const cutoff = now() - LIMITS.lifetimeMs;
      for (const [nodeId, node] of nodes) if (node.at < cutoff) removeNode(nodeId);
    }

    function add(type, key, attrs = {}) {
      if (!NODE_TYPES.has(type) || typeof key !== 'string' || !key || key.length > LIMITS.keyLength) return null;
      const nodeId = id(type, key);
      const existing = nodes.get(nodeId);
      const clean = {};
      for (const [name, value] of Object.entries(attrs || {}).slice(0, 16)) {
        if (typeof value === 'string') clean[name] = value.slice(0, name === 'url' ? 4096 : 512);
        else if (typeof value === 'number' && Number.isFinite(value)) clean[name] = value;
        else if (typeof value === 'boolean' || value === null) clean[name] = value;
      }
      if (existing) { Object.assign(existing.attrs, clean); existing.at = now(); touch(nodeId); return nodeId; }
      nodes.set(nodeId, { id: nodeId, type, key, attrs: clean, at: now() });
      while (nodes.size > maxNodes) { removeNode(nodes.keys().next().value); evicted++; }
      return nodeId;
    }

    function link(from, to, relation, attrs = {}) {
      const left = nodes.get(from), right = nodes.get(to);
      if (!left || !right || !Object.hasOwn(RELATIONS, relation)) return false;
      if (left.attrs.scope && right.attrs.scope && left.attrs.scope !== right.attrs.scope) return false;
      const edgeId = JSON.stringify([from, relation, to]);
      const at = Number.isFinite(attrs.at) ? attrs.at : now();
      edges.delete(edgeId);
      edges.set(edgeId, { from, to, relation, strength: RELATIONS[relation], at });
      while (edges.size > maxEdges) { edges.delete(edges.keys().next().value); evicted++; }
      return true;
    }

    const out = (nodeId, relation) => [...edges.values()].filter(edge => edge.from === nodeId && (!relation || edge.relation === relation));
    const into = (nodeId, relation) => [...edges.values()].filter(edge => edge.to === nodeId && (!relation || edge.relation === relation));

    // A player's source generation changes whenever its program may have changed. Evidence bound to an
    // older generation remains in the graph for diagnostics but is reported as stale.
    function observePlayer({ scope, playerKey, generation, sourceUrl = '', duration = null, interacted = false, at = now() }) {
      if (!validScope(scope) || !validPlayer(playerKey) || !Number.isSafeInteger(generation) || generation < 1 || !Number.isFinite(at)) return null;
      expire();
      const previous = nodes.get(playerId(scope, playerKey));
      if (previous && generation < previous.attrs.current) return null;
      const priorGen = nodes.get(generationId(scope, playerKey, generation));
      const doc = add('document', scope, { scope });
      const player = add('player', scopedKey(scope, playerKey), { scope, current: generation });
      const gen = add('generation', scopedKey(scope, playerKey, generation), { scope, generation, startedAt: priorGen?.attrs.startedAt ?? at,
        duration: Number.isFinite(duration) || duration === Infinity ? (duration === Infinity ? -1 : duration) : null });
      if (![doc, player, gen].every(key => nodes.has(key))) return null;
      link(doc, player, 'contains', { at });
      link(player, gen, 'generation', { at });
      nodes.get(player).attrs.current = generation;
      const source = parseHttp(sourceUrl);
      if (source) {
        const prior = out(gen, 'plays').map(edge => nodes.get(edge.to)?.attrs.url);
        if (prior.some(url => url !== source.href)) nodes.get(gen).attrs.conflictingSource = true;
        link(gen, addSource(scope, source.href, /\.m3u8$/i.test(source.pathname) ? 'hls' : /\.mpd$/i.test(source.pathname) ? 'dash' : 'direct'), 'plays', { at });
      }
      if (interacted) link(gen, add('interaction', gen + ':user', { scope, at }), 'selected-by-user', { at });
      return gen;
    }

    function observeRequest({ scope, url, kind = 'unknown', playerKey = null, generation = null, at = now(),
      etag = null, length = null, rangeTotal = null }) {
      const parsed = parseHttp(url);
      if (!parsed || !validScope(scope) || !Number.isFinite(at)) return null;
      const nodeId = addSource(scope, parsed.href, kind, { etag: typeof etag === 'string' ? etag : null,
        length: Number.isSafeInteger(length) ? length : null, rangeTotal: Number.isSafeInteger(rangeTotal) ? rangeTotal : null,
        expiresAt: expiresAt(parsed.href) });
      const player = validPlayer(playerKey) ? nodes.get(playerId(scope, playerKey)) : null;
      const gen = player?.attrs.current === generation ? nodes.get(generationId(scope, playerKey, generation)) : null;
      if (gen && at >= gen.attrs.startedAt) link(gen.id, nodeId, 'requested-during', { at });
      // Shared directories are not manifest membership. Only independently checked membership
      // can produce a segment-of relation through the explicit graph link API.
      return nodeId;
    }

    function observeDeclared({ scope, url, playerKey = null, generation = null }) {
      const parsed = parseHttp(url);
      if (!parsed || !validScope(scope)) return null;
      const config = add('config', scopedKey(scope, 'declared'), { scope });
      const kind = /\.m3u8$/i.test(parsed.pathname) ? 'hls' : /\.mpd$/i.test(parsed.pathname) ? 'dash' : 'unknown';
      const nodeId = addSource(scope, parsed.href, kind);
      link(config, nodeId, playerKey ? 'declares' : 'hypothesizes');
      const player = validPlayer(playerKey) ? nodes.get(playerId(scope, playerKey)) : null;
      const gen = player?.attrs.current === generation ? generationId(scope, playerKey, generation) : null;
      if (gen && nodes.has(gen)) link(gen, nodeId, 'declares');
      return nodeId;
    }

    function observeFingerprint({ scope, playerKey, generation, url }) {
      const player = validScope(scope) && validPlayer(playerKey) ? nodes.get(playerId(scope, playerKey)) : null;
      const gen = generationId(scope, playerKey, generation);
      const parsed = parseHttp(url);
      if (player?.attrs.current !== generation || !nodes.has(gen) || !parsed) return false;
      const kind = /\.m3u8$/i.test(parsed.pathname) ? 'hls' : /\.mpd$/i.test(parsed.pathname) ? 'dash' : 'unknown';
      const existing = [...nodes.values()].find(node => node.attrs.scope === scope && node.attrs.url === parsed.href);
      return link(gen, existing?.id || addSource(scope, parsed.href, kind), 'fingerprint');
    }

    // Candidate sources for one player generation, each with source-association features and provenance.
    function associate({ scope, playerKey, adSources = [] }) {
      expire();
      const player = nodes.get(playerId(scope, playerKey));
      if (!player) return { generation: null, candidates: [] };
      const current = player.attrs.current;
      const gens = out(player.id, 'generation').map(edge => nodes.get(edge.to)).filter(Boolean);
      const currentGen = gens.find(node => node.attrs.generation === current);
      const byUrl = new Map();
      const ads = new Set(adSources);
      for (const gen of gens) {
        const isCurrent = gen === currentGen;
        for (const edge of out(gen.id)) {
          const target = nodes.get(edge.to);
          if (!target || !['request', 'manifest'].includes(target.type)) continue;
          if (target.attrs.scope !== scope || !target.attrs.url) continue;
          const url = target.attrs.url;
          const entry = byUrl.get(url) || { url, kind: target.attrs.kind || target.type,
            current: new Set(), old: new Set(), expiresAt: target.attrs.expiresAt ?? expiresAt(url), manifestId: target.type === 'manifest' ? target.id : null };
          (isCurrent ? entry.current : entry.old).add(edge.relation);
          byUrl.set(url, entry);
        }
      }
      const candidates = [...byUrl.values()].map((entry, index) => {
        const currentOnly = entry.current.size > 0;
        const relations = currentOnly ? entry.current : entry.old;
        const segmentsUnder = entry.manifestId ? into(entry.manifestId, 'segment-of').some(edge =>
          edge.at >= (currentGen?.attrs.startedAt ?? Infinity)) : null;
        const strong = !currentGen?.attrs.conflictingSource && (relations.has('plays') || relations.has('fingerprint'));
        return {
          id: 'source-' + index, url: entry.url, kind: entry.kind, expiresAt: entry.expiresAt,
          provenance: strong ? 'observed' : relations.has('requested-during') ? 'associated' : 'declared',
          exactPlayerUrl: strong && relations.has('plays'),
          playerBound: strong || relations.has('requested-during'),
          currentGeneration: currentOnly,
          manifestParent: segmentsUnder,
          resourceWitness: strong && relations.has('fingerprint'),
          wholeFile: entry.kind === 'fragment' ? false : null,
          declared: relations.has('declares'),
          durationAgreement: null,
          adContext: ads.has(entry.url),
          hypothesized: !strong && !relations.has('requested-during'),
          stale: !currentOnly,
          fragmentOnly: entry.kind === 'fragment'
        };
      });
      return { generation: current, candidates };
    }

    function forget(scopePrefix) {
      for (const [nodeId, node] of nodes)
        if (node.attrs.scope === scopePrefix) removeNode(nodeId);
    }

    return Object.freeze({ add, link, observePlayer, observeRequest, observeDeclared, observeFingerprint, associate, forget,
      snapshot: () => ({ schema: SCHEMA, nodes: nodes.size, edges: edges.size, evicted }) });
  }

  // Whether saved bytes may continue from a replacement source. Requires matching strong validators;
  // a model, URL similarity or shared metadata never qualifies. 'verify' means the engine must
  // independently confirm identity (its existing refresh checks) before appending anything.
  function interchangeable(saved, replacement) {
    const reasons = [];
    if (!saved || !replacement) return { verdict: 'incompatible', reasons: ['missing-identity'] };
    if (saved.programId && replacement.programId && saved.programId !== replacement.programId) reasons.push('program-differs');
    if (saved.formatId && replacement.formatId && saved.formatId !== replacement.formatId) reasons.push('format-differs');
    if (Number.isSafeInteger(saved.length) && Number.isSafeInteger(replacement.length) && saved.length !== replacement.length) reasons.push('length-differs');
    const strongTag = tag => typeof tag === 'string' && /^"[\x21\x23-\x7e\x80-\xff]*"$/.test(tag);
    if (strongTag(saved.etag) && strongTag(replacement.etag) && saved.etag !== replacement.etag) reasons.push('validator-differs');
    if (reasons.length) return { verdict: 'incompatible', reasons };
    const savedUrl = parseHttp(saved.url), replacementUrl = parseHttp(replacement.url);
    const exact = !!savedUrl && savedUrl.href === replacementUrl?.href
      && strongTag(saved.etag) && saved.etag === replacement.etag
      && Number.isSafeInteger(saved.length) && saved.length > 0 && saved.length === replacement.length;
    return exact ? { verdict: 'exact', reasons: ['same-resource-strong-validator-and-length'] }
      : { verdict: 'verify', reasons: ['identity-not-proven'] };
  }

  return Object.freeze({ SCHEMA, LIMITS, RELATIONS, EXPIRY_PARAMS, create, expiresAt, interchangeable, directoryOf });
})();
