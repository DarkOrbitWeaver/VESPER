'use strict';

const RECOVERY_ALARM = 'vesper-chrome-downloads';
const DECISIONS_KEY = 'vesperCaptureDecisions';
const MAX_DECISIONS = 20;
const EVIDENCE_AGE = 30_000;
const responseFacts = new Map();
const earlyFailures = new Map();
const nativeAttempts = new Map();
const chromeStarts = new Map();
const observedDownloads = new Map();
const browserOperations = new Map();
let decisionWrites = Promise.resolve();
let capturePaused = null;
let captureGeneration = 0;
let captureClear = Promise.resolve();

const isSafe = item => !!item && !item.incognito && ['safe', 'deepScannedSafe'].includes(item.danger);
const startedByVesper = item => !!item?.byExtensionId && item.byExtensionId === chrome.runtime.id;
const byteCount = value => Number.isSafeInteger(value) && value >= 0 ? value : undefined;
const mimeType = value => {
  const mime = value?.split(';', 1)[0].trim().toLowerCase();
  return mime && /^[a-z0-9!#$&^_.+-]+\/[a-z0-9!#$&^_.+-]+$/.test(mime) && mime.length <= 100 ? mime : undefined;
};

const capturePreferencesReady = browserPreferencesReady.then(() => new Promise(resolve => {
  VesperBrowserPreferences.watchPause(({ paused }) => {
    if (paused !== capturePaused) {
      if (capturePaused !== null) captureGeneration++;
      capturePaused = paused;
      if (paused) {
        responseFacts.clear();
        earlyFailures.clear();
        nativeAttempts.clear();
        chromeStarts.clear();
        observedDownloads.clear();
        captureClear = decisionWrites.then(() => chrome.storage.session.remove(DECISIONS_KEY)).catch(() => {});
      }
    }
    resolve();
    return captureClear;
  });
}));

function captureAllowed(generation = captureGeneration) {
  return capturePaused === false && generation === captureGeneration;
}

async function captureCanBegin(generation = captureGeneration) {
  await capturePreferencesReady;
  await captureClear;
  return captureAllowed(generation);
}

function rememberDownloadFact(map, key, facts) {
  map.delete(key);
  map.set(key, { ...facts, seen: Date.now() });
  if (map.size > 64) map.delete(map.keys().next().value);
}

function recentDownloadFact(map, key) {
  const entry = map.get(key);
  return entry && Date.now() - entry.seen <= EVIDENCE_AGE ? entry : undefined;
}

const DOWNLOAD_TYPES = ['main_frame', 'sub_frame', 'other'];
chrome.webRequest.onHeadersReceived.addListener(async details => {
  const generation = captureGeneration;
  if (details.incognito || capturePaused === true || typeof details.url !== 'string' || details.url.length > 8192) return;
  const header = name => details.responseHeaders?.find(h => h.name.toLowerCase() === name)?.value;
  const attachment = /^attachment\b/i.test(header('content-disposition') || '');
  const type = mimeType(header('content-type'));
  if (!attachment && !['application/octet-stream', 'application/zip', 'application/x-zip-compressed',
    'application/gzip', 'application/x-7z-compressed', 'application/pdf'].includes(type)) return;
  if (!await captureCanBegin(generation)) return;
  const earlier = recentDownloadFact(responseFacts, details.url);
  const ambiguous = earlier?.ambiguous || earlier?.documentId && details.documentId
    && earlier.documentId !== details.documentId;
  rememberDownloadFact(responseFacts, details.url, {
    documentId: details.documentId, ambiguous,
    method: ['GET', 'POST', 'HEAD'].includes(details.method) ? details.method : 'other',
    status: details.statusCode, type, attachment,
    strongEtag: /^"[^"\x00-\x20\x7f]{0,254}"$/.test(header('etag') || ''),
    ranges: /\bbytes\b/i.test(header('accept-ranges') || '')
  });
}, { urls: ['http://*/*', 'https://*/*'], types: DOWNLOAD_TYPES }, ['responseHeaders']);

