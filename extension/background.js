// VESPER service worker. Event-driven only: it wakes for a message, a context-menu click, a download the
// user opted to hand off, a media request, or recovery of an unfinished handoff. It holds no native connection.
'use strict';

importScripts('native-session.js', 'vesper-experience.js', 'player-source.js', 'media-evidence.js', 'vesper-intelligence.js', 'vesper-batch-events.js', 'browser-body.js');

const HOST = 'com.vesper.downloader';
const PROTOCOL = 1;
const MAX_SOURCES = 25;
const MAX_TOTAL_SOURCES = 200;
const SOURCE_TTL = 10 * 60_000;
// Manifests describe the player's media for as long as the page lives, so a late
// click after long playback can still use them. Navigation clears them; signed links are revalidated.
const PLAYER_SOURCE_TTL = 60 * 60_000;
// Picker verification: a few strong candidates checked in parallel, each through the one-source command.
const VERIFY_LIMIT = 5;
const VERIFY_DEADLINE_MS = 20_000;
const LOOKUP_HEDGE_MS = 450; // Do not serialize two slow, independent read-only discovery paths.
// Answers that mean the page lookup itself was turned down, as opposed to a helper or network problem.
const LOOKUP_REFUSALS = ['browser-context', 'sign-in', 'site-refused', 'no-media', 'unavailable', 'source-expired'];
// Tabs with video get a short-lived listener for script-fetched media identified by Content-Type.
const MANIFEST_WATCH_MS = 3 * 60_000;
const MAX_WATCHED_TABS = 6;
const SOURCE_EXPIRY_ALARM = 'vesper-media-sources';
const DEFAULTS = {
  handoff: 'download',
  route: 'auto',
  intercept: true,             // enabled by default when the user explicitly installs the extension; can be disabled
  useSignIn: true              // Chrome must grant cookie access before VESPER can use it.
};

// ---------------------------------------------------------------- native messaging

// Requests share one multiplexed native connection (native-session.js). Older hosts that do not
// understand sessions are detected by the handshake and get one process per message, as before.
function nativeRequest(message, timeoutMs = 330000) {
  return VesperNativeSession.request(HOST, message, timeoutMs);
}

const envelope = (type, extra = {}) => ({ v: PROTOCOL, id: crypto.randomUUID(), type, ...extra });

async function settings() {
  const prefs = { ...DEFAULTS, ...(await chrome.storage.sync.get(DEFAULTS)) };
  prefs.handoff = prefs.handoff === 'queue' ? 'queue' : 'download';
  return prefs;
}

async function extensionPaused() {
  const { vesperPaused = false } = await chrome.storage.local.get('vesperPaused').catch(() => ({}));
  return vesperPaused === true;
}

// Keep only opaque receipt capabilities, not file names, URLs or body bytes.
// A service worker restart must not turn a lost finish reply into another copy.
const BODY_RECEIPTS = 'vesperBodyReceiptsV1';
let bodyReceiptWrites = Promise.resolve();
function bodyScope(sender) {
  if (sender?.tab?.incognito || sender?.id !== chrome.runtime.id) return null;
  return VesperIntelligence.safeScope({tabId: sender?.tab?.id, frameId: sender?.frameId ?? 0, documentId: sender?.documentId});
}
async function rememberBodyReceipt(transferId, sender) {
  const scope = bodyScope(sender);
  if (!scope) throw new Error('Invalid browser copy scope');
  const write = bodyReceiptWrites.catch(() => {}).then(async () => {
    const stored = (await chrome.storage.session.get(BODY_RECEIPTS))[BODY_RECEIPTS];
    const now = Date.now();
    const entries = (Array.isArray(stored) ? stored : []).filter(entry => entry.expires > now);
    entries.push({transferId, scope, expires: now + 2 * 60 * 60_000});
    await chrome.storage.session.set({[BODY_RECEIPTS]: entries.slice(-64)});
  });
  bodyReceiptWrites = write;
  await write;
}
async function bodyReceiptStatus(message, sender) {
  const scope = bodyScope(sender);
  const id = message.transferId;
  if (!scope || typeof id !== 'string' || !/^[a-f0-9-]{36}$/i.test(id))
    return {ok:false,code:'missing'};
  await bodyReceiptWrites.catch(() => {});
  const stored = (await chrome.storage.session.get(BODY_RECEIPTS))[BODY_RECEIPTS];
  if (!Array.isArray(stored) || !stored.some(entry => entry.transferId === id
      && entry.scope === scope && entry.expires > Date.now()))
    return {ok:false,code:'missing'};
  return nativeRequest(envelope('browser-body', {operation:'status',transferId:id,origin:new URL(sender.url).origin + '/'}), 8000);
}

// A native port exists only during an explicit browser-body copy. No new
// permission, cookie export, or connection to the desktop while idle.
chrome.runtime.onConnect?.addListener(port => {
  if (port.name !== VesperBrowserBody.PORT) return;
  if (port.sender?.id !== chrome.runtime.id || !VesperBrowserBody.relay(port, {
    nativeHost: HOST, makeId: () => crypto.randomUUID(),
    connectNative: host => chrome.runtime.connectNative(host),
    rememberReceipt: rememberBodyReceipt,
    canBegin: async () => !(await extensionPaused()) && (await settings()).route !== 'chrome'
  })) port.disconnect();
});

const SITE_CAPTURE_RULES_KEY = 'vesperSiteNeverCaptureHosts';
const SITE_POLICY_TIMEOUT = 15_000;
let siteNeverCaptureHosts = new Set();
let siteRulesLoaded = chrome.storage.local.get(SITE_CAPTURE_RULES_KEY).then(async saved => {
  const hosts = normalizeNeverCaptureHosts(saved[SITE_CAPTURE_RULES_KEY]);
  siteNeverCaptureHosts = new Set(hosts);
}).catch(() => {});
let siteRulesRefresh = null;

