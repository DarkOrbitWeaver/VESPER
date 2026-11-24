// VESPER's bounded browser intelligence primitives. No network calls, tracking, or background polling.
// Page data is evidence, never authority to execute an unapproved download.
'use strict';
globalThis.VesperIntelligence = (() => {
  const MAX_ITEMS = 50;
  const MAX_MEDIA = 40;
  const MAX_EVIDENCE = 160;
  const MAX_URL = 4096;
  const MEDIA_AGE = 60 * 60_000;
  const HTTP = /^https?:$/;
  const MANIFEST = /(?:\.m3u8|\.mpd)(?:[?#]|$)/i;
  const DIRECT = /\.(?:mp4|m4v|webm|mov|mkv|m4a|mp3|ogg|flac)(?:[?#]|$)/i;
  const FRAGMENT = /\.(?:m4s|cmfv|cmfa|ts|aac|vtt|webvtt|key)(?:[?#]|$)/i;
  const KNOWN_SEGMENT = /(?:[?&](?:segment|chunk|part|frag|range)=)/i;
  const normText = value => String(value ?? '').trim().replace(/\s+/g, ' ').slice(0, 200);
  function http(value, base) {
    if (typeof value !== 'string' || !value.trim() || value.length > MAX_URL || /[\r\n\0]/.test(value)) return null;
    try {
      const result = new URL(value, base);
      if (!HTTP.test(result.protocol) || result.username || result.password) return null;
      result.hash = '';
      return result;
    } catch { return null; }
  }
  function safeScope({ tabId, frameId, documentId } = {}) {
    return Number.isSafeInteger(tabId) && tabId >= 0 && Number.isSafeInteger(frameId) && frameId >= 0
      && typeof documentId === 'string' && documentId.length >= 8 && documentId.length <= 128
      ? `${tabId}:${frameId}:${documentId}` : null;
  }
  function sourceKind(url, mime = '') {
    if (FRAGMENT.test(url) || KNOWN_SEGMENT.test(url)) return 'fragment';
    if (MANIFEST.test(url) || /(?:mpegurl|dash\+xml)/i.test(mime)) return 'manifest';
    if (DIRECT.test(url) || /^(?:audio|video)\//i.test(mime)) return 'direct';
    return 'unknown';
  }
  const commonWords = new Map(Object.entries({
    download: 5, save: 3, export: 4, get: 1, obtain: 2, herunterladen: 5, telecharger: 5, télécharger: 5,
    descargar: 5, baixar: 5, scarica: 5, تحميل: 5, تنزيل: 5,
    play: -6, preview: -5, view: -5, open: -3, read: -5, watch: -5, expand: -2,
    next: -4, more: -2, details: -4, subscribe: -5, login: -6, sign: -4
  }));
  // Tiny on-device semantic ranker (a linear feature scorer, *not* a language model).
  // Its score never authorizes a click. Class names are intentionally not required.
  function proposeControl(facts = {}) {
    let score = 0;
    const reasons = [];
    const words = normText(`${facts.label || ''} ${facts.ariaLabel || ''} ${facts.title || ''}`)
      .toLowerCase().split(/[\s\p{P}\p{S}]+/u).filter(Boolean).slice(0, 50);
    for (const word of words) {
      if (!commonWords.has(word)) continue;
      const value = commonWords.get(word);
      score += value;
      reasons.push(`${word}:${value > 0 ? '+' : ''}${value}`);
    }
    if (facts.downloadAttribute) { score += 5; reasons.push('download attribute'); }
    if (facts.role === 'button' || facts.tagName === 'BUTTON') { score += 1; reasons.push('button'); }
    if (facts.isNavigation || facts.role === 'tab') { score -= 6; reasons.push('navigation'); }
    if (facts.isInlinePdf) { score -= 8; reasons.push('inline PDF'); }
    if (facts.isBackground) { score -= 12; reasons.push('background'); }
    if (facts.isDisabled) { score -= 12; reasons.push('disabled'); }
    const type = sourceKind(facts.href || '', facts.mime || '');
    if (type === 'direct') { score += 2; reasons.push('file-like path'); }
    if (type === 'fragment') { score -= 10; reasons.push('media segment'); }
    const classification = score >= 5 ? 'download-likely' : score <= -3 ? 'navigation-likely' : 'uncertain';
    return { classification, score, reasons, mayExecute: false };
  }
  function operationDecision({ approval, chromeCreated, model, navigates, inlinePdf, requestKind } = {}) {
    // An explicit VESPER action or *actual* Chrome download is the authority, not a model score.
    if (requestKind === 'drm' || requestKind === 'opaque') return 'unsupported';
    if (approval?.explicit === true && approval?.scoped === true) return 'approved';
    if (chromeCreated === true) return 'chrome-download';
    if (navigates || inlinePdf) return 'navigation';
    return model?.classification === 'download-likely' ? 'observe-only' : 'ignore';
  }
  // A local observation timeline: bounded memory, limited lifetime, epoch invalidation on source/identity changes.
  function createMediaLedger({ now = Date.now, maxPlayers = MAX_MEDIA, maxEvidence = MAX_EVIDENCE } = {}) {
    const players = new Map();
    let evidence = [];
    let serial = 0;
    const cap = (number, fallback, limit) => Number.isSafeInteger(number) && number > 0 ? Math.min(number, limit) : fallback;
    const playerLimit = cap(maxPlayers, MAX_MEDIA, MAX_MEDIA);
    const evidenceLimit = cap(maxEvidence, MAX_EVIDENCE, MAX_EVIDENCE);
    const expire = () => {
      const cutoff = now() - MEDIA_AGE;
      evidence = evidence.filter(entry => entry.at >= cutoff);
      for (const [key, entry] of players)
        if (entry.updatedAt < cutoff) players.delete(key);
    };
    function playback({ key, scope, pageUrl, mediaUrl = '', stableId = '', event = 'update', duration = null } = {}) {
      if (typeof key !== 'string' || !key || key.length > 128 || !http(pageUrl)) return null;
      expire();
      const page = http(pageUrl).href;
      const source = http(mediaUrl)?.href || (typeof mediaUrl === 'string' && /^blob:https?:/i.test(mediaUrl)
        ? mediaUrl.slice(0, MAX_URL) : '');
      const id = normText(stableId);
      const old = players.get(key);
      // A reused MediaSource/blob URL is not proof that the program stayed the same.
      const changed = !old || old.scope !== scope || old.mediaUrl !== source || old.stableId !== id
        || event === 'emptied' || event === 'loadstart';
      const epoch = changed ? ++serial : old.epoch;
      const current = { key, scope, pageUrl: page, mediaUrl: source, stableId: id, epoch,
        epochStartedAt: changed ? now() : old.epochStartedAt,
        reused: changed ? Boolean(old) : old.reused,
        duration: Number.isFinite(duration) && duration >= 0 ? duration : changed ? null : old?.duration ?? null,
        updatedAt: now(), wasEnded: event === 'ended' };
      players.delete(key); players.set(key, current);
      while (players.size > playerLimit) players.delete(players.keys().next().value);
      return { ...current };
    }
    function record({ scope, url, mime = '', type = 'request', at = now(), playerKey = null } = {}) {
      const parsed = http(url);
      if (!parsed || typeof scope !== 'string' || !scope || !Number.isFinite(at)
          || at < now() - MEDIA_AGE || at > now() + 2000) return false;
      const kind = sourceKind(parsed.href, mime);
      if (kind === 'fragment' || (kind === 'unknown' && type !== 'resource')) return false;
      // A historical resource entry must not acquire the epoch of a newly loaded player.
      const owner = typeof playerKey === 'string' ? players.get(playerKey) : null;
      // Buffered Resource Timing may predate discovery of an already-playing
      // element. Only an exact URL in its first observed epoch can bind retroactively;
      // this remains a ranking hint, never a verified download identity.
      const initialExact = owner && !owner.reused && owner.mediaUrl === parsed.href;
      const bound = owner?.scope === scope && (at >= owner.epochStartedAt || initialExact);
      const key = typeof playerKey === 'string' ? playerKey : null;
      const epoch = bound ? owner.epoch : null;
      // Repeated requests must not evict the entire manifest history.
      const previous = evidence.findIndex(entry => entry.scope === scope && entry.url === parsed.href
        && entry.playerKey === key && entry.epoch === epoch);
      if (previous >= 0) {
        if (evidence[previous].at > at) return true;
        evidence.splice(previous, 1);
      }
      evidence.push({ scope, url: parsed.href, mime: normText(mime), at, kind,
        type, playerKey: key, epoch });
      while (evidence.length > evidenceLimit) {
        // Preserve known manifests and complete sources instead of losing them to unrelated fetch traffic.
        const unknown = evidence.findIndex(item => item.kind === 'unknown');
        evidence.splice(unknown >= 0 ? unknown : 0, 1);
      }
      return true;
    }
    function matches(playerKey) {
      expire();
      const player = players.get(playerKey);
      if (!player) return [];
      const exactUrl = entry => !!player.mediaUrl && entry.url === player.mediaUrl;
      const recent = entry => entry.at >= player.epochStartedAt - (player.reused ? 0 : 15_000);
      return evidence.filter(entry => entry.scope === player.scope
          && (entry.playerKey
            ? entry.playerKey === playerKey && entry.epoch === player.epoch
            : recent(entry) || !player.reused && exactUrl(entry)))
        .map(entry => {
          let score = entry.kind === 'manifest' ? 3 : 1;
          const exact = exactUrl(entry);
          const bound = entry.playerKey === playerKey;
          if (bound) score += 12;
          if (exact) score += 20;
          if (recent(entry)) score += 2;
          return { ...entry, score, exact, bound,
            verified: false, needsIndependentCheck: true };
        })
        .sort((a, b) => b.score - a.score || b.at - a.at).slice(0, 10);
    }
    function forget(scope) {
      for (const [key, entry] of players) if (entry.scope === scope) players.delete(key);
      evidence = evidence.filter(entry => entry.scope !== scope);
    }
    return { playback, record, matches, forget, get: key => { const value = players.get(key); return value && { ...value }; },
      snapshot: () => ({ players: players.size, evidence: evidence.length }) };
  }
  function makeBatchPlan({ items, action, origin, id } = {}) {
    const scope = http(origin);
    if (!scope || !Array.isArray(items) || items.length < 1 || items.length > MAX_ITEMS
        || !action || typeof action.label !== 'string' || !normText(action.label) || !/^[\w-]{12,80}$/.test(id || ''))
      throw new Error('Invalid approved batch');
    const keys = new Set();
    const steps = items.map((item, index) => {
      const key = String(item?.key || '');
      if (!key || key.length > 160 || keys.has(key) || !normText(item?.label)) throw new Error('Ambiguous selected item');
      keys.add(key);
      return { index, key, label: normText(item.label), state: 'approved', intentId: `${id}:${index}` };
    });
    return { id, origin: scope.origin, action: { label: normText(action.label), key: String(action.key || '') }, steps };
  }
  const FINAL = new Set(['submitted', 'verified-native', 'verified-chrome', 'needs-input', 'indeterminate', 'failed-confirmed', 'cancelled']);
  function createBatchExecutor({ plan, adapter, journal, now = Date.now } = {}) {
    if (!plan?.steps?.length || !adapter || !journal?.save) throw new Error('Missing batch dependencies');
    let stopped = false, active = false;
    const started = now();
    const states = plan.steps.map(step => ({ ...step, at: started }));
    const persist = async (step, state, detail = '') => {
      step.state = state; step.at = now(); step.detail = normText(detail);
      await journal.save({ id: plan.id, origin: plan.origin, at: now(),
        steps: states.map(s => ({ index: s.index, key: s.key, state: s.state, at: s.at, detail: s.detail || '' })) });
    };
    async function run() {
      if (active) throw new Error('Batch already running');
      active = true;
      try {
        const prior = await journal.load?.(plan.id);
        if (prior?.steps?.length === states.length) {
          for (const previous of prior.steps) {
            const step = states[previous.index];
            if (!step || step.key !== previous.key) throw new Error('Saved batch differs from approved plan');
            if (previous.state === 'cancelled') {
              step.state = 'cancelled';
              step.detail = previous.detail || '';
              stopped = true;
            } else if (previous.state !== 'approved') {
              step.state = FINAL.has(previous.state) ? previous.state : 'indeterminate';
              step.detail = previous.detail || 'Previous action requires reconciliation';
              // A previous unresolved effect blocks the rest of the approved sequence.
              if (!['submitted', 'verified-native', 'verified-chrome'].includes(step.state)) stopped = true;
            }
          }
        }
        for (const step of states) {
          if (stopped) { if (step.state === 'approved') await persist(step, 'cancelled'); continue; }
          if (step.state !== 'approved') continue;
          try {
            await persist(step, 'activating');
            const selected = await adapter.activate(step);
            if (!selected?.exact) { await persist(step, 'needs-input', 'The selected item was not uniquely found'); stopped = true; break; }
            await persist(step, 'waiting');
            const ready = await adapter.waitReady(step, selected);
            if (stopped) { await adapter.disarm?.(step); await persist(step, 'cancelled'); continue; }
            if (!ready?.verified || ready.itemKey !== step.key || !ready.actionKey
                || ready.actionKey !== plan.action.key) {
              await persist(step, 'needs-input', 'The shared Download button did not prove it belongs to this item');
              stopped = true; break;
            }
            await persist(step, 'ready');
            if (stopped) { await adapter.disarm?.(step); await persist(step, 'cancelled'); continue; }
            // Write-before-click. A crash after this point MUST NOT replay the click automatically.
            await persist(step, 'action-recorded');
            if (stopped) { await adapter.disarm?.(step); await persist(step, 'cancelled'); continue; }
            await adapter.trigger(step, ready);
            await persist(step, 'reconciling');
            const result = await adapter.reconcile(step, ready);
            const outcome = result?.ownership === 'native' && result.verified === true ? 'verified-native'
              : result?.ownership === 'chrome' && result.verified === true ? 'verified-chrome'
              : result?.effect === 'started-confirmed' && result?.verified === true ? 'submitted'
              : result?.effect === 'none-confirmed' ? 'failed-confirmed' : 'indeterminate';
            await persist(step, outcome, result?.detail || '');
            if (!['submitted', 'verified-native', 'verified-chrome'].includes(outcome)) { stopped = true; break; }
          } catch (error) {
            const afterSideEffect = ['action-recorded', 'reconciling'].includes(step.state);
            await persist(step, afterSideEffect ? 'indeterminate' : 'needs-input', error?.message || 'Workflow interrupted');
            stopped = true; break;
          }
        }
        if (stopped) for (const step of states) if (step.state === 'approved') await persist(step, 'cancelled');
        return states.map(s => ({ ...s }));
      } finally { active = false; }
    }
    return { run, cancel: () => { stopped = true; }, snapshot: () => states.map(s => ({ ...s })) };
  }
  function chooseAcquisition({ approved, nativeReplayVerified, originalBytesProven,
      browserReadAllowed, isOneUse, isPost, isBlob, isDrm, chromeHasBytes } = {}) {
    if (isDrm) return { tier: 'unsupported', reason: 'Protected media' };
    if (!approved) return { tier: 'chrome', reason: 'No download authority' };
    if (chromeHasBytes && !originalBytesProven) return { tier: 'chrome', reason: 'Original Chrome bytes are already owned' };
    if (originalBytesProven) return { tier: 'original-stream', reason: 'Original stream ownership proven' };
    if (!isOneUse && !isPost && !isBlob && nativeReplayVerified)
      return { tier: 'native', reason: 'Independent GET verified replayable' };
    if (!isOneUse && !isPost && browserReadAllowed)
      return { tier: 'browser-prototype', reason: 'Requires explicit browser streaming integration' };
    return { tier: 'chrome', reason: 'Cannot prove safe handoff' };
  }
  return Object.freeze({ http, safeScope, sourceKind, proposeControl, operationDecision,
    createMediaLedger, makeBatchPlan, createBatchExecutor, chooseAcquisition, MAX_ITEMS });
})();
