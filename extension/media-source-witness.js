// Page-world media provenance. No body is copied, intercepted or retained.
// A response body object can sometimes be recognized when it becomes an object
// URL or is appended to an MSE SourceBuffer. A player-scoped path hint is useful for
// ranking, but is never authority to download or proof of the complete asset.
(() => {
  'use strict';
  if (typeof MediaSource !== 'function' || typeof SourceBuffer !== 'function'
      || typeof WeakRef !== 'function' || typeof Response !== 'function') return;
  const urls = new Map();
  const owners = new WeakMap();
  const buffers = new WeakMap();
  const bufferSlots = [];
  const MAX_SOURCES = 24, MAX_BUFFERS = 96, MAX_PATHS = 12, TTL = 120_000;
  let observingResponses = false, pendingHashes = 0;
  const apply = Reflect.apply;
  const now = Date.now.bind(Date);

  function replace(object, name, wrapper) {
    try {
      const descriptor = Object.getOwnPropertyDescriptor(object, name);
      if (!descriptor || typeof descriptor.value !== 'function') return false;
      Object.defineProperty(object, name, { ...descriptor, value: wrapper(descriptor.value) });
      return true;
    } catch { return false; }
  }
  function pathOf(address, mime) {
    try {
      const url = new URL(address);
      if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) return null;
      mime ||= '';
      if (!/^(?:video\/|audio\/|application\/(?:octet-stream|mp4))/i.test(mime)
          && !/\.(?:m4s|cmfv|cmfa|ts|aac|mp4|webm)$/i.test(url.pathname)) return null;
      url.hash = '';
      const path = url.origin + url.pathname;
      return path.length <= 2048 && url.href.length <= 4096 ? { path, address: url.href } : null;
    } catch { return null; }
  }
  function rememberBuffer(buffer, source) {
    if (!source || !(buffer instanceof ArrayBuffer || typeof Blob === 'function' && buffer instanceof Blob)) return;
    const { path, address } = source;
    const previous = buffers.get(buffer);
    if (previous?.path === path && now() - previous.at < TTL) return;
    const evidence = { path, at: now(), fingerprint: null };
    buffers.set(buffer, evidence);
    // Hash the exact URL, including its query, without keeping or exporting it.
    // The asynchronous digest never blocks playback, and outstanding work is capped.
    if (pendingHashes < 16 && typeof TextEncoder === 'function' && globalThis.crypto?.subtle) {
      try {
        const pending = globalThis.crypto.subtle.digest('SHA-256', new TextEncoder().encode(address));
        pendingHashes++;
        pending.then(value => {
          evidence.fingerprint = Array.from(new Uint8Array(value), byte => byte.toString(16).padStart(2, '0')).join('');
        }, () => {}).finally(() => pendingHashes--).catch(() => {});
      } catch { /* Nonsecure origins can still use path evidence. */ }
    }
    bufferSlots.push({ ref: new WeakRef(buffer), evidence });
    while (bufferSlots.length > MAX_BUFFERS) {
      const old = bufferSlots.shift(), key = old.ref.deref();
      if (key && buffers.get(key) === old.evidence) buffers.delete(key);
    }
  }

  function watchXhr() {
    if (typeof XMLHttpRequest !== 'function') return;
    try {
      const descriptor = Object.getOwnPropertyDescriptor(XMLHttpRequest.prototype, 'response');
      if (!descriptor?.get || !descriptor.configurable) return;
      Object.defineProperty(XMLHttpRequest.prototype, 'response', {
        ...descriptor,
        get() {
          // The page itself reads the response. No duplicate read or request.
          const value = apply(descriptor.get, this, []);
          try {
            if (this.readyState === 4 && (this.responseType === 'arraybuffer' || this.responseType === 'blob')
                && this.status >= 200 && this.status < 300)
              rememberBuffer(value, pathOf(this.responseURL, this.getResponseHeader('content-type')));
          } catch { /* Do not change the result of the native getter. */ }
          return value;
        }
      });
    } catch { /* Some environments prevent property replacement. */ }
  }

  function watchResponses() {
    if (observingResponses) return;
    observingResponses = true;
    watchXhr();
    for (const method of ['arrayBuffer', 'blob']) replace(Response.prototype, method, original => function (...args) {
      // Preserve the original promise and its result. Only metadata survives.
      const promise = apply(original, this, args);
      try {
        const path = pathOf(this.url, this.headers.get('content-type'));
        if (path) promise.then(buffer => rememberBuffer(buffer, path), () => {}).catch(() => {});
      } catch { /* Optional observation. */ }
      return promise;
    });
  }
  replace(URL, 'createObjectURL', original => function (...args) {
    const url = apply(original, this, args);
    try {
      if (args[0] instanceof MediaSource) {
        urls.set(url, { ref: new WeakRef(args[0]), paths: new Map() });
        while (urls.size > MAX_SOURCES) urls.delete(urls.keys().next().value);
        watchResponses();
      } else if (typeof Blob === 'function' && args[0] instanceof Blob) {
        const evidence = buffers.get(args[0]);
        if (evidence && now() - evidence.at <= TTL) {
          urls.set(url, { ref: new WeakRef(args[0]), paths: new Map([[evidence, now()]]) });
          while (urls.size > MAX_SOURCES) urls.delete(urls.keys().next().value);
        }
      }
    } catch { /* Do not affect page playback. */ }
    return url;
  });
  replace(MediaSource.prototype, 'addSourceBuffer', original => function (...args) {
    const buffer = apply(original, this, args);
    try { owners.set(buffer, new WeakRef(this)); watchResponses(); } catch {}
    return buffer;
  });
  replace(SourceBuffer.prototype, 'appendBuffer', original => function (...args) {
    const result = apply(original, this, args);
    try {
      const input = args[0];
      const buffer = ArrayBuffer.isView(input) ? input.buffer : input;
      const evidence = buffers.get(buffer);
      const owner = owners.get(this)?.deref();
      if (owner && evidence && now() - evidence.at <= TTL) {
        // Never retain the buffer. Match the exact object, not its length.
        for (const record of urls.values()) {
          if (record.ref.deref() !== owner) continue;
          record.paths.delete(evidence);
          record.paths.set(evidence, now());
          while (record.paths.size > MAX_PATHS) record.paths.delete(record.paths.keys().next().value);
        }
      }
    } catch { /* Observation is optional and never blocks append. */ }
    return result;
  });
  // Players may fetch media before creating the MediaSource or Blob URL.
  // Observe the original promise only; never read a response a second time.
  watchResponses();
  document.addEventListener('vesper-media-provenance-request', event => {
    const player = event.target;
    if (!(player instanceof HTMLMediaElement)) return;
    try {
      const url = player.currentSrc || player.src;
      const record = urls.get(url);
      if (!record?.ref.deref()) return;
      const at = now();
      const paths = [...record.paths].filter(([, seen]) => at - seen <= TTL)
        .map(([evidence, seen]) => ({ path: evidence.path, seen,
          ...(evidence.fingerprint ? { fingerprint: evidence.fingerprint } : {}) }));
      player.setAttribute('data-vesper-media-provenance', JSON.stringify({ paths }));
    } catch { /* The isolated script can continue without this hint. */ }
  }, true);
})();
