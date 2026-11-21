'use strict';

const RECOVERY_ALARM = 'vesper-chrome-downloads';
const MAX_BROWSER_RECORDS = 128;
const RESPONSE_LIFETIME = 5 * 60_000;
const NEW_DOWNLOAD_WINDOW = 15_000;
const MAX_CAPTURE_ATTEMPTS = 3;
const CAPTURE_STATE_KEY = 'vesperDownloadCapture';
const DECISIONS_KEY = 'vesperCaptureDecisions';
const MAX_DECISIONS = 20;
const responses = new Map();
const awaitingResponses = new Map();
const createdDownloads = new Map();
const requestChains = new Map();
const blockedRequests = new Map();
const browserOperations = new Map();
let recordWrites = Promise.resolve();
let captureWrites = Promise.resolve();
let decisionWrites = Promise.resolve();
let captureRecovery;

// A #fragment never reaches the server. Chrome's download list and its request log don't agree on keeping it,
// so every address is compared without one.
const withoutFragment = url => typeof url === 'string' ? url.split('#', 1)[0] : url;
const startUrl = item => withoutFragment(item?.url);
const browserUrl = item => withoutFragment(item.finalUrl || item.url);
const isSafe = item => !!item && !item.incognito && ['safe', 'deepScannedSafe'].includes(item.danger);
const startedByVesper = item => !!item?.byExtensionId && item.byExtensionId === chrome.runtime.id;
// Chrome has already downloaded bytes? Then Chrome owns the file. Never take credit for its progress.
const canMove = item => isSafe(item) && item.state === 'in_progress' && item.bytesReceived === 0
  && /^https?:\/\//i.test(browserUrl(item) || '');
