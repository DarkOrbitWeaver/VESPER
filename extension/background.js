// VESPER service worker. Event-driven only: it wakes for a message, a context-menu click, a download the
// user opted to hand off, a media request, or recovery of an unfinished handoff. It holds no native connection.
'use strict';

importScripts('player-source.js');

const HOST = 'com.vesper.downloader';
const PROTOCOL = 1;
const MAX_SOURCES = 25;
const DEFAULTS = {
  handoff: 'download',         // default = VESPER decides, download, queue
  route: 'auto',
  intercept: true,             // enabled by default when the user explicitly installs the extension; can be disabled
  useSignIn: false             // Sign-in cookies are sent only after the user enables this setting.
};

// ---------------------------------------------------------------- native messaging

function nativeRequest(message, timeoutMs = 330000) {
  return new Promise((resolve) => {
    let finished = false;
    const finish = (value) => { if (!finished) { finished = true; clearTimeout(timer); resolve(value); } };
    const timer = setTimeout(() => finish({ ok: false, code: 'timeout', message: 'VESPER did not answer in time.' }), timeoutMs);
    try {
      chrome.runtime.sendNativeMessage(HOST, message, (response) => {
        const error = chrome.runtime.lastError;
        if (error) {
          const missing = /not found|forbidden|access to the specified native messaging host/i.test(error.message);
          return finish({ ok: false, code: missing ? 'host-missing' : 'host-error', message: error.message });
        }
        finish(response || { ok: false, code: 'host-error', message: 'Empty reply from the VESPER connection.' });
      });
    } catch (error) {
      finish({ ok: false, code: 'host-error', message: String(error) });
    }
  });
}

const envelope = (type, extra = {}) => ({ v: PROTOCOL, id: crypto.randomUUID(), type, ...extra });

async function settings() {
  return { ...DEFAULTS, ...(await chrome.storage.sync.get(DEFAULTS)) };
}

// ---------------------------------------------------------------- submission (durable and idempotent)

// Pending signed URLs are short-lived and kept in session storage only.
const PENDING_TTL_MS = 30 * 60_000;
const MAX_PENDING = 50;
let pendingWrites = Promise.resolve();

function prunePending(pending) {
  const now = Date.now();
  for (const [id, entry] of Object.entries(pending)) {
    if (!Number.isFinite(entry?.at) || entry.at > now + 60_000 || now - entry.at > PENDING_TTL_MS
        || !Array.isArray(entry.request?.items)) delete pending[id];
  }
  while (Object.keys(pending).length > MAX_PENDING) {
    const oldest = Object.entries(pending).sort((a, b) => a[1].at - b[1].at)[0]?.[0];
    if (oldest === undefined) break;
    delete pending[oldest];
  }
  return pending;
}

// Move pre-update entries out of persistent storage and remove any stored cookie headers.
const pendingReady = (async () => {
  const [legacy, session] = await Promise.all([
    chrome.storage.local.get('pending'), chrome.storage.session.get('pending')
  ]);
  const pending = prunePending({ ...(legacy.pending || {}), ...(session.pending || {}) });
  for (const entry of Object.values(pending)) {
    entry.request.authorized = false;
    entry.request.items = entry.request.items.map(stripSecrets);
  }
  await chrome.storage.session.set({ pending });
  await chrome.storage.local.remove('pending');
})();

function readPending() {
  return pendingReady.then(async () => {
    const pending = (await chrome.storage.session.get('pending')).pending || {};
    const previousCount = Object.keys(pending).length;
    prunePending(pending);
    if (Object.keys(pending).length !== previousCount) await chrome.storage.session.set({ pending });
    return pending;
  });
}
function changePending(change) {
  const write = pendingWrites.then(async () => {
    const pending = await readPending();
    change(pending);
    await chrome.storage.session.set({ pending: prunePending(pending) });
    await updateBadge();
  });
  pendingWrites = write.catch(() => {});
  return write;
}

async function submit(items, mode, { authorized = false, review = false } = {}) {
  const request = envelope(review ? 'batch-review' : 'submit', { mode: mode || 'ask', items, authorized });
  await changePending(pending => {
    pending[request.id] = { request: { ...request, authorized: false, items: items.map(stripSecrets) },
      at: Date.now() };
  });
  return sendSubmission(request);
}

async function sendSubmission(request) {
  const reply = await nativeRequest(request);
  if (reply.ok || reply.code === 'duplicate' || reply.code === 'cancelled') {
    await changePending(pending => { delete pending[request.id]; });
  }
  return reply;
}

function stripSecrets(item) {
  const { cookie, authorization, ...safe } = item;
  return safe;
}

async function retryPending(id) {
  const pending = await readPending();
  const entry = pending[id];
  if (!entry) return { ok: false, code: 'gone', message: 'That request is no longer pending.' };
  if (entry.chromeDownloadId !== undefined) {
    await changePending(entries => { delete entries[id]; });
    return moveChromeDownload(entry.chromeDownloadId, 'download');
  }
  const enriched = await withSignIn(entry.request.items);
  return sendSubmission({ ...entry.request, ...enriched });
}

async function updateBadge() {
  const pending = await readPending();
  const count = Object.keys(pending).length;
  await chrome.action.setBadgeBackgroundColor({ color: '#B25247' });
  await chrome.action.setBadgeText({ text: count ? '!' : '' });
}

