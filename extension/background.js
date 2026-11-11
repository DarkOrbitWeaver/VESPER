// VESPER service worker. Event-driven only: it wakes for a message, a context-menu click, a download the
// user opted to hand off, or a narrowly filtered media request. It never polls and holds no native connection.
'use strict';

const HOST = 'com.vesper.downloader';
const PROTOCOL = 1;
const MAX_SOURCES = 25;
const DEFAULTS = {
  handoff: 'default',          // default = VESPER decides, download, queue
  intercept: true,             // enabled by default when the user explicitly installs the extension; can be disabled
  extensions: ['zip', 'rar', '7z', 'iso', 'exe', 'msi', 'dmg', 'pdf', 'mp4', 'mkv', 'webm', 'mp3', 'flac', 'epub', 'tar', 'gz'],
  useSignIn: false             // forward cookies for the download's own site, only with explicit consent
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
async function submit(items, mode, { authorized = false } = {}) {
  const request = envelope('submit', { mode: mode || 'ask', items, authorized });
  const { pending = {} } = await chrome.storage.local.get('pending');
  pending[request.id] = { request: { ...request, items: items.map(stripSecrets) }, at: Date.now() };
  await chrome.storage.local.set({ pending });
  await updateBadge();

  const reply = await nativeRequest(request);
  if (reply.ok || reply.code === 'duplicate' || reply.code === 'cancelled') {
    const current = (await chrome.storage.local.get('pending')).pending || {};
    delete current[request.id];
    await chrome.storage.local.set({ pending: current });
    await updateBadge();
  }
  return reply;
}

const stripSecrets = (item) => { const { cookie, authorization, ...safe } = item; return safe; };   // secrets are never stored

async function retryPending(id) {
  const { pending = {} } = await chrome.storage.local.get('pending');
  const entry = pending[id];
  if (!entry) return { ok: false, code: 'gone', message: 'That request is no longer pending.' };
  const reply = await nativeRequest(entry.request);
  if (reply.ok || reply.code === 'duplicate' || reply.code === 'cancelled') {
    delete pending[id];
    await chrome.storage.local.set({ pending });
    await updateBadge();
  }
  return reply;
}

async function updateBadge() {
  const { pending = {} } = await chrome.storage.local.get('pending');
  const count = Object.keys(pending).length;
  await chrome.action.setBadgeBackgroundColor({ color: '#B25247' });
  await chrome.action.setBadgeText({ text: count ? '!' : '' });
}

async function withSignIn(items, pageUrl) {
  const prefs = await settings();
  if (!prefs.useSignIn || !(await chrome.permissions.contains({ permissions: ['cookies'] }))) return { items, authorized: false };
  const enriched = [];
  for (const item of items) {
    // Only the cookies the browser would send to this exact address, never the whole cookie jar.
    const cookies = await chrome.cookies.getAll({ url: item.url });
    enriched.push({ ...item, cookie: cookies.map((c) => `${c.name}=${c.value}`).join('; ') || undefined });
  }
  return { items: enriched, authorized: true };
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
        if (message.src && /^https?:/i.test(message.src)) {
          const hls = /\.m3u8(\?|$)/i.test(message.src);
          await addSource(tabId, { url: message.src, kind: hls ? 'hls' : 'direct', mime: '', bytes: null });
        }
        return { ok: true };
      }
      case 'tab-state': {
        const state = await tabState(message.tabId ?? sender.tab?.id);
        const { pending = {} } = await chrome.storage.local.get('pending');
        return { ok: true, state, pending: Object.entries(pending).map(([id, p]) => ({ id, at: p.at, count: p.request.items.length })) };
      }
      case 'hello': return nativeRequest(envelope('hello'), 15000);
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
        const { items, authorized } = await withSignIn(message.items.map((i) => ({ referer: message.pageUrl, userAgent: navigator.userAgent, ...i })), message.pageUrl);
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
  await updateBadge();
});

chrome.runtime.onStartup.addListener(updateBadge);

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const url = info.menuItemId === 'vesper-link' ? info.linkUrl : info.srcUrl;
  if (!url || !/^https?:/i.test(url)) return;      // blob: and data: URLs cannot be fetched by another program
  const prefs = await settings();
  const { items, authorized } = await withSignIn([{ url, referer: tab?.url, userAgent: navigator.userAgent }], tab?.url);
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

chrome.downloads.onCreated.addListener(async (item) => {
  const prefs = await settings();
  if (!prefs.intercept || !/^https?:/i.test(item.finalUrl || item.url)) return;
  const name = (item.filename || item.finalUrl || item.url).split(/[\\/]/).pop().split('?')[0];
  const extension = (name.split('.').pop() || '').toLowerCase();
  if (!prefs.extensions.includes(extension)) return;

  const url = item.finalUrl || item.url;
  const { items, authorized } = await withSignIn([{ url, referer: item.referrer || undefined, userAgent: navigator.userAgent }], item.referrer);
  // Hand off first; only cancel the browser's own download once VESPER has actually accepted it.
  const reply = await submit(items, prefs.handoff === 'default' ? 'ask' : prefs.handoff, { authorized });
  // Cancel only after VESPER accepted THIS file; never cancel if another batch item succeeded.
  const accepted = reply.ok && Array.isArray(reply.results) &&
    reply.results.some(r => ['started', 'queued', 'already-submitted'].includes(r.status));
  if (accepted) {
    await chrome.downloads.cancel(item.id).catch(() => {});
    await chrome.downloads.erase({ id: item.id }).catch(() => {});
  }
  // On any failure the browser keeps its download: interception must never silently break downloading.
});