const byteCount = value => Number.isSafeInteger(value) && value >= 0 ? value : undefined;
const captureAttemptCount = record => Number.isSafeInteger(record?.attempts) && record.attempts >= 0 ? record.attempts : 0;
// While Chrome doesn't know a download's size it reports 0 or -1. Neither proves anything about the file.
const knownSize = item => [item.fileSize, item.totalBytes].find(value => Number.isSafeInteger(value) && value > 0);
const mimeType = value => {
  const mime = value?.split(';', 1)[0].trim().toLowerCase();
  return mime && /^[a-z0-9!#$&^_.+-]+\/[a-z0-9!#$&^_.+-]+$/.test(mime) && mime.length <= 100 ? mime : undefined;
};

const captureStateReady = chrome.storage.session.get(CAPTURE_STATE_KEY).then(({ [CAPTURE_STATE_KEY]: saved }) => {
  const now = Date.now();
  for (const [url, response] of Array.isArray(saved?.responses) ? saved.responses : [])
    if (typeof url === 'string' && /^https?:\/\//i.test(url) && Number.isFinite(response?.seen)
        && now >= response.seen && now - response.seen <= RESPONSE_LIFETIME && typeof response.replay === 'boolean')
      responses.set(url, response);
  for (const [id, waiting] of Array.isArray(saved?.awaiting) ? saved.awaiting : [])
    if (Number.isSafeInteger(id) && Number.isSafeInteger(waiting?.id) && waiting.id === id
        && typeof waiting.startTime === 'string' && typeof waiting.url === 'string'
        && /^https?:\/\//i.test(waiting.url) && Number.isFinite(waiting.seen)
        && now >= waiting.seen && now - waiting.seen <= RESPONSE_LIFETIME)
      awaitingResponses.set(id, waiting);
  for (const [id, created] of Array.isArray(saved?.created) ? saved.created : [])
    if (Number.isSafeInteger(id) && typeof created?.startTime === 'string' && Number.isFinite(created?.seen)
        && now >= created.seen && now - created.seen <= NEW_DOWNLOAD_WINDOW && createdDownloads.size < MAX_BROWSER_RECORDS)
      createdDownloads.set(id, created);
  for (const [id, chain] of Array.isArray(saved?.chains) ? saved.chains : [])
    if (typeof id === 'string' && id.length > 0 && id.length <= 128
        && /^https?:\/\//i.test(chain?.originalUrl || '') && /^https?:\/\//i.test(chain?.currentUrl || '')
        && typeof chain.replayable === 'boolean' && Number.isFinite(chain.seen)
        && now >= chain.seen && now - chain.seen <= RESPONSE_LIFETIME && requestChains.size < MAX_BROWSER_RECORDS)
      requestChains.set(id, { ...chain, persisted: true });
  for (const [id, seen] of Array.isArray(saved?.blocked) ? saved.blocked : [])
    if (typeof id === 'string' && id.length > 0 && id.length <= 128 && Number.isFinite(seen)
        && now >= seen && now - seen <= RESPONSE_LIFETIME && blockedRequests.size < MAX_BROWSER_RECORDS)
      blockedRequests.set(id, seen);
}).catch(() => {});

let captureSettings = { intercept: true, route: 'auto' };
let captureEnabled = false;
let captureGeneration = 0;
let captureSettingsRevision = 0;
let captureSettingsReady;

function captureIsEnabled(generation = captureGeneration) {
  return captureSettingsReady.then(() => captureEnabled && generation === captureGeneration);
}

function clearCaptureMemory() {
  responses.clear();
  awaitingResponses.clear();
  createdDownloads.clear();
  requestChains.clear();
  blockedRequests.clear();
}

function clearCaptureState() {
  const write = Promise.all([captureWrites.catch(() => {}), decisionWrites.catch(() => {})]).then(async () => {
    await captureStateReady;
    clearCaptureMemory();
    await chrome.storage.session.remove([CAPTURE_STATE_KEY, DECISIONS_KEY]);
  });
  captureWrites = write.catch(() => {});
  decisionWrites = write.catch(() => {});
  return write;
}

function setCapturePreferences(next) {
  const wasEnabled = captureEnabled;
  captureSettings = next;
  captureEnabled = captureSettings.intercept && captureSettings.route !== 'chrome';
  if (!captureEnabled) {
    captureGeneration++;
    return clearCaptureState().then(async () => {
      await stopUnacceptedCaptureAttempts();
      await prepareBrowserRecordsForOptOut();
      await recoverChromeDownloads();
      await clearInactiveBrowserRecords();
    });
  }
  if (!wasEnabled) void captureSettingsReady.then(() => recoverChromeCaptures()).catch(() => {});
  return Promise.resolve();
}

chrome.storage.onChanged.addListener((changes, areaName) => {
  if (areaName !== 'sync' || (!changes.intercept && !changes.route)) return;
  captureSettingsRevision++;
  const next = { ...captureSettings };
  if (changes.intercept) next.intercept = changes.intercept.newValue !== false;
  if (changes.route) next.route = changes.route.newValue || 'auto';
  return setCapturePreferences(next);
});

captureSettingsReady = settings().then(prefs => {
  if (captureSettingsRevision === 0) {
    captureSettings = { intercept: prefs.intercept !== false, route: prefs.route || 'auto' };
    captureEnabled = captureSettings.intercept && captureSettings.route !== 'chrome';
  }
  if (!captureEnabled) return clearCaptureState();
}).catch(() => {
  captureEnabled = false;
  captureGeneration++;
  return clearCaptureState();
});

function saveCaptureState(generation = captureGeneration) {
  if (!captureEnabled || generation !== captureGeneration) return Promise.resolve();
  const now = Date.now();
  for (const [url, response] of responses)
    if (now < response.seen || now - response.seen > RESPONSE_LIFETIME) responses.delete(url);
  for (const [id, waiting] of awaitingResponses)
    if (now < waiting.seen || now - waiting.seen > RESPONSE_LIFETIME) awaitingResponses.delete(id);
  for (const [id, chain] of requestChains)
    if (now < chain.seen || now - chain.seen > RESPONSE_LIFETIME) requestChains.delete(id);
  for (const [id, created] of createdDownloads)
    if (now < created.seen || now - created.seen > NEW_DOWNLOAD_WINDOW) createdDownloads.delete(id);
  for (const [id, seen] of blockedRequests)
    if (now < seen || now - seen > RESPONSE_LIFETIME) blockedRequests.delete(id);
  while (responses.size > MAX_BROWSER_RECORDS) responses.delete(responses.keys().next().value);
  while (awaitingResponses.size > MAX_BROWSER_RECORDS) awaitingResponses.delete(awaitingResponses.keys().next().value);
  while (createdDownloads.size > MAX_BROWSER_RECORDS) createdDownloads.delete(createdDownloads.keys().next().value);
  while (requestChains.size > MAX_BROWSER_RECORDS) requestChains.delete(requestChains.keys().next().value);
  const chains = [...requestChains].filter(([, chain]) => chain.persisted && chain.replayable);
  const state = { responses: [...responses], awaiting: [...awaitingResponses], created: [...createdDownloads], chains, blocked: [...blockedRequests] };
  const write = captureWrites.catch(() => {}).then(() => {
    if (!captureEnabled || generation !== captureGeneration) return;
    return chrome.storage.session.set({ [CAPTURE_STATE_KEY]: state });
  });
  captureWrites = write.catch(() => {});
  return write;
}

function recentlyCreated(item) {
  const created = createdDownloads.get(item?.id);
  return !!created && created.startTime === item.startTime && Date.now() >= created.seen
    && Date.now() - created.seen <= NEW_DOWNLOAD_WINDOW;
}

function rememberWaitingResponse(item, url, generation) {
  awaitingResponses.set(item.id, { id: item.id, startTime: item.startTime, url, seen: Date.now() });
  while (awaitingResponses.size > MAX_BROWSER_RECORDS) awaitingResponses.delete(awaitingResponses.keys().next().value);
  return saveCaptureState(generation);
}

function updateRequestChain(requestId, url, method, statusCode) {
  if (typeof requestId !== 'string' || !requestId || typeof url !== 'string') return null;
  const now = Date.now();
  for (const [key, chain] of requestChains)
    if (now < chain.seen || now - chain.seen > RESPONSE_LIFETIME) requestChains.delete(key);
  for (const [key, seen] of blockedRequests)
    if (now < seen || now - seen > RESPONSE_LIFETIME) blockedRequests.delete(key);
  let chain = requestChains.get(requestId);
  if (!chain) chain = { originalUrl: url, currentUrl: url, replayable: !blockedRequests.has(requestId), persisted: false, seen: now };
  else if (chain.currentUrl !== url) chain.replayable = false;
  chain.currentUrl = url;
  if (method !== 'GET' || statusCode === 401 || statusCode === 407) {
    chain.replayable = false;
    chain.persisted = false;
    blockedRequests.set(requestId, now);
  }
  chain.seen = now;
  requestChains.delete(requestId);
  requestChains.set(requestId, chain);
  while (requestChains.size > MAX_BROWSER_RECORDS) requestChains.delete(requestChains.keys().next().value);
  return chain;
}

function requestChainIsRelevant(chain) {
  return [...awaitingResponses.values()].some(entry => entry.url === chain.originalUrl || entry.url === chain.currentUrl)
    || responses.has(chain.originalUrl) || responses.has(chain.currentUrl);
}

async function persistRequestChainsForDownload(item, generation) {
  if (!(await captureIsEnabled(generation))) return;
  let changed = false;
  for (const chain of requestChains.values()) {
    if (chain.replayable && chain.originalUrl === startUrl(item) && !chain.persisted) {
      chain.persisted = true;
      changed = true;
    }
  }
  if (changed) await saveCaptureState(generation);
}

function rememberRequestRedirect(details, generation) {
  if (!captureEnabled || generation !== captureGeneration) return Promise.resolve();
  const chain = updateRequestChain(details.requestId, withoutFragment(details.url), details.method);
  if (chain && typeof details.redirectUrl === 'string') {
    chain.currentUrl = withoutFragment(details.redirectUrl);
    chain.seen = Date.now();
    if (chain.replayable && requestChainIsRelevant(chain)) chain.persisted = true;
    if (chain.persisted || !chain.replayable) return saveCaptureState(generation);
  }
  return Promise.resolve();
}

// Remember only the response facts needed to identify an attachment. No request bodies or credential headers.
async function observeDownloadResponse(details, generation) {
  if (!captureEnabled || generation !== captureGeneration) return;
  const url = withoutFragment(details.url);
  const chain = updateRequestChain(details.requestId, url, details.method, details.statusCode);
  if (details.statusCode !== 200 && details.statusCode !== 206) {
    if (details.statusCode === 401 || details.statusCode === 407) await saveCaptureState(generation);
    return;
  }
  const header = name => details.responseHeaders?.find(h => h.name.toLowerCase() === name)?.value;
  const mime = mimeType(header('content-type'));
  const attachment = /^attachment\b/i.test(header('content-disposition')?.trim() || '');
  const encoding = header('content-encoding')?.trim().toLowerCase();
  const range = header('content-range')?.match(/\/(\d+)$/);
  const rawLength = details.statusCode === 206 ? range?.[1] : header('content-length');
  const length = rawLength && /^\d+$/.test(rawLength) ? byteCount(Number(rawLength)) : undefined;
  const rawTag = header('etag')?.trim();
  const etag = rawTag && /^"[^"\x00-\x20\x7f]{0,254}"$/.test(rawTag) ? rawTag : undefined;
  const fileResponse = ['main_frame', 'sub_frame', 'other'].includes(details.type)
    && mime !== 'text/html' && mime !== 'application/xhtml+xml' && (mime || length !== undefined || etag);
  if (!attachment && !fileResponse && mime !== 'application/octet-stream') return;
  if (chain?.replayable) chain.persisted = true;
  const old = responses.get(url);
  const conflicts = old && Date.now() - old.seen <= RESPONSE_LIFETIME
    && (!old.replay || old.etag && etag && old.etag !== etag
        || old.length !== undefined && length !== undefined && old.length !== length);
  // "why" is the plain reason VESPER must not fetch this file again by itself. The popup explains it.
  let why;
  if (!chain?.replayable || details.method !== 'GET') why = 'form';
  else if (encoding && encoding !== 'identity') why = 'compressed';
  else if (conflicts) why = old.why || 'changed';
  responses.delete(url);
  responses.set(url, { seen: Date.now(), replay: !why, why, length, etag, contentType: mime });
  const waiting = chain ? [...awaitingResponses.values()].filter(entry =>
    entry.url === chain.originalUrl || entry.url === chain.currentUrl) : [];
  const captured = [];
  for (const entry of waiting) {
    const item = await getChromeDownload(entry.id).catch(() => null);
    if (!captureEnabled || generation !== captureGeneration) return;
    if (item?.startTime === entry.startTime && startUrl(item) === chain.originalUrl
        && (entry.url === startUrl(item) || entry.url === browserUrl(item)) && browserUrl(item) === url
        && chain.currentUrl === url) {
      awaitingResponses.delete(entry.id);
      captured.push(item);
    }
  }
  await saveCaptureState(generation);
  for (const item of captured) void captureChromeDownloadSafely(item, generation);
}

