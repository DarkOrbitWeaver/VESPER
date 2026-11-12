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

// A submission is written down BEFORE it is sent and erased only after VESPER acknowledged it. If the browser
// restarts mid-flight the popup shows it as unconfirmed; resending reuses the same id, which VESPER de-duplicates.
let pendingWrites = Promise.resolve();
function changePending(change) {
  const write = pendingWrites.then(async () => {
    const { pending = {} } = await chrome.storage.local.get('pending');
    change(pending);
    await chrome.storage.local.set({ pending });
    await updateBadge();
  });
  pendingWrites = write.catch(() => {});
  return write;
}

async function submit(items, mode, { authorized = false } = {}) {
  const request = envelope('submit', { mode: mode || 'ask', items, authorized });
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

const stripSecrets = (item) => { const { cookie, authorization, ...safe } = item; return safe; };   // secrets are never stored

async function retryPending(id) {
  const { pending = {} } = await chrome.storage.local.get('pending');
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
  const { pending = {} } = await chrome.storage.local.get('pending');
  const count = Object.keys(pending).length;
  await chrome.action.setBadgeBackgroundColor({ color: '#B25247' });
  await chrome.action.setBadgeText({ text: count ? '!' : '' });
}

async function withSignIn(items) {
  const safe = items.map(stripSecrets);
  const prefs = await settings();
  if (!prefs.useSignIn || !(await chrome.permissions.contains({ permissions: ['cookies'] })))
    return { items: safe, authorized: false };
  const enriched = [];
  for (const item of safe) {
    let cookie;
    try {
      const matches = await chrome.cookies.getAll({ url: item.url });
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

async function tabState(tabId) {
  const key = `tab:${tabId}`;
  const stored = (await chrome.storage.session.get(key))[key];
  return stored || { sources: [], unsupported: null, title: '', duration: null, poster: '' };
}

async function saveTabState(tabId, state) {
  state.sources = state.sources.slice(-MAX_SOURCES);
  await chrome.storage.session.set({ [`tab:${tabId}`]: state });
  if (state.sources.length) {
    await chrome.action.setBadgeBackgroundColor({ tabId, color: '#3D694B' });
    await chrome.action.setBadgeText({ tabId, text: String(Math.min(state.sources.length, 9)) });
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

// Narrow listeners: only media requests, plus playlist-looking URLs. The worker sleeps otherwise.
chrome.webRequest.onResponseStarted.addListener((details) => {
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
}, { urls: ['http://*/*', 'https://*/*'], types: ['media', 'xmlhttprequest', 'other'] }, ['responseHeaders']);

chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (change.status === 'loading' && change.url) {
    void inTabOrder(tabId, async () => {
      await chrome.storage.session.remove(`tab:${tabId}`);
      await clearBrowserChoicesForTab(tabId);
      await chrome.action.setBadgeText({ tabId, text: '' });
    }).catch(() => {});
  }
});
chrome.tabs.onRemoved.addListener(tabId => inTabOrder(tabId, async () => {
  await chrome.storage.session.remove(`tab:${tabId}`);
  await clearBrowserChoicesForTab(tabId);
}).catch(() => {}));

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
  if (mediaLookups.has(lookupKey)) return mediaLookups.get(lookupKey);
  const task = (async () => {
    const state = await tabState(tab);
    const observed = url => state.sources.find(s => sender.documentId && s.documentId === sender.documentId
      && (s.frameId ?? 0) === frame && s.url === url.href && s.seen >= Math.max(Date.now() - 10 * 60_000, message.mediaChangedAt || 0));
    const matching = source ? observed(source) : null;
    const candidate = url => {
      const evidence = observed(url);
      return { url: url.href, kind: evidence?.kind || VesperPlayerSource.kind(url.href), mimeType: evidence?.mime || undefined };
    };
    const matchedManifest = manifest && observed(manifest);
    const candidates = [source, matchedManifest && matchedManifest.kind !== 'direct' ? manifest : null].filter(Boolean).map(candidate);
    const complete = source && matching?.kind === 'direct' && matching.complete && /^(?:audio|video)\//.test(matching.mime)
      && !SEGMENT.test(source.href) && !['range', 'sq', 'segment'].some(key => source.searchParams.has(key));
    const title = binding || !youTube ? message.title : undefined;

    if (route !== 'chrome') {
      const reply = await nativeRequest(envelope('media-options', { url: page.href, pageTitle: title,
        candidates: !youTube || candidates.length ? candidates : undefined }), 60000);
      if (reply.ok || !complete || !['host-missing', 'host-error', 'app-unavailable', 'network', 'sign-in', 'unavailable', 'timeout'].includes(reply.code)) return reply;
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
  try { return await task; }
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

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  (async () => {
    await recoverChromeCaptures();
    switch (message.type) {
      case 'media-element': {
        // Older open tabs may still send these. Only observed responses belong in the network cache.
        return { ok: true };
      }
      case 'tab-state': {
        const state = await tabState(message.tabId ?? sender.tab?.id);
        const { pending = {} } = await chrome.storage.local.get('pending');
        return { ok: true, state, pending: Object.entries(pending).map(([id, p]) => ({ id, at: p.at, count: p.request.items.length })) };
      }
      case 'hello': return nativeRequest(envelope('hello'), 20000);
      case 'chrome-downloads': return listChromeDownloads();
      case 'move-download': return moveChromeDownload(message.downloadId, 'download');
      case 'media-options': return playerOptions(message, sender);
      case 'browser-media-select': return selectBrowserFile(message, sender);
      case 'media-select': {
        const { mediaSession, formatChoice, mode } = message;
        if (!/^[a-f0-9]{32}$/i.test(mediaSession || '') || !Number.isInteger(formatChoice) || formatChoice < 0 ||
            formatChoice >= 64 || !['queue', 'download'].includes(mode))
          return { ok: false, code: 'invalid', message: 'Choose an available format.' };
        return nativeRequest(envelope('media-select', {
          id: `${mediaSession}-${formatChoice}-${mode}`, mediaSession, formatChoice, mode
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
          url: pageUrl, pageTitle: message.title || state.title || '',
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
  const url = info.menuItemId === 'vesper-link' ? info.linkUrl : info.srcUrl;
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
  const { items, authorized } = await withSignIn([{ url, referer: tab?.url, userAgent: navigator.userAgent }]);
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