async function logChromeMiss(item, generation, useEarlyFailure = true) {
  if (!item || item.incognito || item.byExtensionId || !Number.isSafeInteger(item.id)
      || !/^(https?:|blob:|data:)/i.test(item.url || '') || !await captureCanBegin(generation)) return;
  await siteRulesLoaded;
  if (!captureAllowed(generation) || matchesNeverCaptureHost(item)) return;

  const original = (item.url || '').split('#', 1)[0];
  const final = (item.finalUrl || original).split('#', 1)[0];
  const evidence = recentDownloadFact(responseFacts, final);
  const observed = evidence?.ambiguous ? undefined : evidence;
  const failure = useEarlyFailure && recentDownloadFact(earlyFailures, original);
  const native = recentDownloadFact(nativeAttempts, original) || recentDownloadFact(nativeAttempts, final);
  const received = byteCount(item.bytesReceived);
  const entry = {
    id: item.id, startTime: item.startTime, at: Date.now(), outcome: 'failed',
    reason: failure?.reason || (/^(blob:|data:)/i.test(original) ? 'browser-generated'
      : observed?.method === 'POST' ? 'post' : 'chrome-created'),
    original: diagnosticRoute(original), final: diagnosticRoute(final),
    redirected: original !== final, hasQuery: original.includes('?') || final.includes('?'),
    received, size: byteCount(item.totalBytes), state: item.state,
    response: observed && { method: observed.method, status: observed.status, type: observed.type,
      attachment: observed.attachment, strongEtag: observed.strongEtag, ranges: observed.ranges }
  };
  if (failure) earlyFailures.delete(original);
  const write = decisionWrites.then(async () => {
    if (!captureAllowed(generation)) return;
    const { [DECISIONS_KEY]: saved } = await chrome.storage.session.get(DECISIONS_KEY);
    const older = Array.isArray(saved) ? saved : [];
    const prior = older.find(old => old.id === entry.id && old.startTime === entry.startTime);
    if (!useEarlyFailure && !prior) return;
    if (prior) {
      entry.reason = prior.reason;
      entry.received = Math.max(entry.received ?? 0, prior.received ?? 0);
      entry.response ??= prior.response;
    }
    await chrome.storage.session.set({ [DECISIONS_KEY]: [entry, ...older.filter(old => old.id !== entry.id)].slice(0, MAX_DECISIONS) });
  });
  decisionWrites = write.catch(() => {});
  await decisionWrites;
  if (useEarlyFailure) {
    await recordFailure('chrome-miss', entry.reason, item.referrer, original, {
      attempt: failure?.attempt || native?.attempt, job: native?.job, native: native?.state,
      browserId: item.id, chromeStarted: true, state: item.state,
      finalRoute: final, redirected: entry.redirected, query: entry.hasQuery, bytes: received, total: entry.size,
      method: observed?.method, status: observed?.status, mime: observed?.type,
      attachment: observed?.attachment, ranges: observed?.ranges, proof: observed?.strongEtag ? 'etag' : undefined
    });
  }
}

// RFC 9110 permits a one-second difference with trusted clocks. Use 60 seconds here.
function strongLastModified(value, date) {
  const stamp = value?.trim();
  if (!stamp || stamp.length > 64) return undefined;
  const modified = Date.parse(stamp), served = Date.parse(date?.trim() || '');
  return Number.isFinite(modified) && Number.isFinite(served) && served - modified >= 60_000
    && new Date(modified).toUTCString() === stamp ? stamp : undefined;
}

function chromeFileName(item) {
  const name = item?.filename?.split(/[\\/]/).at(-1);
  return name && !/\.crdownload$/i.test(name) ? name : undefined;
}

const sameDownload = (item, record) => !!item && item.startTime === record.startTime && !item.incognito;

