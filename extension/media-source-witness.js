// Page-world media provenance. No complete body is copied, intercepted or retained.
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
  // Inspect only bytes the page already read. Keep two short, distinctive
  // samples from an MP4 media-data box, not the body or any encryption keys.
  // Equal-length, zero-filled or ambiguous matches never become provenance.
  function distinctiveSample(bytes, start, end, kind) {
    if (end - start < 128) return null;
    const first = bytes.slice(start, start + 32), last = bytes.slice(end - 32, end);
    return new Set(first).size >= 12 && new Set(last).size >= 12
      && first.some((value, j) => value !== last[j])
      ? {kind, length: end - start, first, last} : null;
  }
  function equalSample(a, b) {
    return !!a && !!b && a.kind === b.kind && a.length === b.length
      && a.first.every((value, i) => value === b.first[i])
      && a.last.every((value, i) => value === b.last[i]);
  }
  function mediaSample(input) {
    try {
      const bytes = ArrayBuffer.isView(input)
        ? new Uint8Array(input.buffer, input.byteOffset, input.byteLength)
        : input instanceof ArrayBuffer ? new Uint8Array(input) : null;
      if (!bytes || bytes.length < 136) return null;
      const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
      // Whole copied WebM clusters and MPEG-TS packet sequences can provide
      // the same non-authoritative hint as a copied MP4 mdat. No decoder or
      // whole-body hash; require recognizable framing and distinctive samples.
      const signature = view.getUint32(0);
      if (signature === 0x1f43b675 || signature === 0x1a45dfa3)
        return distinctiveSample(bytes, 0, bytes.length, 'webm');
      if (bytes.length >= 188 * 4 && bytes.length % 188 === 0
          && [0,188,376,564,bytes.length-188].every(i => bytes[i] === 0x47 && (bytes[i+3] & 0x30) !== 0))
        return distinctiveSample(bytes, 0, bytes.length, 'ts');
      let offset = 0, sample = null;
      for (let i = 0; i < 128 && offset + 8 <= bytes.length; i++) {
        let size = view.getUint32(offset), header = 8;
        const type = view.getUint32(offset + 4);
        if (size === 1) {
          if (offset + 16 > bytes.length || view.getUint32(offset + 8) !== 0) return null;
          size = view.getUint32(offset + 12); header = 16;
        } else if (size === 0) size = bytes.length - offset;
        if (size < header || size > bytes.length - offset) return null;
        if (type === 0x6d646174 && size - header >= 128) {
          sample = distinctiveSample(bytes, offset + header, offset + size, 'mp4');
        }
        offset += size;
      }
      return offset === bytes.length ? sample : null;
    } catch { return null; }
  }
  function matchingSample(input) {
    const sample = mediaSample(input);
    if (!sample) return null;
    const at = now();
    let match = null;
    for (const { evidence } of bufferSlots) {
      const prior = evidence.sample;
      if (at - evidence.at > TTL || !equalSample(sample, prior)) continue;
      // Even two matching samples cannot distinguish different requests.
      if (match && match !== evidence) return null;
      match = evidence;
    }
    return match && (match.sampleHint ||= { path: match.path, at: match.at, sampleOnly: true });
  }

  function rememberBuffer(buffer, source) {
    if (!source || !(buffer instanceof ArrayBuffer || typeof Blob === 'function' && buffer instanceof Blob)) return;
    const { path, address } = source;
    const previous = buffers.get(buffer);
    if (previous?.path === path && now() - previous.at < TTL) return;
    const evidence = { path, at: now(), fingerprint: null, sample: mediaSample(buffer) };
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
      const exact = buffers.get(buffer);
      // A typed view over a larger response does not prove its whole contents.
      const full = !ArrayBuffer.isView(input) || input.byteOffset === 0 && input.byteLength === buffer.byteLength;
      const exactUnchanged = full && exact && (!exact.sample || equalSample(exact.sample, mediaSample(input)));
      const evidence = exactUnchanged ? exact : matchingSample(input);
      const owner = owners.get(this)?.deref();
      if (owner && evidence && now() - evidence.at <= TTL) {
        // Never retain the buffer. Exact-object and sample-only evidence stay distinct.
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
      if (!record?.ref.deref() || player.mediaKeys) return;
      const at = now();
      const paths = [...record.paths].filter(([, seen]) => at - seen <= TTL)
        .map(([evidence, seen]) => ({ path: evidence.path, seen,
          ...(evidence.fingerprint ? { fingerprint: evidence.fingerprint } : {}) }));
      player.setAttribute('data-vesper-media-provenance', JSON.stringify({ paths }));
    } catch { /* The isolated script can continue without this hint. */ }
  }, true);
})();