// Without a public-suffix list, only an exact host match is treated as same-site.
// This may withhold some valid cookies, but it cannot send Strict cookies across uncertain site boundaries.
function isSameSite(targetUrl, initiatorUrl) {
  try {
    const target = new URL(targetUrl), initiator = new URL(initiatorUrl);
    return target.protocol === initiator.protocol && target.hostname === initiator.hostname;
  } catch { return false; }
}

// Only the cookies Chrome itself would attach. Every caller is something the user started at the top level
// (a clicked link, a download Chrome already began), so Lax cookies go along. SameSite=Strict cookies are for
// requests that come from the cookie's own site, so a link on another site never gets them.
async function signInCookies(url, initiatorUrl, { frame, userGesture = true } = {}) {
  let storeId;
  if (frame && chrome.cookies.getAllCookieStores) {
    const stores = await chrome.cookies.getAllCookieStores();
    storeId = stores.find(store => store.tabIds.includes(frame.tabId))?.id;
    if (!storeId) return []; // Never read another profile's cookies when a frame was supplied.
  }
  const query = { url, ...(storeId ? { storeId } : {}) };
  const cookies = await chrome.cookies.getAll(query);
  if (frame && chrome.cookies.getPartitionKey) {
    try {
      const { partitionKey } = await chrome.cookies.getPartitionKey(frame);
      if (partitionKey?.topLevelSite)
        cookies.push(...await chrome.cookies.getAll({ ...query, partitionKey }));
    } catch { /* Detached frames cannot authorize a partition. Unpartitioned cookies remain scoped. */ }
  }
  const fromSameSite = isSameSite(url, initiatorUrl);
  const seen = new Set();
  return cookies.filter(cookie => {
    if (cookie.sameSite === 'strict' && !fromSameSite) return false;
    if (cookie.sameSite !== 'no_restriction' && !fromSameSite && !userGesture) return false;
    const key = JSON.stringify([cookie.name, cookie.domain, cookie.path, cookie.partitionKey]);
    if (seen.has(key)) return false;
    seen.add(key); return true;
  });
}

async function withSignIn(items, context = {}) {
  const safe = items.map(stripSecrets);
  const prefs = await settings();
  if (!prefs.useSignIn || !(await chrome.permissions.contains({ permissions: ['cookies'] })))
    return { items: safe, authorized: false };
  const enriched = [];
  for (const item of safe) {
    let cookie;
    try {
      const matches = await signInCookies(item.url, item.referer, context);
      const header = matches.map(c => `${c.name}=${c.value}`).join('; ');
      if (header && header.length <= 8192 && !/[\r\n\0]/.test(header)) cookie = header;
    } catch { /* access can be revoked while the download is being sent */ }
    enriched.push(cookie ? { ...item, cookie } : item);
  }
  return { items: enriched, authorized: enriched.some(item => item.cookie !== undefined) };
}

// ---------------------------------------------------------------- media sources per tab (kept in session storage)

const SEGMENT = /\.(ts|m4s|aac|vtt|webvtt|cmfv|cmfa|key)(\?|$)/i;
const DIRECT = /\.(mp4|webm|m4v|mov|mkv|mp3|m4a|flac|wav|ogg|opus)(\?|$)/i;
const sourceWrites = new Map();
const mediaLookups = new Map();
const sourceSelections = new Map();
const browserChoices = new Map();
const browserChoiceStarts = new Map();
const browserChoicesKey = 'vesperBrowserChoices';
let browserChoiceWrites = Promise.resolve();
const browserChoicesReady = chrome.storage.session.get(browserChoicesKey).then(({ [browserChoicesKey]: saved }) => {
  const now = Date.now();
  for (const [token, choice] of Array.isArray(saved) ? saved : []) {
    if (typeof token === 'string' && typeof choice?.url === 'string' && /^https?:\/\//i.test(choice.url)
        && Number.isSafeInteger(choice.tab) && Number.isSafeInteger(choice.frame) && typeof choice.documentId === 'string'
        && Number.isFinite(choice.expires) && choice.expires > now)
      browserChoices.set(token, choice);
  }
}).catch(() => {});

function saveBrowserChoices() {
  const now = Date.now();
  for (const [token, choice] of browserChoices)
    if (choice.expires <= now) browserChoices.delete(token);
  while (browserChoices.size > 32) browserChoices.delete(browserChoices.keys().next().value);
  const write = browserChoiceWrites.then(() => chrome.storage.session.set({ [browserChoicesKey]: [...browserChoices] }));
  browserChoiceWrites = write.catch(() => {});
  return write;
}

async function findStartedBrowserChoice(choice) {
  if (Number.isSafeInteger(choice.downloadId)) {
    const [download] = await chrome.downloads.search({ id: choice.downloadId });
    if (download?.url === choice.url && download.byExtensionId === chrome.runtime.id) return download;
  }
  if (!Number.isFinite(choice.startedAt)) return null;
  const startedAfter = new Date(choice.startedAt - 1000).toISOString();
  const startedBefore = new Date(choice.startedAt + 10_000).toISOString();
  const downloads = await chrome.downloads.search({ startedAfter, startedBefore, limit: 128 });
  return downloads.find(download => download.url === choice.url && download.byExtensionId === chrome.runtime.id) || null;
}

async function startBrowserChoice(choice) {
  choice.started = true;
  choice.startedAt = Date.now();
  delete choice.downloadId;
  await saveBrowserChoices();
  try {
    choice.downloadId = await chrome.downloads.download({ url: choice.url, saveAs: false, conflictAction: 'uniquify' });
    await saveBrowserChoices().catch(() => {});
    return true;
  } catch {
    choice.started = false;
    delete choice.startedAt;
    delete choice.downloadId;
    await saveBrowserChoices().catch(() => {});
    return false;
  }
}

