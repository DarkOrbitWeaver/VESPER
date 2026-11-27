'use strict';

const MANAGED_DOWNLOADS_KEY = 'vesperManagedDownloads';
const MAX_MANAGED_DOWNLOADS = 128;
const manualManagedDownloads = new Set();
let managedWrites = Promise.resolve();
let managedRefresh;
let managedPort;
let managedWatchPending = false;
let managedWatchHasFiles = false;
let managedProfile;

function managedHosts(item) {
  const hosts = new Set();
  for (const address of [item.referrer, item.url, item.finalUrl]) {
    try {
      const source = new URL(address);
      const origin = new URL(source.origin);
      if (/^https?:$/.test(origin.protocol)) {
        const host = normalizeRuleHost(origin.hostname);
        if (host) hosts.add(host);
      }
    } catch { /* Local downloads can lack a website. */ }
  }
  return [...hosts];
}

function managedCandidate(item) {
  const notMalicious = !['content', 'url', 'unwanted'].includes(item?.danger);
  const safeState = item?.state === 'complete' ? isSafe(item) : notMalicious;
  return safeState && !item?.incognito && !startedByVesper(item) && Number.isSafeInteger(item?.id)
    && Number.isFinite(Date.parse(item?.startTime))
    && /^(https?:|blob:|data:)/i.test(item?.finalUrl || item?.url || '')
    && managedHosts(item).length > 0;
}

function managesChromeDownloads() {
  return typeof chrome.runtime.connectNative === 'function';
}

async function managedItems() {
  await managedWrites;
  const { [MANAGED_DOWNLOADS_KEY]: items = [] } = await chrome.storage.local.get(MANAGED_DOWNLOADS_KEY);
  return items;
}

function updateManagedItems(update) {
  const job = managedWrites.then(async () => {
    const { [MANAGED_DOWNLOADS_KEY]: items = [] } = await chrome.storage.local.get(MANAGED_DOWNLOADS_KEY);
    await chrome.storage.local.set({ [MANAGED_DOWNLOADS_KEY]: update(items) });
  });
  managedWrites = job.catch(() => {});
  return job;
}

function managedProfileId() {
  managedProfile ??= (async () => {
    const { vesperManagedProfile } = await chrome.storage.local.get('vesperManagedProfile');
    if (/^[a-z0-9-]{8,36}$/i.test(vesperManagedProfile || '')) return vesperManagedProfile;
    const id = crypto.randomUUID();
    await chrome.storage.local.set({ vesperManagedProfile: id });
    return id;
  })().catch(error => { managedProfile = null; throw error; });
  return managedProfile;
}

const managedId = (item, profile) => `c-${profile}-${item.id.toString(36)}-${Date.parse(item.startTime).toString(36)}`;

function managedProgress(item, profile) {
  return {
    id: managedId(item, profile), name: (chromeFileName(item) || 'Chrome download').slice(0, 255),
    start: Date.parse(item.startTime), received: byteCount(item.bytesReceived) ?? 0,
    length: [item.fileSize, item.totalBytes].find(value => Number.isSafeInteger(value) && value > 0),
    paused: item.paused === true, canResume: item.canResume === true,
    hosts: managedHosts(item)
  };
}

async function rememberManaged(item, generation) {
  await updateManagedItems(items => {
    if (generation !== undefined && !captureAllowed(generation)) return items;
    return [...items.filter(row => row.id !== item.id), { id: item.id, startTime: item.startTime }].slice(-MAX_MANAGED_DOWNLOADS);
  });
}

async function forgetManaged(entries) {
  if (entries.length)
    await updateManagedItems(items => items.filter(item => !entries.some(entry => entry.id === item.id && sameDownload(entry, item))));
}

async function listManagedCandidates() {
  if (!managesChromeDownloads()) return [];
  const [items, enrolled, records] = await Promise.all([
    chrome.downloads.search({ state: 'in_progress', orderBy: ['-startTime'], limit: 32 }),
    managedItems(), readBrowserRecords(), siteRulesLoaded
  ]);
  return items.filter(item => managedCandidate(item) && item.state === 'in_progress'
      && !enrolled.some(entry => entry.id === item.id && sameDownload(item, entry)) && !nativeOwnsManagedItem(item, records)
      && !manualManagedDownloads.has(item.id) && !matchesNeverCaptureHost(item))
    .map(item => ({ id: item.id, startTime: item.startTime, name: chromeFileName(item) || 'Chrome download' }));
}

function nativeOwnsManagedItem(item, records) {
  const record = records[item.id];
  return browserOperations.has(item.id) || sameDownload(item, record || {})
    && ['attempted', 'parking', 'parked', 'done'].includes(record.stage);
}

