'use strict';

globalThis.VesperMediaEvidence = (() => {
  const MAX_DURATION_SECONDS = 31_536_000;
  const MAX_HEIGHT = 16_384;

  function positiveNumber(value, max) {
    return Number.isFinite(value) && value > 0 && value <= max ? value : undefined;
  }

  function mediaFacts(reply) {
    const data = reply?.data || {};
    const best = data.choices?.[0] || {};
    const detail = [best.detail, best.label].filter(value => typeof value === 'string').join(' ');
    const height = [data.height, data.videoHeight, best.height, best.videoHeight]
      .map(value => Number.isInteger(value) ? positiveNumber(value, MAX_HEIGHT) : undefined).find(value => value !== undefined)
      || detailHeight(detail);
    const hasAudio = trackFlag(data, best, 'audio', detailAudio(detail), /\baudio not confirmed\b/i.test(detail));
    const hasVideo = trackFlag(data, best, 'video', detailVideo(detail));

    return {
      durationSeconds: positiveNumber(data.duration, MAX_DURATION_SECONDS),
      sizeBytes: !best.sizeEstimated && Number.isSafeInteger(best.sizeBytes) && best.sizeBytes >= 0
        ? best.sizeBytes : undefined,
      height,
      live: typeof data.isLive === 'boolean' ? data.isLive : undefined,
      hasAudio,
      hasVideo
    };
  }

  function detailHeight(detail) {
    const match = /(?:^|\D)(\d{2,5})p(?:\d{2,3})?\b/i.exec(detail);
    if (!match) return undefined;
    const height = Number(match[1]);
    return height >= 16 && height <= MAX_HEIGHT ? height : undefined;
  }

  function trackFlag(data, choice, kind, hint, unverified = false) {
    const property = kind === 'audio' ? 'hasAudio' : 'hasVideo';
    for (const value of [choice[property], data[property]]) {
      if (typeof value === 'boolean') return value;
    }

    const listName = kind === 'audio' ? 'audioTracks' : 'videoTracks';
    for (const value of [choice[listName], data[listName]]) {
      if (Array.isArray(value) && value.length > 0) return true;
    }

    const tracks = [choice.tracks, data.tracks].filter(Array.isArray).flat();
    if (tracks.some(track => track?.kind === kind)) return true;
    return unverified || hint !== undefined ? hint : undefined;
  }

  function detailAudio(detail) {
    if (/\baudio only\b/i.test(detail)) return true;
    if (/\bno audio\b/i.test(detail)) return false;
    if (/\bwith audio\b|\+ audio\b/i.test(detail)) return true;
    return undefined;
  }

  function detailVideo(detail) {
    if (/\baudio only\b|\bno video\b/i.test(detail)) return false;
    if (/\bvideo only\b|\bwith audio\b|\+ audio\b/i.test(detail)) return true;
    return undefined;
  }

  function playerFacts(value) {
    if (!value || typeof value !== 'object') return {};
    const facts = {};
    const durationSeconds = positiveNumber(value.durationSeconds, MAX_DURATION_SECONDS);
    const height = positiveNumber(value.height, MAX_HEIGHT);
    if (durationSeconds !== undefined) facts.durationSeconds = durationSeconds;
    if (Number.isInteger(height)) facts.height = height;
    if (typeof value.live === 'boolean') facts.live = value.live;
    if (['video', 'audio'].includes(value.kind)) facts.kind = value.kind;
    return facts;
  }

  function score(player, verified) {
    let result = 0;
    if (player.live === true && verified.live === true) result += 5;
    else if (player.live === true && verified.live === false) result -= 1;

    if (player.durationSeconds && verified.durationSeconds) {
      const tolerance = Math.max(1, player.durationSeconds * 0.03);
      result += Math.abs(player.durationSeconds - verified.durationSeconds) <= tolerance ? 5 : -2;
    }

    if (player.height && verified.height) {
      const difference = Math.abs(player.height - verified.height);
      if (difference <= Math.max(16, player.height * 0.03)) result += 3;
      else result -= 1;
    }

    if (player.height && verified.hasVideo === false) result -= 3;
    else if (player.height && verified.hasVideo === true) result += 1;
    return result;
  }

  function trackLabel(facts) {
    if (facts.hasVideo === true && facts.hasAudio === true) return 'audio + video';
    if (facts.hasVideo === true && facts.hasAudio === false) return 'video only';
    if (facts.hasVideo === false && facts.hasAudio === true) return 'audio only';
    return '';
  }

  function matchesPlayback(player, facts) {
    const video = player.height || player.kind === 'video';
    if (video && facts.hasVideo === false) return false;
    if (player.kind === 'audio' && facts.hasAudio === false) return false;
    // A header-only response can claim a video MIME type without a playable file.
    if (video && player.durationSeconds >= 1 && !facts.durationSeconds
        && Number.isSafeInteger(facts.sizeBytes) && facts.sizeBytes < 1024) return false;
    if (!player.durationSeconds || !facts.durationSeconds) return true;
    const tolerance = Math.max(2, player.durationSeconds * 0.08);
    return Math.abs(player.durationSeconds - facts.durationSeconds) <= tolerance;
  }

  // A graph is constructed for one picker request, never persisted across
  // documents. Edges are evidence, not download authority.
  function associate({ sources, player = {}, currentUrl = '', resources = [], exactResources = [], fragments = [],
    scope = 'picker', playerKey = 'player', generation = 1, worker = false } = {}) {
    const list = Array.isArray(sources) ? sources.slice(0, 32) : [];
    const observed = new Set(Array.isArray(resources) ? resources.slice(0, 12) : []);
    const bound = new Set(Array.isArray(exactResources) ? exactResources.slice(0, 32) : []);
    const paths = new Set(Array.isArray(fragments) ? fragments.slice(0, 16) : []);
    const parse = value => {
      try {
        const url = new URL(value);
        if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password) return null;
        url.hash = '';
        return url;
      } catch { return null; }
    };
    const exact = parse(currentUrl)?.href;
    const mediaPath = value => {
      const url = parse(value);
      return url ? url.origin + url.pathname : null;
    };
    const fragmentPaths = [...paths].map(mediaPath).filter(Boolean);
    const nodes = list.map((entry, index) => {
      const url = parse(entry.source?.url);
      const facts = entry.reply?.ok ? mediaFacts(entry.reply) : {};
      const durationAgreement = player.durationSeconds && facts.durationSeconds
        ? Math.abs(player.durationSeconds - facts.durationSeconds) <= Math.max(1, player.durationSeconds * 0.03) : null;
      const compatible = matchesPlayback(player, facts)
        && !(entry.source?.sliceEvidence && durationAgreement === false);
      const edges = [];
      const direct = !worker && !!exact && url?.href === exact && !entry.source?.group;
      const witnessed = !worker && (bound.has(url?.href) || entry.source?.group?.some(member => bound.has(member.url)));
      if (direct) edges.push({ kind: 'current-source', weight: 20 });
      if (witnessed) edges.push({ kind: 'player-resource', weight: 24 });
      if (entry.source?.sliceEvidence) edges.push({ kind: 'player-slices', weight: 24 });
      if (observed.has(url?.href)) edges.push({ kind: 'resource', weight: 4 });
      // Shared directories are useful for ranking, but a CDN can host unrelated
      // programs, ads and multiple encodings under the same path.
      if (url) {
        const dir = url.pathname.slice(0, url.pathname.lastIndexOf('/') + 1);
        const distinctive = dir.split('/').some(part => part.length >= 6 && /\d/.test(part));
        const hits = distinctive ? fragmentPaths.filter(path => path.startsWith(url.origin + dir)).length : 0;
        if (hits) edges.push({ kind: 'segment-family', weight: Math.min(6, hits * 2) });
      }
      const memberships = entry.reply?.data?.playbackMatches;
      if (entry.reply?.ok === true && Number.isInteger(memberships) && memberships > 0 && memberships <= 12)
        edges.push({ kind: 'manifest-segment', weight: Math.min(30, 12 + 3 * memberships) });
      const match = score(player, facts);
      if (match) edges.push({ kind: 'media-facts', weight: match });
      const verified = entry.reply?.ok === true && !!entry.reply.data?.choices?.length;
      return { index, entry, edges, verified, direct, witnessed, compatible, durationAgreement,
        rank: edges.reduce((sum, edge) => sum + edge.weight, 0) };
    });
    // The portable model ranks already-verified candidates. It cannot select a
    // source without the independent exact-player check below.
    const learned = globalThis.VesperInference?.rankSources(nodes.filter(node => node.verified && node.compatible).map(node => {
      return { id: 'source-' + node.index, exactPlayerUrl: node.direct,
        playerBound: node.direct || !!node.witnessed,
        resourceWitness: !!node.witnessed,
        manifestParent: node.edges.some(edge => edge.kind === 'manifest-segment'),
        wholeFile: node.entry.source?.wholeRepresentation === true || node.entry.source?.rangeResource === true ? true : null,
        durationAgreement: node.durationAgreement == null ? null : Number(node.durationAgreement),
        currentGeneration: node.direct || node.witnessed ? true : null,
        hypothesized: !node.direct && !node.witnessed && !!node.entry.source?.sliceEvidence,
        fragmentOnly: node.entry.source?.kind === 'fragment' };
    }));
    const preferred = learned?.decisive ? learned.selected : null;
    nodes.sort((a, b) => Number(b.direct && b.verified && b.compatible) - Number(a.direct && a.verified && a.compatible)
      || Number('source-' + b.index === preferred) - Number('source-' + a.index === preferred)
      || b.rank - a.rank || a.index - b.index);
    // Use the typed evidence graph for the actual selection verdict, not
    // merely lookup ordering. Keep the model's ranking separate from identity.
    const graph = globalThis.VesperEvidenceGraph?.create();
    const decision = graph?.assessPicker({ scope, playerKey, generation, sourceUrl: currentUrl, worker,
      candidates: [...nodes].sort((a,b) => a.index - b.index).map(node => ({
        url: node.entry.source?.url, kind: node.entry.source?.kind, direct: node.direct,
        witnessed: !!node.witnessed, verified: node.verified, compatible: node.compatible,
        conflictingValidator: node.entry.source?.conflictingValidator === true
      })) });
    const rows = new Map((decision?.rows || []).map(row => [row.index, row]));
    // Reject contradicted/fragment-only hypotheses for every caller, not only
    // the auto-selection branch. An incomplete graph cannot grant a choice.
    for (const node of nodes) node.compatible = rows.get(node.index)?.compatible === true;
    return { nodes, selected: decision?.verdict === 'exact'
      ? nodes.find(node => node.index === decision.selected)?.entry || null : null };
  }

  return { mediaFacts, playerFacts, score, trackLabel, matchesPlayback, associate };
})();
