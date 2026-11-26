// MAIN-world universal media provenance, MSE SourceBuffer reconstruction, byte-slice association,
// shadow-DOM player discovery, and live player-config inspection across any website.
(() => {
  'use strict';
  if (globalThis.__vesperMediaSourceWitnessV2) return;
  globalThis.__vesperMediaSourceWitnessV2 = true;

  if (typeof WeakRef !== 'function') return;

  const MAX_SOURCES = 24;
  const MAX_BUFFERS = 256;
  const MAX_URLS_PER_TRACK = 64;
  const MAX_INIT_BYTES = 256 * 1024;
  const MAX_TRACK_BUFFER_BYTES = 24 * 1024 * 1024;
  const MAX_TOTAL_MSE_BYTES = 64 * 1024 * 1024;
  const TTL = 120_000;
  const apply = Reflect.apply;
  const now = Date.now.bind(Date);

  const objectUrls = new Map();       // blobUrl -> { ref: WeakRef<MediaSource|Blob>, createdAt, tracks: Set<TrackState>, paths: Map }
  const sourceOwners = new WeakMap(); // SourceBuffer -> TrackState
  const mediaSources = new WeakMap(); // MediaSource -> tracks
  const bufferProvenance = new WeakMap(); // ArrayBuffer | Blob -> ProvenanceRecord
  const streamProvenance = new WeakMap(); // ReadableStream -> SourceDescriptor
  const readerProvenance = new WeakMap(); // ReadableStreamDefaultReader -> SourceDescriptor
  const requestMeta = new WeakMap();      // XMLHttpRequest -> { method, url, rangeHeader }
  const bufferRing = [];
  const retainedMseOrder = [];
  let totalRetainedBytes = 0;
  let pendingDigests = 0;

  const SLICE_QUERY_KEYS = new Set(['bytestart', 'byteend', 'range', 'sq', 'segment', 'seg', 'chunk', 'frag', 'part']);

  function replace(object, name, wrapper) {
    try {
      const descriptor = Object.getOwnPropertyDescriptor(object, name);
      if (!descriptor || typeof descriptor.value !== 'function') return false;
      Object.defineProperty(object, name, { ...descriptor, value: wrapper(descriptor.value) });
      return true;
    } catch { return false; }
  }

  function parseHttpUrl(address) {
    if (typeof address !== 'string' || !address || address.length > 4096 || /[\r\n\0]/.test(address)) return null;
    try {
      const url = new URL(address, globalThis.location?.href);
      if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) return null;
      url.hash = '';
      return url;
    } catch { return null; }
  }

  function extractSliceRange(url, contentRange, requestRange, byteLength) {
    if (typeof contentRange === 'string') {
      const match = /^bytes\s+(\d+)-(\d+)\/(\d+|\*)$/i.exec(contentRange.trim());
      if (match) {
        const start = Number(match[1]), end = Number(match[2]);
        const total = match[3] === '*' ? null : Number(match[3]);
        if (Number.isSafeInteger(start) && Number.isSafeInteger(end) && end >= start)
          return { start, end, total: Number.isSafeInteger(total) && total > 0 ? total : null, mode: 'http-range' };
      }
    }
    const bs = url.searchParams.get('bytestart');
    const be = url.searchParams.get('byteend');
    if (bs !== null && /^\d+$/.test(bs)) {
      const start = Number(bs);
      const end = be !== null && /^\d+$/.test(be) ? Number(be) : (byteLength > 0 ? start + byteLength - 1 : start);
      if (Number.isSafeInteger(start) && Number.isSafeInteger(end) && end >= start)
        return { start, end, total: null, mode: 'query-bytestart' };
    }
    const rangeParam = url.searchParams.get('range');
    if (rangeParam && /^\d+-\d+$/.test(rangeParam)) {
      const [s, e] = rangeParam.split('-').map(Number);
      if (Number.isSafeInteger(s) && Number.isSafeInteger(e) && e >= s)
        return { start: s, end: e, total: null, mode: 'query-range' };
    }
    if (typeof requestRange === 'string') {
      const m = /^bytes=(\d+)-(\d*)$/i.exec(requestRange.trim());
      if (m) {
        const start = Number(m[1]);
        const end = m[2] ? Number(m[2]) : (byteLength > 0 ? start + byteLength - 1 : start);
        if (Number.isSafeInteger(start) && Number.isSafeInteger(end) && end >= start)
          return { start, end, total: null, mode: 'request-range' };
      }
    }
    return null;
  }

  function canonicalizeSliceUrl(url) {
    const copy = new URL(url.href);
    const parts = copy.search.slice(1).split('&');
    const kept = parts.filter(part => part && !/^(?:bytestart|byteend|range)=/i.test(part));
    if (kept.length === parts.length || !copy.search) return null;
    copy.search = kept.length ? '?' + kept.join('&') : '';
    return copy.href;
  }

  function describeNetworkSource(address, mime = '', contentRange = '', requestRange = '', contentLength = null) {
    const url = parseHttpUrl(address);
    if (!url) return null;
    const cleanMime = String(mime || '').split(';')[0].trim().toLowerCase();
    const isManifest = /\.(?:m3u8|mpd)(?:[?#]|$)/i.test(url.href) || /(?:mpegurl|dash\+xml)/i.test(cleanMime);
    const isMediaLike = isManifest
      || /^(?:video\/|audio\/|application\/(?:octet-stream|mp4|ogg|vnd\.yt-ump))/i.test(cleanMime)
      || /\.(?:m4s|cmfv|cmfa|ts|aac|mp4|m4v|m4a|webm|mp3|ogg|opus|flac)(?:[?#]|$)/i.test(url.pathname)
      || [...url.searchParams.keys()].some(k => SLICE_QUERY_KEYS.has(k.toLowerCase()));
    if (!isMediaLike) return null;
    const declaredLen = /^\d+$/.test(String(contentLength ?? '')) ? Number(contentLength) : null;
    const slice = extractSliceRange(url, contentRange, requestRange, declaredLen || 0);
    return {
      address: url.href,
      path: url.origin + url.pathname,
      canonicalUrl: slice ? canonicalizeSliceUrl(url) : null,
      mime: cleanMime,
      isManifest,
      slice,
      declaredLength: declaredLen,
      at: now(),
      fingerprint: null
    };
  }

  function computeDigestAsync(record) {
    if (record.fingerprint || pendingDigests >= 24 || typeof TextEncoder !== 'function' || !globalThis.crypto?.subtle) return;
    pendingDigests++;
    try {
      globalThis.crypto.subtle.digest('SHA-256', new TextEncoder().encode(record.address))
        .then(buf => {
          record.fingerprint = Array.from(new Uint8Array(buf), b => b.toString(16).padStart(2, '0')).join('');
        }, () => {})
        .finally(() => { pendingDigests--; });
    } catch { pendingDigests--; }
  }

  function inspectMediaBytes(input) {
    try {
      const bytes = ArrayBuffer.isView(input)
        ? new Uint8Array(input.buffer, input.byteOffset, input.byteLength)
        : input instanceof ArrayBuffer ? new Uint8Array(input) : null;
      if (!bytes || bytes.length < 8) return null;
      const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
      const firstWord = view.getUint32(0);

      // WebM / Matroska EBML header (0x1A45DFA3) vs Cluster (0x1F43B675)
      if (firstWord === 0x1a45dfa3) return { container: 'webm', hasInit: true, hasMedia: bytes.length > 4096 };
      if (firstWord === 0x1f43b675) return { container: 'webm', hasInit: false, hasMedia: true };

      // MPEG-TS 188-byte packets
      if (bytes.length >= 376 && bytes[0] === 0x47 && bytes[188] === 0x47)
        return { container: 'ts', hasInit: false, hasMedia: true };

      // ISO BMFF / MP4 / fMP4 box scan
      let offset = 0, hasFtyp = false, hasMoov = false, hasMoof = false, hasMdat = false, initEnd = 0;
      for (let i = 0; i < 64 && offset + 8 <= bytes.length; i++) {
        let size = view.getUint32(offset), header = 8;
        const type = view.getUint32(offset + 4);
        if (size === 1) {
          if (offset + 16 > bytes.length) break;
          size = view.getUint32(offset + 8) * 4294967296 + view.getUint32(offset + 12);
          header = 16;
        } else if (size === 0) {
          size = bytes.length - offset;
        }
        if (size < header || offset + size > bytes.length) break;
        if (type === 0x66747970) { hasFtyp = true; if (!hasMoof && !hasMdat) initEnd = offset + size; } // ftyp
        else if (type === 0x6d6f6f76) { hasMoov = true; if (!hasMoof && !hasMdat) initEnd = offset + size; } // moov
        else if (type === 0x73696478 && hasMoov && !hasMoof && !hasMdat) { initEnd = offset + size; } // sidx
        else if (type === 0x6d6f6f66) hasMoof = true; // moof
        else if (type === 0x6d646174) hasMdat = true; // mdat
        offset += size;
      }
      if (hasFtyp || hasMoov || hasMoof || hasMdat) {
        return {
          container: 'mp4',
          hasInit: hasMoov,
          initEnd: hasMoov ? Math.min(initEnd || bytes.length, MAX_INIT_BYTES) : 0,
          hasMedia: hasMdat || hasMoof
        };
      }
      return bytes.length >= 128 ? { container: 'unknown', hasInit: false, hasMedia: true } : null;
    } catch { return null; }
  }

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
  function rememberBuffer(buffer, descriptor) {
    if (!descriptor || !(buffer instanceof ArrayBuffer || (typeof Blob === 'function' && buffer instanceof Blob))) return;
    const sample = buffer instanceof ArrayBuffer ? mediaSample(buffer) : null;
    const existing = bufferProvenance.get(buffer);
    if (existing?.address === descriptor.address && now() - existing.at <= TTL) return;
    const record = { ...descriptor, at: now(), sample };
    computeDigestAsync(record);
    bufferProvenance.set(buffer, record);
    bufferRing.push({ ref: new WeakRef(buffer), record });
    while (bufferRing.length > MAX_BUFFERS) {
      const evicted = bufferRing.shift();
      const target = evicted?.ref.deref();
      if (target && bufferProvenance.get(target) === evicted.record) bufferProvenance.delete(target);
    }
  }

  // Propagate ArrayBuffer provenance across .slice() so players slicing ring buffers keep exact URL + byte offset!
  replace(ArrayBuffer.prototype, 'slice', original => function (begin, end) {
    const sliced = apply(original, this, [begin, end]);
    try {
      const parent = bufferProvenance.get(this);
      if (parent && now() - parent.at <= TTL) {
        const offset = Math.max(0, Number(begin) || 0);
        const childSlice = parent.slice ? {
          ...parent.slice,
          start: parent.slice.start + offset,
          end: parent.slice.start + offset + Math.max(0, sliced.byteLength - 1)
        } : null;
        rememberBuffer(sliced, { ...parent, slice: childSlice });
      }
    } catch {}
    return sliced;
  });

  function hookNetwork() {
    // 1. Fetch hook (captures request Range headers + manifest URLs + response bodies)
    if (typeof globalThis.fetch === 'function') {
      replace(globalThis, 'fetch', original => function (input, init) {
        let reqRange = '';
        try {
          const h = init?.headers || (typeof Request === 'function' && input instanceof Request ? input.headers : null);
          if (h) {
            if (typeof h.get === 'function') reqRange = h.get('range') || h.get('Range') || '';
            else if (typeof h === 'object') reqRange = h.Range || h.range || '';
          }
        } catch {}
        const promise = apply(original, this, [input, init]);
        try {
          promise.then(response => {
            try {
              if (response && reqRange) streamProvenance.set(response, { reqRange });
            } catch {}
          }, () => {});
        } catch {}
        return promise;
      });
    }

    // 2. Response body readers
    if (typeof Response === 'function') {
      for (const method of ['arrayBuffer', 'blob']) {
        replace(Response.prototype, method, original => function (...args) {
          const promise = apply(original, this, args);
          try {
            const meta = streamProvenance.get(this);
            const desc = describeNetworkSource(
              this.url,
              this.headers?.get('content-type'),
              this.headers?.get('content-range'),
              meta?.reqRange || '',
              this.headers?.get('content-length')
            );
            if (desc) promise.then(buf => rememberBuffer(buf, desc), () => {});
          } catch {}
          return promise;
        });
      }
      if (typeof ReadableStream === 'function' && typeof ReadableStreamDefaultReader === 'function') {
        try {
          const bodyDesc = Object.getOwnPropertyDescriptor(Response.prototype, 'body');
          if (bodyDesc?.get && bodyDesc.configurable) {
            Object.defineProperty(Response.prototype, 'body', {
              ...bodyDesc,
              get() {
                const stream = apply(bodyDesc.get, this, []);
                try {
                  if (stream) {
                    const meta = streamProvenance.get(this);
                    const desc = describeNetworkSource(
                      this.url,
                      this.headers?.get('content-type'),
                      this.headers?.get('content-range'),
                      meta?.reqRange || '',
                      this.headers?.get('content-length')
                    );
                    if (desc) streamProvenance.set(stream, desc);
                  }
                } catch {}
                return stream;
              }
            });
          }
          replace(ReadableStream.prototype, 'getReader', original => function (...args) {
            const reader = apply(original, this, args);
            try {
              const desc = streamProvenance.get(this);
              if (reader && desc) readerProvenance.set(reader, desc);
            } catch {}
            return reader;
          });
          replace(ReadableStreamDefaultReader.prototype, 'read', original => function (...args) {
            const promise = apply(original, this, args);
            try {
              const desc = readerProvenance.get(this);
              if (desc) {
                promise.then(chunk => {
                  const val = chunk && !chunk.done ? chunk.value : null;
                  if (val) rememberBuffer(val.buffer || val, desc);
                }, () => {});
              }
            } catch {}
            return promise;
          });
        } catch {}
      }
    }

    // 3. XMLHttpRequest hook
    if (typeof XMLHttpRequest === 'function') {
      replace(XMLHttpRequest.prototype, 'open', original => function (method, url, ...rest) {
        try { requestMeta.set(this, { method: String(method || 'GET'), url: String(url || ''), rangeHeader: '' }); } catch {}
        return apply(original, this, [method, url, ...rest]);
      });
      replace(XMLHttpRequest.prototype, 'setRequestHeader', original => function (name, value) {
        try {
          if (String(name || '').toLowerCase() === 'range') {
            const meta = requestMeta.get(this);
            if (meta) meta.rangeHeader = String(value || '');
          }
        } catch {}
        return apply(original, this, [name, value]);
      });
      try {
        const respDesc = Object.getOwnPropertyDescriptor(XMLHttpRequest.prototype, 'response');
        if (respDesc?.get && respDesc.configurable) {
          Object.defineProperty(XMLHttpRequest.prototype, 'response', {
            ...respDesc,
            get() {
              const val = apply(respDesc.get, this, []);
              try {
                if (this.readyState === 4 && this.status >= 200 && this.status < 300) {
                  const ct = this.getResponseHeader('content-type') || '';
                  if (this.responseType === 'arraybuffer' || this.responseType === 'blob') {
                    const meta = requestMeta.get(this);
                    const desc = describeNetworkSource(
                      this.responseURL || meta?.url || '',
                      ct,
                      this.getResponseHeader('content-range') || '',
                      meta?.rangeHeader || '',
                      this.getResponseHeader('content-length')
                    );
                    if (desc) rememberBuffer(val, desc);
                  }
                }
              } catch {}
              return val;
            }
          });
        }
      } catch {}
    }
  }

  function discardTrack(track) {
    totalRetainedBytes -= track.retainedBytes;
    track.initBytes = null;
    track.chunks = [];
    track.retainedBytes = 0;
    track.evicted = true;
  }

  function reserveMseBytes(track, count) {
    if (track.evicted || track.retainedBytes + count > MAX_TRACK_BUFFER_BYTES) {
      if (!track.evicted) discardTrack(track);
      return false;
    }
    while (totalRetainedBytes + count > MAX_TOTAL_MSE_BYTES) {
      const oldest = retainedMseOrder.shift();
      if (!oldest) return false;
      if (!oldest.evicted) discardTrack(oldest);
      if (oldest === track) return false;
    }
    totalRetainedBytes += count;
    track.retainedBytes += count;
    return true;
  }

  function inferRole(mimeType) {
    const m = String(mimeType || '').toLowerCase();
    if (m.startsWith('audio/')) return 'audio';
    if (m.startsWith('video/')) {
      const codecs = /codecs\s*=\s*"?([^";]+)/i.exec(m)?.[1] || '';
      if (codecs.includes(',') && /(?:mp4a|opus|vorbis|ac-3|ec-3|flac)/i.test(codecs)) return 'muxed';
      return 'video';
    }
    return 'muxed';
  }

  function matchBySample(sample) {
    if (!sample) return null;
    let match = null;
    for (const { record } of bufferRing) {
      if (now() - record.at > TTL || !equalSample(sample, record.sample)) continue;
      if (match && match !== record) return null;
      match = record;
    }
    return match && { path: match.path, at: match.at, address: match.path, sampleOnly: true };
  }

  if (typeof MediaSource === 'function' && typeof SourceBuffer === 'function') {
    replace(URL, 'createObjectURL', original => function (...args) {
      const url = apply(original, this, args);
      try {
        const obj = args[0];
        if (obj instanceof MediaSource) {
          let msState = mediaSources.get(obj);
          if (!msState) {
            msState = { tracks: new Set() };
            mediaSources.set(obj, msState);
          }
          objectUrls.set(url, { kind: 'mse', ref: new WeakRef(obj), msState, paths: new Map(), createdAt: now() });
          while (objectUrls.size > MAX_SOURCES) objectUrls.delete(objectUrls.keys().next().value);
        } else if (typeof Blob === 'function' && obj instanceof Blob) {
          const prov = bufferProvenance.get(obj);
          const paths = new Map();
          if (prov && now() - prov.at <= TTL) paths.set(prov.address, { evidence: prov, seen: prov.at });
          objectUrls.set(url, { kind: 'blob', ref: new WeakRef(obj), blobType: obj.type, blobSize: obj.size, paths, createdAt: now() });
          while (objectUrls.size > MAX_SOURCES) objectUrls.delete(objectUrls.keys().next().value);
        }
      } catch {}
      return url;
    });

    let trackCounter = 0;
    replace(MediaSource.prototype, 'addSourceBuffer', original => function (mimeType, ...rest) {
      const sb = apply(original, this, [mimeType, ...rest]);
      try {
        let msState = mediaSources.get(this);
        if (!msState) {
          msState = { tracks: new Set() };
          mediaSources.set(this, msState);
        }
        const track = {
          id: 'sb-' + (++trackCounter),
          mimeType: String(mimeType || ''),
          role: inferRole(mimeType),
          ownerRef: new WeakRef(this),
          initBytes: null,
          chunks: [],
          retainedBytes: 0,
          totalAppendedBytes: 0,
          evicted: false
        };
        msState.tracks.add(track);
        sourceOwners.set(sb, track);
        if (retainedMseOrder.length >= 32) discardTrack(retainedMseOrder.shift());
        retainedMseOrder.push(track);
      } catch {}
      return sb;
    });

    replace(SourceBuffer.prototype, 'appendBuffer', original => function (data, ...rest) {
      const res = apply(original, this, [data, ...rest]);
      try {
        const track = sourceOwners.get(this);
        if (!track) return res;
        const rawBytes = ArrayBuffer.isView(data)
          ? new Uint8Array(data.buffer, data.byteOffset, data.byteLength)
          : data instanceof ArrayBuffer ? new Uint8Array(data) : null;
        if (!rawBytes || rawBytes.byteLength === 0) return res;

        const backing = ArrayBuffer.isView(data) ? data.buffer : data;
        const sample = mediaSample(rawBytes);
        const exact = bufferProvenance.get(backing);
        const wholeBuffer = backing.byteLength === rawBytes.byteLength;
        const prov = exact && wholeBuffer && (!exact.sample || equalSample(exact.sample, sample))
          ? exact : matchBySample(sample);
        const inspected = inspectMediaBytes(rawBytes);

        if (!track.evicted && track.totalAppendedBytes === 0 && inspected?.hasInit) {
          if (rawBytes.byteLength <= MAX_TRACK_BUFFER_BYTES && reserveMseBytes(track, rawBytes.byteLength))
            track.initBytes = rawBytes.slice();
          else discardTrack(track);
        } else if (!track.evicted && track.initBytes) {
          if (inspected?.hasInit) discardTrack(track);
          else if (reserveMseBytes(track, rawBytes.byteLength)) track.chunks.push(rawBytes.slice());
        }
        track.totalAppendedBytes += rawBytes.byteLength;

        if (prov && now() - prov.at <= TTL) {
          const seenAt = now();
          const ms = track.ownerRef.deref();
          if (ms) {
            for (const record of objectUrls.values()) {
              if (record.ref.deref() !== ms) continue;
              record.paths.delete(prov.address);
              record.paths.set(prov.address, { evidence: prov, seen: seenAt });
              while (record.paths.size > MAX_URLS_PER_TRACK) record.paths.delete(record.paths.keys().next().value);
            }
          }
        }
      } catch {}
      return res;
    });
  }

  // Shadow DOM discovery: mark elements that attach shadow roots so isolated content.js can find nested players
  if (typeof Element === 'function' && Element.prototype.attachShadow) {
    const shadowRoots = new WeakMap();
    replace(Element.prototype, 'attachShadow', original => function (init) {
      const root = apply(original, this, [init]);
      try {
        shadowRoots.set(this, root);
        this.setAttribute?.('data-vesper-shadow-host', '');
      } catch {}
      return root;
    });
  }

  hookNetwork();

  function encodeBase64(bytes) {
    let binary = '';
    for (let offset = 0; offset < bytes.length; offset += 8192)
      binary += String.fromCharCode(...bytes.subarray(offset, offset + 8192));
    return btoa(binary);
  }

  // Universal player identity inspection (replaces single-site youtube-player-witness.js)
  document.addEventListener('vesper-youtube-identity-request', event => {
    const video = event.composedPath?.()[0] || event.target;
    if (video?.tagName !== 'VIDEO') return;
    video.removeAttribute('data-vesper-youtube-video-id');
    video.removeAttribute('data-vesper-player-manifest');
    if (video.readyState < 1) return;
    try {
      const ytPlayer = video.closest?.('.html5-video-player');
      if (ytPlayer && typeof ytPlayer.getVideoData === 'function' && ytPlayer.contains(video)) {
        const id = ytPlayer.getVideoData()?.video_id;
        if (/^[A-Za-z0-9_-]{11}$/.test(id || '')) video.setAttribute('data-vesper-youtube-video-id', id);
      }
      // Generic HLS.js / Dash.js / Shaka / Video.js attached instance manifest discovery
      for (const key of Object.getOwnPropertyNames(video).slice(0, 40)) {
        const val = Object.getOwnPropertyDescriptor(video, key)?.value;
        if (!val || typeof val !== 'object') continue;
        const manifestCandidate = val.url || val.manifestUri_ || val.src_ || val.currentSource?.src;
        const parsed = typeof manifestCandidate === 'string' ? parseHttpUrl(manifestCandidate) : null;
        if (parsed && /\.(?:m3u8|mpd)(?:[?#]|$)/i.test(parsed.href)) {
          video.setAttribute('data-vesper-player-manifest', parsed.href);
          break;
        }
      }
    } catch {}
  }, true);

  // Rich provenance inspection for isolated content script
  document.addEventListener('vesper-media-provenance-request', event => {
    const player = event.composedPath?.()[0] || event.target;
    if (!(player instanceof HTMLMediaElement)) return;
    try {
      const src = player.currentSrc || player.src;
      const record = objectUrls.get(src);
      if (!record?.ref.deref() || player.mediaKeys) return;
      const at = now();
      const paths = [...record.paths.values()]
        .filter(item => at - item.seen <= TTL)
        .slice(-24)
        .map(({ evidence, seen }) => ({
          path: evidence.path,
          seen,
          ...(evidence.canonicalUrl ? { canonicalUrl: evidence.canonicalUrl } : {}),
          ...(evidence.fingerprint ? { fingerprint: evidence.fingerprint } : {})
        }));

      const tracks = [];
      if (record.kind === 'mse' && record.msState) {
        for (const track of record.msState.tracks) {
          const bufferedComplete = Boolean(
            track.initBytes && !track.evicted && track.retainedBytes > 0
            && record.ref.deref()?.readyState === 'ended'
            && player.buffered?.length === 1 && player.buffered.start(0) <= 0.5
            && Number.isFinite(player.duration) && player.duration > 0
            && player.buffered.end(0) >= player.duration - 0.75
          );
          tracks.push({
            id: track.id,
            role: track.role,
            mimeType: track.mimeType,
            hasInit: Boolean(track.initBytes),
            initLength: track.initBytes?.byteLength || 0,
            retainedBytes: track.retainedBytes,
            totalAppendedBytes: track.totalAppendedBytes,
            bufferedComplete
          });
        }
      }

      player.setAttribute('data-vesper-media-provenance', JSON.stringify({
        kind: record.kind,
        paths,
        tracks
      }));
    } catch {}
  }, true);

  // Export buffered MSE track chunks on demand (chunked by index so DOM attribute stays < 768 KiB)
  document.addEventListener('vesper-mse-export-request', event => {
    const player = event.composedPath?.()[0] || event.target;
    if (!(player instanceof HTMLMediaElement) || player.mediaKeys) return;
    try {
      const reqText = player.getAttribute('data-vesper-mse-export-query');
      player.removeAttribute('data-vesper-mse-export-query');
      if (!reqText) return;
      const req = JSON.parse(reqText);
      const record = objectUrls.get(player.currentSrc || player.src);
      if (record?.kind !== 'mse' || !record.msState) return;
      const track = [...record.msState.tracks].find(t => t.id === req.trackId);
      if (!track?.initBytes || track.evicted || record.ref.deref()?.readyState !== 'ended') return;

      const partCount = 1 + track.chunks.length;
      const index = req.partIndex;
      if (!Number.isSafeInteger(index) || index < 0 || index >= partCount) {
        player.setAttribute('data-vesper-mse-export-chunk', JSON.stringify({ done: true, totalParts: partCount }));
        return;
      }
      const bytes = index === 0 ? track.initBytes : track.chunks[index - 1];
      // Slice large single appends into <= 384 KiB pieces
      const SLICE = 384 * 1024;
      const subOffset = req.subOffset;
      if (!Number.isSafeInteger(subOffset) || subOffset < 0 || subOffset >= bytes.byteLength) return;
      const slice = bytes.subarray(subOffset, Math.min(bytes.byteLength, subOffset + SLICE));
      const nextSubOffset = subOffset + slice.byteLength < bytes.byteLength ? subOffset + slice.byteLength : 0;
      const nextPartIndex = nextSubOffset === 0 ? index + 1 : index;
      player.setAttribute('data-vesper-mse-export-chunk', JSON.stringify({
        done: false,
        chunk: encodeBase64(slice),
        byteLength: slice.byteLength,
        nextPartIndex,
        nextSubOffset,
        finishedAfterThis: nextPartIndex >= partCount
      }));
    } catch {}
  }, true);
})();
