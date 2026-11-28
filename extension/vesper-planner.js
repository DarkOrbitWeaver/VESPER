// VESPER acquisition planning. Explicit, bounded state transitions over a fixed route catalog.
// The planner decides which safe step resolves the most important remaining uncertainty; the
// native engine and existing bridges still own authorization, bytes, verification and recovery.
'use strict';
globalThis.VesperPlanner = (() => {
  const MAX_ATTEMPTS = 6;
  const MAX_SOURCES = 16;

  // cost is relative network/user cost; evidence routes only gather facts, acquisition routes move bytes.
  const ROUTES = Object.freeze({
    'resolve-selection': { role: 'user', cost: 0 },
    'verify-observed': { role: 'evidence', cost: 1 },
    'adapter-lookup': { role: 'evidence', cost: 2 },
    'generic-discovery': { role: 'evidence', cost: 3 },
    'refresh-source': { role: 'evidence', cost: 2 },
    'verify-replacement': { role: 'evidence', cost: 2 },
    'native-http': { role: 'acquire', cost: 1 },
    'native-segmented': { role: 'acquire', cost: 1 },
    'native-hls': { role: 'acquire', cost: 2 },
    'native-dash': { role: 'acquire', cost: 2 },
    'native-adaptive-merge': { role: 'acquire', cost: 3 },
    'browser-body': { role: 'acquire', cost: 2 },
    'chrome-keep': { role: 'fallback', cost: 0 },
    'chrome-handoff': { role: 'fallback', cost: 4 }
  });
  const TERMINAL = new Set(['succeeded', 'unsupported', 'refused']);

  function sourceRoute(source) {
    if (source.kind === 'hls') return 'native-hls';
    if (source.kind === 'dash') return 'native-dash';
    if (source.kind === 'adaptive') return 'native-adaptive-merge';
    if (source.kind === 'direct') return source.acceptsRanges === true && Number.isSafeInteger(source.length) ? 'native-segmented' : 'native-http';
    return null;
  }

  // Ownership for a user-initiated browser download. An explicit VESPER action or an actual Chrome
  // download is the authority; a model score never is. Chrome keeps bytes it already owns.
  function ownershipRoute({ approved, nativeReplayVerified, originalBytesProven, browserReadAllowed,
    isOneUse, isPost, isBlob, isDrm, chromeExists, chromeHasBytes } = {}) {
    if (isDrm) return { tier: 'unsupported', route: null, reason: 'Protected media' };
    if (!approved) return { tier: 'chrome', route: 'chrome-keep', reason: 'No download authority' };
    if (chromeExists || chromeHasBytes) return { tier: 'chrome', route: 'chrome-keep', reason: 'Chrome already owns the download' };
    if (originalBytesProven) return { tier: 'original-stream', route: 'browser-body', reason: 'Original stream ownership proven' };
    if (!isOneUse && !isPost && !isBlob && nativeReplayVerified)
      return { tier: 'native', route: 'native-http', reason: 'Independent GET verified replayable' };
    if (!isOneUse && !isPost && browserReadAllowed)
      return { tier: 'browser-prototype', route: 'browser-body', reason: 'Requires explicit browser streaming integration' };
    return { tier: 'chrome', route: 'chrome-keep', reason: 'Cannot prove safe handoff' };
  }

  function attemptsOf(situation) {
    return Array.isArray(situation.attempts) ? situation.attempts.slice(0, MAX_ATTEMPTS * 2) : [];
  }
  const tried = (attempts, route, key = '') => attempts.some(item => item.route === route && (item.key || '') === key);
  const failedRoute = (attempts, route) => attempts.some(item => item.route === route && item.outcome !== 'succeeded');

  /**
   * situation = {
   *   intent: { explicit, scoped, wants: 'file'|'media' },
   *   selection: { decisive, ambiguous },
   *   sources: [{ key, kind, provenance: 'observed'|'associated'|'declared'|'adapter', verified, expiresAt, drm,
   *               oneUse, post, blob, acceptsRanges, length }],
   *   capabilities: { adapter, generic, browserBody, chromeDownload: { exists, receivingBytes } },
   *   experience: { adapterPromising },
   *   partial: { exists, identityVerdict: 'exact'|'verify'|'incompatible' },
   *   attempts: [{ route, key, outcome: 'succeeded'|'failed'|'refused'|'expired'|'unsupported' }]
   * }
   * Returns the next step plus permitted alternatives and the evidence that is still missing.
   */
  function plan(situation = {}, now = Date.now()) {
    const intent = situation.intent || {};
    const caps = situation.capabilities || {};
    const attempts = attemptsOf(situation);
    const sources = (Array.isArray(situation.sources) ? situation.sources : []).slice(0, MAX_SOURCES);
    const result = (status, route, reason, extra = {}) => ({ status, next: route ? { route, ...ROUTES[route], ...(extra.source ? { source: extra.source } : {}) } : null,
      reason, missing: extra.missing || [], alternatives: extra.alternatives || [], attempts: attempts.length });

    if (sources.some(source => source.drm === true)) return result('unsupported', null, 'protected-content');
    const terminal = attempts.find(item => TERMINAL.has(item.outcome) && ROUTES[item.route]?.role !== 'evidence');
    if (terminal?.outcome === 'succeeded') return result('done', null, 'acquired');
    if (terminal) return result(terminal.outcome === 'unsupported' ? 'unsupported' : 'stopped', null, 'server-' + terminal.outcome);
    if (attempts.length >= MAX_ATTEMPTS) return result('stopped', null, 'attempt-budget');

    const chrome = caps.chromeDownload || {};
    // Ownership starts at onCreated, even at zero bytes. No proof can authorize a second copy.
    if (chrome.exists === true || chrome.receivingBytes === true) return result('fallback', 'chrome-keep', 'chrome-owns-bytes');
    if (situation.selection?.ambiguous === true && intent.explicit === true)
      return result('needs-user', 'resolve-selection', 'ambiguous-target', { missing: ['user-selection'] });

    const usable = sources.filter(source => source.verified === true && typeof source.key === 'string' && source.key.length > 0 && !(Number.isFinite(source.expiresAt) && source.expiresAt <= now));
    const expired = sources.filter(source => source.verified === true && Number.isFinite(source.expiresAt) && source.expiresAt <= now);
    const partial = situation.partial || {};

    // Saved bytes continue only from a source whose identity is proven exact or engine-verifiable.
    if (partial.exists === true && partial.identityVerdict === 'incompatible')
      return result('needs-user', null, 'replacement-identity-differs', { missing: ['fresh-download-consent'] });

    if (intent.explicit !== true || intent.scoped !== true) {
      // Without explicit scoped intent the planner may prepare choices but never acquires.
      const evidence = nextEvidence(sources, caps, situation.experience, attempts, situation.selection);
      return result('observe', evidence?.route || null, 'no-download-authority', { missing: ['explicit-intent'] });
    }

    // The verifier must bind the proof to the exact selected replacement, not just the job.
    if (partial.exists === true && (partial.identityVerdict !== 'exact' || typeof partial.verifiedSourceKey !== 'string'
        || !usable.some(source => source.key === partial.verifiedSourceKey)))
      return !tried(attempts, 'verify-replacement')
        ? result('needs-evidence', 'verify-replacement', 'replacement-identity-unproven', { missing: ['engine-identity-proof'] })
        : result('needs-user', null, 'replacement-identity-unproven', { missing: ['engine-identity-proof'] });

    for (const source of usable) {
      if (partial.exists === true && source.key !== partial.verifiedSourceKey) continue;
      const oneShot = source.oneUse === true || source.post === true || source.blob === true;
      const route = oneShot ? (caps.browserBody && source.originalBytesProven === true ? 'browser-body' : null) : sourceRoute(source);
      if (route && !tried(attempts, route, source.key))
        return result('act', route, 'verified-source', { source: source.key,
          alternatives: partial.exists === true ? [] : usable.filter(other => other !== source && !other.oneUse && !other.post && !other.blob).map(other => sourceRoute(other)).filter(Boolean) });
    }
    if (expired.length && !failedRoute(attempts, 'refresh-source'))
      return result('needs-evidence', 'refresh-source', 'source-expired', { missing: ['fresh-signed-source'] });

    const evidence = nextEvidence(sources, caps, situation.experience, attempts, situation.selection);
    if (evidence) return result('needs-evidence', evidence.route, evidence.reason, { missing: ['verified-source'] });
    if (partial.exists === true) return result('needs-user', null, 'partial-recovery-exhausted', { missing: ['fresh-download-consent'] });
    if (chrome.exists === true) return result('fallback', 'chrome-keep', 'native-routes-exhausted');
    if (caps.chromeHandoff === true && !tried(attempts, 'chrome-handoff'))
      return result('fallback', 'chrome-handoff', 'native-routes-exhausted');
    return result('stopped', null, 'no-safe-route');
  }

  // Cheapest untried evidence route. Observed traffic is checked first unless experience says the
  // site's own lookup is the more reliable identity, in which case the adapter goes first.
  function nextEvidence(sources, caps, experience = {}, attempts = [], selection = {}) {
    const observed = sources.some(source => source.verified !== true && ['observed', 'associated'].includes(source.provenance));
    const order = [];
    const adapterFirst = caps.adapter && experience?.adapterPromising !== false;
    if (adapterFirst) order.push(['adapter-lookup', 'specialist-identity']);
    if (observed && selection?.ambiguous !== true)
      order.push(['verify-observed', adapterFirst ? 'observed-fallback' : caps.adapter ? 'adapter-refused-recently' : 'observed-traffic']);
    if (caps.adapter && !adapterFirst) order.push(['adapter-lookup', 'specialist-fallback']);
    if (caps.generic) order.push(['generic-discovery', 'generic-structure']);
    const next = order.find(([route]) => !tried(attempts, route));
    return next ? { route: next[0], reason: next[1] } : null;
  }

  // A bounded run: records outcomes, re-plans, and refuses to exceed the attempt budget.
  function createRun(situation, { now = Date.now } = {}) {
    const state = { ...situation, attempts: attemptsOf(situation) };
    const history = [];
    return Object.freeze({
      next() {
        const step = plan(state, now());
        history.push({ status: step.status, route: step.next?.route || null, reason: step.reason });
        if (history.length > MAX_ATTEMPTS * 3) history.shift();
        return step;
      },
      report({ route, key = '', outcome, sources }) {
        if (!ROUTES[route] || !['succeeded', 'failed', 'refused', 'expired', 'unsupported'].includes(outcome)) throw new Error('Invalid plan outcome');
        if (state.attempts.length >= MAX_ATTEMPTS) throw new Error('Attempt budget exhausted');
        state.attempts = [...state.attempts, { route, key, outcome }].slice(-MAX_ATTEMPTS * 2);
        if (Array.isArray(sources)) state.sources = sources.slice(0, MAX_SOURCES);
      },
      revise(changes) { Object.assign(state, changes, { attempts: state.attempts }); },
      history: () => history.slice()
    });
  }

  // Early format lookup admission. Respects per-tab and per-site cooldowns, a global concurrency
  // limit, server Retry-After deadlines and escalating backoff after repeated refusals.
  function createPrefetchGovernor({ now = Date.now, tabCooldownMs = 1500, siteCooldownMs = 2000, maxActive = 2,
    maxSites = 64, refusalBackoffMs = 60_000, maxBackoffMs = 15 * 60_000 } = {}) {
    const tabAt = new Map(), siteUntil = new Map(), refusals = new Map();
    const active = new Map();
    let serial = 0;
    function admit({ tab, site }) {
      const at = now();
      const key = String(site || '').toLowerCase();
      const last = tabAt.get(tab) ?? -Infinity;
      const retryAfterMs = Math.max(0, last + tabCooldownMs - at, (siteUntil.get(key) ?? 0) - at);
      if (active.size >= maxActive || [...active.values()].includes(key) || retryAfterMs > 0) return { allow: false, retryAfterMs: retryAfterMs || 5000 };
      for (const [host, until] of siteUntil) if (until <= at && ![...active.values()].includes(host)) siteUntil.delete(host);
      if (siteUntil.size >= maxSites) return { allow: false, retryAfterMs: null };
      tabAt.set(tab, at);
      while (tabAt.size > 256) tabAt.delete(tabAt.keys().next().value);
      siteUntil.set(key, at + siteCooldownMs);
      const ticket = ++serial;
      active.set(ticket, key);
      return { allow: true, ticket };
    }
    function settle({ ticket, code, retryAt } = {}) {
      const key = active.get(ticket);
      if (key === undefined) return;
      active.delete(ticket);
      if (code === 'rate-limited' || code === 'site-refused') {
        const count = Math.min((refusals.get(key) || 0) + 1, 8);
        refusals.delete(key); refusals.set(key, count);
        while (refusals.size > maxSites) refusals.delete(refusals.keys().next().value);
        const backoff = Math.min(maxBackoffMs, refusalBackoffMs * 2 ** (count - 1));
        if (siteUntil.has(key))
          siteUntil.set(key, Math.max(siteUntil.get(key), now() + backoff, Number.isFinite(retryAt) ? retryAt : 0));
      } else if (code === 'ok') refusals.delete(key);
    }
    return Object.freeze({ admit, settle, forgetTab: tab => tabAt.delete(tab),
      snapshot: () => ({ active: active.size, sites: siteUntil.size, tabs: tabAt.size, refusals: refusals.size }) });
  }

  return Object.freeze({ ROUTES, MAX_ATTEMPTS, plan, createRun, ownershipRoute, sourceRoute, createPrefetchGovernor });
})();