async function clearBrowserChoicesForTab(tabId) {
  await browserChoicesReady;
  for (const [token, choice] of browserChoices)
    if (choice.tab === tabId) browserChoices.delete(token);
  await saveBrowserChoices();
}

function inTabOrder(tabId, work) {
  const write = (sourceWrites.get(tabId) || Promise.resolve()).catch(() => {}).then(work);
  sourceWrites.set(tabId, write);
  return write.finally(() => { if (sourceWrites.get(tabId) === write) sourceWrites.delete(tabId); });
}

// The worker is woken for every media response on a busy video site. Keep each tab's state in memory and
// write it back at most once a second, and only when something actually changed.
const tabStates = new Map();
const tabStateWrites = new Map();
const tabFlushTimers = new Map();
const tabBadgeCounts = new Map();
const mediaPrefetchCache = new Map();
const mediaPrefetchAt = new Map();
const MEDIA_PREFETCH_TTL = 30_000;
const MEDIA_PREFETCH_COOLDOWN = 15_000;
const MEDIA_PREFETCH_LIMIT = 32;

function forgetTab(tabId) {
  clearTimeout(tabFlushTimers.get(tabId));
  tabFlushTimers.delete(tabId);
  tabStates.delete(tabId);
  tabStateWrites.delete(tabId);
  tabBadgeCounts.delete(tabId);
}

function clearMediaPrefetch(tabId) {
  mediaPrefetchAt.delete(tabId);
  for (const [key, entry] of mediaPrefetchCache)
    if (entry.tab === tabId) mediaPrefetchCache.delete(key);
}

function cachedMediaOptions(key) {
  const now = Date.now();
  for (const [cacheKey, entry] of mediaPrefetchCache)
    if (entry.expires <= now) mediaPrefetchCache.delete(cacheKey);
  return mediaPrefetchCache.get(key)?.reply;
}

function rememberMediaOptions(key, tab, reply) {
  if (!reply?.ok || (!reply.data?.choices?.length && !reply.data?.sourceChoices?.length)) return;
  while (mediaPrefetchCache.size >= MEDIA_PREFETCH_LIMIT)
    mediaPrefetchCache.delete(mediaPrefetchCache.keys().next().value);
  mediaPrefetchCache.set(key, { tab, reply, expires: Date.now() + MEDIA_PREFETCH_TTL });
}

function supportsMediaPrefetch(page) {
  const host = page.hostname.toLowerCase();
  return ['youtube.com', 'youtube-nocookie.com', 'youtu.be', 'tiktok.com', 'instagram.com',
    'vimeo.com', 'dailymotion.com', 'bilibili.com', 'reddit.com', 'redd.it']
    .some(domain => host === domain || host.endsWith('.' + domain));
}

async function tabState(tabId) {
  if (tabStates.has(tabId)) return tabStates.get(tabId);
  const key = `tab:${tabId}`;
  const stored = (await chrome.storage.session.get(key))[key];
  const state = stored || { sources: [], unsupported: null, title: '', duration: null, poster: '' };
  tabStates.set(tabId, state);
  return state;
}

async function saveTabState(tabId, state, { force = false } = {}) {
  state.sources = state.sources.slice(-MAX_SOURCES);
  tabStates.set(tabId, state);
  const now = Date.now();
  const lastWrite = tabStateWrites.get(tabId) || 0;
  if (!force && now - lastWrite < 1000) {
    if (!tabFlushTimers.has(tabId)) tabFlushTimers.set(tabId, setTimeout(() => {
      tabFlushTimers.delete(tabId);
      void inTabOrder(tabId, async () => {
        const current = tabStates.get(tabId);
        if (current) await saveTabState(tabId, current, { force: true });
      }).catch(() => {});
    }, Math.max(1, 1000 - (now - lastWrite))));
    return;
  }            // the in-memory copy is current; storage catches up later
  tabStateWrites.set(tabId, now);
  await chrome.storage.session.set({ [`tab:${tabId}`]: state });
  const count = state.sources.length;
  if (count && tabBadgeCounts.get(tabId) !== count) {      // the badge only changes when the count does
    tabBadgeCounts.set(tabId, count);
    await chrome.action.setBadgeBackgroundColor({ tabId, color: '#3D694B' });
    await chrome.action.setBadgeText({ tabId, text: String(Math.min(count, 9)) });
  }
}

function mediaPage(message, sender) {
  try {
    const frame = new URL(sender.url || sender.tab?.url);
    if (!['http:', 'https:'].includes(frame.protocol)) return null;
    const live = message.pageUrl ? new URL(message.pageUrl) : frame;
    const page = live.protocol === frame.protocol && live.origin === frame.origin ? live : frame;
    page.hash = '';
    return page;
  } catch { return null; }
}

function isYouTubePage(page) {
  return /(?:^|\.)(?:youtube\.com|youtube-nocookie\.com|youtu\.be)$/i.test(page.hostname);
}

async function addSource(tabId, source) {
  if (tabId < 0 || !source.documentId || !VesperPlayerSource.http(source.url) || SEGMENT.test(source.url)) return;
  await inTabOrder(tabId, async () => {
    const state = await tabState(tabId);
    const seen = source.seen ?? Date.now();
    const match = s => s.documentId === source.documentId && s.frameId === source.frameId && s.url === source.url;
    const previous = state.sources.find(match);
    state.sources = state.sources.filter(s => !match(s) && s.seen >= seen - 10 * 60_000);
    state.sources.push({ ...previous, ...source, seen });
    await saveTabState(tabId, state);
  });
}