function normalizeRuleHost(value) {
  if (typeof value !== 'string' || value.length > 253) return null;
  const text = value.trim().replace(/\.+$/, '');
  if (!text || /[\\/@?#*\s]/.test(text)
      || text.startsWith('[') !== text.endsWith(']')) return null;
  try {
    const host = text.includes(':') && !text.startsWith('[') ? `[${text}]` : text;
    const parsed = new URL(`https://${host}/`);
    if (parsed.username || parsed.password || parsed.port || parsed.pathname !== '/' || parsed.search || parsed.hash) return null;
    return parsed.hostname.toLowerCase().replace(/^\[|\]$/g, '').replace(/\.$/, '') || null;
  } catch { return null; }
}

function normalizeNeverCaptureHosts(value) {
  if (!Array.isArray(value) || value.length > 100) return null;
  const hosts = [];
  for (const host of value) {
    const normalized = normalizeRuleHost(host);
    if (!normalized) return null;
    if (!hosts.includes(normalized)) hosts.push(normalized);
  }
  return hosts;
}

async function cacheNeverCaptureHosts(reply) {
  await siteRulesLoaded;
  const hosts = normalizeNeverCaptureHosts(reply?.data?.siteRules?.neverCaptureHosts);
  if (reply?.ok !== true || !hosts) return false;
  await chrome.storage.local.set({ [SITE_CAPTURE_RULES_KEY]: hosts });
  siteNeverCaptureHosts = new Set(hosts);
  return true;
}

async function refreshNeverCaptureHosts() {
  await siteRulesLoaded;
  if (siteRulesRefresh) return siteRulesRefresh;
  siteRulesRefresh = (async () => {
    try {
      const reply = await nativeRequest(envelope('hello'), SITE_POLICY_TIMEOUT);
      await rememberCaptureProofs(reply);
      return await cacheNeverCaptureHosts(reply);
    } catch { return false; }
  })().finally(() => { siteRulesRefresh = null; });
  return siteRulesRefresh;
}

function matchesNeverCaptureHost(item) {
  for (const address of [item?.referrer, item?.url, item?.finalUrl]) {
    try {
      const candidate = new URL(address);
      if (candidate.protocol === 'http:' || candidate.protocol === 'https:') {
        const host = normalizeRuleHost(candidate.hostname);
        if (host && siteNeverCaptureHosts.has(host)) return true;
      }
    } catch { /* Invalid sources have no site host to compare. */ }
  }
  return false;
}

async function captureSiteDecision(item, signedIn) {
  await siteRulesLoaded;
  // Unsigned capture checks live rules in the same native call that stages the transfer.
  // Refresh a cached block so removing a site rule takes effect without an extension restart.
  if (!signedIn && !matchesNeverCaptureHost(item)) return 'allowed';
  if (!(await refreshNeverCaptureHosts())) return 'policy-unavailable';
  return matchesNeverCaptureHost(item) ? 'site-rule' : 'allowed';
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

async function signInApproval() {
  const prefs = await settings();
  if (!prefs.useSignIn || !(await chrome.permissions.contains({ permissions: ['cookies'] }).catch(() => false)))
    return null;
  const { signInHosts = [] } = await chrome.storage.local.get('signInHosts').catch(() => ({}));
  return new Set(Array.isArray(signInHosts)
    ? signInHosts.filter(host => typeof host === 'string' && host.length <= 253).slice(0, 50).map(host => host.toLowerCase()) : []);
}

async function withSignIn(items, context = {}) {
  const safe = items.map(stripSecrets);
  const approvedHosts = await signInApproval();
  if (!approvedHosts) return { items: safe, authorized: false };
  const enriched = [];
  for (const item of safe) {
    let targetHost;
    let allowVideoSignIn = false;
    try {
      const target = new URL(item.url);
      if (target.protocol === 'https:' && !target.username && !target.password) {
        targetHost = target.hostname.toLowerCase();
        allowVideoSignIn = context.mediaPicker && context.userGesture && context.frame && supportsMediaPrefetch(target)
          && target.origin === new URL(item.referer).origin;
      }
    } catch { /* invalid URLs receive no browser credentials */ }
    if (!targetHost || (!allowVideoSignIn && !approvedHosts.has(targetHost))) {
      enriched.push(item);
      continue;
    }
    let cookie;
    try {
      const matches = await signInCookies(item.url, item.referer, context);
      // A flat Cookie header cannot carry the original path restriction to a media URL.
      const usable = context.mediaPicker ? matches.filter(c => c.path === '/') : matches;
      const header = usable.map(c => `${c.name}=${c.value}`).join('; ');
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
let sourceExpiryScheduled = false;
let sourceStorageReady = null;
let sourceBudgetWrites = Promise.resolve();

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
  for (const key of mediaLookups.keys())
    if (key.startsWith(String(tabId) + '|')) mediaLookups.delete(key);
}

function cachedMediaOptions(key) {
  const now = Date.now();
  for (const [cacheKey, entry] of mediaPrefetchCache)
    if (entry.expires <= now) mediaPrefetchCache.delete(cacheKey);
  return mediaPrefetchCache.get(key)?.reply;
}

function sourceLifetime(source) {
  return ['hls', 'dash'].includes(source?.kind) ? PLAYER_SOURCE_TTL : SOURCE_TTL;
}

function pruneSourceList(sources, now = Date.now()) {
  return (Array.isArray(sources) ? sources : [])
    .filter(source => Number.isFinite(source.seen) && source.seen >= now - sourceLifetime(source) && source.seen <= now + 1000)
    .slice(-MAX_SOURCES);
}

// How strongly one observed request suggests it carries the selected player's media. Strength only
// orders verification; identity still comes from the exact-source check of that one URL.
function evidenceStrength(source, changedAt) {
  let score = ['hls', 'dash'].includes(source.kind) ? 3 : isWholeDirectSource(source) ? 2 : 0;
  if (source.requestType === 'media') score += 1;          // requested by a media element itself
  if (source.typedManifest) score += 1;                     // identified by its Content-Type, not its name
  const distance = Math.abs(source.seen - changedAt);
  if (distance <= 3000) score += 2;
  else if (distance <= 15_000) score += 1;
  return score;
}

// Group only for probe diversity. These are not identity keys, and every actual probe
// uses the untouched observed URL. Keep content, quality and track parameters distinct.
function sourceProbeFamily(source) {
  const url = VesperPlayerSource.http(source.url);
  if (!url) return source.url;
  for (const key of [...url.searchParams.keys()])
    if (/^(?:token|sig|signature|expires|expiry|x-amz-(?:signature|date|expires|credential|security-token|algorithm|signedheaders))$/i.test(key))
      url.searchParams.delete(key);
  url.searchParams.sort();
  return source.kind + ':' + url.origin + url.pathname + url.search;
}

// Segment paths the player fetched after its current source began (path only, no query). A manifest whose
// directory contains those segments is that player's media; ads and neighbouring feed videos use other paths.
// Only a shared distinctive path segment (an asset id, not "/video/hls/") counts.
function fragmentAffinity(candidateUrl, fragments) {
  const url = VesperPlayerSource.http(candidateUrl);
  if (!url || !fragments.length) return 0;
  const directory = url.pathname.split('/').filter(Boolean).slice(0, -1);
  const distinctive = part => part.length >= 6 && /\d/.test(part) || /^[0-9a-f-]{8,}$/i.test(part);
  let best = 0;
  for (const fragment of fragments) {
    if (fragment.host !== url.host) continue;
    let shared = 0;
    while (shared < directory.length && shared < fragment.parts.length - 1 && directory[shared] === fragment.parts[shared]) shared++;
    if (shared && directory.slice(0, shared).some(distinctive)) best = Math.max(best, shared);
  }
  return best;
}

function sourceAffinity(source, fragments) {
  return Math.max(fragmentAffinity(source.url, fragments), ...(source.group || []).map(member => fragmentAffinity(member.url, fragments)));
}

// Automatic selection uses VesperMediaEvidence.associate; timing and
// fragment-directory hints may rank sources but cannot establish ownership.

function rankObservedSources(sources, changedAt, resourceWitness = new Set(), fragments = [], exactResources = new Set()) {
  const ranked = sources.map(source => ({ source, score: evidenceStrength(source, changedAt)
    + (exactResources.has(source.url) ? 24 : 0) + (resourceWitness.has(source.url) ? 8 : 0) + 4 * sourceAffinity(source, fragments),
    distance: Math.abs(source.seen - changedAt) }))
    .sort((a, b) => b.score - a.score || a.distance - b.distance)
    .map(entry => entry.source);
  const first = [], alternatives = [], families = new Set();
  for (const source of ranked) {
    const family = sourceProbeFamily(source);
    if (families.has(family)) alternatives.push(source);
    else { families.add(family); first.push(source); }
  }
  return [...first, ...alternatives];
}

// Checks each candidate separately with the exact-source command, in parallel, under one deadline.
// A candidate that fails is never replaced with another source.
function explainMediaProtocol(reply) {
  if (!reply?.ok && reply?.code === 'invalid'
      && /^unknown request(?: type)?\.?$/i.test(String(reply.message || '').trim()))
    return { ok: false, code: 'desktop-version-mismatch',
      message: 'The desktop helper and browser extension are different versions. Update VESPER and its browser helper together.' };
  return reply;
}

function xAssetKey(source) {
  const url = VesperPlayerSource.http(source.url);
  if (!url || url.origin !== 'https://video.twimg.com') return null;
  const parts = url.pathname.split('/').filter(Boolean);
  if (parts.length < 4 || !['ext_tw_video','amplify_video','tweet_video'].includes(parts[0])
      || !/^\d{5,24}$/.test(parts[1]) || parts.some(part => part.includes('%'))) return null;
  return 'x:' + parts[0] + ':' + parts[1];
}

function groupObservedAssets(sources, identity) {
  if (!identity?.startsWith('x:')) return sources;
  const result = [], groups = new Map();
  for (const source of sources) {
    const key = isRangedMediaCandidate(source) && !isWholeDirectSource(source) ? null : xAssetKey(source);
    if (!key) { result.push(source); continue; }
    const group = groups.get(key);
    if (!group) {
      const entry = { ...source, assetKey: key, group: [source] };
      groups.set(key, entry); result.push(entry);
    } else if (group.group.length < 5) group.group.push(source);
  }
  return result.map(source => source.group?.length === 1 ? source.group[0] : source);
}

function exactSourceRequest(source, auth, pageUrl, title, playbackFingerprints) {
  const group = source.group || [source];
  const ranged = group.length === 1 && isRangedMediaCandidate(group[0]) && !isWholeDirectSource(group[0]);
  return envelope(ranged ? 'media-range-options' : group.length > 1 ? 'media-group-options' : 'media-source-options', {
    ...auth, url: pageUrl, pageTitle: title,
    ...(playbackFingerprints?.length ? { playbackFingerprints } : {}),
    candidates: group.map(item => ({ url: item.url, kind: item.kind, mimeType: item.mime || undefined,
      ...(ranged ? { observedLength: item.bytes, observedETag: item.validator } : {}) }))
  });
}

async function verifyObservedSources(sources, { auth, pageUrl, title, playbackFingerprints, deadline = Date.now() + VERIFY_DEADLINE_MS }) {
  const groups = await Promise.all(sources.map(async source => {
    const reply = explainMediaProtocol(await nativeRequest(exactSourceRequest(source, auth, pageUrl, title, playbackFingerprints),
      Math.max(1000, deadline - Date.now())));
    // If a grouped manifest refers outside the known asset namespace, retain
    // the original independent verification route. Never try unchecked URLs.
    if (source.group?.length > 1 && !reply?.ok && reply.code === 'invalid' && Date.now() < deadline) {
      return Promise.all(source.group.map(async member => ({
        source: member,
        reply: explainMediaProtocol(await nativeRequest(exactSourceRequest(member, auth, pageUrl, title, playbackFingerprints),
          Math.max(1000, deadline - Date.now())))
      })));
    }
    return [{ source, reply }];
  }));
  return groups.flat();
}

const clockText = seconds => {
  if (!Number.isFinite(seconds) || seconds <= 0) return '';
  const total = Math.round(seconds), h = Math.floor(total / 3600), m = Math.floor(total % 3600 / 60), sec = total % 60;
  return h ? `${h}:${String(m).padStart(2, '0')}:${String(sec).padStart(2, '0')}` : `${m}:${String(sec).padStart(2, '0')}`;
};

// Only native-proven identical executable recipes are duplicates. Identical
// resolution, duration or protocol never prove that two videos are the same.
// Signed requests may carry different transient context; keep those separate.
function distinctVerifiedDeliveries(results, signed) {
  const usable = results.filter(result => result.reply?.ok && result.reply.data?.choices?.length);
  if (signed) return usable;
  const digest = value => typeof value === 'string' && /^[a-f0-9]{64}$/i.test(value) ? value.toLowerCase() : null;
  const recipes = data => {
    if (!Array.isArray(data.deliveryRecipeFingerprints) || !data.deliveryRecipeFingerprints.length
        || data.deliveryRecipeFingerprints.length > 64 || !data.deliveryRecipeFingerprints.every(digest)) return null;
    return new Set(data.deliveryRecipeFingerprints.map(digest));
  };
  const evidence = usable.map(result => ({key:digest(result.reply.data.deliveryFingerprint),
    tracks:digest(result.reply.data.deliveryTrackFingerprint), recipes:recipes(result.reply.data)}));
  return usable.filter((result,i) => {
    const own = evidence[i];
    return !evidence.some((other,j) => {
      if (i === j) return false;
      // Compare only opaque fingerprints of executable recipes, not resolution,
      // media length or a guessed CDN name. Preserve unknown and richer sources.
      if (own.recipes && other.recipes && own.tracks && own.tracks === other.tracks
          && (other.recipes.size > own.recipes.size || other.recipes.size === own.recipes.size && j < i)
          && [...own.recipes].every(key => other.recipes.has(key))) return true;
      return j < i && own.key && own.key === other.key
        && (!own.tracks || !other.tracks || own.tracks === other.tracks);
    });
  });
}

// The most specific reason none of the candidates could be used, so the picker can say what to do.
function bestRefusal(results) {
  const order = ['desktop-version-mismatch', 'protected', 'blocked', 'sign-in', 'site-refused', 'source-expired', 'rate-limited',
    'browser-context', 'unsupported', 'network', 'host-missing', 'app-unavailable', 'host-error', 'timeout'];
  const failures = results.map(result => result.reply).filter(reply => reply && !reply.ok && reply.code);
  for (const code of order) {
    const reply = failures.find(item => item.code === code);
    if (reply) return reply;
  }
  return null;
}

const watchedTabs = new Map();

// Script-driven players may fetch manifests or full media from extensionless URLs.
// Only recently active video tabs are watched; unrelated response types are discarded.
function watchTabForManifests(tabId) {
  if (!Number.isSafeInteger(tabId) || tabId < 0) return;
  const now = Date.now();
  for (const [id, watch] of watchedTabs) if (watch.expires <= now) unwatchTab(id);
  const existing = watchedTabs.get(tabId);
  if (existing) {
    clearTimeout(existing.timer);
    existing.expires = now + MANIFEST_WATCH_MS;
    existing.timer = setTimeout(() => {
      if (watchedTabs.get(tabId) === existing) unwatchTab(tabId);
    }, MANIFEST_WATCH_MS);
    return;
  }
  while (watchedTabs.size >= MAX_WATCHED_TABS) unwatchTab(watchedTabs.keys().next().value);
  const listener = details => {
    if (details.tabId !== tabId || details.method !== 'GET') return;
    const type = (details.responseHeaders?.find(h => h.name.toLowerCase() === 'content-type')?.value || '').trim();
    const manifest = /mpegurl|dash\+xml/i.test(type);
    if (manifest) {
      if (/\.(m3u8|mpd)(\?|$)/i.test(details.url)) return;
      return observeMediaResponse({ ...details, typedManifest: true });
    }
    if (!/^(?:audio|video)\//i.test(type) || DIRECT.test(details.url)) return;
    // Segment/range responses must never become full-file suggestions just because
    // their MIME says video. The normal observer still validates headers and kind.
    const url = VesperPlayerSource.http(details.url);
    if (!url || isPartialMediaUrl(url) || VesperIntelligence.sourceKind(url.href, type) === 'fragment') return;
    return observeMediaResponse(details);
  };
  try {
    chrome.webRequest.onResponseStarted.addListener(listener,
      { urls: ['http://*/*', 'https://*/*'], types: ['xmlhttprequest', 'other'], tabId }, ['responseHeaders']);
  } catch { return; }
  const watch = { listener, expires: now + MANIFEST_WATCH_MS };
  watch.timer = setTimeout(() => { if (watchedTabs.get(tabId) === watch) unwatchTab(tabId); }, MANIFEST_WATCH_MS);
  watchedTabs.set(tabId, watch);
}

function unwatchTab(tabId) {
  const watch = watchedTabs.get(tabId);
  if (!watch) return;
  watchedTabs.delete(tabId);
  clearTimeout(watch.timer);
  try { chrome.webRequest.onResponseStarted.removeListener(watch.listener); } catch { /* already gone */ }
}

async function loadObservedSources() {
  const stored = await chrome.storage.session.get(null);
  const now = Date.now();
  for (const [key, value] of Object.entries(stored)) {
    if (!/^tab:\d+$/.test(key)) continue;
    const tabId = Number(key.slice(4));
    if (!Number.isSafeInteger(tabId)) continue;
    const state = tabStates.get(tabId) || (value && typeof value === 'object' ? value : null);
    if (!state) continue;
    state.sources = pruneSourceList(state.sources, now);
    tabStates.set(tabId, state);
  }
  await enforceSourceBudget();
}

function ensureObservedSourcesLoaded() {
  if (!sourceStorageReady)
    sourceStorageReady = loadObservedSources().catch(error => {
      sourceStorageReady = null;
      throw error;
    });
  return sourceStorageReady;
}

function enforceSourceBudget() {
  const work = sourceBudgetWrites.catch(() => {}).then(async () => {
    const entries = [];
    for (const [tabId, state] of tabStates)
      for (const source of state.sources || []) entries.push({ tabId, source });
    if (entries.length <= MAX_TOTAL_SOURCES) return;

    entries.sort((a, b) => a.source.seen - b.source.seen);
    const excess = entries.length - MAX_TOTAL_SOURCES;
    const removed = new Map();
    for (const { tabId, source } of entries.slice(0, excess)) {
      if (!removed.has(tabId)) removed.set(tabId, new Set());
      removed.get(tabId).add(source);
    }

    for (const [tabId, sources] of removed) await inTabOrder(tabId, async () => {
      const state = await tabState(tabId, { skipSourceLoad: true });
      const count = state.sources.length;
      state.sources = state.sources.filter(source => !sources.has(source));
      if (state.sources.length !== count) await saveTabState(tabId, state, { force: true });
    });
  });
  sourceBudgetWrites = work.catch(() => {});
  return work;
}

async function scheduleSourceExpiry() {
  if (sourceExpiryScheduled) return;
  const alarm = await chrome.alarms.get(SOURCE_EXPIRY_ALARM);
  if (!alarm) await chrome.alarms.create(SOURCE_EXPIRY_ALARM, { delayInMinutes: SOURCE_TTL / 60_000 });
  sourceExpiryScheduled = true;
}

async function pruneObservedSources() {
  sourceExpiryScheduled = false;
  const stored = await chrome.storage.session.get(null);
  const now = Date.now();
  for (const key of Object.keys(stored)) {
    if (!key.startsWith('tab:') || !/^tab:\d+$/.test(key)) continue;
    const tabId = Number(key.slice(4));
    await inTabOrder(tabId, async () => {
      const state = (await chrome.storage.session.get(key))[key];
      if (!state || typeof state !== 'object') return;
      state.sources = pruneSourceList(state.sources, now);
      tabStates.set(tabId, state);
      await chrome.storage.session.set({ [key]: state });
    });
  }
  await enforceSourceBudget();
  let nextExpiry = Infinity;
  for (const state of tabStates.values())
    for (const source of state.sources || []) nextExpiry = Math.min(nextExpiry, source.seen + sourceLifetime(source));
  if (Number.isFinite(nextExpiry)) {
    await chrome.alarms.create(SOURCE_EXPIRY_ALARM, { when: Math.max(Date.now() + 30_000, nextExpiry) });
    sourceExpiryScheduled = true;
  }
}

function rememberMediaOptions(key, tab, reply) {
  // Source choices are single-use. A new picker request needs fresh tokens.
  if (!reply?.ok || !reply.data?.choices?.length) return;
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

function isPartialMediaUrl(url) {
  return SEGMENT.test(url.pathname) || [...url.searchParams.keys()]
    .some(key => /^(?:range|sq|segment|chunk|frag|bytestart|byteend)$/i.test(key));
}

// A partial response with a strong validator and complete extent is only a
// candidate for the separate native verification command, never a whole file.
function isRangedMediaCandidate(source) {
  const url = VesperPlayerSource.http(source.url);
  return source.kind === 'direct' && source.rangeResource === true && !source.conflictingValidator
    && Number.isSafeInteger(source.bytes) && source.bytes > 0 && source.bytes <= 17_179_869_184
    && /^"[^"\x00-\x20\x7f]{1,254}"$/.test(source.validator || '')
    && source.mime === 'video/mp4' && !!url && !isPartialMediaUrl(url);
}

function isWholeDirectSource(source) {
  if (!source.wholeRepresentation || source.kind !== 'direct') return false;
  const url = VesperPlayerSource.http(source.url);
  if (!url || isPartialMediaUrl(url)) return false;
  return /^(?:audio|video)\//i.test(source.mime || '') || DIRECT.test(url.href);
}

async function tabState(tabId, { skipSourceLoad = false } = {}) {
  if (!skipSourceLoad) await ensureObservedSourcesLoaded();
  const key = `tab:${tabId}`;
  const cached = tabStates.get(tabId);
  const state = cached || (await chrome.storage.session.get(key))[key]
    || { sources: [], unsupported: null, title: '', duration: null, poster: '' };
  const previousCount = Array.isArray(state.sources) ? state.sources.length : 0;
  state.sources = pruneSourceList(state.sources);
  if (state.sources.length !== previousCount) await chrome.storage.session.set({ [key]: state });
  tabStates.set(tabId, state);
  return state;
}

async function saveTabState(tabId, state, { force = false } = {}) {
  state.sources = pruneSourceList(state.sources);
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
  if (!Number.isSafeInteger(tabId) || tabId < 0 || !source.documentId || !VesperPlayerSource.http(source.url)
      || SEGMENT.test(source.url) || !Number.isFinite(source.seen ?? Date.now())) return;
  await ensureObservedSourcesLoaded();
  await inTabOrder(tabId, async () => {
    const state = await tabState(tabId);
    const seen = source.seen ?? Date.now();
    const match = s => s.documentId === source.documentId && s.frameId === source.frameId && s.url === source.url;
    const previous = state.sources.find(match);
    state.sources = state.sources.filter(s => !match(s));
    // The same document can reuse a URL for different programs. Don't use an
    // older strong validator if the latest observation omitted or changed it.
    const conflictingValidator = Boolean(previous && Math.abs(previous.seen - seen) <= SOURCE_TTL
      && (previous.conflictingValidator
        || previous.validator && source.validator && previous.validator !== source.validator
        || previous.bytes && source.bytes && previous.bytes !== source.bytes
        || previous.mime && source.mime && previous.mime !== source.mime));
    // An older callback can arrive after a newer response (including across
    // worker wakeups). Never let it reinstate an old ETag, range or CORS policy.
    const latest = previous && previous.seen > seen ? previous : { ...previous, ...source, seen };
    state.sources.push({ ...latest, conflictingValidator });
    state.sources.sort((a, b) => a.seen - b.seen);
    await saveTabState(tabId, state);
    await scheduleSourceExpiry();
  });
  await enforceSourceBudget();
}

function sourceLabel(source) {
  const media = source.kind === 'direct' && source.mime.startsWith('audio/') ? 'Audio' : 'Video';
  const format = source.kind === 'direct' ? typeName(source.mime) : source.kind.toUpperCase();
  const size = Number.isSafeInteger(source.bytes) && source.bytes >= 524288 ? mb(source.bytes) : '';
  return {
    token: source.token,
    label: source.kind === 'direct' ? `${format} ${media.toLowerCase()}` : `${format} stream`,
    detail: size ? `${format} · ${size}` : format
  };
}

function verifiedSourceLabel(source, reply) {
  const data = reply.data;
  const facts = VesperMediaEvidence.mediaFacts(reply);
  const format = source.group?.length > 1 ? 'Video' : source.kind === 'direct' ? typeName(source.mime) : source.kind.toUpperCase();
  const count = new Set([...data.choices, ...(data.more || [])].map(choice => choice.index)).size;
  const duration = facts.live === true ? '' : clockText(facts.durationSeconds);
  const best = data.choices[0];
  const tracks = VesperMediaEvidence.trackLabel(facts);
  const media = facts.hasVideo === false && facts.hasAudio === true ? 'Audio' : 'Video';
  const countText = count > 1 ? `${count} formats` : '1 format';
  return {
    token: source.token,
    label: [duration, facts.live === true ? 'Live' : '', facts.height ? `${facts.height}p` : '',
      tracks, format, countText].filter(Boolean).join(' · '),
    detail: best?.detail || best?.label || '',
    displayLabel: [media, duration || (facts.live === true ? 'Live' : ''), facts.height ? `${facts.height}p` : ''].filter(Boolean).join(' · '),
    displayDetail: [tracks, countText, best?.container,
      Number.isSafeInteger(best?.sizeBytes) && best.sizeBytes > 0
        ? (best.sizeEstimated ? '≈ ' : '') + mb(best.sizeBytes) : ''].filter(Boolean).join(' · '),
    durationSeconds: facts.durationSeconds
  };
}

function rememberSourceSelections(sources, context) {
  sources = sources.slice(0, 32);
  const now = Date.now();
  for (const [token, selection] of sourceSelections)
    if (selection.expires <= now) sourceSelections.delete(token);
  while (sourceSelections.size + sources.length > 32)
    sourceSelections.delete(sourceSelections.keys().next().value);
  return sources.map(source => {
    const token = crypto.randomUUID();
    sourceSelections.set(token, { ...context, url: source.url, kind: source.kind, mime: source.mime,
      seen: source.seen, validator: source.validator, bytes: source.bytes,
      rangeResource: source.rangeResource, wholeRepresentation: source.wholeRepresentation, group: source.group,
      expires: now + 60_000, verified: context.signed ? undefined : source.verified });
    return source.verified
      ? verifiedSourceLabel({ ...source, token }, source.verified)
      : sourceLabel({ ...source, token });
  });
}

// Narrow listeners: only media requests, plus playlist-looking URLs. The worker sleeps otherwise.
const observeMediaResponse = (details) => {
  if (details.incognito || details.method !== 'GET' || ![200, 206].includes(details.statusCode)) return;
  const header = (name) => details.responseHeaders?.find((h) => h.name.toLowerCase() === name)?.value;
  const type = header('content-type') || '';
  const range = header('content-range');
  const rangeMatch = /^bytes\s+(\d+)-(\d+)\/(\d+)$/i.exec(range || '');
  const rangeStart = rangeMatch ? Number(rangeMatch[1]) : null;
  const rangeEnd = rangeMatch ? Number(rangeMatch[2]) : null;
  const total = rangeMatch ? Number(rangeMatch[3]) : null;
  const contentLength = header('content-length') === undefined ? null : Number(header('content-length'));
  const bytes = Number.isSafeInteger(total) && total > 0 ? total
    : Number.isSafeInteger(contentLength) && contentLength > 0 ? contentLength : null;
  const fullRange = !!(details.statusCode === 206 && rangeMatch
    && Number.isSafeInteger(rangeStart) && Number.isSafeInteger(rangeEnd) && Number.isSafeInteger(total)
    && rangeStart === 0 && rangeEnd + 1 === total && total > 0
    && (contentLength === null || Number.isSafeInteger(contentLength) && contentLength === total));
  const isPlaylist = /\.(m3u8|mpd)(\?|$)/i.test(details.url) || /mpegurl|dash\+xml/i.test(type);
  const rawTag = header('etag')?.trim();
  const encoding = header('content-encoding')?.trim().toLowerCase();
  const validRange = details.statusCode === 206 && rangeMatch
    && Number.isSafeInteger(rangeStart) && Number.isSafeInteger(rangeEnd)
    && Number.isSafeInteger(total) && rangeStart >= 0 && rangeEnd >= rangeStart && rangeEnd < total
    && (contentLength === null || contentLength === rangeEnd - rangeStart + 1);
  const validator = !isPlaylist && (!encoding || encoding === 'identity')
    && (details.statusCode === 200 && !range || validRange)
    && rawTag && /^"[^"\x00-\x20\x7f]{1,254}"$/.test(rawTag) ? rawTag : null;
  if (!isPlaylist && !(details.type === 'media' || DIRECT.test(details.url) || /^(?:audio|video)\//i.test(type))) return;
  return addSource(details.tabId, {
    url: details.url,
    kind: VesperPlayerSource.kind(details.url, type), mime: type.split(';')[0].trim().toLowerCase(), bytes, validator,
    // Store only the CORS policy, not request cookies or response secrets.
    corsOrigin: (() => {
      const value = header('access-control-allow-origin')?.trim();
      if (value === '*') return value;
      try { return new URL(value).origin === value ? value : null; } catch { return null; }
    })(),
    corsCredentials: header('access-control-allow-credentials')?.trim() === 'true',
    corsExposeEtag: (header('access-control-expose-headers') || '').split(',').some(name => name.trim().toLowerCase() === 'etag'),
    corsExposeWildcard: (header('access-control-expose-headers') || '').split(',').some(name => name.trim() === '*'),
    // This means headers describe the whole representation; VESPER still fetches and validates the URL itself.
    wholeRepresentation: !isPlaylist && (details.statusCode === 200 && !range || fullRange),
    rangeResource: !isPlaylist && !!validRange && !!validator,
    unexpectedRange: details.statusCode === 200 && !!range,
    requestType: details.type, ...(details.typedManifest ? { typedManifest: true } : {}),
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
      unwatchTab(tabId);
      clearMediaPrefetch(tabId);
      await chrome.storage.session.remove(`tab:${tabId}`);
      await clearBrowserChoicesForTab(tabId);
      await chrome.action.setBadgeText({ tabId, text: '' });
    }).catch(() => {});
  }
});
chrome.tabs.onRemoved.addListener(tabId => inTabOrder(tabId, async () => {
  forgetTab(tabId);
  unwatchTab(tabId);
  clearMediaPrefetch(tabId);
  await chrome.storage.session.remove(`tab:${tabId}`);
  await clearBrowserChoicesForTab(tabId);
}).catch(() => {}));

async function mediaAuthorization(pageUrl, sender, { userGesture = false } = {}) {
  if (!userGesture) return {};
  const frame = Number.isInteger(sender?.tab?.id) ? {
    tabId: sender.tab.id, frameId: sender.frameId ?? 0,
    ...(sender.documentId ? { documentId: sender.documentId } : {})
  } : undefined;
  const enriched = await withSignIn([{ url: pageUrl, referer: sender?.url || pageUrl,
    userAgent: navigator.userAgent }], { frame, userGesture, mediaPicker: true });
  return enriched.authorized ? { authorized: true, mediaAuth: enriched.items[0] } : {};
}

function observedBrowserBodyProof(state, message, sender, useSignIn = false) {
  const page = mediaPage(message, sender);
  const url = VesperPlayerSource.http(message.playerUrl);
  if (!page || !url || sender.tab?.incognito || !bodyScope(sender)
      || VesperPlayerSource.identity(message.playerPageUrl || message.pageUrl) !== VesperPlayerSource.identity(page.href)
      || !Number.isInteger(message.generation) || message.generation < 1
      || !Number.isFinite(message.mediaChangedAt) || message.mediaChangedAt <= 0
      || !VesperBrowserBody.eligibleHttpMedia(url.href)
      || isPartialMediaUrl(url)) return null;
  const now = Date.now();
  const observed = state.sources.find(source => source.url === url.href
    && source.documentId === sender.documentId && (source.frameId ?? 0) === (sender.frameId ?? 0)
    && source.kind === 'direct' && !source.conflictingValidator
    && (source.requestType === 'media' || ['xmlhttprequest','other'].includes(source.requestType) && source.wholeRepresentation)
    && source.seen >= Math.max(now - SOURCE_TTL, message.mediaChangedAt - 15000) && source.seen <= now + 1000);
  if (!observed) return null;
  const proof = { url: url.href, etag: observed.validator, length: observed.bytes, mime: observed.mime };
  const origin = new URL(sender.url).origin;
  if (url.origin === origin) proof.credentials = useSignIn ? 'same-origin' : 'omit';
  if (url.origin !== origin) {
    // Only an actual CORS-readable media representation with an exposed
    // validator can be copied from a different origin. The server must
    // permit the subsequent If-Match preflight and expose the new ETag too.
    const credentialed = message.playerCrossOrigin === 'use-credentials';
    if ((!observed.corsExposeEtag && !(observed.corsExposeWildcard && !credentialed))
        || ![origin, '*'].includes(observed.corsOrigin)) return null;
    if (credentialed && (observed.corsOrigin !== origin || !observed.corsCredentials)) return null;
    proof.corsOrigin = origin;
    proof.credentials = credentialed ? 'include' : 'omit';
  }
  return VesperBrowserBody.validMediaProof(proof, url.href, origin) ? proof : null;
}

async function browserBodyMediaProof(message, sender) {
  const prefs = await settings();
  if (await extensionPaused() || prefs.route === 'chrome')
    return { ok: false, code: 'paused' };
  if (!Number.isSafeInteger(sender.tab?.id)) return { ok: false, code: 'missing' };
  const proof = observedBrowserBodyProof(await tabState(sender.tab.id), message, sender, prefs.useSignIn === true);
  if (proof?.credentials === 'include' && prefs.useSignIn !== true)
    return { ok: false, code: 'sign-in', message: 'Browser sign-in is disabled.' };
  if (!proof) return { ok: false, code: 'missing', message: 'No current verified browser file is available.' };
  // Fetch only after current site rules are confirmed. Nothing from the browser's
  // cookie store is sent to the native engine.
  if (!(await refreshNeverCaptureHosts()) || matchesNeverCaptureHost({url:proof.url,referrer:sender.url}))
    return { ok:false,code:'blocked',message:'VESPER could not confirm that this site permits native acquisition.' };
  return { ok:true, data:proof };
}

function playerActiveForWatch(message) {
  return message.playerPlaying === true || message.playerHasMedia === true;
}

async function playerOptions(message, sender) {
  const page = mediaPage(message, sender);
  if (!page) return { ok: false, code: 'invalid', message: 'This page cannot be inspected.' };
  const identity = VesperPlayerSource.identity(page.href);
  const youTube = isYouTubePage(page);
  const binding = VesperPlayerSource.identity(message.playerPageUrl) === identity;
  const player = binding ? VesperMediaEvidence.playerFacts(message.playerFacts) : {};
  const source = binding && VesperPlayerSource.http(message.playerUrl);
  const manifest = binding && VesperPlayerSource.http(message.manifestUrl);
  const mutedLoop = message.mutedLoop === true && !!source && (sender.frameId ?? 0) === 0;
  const { route } = await settings();
  const frame = sender.frameId ?? 0;
  const tab = sender.tab?.id;
  const prefetch = message.prefetch === true;
  const auth = route !== 'chrome' && !prefetch
    ? await mediaAuthorization(page.href, sender, { userGesture: true }) : {};
  const playbackFingerprints = binding && !prefetch && Number.isInteger(message.generation) && message.generation > 0
    && sender.documentId && Array.isArray(message.playerResourceFingerprints)
    ? [...new Set(message.playerResourceFingerprints.slice(0, 12)
      .filter(value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value)))].sort() : [];
  const lookupKey = [tab, frame, sender.documentId, identity, page.href, message.generation, source?.href, manifest?.href,
    route, mutedLoop, message.mediaChangedAt, playbackFingerprints.join(',')].join('|');
  if (message.refresh === true && message.prefetch !== true) mediaPrefetchCache.delete(lookupKey);
  const cached = !auth.authorized && cachedMediaOptions(lookupKey);
  if (cached) return cached;
  // A click can use sign-in even while a background lookup is in flight.
  const pendingKey = [lookupKey, prefetch ? 'prefetch' : auth.authorized ? 'signed' : '', JSON.stringify(player)].join('|');
  if (mediaLookups.has(pendingKey)) return mediaLookups.get(pendingKey);
  const task = (async () => {
    if (prefetch) {
      const { prefetchFormats = false } = await chrome.storage.local.get('prefetchFormats');
      if (!prefetchFormats) return { ok: true, data: { prefetchSkipped: true } };
    }
    const state = await tabState(tab);
    const now = Date.now();
    const changedAt = Number.isFinite(message.mediaChangedAt) ? message.mediaChangedAt : 0;
    const directCutoff = changedAt > 0 ? changedAt - 2_000 : 0;
    const currentEvidence = source => sender.documentId && source.documentId === sender.documentId
      && (source.frameId ?? 0) === frame && Number.isFinite(source.seen)
      && source.seen >= now - sourceLifetime(source) && source.seen <= now + 1000;
    const observed = url => state.sources.find(s => currentEvidence(s) && s.seen >= directCutoff && s.url === url.href);
    const matching = source ? observed(source) : null;
    const candidate = url => {
      const evidence = observed(url);
      return { url: url.href, kind: evidence?.kind || VesperPlayerSource.kind(url.href), mimeType: evidence?.mime || undefined };
    };
    const manifestIsCurrent = evidence => Number.isInteger(message.generation) && message.generation > 0 && changedAt > 0
      && evidence.seen >= changedAt - 15_000 && evidence.seen <= now + 1000;
    const matchedManifest = manifest && state.sources.find(source => currentEvidence(source)
      && source.url === manifest.href && manifestIsCurrent(source));
    const resourceWitness = new Set((Array.isArray(message.resourceUrls) ? message.resourceUrls : [])
      .slice(0, 12).map(url => VesperIntelligence.http(url)?.href).filter(Boolean));
    const fragments = binding ? (Array.isArray(message.fragmentUrls) ? message.fragmentUrls : []).slice(0, 8)
      .map(value => VesperPlayerSource.http(value)).filter(url => url && !url.search)
      .map(url => ({ host: url.host, origin: url.origin, parts: url.pathname.split('/').filter(Boolean) })) : [];
    const playerFragments = binding && message.generation > 0 ? (Array.isArray(message.playerFragmentUrls) ? message.playerFragmentUrls : [])
      .slice(0, 12).map(value => VesperPlayerSource.http(value)).filter(url => url && !url.search)
      .map(url => ({ host: url.host, origin: url.origin, parts: url.pathname.split('/').filter(Boolean) })) : [];
    const rankingFragments = playerFragments.length ? playerFragments : fragments;
    const possibleSources = [];
    const playerActiveOrLoaded = playerActiveForWatch(message);
    if (!matchedManifest && source === null && playerActiveOrLoaded && Number.isInteger(message.generation) && message.generation > 0
        && changedAt > 0 && sender.documentId && Number.isInteger(tab)) {
      for (const evidence of state.sources) {
        const sourceCutoff = Math.max(now - sourceLifetime(evidence), changedAt - 15_000);
        const recent = evidence.seen >= sourceCutoff;
        const isManifest = ['hls', 'dash'].includes(evidence.kind);
        if (currentEvidence(evidence) && recent
            && (isManifest || evidence.seen >= directCutoff
              && (isWholeDirectSource(evidence) || isRangedMediaCandidate(evidence)))
            && !possibleSources.some(item => item.url === evidence.url))
          possibleSources.push(evidence);
      }
    }
    if (prefetch && possibleSources.length > 0 && !matching && !matchedManifest)
      return { ok: true, data: { prefetchSkipped: true } };   // opening the picker verifies; idle pages stay cheap
    if (prefetch) {
      if (!supportsMediaPrefetch(page) && !matching && !matchedManifest && possibleSources.length === 0)
        return { ok: true, data: { prefetchSkipped: true } };
      const lastPrefetch = mediaPrefetchAt.get(tab);
      if (lastPrefetch !== undefined && now - lastPrefetch < MEDIA_PREFETCH_COOLDOWN)
        return { ok: true, data: { prefetchSkipped: true } };
      mediaPrefetchAt.set(tab, now);
    }
    // A direct current player also benefits from the verified range route.
    // Do not downgrade a known partial representation into an unchecked URL.
    if (matching && isRangedMediaCandidate(matching) && !isWholeDirectSource(matching)
        && !possibleSources.some(item => item.url === matching.url)) {
      if (prefetch) return { ok: true, data: { prefetchSkipped: true } };
      possibleSources.unshift(matching);
    }
    const usableSource = source && !isPartialMediaUrl(source) && !matching?.unexpectedRange
      && !(matching && isRangedMediaCandidate(matching) && !isWholeDirectSource(matching)) ? source : null;
    const candidates = [usableSource, matchedManifest && matchedManifest.kind !== 'direct' ? manifest : null]
      .filter(Boolean).map(url => {
        const hint = candidate(url);
        if (mutedLoop && url === source && hint.kind === 'direct') hint.mutedLoop = true;
        return hint;
      });
    const wholeRepresentation = usableSource && matching && isWholeDirectSource(matching)
      && /^(?:audio|video)\//.test(matching.mime);
    const title = binding || !youTube ? message.title : undefined;

    if (route !== 'chrome') {
      // A single-video page can give the resolver a better identity than nearby network traffic.
      const singleVideo = binding && identity && (identity.startsWith('tiktok:')
        || identity.startsWith('dailymotion:')
        || identity.startsWith('instagram:') && /^\/reels?\//.test(page.pathname));
      const pageLookup = () => nativeRequest(envelope('media-options', {
        ...auth,
        url: page.href, pageTitle: title, candidates: !youTube || candidates.length ? candidates : undefined,
        ...(playbackFingerprints.length ? { playbackFingerprints } : {})
      }), 60000);
      // Both routes can answer for this player. Where the page lookup keeps being refused, the player's own
      // traffic is asked first and the page lookup becomes the fallback, instead of failing slowly on every click.
      const lookupStrategy = auth.authorized ? 'page-lookup-signed' : 'page-lookup';
      const bothRoutes = !!singleVideo && possibleSources.length > 0 && !prefetch;
      const trafficFirst = bothRoutes && !(await VesperExperience.promising(page.hostname, lookupStrategy));
      const learnLookup = reply => {
        const outcome = reply.ok && reply.data?.choices?.length ? 'verified'
          : LOOKUP_REFUSALS.includes(reply.code) ? 'refused' : null;
        return bothRoutes && outcome ? VesperExperience.note(page.hostname, lookupStrategy, outcome).catch(() => {}) : undefined;
      };
      let trafficWork;
      const startTraffic = () => {
        if (!trafficWork) {
          trafficWork = (async () => {
            const pickerTitle = binding ? message.title : undefined;
            const context = { identity, tab, frame, documentId: sender.documentId, signed: auth.authorized === true,
              generation: message.generation, playerUrl: message.playerUrl, title: pickerTitle };
            const fingerprints = new Set(playbackFingerprints);
            const exactResources = new Set();
            if (fingerprints.size && !prefetch) {
              await Promise.all(possibleSources.slice(0, MAX_SOURCES).map(async item => {
                try {
                  const url = VesperPlayerSource.http(item.url);
                  if (url && fingerprints.has(await sourceIdentity(url.href))) exactResources.add(item.url);
                } catch { /* Missing crypto is a ranking miss, not a download failure. */ }
              }));
            }
            // Resource witnesses must influence the verification budget, not only the display order.
            const ranked = groupObservedAssets(rankObservedSources(possibleSources, changedAt, resourceWitness, rankingFragments, exactResources)
              .slice(0, VERIFY_LIMIT + 4), identity);
            const options = { auth, pageUrl: page.href, title: pickerTitle, playbackFingerprints: [...fingerprints],
              deadline: Date.now() + VERIFY_DEADLINE_MS };
            const checked = await verifyObservedSources(ranked.slice(0, VERIFY_LIMIT), options);
            return { pickerTitle, context, fingerprints, exactResources, ranked, options, checked };
          })();
          // A quick successful page answer may win before a speculative check finishes.
          // The speculative route only asks for formats; it never dispatches a download.
          void trafficWork.catch(() => {});
        }
        return trafficWork;
      };
      if ((possibleSources.length === 0 || singleVideo) && !trafficFirst) {
        // Preserve the page's policy/identity verdict. If it is slow, overlap a
        // bounded exact-source check so a refusal does not cost two serial waits.
        const hedge = bothRoutes ? setTimeout(startTraffic, LOOKUP_HEDGE_MS) : null;
        let reply;
        try { reply = await pageLookup(); }
        finally { if (hedge !== null) clearTimeout(hedge); }
        await learnLookup(reply);
        if (possibleSources.length === 0) {
          if (reply.ok) return reply;
          // An exact observed player resource can be checked without the page
          // adapter. Do not retry policy/protection or uncertain identity failures.
          const exact = wholeRepresentation ? matching : matchedManifest;
          if (!prefetch && exact && Number.isInteger(message.generation) && message.generation > 0 && ['browser-context', 'sign-in', 'site-refused', 'no-media', 'unavailable', 'source-unavailable'].includes(reply.code)) {
            const checked = explainMediaProtocol(await nativeRequest(exactSourceRequest(exact, auth, page.href, title, playbackFingerprints), 20000));
            if (checked.ok && checked.data?.choices?.length) return checked;
            if (['protected','blocked','rate-limited','desktop-version-mismatch'].includes(checked.code)) return checked;
          }
          if (!wholeRepresentation || !['host-missing', 'host-error', 'app-unavailable', 'network', 'sign-in', 'no-media', 'unavailable', 'timeout'].includes(reply.code)) return reply;
        } else if (reply.ok || !LOOKUP_REFUSALS.includes(reply.code)) {
          return reply;
        }
      }
      if (possibleSources.length > 0) {
        const { pickerTitle, context, fingerprints, exactResources, ranked, options, checked } = await startTraffic();
        const usableReply = result => result.reply?.ok && result.reply.data?.choices?.length;
        // A global failure is not a reason to ask the user to retry every URL.
        const incompatible = checked.find(result => result.reply?.code === 'desktop-version-mismatch');
        if (incompatible && !checked.some(usableReply)) return incompatible.reply;
        // An unrelated usable stream must not exhaust the search when real playback evidence exists.
        // One additional bounded wave is allowed if no verified source matches it.
        const boundMatch = result => usableReply(result) && (exactResources.has(result.source.url)
          || result.source.group?.some(member => exactResources.has(member.url))
          || Number.isInteger(result.reply.data?.playbackMatches) && result.reply.data.playbackMatches > 0
            && result.reply.data.playbackMatches <= fingerprints.size);
        const needsMore = !checked.some(usableReply) || fingerprints.size > 0 && !checked.some(boundMatch);
        if (needsMore && ranked.length > VERIFY_LIMIT && options.deadline - Date.now() >= 1000
            && !checked.some(result => ['protected', 'blocked', 'rate-limited', 'host-missing',
              'app-unavailable', 'host-error'].includes(result.reply?.code))) {
          checked.push(...await verifyObservedSources(ranked.slice(VERIFY_LIMIT, VERIFY_LIMIT + 4), options));
        }
        const association = VesperMediaEvidence.associate({
          sources: checked, player, currentUrl: source?.href || '',
          resources: [...resourceWitness], exactResources: [...exactResources], fragments: rankingFragments.map(fragment =>
            fragment.origin + '/' + fragment.parts.join('/'))
        });
        const ordered = association.nodes.map(node => node.entry);
        const usable = distinctVerifiedDeliveries(ordered, auth.authorized === true);
        if (association.selected && usable.includes(association.selected)) return association.selected.reply;
        // Timing found these, so the user confirms which one is this player, even when only one
        // verified. Each row shows checked facts, and choosing it reuses the verified reply at once.
        // A matching duration or label does not prove two URLs are the same source.
        if (usable.length > 0) {
          const sourceChoices = rememberSourceSelections(usable.map(result =>
            ({ ...result.source, verified: result.reply })), context);
          const labels = new Map();
          for (const choice of sourceChoices) {
            const key = choice.displayLabel;
            labels.set(key, (labels.get(key) || 0) + 1);
          }
          const ordinal = new Map();
          for (const choice of sourceChoices) {
            const key = choice.displayLabel;
            if (labels.get(key) > 1) {
              const index = (ordinal.get(key) || 0) + 1;
              ordinal.set(key, index);
              choice.displayLabel = key + ' · Source ' + index;
            }
          }
          return { ok: true, data: { title: pickerTitle, sourceChoices, verifiedSources: true } };
        }
        const refusal = bestRefusal(ordered);
        // Learning changes route order, not the protection or security policy.
        if (trafficFirst && !['protected', 'blocked', 'rate-limited', 'desktop-version-mismatch'].includes(refusal?.code)) {
          const reply = await pageLookup();
          await learnLookup(reply);
          if (reply.ok || ['protected', 'blocked', 'rate-limited', 'desktop-version-mismatch'].includes(reply.code)) return reply;
        }
        if (refusal) return refusal;
        return { ok: false, code: 'no-reusable-source',
          message: 'This player’s video isn’t available as a file or stream VESPER can reuse.' };
      }
    }
    if (!wholeRepresentation) return { ok: false, code: 'chrome-format', message: 'Chrome hasn’t exposed a full-file URL for this player.' };
    await browserChoicesReady;
    const token = crypto.randomUUID();
    const container = typeName(matching.mime);
    for (const [key, choice] of browserChoices)
      if (choice.expires <= Date.now()) browserChoices.delete(key);
    while (browserChoices.size >= 32) browserChoices.delete(browserChoices.keys().next().value);
    browserChoices.set(token, { url: source.href, generation: message.generation ?? 0,
      identity, tab, frame, documentId: sender.documentId, expires: Date.now() + 120_000 });
    await saveBrowserChoices();
    return { ok: true, data: { browserChoice: token, title, choices: [{ index: 0,
      label: matching.mime.startsWith('audio/') ? 'Original audio' : 'Original file',
      detail: 'Saves with Chrome', container }] } };
  })();
  // Share the fully decorated answer (including browser-body availability), not
  // the raw native reply. Otherwise concurrent opens can render different formats.
  const result = (async () => {
    const reply = explainMediaProtocol(await task);
    // The failed native resolver or explicit Chrome fallback may be accompanied
    // by a verified same-origin conditional GET option. Never offer it in Chrome-only mode.
    if (!prefetch && route !== 'chrome' && (reply.data?.browserChoice
        || (!reply.ok && ['no-media','no-reusable-source','source-unavailable','browser-context','unavailable','site-refused','source-expired','sign-in','unsupported'].includes(reply.code)))) {
      const bodyPrefs = await settings();
      const proof = observedBrowserBodyProof(await tabState(tab), message, sender, bodyPrefs.useSignIn === true);
      if (proof && (proof.credentials !== 'include' || bodyPrefs.useSignIn === true)) {
        return { ...reply, data: { ...reply.data, browserBodyAvailable: true,
          browserBodyFormat: { mime: proof.mime, sizeBytes: proof.length, container: typeName(proof.mime) } } };
      }
    }
    if (!auth.authorized && mediaLookups.get(pendingKey) === result) rememberMediaOptions(lookupKey, tab, reply);
    return reply;
  })();
  mediaLookups.set(pendingKey, result);
  try { return await result; }
  finally { if (mediaLookups.get(pendingKey) === result) mediaLookups.delete(pendingKey); }
}

async function selectBrowserFile(message, sender) {
  await browserChoicesReady;
  const choice = browserChoices.get(message.browserChoice);
  if (!choice || choice.expires <= Date.now() || choice.tab !== sender.tab?.id || choice.frame !== (sender.frameId ?? 0)
    || !sender.documentId || choice.documentId !== sender.documentId
    || choice.generation !== (message.generation ?? 0) || choice.url !== VesperPlayerSource.http(message.playerUrl)?.href
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
  const now = Date.now();
  const current = selected => state.sources.some(source => source.url === selected.url && source.documentId === selection.documentId
    && (source.frameId ?? 0) === selection.frame && source.seen >= selected.seen && source.kind === selected.kind
    && source.seen >= now - (selected.kind === 'direct' ? 120_000 : sourceLifetime(selected)) && source.seen <= now + 1000
    && (source.kind !== 'direct' || (isWholeDirectSource(source) || isRangedMediaCandidate(source)) && !source.conflictingValidator
      && source.validator === selected.validator && source.bytes === selected.bytes && source.mime === selected.mime));
  if (!(selection.group || [selection]).every(current))
    return { ok: false, code: 'expired-selection', message: 'Refresh the formats for the current video.' };
  const { route } = await settings();
  if (route === 'chrome')
    return { ok: false, code: 'chrome-format', message: 'Chrome hasn’t exposed a full-file URL for this player.' };
  const auth = await mediaAuthorization(page.href, sender, { userGesture: true });
  // Signed choices get a fresh check so changing sign-in permissions cannot reuse the earlier session.
  if (!auth.authorized && selection.verified?.ok) return selection.verified;
  return explainMediaProtocol(await nativeRequest(exactSourceRequest(selection, auth, page.href, selection.title), 60000));
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

const mb = n => n < 1024 ? `${n} B` : n < 104858 ? `${(n / 1024).toFixed(1)} KB`
  : n >= 1073741824 ? `${(n / 1073741824).toFixed(2)} GB` : `${(n / 1048576).toFixed(n >= 104857600 ? 0 : 1)} MB`;
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
// They get only what the in-page picker needs. Link submissions and settings stay with extension pages.
const EXTENSION_PAGES = `chrome-extension://${chrome.runtime.id}/`;
const CONTENT_COMMANDS = new Set(['smart-submit', 'media-element', 'media-options', 'media-select', 'media-source-select', 'browser-media-select', 'native-picker',
  'batch-arm', 'batch-status', 'batch-clear', 'batch-disarm', 'batch-journal-read', 'batch-journal-write', 'batch-journal-ack', 'browser-body-status', 'browser-body-media-proof']);
const PAUSE_BLOCKED_COMMANDS = new Set(['smart-submit', 'batch-arm', 'media-options', 'media-select', 'media-source-select',
  'browser-media-select', 'native-picker', 'retry', 'submit']);
const PAGE_COMMANDS = new Set([...CONTENT_COMMANDS, 'tab-state', 'hello',
  'native-picker', 'describe', 'retry', 'submit', 'managed-downloads', 'continue-managed']);

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
    if (await extensionPaused() && PAUSE_BLOCKED_COMMANDS.has(message.type))
      return { ok: false, code: 'paused', message: 'VESPER is paused in Chrome.' };
    if (message.type === 'browser-body-status') return bodyReceiptStatus(message, sender);
    if (message.type === 'browser-body-media-proof') return browserBodyMediaProof(message, sender);
    if (message.type.startsWith('batch-journal-') || message.type === 'batch-arm' || message.type === 'batch-status'
        || message.type === 'batch-clear' || message.type === 'batch-disarm')
      return VesperBatchEvents.handle(message.type, message, sender);
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
          const sha256 = typeof item?.sha256 === 'string' && /^[a-f0-9]{64}$/i.test(item.sha256) ? item.sha256.toUpperCase() : undefined;
          const reviewLabel = typeof item.reviewLabel === 'string' && item.reviewLabel.length <= 200 ? item.reviewLabel : undefined;
          const reviewType = typeof item.reviewType === 'string' && /^[a-z0-9 ]{1,32}$/i.test(item.reviewType) ? item.reviewType : undefined;
          const seconds = item.reviewDurationSeconds;
          const reviewDurationSeconds = Number.isFinite(seconds) && seconds > 0 && seconds <= 31_536_000 ? seconds : undefined;
          safe.push({ url: url.href, referer: page.href, userAgent: navigator.userAgent, ...(sha256 ? { sha256 } : {}),
            reviewLabel, reviewType, reviewDurationSeconds });
        }
        if (!safe.length) return { ok: false, code: 'invalid', message: 'No reusable file addresses were selected.' };
        // Untrusted content can propose links, never skip the native confirmation or choose a disk path.
        const signed = await withSignIn(safe, { frame: { tabId: sender.tab.id, frameId: sender.frameId ?? 0 }, userGesture: true });
        return submit(signed.items, 'ask', { authorized: signed.authorized, review: true });
      }
      case 'media-element': {
        // Renew the bounded media response observer for a tab with a video.
        if (message.watching === true && !(await extensionPaused())) watchTabForManifests(sender.tab?.id);
        return { ok: true };
      }
      case 'tab-state': {
        const state = await tabState(message.tabId ?? sender.tab?.id);
        const pending = await readPending();
        return { ok: true, state, pending: Object.entries(pending).map(([id, p]) => ({ id, at: p.at, count: p.request.items.length })) };
      }
      case 'hello': {
        const reply = await nativeRequest(envelope('hello'), 20000);
        await cacheNeverCaptureHosts(reply);
        return reply;
      }
      case 'managed-downloads': return { ok: true, files: await listManagedCandidates() };
      case 'continue-managed': return continueManagedDownload(message.id, message.startTime);
      case 'media-options': return playerOptions(message, sender);
      case 'media-source-select': return selectMediaSource(message, sender);
      case 'browser-media-select': return selectBrowserFile(message, sender);
      case 'media-select': {
        const { mediaSession, formatChoice, mode } = message;
        const audioTrack = typeof message.audioTrack === 'string' && message.audioTrack.length <= 256 ? message.audioTrack : undefined;
        const subtitles = Array.isArray(message.subtitles) && message.subtitles.length <= 25
          && message.subtitles.every(language => typeof language === 'string' && /^[a-z0-9_-]{1,35}$/i.test(language)) ? message.subtitles : undefined;
        const includeAutoSubtitles = message.includeAutoSubtitles === true;
        const embedSubtitles = message.embedSubtitles === true;
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
        if (audioTrack || subtitles?.length || includeAutoSubtitles || embedSubtitles) {
          const bytes = new TextEncoder().encode(JSON.stringify([audioTrack || null, [...(subtitles || [])].sort(), includeAutoSubtitles, embedSubtitles]));
          const digest = await crypto.subtle.digest('SHA-256', bytes);
          intentSuffix = '-' + [...new Uint8Array(digest)].slice(0, 16).map(byte => byte.toString(16).padStart(2, '0')).join('');
        }
        return nativeRequest(envelope('media-select', {
          id: `${mediaSession}-${formatChoice}-${mode}${suffix}${intentSuffix}`,
          mediaSession, formatChoice, mode, range, audioTrack, subtitles, includeAutoSubtitles, embedSubtitles
        }), 25000);
      }
      case 'native-picker': {
        const tabId = sender.tab?.id ?? message.tabId;
        const state = await tabState(tabId);
        const framePage = sender.tab ? mediaPage({}, sender) : null;
        const requestedPage = VesperPlayerSource.http(message.pageUrl);
        const trustedContentPage = framePage && (!message.pageUrl || requestedPage?.origin === framePage.origin)
          ? (requestedPage || framePage).href : null;
        const pageUrl = sender.tab ? trustedContentPage
          : requestedPage?.href || (await chrome.tabs.get(tabId))?.url;
        if (!VesperPlayerSource.http(pageUrl))
          return { ok: false, code: 'invalid', message: 'This page address cannot be inspected.' };
        // Media authorization follows the same approved, page-scoped path as media options.
        return nativeRequest(envelope('media-picker', {
          ...await mediaAuthorization(pageUrl, sender, { userGesture: !!trustedContentPage }),
          url: pageUrl, pageTitle: message.title || state.title || '',
          candidates: []
        }));
      }
      case 'describe': return describe(message.url, message.duration);
      case 'retry': return retryPending(message.id);
      case 'submit': {
        const { items, authorized } = await withSignIn(message.items.map((i) => ({ referer: message.pageUrl, userAgent: navigator.userAgent, ...i })));
        const prefs = await settings();
        const mode = message.mode || prefs.handoff;
        return submit(items, mode, { authorized });
      }
      default: return { ok: false, code: 'unknown', message: 'Unknown message.' };
    }
  })().then(sendResponse, (error) => sendResponse({ ok: false, code: 'extension-error', message: String(error) }));
  return true;   // async response
});

