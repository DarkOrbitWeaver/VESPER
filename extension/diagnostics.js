'use strict';

const failureLogKey = 'vesperFailureLog';
const diagnosticToken = value => typeof value === 'string' && /^[a-z0-9_.+:-]{1,96}$/i.test(value) ? value : undefined;

function diagnosticRoute(value) {
  if (typeof value !== 'string') return undefined;
  if (/^(blob|data):/i.test(value)) return value.split(':', 1)[0].toLowerCase();
  const url = VesperPlayerSource.http(value);
  if (!url) return undefined;
  const parts = url.pathname.split('/').filter(Boolean).slice(0, 8).map(part => {
    if (/^(archive|attachments?|downloads?|exports?|files?|refs|heads|tags|releases?|zip|tar\.gz|reels?|p|video|videos|status|watch|shorts|embed|stories|foryou)$/i.test(part))
      return part.toLowerCase();
    const suffix = /\.(?:zip|gz|7z|rar|msi|exe|mp4|webm|m3u8|mpd|pdf|csv|xlsx|docx|md|patch|diff)$/i.exec(part);
    return '*' + (suffix?.[0]?.toLowerCase() || '');
  });
  return url.origin + '/' + parts.join('/');
}

const failureFields = new Set([
  'stage', 'attempt', 'job', 'browserId', 'state', 'method', 'status', 'mime', 'bytes', 'total',
  'proof', 'attachment', 'ranges', 'redirected', 'query', 'named', 'signedIn', 'reuse', 'native', 'finalRoute',
  'chromeError', 'chromeStarted', 'routeMatch', 'elapsedMs', 'nativeMs', 'dispatchMs', 'probeMs', 'acceptMs',
  'source', 'ready', 'generation', 'playing', 'height', 'durationMs', 'preview', 'refresh', 'quick',
  'boundPlayer', 'boundPost', 'responses', 'exactMatches', 'candidates', 'postLookup', 'observedLookups',
  'witnesses', 'witnessPaths', 'objectKind', 'contentBuild', 'tracks', 'completeTracks', 'retainedBytes', 'bodyProof', 'attemptedBody', 'subtitleFailures'
]);

function diagnosticFacts(facts) {
  const result = {};
  for (const [key, value] of Object.entries(facts || {})) {
    if (!failureFields.has(key)) continue;
    if (typeof value === 'boolean' || Number.isSafeInteger(value) && value >= 0) result[key] = value;
    else if (key === 'finalRoute') {
      result[key] = diagnosticRoute(value);
    } else if (key === 'mime') {
      if (typeof value === 'string' && /^[a-z0-9!#$&^_.+-]{1,48}\/[a-z0-9!#$&^_.+-]{1,48}$/i.test(value))
        result[key] = value;
    } else if (key === 'observedLookups' && Array.isArray(value)) {
      result[key] = value.slice(0, 8).map(item => diagnosticToken(item)?.slice(0, 48)).filter(Boolean).join(',') || undefined;
    } else if (diagnosticToken(value)) result[key] = value;
  }
  return JSON.stringify(result).length <= 2500 ? result : {};
}

let logWrites = Promise.resolve(), logTimer, logSending = false, logRetryAfter = 0, logAddedWhileSending = false;

// Anonymous, bounded local timings. No URLs, job IDs, credentials or native
// export; recording is asynchronous and does not delay capture acceptance.
const startupLogKey = 'vesperStartupTimings';
let startupWrites = Promise.resolve();
function recordStartupTiming(timing) {
  const build = diagnosticToken(chrome.runtime.getManifest?.()?.version_name || chrome.runtime.getManifest?.()?.version);
  if (!build || !timing) return Promise.resolve();
  const facts = {};
  for (const key of ['dispatchMs', 'probeMs', 'stageMs', 'acceptMs', 'clickToAcceptMs'])
    if (Number.isSafeInteger(timing[key]) && timing[key] >= 0 && timing[key] <= 120000) facts[key] = timing[key];
  if (facts.clickToAcceptMs === undefined) return Promise.resolve();
  startupWrites = startupWrites.then(async () => {
    const saved = await chrome.storage.local.get(startupLogKey);
    const previous = Array.isArray(saved[startupLogKey]) ? saved[startupLogKey] : [];
    await chrome.storage.local.set({ [startupLogKey]: [...previous.slice(-63), { kind: 'startup-timing', build, utc: Date.now(), facts }] });
  }).catch(() => {});
  return startupWrites;
}

function recordFailure(kind, code, page, resource, facts = {}) {
  const manifest = chrome.runtime.getManifest?.();
  const build = diagnosticToken(manifest?.version_name || manifest?.version);
  if (!build) return Promise.resolve(null);
  const entry = {
    id: crypto.randomUUID(), utc: Date.now(), build, kind, code: diagnosticToken(code) || 'unknown',
    page: diagnosticRoute(page), resource: diagnosticRoute(resource), facts: diagnosticFacts(facts)
  };
  const write = logWrites.then(async () => {
    const saved = await chrome.storage.local.get(failureLogKey);
    const previous = Array.isArray(saved[failureLogKey]) ? saved[failureLogKey] : [];
    await chrome.storage.local.set({ [failureLogKey]: [...previous.slice(-191), entry] });
    if (logSending) logAddedWhileSending = true;
    scheduleFailureExport();
    return entry;
  }).catch(() => null);
  logWrites = write;
  return write;
}

function scheduleFailureExport() {
  if (!chrome.runtime.getManifest || logTimer || logSending || Date.now() < logRetryAfter) return;
  logTimer = setTimeout(() => {
    logTimer = null;
    void exportPendingFailures();
  }, 500);
}

async function exportPendingFailures() {
  if (logSending) return;
  logSending = true;
  logAddedWhileSending = false;
  try {
    for (let batch = 0; batch < 12; batch++) {
      await logWrites;
      const saved = await chrome.storage.local.get(failureLogKey);
      const entries = (Array.isArray(saved[failureLogKey]) ? saved[failureLogKey] : [])
        .filter(entry => !entry.exported).slice(0, 16);
      if (!entries.length) return;
      const reply = await nativeRequest(envelope('browser-diagnostics', { entries }), 2000);
      if (!reply?.ok) {
        logRetryAfter = Date.now() + 60_000;
        return;
      }
      const sent = new Set(entries.map(entry => entry.id));
      const ack = logWrites.then(async () => {
        const current = await chrome.storage.local.get(failureLogKey);
        if (!Array.isArray(current[failureLogKey])) return;
        await chrome.storage.local.set({ [failureLogKey]: current[failureLogKey].map(entry =>
          sent.has(entry.id) ? { ...entry, exported: true } : entry) });
      });
      logWrites = ack.catch(() => {});
      await ack;
    }
  } catch {
    logRetryAfter = Date.now() + 60_000;
  } finally {
    logSending = false;
    if (logAddedWhileSending) scheduleFailureExport();
  }
}
