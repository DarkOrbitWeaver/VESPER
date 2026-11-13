'use strict';

const RECOVERY_ALARM = 'vesper-chrome-downloads';
const MAX_BROWSER_RECORDS = 128;
const RESPONSE_LIFETIME = 60_000;
const CAPTURE_STATE_KEY = 'vesperDownloadCapture';
const responses = new Map();
const awaitingResponses = new Map();
const browserOperations = new Map();
let recordWrites = Promise.resolve();
let captureWrites = Promise.resolve();
let captureRecovery;
let profileIdPromise;
let activityPort, openingActivity = false;

const browserUrl = item => item.finalUrl || item.url;
const isSafe = item => !!item && !item.incognito && ['safe', 'deepScannedSafe'].includes(item.danger);
const startedByVesper = item => !!item?.byExtensionId && item.byExtensionId === chrome.runtime.id;
const canMove = item => isSafe(item) && item.state === 'in_progress' && /^https?:\/\//i.test(browserUrl(item) || '');
const byteCount = value => Number.isSafeInteger(value) && value >= 0 ? value : undefined;
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
}).catch(() => {});

function saveCaptureState() {
  const now = Date.now();
  for (const [url, response] of responses)
    if (now < response.seen || now - response.seen > RESPONSE_LIFETIME) responses.delete(url);
  for (const [id, waiting] of awaitingResponses)
    if (now < waiting.seen || now - waiting.seen > RESPONSE_LIFETIME) awaitingResponses.delete(id);
  while (responses.size > MAX_BROWSER_RECORDS) responses.delete(responses.keys().next().value);
  while (awaitingResponses.size > MAX_BROWSER_RECORDS) awaitingResponses.delete(awaitingResponses.keys().next().value);
  const state = { responses: [...responses], awaiting: [...awaitingResponses] };
  const write = captureWrites.then(() => chrome.storage.session.set({ [CAPTURE_STATE_KEY]: state }));
  captureWrites = write.catch(() => {});
  return write;
}

function rememberWaitingResponse(item, url) {
  awaitingResponses.set(item.id, { id: item.id, startTime: item.startTime, url, seen: Date.now() });
  while (awaitingResponses.size > MAX_BROWSER_RECORDS) awaitingResponses.delete(awaitingResponses.keys().next().value);
  return saveCaptureState();
}

// Remember only the response facts needed to identify an attachment. No request bodies or credential headers.
async function observeDownloadResponse(details) {
  await captureStateReady;
  if (details.statusCode !== 200 && details.statusCode !== 206) return;
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
  const old = responses.get(details.url);
  const conflicts = old && Date.now() - old.seen <= RESPONSE_LIFETIME
    && (!old.replay || old.etag && etag && old.etag !== etag
        || old.length !== undefined && length !== undefined && old.length !== length);
  responses.delete(details.url);
  responses.set(details.url, { seen: Date.now(), replay: details.method === 'GET' && (!encoding || encoding === 'identity') && !conflicts,
    length, etag, contentType: mime });
  const waiting = [...awaitingResponses.values()].filter(entry => entry.url === details.url);
  for (const entry of waiting) awaitingResponses.delete(entry.id);
  await saveCaptureState();
  for (const entry of waiting) {
    void getChromeDownload(entry.id).then(item => {
      if (item?.startTime === entry.startTime && browserUrl(item) === details.url) return captureChromeDownload(item);
    }).catch(() => {});
  }
}

chrome.webRequest.onHeadersReceived.addListener(details => { void observeDownloadResponse(details).catch(() => {}); },
  { urls: ['http://*/*', 'https://*/*'], types: ['main_frame', 'sub_frame', 'other', 'xmlhttprequest', 'media'] }, ['responseHeaders']);

function recentResponse(url) {
  const observed = responses.get(url);
  return observed && Date.now() - observed.seen <= RESPONSE_LIFETIME ? observed : undefined;
}

function downloadProof(item, manual) {
  const hint = recentResponse(browserUrl(item));
  if (hint ? !hint.replay : !manual) return null;
  const size = byteCount(item.fileSize) ?? byteCount(item.totalBytes);
  if (size !== undefined && hint?.length !== undefined && size !== hint.length) return null;
  const length = hint?.length ?? size;
  if (length === undefined && !hint?.etag) return null;
  return { length, etag: hint?.etag, contentType: hint?.contentType ?? mimeType(item.mime) };
}

