// One multiplexed native-messaging connection for VESPER's request/reply commands.
//
// chrome.runtime.sendNativeMessage starts a new host process for every message, and the
// media picker alone can verify up to nine sources at once. This module opens a single
// connectNative port on demand, sends every request over it with its own id, and closes
// the port after a short idle period. A connected native port keeps the service worker
// alive (Chrome 105+), so the idle close is what keeps VESPER at zero cost when unused.
//
// Nothing is retried here. A reply that is lost because the port closed is reported to
// the caller exactly like a crashed one-shot host, so callers keep their existing
// duplicate-safe recovery logic.
'use strict';
globalThis.VesperNativeSession = (() => {
  const IDLE_MS = 15_000;
  const HANDSHAKE_MS = 4000;
  const MAX_IN_FLIGHT = 12;
  const MAX_WAITING = 48;
  // Read-only discovery must never occupy every connection slot. A parked browser
  // download, cancellation or approved selection needs to reach the helper promptly.
  const MAX_LOOKUPS = MAX_IN_FLIGHT - 2;
  const LOOKUP_TYPES = new Set(['media-options','media-source-options','media-range-options','media-group-options']);
  const LOOKUP_WAIT_LIMIT = MAX_WAITING - 8;
  const timedOut = () => ({ ok: false, code: 'timeout', message: 'VESPER did not answer in time.' });
  const MISSING = /not found|forbidden|access to the specified native messaging host/i;
  let port = null;
  let opening = null;
  let multiplexed = null;          // null: unknown, true: host supports sessions, false: use one-shot messages
  let idleTimer = 0;
  let active = 0;
  let activeLookups = 0;
  const inFlight = new Map();      // request id -> settle(reply)
  const waiting = [];

  function oneShot(host, message, timeoutMs) {
    return new Promise(resolve => {
      let finished = false;
      const finish = value => {
        if (finished) return;
        finished = true;
        clearTimeout(timer);
        resolve(value);
      };
      const timer = setTimeout(() => finish({ ok: false, code: 'timeout', message: 'VESPER did not answer in time.' }), timeoutMs);
      try {
        chrome.runtime.sendNativeMessage(host, message, response => {
          const error = chrome.runtime.lastError;
          if (error) return finish({ ok: false, code: MISSING.test(error.message) ? 'host-missing' : 'host-error', message: error.message });
          finish(response || { ok: false, code: 'host-error', message: 'Empty reply from the VESPER connection.' });
        });
      } catch (error) {
        finish({ ok: false, code: 'host-error', message: String(error) });
      }
    });
  }

  function closePort() {
    clearTimeout(idleTimer);
    idleTimer = 0;
    const current = port;
    port = null;
    opening = null;
    if (current) {
      failInFlight({ ok: false, code: 'host-error', message: 'The VESPER connection closed.' });
      try { current.disconnect(); } catch { /* Already closed. */ }
    }
  }

  function scheduleIdle() {
    clearTimeout(idleTimer);
    idleTimer = 0;
    if (!port || active || waiting.length) return;
    idleTimer = setTimeout(() => {
      if (!active && !waiting.length) closePort();
    }, IDLE_MS);
  }

  function failInFlight(reply) {
    for (const settle of [...inFlight.values()]) settle(reply);
  }

  // The handshake is a local reply from the host process; it never wakes the desktop app.
  // An older host answers "Unknown request type" and exits, which selects one-shot messages.
  function open(host) {
    if (opening) return opening;
    opening = new Promise(resolve => {
      let decided = false;
      let connection;
      const decide = value => {
        if (decided) return;
        decided = true;
        clearTimeout(timer);
        resolve(value);
      };
      const unsupported = () => {
        multiplexed = false;
        decide({ ok: false, code: 'unsupported' });
        try { connection?.disconnect(); } catch { /* Already closed. */ }
      };
      const timer = setTimeout(unsupported, HANDSHAKE_MS);
      try { connection = chrome.runtime.connectNative(host); }
      catch (error) {
        multiplexed = false;
        decide({ ok: false, code: 'host-error', message: String(error) });
        return;
      }
      const handshakeId = crypto.randomUUID();
      connection.onMessage.addListener(reply => {
        if (!decided) {
          if (reply?.ok === true && reply.id === handshakeId && reply.data?.mux === true) {
            multiplexed = true;
            port = connection;
            decide({ ok: true });
          } else unsupported();
          return;
        }
        if (port !== connection || typeof reply?.id !== 'string') return;
        inFlight.get(reply.id)?.(reply);
      });
      connection.onDisconnect.addListener(() => {
        const error = chrome.runtime.lastError;
        const missing = MISSING.test(error?.message || '');
        if (!decided) {
          if (missing) decide({ ok: false, code: 'host-missing', message: error.message });
          else unsupported();
          return;
        }
        if (port !== connection) return;
        port = null;
        opening = null;
        clearTimeout(idleTimer);
        failInFlight({ ok: false, code: missing ? 'host-missing' : 'host-error',
          message: 'The VESPER connection closed before it answered.' });
      });
      try { connection.postMessage({ v: 1, id: handshakeId, type: 'session-open' }); }
      catch { unsupported(); }
    }).then(result => {
      if (!result.ok) opening = null;
      return result;
    });
    return opening;
  }

  function sendOverPort(job, remainingMs) {
    const connection = port;
    const id = job.message?.id;
    if (!connection) return Promise.resolve(null);
    if (typeof id !== 'string' || inFlight.has(id))
      return Promise.resolve({ ok: false, code: 'invalid', message: 'Each native request needs a unique id.' });
    return new Promise(resolve => {
      const settle = reply => {
        if (inFlight.get(id) !== settle) return;
        inFlight.delete(id);
        clearTimeout(timer);
        resolve(reply);
      };
      const timer = setTimeout(() => settle({ ok: false, code: 'timeout', message: 'VESPER did not answer in time.' }), remainingMs);
      inFlight.set(id, settle);
      try { connection.postMessage(job.message); }
      catch { settle({ ok: false, code: 'host-error', message: 'The VESPER connection closed.' }); }
    });
  }

  async function dispatch(job) {
    const remaining = () => job.deadline - Date.now();
    if (remaining() <= 0) return timedOut();
    if (multiplexed !== false && typeof chrome.runtime.connectNative === 'function') {
      const opened = await open(job.host);
      if (remaining() <= 0) return timedOut();
      if (opened.ok) {
        const reply = await sendOverPort(job, remaining());
        if (reply) return reply;
      } else if (opened.code !== 'unsupported') return opened;
    }
    return remaining() > 0 ? oneShot(job.host, job.message, remaining()) : timedOut();
  }

  function pump() {
    clearTimeout(idleTimer);
    idleTimer = 0;
    while (waiting.length && active < MAX_IN_FLIGHT) {
      // Preserve FIFO within each class, but dispatch actual user actions and
      // ownership controls ahead of queued, read-only format discovery.
      let index = waiting.findIndex(job => !job.lookup);
      if (index < 0 && activeLookups < MAX_LOOKUPS) index = 0;
      if (index < 0) break;
      const [job] = waiting.splice(index, 1);
      clearTimeout(job.timer);
      active++;
      if (job.lookup) activeLookups++;
      dispatch(job).then(job.resolve, error => job.resolve({ ok: false, code: 'host-error', message: String(error) }))
        .finally(() => { active--; if (job.lookup) activeLookups--; pump(); });
    }
    scheduleIdle();
  }

  function request(host, message, timeoutMs = 330_000) {
    if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) return Promise.resolve(timedOut());
    const lookup = LOOKUP_TYPES.has(message?.type);
    if (waiting.length >= MAX_WAITING || lookup && waiting.length >= LOOKUP_WAIT_LIMIT)
      return Promise.resolve({ ok: false, code: 'busy', message: 'VESPER has too many pending requests.' });
    return new Promise(resolve => {
      const job = { host, message, lookup, resolve, deadline: Date.now() + timeoutMs, timer: 0 };
      job.timer = setTimeout(() => {
        const index = waiting.indexOf(job);
        if (index < 0) return;
        waiting.splice(index, 1);
        resolve(timedOut());
        pump();
      }, timeoutMs);
      waiting.push(job);
      pump();
    });
  }

  return Object.freeze({ request, close: closePort,
    snapshot: () => ({ connected: !!port, multiplexed, active, waiting: waiting.length }) });
})();
