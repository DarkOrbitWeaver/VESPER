// VESPER service worker. Event-driven only: it wakes for a message, a context-menu click, a download the
// user opted to hand off, or a narrowly filtered media request. It never polls and holds no native connection.
'use strict';

const HOST = 'com.vesper.downloader';
const PROTOCOL = 1;
const MAX_SOURCES = 25;
const DEFAULTS = {
  handoff: 'download',         // default = VESPER decides, download, queue
  intercept: true,             // enabled by default when the user explicitly installs the extension; can be disabled
  useSignIn: true              // Chrome still requires the user to grant cookie access
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

async function submit(items, mode, { authorized = false, chromeDownloadId } = {}) {
  const request = envelope('submit', { mode: mode || 'ask', items, authorized,
    confirmTransfer: chromeDownloadId !== undefined });
  await changePending(pending => {
    pending[request.id] = { request: { ...request, authorized: false, items: items.map(stripSecrets) },
      at: Date.now(), chromeDownloadId };
  });
  return sendSubmission(request, chromeDownloadId);
}

async function sendSubmission(request, chromeDownloadId) {
  const reply = await nativeRequest(request);
  let chromePaused = false;
  if (chromeDownloadId !== undefined && reply.ok && reply.results?.[0]?.transferReady === true)
    chromePaused = await pauseChromeDownload(chromeDownloadId, request.items[0].url);
  if (reply.ok || reply.code === 'duplicate' || reply.code === 'cancelled') {
    await changePending(pending => { delete pending[request.id]; });
  }
  return chromeDownloadId === undefined ? reply : { ...reply, chromePaused };
}

const stripSecrets = (item) => { const { cookie, authorization, ...safe } = item; return safe; };   // secrets are never stored

async function retryPending(id) {
  const { pending = {} } = await chrome.storage.local.get('pending');
  const entry = pending[id];
  if (!entry) return { ok: false, code: 'gone', message: 'That request is no longer pending.' };
  const enriched = await withSignIn(entry.request.items);
  return sendSubmission({ ...entry.request, ...enriched }, entry.chromeDownloadId);
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

function sourceKey(url) {
  try { const u = new URL(url); return u.origin + u.pathname; } catch { return url; }   // signed query strings change per request
}

async function addSource(tabId, source) {
  if (tabId < 0 || !/^https?:/i.test(source.url) || SEGMENT.test(source.url)) return;
  const state = await tabState(tabId);
  const key = sourceKey(source.url);
  if (state.sources.some((s) => sourceKey(s.url) === key)) return;
  state.sources.push({ ...source, seen: Date.now() });
  await saveTabState(tabId, state);
}

// Narrow listeners: only media requests, plus playlist-looking URLs. The worker sleeps otherwise.
chrome.webRequest.onResponseStarted.addListener((details) => {
  const header = (name) => details.responseHeaders?.find((h) => h.name.toLowerCase() === name)?.value;
  const type = header('content-type') || '';
  const range = header('content-range');
  const bytes = range ? Number(range.split('/')[1]) : Number(header('content-length')) || null;
  const isPlaylist = /\.(m3u8|mpd)(\?|$)/i.test(details.url) || /mpegurl|dash\+xml/i.test(type);
  if (!isPlaylist && !(details.type === 'media' || DIRECT.test(details.url))) return;
  if (!isPlaylist && bytes !== null && bytes < 256 * 1024) return;              // ignore tiny clips and beacons
  addSource(details.tabId, {
    url: details.url,
    kind: /\.mpd(\?|$)/i.test(details.url) ? 'dash' : isPlaylist ? 'hls' : 'direct',
    mime: type.split(';')[0], bytes
  });
}, { urls: ['http://*/*', 'https://*/*'], types: ['media', 'xmlhttprequest', 'other'] }, ['responseHeaders']);

chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (change.url) { chrome.storage.session.remove(`tab:${tabId}`); chrome.action.setBadgeText({ tabId, text: '' }); }   // new page, new sources
});
chrome.tabs.onRemoved.addListener((tabId) => chrome.storage.session.remove(`tab:${tabId}`));

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
    switch (message.type) {
      case 'media-element': {
        const tabId = sender.tab?.id ?? -1;
        const state = await tabState(tabId);
        state.title = message.title || state.title;
        state.duration = message.duration || state.duration;
        state.poster = message.poster || state.poster;
        if (message.unsupported) state.unsupported = message.unsupported;
        await saveTabState(tabId, state);
        if (!message.unsupported && message.src && /^https?:/i.test(message.src)) {
          const kind = /\.m3u8(?:[?#]|$)/i.test(message.src) ? 'hls'
            : /\.mpd(?:[?#]|$)/i.test(message.src) ? 'dash' : 'direct';
          await addSource(tabId, { url: message.src, kind, mime: '', bytes: null });
        }
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
      case 'media-options': {
        // Use the actual sender's frame. Prefer its player over unrelated requests in the tab.
        const page = sender.url || sender.tab?.url;
        if (!/^https?:\/\//i.test(page || ''))
          return { ok: false, code: 'invalid', message: 'This page cannot be inspected.' };
        const player = /^https?:\/\//i.test(message.playerUrl || '') ? message.playerUrl : null;
        const state = await tabState(sender.tab?.id);
        const candidates = player
          ? [{ url: player, kind: /\.m3u8(?:[?#]|$)/i.test(player) ? 'hls'
            : /\.mpd(?:[?#]|$)/i.test(player) ? 'dash' : 'direct' }]
          : state.sources.slice(0, 25).map(s => ({ url: s.url, kind: s.kind, mimeType: s.mime || undefined }));
        return nativeRequest(envelope('media-options', { url: page, pageTitle: message.title, candidates }), 60000);
      }
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
          candidates: state.sources.slice(0, 25).map(s => ({
            url: s.url, kind: s.kind, mimeType: s.mime || undefined
          }))
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
});

chrome.runtime.onStartup.addListener(updateBadge);

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === 'vesper-active-downloads') {
    await chrome.tabs.create({ url: chrome.runtime.getURL('transfers.html') });
    return;
  }
  const url = info.menuItemId === 'vesper-link' ? info.linkUrl : info.srcUrl;
  if (!url || !/^https?:/i.test(url)) return;      // blob: and data: URLs cannot be fetched by another program
  const prefs = await settings();
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

// ---------------------------------------------------------------- opt-in takeover of ordinary downloads

const browserTransfers = new Map();
const browserUrl = item => item.finalUrl || item.url;
const canMove = item => item?.state === 'in_progress' && item.danger === 'safe'
  && !item.incognito && /^https?:\/\//i.test(browserUrl(item) || '');

function browserName(item) {
  const name = item.filename?.split(/[\\/]/).pop();
  if (name && !/\.crdownload$/i.test(name)) return name;
  try { return new URL(browserUrl(item)).pathname.split('/').pop() || ''; }
  catch { return ''; }
}

async function listChromeDownloads() {
  const downloads = await chrome.downloads.search({ state: 'in_progress', orderBy: ['-startTime'], limit: 25 });
  return { ok: true, downloads: downloads.filter(canMove).slice(0, 8).map(item => ({
    id: item.id, name: browserName(item) || 'Download', paused: item.paused
  })) };
}

async function pauseChromeDownload(id, url) {
  try {
    const [item] = await chrome.downloads.search({ id });
    if (!canMove(item) || browserUrl(item) !== url) return false;
    if (!item.paused) await chrome.downloads.pause(id);
    const [current] = await chrome.downloads.search({ id });
    return current?.state === 'in_progress' && current.paused;
  } catch { return false; }
}

async function moveChromeDownload(id, mode) {
  if (!Number.isInteger(id)) return { ok: false, code: 'invalid', message: 'Choose a Chrome download.' };
  if (browserTransfers.has(id)) return browserTransfers.get(id);
  const move = (async () => {
    const [item] = await chrome.downloads.search({ id });
    if (!canMove(item)) return { ok: false, code: 'gone', message: 'This Chrome download is no longer available to move.' };
    const { pending = {} } = await chrome.storage.local.get('pending');
    const unconfirmed = Object.entries(pending).find(([, entry]) => entry.chromeDownloadId === id);
    if (unconfirmed) return retryPending(unconfirmed[0]);
    const { items, authorized } = await withSignIn([{
      url: browserUrl(item), fileName: browserName(item) || undefined,
      referer: item.referrer || undefined, userAgent: navigator.userAgent
    }]);
    return submit(items, mode, { authorized, chromeDownloadId: id });
  })();
  browserTransfers.set(id, move);
  try { return await move; }
  finally { browserTransfers.delete(id); }
}

async function captureChromeDownload(item) {
  if (!canMove(item)) return;
  try {
    const prefs = await settings();
    if (!prefs.intercept) return;
    await moveChromeDownload(item.id, prefs.handoff === 'default' ? 'ask' : prefs.handoff);
  } catch { /* Leave the Chrome download alone if handoff is unavailable. */ }
}

chrome.downloads.onCreated.addListener(captureChromeDownload);
chrome.downloads.onChanged.addListener(async change => {
  if (change.danger?.current !== 'safe' || change.danger.previous === 'safe') return;
  try {
    const [item] = await chrome.downloads.search({ id: change.id });
    await captureChromeDownload(item);
  } catch { /* Chrome still owns this transfer. */ }
});