async function sourceIdentity(url) {
  if (typeof url !== 'string' || !/^https?:\/\//i.test(url) || url.length > 8192) return null;
  const bytes = new TextEncoder().encode(url.split('#', 1)[0]);
  const digest = await crypto.subtle.digest('SHA-256', bytes);
  return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
}

const getChromeDownload = async id => (await chrome.downloads.search({ id }))[0];
const readBrowserRecords = async () => (await chrome.storage.local.get('browserDownloads')).browserDownloads || {};
const transferCommand = (transferId, operation, timeout) =>
  nativeRequest(envelope('browser-transfer', { transferId, operation }), timeout);

async function armBrowserRecovery() {
  if (!(await chrome.alarms.get(RECOVERY_ALARM)))
    await chrome.alarms.create(RECOVERY_ALARM, { delayInMinutes: 1 });
}

let recoveringChrome;
function recoverChromeDownloads() {
  recoveringChrome ??= (async () => {
    const records = await readBrowserRecords();
    const pending = {};
    for (const [key, record] of Object.entries(records)) {
      const id = Number(key);
      if (!Number.isSafeInteger(id) || !record?.transferId
          || !['attempted', 'parking', 'parked', 'releasing'].includes(record.stage)) continue;
      // The previous version could park downloads. Stop its native copy before releasing a zero-byte pause.
      const recovery = (async () => {
        const stopped = await transferCommand(record.transferId, 'abandon', 5000);
        if (!stopped?.ok || !['failed', 'complete'].includes(stopped.data?.state)) return false;
        if (stopped.data.state === 'failed' && record.resumeChrome === true) {
          const item = await getChromeDownload(id);
          if (sameDownload(item, record) && item.paused && item.state === 'in_progress'
              && item.bytesReceived === 0 && isSafe(item)) await chrome.downloads.resume(id);
        }
        return true;
      })();
      browserOperations.set(id, recovery);
      if (!await recovery.catch(() => false)) pending[key] = record;
      browserOperations.delete(id);
    }
    if (Object.keys(records).length) await chrome.storage.local.set({ browserDownloads: pending });
    if (Object.keys(pending).length) await armBrowserRecovery();
    await refreshManagedDownloads();
  })().finally(() => { recoveringChrome = undefined; });
  return recoveringChrome;
}

chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === RECOVERY_ALARM) return recoverChromeDownloads().catch(() => {});
  if (alarm.name === SOURCE_EXPIRY_ALARM) return pruneObservedSources().catch(() => {});
});

chrome.downloads.onCreated.addListener(async item => {
  const generation = captureGeneration;
  // Record ownership synchronously, before policy/storage/diagnostic awaits.
  // A pending early native stage must see a browser-created entry at zero bytes.
  if (capturePaused !== true && item && !item.incognito && !item.byExtensionId
      && Number.isSafeInteger(item.id) && /^https?:/i.test(item.url || '')) {
    const original = item.url.split('#', 1)[0], final = (item.finalUrl || original).split('#', 1)[0];
    rememberDownloadFact(chromeStarts, original, {id:item.id});
    if (final !== original) rememberDownloadFact(chromeStarts, final, {id:item.id});
  }
  try {
    if (!await captureCanBegin(generation)) return;
    await logChromeMiss(item, generation);
    if (item?.state === 'in_progress' && !item.byExtensionId && !item.incognito)
      rememberDownloadFact(observedDownloads, item.id, { startTime: item.startTime });
    await enrollAutomaticChromeDownload(item, generation);
    void refreshManagedDownloads();
  } catch {}
});

chrome.downloads.onChanged.addListener(async change => {
  const generation = captureGeneration;
  try {
    if (change.state || change.error || change.finalUrl || change.totalBytes || change.bytesReceived) {
      const seen = recentDownloadFact(observedDownloads, change.id);
      if (seen || ['complete', 'interrupted'].includes(change.state?.current) || change.error?.current) {
        const item = await getChromeDownload(change.id);
        if (seen && item?.startTime === seen.startTime) await logChromeMiss(item, generation, false);
        if (item && !item.incognito && (!item.byExtensionId || startedByVesper(item))
            && (['complete', 'interrupted'].includes(change.state?.current) || change.error?.current)
            && await captureCanBegin(generation)) {
          await siteRulesLoaded;
          if (captureAllowed(generation) && !matchesNeverCaptureHost(item)
              && /^(https?:|blob:|data:)/i.test(item.url || ''))
            await recordFailure('chrome-result', item.state, item.referrer, item.url, {
              browserId: item.id, bytes: byteCount(item.bytesReceived), total: byteCount(item.totalBytes),
              chromeError: item.error || change.error?.current, finalRoute: item.finalUrl, state: item.state
            });
        }
        if (item?.state === 'complete') await enrollAutomaticChromeDownload(item, generation);
        if (change.state?.current === 'complete' || change.state?.current === 'interrupted')
          observedDownloads.delete(change.id);
      }
    }
    void refreshManagedDownloads();
  } catch {}
});

// Old worker state contained signed response URLs. It has no consumers after the update.
void chrome.storage.session.remove('vesperDownloadCapture').catch(() => {});
void capturePreferencesReady.then(recoverChromeDownloads).catch(() => {});

// Early route for a trusted, unmodified click on a concrete file link.
// No browser download item exists yet. Stage a native GET against a read-only
// response proof, then commit ownership. Never replay an uncertain acceptance.
function githubArchiveTarget(address) {
  const url = new URL(address);
  if (url.origin !== 'https://github.com' || url.search) return null;
  const match = /^\/([\w.-]+\/[\w.-]+)\/archive\/(.+)\.(zip|tar\.gz)$/.exec(url.pathname);
  if (!match) return null;
  const [, repo, ref, format] = match;
  return new URL('/' + repo + '/' + (format === 'zip' ? 'zip' : 'tar.gz') + '/' + ref, 'https://codeload.github.com');
}

