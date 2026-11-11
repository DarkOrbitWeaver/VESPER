'use strict';
// Toolbar panel: runs only while it is open. It asks the service worker for state; it never scans pages itself.

const $ = (id) => document.getElementById(id);
const send = (message) => new Promise((resolve) =>
  chrome.runtime.sendMessage(message, (reply) => resolve(chrome.runtime.lastError ? { ok: false, code: 'extension-error', message: chrome.runtime.lastError.message } : reply)));

const FRIENDLY = {
  'host-missing': 'The VESPER connection isn’t installed. Reinstall or update VESPER to repair its browser helper.',
  'app-unavailable': 'VESPER isn’t running and couldn’t be started.',
  'host-error': 'The connection to VESPER failed.',
  duplicate: 'That link is already in VESPER.',
  cancelled: 'Cancelled in VESPER.',
  timeout: 'VESPER didn’t answer in time.'
};
const explain = (reply) => FRIENDLY[reply?.code] || reply?.message || 'Something went wrong.';
const size = (n) => (n ? (n >= 1073741824 ? `${(n / 1073741824).toFixed(2)} GB` : `${(n / 1048576).toFixed(n >= 104857600 ? 0 : 1)} MB`) : '');

function say(text, kind) {
  const el = $('result');
  el.hidden = !text; el.textContent = text || ''; el.className = `result ${kind || ''}`;
}

async function connection() {
  const reply = await send({ type: 'hello' });
  const status = $('status');
  if (reply?.ok) { status.textContent = reply.data?.appRunning === false ? 'VESPER will start when you send a download.' : 'Connected to VESPER.'; status.className = 'good'; }
  else { status.textContent = explain(reply); status.className = 'bad'; }
}

let sources = [];
async function load() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const reply = await send({ type: 'tab-state', tabId: tab?.id });
  const state = reply?.state || { sources: [] };
  sources = (state.sources || []).filter((s) => s.kind === 'hls' || s.kind === 'direct');

  const note = $('media-note'), list = $('media-list');
  list.replaceChildren();
  $('batch').hidden = sources.length === 0;
  if (state.unsupported === 'drm') note.textContent = 'This page plays protected (DRM) video. VESPER does not bypass protection.';
  else if (sources.length) note.textContent = sources.length === 1 ? 'One downloadable source was found.' : `${sources.length} downloadable sources were found.`;
  else if ((state.sources || []).some((s) => s.kind === 'dash')) note.textContent = 'This page streams with DASH, which VESPER can’t download yet.';
  else note.textContent = 'No downloadable media found. Play the video first, then open this panel again. Some players stream in ways VESPER can’t access.';

  sources.forEach((source, index) => {
    const li = document.createElement('li');
    const box = Object.assign(document.createElement('input'), { type: 'checkbox', checked: sources.length === 1, id: `s${index}` });
    const name = document.createElement('div'); name.className = 'name';
    const title = document.createElement('b'); const detail = document.createElement('small');
    let host = ''; try { host = new URL(source.url).hostname; } catch { /* keep empty */ }
    title.textContent = source.kind === 'hls' ? 'HLS stream' : (source.mime || 'Media file');
    detail.textContent = [host, size(source.bytes)].filter(Boolean).join(' · ');
    name.append(title, detail);
    li.append(box, name);
    list.append(li);
  });

  const pending = reply?.pending || [];
  $('pending').hidden = pending.length === 0;
  const pl = $('pending-list'); pl.replaceChildren();
  for (const p of pending) {
    const li = document.createElement('li');
    const name = document.createElement('div'); name.className = 'name';
    name.textContent = `${p.count} link${p.count === 1 ? '' : 's'} · ${new Date(p.at).toLocaleTimeString()}`;
    const retry = Object.assign(document.createElement('button'), { className: 'btn', textContent: 'Send again' });
    retry.onclick = async () => { retry.disabled = true; const r = await send({ type: 'retry', id: p.id }); say(r?.ok ? 'Confirmed by VESPER.' : explain(r), r?.ok ? 'good' : 'bad'); load(); };
    li.append(name, retry); pl.append(li);
  }
}

// Batch actions use the best quality of each chosen source. Per-quality choice lives in the on-video picker.
async function sendSelected(mode) {
  const chosen = sources.filter((_, i) => $(`s${i}`)?.checked);
  if (!chosen.length) return say('Choose at least one item.', 'bad');
  $('queue-selected').disabled = $('download-selected').disabled = true;
  say('Preparing…');
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const items = [];
  for (const source of chosen) {
    const described = await send({ type: 'describe', url: source.url, duration: null });
    if (!described?.ok) { say(explain(described), 'bad'); break; }
    const best = described.choices.find((c) => c.recommended) || described.choices[0];
    items.push({ url: best.url, kind: best.kind === 'hls' ? 'hls' : 'http', fileName: best.kind === 'hls' ? (tab?.title || 'video').replace(/[\\/:*?"<>|]+/g, ' ').trim().slice(0, 120) : undefined });
  }
  if (items.length === chosen.length) {
    const reply = await send({ type: 'submit', mode, pageUrl: tab?.url, items });
    say(reply?.ok ? (mode === 'queue' ? 'Added to the VESPER queue.' : 'Sent to VESPER.') : explain(reply), reply?.ok ? 'good' : 'bad');
  }
  $('queue-selected').disabled = $('download-selected').disabled = false;
}

async function settingsInit() {
  const prefs = await chrome.storage.sync.get({ handoff: 'default', intercept: true, useSignIn: false });
  $('handoff').value = prefs.handoff;
  $('intercept').checked = prefs.intercept;
  $('signin').checked = prefs.useSignIn && await chrome.permissions.contains({ permissions: ['cookies'] });
  $('handoff').onchange = () => chrome.storage.sync.set({ handoff: $('handoff').value });
  $('intercept').onchange = () => chrome.storage.sync.set({ intercept: $('intercept').checked });
  $('signin').onchange = async () => {
    let granted = true;
    if ($('signin').checked) granted = await chrome.permissions.request({ permissions: ['cookies'] });   // needs this user gesture
    $('signin').checked = granted && $('signin').checked;
    chrome.storage.sync.set({ useSignIn: $('signin').checked });
  };
}

$('open-native-picker').onclick = async () => {
  const button = $('open-native-picker');
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.url?.startsWith('http')) return say('Open a normal webpage to discover media.', 'bad');
  button.disabled = true;
  say('Opening the native VESPER media picker…');
  try {
    const reply = await send({ type: 'native-picker', tabId: tab.id, pageUrl: tab.url, title: tab.title });
    say(reply?.ok ? 'Media was added to VESPER.' : explain(reply), reply?.ok ? 'good' : 'bad');
  } finally { button.disabled = false; }
};
$('queue-selected').onclick = () => sendSelected('queue');
$('download-selected').onclick = () => sendSelected('download');
settingsInit();
load();
connection();