// Only document and attachment requests can sit behind a Chrome download. Listening for every XHR and
// media response would wake the worker for the whole web.
const DOWNLOAD_TYPES = ['main_frame', 'sub_frame', 'other'];
chrome.webRequest.onHeadersReceived.addListener(details => {
  const generation = captureGeneration;
  void captureSettingsReady.then(async () => {
    if (await captureIsEnabled(generation)) await captureStateReady.then(() => observeDownloadResponse(details, generation));
  }).catch(() => {});
},
  { urls: ['http://*/*', 'https://*/*'], types: DOWNLOAD_TYPES }, ['responseHeaders']);
chrome.webRequest.onBeforeRedirect.addListener(details => {
  const generation = captureGeneration;
  void captureSettingsReady.then(async () => {
    if (await captureIsEnabled(generation)) await captureStateReady.then(() => rememberRequestRedirect(details, generation));
  }).catch(() => {});
},
  { urls: ['http://*/*', 'https://*/*'], types: DOWNLOAD_TYPES });

function recentResponse(url) {
  const observed = responses.get(url);
  return observed && Date.now() - observed.seen <= RESPONSE_LIFETIME ? observed : undefined;
}

// VESPER needs a strong ETag before it can verify that a new request returns Chrome's representation.
// Without one, "reason" says in one word why Chrome keeps the download.
function downloadProof(item, manual) {
  const hint = recentResponse(browserUrl(item));
  if (!hint && !manual) return { reason: 'unseen' };
  if (hint && !hint.replay) return { reason: hint.why || 'form' };
  const size = knownSize(item);
  if (size !== undefined && hint?.length !== undefined && size !== hint.length) return { reason: 'changed' };
  if (!hint?.etag) return { reason: 'no-validator' };
  const length = hint?.length ?? size;
  return { proof: { length, etag: hint?.etag, contentType: hint?.contentType ?? mimeType(item.mime) } };
}

// ---- why a download stayed in Chrome (shown in the popup)

function decisionLabel(item) {
  let name = chromeFileName(item), site = '';
  try {
    const url = new URL(browserUrl(item));
    if (/^https?:$/.test(url.protocol)) {
      site = url.hostname;
      name ||= decodeURIComponent(url.pathname.split('/').pop() || '');
    }
  } catch { /* a blob or a badly escaped path: fall back to a generic name */ }
  return { name: (name || 'Download').slice(0, 80), site: site.slice(0, 80) };
}

// Kept for this browser session only, newest first. File name and site, never the full link or its query,
// and nothing at all from Incognito windows.
function noteDecision(item, outcome, generation = captureGeneration) {
  if (!captureEnabled || generation !== captureGeneration || !item || item.incognito || !Number.isSafeInteger(item.id))
    return Promise.resolve();
  const entry = { id: item.id, at: Date.now(), ...decisionLabel(item), outcome };
  const write = decisionWrites.catch(() => {}).then(async () => {
    if (!captureEnabled || generation !== captureGeneration) return;
    const { [DECISIONS_KEY]: saved } = await chrome.storage.session.get(DECISIONS_KEY);
    const older = (Array.isArray(saved) ? saved : []).filter(old => old.id !== item.id);
    await chrome.storage.session.set({ [DECISIONS_KEY]: [entry, ...older].slice(0, MAX_DECISIONS) });
  });
  decisionWrites = write.catch(() => {});
  return decisionWrites;
}