async function continueManagedDownload(id, startTime) {
  if (!(await listManagedCandidates()).some(item => item.id === id && item.startTime === startTime)
      || manualManagedDownloads.has(id) || browserOperations.has(id))
    return { ok: false, message: 'This download is no longer available to add. Check Chrome’s Downloads page.' };
  manualManagedDownloads.add(id);
  try {
    const item = await getChromeDownload(id);
    if (!managedCandidate(item) || item.state !== 'in_progress' || item.startTime !== startTime)
      return { ok: false, message: 'Chrome has already finished or stopped this download.' };
    const profile = await managedProfileId();
    const reply = await nativeRequest(envelope('browser-managed', {
      operation: 'connect', profile, files: [managedProgress(item, profile)]
    }), 15000);
    if (!reply?.ok) return reply;
    if (reply.data?.blocked?.includes(managedId(item, profile)))
      return { ok: false, message: 'This site is set to keep downloads in Chrome.' };
    await rememberManaged(item);
    void refreshManagedDownloads();
    return { ok: true, message: 'Chrome is still downloading this file. You can follow it in VESPER.' };
  } finally {
    manualManagedDownloads.delete(id);
  }
}

function refreshManagedDownloads() {
  if (!managesChromeDownloads()) return Promise.resolve();
  if (managedRefresh) return managedRefresh;
  managedRefresh = refreshManaged().catch(() => {}).finally(() => { managedRefresh = null; });
  return managedRefresh;
}

async function refreshManaged() {
  const [entries, records] = await Promise.all([managedItems(), readBrowserRecords()]);
  if (!entries.length) {
    const port = managedPort;
    managedPort = null;
    managedWatchPending = false;
    if (port) port.disconnect();
    return;
  }

  const profile = await managedProfileId();
  const files = [];
  const forget = [];
  for (const entry of entries) {
    const item = await getChromeDownload(entry.id);
    if (!item || !sameDownload(item, entry) || ['USER_CANCELED', 'USER_SHUTDOWN'].includes(item.error)) {
      forget.push(entry);
      continue;
    }
    const record = records[item.id];
    if (record?.stage === 'done' && sameDownload(item, record)) { forget.push(entry); continue; }
    if (nativeOwnsManagedItem(item, records) || !managedCandidate(item)) continue;
    if (item.state === 'in_progress') {
      if (files.length < 32) files.push(managedProgress(item, profile));
    } else if (item.state === 'complete' && item.exists !== false && item.filename && byteCount(item.fileSize) !== undefined) {
      const reply = await nativeRequest(envelope('browser-managed', {
        operation: 'complete', profile, downloadId: managedId(item, profile), path: item.filename,
        length: item.fileSize, hosts: managedHosts(item)
      }), 10000);
      if (reply?.ok || ['invalid', 'invalid-file', 'site-capture-disabled'].includes(reply?.code)) forget.push(entry);
      else await armBrowserRecovery();
    }
  }
  await forgetManaged(forget);
  if (!files.length && !managedPort) return;
  if (!managedPort) {
    const port = chrome.runtime.connectNative(HOST);
    managedPort = port;
    port.onMessage.addListener(reply => {
      if (managedPort !== port) return;
      managedWatchPending = false;
      if (!reply?.ok) {
        managedPort = null;
        port.disconnect();
        if (reply?.code === 'app-unavailable') void armBrowserRecovery().catch(() => {});
        return;
      }
      if (!managedWatchHasFiles) {
        managedPort = null;
        port.disconnect();
        return;
      }
      const blocked = reply.data?.blocked || [];
      void forgetManaged(entries.filter(entry => blocked.includes(managedId(entry, profile))))
        .then(() => refreshManagedDownloads()).catch(() => {});
    });
    port.onDisconnect.addListener(() => {
      const error = chrome.runtime.lastError;
      if (managedPort !== port) return;
      managedPort = null;
      managedWatchPending = false;
      if (!/not found|forbidden/i.test(error?.message || ''))
        void armBrowserRecovery().catch(() => {});
    });
  }
  if (managedWatchPending) return;
  managedWatchPending = true;
  managedWatchHasFiles = files.length > 0;
  managedPort.postMessage(envelope('browser-managed', { operation: 'watch', profile, files }));
}

async function enrollAutomaticChromeDownload(item, generation) {
  if (!managesChromeDownloads() || !managedCandidate(item) || !await captureCanBegin(generation)) return;
  await siteRulesLoaded;
  if (!matchesNeverCaptureHost(item)) await rememberManaged(item, generation);
}