// ---------------------------------------------------------------- context menus (links, video, audio)

function contextPageUrl(info, tab) {
  if (Number.isInteger(info?.frameId) && info.frameId > 0 && /^https?:\/\//i.test(info.frameUrl || ''))
    return info.frameUrl;
  return /^https?:\/\//i.test(tab?.url || '') ? tab.url : null;
}

function linkedMediaPage(value) {
  const page = VesperPlayerSource.http(value);
  if (!page) return null;
  const identity = VesperPlayerSource.identity(page.href);
  if (/^(youtube|instagram|tiktok):/.test(identity)) return page.href;
  if (!isYouTubePage(page)) return null;
  if (['/playlist', '/watch'].includes(page.pathname)
      && /^[\w-]{2,128}$/.test(page.searchParams.get('list') || '')) return page.href;
  return null;
}

async function openContextMedia(pageUrl, info, tab) {
  const reply = pageUrl ? await nativeRequest(envelope('media-picker', {
    ...await mediaAuthorization(pageUrl, { tab, url: info.frameUrl || info.pageUrl || tab?.url || pageUrl,
      frameId: info.frameId ?? 0 }, { userGesture: true }),
    url: pageUrl, pageTitle: '', candidates: []
  })) : { ok: false, message: 'This page cannot be inspected.' };
  await flash(tab?.id, reply, info.frameId);
}

async function updateVesperContextMenus(paused) {
  for (const id of ['vesper-smart', 'vesper-download'])
    await chrome.contextMenus.remove(id).catch(() => {});
  if (paused) return;
  chrome.contextMenus.create({ id: 'vesper-smart', title: 'Smart Select files with VESPER', contexts: ['page'] });
  chrome.contextMenus.create({ id: 'vesper-download', title: 'Download with VESPER', contexts: ['link', 'video', 'audio'] });
}

chrome.storage.onChanged.addListener((changes, areaName) => {
  if (areaName !== 'local' || !changes.vesperPaused) return;
  return updateVesperContextMenus(changes.vesperPaused.newValue === true);
});

chrome.runtime.onInstalled.addListener(async details => {
  // A new version may resolve what the previous one could not, so what that one learned no longer applies.
  if (details?.reason === 'update') await VesperExperience.clear().catch(() => {});
  await chrome.contextMenus.removeAll();
  await updateVesperContextMenus(await extensionPaused());
  chrome.contextMenus.create({ id: 'vesper-transfers', title: 'VESPER transfers…', contexts: ['action'] });
  await updateBadge();
  await recoverChromeDownloads();
});

chrome.runtime.onStartup.addListener(async () => { await updateBadge(); await recoverChromeDownloads(); });

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  try { await runContextAction(info, tab); }
  catch { await flash(tab?.id, { ok: false, message: 'VESPER could not open this item. Try again.' }, info.frameId); }
});

