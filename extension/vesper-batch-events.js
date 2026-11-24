// Chrome-owned download event correlation for user-approved Smart Select automation.
// Does not start, cancel, pause, delete, rename, or claim any Chrome download.
// No polling when idle. A worker restart restores in-flight watches from session storage.
'use strict';
globalThis.VesperBatchEvents = (() => {
  const KEY = 'vesperApprovedBatchWatchesV1';
  const MAX_WATCHES = 64;
  const JOURNAL_KEY = 'vesperGuidedJournalsV2';
  const JOURNAL_TTL = 2 * 60 * 60_000;
  const journals = new Map();
  const journalStates = new Set(['approved','activating','waiting','ready','action-recorded','reconciling',
    'submitted','verified-native','verified-chrome','needs-input','indeterminate','failed-confirmed','cancelled']);
  const unfinishedStates = new Set(['activating','waiting','ready','action-recorded','reconciling','indeterminate']);
  const unresolved = record => record?.steps.some(step => unfinishedStates.has(step.state));
  const transitions = {
    approved: ['activating','cancelled'],
    activating: ['waiting','needs-input','indeterminate','cancelled'],
    waiting: ['ready','needs-input','indeterminate','cancelled'],
    ready: ['action-recorded','needs-input','indeterminate','cancelled'],
    'action-recorded': ['reconciling','indeterminate','cancelled'],
    reconciling: ['submitted','verified-native','verified-chrome','failed-confirmed','indeterminate']
  };
  function canUpdateJournal(previous, next) {
    return previous.steps.length === next.steps.length && previous.steps.every((step, index) => {
      const replacement = next.steps[index];
      return step.key === replacement.key && (step.state === replacement.state
        || transitions[step.state]?.includes(replacement.state));
    });
  }
  const journalLoad = chrome.storage.session.get(JOURNAL_KEY).then(value => {
    for (const [key, record] of Object.entries(value[JOURNAL_KEY] || {}))
      if (record?.expires > Date.now() && Array.isArray(record.steps)) journals.set(key, record);
  });
  let journalWrites = Promise.resolve();
  function journalCommand(type, message, sender) {
    const tabId = sender.tab?.id, frameId = sender.frameId ?? 0, documentId = sender.documentId;
    if (sender.tab?.incognito || !globalThis.VesperIntelligence.safeScope({tabId,frameId,documentId}))
      return Promise.resolve({ok:false,code:'invalid'});
    const key = JSON.stringify([tabId,frameId,documentId]);
    const operation = journalWrites.catch(() => {}).then(async () => {
      await journalLoad;
      for (const [k,v] of journals) if (v.expires <= now()) journals.delete(k);
      const previous = journals.get(key);
      if (type === 'batch-journal-read') return {ok:true,record:previous || null};
      if (type === 'batch-journal-ack') {
        if (previous?.id !== message.id) return {ok:false,code:'missing'};
        journals.delete(key);
      } else {
        const value = message.record;
        if (!validId(value?.id) || !Array.isArray(value.steps) || !value.steps.length || value.steps.length > 50
            || value.steps.some((step,index) => step?.index !== index || !/^[\w-]{1,160}$/.test(step.key || '')
              || !journalStates.has(step.state))) return {ok:false,code:'invalid'};
        if (new Set(value.steps.map(step => step.key)).size !== value.steps.length) return {ok:false,code:'invalid'};
        if (previous && previous.id !== value.id && unresolved(previous)) return {ok:false,code:'unresolved'};
        if (previous?.id === value.id && !canUpdateJournal(previous, value)) return {ok:false,code:'stale-journal'};
        // No labels, signed URLs, or page text persisted. This is a replay guard, not proof of ownership.
        if (!previous && journals.size >= 32) return {ok:false,code:'busy'};
        journals.set(key,{id:value.id,expires:now()+JOURNAL_TTL,steps:value.steps.map(({index,key,state})=>({index,key,state}))});
      }
      await chrome.storage.session.set({[JOURNAL_KEY]:Object.fromEntries(journals)});
      return {ok:true};
    });
    journalWrites = operation;
    return operation;
  }
  const TTL = 90_000;
  const ACTIVE_TTL = 2 * 60 * 60_000;
  const watchers = new Map();
  const watchKey = record => `${record.batchId}:${record.itemId}`;
  const recentRequests = []; // ephemeral cross-tab ambiguity guard, never persisted
  let writes = Promise.resolve();
  const now = () => Date.now();
  const validId = s => typeof s === 'string' && /^[\w-]{12,80}$/.test(s);
  const same = (a, b) => typeof a === 'string' && typeof b === 'string' && a.split('#', 1)[0] === b.split('#', 1)[0];
  const load = chrome.storage.session.get(KEY).then(value => {
    for (const record of Object.values(value[KEY] || {})) {
      if (validId(record?.batchId) && Number.isSafeInteger(record.tabId) && record.tabId >= 0
          && Number.isFinite(record.expires) && record.expires > now()) {
        watchers.set(watchKey(record), record);
      }
    }
  }).catch(() => {});
  function prune() {
    for (const [key, record] of watchers) if (record.expires <= now()) watchers.delete(key);
    // Never evict unresolved or active download evidence to make room for new work.
    while (watchers.size > MAX_WATCHES) {
      const terminal = [...watchers].find(([, record]) => record.completed || record.nativeCompleted);
      if (!terminal) break;
      watchers.delete(terminal[0]);
    }
  }
  function persist() {
    writes = writes.catch(() => {}).then(async () => {
      await load;
      prune();
      const result = {};
      for (const [id, value] of watchers) result[id] = value;
      await chrome.storage.session.set({ [KEY]: result });
    });
    return writes;
  }
  function view(record) {
    if (!record) return { ok: false, code: 'missing' };
    return { ok: true, batchId: record.batchId, itemId: record.itemId, status: record.status,
      matched: record.matched === true, downloadId: record.downloadId ?? null,
      danger: record.danger || 'unknown', completed: record.completed === true,
      nativeCompleted: record.nativeCompleted === true && record.completed !== true };
  }
  async function arm(message, sender) {
    await load; prune();
    const expected = globalThis.VesperIntelligence.http(message.expectedUrl);
    const tabId = sender.tab?.id;
    if (!validId(message.batchId) || !validId(message.itemId) || !expected
        || !globalThis.VesperIntelligence.safeScope({ tabId, frameId: sender.frameId ?? 0, documentId: sender.documentId })
        || !Number.isSafeInteger(tabId) || tabId < 0 || sender.tab?.incognito === true
        || !/^https?:/i.test(sender.url || ''))
      return { ok: false, code: 'invalid', message: 'Approved batch scope is invalid.' };
    // An existing action cannot be replaced or armed twice, even in the same batch.
    const existing = watchers.get(watchKey(message));
    if (existing) return { ok: false, code: 'busy', message: 'This batch action already has a watch.' };
    if ([...watchers.values()].some(entry => entry.tabId === tabId && entry.status === 'armed'))
      return { ok: false, code: 'busy', message: 'Another VESPER batch action is pending in this tab.' };
    if (watchers.size >= MAX_WATCHES) {
      const terminal = [...watchers].find(([, record]) => record.completed || record.nativeCompleted);
      if (!terminal) return { ok: false, code: 'busy', message: 'Too many downloads are awaiting their outcome.' };
      watchers.delete(terminal[0]);
    }
    const record = { batchId: message.batchId, itemId: message.itemId, tabId,
      frameId: sender.frameId ?? 0, documentId: sender.documentId || '',
      expectedUrl: expected.href, expires: now() + TTL, createdAt: now(), status: 'armed',
      matched: false, danger: 'unknown', completed: false };
    watchers.set(watchKey(record), record);
    await persist();
    return view(record);
  }
  async function query(message, sender) {
    await load; prune();
    if (!validId(message.batchId) || !validId(message.itemId)) return { ok: false, code: 'invalid' };
    const record = watchers.get(`${message.batchId}:${message.itemId}`);
    if (!record || record.tabId !== sender.tab?.id || record.frameId !== (sender.frameId ?? 0)
        || record.documentId !== sender.documentId || record.itemId !== message.itemId)
      return { ok: false, code: 'missing' };
    return view(record);
  }
  async function clear(message, sender) {
    await load;
    if (!validId(message.batchId)) return { ok: false, code: 'invalid' };
    for (const [key, record] of watchers)
      if (record.batchId === message.batchId && record.tabId === sender.tab?.id
          && record.frameId === (sender.frameId ?? 0) && record.documentId === sender.documentId) watchers.delete(key);
    await persist();
    return { ok: true };
  }
  async function disarm(message, sender) {
    await load;
    if (!validId(message.batchId) || !validId(message.itemId)) return { ok: false, code: 'invalid' };
    const key = `${message.batchId}:${message.itemId}`;
    const record = watchers.get(key);
    if (!record || record.tabId !== sender.tab?.id || record.frameId !== (sender.frameId ?? 0)
        || record.documentId !== sender.documentId) return { ok: false, code: 'missing' };
    if (record.status !== 'armed') return { ok: true, retained: true, status: record.status };
    watchers.delete(key);
    await persist();
    return { ok: true, status: 'disarmed' };
  }
  // Only a same-tab/frame request witnessed after arming can support attribution.
  // DownloadItem alone has no authoritative tab/frame identity.
  chrome.webRequest.onBeforeRequest.addListener(details => {
    // Do not miss the first request after a sleeping service worker wakes.
    void load.then(() => observeRequest(details)).catch(() => {});
  }, { urls: ['http://*/*', 'https://*/*'], types: ['main_frame', 'sub_frame', 'other'] });
  function observeRequest(details) {
    if (watchers.size === 0 || !Number.isSafeInteger(details.tabId) || details.tabId < 0
        || typeof details.requestId !== 'string' || !details.requestId) return;
    const current = now();
    const at = Number.isFinite(details.timeStamp) ? details.timeStamp : current;
    if (at > current + 1000 || at < current - TTL) return;
    const armed = [...watchers.values()].filter(record => record.status === 'armed'
      && record.expires > current && at >= record.createdAt);
    if (!armed.length) return;
    const url = globalThis.VesperIntelligence.http(details.url)?.href;
    if (!url) return;
    // A competing request may target a redirect destination before that
    // destination is known. Keep only a small, ephemeral active-window ledger.
    recentRequests.push({ requestId: details.requestId, url,
      tabId: details.tabId, frameId: details.frameId, at });
    let changed = false;
    while (recentRequests.length > 100) {
      const removed = recentRequests.shift();
      for (const record of armed) {
        if (removed.at >= record.createdAt && !record.redirectHistoryIncomplete) {
          record.redirectHistoryIncomplete = true;
          changed = true;
        }
      }
    }
    for (const record of armed) {
      const expected = same(url, record.expectedUrl);
      const redirected = same(url, record.requestWitness?.url);
      if (!expected && !redirected) continue;
      if (redirected && !expected && record.firstRequestId !== details.requestId) {
        record.ambiguous = true;
        changed = true;
        continue;
      }
      // Keep the ambiguity for the entire approved window, even if a countdown
      // outlives the short recent-request ledger or the worker goes to sleep.
      const correctScope = record.tabId === details.tabId && record.frameId === details.frameId
        && (!details.documentId || details.documentId === record.documentId);
      if (!correctScope || record.firstRequestId && record.firstRequestId !== details.requestId) {
        record.ambiguous = true;
        changed = true;
        continue;
      }
      if (!record.firstRequestId) record.firstRequestId = details.requestId;
      if (!record.ambiguous) {
        record.requestWitness = { requestId: details.requestId, at, url: details.url };
        changed = true;
      }
    }
    if (changed) void persist().catch(() => {});
  }
  chrome.webRequest.onBeforeRedirect.addListener(details => {
    void load.then(() => observeRedirect(details)).catch(() => {});
  }, { urls: ['http://*/*', 'https://*/*'], types: ['main_frame', 'sub_frame', 'other'] });
  function observeRedirect(details) {
    let changed = false;
    for (const recent of recentRequests)
      if (recent.requestId === details.requestId && globalThis.VesperIntelligence.http(details.redirectUrl))
        recent.url = globalThis.VesperIntelligence.http(details.redirectUrl).href;
    for (const record of watchers.values()) {
      if (record.status !== 'armed' || record.requestWitness?.requestId !== details.requestId) continue;
      const next = globalThis.VesperIntelligence.http(details.redirectUrl);
      if (next) {
        record.requestWitness.url = next.href;
        if (record.redirectHistoryIncomplete || recentRequests.some(request => request.requestId !== details.requestId
            && request.at >= record.createdAt && same(request.url, next.href))) record.ambiguous = true;
        changed = true;
      }
    }
    if (changed) void persist().catch(() => {});
  }
  chrome.downloads.onCreated.addListener(item => {
    // Chrome's DownloadItem lacks a trustworthy originating tab or player identifier.
    // Only exact user-approved expected URLs count as attributed starts.
    void (async () => {
      await load; prune();
      const matches = [...watchers.values()].filter(record => record.status === 'armed' && !record.ambiguous
        && item.incognito !== true && (!Number.isSafeInteger(item.tabId) || item.tabId === record.tabId)
        && record.requestWitness && record.requestWitness.at >= record.createdAt
        && (same(item.url, record.expectedUrl) || same(item.finalUrl, record.expectedUrl)
          || same(item.url, record.requestWitness.url) || same(item.finalUrl, record.requestWitness.url)));
      if (matches.length !== 1) return;
      const record = matches[0];
      // Two tabs downloading the same URL at once cannot be distinguished by DownloadItem.
      // The safe result is unattributed rather than pretending to know which tab owns it.
      const collision = recentRequests.some(request => request.requestId !== record.requestWitness.requestId
        && request.at >= record.createdAt - 1000 && now() - request.at <= TTL
        && (same(request.url, item.url) || same(request.url, item.finalUrl)));
      if (collision) return;
      record.downloadId = item.id;
      record.downloadStartTime = typeof item.startTime === 'string' ? item.startTime : null;
      record.expires = now() + ACTIVE_TTL;
      delete record.expectedUrl; // No signed URL is needed once Chrome assigned the download ID.
      delete record.requestWitness;
      delete record.firstRequestId;
      delete record.ambiguous;
      delete record.redirectHistoryIncomplete;
      record.status = item.state === 'complete' ? 'chrome-completed' : 'download-observed';
      record.matched = true;
      record.danger = item.danger || 'unknown';
      record.completed = item.state === 'complete';
      await persist();
    })().catch(() => {});
  });
  chrome.downloads.onChanged.addListener(delta => {
    void (async () => {
      await load;
      const record = [...watchers.values()].find(entry => entry.downloadId === delta.id);
      if (!record) return;
      if (delta.danger?.current) record.danger = delta.danger.current;
      if (delta.state?.current === 'complete') {
        record.completed = true; record.nativeCompleted = false; record.status = 'chrome-completed';
        record.expires = now() + 5 * 60_000;
      } else if (delta.state?.current === 'interrupted') {
        // Browser cancellation can indicate VESPER ownership, user cancellation, or a failure.
        // It is NOT proof that VESPER acquired a correct file.
        if (!record.nativeCompleted) record.status = 'ownership-unresolved';
      }
      await persist();
    })().catch(() => {});
  });
  // Internal capture-engine notification only; never exposed as a page command.
  async function noteNativeCompletion(id, startTime) {
    await load; prune();
    if (!Number.isSafeInteger(id) || typeof startTime !== 'string' || !startTime) return false;
    const matching = [...watchers.values()].filter(entry => entry.downloadId === id
      && entry.downloadStartTime === startTime && entry.matched && !entry.completed
      && ['safe', 'deepScannedSafe'].includes(entry.danger));
    if (matching.length !== 1) return false;
    const record = matching[0];
    record.nativeCompleted = true;
    record.status = 'native-completed';
    record.expires = now() + 5 * 60_000;
    await persist();
    return true;
  }
  return Object.freeze({ noteNativeCompletion, handle: (type, message, sender) => {
    if (type.startsWith('batch-journal-')) return journalCommand(type, message, sender);
    if (type === 'batch-arm') return arm(message, sender);
    if (type === 'batch-status') return query(message, sender);
    if (type === 'batch-clear') return clear(message, sender);
    if (type === 'batch-disarm') return disarm(message, sender);
    return Promise.resolve({ ok: false, code: 'unknown' });
  }});
})();