const earlyDirectInFlight = new Map();
async function earlyDirectDownload(message, sender) {
  const chromeFallback = { ok: false, fallback: true };
  const uncertain = { ok: false, fallback: false, message: 'VESPER could not confirm ownership. Check VESPER before retrying.' };
  const page = VesperPlayerSource.http(sender.url);
  const url = page && VesperEarlyDownload.candidate(message.url, page.href, message.downloadName, message.explicitIntent === true);
  const semanticOnly = !!url && !VesperEarlyDownload.candidate(message.url, page.href, message.downloadName);
  if (!url || !sender.tab || sender.tab.incognito || !Number.isSafeInteger(sender.tab.id)) return chromeFallback;
  const attempt = crypto.randomUUID();
  const started = Date.now();
  const facts = { attempt, stage: 'click', named: typeof message.downloadName === 'string', query: url.includes('?') };
  // Client time is diagnostic only, never authority for capture or ownership.
  const clickAt = Number.isSafeInteger(message.clickAt) && message.clickAt <= started
    && message.clickAt >= started - 15000 ? message.clickAt : started;
  facts.dispatchMs = started - clickAt;
  let finalUrl;
  const browserOwns = () => [url, finalUrl].some(address =>
    (recentDownloadFact(chromeStarts, address)?.seen ?? 0) >= started);
  const chromeOwned = {ok:true,browserOwned:true};
  const fallback = reason => {
    if (browserOwns()) return chromeOwned;
    if (reason && captureAllowed()) {
      rememberDownloadFact(earlyFailures, url, { reason, attempt });
      if (nativeAttempts.get(url)?.attempt === attempt) nativeAttempts.delete(url);
      void recordFailure('download-failed', reason, page.href, url, { ...facts, elapsedMs: Date.now() - started });
    }
    return chromeFallback;
  };
  const key = sender.tab.id + ':' + (sender.frameId ?? 0);
  if (earlyDirectInFlight.get(key) === url) {
    if (captureAllowed()) void recordFailure('handoff', 'repeat-suppressed', page.href, url, facts);
    return { ok: true };
  }
  if (earlyDirectInFlight.has(key) || earlyDirectInFlight.size >= 8) return fallback('early-busy');
  earlyDirectInFlight.set(key, url);
  const generation = captureGeneration;
  let transferId, accepting = false;
  const accepted = () => {
    rememberDownloadFact(nativeAttempts, url, { attempt, job: facts.job, state: 'accepted' });
    const chrome = recentDownloadFact(chromeStarts, url);
    if (chrome?.seen >= started && captureAllowed(generation))
      void recordFailure('handoff', 'possible-dual-ownership', page.href, url, {
        attempt, job: facts.job, native: 'accepted', browserId: chrome.id, chromeStarted: true, routeMatch: true
      });
    const timing = {
      dispatchMs: facts.dispatchMs, probeMs: facts.probeMs,
      stageMs: facts.nativeMs, acceptMs: facts.acceptMs,
      clickToAcceptMs: Math.max(0, Date.now() - clickAt)
    };
    if (captureAllowed(generation)) void recordStartupTiming(timing);
    return { ok: true, timing };
  };
  try {
    try {
      await siteRulesLoaded;
      if (!await captureCanBegin(generation) || matchesNeverCaptureHost({ url, referrer: page.href })) return fallback();
      // With permission, check the same signed-in representation the click would see.
      // A guest file can look valid while containing different data.
      const archiveTarget = githubArchiveTarget(url);
      const address = new URL(url);
      // Public source archives have a known CDN destination. Keep GitHub cookies out of this request.
      const allowCreds = !archiveTarget && page.protocol === 'https:' && await signInEnabled();
      const credMode = allowCreds && address.origin === page.origin ? 'include' : 'omit';
      const redirect = credMode === 'include' ? 'error' : 'follow';
      if (credMode === 'include' && await captureSiteDecision({ url, referrer: page.href }, true) !== 'allowed')
        return fallback('site-policy');
      if (!captureAllowed(generation)) return fallback();
      // Start only the local native-session handshake during the existing
      // response probe. No stage/accept message may precede verified evidence.
      VesperNativeSession.prepare?.(HOST);
      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 3500);
      let response, signedItems = null;
      facts.stage = 'probe';
      const probeStarted = Date.now();
      try {
        // Presigned file links can be valid only for GET.
        if (!VesperEarlyDownload.signedDownload(address)) {
          facts.method = 'HEAD';
          response = await fetch(url, { method: 'HEAD', credentials: credMode, redirect,
            cache: 'no-store', signal: controller.signal });
        }
        if (!response || [405, 501].includes(response.status)) {
          facts.method = 'GET';
          await response?.body?.cancel?.().catch(() => {});
          response = await fetch(url, { method: 'GET', headers: { Range: 'bytes=0-0' }, credentials: credMode, redirect,
            cache: 'no-store', signal: controller.signal });
        }
      } finally {
        clearTimeout(timeout);
        facts.probeMs = Math.max(0, Date.now() - probeStarted);
      }
      const rangeTotal = response.status === 206 ? /^bytes\s+0-0\/(\d+)$/i.exec(response.headers.get('content-range') || '')?.[1] : null;
      const acceptsRanges = response.status === 206 || (response.headers.get('accept-ranges') || '').toLowerCase().includes('bytes');
      if (response.body) await response.body.cancel().catch(() => {});
      const statusOk = response.status === 200 || (response.status === 206 && !!rangeTotal);
      const final = VesperPlayerSource.http(response.url || url);
      finalUrl = final?.href;
      const referer = message.omitReferrer ? undefined : VesperEarlyDownload.referrer(page.href, final?.href, message.referrerPolicy);
      if (credMode === 'include' && final?.origin === page.origin && !response.redirected) {
        if (!await signInEnabled()) return fallback('site-policy');
        // The cookie initiator is the document, even when it omits Referer.
        const matches = await signInCookies(final.href, page.href,
          { frame: { tabId: sender.tab.id, frameId: sender.frameId ?? 0, ...(sender.documentId ? { documentId: sender.documentId } : {}) }, userGesture: true });
        const cookie = matches.map(c => c.name + '=' + c.value).join('; ');
        if (matches.length && (!cookie || cookie.length > 8192 || /[\r\n\0]/.test(cookie))) return fallback('site-policy');
        if (cookie) signedItems = [{url:final.href,originalUrl:url,...(referer ? {referer} : {}),userAgent:navigator.userAgent,cookie}];
      }
      const mime = mimeType(response.headers.get('content-type'));
      const disposition = response.headers.get('content-disposition') || '';
      const attachment = /^\s*attachment\s*(?:;|$)/i.test(disposition);
      const explicitName = VesperEarlyDownload.explicitName(address, page, message.downloadName);
      const html = mime === 'text/html' || mime === 'application/xhtml+xml';
      const namedHtml = /\.(?:html?|xhtml)$/i.test(explicitName || '') && attachment;
      const binary = /^(?:video\/|audio\/|application\/(?:octet-stream|zip|x-zip-compressed|x-7z-compressed|x-rar-compressed|vnd\.rar|gzip|x-gzip|x-msdownload|x-msi|x-msdos-program|vnd\.android\.package-archive|x-iso9660-image|x-apple-diskimage|vnd\.debian\.binary-package|x-rpm|x-tar|x-xz|x-bzip2|zstd))/.test(mime || '');
      const lengthText = rangeTotal || response.headers.get('content-length') || '';
      const length = /^\d+$/.test(lengthText) ? Number(lengthText) : undefined;
      const rawEtag = response.headers.get('etag')?.trim();
      const strongEtag = rawEtag && /^"[^"\x00-\x20\x7f]{0,254}"$/.test(rawEtag) ? rawEtag : undefined;
      const lastModified = !rawEtag ? strongLastModified(response.headers.get('last-modified'), response.headers.get('date')) : undefined;
      const useAttachmentLength = !rawEtag && !lastModified && acceptsRanges && length >= 262_144 && attachment;
      const useGitHubArchive = !!archiveTarget && (!strongEtag && !useAttachmentLength
        && !(lastModified && length > 0) || length === 0);
      const encoding = response.headers.get('content-encoding');
      Object.assign(facts, {
        status: response.status, mime, total: byteCount(length), attachment, ranges: acceptsRanges,
        redirected: !!response.redirected, signedIn: !!signedItems, finalRoute: final?.href,
        proof: useGitHubArchive ? 'github-archive' : strongEtag ? 'etag'
          : lastModified ? 'last-modified' : useAttachmentLength ? 'attachment-length' : 'none'
      });
      if (archiveTarget && (final?.href !== archiveTarget.href || !attachment
          || !['application/zip', 'application/x-zip-compressed', 'application/gzip', 'application/x-gzip'].includes(mime)))
        return fallback('archive-response');
      if (!statusOk) return fallback('probe-status');
      if (!final || credMode === 'include' && (response.redirected || final.origin !== page.origin)
          || final.protocol !== 'https:' && final.origin !== page.origin) return fallback('redirect-unverified');
      if (semanticOnly && !attachment || html && !namedHtml || !attachment && explicitName === null && !binary) return fallback('file-intent-unverified');
      if (!useGitHubArchive && (length === undefined ? !(archiveTarget && strongEtag) : !Number.isSafeInteger(length) || length <= 0))
        return fallback('length-unverified');
      if (!useGitHubArchive && !strongEtag && !lastModified && !useAttachmentLength) return fallback('validator-unverified');
      if (encoding && encoding.toLowerCase() !== 'identity') return fallback('encoding-unverified');
      if (matchesNeverCaptureHost({ url, finalUrl: final.href, referrer: page.href }) || !captureAllowed(generation))
        return fallback();
      if (browserOwns()) return chromeOwned;
      const proof = {
        ...(Number.isSafeInteger(length) && length > 0 ? { length } : {}),
        ...(mime ? { contentType: mime } : {}),
        attachment,
        ...(acceptsRanges ? { acceptsRanges: true } : {}),
        ...(useGitHubArchive ? { basis: 'github-archive' } : strongEtag ? { etag: strongEtag, basis: 'etag' }
          : lastModified ? { lastModified, basis: 'last-modified' }
          : { basis: 'attachment-length' })
      };
      const items = signedItems || [{ url: final.href, originalUrl: url,
        ...(referer ? { referer } : {}), userAgent: navigator.userAgent }];
      // A server-specified attachment filename wins over the HTML download name.
      if (explicitName && !/;\s*filename\*?\s*=/i.test(disposition)) items[0].fileName = explicitName;
      const request = envelope(signedItems ? 'browser-submit' : 'browser-capture', { mode: 'download', authorized: !!signedItems, browserProof: proof, items });
      transferId = request.id;
      facts.stage = 'native-stage';
      rememberDownloadFact(nativeAttempts, url, { attempt, state: 'pending' });
      const nativeStarted = Date.now();
      const staged = await nativeRequest(request, 10000);
      facts.nativeMs = Date.now() - nativeStarted;
      facts.job = staged.results?.[0]?.jobId;
      facts.native = staged.results?.[0]?.transferReason || staged.code;
      if (browserOwns()) {
        // No native acceptance has been sent. Leave Chrome's original entry alone,
        // and tell the content script not to replay the suppressed default click.
        await transferCommand(transferId, 'abandon', 3000).catch(() => {});
        return chromeOwned;
      }
      const route = VesperPlanner.ownershipRoute({approved:captureAllowed(generation),
        nativeReplayVerified:staged.ok && staged.results?.[0]?.transferReady === true,
        chromeExists:browserOwns()});
      if (route.route !== 'native-http') {
        // No accept has been dispatched, so a late staged job cannot publish.
        await transferCommand(transferId, 'abandon', 3000).catch(() => {});
        return fallback(staged.code === 'site-capture-disabled' ? undefined : 'native-declined');
      }
      accepting = true;
      facts.stage = 'native-accept';
      const acceptStarted = Date.now();
      const result = await transferCommand(transferId, 'accept', 5000);
      facts.acceptMs = Math.max(0, Date.now() - acceptStarted);
      if (result.ok && ['active', 'complete'].includes(result.data?.state))
        return accepted();
    } catch { /* ambiguous acceptance must be reconciled below */ }
    if (!transferId) return fallback('probe-failed');
    if (!accepting) {
      await transferCommand(transferId, 'abandon', 3000).catch(() => {});
      return fallback('native-unavailable');
    }
    // A lost acceptance can still own the file. Query it before deciding whether Chrome may start.
    try {
      const status = await transferCommand(transferId, 'status', 5000);
      if (status.ok && ['active', 'complete'].includes(status.data?.state)) return accepted();
      const stopped = await transferCommand(transferId, 'abandon', 5000);
      if (stopped.ok && stopped.data?.state === 'complete') return accepted();
      if (stopped.ok && stopped.data?.state === 'failed') return fallback('native-accept-failed');
    } catch {}
    void recordFailure('download-failed', 'ownership-uncertain', page.href, url, { ...facts, elapsedMs: Date.now() - started });
    return uncertain;
  } finally { earlyDirectInFlight.delete(key); }
}