function sourceLabel(source, index) {
  const url = new URL(source.url);
  return {
    token: source.token,
    label: `${source.kind.toUpperCase()} stream ${index + 1}`,
    detail: url.hostname
  };
}

function rememberSourceSelections(sources, context) {
  const now = Date.now();
  for (const [token, selection] of sourceSelections)
    if (selection.expires <= now) sourceSelections.delete(token);
  while (sourceSelections.size + sources.length > 32)
    sourceSelections.delete(sourceSelections.keys().next().value);
  return sources.map((source, index) => {
    const token = crypto.randomUUID();
    sourceSelections.set(token, { ...context, url: source.url, kind: source.kind, mime: source.mime,
      seen: source.seen, expires: now + 60_000 });
    return sourceLabel({ ...source, token }, index);
  });
}

// Narrow listeners: only media requests, plus playlist-looking URLs. The worker sleeps otherwise.
const observeMediaResponse = (details) => {
  if (details.method !== 'GET' || ![200, 206].includes(details.statusCode)) return;
  const header = (name) => details.responseHeaders?.find((h) => h.name.toLowerCase() === name)?.value;
  const type = header('content-type') || '';
  const range = header('content-range');
  const bytes = range ? Number(range.split('/')[1]) : Number(header('content-length')) || null;
  const isPlaylist = /\.(m3u8|mpd)(\?|$)/i.test(details.url) || /mpegurl|dash\+xml/i.test(type);
  if (!isPlaylist && !(details.type === 'media' || DIRECT.test(details.url) || /^(?:audio|video)\//i.test(type))) return;
  return addSource(details.tabId, {
    url: details.url,
    kind: VesperPlayerSource.kind(details.url, type), mime: type.split(';')[0].trim().toLowerCase(), bytes,
    complete: !isPlaylist && (details.statusCode === 200 || Number.isSafeInteger(bytes) && bytes > 0),
    documentId: details.documentId, frameId: details.frameId, seen: details.timeStamp
  }).catch(() => {});
 };
chrome.webRequest.onResponseStarted.addListener(observeMediaResponse,
  { urls: ['http://*/*', 'https://*/*'], types: ['media'] }, ['responseHeaders']);
chrome.webRequest.onResponseStarted.addListener(observeMediaResponse, {
  urls: ['*://*/*.m3u8*', '*://*/*.mpd*', '*://*/*.mp4*', '*://*/*.webm*', '*://*/*.m4a*',
    '*://*/*.mp3*', '*://*/*.opus*', '*://*/*.flac*', '*://*.googlevideo.com/videoplayback*',
    '*://*.tiktok.com/video/*', '*://*.tiktokcdn.com/*', '*://*.tiktokcdn-us.com/*',
    '*://*.vimeocdn.com/*', '*://*.fbcdn.net/*', '*://*.cdninstagram.com/*'],
  types: ['xmlhttprequest', 'other']
}, ['responseHeaders']);

chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (change.status === 'loading' && change.url) {
    void inTabOrder(tabId, async () => {
      forgetTab(tabId);                        // the page is gone; its sources go with it
      clearMediaPrefetch(tabId);
      await chrome.storage.session.remove(`tab:${tabId}`);
      await clearBrowserChoicesForTab(tabId);
      await chrome.action.setBadgeText({ tabId, text: '' });
    }).catch(() => {});
  }
});
chrome.tabs.onRemoved.addListener(tabId => inTabOrder(tabId, async () => {
  forgetTab(tabId);
  clearMediaPrefetch(tabId);
  await chrome.storage.session.remove(`tab:${tabId}`);
  await clearBrowserChoicesForTab(tabId);
}).catch(() => {}));

async function mediaAuthorization(pageUrl, sender) {
  const frame = Number.isInteger(sender?.tab?.id) ? {
    tabId: sender.tab.id, frameId: sender.frameId ?? 0,
    ...(sender.documentId ? { documentId: sender.documentId } : {})
  } : undefined;
  const enriched = await withSignIn([{ url: pageUrl, referer: sender?.url || pageUrl,
    userAgent: navigator.userAgent }], { frame, userGesture: false });
  return enriched.authorized ? { authorized: true, mediaAuth: enriched.items[0] } : {};
}