async function openFramePicker(info, tab, playerUrl) {
  if (!Number.isSafeInteger(tab?.id)) return false;
  try {
    const opened = await chrome.tabs.sendMessage(tab.id, { type: 'vesper-open-picker', playerUrl }, { frameId: info.frameId ?? 0 });
    return opened?.ok === true;
  } catch { return false; }
}

async function runContextAction(info, tab) {
  await recoverChromeCaptures();
  if (info.menuItemId === 'vesper-transfers') {
    await chrome.tabs.create({ url: chrome.runtime.getURL('transfers.html') });
    return;
  }
  if (await extensionPaused()) return;
  if (info.menuItemId === 'vesper-smart') {
    try { await chrome.tabs.sendMessage(tab.id, { type: 'vesper-smart-select' }, { frameId: info.frameId ?? 0 }); }
    catch { await flash(tab?.id, { ok: false, message: 'Smart Select is unavailable on this page.' }, info.frameId); }
    return;
  }
  if (info.menuItemId !== 'vesper-download') return;
  const mediaAction = info.mediaType === 'video' || info.mediaType === 'audio';
  const url = mediaAction ? info.srcUrl : info.linkUrl;
  const targetPage = linkedMediaPage(info.linkUrl);
  if (targetPage) {
    await openContextMedia(targetPage, info, tab);
    return;
  }
  if (mediaAction && (!url || url.startsWith('blob:'))) {
    if (url && await openFramePicker(info, tab, url)) return;
    await openContextMedia(contextPageUrl(info, tab), info, tab);
    return;
  }
  if (!mediaAction && typeof url === 'string' && url.startsWith('blob:')) {
    const prefs = await settings();
    if (prefs.route === 'chrome' || tab?.incognito || !Number.isSafeInteger(tab?.id)) {
      await flash(tab?.id, { ok: false, message: 'Use Chrome to save this browser file.' }, info.frameId);
      return;
    }
    try {
      const reply = await chrome.tabs.sendMessage(tab.id, { type: 'vesper-copy-generated', url }, { frameId: info.frameId ?? 0 });
      if (!reply?.ok) await flash(tab.id, reply || { ok: false, message: 'This browser file is no longer available.' }, info.frameId);
    } catch {
      await flash(tab.id, { ok: false, message: 'This browser file is no longer available. Use the original download link.' }, info.frameId);
    }
    return;
  }
  if (!VesperPlayerSource.http(url)) {
    await flash(tab?.id, { ok: false, message: 'VESPER needs a web address for this item.' }, info.frameId);
    return;
  }
  const prefs = await settings();
  if (prefs.route === 'chrome') {
    if (mediaAction && VesperPlayerSource.kind(url) !== 'direct') {
      await flash(tab?.id, { ok: false, message: 'Chrome can only save a direct media file here.' }, info.frameId);
      return;
    }
    try {
      await chrome.downloads.download({ url, saveAs: false, conflictAction: 'uniquify' });
      await flash(tab?.id, { ok: true }, info.frameId);
    } catch { await flash(tab?.id, { ok: false, message: 'Chrome could not start the download.' }, info.frameId); }
    return;
  }
  // The frame that holds the link is the real initiator; an embedded frame can be another site than the tab.
  const { items, authorized } = await withSignIn([{ url, referer: info.frameUrl || info.pageUrl || tab?.url, userAgent: navigator.userAgent }],
    { frame: Number.isInteger(tab?.id) ? { tabId: tab.id, frameId: info.frameId ?? 0 } : undefined });
  const reply = await submit(items, prefs.handoff, { authorized });
  await flash(tab?.id, reply, info.frameId);
}