function chromeFileName(item) {
  const name = item.filename?.split(/[\\/]/).pop();
  return name && !/\.crdownload$/i.test(name) ? name : undefined;
}

async function listChromeDownloads() {
  const downloads = await chrome.downloads.search({ state: 'in_progress', orderBy: ['-startTime'], limit: 25 });
  return { ok: true, downloads: downloads.filter(item => canMove(item) && downloadProof(item, true)).slice(0, 8).map(item => ({
    id: item.id, name: chromeFileName(item) || 'Download', paused: item.paused
  })) };
}

const sameDownload = (item, record) => !!item && item.startTime === record.startTime && !item.incognito;
const needsRecovery = record => ['parking', 'parked', 'import'].includes(record.stage);
const getChromeDownload = async id => (await chrome.downloads.search({ id }))[0];
const readBrowserRecords = async () => (await chrome.storage.local.get('browserDownloads')).browserDownloads || {};

async function recoverUncapturedChromeDownloads() {
  await captureStateReady;
  const [items, records] = await Promise.all([
    chrome.downloads.search({ state: 'in_progress', orderBy: ['-startTime'], limit: 25 }),
    readBrowserRecords()
  ]);
  const recoverable = items.filter(item => canMove(item) && !item.paused && !startedByVesper(item)
    && !browserOperations.has(item.id) && !sameDownload(item, records[item.id] || {}));
  await Promise.allSettled(recoverable.map(item => captureChromeDownload(item)));
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
      for (const [key, entry] of Object.entries(records))
        if (!needsRecovery(entry) && Date.now() - entry.at > 60 * 60_000) delete records[key];
      if (!records[id] && Object.keys(records).length >= MAX_BROWSER_RECORDS) {
        const expendable = Object.keys(records).find(key => !needsRecovery(records[key]));
        if (expendable === undefined) return false;
        delete records[expendable];
      }
      records[id] = { ...record, at: Date.now() };
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
const chromeKeeps = (message = 'Chrome is continuing this download.') => ({ ok: false, code: 'in-chrome', chromePaused: false, message });

async function armBrowserRecovery() {
  if (!(await chrome.alarms.get(RECOVERY_ALARM)))
    await chrome.alarms.create(RECOVERY_ALARM, { delayInMinutes: 1 });
}

function moveChromeDownload(id, mode, automatic = false) {
  if (!Number.isSafeInteger(id) || id < 0) return Promise.resolve(chromeKeeps('Choose a Chrome download.'));
  return inBrowserOrder(id, async () => {
    const item = await getChromeDownload(id);
    if (!canMove(item) || automatic && item.paused || !Number.isFinite(Date.parse(item.startTime)))
      return chromeKeeps('This Chrome download is unavailable to move.');
    const proof = downloadProof(item, !automatic);
    if (!proof) return chromeKeeps('Chrome is handling this file.');
    const previous = await browserRecord(item);
    if (previous?.stage === 'parked' || previous?.stage === 'parking') return { ok: true, chromePaused: item.paused };
    if (automatic && previous) return chromeKeeps();

    const record = { startTime: item.startTime, transferId: crypto.randomUUID(), stage: 'attempted' };
    if (!(await writeBrowserRecord(id, record))) return chromeKeeps();
    const { items, authorized } = await withSignIn([{
      url: browserUrl(item), fileName: chromeFileName(item),
      referer: item.referrer || undefined, userAgent: navigator.userAgent
    }]);
    const reply = await nativeRequest(envelope('browser-submit', { id: record.transferId, mode, items, authorized, browserProof: proof }), 24000);
    const result = reply.results?.[0];
    if (!reply.ok || result?.transferReady !== true) {
      // A delayed reply cannot publish a tentative download. Release the job if the helper answered.
      if (reply.ok) await transferCommand(record.transferId, 'abandon');
      return chromeKeeps(reply.ok ? undefined : reply.message || undefined);
    }

    const current = await getChromeDownload(id);
    if (!sameDownload(current, record) || !canMove(current) || browserUrl(current) !== browserUrl(item)
        || automatic && current.paused) {
      await transferCommand(record.transferId, 'abandon');
      return chromeKeeps();
    }
    record.stage = 'parking';
    record.resumeChrome = !current.paused;
    try {
      await writeBrowserRecord(id, record);
      await armBrowserRecovery();
      if (record.resumeChrome) await chrome.downloads.pause(id);
      const paused = await getChromeDownload(id);
      const canAccept = sameDownload(paused, record) && canMove(paused) && paused.paused
        && browserUrl(paused) === browserUrl(item);
      const accepted = canAccept ? await transferCommand(record.transferId, 'accept') : null;
      if (canAccept && accepted?.ok && ['active', 'complete'].includes(accepted.data?.state)) {
        record.stage = 'parked';
        await writeBrowserRecord(id, record);
        watchChromeDownload(id, record);
        return { ...reply, chromePaused: true };
      }
    } catch { /* The recovery record was written before changing Chrome. */ }
    await restoreChromeDownload(id, record);
    return chromeKeeps();
  });
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
  await writeBrowserRecord(id, { ...record, stage: 'attempted' });
}

async function finishBrowserHandoff(id, record) {
  const item = await getChromeDownload(id);
  if (sameDownload(item, record) && item.state === 'in_progress' && item.paused) await chrome.downloads.cancel(id);
  await writeBrowserRecord(id, { ...record, stage: 'done' });
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
  if (!sameDownload(item, record)) { await writeBrowserRecord(id, null); return; }
  if (item.state === 'complete') { await registerChromeDownload(item, current); return; }
  if (record.stage === 'import') { await writeBrowserRecord(id, null); return; }
  if (item.state === 'in_progress' && !item.paused) { await restoreChromeDownload(id, record); return; }
  if (record.stage === 'parking') {
    if (!canMove(item) || !item.paused || !(await transferCommand(record.transferId, 'accept')).ok) {
      await restoreChromeDownload(id, record);
      return;
    }
    record.stage = 'parked';
    await writeBrowserRecord(id, record);
  }
  const reply = knownReply || await transferCommand(record.transferId, 'status');
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
  await wakeChromeActivity(false);
}

function profileId() {
  profileIdPromise ??= (async () => {
    const { chromeDownloadProfile } = await chrome.storage.local.get('chromeDownloadProfile');
    if (typeof chromeDownloadProfile === 'string' && /^[a-z0-9-]{8,36}$/i.test(chromeDownloadProfile)) return chromeDownloadProfile;
    const id = crypto.randomUUID();
    await chrome.storage.local.set({ chromeDownloadProfile: id });
    return id;
  })().catch(error => { profileIdPromise = undefined; throw error; });
  return profileIdPromise;
}

function chromeOrigin(item) {
  for (const url of [browserUrl(item), item.referrer]) {
    try {
      const origin = new URL(url).origin;
      if (/^https?:\/\//i.test(origin)) return origin;
    } catch { /* A generated file can have no web origin. */ }
  }
  return undefined;
}

const downloadReceiptId = (item, profile) => `c-${profile}-${item.id.toString(36)}-${Date.parse(item.startTime).toString(36)}`;

async function registerChromeDownload(item, record) {
  if (record?.stage === 'done') return;
  if (!isSafe(item) || item.state !== 'complete' || item.exists === false || !item.filename ||
      byteCount(item.fileSize) === undefined || !Number.isFinite(Date.parse(item.startTime))) {
    if (record?.stage === 'import') await writeBrowserRecord(item.id, null);
    return;
  }
  if (!record?.transferId && !startedByVesper(item) && !(await settings()).intercept) { await writeBrowserRecord(item.id, null); return; }
  const attempt = { ...record, startTime: item.startTime, stage: 'import', tries: (record?.tries || 0) + 1 };
  if (!(await writeBrowserRecord(item.id, attempt))) return;
  await armBrowserRecovery();
  if (attempt.transferId && record.stage !== 'import') await transferCommand(attempt.transferId, 'abandon');
  const id = downloadReceiptId(item, await profileId());
  const reply = await nativeRequest(envelope('browser-completed', {
    id, path: item.filename, length: item.fileSize, origin: chromeOrigin(item), transferId: attempt.transferId
  }), 16000);
  const retry = !reply.ok && !['invalid', 'invalid-file', 'host-missing'].includes(reply.code) && attempt.tries < 3;
  if (!retry) await writeBrowserRecord(item.id, { ...attempt, stage: 'done' });
}

async function chromeActivity() {
  const [prefs, items, records] = await Promise.all([settings(),
    chrome.downloads.search({ state: 'in_progress', orderBy: ['-startTime'], limit: 32 }), readBrowserRecords()]);
  return items.filter(item => isSafe(item) && item.state === 'in_progress' && Number.isFinite(Date.parse(item.startTime))
    && (prefs.intercept || startedByVesper(item)) && !browserOperations.has(item.id)
    && !(sameDownload(item, records[item.id] || {}) && ['parking', 'parked', 'done'].includes(records[item.id].stage)));
}

async function wakeChromeActivity(startDesktop = true) {
  if (activityPort || openingActivity || !chrome.runtime.connectNative) return;
  openingActivity = true;
  try {
    let items = await chromeActivity();
    if (!items.length) return;
    const profile = await profileId();
    const port = chrome.runtime.connectNative(HOST);
    activityPort = port;
    let ended = false;
    function end() {
      ended = true;
      if (activityPort === port) activityPort = null;
      port.disconnect();
    }
    function report(operation) {
      port.postMessage(envelope('browser-activity', { profile, operation, files: items.map(item => ({
        id: downloadReceiptId(item, profile), name: (chromeFileName(item) || 'Chrome download').slice(0, 255),
        start: Date.parse(item.startTime), received: byteCount(item.bytesReceived) || 0,
        length: byteCount(item.totalBytes) ?? byteCount(item.fileSize), paused: item.paused === true, canResume: item.canResume === true
      })) }));
    }
    port.onMessage.addListener(async reply => {
      if (ended) return;
      if (!reply?.ok || items.length === 0) {
        end();
        if (reply?.code === 'app-unavailable') await armBrowserRecovery().catch(() => {});
        return;
      }
      for (const command of reply.data?.commands || []) {
        const expected = items.find(item => downloadReceiptId(item, profile) === command.id);
        if (!expected) continue;
        try {
          const current = await getChromeDownload(expected.id);
          if (!sameDownload(current, expected) || !isSafe(current) || current.state !== 'in_progress'
              || browserOperations.has(current.id)) continue;
          if (['parking', 'parked', 'done'].includes((await browserRecord(current))?.stage)) continue;
          if (command.action === 'pause' && !current.paused) await chrome.downloads.pause(current.id);
          else if (command.action === 'resume' && current.paused && current.canResume) await chrome.downloads.resume(current.id);
          else if (command.action === 'cancel') await chrome.downloads.cancel(current.id);
        } catch { /* Chrome may have finished while the command was in flight. */ }
      }
      try { items = await chromeActivity(); if (!ended) report('watch'); }
      catch { end(); await armBrowserRecovery().catch(() => {}); }
    });
    port.onDisconnect.addListener(() => {
      const error = chrome.runtime.lastError;
      if (!ended && items.length && !/not found|forbidden/i.test(error?.message || '')) void armBrowserRecovery().catch(() => {});
      ended = true;
      if (activityPort === port) activityPort = null;
    });
    report(startDesktop ? 'connect' : 'watch');
  } finally { openingActivity = false; }
}

async function captureChromeDownload(item) {
  await captureStateReady;
  if (item?.state === 'complete') {
    if (awaitingResponses.delete(item.id)) await saveCaptureState();
    await inBrowserOrder(item.id, async () => {
      const current = await getChromeDownload(item.id);
      if (current?.startTime === item.startTime) await registerChromeDownload(current, await browserRecord(current));
    });
    return;
  }
  if (!canMove(item) || item.paused || startedByVesper(item)) return;
  const url = browserUrl(item);
  if (!downloadProof(item, false)) {
    if (!recentResponse(url)) await rememberWaitingResponse(item, url);
    else if (awaitingResponses.delete(item.id)) await saveCaptureState();
    return;
  }
  if (awaitingResponses.delete(item.id)) await saveCaptureState();
  const prefs = await settings();
  if (prefs.intercept && prefs.route !== 'chrome') await moveChromeDownload(item.id, prefs.handoff === 'default' ? 'ask' : prefs.handoff, true);
}

chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === RECOVERY_ALARM) return recoverChromeDownloads().catch(() => {}); });
chrome.downloads.onCreated.addListener(item => captureChromeDownload(item).then(() => wakeChromeActivity()).catch(() => {}));
chrome.downloads.onChanged.addListener(async change => {
  try {
    if (change.paused?.current === false && change.state?.current !== 'complete') {
      await inBrowserOrder(change.id, async () => {
        const record = (await readBrowserRecords())[change.id];
        if (record?.stage === 'parked') await reconcileChromeDownload(change.id, record);
      });
    } else if (change.state?.current === 'complete' || ['safe', 'deepScannedSafe'].includes(change.danger?.current)
        || change.finalUrl || change.totalBytes) {
      await captureChromeDownload(await getChromeDownload(change.id));
    }
  } catch { /* The existing Chrome file or its recovery record is still available. */ }
  await wakeChromeActivity().catch(() => {});
});