async function playerOptions(message, sender) {
  const page = mediaPage(message, sender);
  if (!page) return { ok: false, code: 'invalid', message: 'This page cannot be inspected.' };
  const identity = VesperPlayerSource.identity(page.href);
  const youTube = isYouTubePage(page);
  const binding = VesperPlayerSource.identity(message.playerPageUrl) === identity;
  const source = binding && VesperPlayerSource.http(message.playerUrl);
  const manifest = binding && VesperPlayerSource.http(message.manifestUrl);
  const { route } = await settings();
  const frame = sender.frameId ?? 0;
  const tab = sender.tab?.id;
  const lookupKey = [tab, frame, sender.documentId, identity, message.generation, source?.href, manifest?.href, route].join('|');
  const cached = cachedMediaOptions(lookupKey);
  if (cached) return cached;
  const prefetch = message.prefetch === true;
  if (mediaLookups.has(lookupKey)) return mediaLookups.get(lookupKey);
  if (prefetch) {
    const { prefetchFormats = false } = await chrome.storage.local.get('prefetchFormats');
    if (!prefetchFormats) return { ok: true, data: { prefetchSkipped: true } };
  }
  const task = (async () => {
    const state = await tabState(tab);
    const now = Date.now();
    const changedAt = Number.isFinite(message.mediaChangedAt) ? message.mediaChangedAt : 0;
    const cutoff = Math.max(now - 10 * 60_000, changedAt > 0 ? changedAt - 2000 : 0);
    const currentEvidence = source => sender.documentId && source.documentId === sender.documentId
      && (source.frameId ?? 0) === frame && Number.isFinite(source.seen)
      && source.seen >= cutoff && source.seen <= now + 1000;
    const observed = url => state.sources.find(s => currentEvidence(s) && s.url === url.href);
    const matching = source ? observed(source) : null;
    const candidate = url => {
      const evidence = observed(url);
      return { url: url.href, kind: evidence?.kind || VesperPlayerSource.kind(url.href), mimeType: evidence?.mime || undefined };
    };
    const matchedManifest = manifest && observed(manifest);
    const possibleManifests = [];
    if (!manifest && source === null && message.playerPlaying === true && Number.isInteger(message.generation) && message.generation > 0
        && changedAt > 0 && sender.documentId && Number.isInteger(tab)) {
      const manifestCutoff = Math.max(now - 120_000, changedAt - 2000);
      for (const evidence of state.sources) {
        if (currentEvidence(evidence) && evidence.seen >= manifestCutoff && ['hls', 'dash'].includes(evidence.kind)
            && !possibleManifests.some(item => item.url === evidence.url))
          possibleManifests.push(evidence);
      }
    }
    if (prefetch) {
      if (!supportsMediaPrefetch(page) && !matching && !matchedManifest && possibleManifests.length === 0)
        return { ok: true, data: { prefetchSkipped: true } };
      const lastPrefetch = mediaPrefetchAt.get(tab);
      if (lastPrefetch !== undefined && now - lastPrefetch < MEDIA_PREFETCH_COOLDOWN)
        return { ok: true, data: { prefetchSkipped: true } };
      mediaPrefetchAt.set(tab, now);
    }
    if (route !== 'chrome' && possibleManifests.length > 0) {
      const sources = possibleManifests.sort((a, b) => b.seen - a.seen);
      const sourceChoices = rememberSourceSelections(sources, { identity, tab, frame, documentId: sender.documentId,
        generation: message.generation, playerUrl: source?.href || message.playerUrl, title: binding ? message.title : undefined });
      return { ok: true, data: { title: binding ? message.title : undefined, sourceChoices } };
    }
    const candidates = [source, matchedManifest && matchedManifest.kind !== 'direct' ? manifest : null]
      .filter(Boolean).map(candidate);
    const complete = source && matching?.kind === 'direct' && matching.complete && /^(?:audio|video)\//.test(matching.mime)
      && !SEGMENT.test(source.href) && !['range', 'sq', 'segment'].some(key => source.searchParams.has(key));
    const title = binding || !youTube ? message.title : undefined;

    if (route !== 'chrome') {
      const reply = await nativeRequest(envelope('media-options', { ...await mediaAuthorization(page.href, sender), url: page.href, pageTitle: title,
        candidates: !youTube || candidates.length ? candidates : undefined }), 60000);
      if (reply.ok || !complete || !['host-missing', 'host-error', 'app-unavailable', 'network', 'sign-in', 'no-media', 'unavailable', 'timeout'].includes(reply.code)) return reply;
    }
    if (!complete) return { ok: false, code: 'chrome-format', message: 'Chrome hasn’t exposed a complete file for this player.' };
    await browserChoicesReady;
    const token = crypto.randomUUID();
    const container = typeName(matching.mime);
    for (const [key, choice] of browserChoices)
      if (choice.expires <= Date.now()) browserChoices.delete(key);
    while (browserChoices.size >= 32) browserChoices.delete(browserChoices.keys().next().value);
    browserChoices.set(token, { url: source.href, identity, tab, frame, documentId: sender.documentId, expires: Date.now() + 120_000 });
    await saveBrowserChoices();
    return { ok: true, data: { browserChoice: token, title, choices: [{ index: 0,
      label: matching.mime.startsWith('audio/') ? 'Original audio' : 'Original file',
      detail: 'Saves with Chrome', container }] } };
  })();
  mediaLookups.set(lookupKey, task);
  try {
    const reply = await task;
    rememberMediaOptions(lookupKey, tab, reply);
    return reply;
  }
  finally { mediaLookups.delete(lookupKey); }
}

async function selectBrowserFile(message, sender) {
  await browserChoicesReady;
  const choice = browserChoices.get(message.browserChoice);
  if (!choice || choice.expires <= Date.now() || choice.tab !== sender.tab?.id || choice.frame !== (sender.frameId ?? 0)
    || !sender.documentId || choice.documentId !== sender.documentId
    || choice.identity !== VesperPlayerSource.identity(mediaPage(message, sender)?.href))
    return { ok: false, code: 'expired-selection', message: 'Refresh the formats for the current video.' };
  const inFlight = browserChoiceStarts.get(message.browserChoice);
  if (inFlight) return await inFlight;
  const operation = confirmOrStartBrowserChoice(choice);
  browserChoiceStarts.set(message.browserChoice, operation);
  try { return await operation; }
  finally { if (browserChoiceStarts.get(message.browserChoice) === operation) browserChoiceStarts.delete(message.browserChoice); }
}