const contextFlashes = new Map();
async function flash(tabId, reply, frameId = 0) {
  if (!Number.isSafeInteger(tabId)) return;
  clearTimeout(contextFlashes.get(tabId));
  await chrome.action.setBadgeBackgroundColor({ tabId, color: reply.ok ? '#3D694B' : '#B25247' }).catch(() => {});
  await chrome.action.setBadgeText({ tabId, text: reply.ok ? '✓' : '!' }).catch(() => {});
  if (!reply.ok) {
    const message = reply.message || 'VESPER could not open this item.';
    await chrome.action.setTitle?.({ tabId, title: message }).catch(() => {});
    await chrome.tabs.sendMessage(tabId, { type: 'vesper-context-notice', reply: { code: reply.code, message } }, { frameId })
      .catch(() => {});
  } else await chrome.action.setTitle?.({ tabId, title: 'VESPER Download Manager' }).catch(() => {});
  contextFlashes.set(tabId, setTimeout(() => {
    contextFlashes.delete(tabId);
    chrome.action.setBadgeText({ tabId, text: '' }).catch(() => {});
    chrome.action.setTitle?.({ tabId, title: 'VESPER Download Manager' }).catch(() => {});
  }, reply.ok ? 2500 : 5000));
}

// ---------------------------------------------------------------- Chrome downloads

importScripts('browser-downloads.js');
importScripts('browser-managed.js');