// Turns VESPER's answer to a handoff into one of the popup's reasons.
function handoffOutcome(reply, result, signedIn) {
  if (!reply.ok) {
    if (reply.code === 'host-missing') return 'app-missing';
    if (['app-unavailable', 'timeout', 'host-error', 'error'].includes(reply.code)) return 'app-unavailable';
    return reply.code === 'duplicate' ? 'duplicate' : 'refused';
  }
  switch (result?.transferReason) {
    case 'auth': case 'forbidden': return signedIn ? 'site-refused' : 'sign-in';
    case 'browser-fallback': return signedIn ? 'different' : 'different-signed-out';
    case 'queued': case 'busy': case 'slow': return result.transferReason;
    default: return result?.status === 'queued' ? 'queued' : 'not-ready';
  }
}

function chromeFileName(item) {
  const name = item.filename?.split(/[\\/]/).pop();
  return name && !/\.crdownload$/i.test(name) ? name : undefined;
}


const sameDownload = (item, record) => !!item && item.startTime === record.startTime && !item.incognito;

// Browser URLs can contain private signed query parameters. Persist only a SHA-256 identity,
// never a reusable download address or credentials, for recovery across MV3 worker restarts.
async function sourceIdentity(url) {
  if (typeof url !== 'string' || !/^https?:\/\//i.test(url) || url.length > 8192) return null;
  const bytes = new TextEncoder().encode(withoutFragment(url));
  const digest = await crypto.subtle.digest('SHA-256', bytes);
  return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
}
const sameSource = async (item, record) =>
  typeof record?.sourceHash === 'string' && /^[0-9a-f]{64}$/.test(record.sourceHash)
  && await sourceIdentity(browserUrl(item)) === record.sourceHash;
const needsRecovery = record => ['parking', 'parked', 'releasing', 'import'].includes(record.stage);
const getChromeDownload = async id => (await chrome.downloads.search({ id }))[0];
const readBrowserRecords = async () => (await chrome.storage.local.get('browserDownloads')).browserDownloads || {};

async function recoverUncapturedChromeDownloads() {
  await captureStateReady;
  if (!(await captureIsEnabled())) return;
  await stopExhaustedCaptureAttempts();
  const records = await readBrowserRecords();
  // Only an onCreated event (also kept briefly across worker restarts) authorizes automatic capture.
  // Do not scan Chrome's existing downloads and adopt work it has already been doing.
  const items = await Promise.all([...createdDownloads.keys()].map(id => getChromeDownload(id).catch(() => null)));
  const recoverable = items.filter(item => recentlyCreated(item) && canMove(item) && !item.paused
    && !startedByVesper(item) && !browserOperations.has(item.id)
    && (!sameDownload(item, records[item.id] || {}) || records[item.id]?.stage === 'attempted'));
  await Promise.allSettled(recoverable.map(item => captureChromeDownloadSafely(item)));
}

async function stopExhaustedCaptureAttempts() {
  const records = await readBrowserRecords();
  const attempts = Object.entries(records).filter(([, record]) => record.stage === 'attempted');
  await Promise.allSettled(attempts.map(([key, record]) => {
    const id = Number(key);
    return inBrowserOrder(id, async () => {
      const current = (await readBrowserRecords())[id];
      if (!current || current.transferId !== record.transferId || current.stage !== 'attempted') return;
      const item = await getChromeDownload(id).catch(() => null);
      if (captureAttemptCount(current) < MAX_CAPTURE_ATTEMPTS && recentlyCreated(item)) return;
      await releaseChromeDownload(item, { ...current, id, standDownAfterRelease: true });
    });
  }));
}

function recoverChromeCaptures() {
  captureRecovery ??= recoverUncapturedChromeDownloads().catch(() => {});
  return captureRecovery;
}

async function browserRecord(item) {
  const record = (await readBrowserRecords())[item.id];
  return record && sameDownload(item, record) ? record : null;
}

function writeBrowserRecord(id, record) {
  const write = recordWrites.then(async () => {
    const records = await readBrowserRecords();
    if (record) {
      let saved = record;
      if (!captureEnabled) {
        saved = needsRecovery(record) || record.stage === 'attempted'
          ? optOutReleaseRecord(record)
          : null;
      }
      if (!saved) {
        delete records[id];
        await chrome.storage.local.set({ browserDownloads: records });
        return true;
      }
      for (const [key, entry] of Object.entries(records))
        if (!needsRecovery(entry) && Date.now() - entry.at > 60 * 60_000) delete records[key];
      if (!records[id] && Object.keys(records).length >= MAX_BROWSER_RECORDS) {
        const expendable = Object.keys(records).find(key => !needsRecovery(records[key]));
        if (expendable === undefined) return false;
        delete records[expendable];
      }
      records[id] = { ...saved, at: Date.now() };
    } else delete records[id];
    await chrome.storage.local.set({ browserDownloads: records });
    return true;
  });
  recordWrites = write.catch(() => {});
  return write;
}

function inBrowserOrder(id, work) {
  const next = (browserOperations.get(id) || Promise.resolve()).catch(() => {}).then(work);
  browserOperations.set(id, next);
  return next.finally(() => { if (browserOperations.get(id) === next) browserOperations.delete(id); });
}

const transferCommand = (transferId, operation) => nativeRequest(envelope('browser-transfer', { transferId, operation }),
  operation === 'watch' ? 42000 : operation === 'abandon' ? 5000 : 16000);
const unavailableTransferState = reply => !reply?.ok
  && ['host-missing', 'timeout', 'app-unavailable', 'host-error', 'error'].includes(reply?.code);
const chromeKeeps = (message = 'Chrome is continuing this download.') => ({ ok: false, code: 'in-chrome', chromePaused: false, message });

async function armBrowserRecovery() {
  if (!(await chrome.alarms.get(RECOVERY_ALARM)))
    await chrome.alarms.create(RECOVERY_ALARM, { delayInMinutes: 1 });
}

function moveChromeDownload(id, mode, automatic = false) {
  if (!Number.isSafeInteger(id) || id < 0) return Promise.resolve(chromeKeeps('Choose a Chrome download.'));
  return inBrowserOrder(id, async () => {
    const generation = captureGeneration;
    if (automatic && !(await captureIsEnabled(generation))) return chromeKeeps();
    const item = await getChromeDownload(id);
    if (!automatic || !recentlyCreated(item))
      return chromeKeeps('Chrome owns this download. Start it with VESPER to download using VESPER.');
    if (!canMove(item) || item.paused || !Number.isFinite(Date.parse(item.startTime)))
      return chromeKeeps('This Chrome download is unavailable to move.');
    const note = outcome => automatic ? noteDecision(item, outcome, generation) : Promise.resolve();
    const { proof, reason } = downloadProof(item, !automatic);
    if (!proof) {
      await note(reason);
      return chromeKeeps('Chrome is handling this file.');
    }
    const previous = await browserRecord(item);
    // A transfer from a previous worker must never be attached to a different redirect/source.
    if (previous?.stage === 'attempted' && !(await sameSource(item, previous))) {
      await releaseChromeDownload(item, { ...previous, id, standDownAfterRelease: true });
      return chromeKeeps('The source changed. Chrome keeps this download.');
    }
    if (previous?.stage === 'parked' || previous?.stage === 'parking') return { ok: true, chromePaused: item.paused };
    if (previous?.stage === 'done') return chromeKeeps('VESPER already has this download.');
    if (automatic && previous && previous.stage !== 'attempted') return chromeKeeps();
    if (automatic && previous?.stage === 'attempted' && Date.now() < (previous.retryAt || 0)) return chromeKeeps();
    if (automatic && previous?.stage === 'attempted' && captureAttemptCount(previous) >= MAX_CAPTURE_ATTEMPTS) {
      await releaseChromeDownload(item, { ...previous, id, standDownAfterRelease: true });
      return chromeKeeps();
    }

    const [siteDecision, sourceHash] = await Promise.all([
      shouldKeepChromeForSite(item), sourceIdentity(browserUrl(item))
    ]);
    if (siteDecision === 'policy-unavailable') {
      await note('policy-unavailable');
      return chromeKeeps('VESPER could not confirm the site rules. Chrome is continuing this download.');
    }
    if (siteDecision === 'site-rule') {
      await note('site-rule');
      return chromeKeeps('This site is set to keep automatic downloads in Chrome.');
    }
    if (!sourceHash) return chromeKeeps('The download source cannot be verified.');

    // The helper may have taken time to start. Check Chrome before starting a second request.
    const candidate = await getChromeDownload(id);
    if (!sameDownload(candidate, item) || !canMove(candidate) || candidate.paused
        || browserUrl(candidate) !== browserUrl(item) || startUrl(candidate) !== startUrl(item)
        || candidate.referrer !== item.referrer) {
      if (previous?.stage === 'attempted')
        await releaseChromeDownload(candidate, { ...previous, id, standDownAfterRelease: true });
      if (sameDownload(candidate, item))
        await note(candidate.state === 'complete' ? 'chrome-finished' : candidate.bytesReceived > 0 ? 'chrome-started' : 'not-ready');
      return chromeKeeps();
    }
    const record = previous?.stage === 'attempted'
      ? { ...previous }
      : { startTime: item.startTime, transferId: crypto.randomUUID(), stage: 'attempted', attempts: 0 };
    record.sourceHash = sourceHash;
    delete record.retryAt;
    record.attempts = captureAttemptCount(record) + 1;
    if (!(await writeBrowserRecord(id, record))) return chromeKeeps();
    if (automatic && !(await captureIsEnabled(generation))) {
      await writeBrowserRecord(id, null);
      return chromeKeeps();
    }
    const { items, authorized } = await withSignIn([{
      url: browserUrl(item), originalUrl: startUrl(item), fileName: chromeFileName(candidate),
      referer: item.referrer || undefined, userAgent: navigator.userAgent
    }]);
    if (automatic && !(await captureIsEnabled(generation))) {
      await writeBrowserRecord(id, null);
      return chromeKeeps();
    }
    const reply = await nativeRequest(envelope('browser-submit', { id: record.transferId, mode, items, authorized, browserProof: proof }), 24000);
    // Chrome may finish while native verification is in flight. Its completed file is never ours.
    const latest = await getChromeDownload(id);
    if (!sameDownload(latest, record) || latest.state === 'complete' || !(await sameSource(latest, record))) {
      await releaseChromeDownload(latest, { ...record, id });
      if (latest?.state === 'complete') await note('chrome-finished');
      return chromeKeeps();
    }
    const result = reply.results?.[0];
    if (automatic && !(await captureIsEnabled(generation))) {
      await releaseChromeDownload(latest, { ...record, id });
      return chromeKeeps();
    }
    if (!reply.ok || result?.transferReady !== true) {
      let stopped;
      if (reply.ok || unavailableTransferState(reply) && reply.code !== 'host-missing'
          || record.attempts >= MAX_CAPTURE_ATTEMPTS) {
        // The helper may have started a candidate even if its reply was lost. Keep the cleanup record until it answers.
        stopped = await releaseChromeDownload(latest, { ...record, id, standDownAfterRelease: true });
      } else {
        await writeBrowserRecord(id, { startTime: record.startTime, stage: 'stand-down' });
      }
      // Chrome never paused for this candidate. Recovery only cleans up the VESPER half.
      await note(stopped?.ok && stopped.data?.state === 'complete' ? 'capture-error' : handoffOutcome(reply, result, authorized));
      return chromeKeeps(reply.ok ? undefined : reply.message || undefined);
    }

    const current = await getChromeDownload(id);
    if (!sameDownload(current, record) || !canMove(current) || !(await sameSource(current, record))
        || automatic && current.paused) {
      await releaseChromeDownload(current, { ...record, id });
      await note(sameDownload(current, record) && current.state === 'complete' ? 'chrome-finished' : 'not-ready');
      return chromeKeeps();
    }
    record.stage = 'parking';
    record.resumeChrome = !current.paused;
    try {
      await writeBrowserRecord(id, record);
      await armBrowserRecovery();
      if (automatic && !(await captureIsEnabled(generation))) {
        await releaseChromeAfterOptOut(id, record);
        return chromeKeeps();
      }
      if (record.resumeChrome) await chrome.downloads.pause(id);
      const paused = await getChromeDownload(id);
      if (automatic && !(await captureIsEnabled(generation))) {
        await releaseChromeAfterOptOut(id, record);
        return chromeKeeps();
      }
      const canAccept = sameDownload(paused, record) && canMove(paused) && paused.paused
        && await sameSource(paused, record);
      if (automatic && !(await captureIsEnabled(generation))) {
        await releaseChromeAfterOptOut(id, record);
        return chromeKeeps();
      }
      const accepted = canAccept ? await transferCommand(record.transferId, 'accept') : null;
      if (automatic && !(await captureIsEnabled(generation))) {
        await releaseChromeAfterOptOut(id, record);
        return chromeKeeps();
      }
      if (canAccept && accepted?.ok && ['active', 'complete'].includes(accepted.data?.state)) {
        // Chrome can finish between the paused check and the native acceptance reply.
        const confirmed = await getChromeDownload(id);
        if (automatic && !(await captureIsEnabled(generation))) {
          await releaseChromeAfterOptOut(id, record);
          return chromeKeeps();
        }
        const confirmedSourceMatches = sameDownload(confirmed, record) && await sameSource(confirmed, record);
        if (automatic && !(await captureIsEnabled(generation))) {
          await releaseChromeAfterOptOut(id, record);
          return chromeKeeps();
        }
        if (!sameDownload(confirmed, record) || !canMove(confirmed) || !confirmed.paused
            || !confirmedSourceMatches) {
          if (sameDownload(confirmed, record) && confirmed.state === 'complete') {
            await releaseChromeDownload(confirmed, { ...record, id });
            await note('chrome-finished');
          } else {
            // We paused Chrome. Only resume after the native transfer has stopped.
            await restoreChromeDownload(id, record);
            await note('not-ready');
          }
          return chromeKeeps();
        }
        record.stage = 'parked';
        await writeBrowserRecord(id, record);
        if (automatic && !(await captureIsEnabled(generation))) {
          await releaseChromeAfterOptOut(id, record);
          return chromeKeeps();
        }
        watchChromeDownload(id, record);
        await noteDecision(item, 'moved');
        return { ...reply, chromePaused: true };
      }
    } catch { /* The recovery record was written before changing Chrome. */ }
    await restoreChromeDownload(id, record);
    await note('not-ready');
    return chromeKeeps();
  });
}

// Chrome finished its own file. The native candidate may still be running, so abort it
// before dropping the durable recovery record. Never cancel or erase Chrome's completed file.
async function releaseChromeDownload(item, record) {
  if (!record || !record.transferId) return;
  if (record.stage === 'done') {
    await writeBrowserRecord(item?.id ?? record.id, null);
    return;
  }
  const id = item?.id ?? record.id;
  if (!Number.isSafeInteger(id)) return;
  const releasing = { ...record, stage: 'releasing', id };
  await writeBrowserRecord(id, releasing);
  let stopped;
  try { stopped = await transferCommand(record.transferId, 'abandon'); }
  catch { /* The native helper may be offline. Retry on the recovery alarm. */ }
  if (stopped?.ok && ['failed', 'complete'].includes(stopped.data?.state)) {
    if (record.standDownAfterRelease && captureEnabled)
      await writeBrowserRecord(id, { startTime: record.startTime, stage: 'stand-down' });
    else
      await writeBrowserRecord(id, null);
    if (item?.state === 'complete') await noteDecision(item, 'chrome-finished');
  } else {
    await armBrowserRecovery();
  }
  return stopped;
}

async function releaseChromeAfterOptOut(id, record) {
  await writeBrowserRecord(id, optOutReleaseRecord(record));
  let stopped;
  try { stopped = await transferCommand(record.transferId, 'abandon'); }
  catch { /* Keep Chrome parked until the native transfer can be checked again. */ }
  if (!stopped?.ok || !['failed', 'complete'].includes(stopped.data?.state)) {
    await armBrowserRecovery();
    return;
  }

  let item;
  try { item = await getChromeDownload(id); }
  catch {
    await armBrowserRecovery();
    return;
  }
  const shouldResume = item && (item.state === 'in_progress' && item.paused
    || item.state === 'interrupted' && item.canResume && !item.error?.startsWith('USER_'));
  if (sameDownload(item, record) && record.resumeChrome && shouldResume) {
    try { await chrome.downloads.resume(id); }
    catch {
      await armBrowserRecovery();
      return;
    }
  }

  if (captureEnabled) await writeBrowserRecord(id, { startTime: record.startTime, stage: 'stand-down' });
  else await writeBrowserRecord(id, null);
}

async function stopUnacceptedCaptureAttempts() {
  await recordWrites;
  const records = await readBrowserRecords();
  const attempts = Object.entries(records).filter(([, record]) => record.stage === 'attempted');
  await Promise.allSettled(attempts.map(([key, record]) => {
    const id = Number(key);
    return inBrowserOrder(id, async () => {
      const current = (await readBrowserRecords())[id];
      if (!current || current.transferId !== record.transferId || current.stage !== 'attempted') return;
      const item = await getChromeDownload(id).catch(() => null);
      await releaseChromeDownload(item, { ...current, id });
    });
  }));
}

async function clearInactiveBrowserRecords() {
  await recordWrites;
  const records = await readBrowserRecords();
  let changed = false;
  for (const [id, record] of Object.entries(records)) {
    if (needsRecovery(record) || record.stage === 'attempted') continue;
    delete records[id];
    changed = true;
  }
  if (changed) await chrome.storage.local.set({ browserDownloads: records });
}

async function prepareBrowserRecordsForOptOut() {
  const write = recordWrites.then(async () => {
    const records = await readBrowserRecords();
    let changed = false;
    for (const [key, record] of Object.entries(records)) {
      if (!needsRecovery(record) && record.stage !== 'attempted') continue;
      records[key] = { ...optOutReleaseRecord(record), at: Date.now() };
      changed = true;
    }
    if (changed) await chrome.storage.local.set({ browserDownloads: records });
  });
  recordWrites = write.catch(() => {});
  await write;
}

function optOutReleaseRecord(record) {
  return {
    startTime: record.startTime,
    transferId: record.transferId,
    stage: 'releasing',
    resumeChrome: record.resumeChrome === true,
    preserveChrome: true
  };
}

async function restoreChromeDownload(id, record) {
  let stopped;
  try { stopped = await transferCommand(record.transferId, 'abandon'); }
  catch {
    await armBrowserRecovery();
    return;
  }
  if (stopped.ok && stopped.data?.state === 'complete') { await finishBrowserHandoff(id, record); return; }
  if (!stopped.ok || stopped.data?.state !== 'failed') {
    // Keep Chrome parked until VESPER confirms the transfer has stopped or completed.
    await armBrowserRecovery();
    return;
  }
  const item = await getChromeDownload(id);
  if (sameDownload(item, record) && record.resumeChrome &&
      (item.state === 'in_progress' && item.paused || item.state === 'interrupted' && item.canResume && !item.error?.startsWith('USER_')))
    await chrome.downloads.resume(id);
  await writeBrowserRecord(id, { ...record, stage: 'stand-down' });
}

async function finishBrowserHandoff(id, record) {
  if (!captureEnabled) {
    await releaseChromeAfterOptOut(id, record);
    return;
  }

  const item = await getChromeDownload(id);
  if (sameDownload(item, record) && item.state === 'complete') {
    await releaseChromeDownload(item, { ...record, id });
    return;
  }

  // Native completion does not authorize deleting a Chrome file unless its *current* browser
  // request is still our original, zero-byte parked candidate. Recheck after the async hash.
  if (sameDownload(item, record) && canMove(item) && item.paused && await sameSource(item, record)) {
    const confirmed = await getChromeDownload(id);
    if (!captureEnabled) {
      await releaseChromeAfterOptOut(id, record);
      return;
    }
    if (sameDownload(confirmed, record) && canMove(confirmed) && confirmed.paused
        && await sameSource(confirmed, record)) {
      if (!captureEnabled) {
        await releaseChromeAfterOptOut(id, record);
        return;
      }
      const finalCheck = await getChromeDownload(id);
      if (!captureEnabled) {
        await releaseChromeAfterOptOut(id, record);
        return;
      }
      if (sameDownload(finalCheck, record) && finalCheck.state === 'interrupted'
          && finalCheck.error?.startsWith('USER_')) {
        await writeBrowserRecord(id, captureEnabled
          ? { startTime: record.startTime, stage: 'stand-down' }
          : null);
        return;
      }
      if (sameDownload(finalCheck, record) && canMove(finalCheck) && finalCheck.paused
          && browserUrl(finalCheck) === browserUrl(confirmed)) {
        try {
          await chrome.downloads.cancel(id);
          const cancelled = await getChromeDownload(id);
          if (sameDownload(cancelled, record) && cancelled.state === 'interrupted'
              && cancelled.error?.startsWith('USER_')) {
            await writeBrowserRecord(id, captureEnabled
              ? { startTime: record.startTime, stage: 'stand-down' }
              : null);
            return;
          }
          // Only erase after Chrome confirms cancellation. A completed copy remains in Chrome.
          if (!cancelled || sameDownload(cancelled, record) && cancelled.state === 'interrupted') {
            if (cancelled && captureEnabled) await chrome.downloads.erase({ id }).catch(() => {});
            if (!captureEnabled) {
              await releaseChromeAfterOptOut(id, record);
              return;
            }
            await writeBrowserRecord(id, null);
            return;
          }
          if (sameDownload(cancelled, record) && cancelled.state === 'complete') {
            await releaseChromeDownload(cancelled, { ...record, id });
            return;
          }
        } catch { /* Chrome still owns any copy the extension could not cancel. */ }
      }
    }
  }

  const latest = await getChromeDownload(id);
  if (sameDownload(latest, record) && latest.state === 'complete') {
    await releaseChromeDownload(latest, { ...record, id });
    return;
  }
  if (sameDownload(latest, record) && latest.state === 'interrupted' && latest.error?.startsWith('USER_')) {
    await writeBrowserRecord(id, captureEnabled
      ? { startTime: record.startTime, stage: 'stand-down' }
      : null);
    return;
  }
  // Changed source, already-downloaded bytes, or Chrome was resumed independently.
  // Do not erase it just because VESPER finished a separate copy.
  if (sameDownload(latest, record) && record.resumeChrome && latest.state === 'in_progress' && latest.paused)
    await chrome.downloads.resume(id).catch(() => {});
  await writeBrowserRecord(id, { ...record, stage: 'stand-down' });
}

function watchChromeDownload(id, record) {
  const startedAt = Date.now();
  void transferCommand(record.transferId, 'watch').then(reply => {
    if (reply.ok && reply.data?.state === 'active') {
      if (Date.now() - startedAt >= 20000) {
        return readBrowserRecords().then(records => {
          const current = records[id];
          if (current?.transferId === record.transferId && current.stage === 'parked')
            watchChromeDownload(id, current);
        });
      }
      return armBrowserRecovery();
    }
    return inBrowserOrder(id, () => reconcileChromeDownload(id, record, reply));
  }).catch(() => armBrowserRecovery());
}

async function reconcileChromeDownload(id, record, knownReply) {
  const current = (await readBrowserRecords())[id];
  if (!current || current.transferId !== record.transferId || current.stage !== record.stage) return;
  const item = await getChromeDownload(id);
  if (record.stage === 'releasing' || !sameDownload(item, record)) {
    if (current.preserveChrome) await releaseChromeAfterOptOut(id, { ...current, id });
    else await releaseChromeDownload(item, { ...current, id });
    return;
  }
  // Chrome finished it. The file stays Chrome's; VESPER only stands down its own copy.
  if (item.state === 'complete') { await releaseChromeDownload(item, { ...current, id }); return; }
  if (!(await sameSource(item, record))) {
    // Old records without a source hash also fail closed. Resume Chrome only after native stops.
    await restoreChromeDownload(id, record);
    return;
  }
  if (record.stage === 'import') { await writeBrowserRecord(id, null); return; }
  if (record.stage === 'parking' && !(await captureIsEnabled())) {
    await restoreChromeDownload(id, record);
    return;
  }
  if (item.state === 'in_progress' && !item.paused) { await restoreChromeDownload(id, record); return; }
  if (record.stage === 'parking') {
    if (!canMove(item) || !item.paused) {
      await restoreChromeDownload(id, record);
      return;
    }
    const accepted = await transferCommand(record.transferId, 'accept');
    if (!accepted?.ok) {
      if (unavailableTransferState(accepted)) {
        await armBrowserRecovery();
        return;
      }
      await restoreChromeDownload(id, record);
      return;
    }
    record.stage = 'parked';
    await writeBrowserRecord(id, record);
  }
  const reply = knownReply || await transferCommand(record.transferId, 'status');
  if (unavailableTransferState(reply)) {
    await armBrowserRecovery();
    return;
  }
  if (reply.ok && reply.data?.state === 'active') return;
  if (reply.ok && reply.data?.state === 'complete') { await finishBrowserHandoff(id, record); return; }
  await restoreChromeDownload(id, record);
}

async function recoverChromeDownloads() {
  await recoverChromeCaptures();
  const records = Object.entries(await readBrowserRecords()).filter(([, record]) => needsRecovery(record));
  await Promise.allSettled(records.map(([key, record]) =>
    inBrowserOrder(Number(key), () => reconcileChromeDownload(Number(key), record))));
  if (Object.values(await readBrowserRecords()).some(needsRecovery)) await armBrowserRecovery();
}


async function captureChromeDownload(item, generation = captureGeneration) {
  await captureStateReady;
  if (item?.state === 'complete') {
    // Too late to take over, and Chrome's file is not VESPER's to list. Just stand down if we had started one.
    const removed = createdDownloads.delete(item.id);
    const hadWaiting = awaitingResponses.delete(item.id);
    if (captureEnabled && generation === captureGeneration && (hadWaiting || removed))
      await saveCaptureState(generation);
    await inBrowserOrder(item.id, async () => {
      const current = await getChromeDownload(item.id);
      if (current?.startTime === item.startTime) await releaseChromeDownload(current, await browserRecord(current));
    });
    return;
  }
  if (!(await captureIsEnabled(generation))) return;
  await persistRequestChainsForDownload(item, generation);
  if (!(await captureIsEnabled(generation))) return;
  if (startedByVesper(item) || !recentlyCreated(item)) return;
  const wanted = captureEnabled;
  if (!canMove(item) || item.paused) {
    // A file the page built itself (blob: or data:) has no address VESPER could fetch.
    const pageMade = isSafe(item) && item.state === 'in_progress' && /^(blob|data):/i.test(item.finalUrl || item.url || '');
    if (wanted && pageMade && await captureIsEnabled(generation)) await noteDecision(item, 'page-made');
    else if (wanted && isSafe(item) && item.state === 'in_progress' && item.bytesReceived > 0
        && await captureIsEnabled(generation))
      await noteDecision(item, 'chrome-started');
    return;
  }
  const url = browserUrl(item);
  const { proof, reason } = downloadProof(item, false);
  if (!proof) {
    if (!recentResponse(url)) await rememberWaitingResponse(item, url, generation);
    else if (awaitingResponses.delete(item.id)) await saveCaptureState(generation);
    if (wanted && await captureIsEnabled(generation)) await noteDecision(item, reason);
    return;
  }
  if (awaitingResponses.delete(item.id)) await saveCaptureState(generation);
  if (wanted && await captureIsEnabled(generation)) {
    const prefs = await settings();
    if (await captureIsEnabled(generation))
      await moveChromeDownload(item.id, prefs.handoff, true);
  }
}

async function captureChromeDownloadSafely(item, generation = captureGeneration) {
  try { await captureChromeDownload(item, generation); }
  catch {
    if (!(await captureIsEnabled(generation)) || !canMove(item) || item.incognito || item.paused) return;
    try {
      const prefs = await settings();
      if (prefs.intercept && prefs.route !== 'chrome') await noteDecision(item, 'capture-error');
    } catch { /* Chrome still owns the file if the extension cannot save its explanation. */ }
  }
}

chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === RECOVERY_ALARM) return recoverChromeDownloads().catch(() => {});
  if (alarm.name === SOURCE_EXPIRY_ALARM) return pruneObservedSources().catch(() => {});
});
chrome.downloads.onCreated.addListener(item => {
  const seen = Date.now();
  const generation = captureGeneration;
  return captureSettingsReady.then(async () => {
    if (!(await captureIsEnabled(generation))) return;
    await captureStateReady;
    if (!(await captureIsEnabled(generation))) return;
    if (Number.isSafeInteger(item?.id) && item.state === 'in_progress' && !item.incognito && !startedByVesper(item)) {
      createdDownloads.set(item.id, { startTime: item.startTime, seen });
      await saveCaptureState(generation);
    }
    await captureChromeDownloadSafely(item, generation);
  }).catch(() => {});
});
chrome.downloads.onChanged.addListener(async change => {
  try {
    if (change.paused?.current === false && change.state?.current !== 'complete') {
      await inBrowserOrder(change.id, async () => {
        const record = (await readBrowserRecords())[change.id];
        if (record?.stage === 'parked') await reconcileChromeDownload(change.id, record);
      });
    } else if (change.state?.current === 'complete' || ['safe', 'deepScannedSafe'].includes(change.danger?.current)
        || change.finalUrl || change.totalBytes || change.fileSize) {
      const item = await getChromeDownload(change.id);
      if (item?.state === 'complete') await captureChromeDownloadSafely(item);
      else if (recentlyCreated(item) && await captureIsEnabled()) await captureChromeDownloadSafely(item);
    }
  } catch { /* The existing Chrome file or its recovery record is still available. */ }
});

// A new MV3 worker has no worker-start event. Reconcile the short-lived onCreated evidence at evaluation time.
void captureSettingsReady.then(async () => {
  if (!captureEnabled) await stopUnacceptedCaptureAttempts();
  if (!captureEnabled) await prepareBrowserRecordsForOptOut();
  await recoverChromeDownloads();
  if (!captureEnabled) await clearInactiveBrowserRecords();
}).catch(() => {});