async function selectMediaSource(message, sender) {
  const token = message.sourceChoice;
  const selection = sourceSelections.get(token);
  if (!selection || selection.expires <= Date.now()) {
    sourceSelections.delete(token);
    return { ok: false, code: 'expired-selection', message: 'Refresh the formats for the current video.' };
  }
  const page = mediaPage(message, sender);
  if (!page || selection.tab !== sender.tab?.id || selection.frame !== (sender.frameId ?? 0)
      || selection.documentId !== sender.documentId || selection.identity !== VesperPlayerSource.identity(page.href)
      || selection.generation !== message.generation || selection.playerUrl !== (message.playerUrl || '')) {
    sourceSelections.delete(token);
    return { ok: false, code: 'expired-selection', message: 'Refresh the formats for the current video.' };
  }
  sourceSelections.delete(token);
  const state = await tabState(selection.tab);
  const evidence = state.sources.find(source => source.url === selection.url && source.documentId === selection.documentId
    && (source.frameId ?? 0) === selection.frame && source.seen === selection.seen);
  if (!evidence || evidence.seen < Date.now() - 120_000 || evidence.seen > Date.now() + 1000)
    return { ok: false, code: 'expired-selection', message: 'Refresh the formats for the current video.' };
  const { route } = await settings();
  if (route === 'chrome')
    return { ok: false, code: 'chrome-format', message: 'Chrome has not exposed a complete file for this player.' };
  return nativeRequest(envelope('media-options', { ...await mediaAuthorization(page.href, sender), url: page.href, pageTitle: selection.title,
    candidates: [{ url: selection.url, kind: selection.kind, mimeType: selection.mime || undefined }] }), 60000);
}

async function confirmOrStartBrowserChoice(choice) {
  if (choice.started) {
    let existing;
    try { existing = await findStartedBrowserChoice(choice); }
    catch { return { ok: false, code: 'chrome-download-status-unavailable', message: 'Chrome could not confirm this download. Try again shortly.' }; }
    if (existing) {
      choice.downloadId = existing.id;
      await saveBrowserChoices();
      return { ok: true };
    }
    choice.started = false;
    delete choice.startedAt;
    delete choice.downloadId;
    await saveBrowserChoices();
  }
  return await startBrowserChoice(choice)
    ? { ok: true }
    : { ok: false, code: 'chrome-download-failed', message: 'Chrome could not start this download.' };
}

// ---------------------------------------------------------------- describing a source: honest quality choices

const mb = (n) => (n >= 1073741824 ? `${(n / 1073741824).toFixed(2)} GB` : `${(n / 1048576).toFixed(n >= 104857600 ? 0 : 1)} MB`);
const typeName = (mime) => ({ 'video/mp4': 'MP4', 'video/webm': 'WebM', 'audio/mpeg': 'MP3', 'audio/mp4': 'M4A', 'audio/ogg': 'Ogg' }[mime] || (mime ? mime.split('/')[1]?.toUpperCase() : 'File'));

async function describe(url, durationSeconds) {
  const probe = await nativeRequest(envelope('probe', { url }), 20000);
  if (!probe.ok) return { ok: false, code: probe.code, message: probe.message };
  const p = probe.data;
  const eta = (bandwidth) => (durationSeconds > 0 ? `~${mb((bandwidth * durationSeconds) / 8)}` : '');

  if (p.kind === 'direct')
    return { ok: true, choices: [{ id: 'original', label: 'Original', detail: [typeName(p.contentType), p.contentLength ? mb(p.contentLength) : ''].filter(Boolean).join(' · '),
      url, kind: 'http', recommended: true }], more: [] };

  if (p.kind === 'hls-media')
    return { ok: true, choices: [{ id: 'stream', label: 'Stream', detail: 'HLS video', url, kind: 'hls', recommended: true }], more: [] };

  if (p.kind !== 'hls-master') return { ok: false, code: 'unsupported', message: p.message || 'This source cannot be downloaded.' };

  // Deduplicate equivalent qualities (same height): keep the richest stream for each.
  const byHeight = new Map();
  for (const v of p.variants.filter((v) => !v.audioOnly)) {
    const current = byHeight.get(v.height);
    if (!current || v.bandwidth > current.bandwidth) byHeight.set(v.height, v);
  }
  const video = [...byHeight.values()].sort((a, b) => b.height - a.height || b.bandwidth - a.bandwidth);
  const audio = p.variants.filter((v) => v.audioOnly).sort((a, b) => b.bandwidth - a.bandwidth)[0];
  const make = (v, label) => ({ id: `${label}-${v.height}`, label: `${v.height ? v.height + 'p' : 'Auto'} · ${label}`,
    detail: ['HLS', eta(v.bandwidth)].filter(Boolean).join(' · '), url: v.url, kind: 'hls', bandwidth: v.bandwidth });

  const choices = [];
  if (video.length) {
    const best = video[0], smallest = video.reduce((a, b) => (b.bandwidth < a.bandwidth ? b : a));
    choices.push({ ...make(best, 'Best'), recommended: true });
    const balanced = video.filter((v) => v !== best && v !== smallest).sort((a, b) => Math.abs(a.height - 720) - Math.abs(b.height - 720))[0];
    if (balanced) choices.push(make(balanced, 'Balanced'));
    if (smallest !== best) choices.push(make(smallest, 'Smaller'));
  }
  if (audio) choices.push({ id: 'audio', label: 'Audio only', detail: ['HLS audio', eta(audio.bandwidth)].filter(Boolean).join(' · '), url: audio.url, kind: 'hls', audio: true });
  const shown = new Set(choices.map((c) => c.url));
  const more = video.filter((v) => !shown.has(v.url)).map((v) => make(v, `${Math.round(v.bandwidth / 1000)} kbps`));
  return choices.length ? { ok: true, choices, more } : { ok: false, code: 'unsupported', message: 'No downloadable quality was found.' };
}

// ---------------------------------------------------------------- messages from the popup and content scripts

// Content scripts run inside web pages, and a page that breaks out of its sandbox can forge their messages.
// They get only what the in-page picker needs. Everything that reads other tabs, moves Chrome downloads or
// submits links is reserved for VESPER's own pages (the popup and the Chrome downloads page).
const EXTENSION_PAGES = `chrome-extension://${chrome.runtime.id}/`;
const CONTENT_COMMANDS = new Set(['smart-submit', 'media-element', 'media-options', 'media-select', 'media-source-select', 'browser-media-select']);
const PAGE_COMMANDS = new Set([...CONTENT_COMMANDS, 'tab-state', 'hello', 'chrome-downloads', 'move-download',
  'native-picker', 'describe', 'retry', 'submit']);

function commandsFor(sender) {
  if (!sender || sender.id !== chrome.runtime.id) return null;
  const url = typeof sender.url === 'string' ? sender.url : '';
  if (url.startsWith(EXTENSION_PAGES)) return PAGE_COMMANDS;        // the transfers page lives in a tab, so sender.tab says nothing
  if (sender.tab && /^https?:\/\//i.test(url)) return CONTENT_COMMANDS;
  return null;
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!commandsFor(sender)?.has(message?.type)) {
    sendResponse({ ok: false, code: 'forbidden', message: 'This page can’t ask VESPER for that.' });
    return false;
  }
  (async () => {
    await recoverChromeCaptures();
    switch (message.type) {
      case 'smart-submit': {
        const page = mediaPage({}, sender);
        if (!page || !Array.isArray(message.items) || message.items.length < 1 || message.items.length > 50)
          return { ok: false, code: 'invalid', message: 'Select between 1 and 50 files.' };
        const unique = new Set();
        const safe = [];
        for (const item of message.items) {
          const url = VesperPlayerSource.http(item?.url);
          if (!url || unique.has(url.href)) continue;
          unique.add(url.href);
          safe.push({ url: url.href, referer: page.href, userAgent: navigator.userAgent });
        }
        if (!safe.length) return { ok: false, code: 'invalid', message: 'No reusable file addresses were selected.' };
        // Untrusted content can propose links, never skip the native confirmation or choose a disk path.
        const signed = await withSignIn(safe, { frame: { tabId: sender.tab.id, frameId: sender.frameId ?? 0 }, userGesture: true });
        return submit(signed.items, 'ask', { authorized: signed.authorized, review: true });
      }
      case 'media-element': {
        // Older open tabs may still send these. Only observed responses belong in the network cache.
        return { ok: true };
      }
      case 'tab-state': {
        const state = await tabState(message.tabId ?? sender.tab?.id);
        const pending = await readPending();
        return { ok: true, state, pending: Object.entries(pending).map(([id, p]) => ({ id, at: p.at, count: p.request.items.length })) };
      }
      case 'hello': return nativeRequest(envelope('hello'), 20000);
      case 'chrome-downloads': return listChromeDownloads();
      case 'move-download': return moveChromeDownload(message.downloadId, 'download');
      case 'media-options': return playerOptions(message, sender);
      case 'media-source-select': return selectMediaSource(message, sender);
      case 'browser-media-select': return selectBrowserFile(message, sender);
      case 'media-select': {
        const { mediaSession, formatChoice, mode } = message;
        const audioTrack = typeof message.audioTrack === 'string' && message.audioTrack.length <= 256 ? message.audioTrack : undefined;
        const subtitles = Array.isArray(message.subtitles) && message.subtitles.length <= 25
          && message.subtitles.every(language => typeof language === 'string' && /^[a-z0-9_-]{1,35}$/i.test(language)) ? message.subtitles : undefined;
        const includeAutoSubtitles = message.includeAutoSubtitles === true;
        if (!/^[a-f0-9]{32}$/i.test(mediaSession || '') || !Number.isInteger(formatChoice) || formatChoice < 0 ||
            formatChoice >= 64 || !['queue', 'download'].includes(mode))
          return { ok: false, code: 'invalid', message: 'Choose an available format.' };
        // "Download part": start and end in seconds, validated again by VESPER's planner.
        let range;
        if (message.range) {
          const { start, end } = message.range;
          if (!Number.isFinite(start) || !Number.isFinite(end) || start < 0 || end <= start || end > 31_536_000)
            return { ok: false, code: 'invalid', message: 'Enter a start time before the end time.' };
          range = { start, end };
        }
        const suffix = range ? `-${Math.round(range.start * 1000).toString(36)}-${Math.round(range.end * 1000).toString(36)}` : '';
        let intentSuffix = '';
        if (audioTrack || subtitles?.length || includeAutoSubtitles) {
          const bytes = new TextEncoder().encode(JSON.stringify([audioTrack || null, [...(subtitles || [])].sort(), includeAutoSubtitles]));
          const digest = await crypto.subtle.digest('SHA-256', bytes);
          intentSuffix = '-' + [...new Uint8Array(digest)].slice(0, 16).map(byte => byte.toString(16).padStart(2, '0')).join('');
        }
        return nativeRequest(envelope('media-select', {
          id: `${mediaSession}-${formatChoice}-${mode}${suffix}${intentSuffix}`,
          mediaSession, formatChoice, mode, range, audioTrack, subtitles, includeAutoSubtitles
        }), 25000);
      }
      case 'native-picker': {
        const tabId = sender.tab?.id ?? message.tabId;
        const state = await tabState(tabId);
        const pageUrl = message.pageUrl || (await chrome.tabs.get(tabId))?.url;
        if (!/^https?:/i.test(pageUrl || ''))
          return { ok: false, code: 'invalid', message: 'This page address cannot be inspected.' };
        // No cookies or hidden credentials are sent by this command. The native resolver
        // validates and probes each supplied media hint before it can be selected.
        return nativeRequest(envelope('media-picker', {
          ...await mediaAuthorization(pageUrl, sender), url: pageUrl, pageTitle: message.title || state.title || '',
          candidates: []
        }));
      }
      case 'describe': return describe(message.url, message.duration);
      case 'retry': return retryPending(message.id);
      case 'submit': {
        const { items, authorized } = await withSignIn(message.items.map((i) => ({ referer: message.pageUrl, userAgent: navigator.userAgent, ...i })));
        const prefs = await settings();
        const mode = message.mode || (prefs.handoff === 'default' ? 'ask' : prefs.handoff);
        return submit(items, mode, { authorized });
      }
      default: return { ok: false, code: 'unknown', message: 'Unknown message.' };
    }
  })().then(sendResponse, (error) => sendResponse({ ok: false, code: 'extension-error', message: String(error) }));
  return true;   // async response
});

// ---------------------------------------------------------------- context menus (links, video, audio)

chrome.runtime.onInstalled.addListener(async () => {
  await chrome.contextMenus.removeAll();
  chrome.contextMenus.create({ id: 'vesper-smart', title: 'Smart Select files with VESPER…', contexts: ['page', 'link', 'selection'] });
  chrome.contextMenus.create({ id: 'vesper-link', title: 'Download link with VESPER', contexts: ['link'] });
  chrome.contextMenus.create({ id: 'vesper-media', title: 'Download media with VESPER', contexts: ['video', 'audio'] });
  chrome.contextMenus.create({ id: 'vesper-active-downloads', title: 'Move an active Chrome download…', contexts: ['action'] });
  await updateBadge();
  await recoverChromeDownloads();
});

chrome.runtime.onStartup.addListener(async () => { await updateBadge(); await recoverChromeDownloads(); });

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  await recoverChromeCaptures();
  if (info.menuItemId === 'vesper-active-downloads') {
    await chrome.tabs.create({ url: chrome.runtime.getURL('transfers.html') });
    return;
  }
  if (info.menuItemId === 'vesper-smart') {
    try { await chrome.tabs.sendMessage(tab.id, { type: 'vesper-smart-select' }, { frameId: info.frameId ?? 0 }); }
    catch { flash(tab?.id, { ok: false }); }
    return;
  }
  const url = info.menuItemId === 'vesper-link' ? info.linkUrl : info.srcUrl;
  if (info.menuItemId === 'vesper-media' && url && !/^https?:/i.test(url)) {
    // A blob: or data: source is made by the page itself (an MSE player). VESPER cannot fetch it, but it
    // can resolve the page's real stream. Open the media picker for this page instead of failing silently.
    const pageUrl = tab?.url;
    if (pageUrl && /^https?:/i.test(pageUrl)) {
      const reply = await nativeRequest(envelope('media-picker', { ...await mediaAuthorization(pageUrl, { tab, url: info.frameUrl || pageUrl, frameId: info.frameId ?? 0 }), url: pageUrl, pageTitle: '', candidates: [] }));
      flash(tab?.id, reply.ok ? { ok: true } : reply);
    } else {
      flash(tab?.id, { ok: false, message: 'This page cannot be inspected.' });
    }
    return;
  }
  if (!url || !/^https?:/i.test(url)) {
    if (info.menuItemId === 'vesper-media') flash(tab?.id, { ok: false });
    return;      // blob: and data: URLs cannot be fetched by another program
  }
  const prefs = await settings();
  if (prefs.route === 'chrome') {
    if (info.menuItemId !== 'vesper-link' && VesperPlayerSource.kind(url) !== 'direct') {
      flash(tab?.id, { ok: false });
      return;
    }
    try {
      await chrome.downloads.download({ url, saveAs: false, conflictAction: 'uniquify' });
      flash(tab?.id, { ok: true });
    } catch { flash(tab?.id, { ok: false }); }
    return;
  }
  // The frame that holds the link is the real initiator; an embedded frame can be another site than the tab.
  const { items, authorized } = await withSignIn([{ url, referer: info.frameUrl || info.pageUrl || tab?.url, userAgent: navigator.userAgent }],
    { frame: Number.isInteger(tab?.id) ? { tabId: tab.id, frameId: info.frameId ?? 0 } : undefined });
  const reply = await submit(items, prefs.handoff === 'default' ? 'ask' : prefs.handoff, { authorized });
  flash(tab?.id, reply);
});

function flash(tabId, reply) {
  if (tabId === undefined) return;
  chrome.action.setBadgeBackgroundColor({ tabId, color: reply.ok ? '#3D694B' : '#B25247' });
  chrome.action.setBadgeText({ tabId, text: reply.ok ? '✓' : '!' });
  setTimeout(() => chrome.action.setBadgeText({ tabId, text: '' }), 2500);
}

// ---------------------------------------------------------------- Chrome downloads

importScripts('browser-downloads.js');
