// Universal Chrome-authenticated byte transport into VESPER's native engine & lossless remuxer.
// Supports:
//   1. Finite & streaming browser objects (blob:, data:, redirect-safe HTTP/HTTPS streams, ranged slices)
//   2. Service worker fetch of an observed complete media file
//   3. Direct MAIN-world MSE SourceBuffer track export when URLs use one-shot tokens.
'use strict';
globalThis.VesperBrowserBody = (() => {
  const PROTOCOL = 2;
  const CHUNK = 512 * 1024;
  const WINDOW = 4;
  const MAX_BYTES = 16 * 1024 * 1024 * 1024;
  const PORT = 'vesper-browser-body-v2';
  const pendingReceipts = new Map();
  const MAX_PENDING_RECEIPTS = 32;

  const EXTENSIONS = Object.freeze({
    'video/mp4': 'mp4', 'video/webm': 'webm', 'video/quicktime': 'mov',
    'video/x-matroska': 'mkv', 'video/mp2t': 'ts', 'audio/mp4': 'm4a', 'audio/m4a': 'm4a', 'audio/x-m4a': 'm4a',
    'audio/mpeg': 'mp3', 'audio/webm': 'webm', 'audio/ogg': 'ogg', 'video/ogg': 'ogv',
    'audio/wav': 'wav', 'audio/x-wav': 'wav', 'audio/flac': 'flac', 'audio/aac': 'aac', 'audio/opus': 'opus'
  });

  const GENERATED_EXTENSIONS = Object.freeze({
    'application/zip': 'zip', 'application/x-zip-compressed': 'zip', 'application/x-7z-compressed': '7z',
    'application/pdf': 'pdf', 'application/json': 'json', 'text/csv': 'csv', 'text/markdown': 'md',
    'text/plain': 'txt', 'text/vtt': 'vtt', 'application/x-subrip': 'srt',
    'image/png': 'png', 'image/jpeg': 'jpg', 'image/webp': 'webp', 'image/svg+xml': 'svg', 'image/avif': 'avif',
    'application/gzip': 'gz', 'application/epub+zip': 'epub',
    'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': 'xlsx',
    'application/vnd.openxmlformats-officedocument.wordprocessingml.document': 'docx',
    'application/vnd.openxmlformats-officedocument.presentationml.presentation': 'pptx'
  });

  function safeName(value) {
    return String(value || '').replace(/[<>:"/\\|?*\u0000-\u001f\u007f\u202a-\u202e\u2066-\u2069]/g, '_')
      .trim().replace(/[. ]+$/g, '').slice(0, 180).replace(/[. ]+$/g, '');
  }

  function localBlob(url, origin) {
    try {
      const value = new URL(url);
      return value.protocol === 'blob:' && value.origin === origin
        && /^https?:\/\//.test(origin) && url.length <= 4096;
    } catch { return false; }
  }

  function dataUrl(url) {
    return typeof url === 'string' && /^data:[a-z0-9!#$&^_.+-]+\/[a-z0-9!#$&^_.+-]+(?:;[^,]*)?,/i.test(url)
      && url.length <= 64 * 1024 * 1024;
  }

  function generatedLink(links, url, origin) {
    if (!localBlob(url, origin) && !dataUrl(url)) return null;
    const names = new Set();
    for (const link of links) {
      if (!link.isConnected || !link.hasAttribute('download') || link.href !== url) continue;
      names.add(safeName(link.getAttribute('download')));
      if (names.size > 1) return null;
    }
    return names.size === 1 ? { url, fileName: [...names][0] } : null;
  }

  function automaticGeneratedFile(proposal, origin) {
    const { url, fileName, size, mime } = proposal || {};
    const validUrl = localBlob(url, origin) || dataUrl(url);
    if (!validUrl || (size !== undefined && (!Number.isSafeInteger(size) || size <= 0 || size > MAX_BYTES))
        || typeof fileName !== 'string' || safeName(fileName) !== fileName || fileName.length > 180
        || !/\.(?:zip|7z|rar|tar|gz|tgz|xz|zst|png|jpe?g|webp|avif|svg|gif|txt|md|csv|json|xml|yaml|yml|sql|srt|vtt|epub|pdf|docx|xlsx|pptx|mp4|webm|mp3|wav|ogg|opus|m4a|flac|mkv)$/i.test(fileName)
        || typeof mime !== 'string' || /(?:javascript|executable|x-msdownload|x-msi|x-sh|x-bat)/i.test(mime))
      return null;
    return { url, fileName, expectedLength: Number.isSafeInteger(size) && size > 0 ? size : null, automatic: true };
  }

  function eligibleHttpMedia(url) {
    try {
      const value = new URL(url);
      return value.protocol === 'https:' && !value.username && !value.password
        && !value.hash && url.length <= 4096
        && !/\.(?:m3u8|mpd|m4s|cmfv|cmfa|ts|aac)(?:\?|$)/i.test(value.href)
        && ![...value.searchParams.keys()].some(key => /^(?:range|sq|segment|chunk|frag|bytestart|byteend)$/i.test(key));
    } catch { return false; }
  }

  function sameOriginMedia(url, origin) {
    return eligibleHttpMedia(url) && new URL(url).origin === origin;
  }

  function validMediaProof(proof, url, origin) {
    return proof?.url === url && eligibleHttpMedia(url)
      && (sameOriginMedia(url, origin) && [undefined, 'same-origin', 'omit'].includes(proof.credentials) || proof.corsOrigin === origin
        && ['omit', 'include'].includes(proof.credentials))
      && typeof proof.etag === 'string' && /^"[^"\x00-\x20\x7f]{1,254}"$/.test(proof.etag)
      && Object.hasOwn(EXTENSIONS, proof.mime)
      && Number.isSafeInteger(proof.length) && proof.length > 0 && proof.length <= MAX_BYTES;
  }

  function makeMediaFileName(title, extension) {
    const stem = String(title || 'Video').replace(/[<>:"/\\|?*\u0000-\u001f]/g, ' ')
      .replace(/\s+/g, ' ').replace(/[. ]+$/g, '').trim().slice(0, 140) || 'Video';
    return stem + '.' + extension;
  }

  function fileFacts(response, title, generated = null, verifiedPart = null, allowUnknownLength = false) {
    const mime = (response.headers.get('content-type') || '').split(';')[0].trim().toLowerCase();
    const lengthText = response.headers.get('content-length');
    const length = lengthText === null && allowUnknownLength ? null
      : /^\d+$/.test(lengthText || '') ? Number(lengthText) : NaN;
    const encoding = response.headers.get('content-encoding');
    if ((!verifiedPart && response.status !== 200 || verifiedPart && response.status !== 206) || !response.body || response.type === 'opaque'
        || response.redirected || encoding && encoding.toLowerCase() !== 'identity'
        || (!verifiedPart && response.headers.has('content-range')) || (!generated && !Object.hasOwn(EXTENSIONS, mime))
        || length !== null && (!Number.isSafeInteger(length) || length <= 0 || length > MAX_BYTES))
      throw new Error('This browser object is not a complete, finite file.');
    if (generated) {
      const name = safeName(generated.fileName);
      const extension = Object.hasOwn(EXTENSIONS, mime) ? EXTENSIONS[mime]
        : Object.hasOwn(GENERATED_EXTENSIONS, mime) ? GENERATED_EXTENSIONS[mime] : 'bin';
      return { length, fileName: name || 'download.' + extension };
    }
    const extension = EXTENSIONS[mime];
    const stem = String(title || 'Video').replace(/[<>:"/\\|?*\u0000-\u001f]/g, ' ')
      .replace(/\s+/g, ' ').replace(/[. ]+$/g, '').trim().slice(0, 140) || 'Video';
    return { length: verifiedPart ? verifiedPart.total : length, fileName: stem + '.' + extension };
  }

  // Only the exact resource, with a strong observed validator and a known total,
  // can be continued after a server caps a browser response to a byte range.
  function verifiedHttpPart(response, proof, start) {
    const mime = (response.headers.get('content-type') || '').split(';')[0].trim().toLowerCase();
    const length = response.headers.get('content-length');
    const encoding = response.headers.get('content-encoding');
    if (response.redirected || response.type === 'opaque' || !response.body
        || response.url && response.url !== proof.url
        || response.headers.get('etag') !== proof.etag || mime !== proof.mime
        || encoding && encoding.toLowerCase() !== 'identity')
      throw new Error('The media representation changed. No copy was accepted.');
    if (response.status === 200 && start === 0 && !response.headers.has('content-range')
        && /^\d+$/.test(length || '') && Number(length) === proof.length)
      return null;
    const range = /^bytes (\d+)-(\d+)\/(\d+)$/.exec(response.headers.get('content-range') || '');
    if (response.status !== 206 || !range || !/^\d+$/.test(length || ''))
      throw new Error('The browser did not return a verifiable media range.');
    const [first, last, total] = range.slice(1).map(Number);
    if (![first,last,total].every(Number.isSafeInteger) || first !== start || last < first
        || total !== proof.length || last >= total || Number(length) !== last - first + 1)
      throw new Error('The browser returned an inconsistent media range.');
    return { end: last + 1, total };
  }

  // A MediaSource object URL has the same syntax as a file Blob, but cannot
  // be fetched as a finite file. Only offer a format after inspecting the
  // local object's response metadata; never consume or buffer the body here.
  async function inspectLocalMedia(url, origin, fetcher = globalThis.fetch) {
    if (!localBlob(url, origin)) return null;
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 3000);
    let response;
    try {
      response = await fetcher(url, { signal: controller.signal, credentials: 'same-origin', redirect: 'error' });
      if (response.url && response.url !== url) return null;
      const facts = fileFacts(response, 'Media');
      const mime = response.headers.get('content-type').split(';')[0].trim().toLowerCase();
      return { mime, sizeBytes: facts.length, container: EXTENSIONS[mime].toUpperCase() };
    } catch { return null; }
    finally {
      clearTimeout(timer);
      if (response?.body) void response.body.cancel().catch(() => {});
    }
  }

  function encode(bytes) {
    let binary = '';
    for (let offset = 0; offset < bytes.length; offset += 8192)
      binary += String.fromCharCode(...bytes.subarray(offset, offset + 8192));
    return btoa(binary);
  }

  function decodeBase64(text) {
    const bin = atob(text);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
  }

  const hex = buffer => Array.from(new Uint8Array(buffer), value => value.toString(16).padStart(2, '0')).join('');

  function openChannel(connect, signal, onProgress = () => {}, onFinalizing = () => {}) {
    const port = connect({ name: PORT });
    const pending = [];
    let closed = false, sequence = 0;
    const fail = message => {
      if (closed) return;
      closed = true;
      for (const call of pending.splice(0)) {
        clearTimeout(call.timer);
        call.reject(new Error(message));
      }
      signal?.removeEventListener('abort', onAbort);
      try { port.disconnect(); } catch {}
    };
    const onAbort = () => fail('Browser copy cancelled.');
    signal?.addEventListener('abort', onAbort, { once: true });
    port.onDisconnect.addListener(() => fail('VESPER browser copy connection closed.'));
    port.onMessage.addListener(reply => {
      const call = pending[0];
      if (!call || reply?.sequence !== call.sequence) {
        fail('Unexpected browser copy response.');
        return;
      }
      clearTimeout(call.timer);
      if (reply.finalizing) {
        call.timer = setTimeout(() => fail('VESPER browser copy timed out.'), 620_000);
        onFinalizing(reply.transferId, reply.received);
        return;
      }
      if (reply.progress) {
        call.timer = setTimeout(() => fail('VESPER browser copy timed out.'), 120_000);
        onProgress(reply.received, reply.total);
        return;
      }
      pending.shift();
      if (reply.ok) call.resolve(reply.data || {});
      else {
        const error = new Error(reply.message || 'VESPER could not accept the browser stream.');
        error.code = reply.code;
        call.reject(error);
        fail(reply.message || 'VESPER could not accept the browser stream.');
      }
    });
    return {
      send(message) {
        if (closed || signal?.aborted || pending.length >= WINDOW)
          return Promise.reject(new Error('Browser copy connection unavailable.'));
        return new Promise((resolve, reject) => {
          const id = ++sequence;
          const timer = setTimeout(() => fail('VESPER browser copy timed out.'), message.operation === 'finish' ? 620_000 : 120_000);
          pending.push({ sequence: id, timer, resolve, reject });
          try { port.postMessage({ ...message, sequence: id }); }
          catch { fail('VESPER browser copy connection failed.'); }
        });
      },
      close: () => fail('Browser copy closed.')
    };
  }

  async function resolveReceipt(queryReceipt, receipt) {
    try {
      const reply = await queryReceipt(receipt.transferId);
      if (reply?.ok && reply.data?.accepted && typeof reply.data.jobId === 'string'
          && (!receipt.length || reply.data.offset === receipt.length))
        return reply.data;
    } catch {}
    return null;
  }

  function uncertainFinish() {
    const error = new Error('VESPER could not confirm the final browser stream. Check the desktop queue before retrying.');
    error.code = 'uncertain-finish';
    return error;
  }

  function requireNativeBegin(begin, mediaCheck) {
    if (begin.offset !== 0 || begin.accepted) throw new Error('Unexpected initial browser copy state.');
    if (mediaCheck && begin.mediaCheck !== mediaCheck) {
      const error = new Error('Update the desktop helper to verify this browser media.');
      error.code = 'desktop-version-mismatch';
      throw error;
    }
  }

  function mediaCheckFor(mime) {
    return ['mp4', 'm4a', 'mov'].includes(EXTENSIONS[mime]) ? 'mp4' : undefined;
  }

  async function copyBlob({ url, origin, title, generated = null, mediaProof = null, signal, onProgress = () => {},
    fetcher = globalThis.fetch, connect = options => chrome.runtime.connect(options),
    queryReceipt = transferId => chrome.runtime.sendMessage({ type: 'browser-body-status', transferId }) }) {
    const generatedData = Boolean(generated && dataUrl(url));
    const isBlob = localBlob(url, origin) || generatedData;
    const httpMedia = !generated && validMediaProof(mediaProof, url, origin);
    if (!isBlob && !httpMedia) throw new Error('The file is not available in this browser document.');
    if (signal?.aborted) throw new Error('Browser copy cancelled.');
    const receiptKey = JSON.stringify([origin, url]);
    const prior = pendingReceipts.get(receiptKey);
    if (prior) {
      const accepted = await resolveReceipt(queryReceipt, prior);
      if (!accepted) throw uncertainFinish();
      pendingReceipts.delete(receiptKey);
      return accepted; // The previous job exists; never fetch or publish it again.
    }
    if (pendingReceipts.size >= MAX_PENDING_RECEIPTS) throw uncertainFinish();
    // A separate, explicitly requested conditional GET in the browser's own
    // session. No cookies leave Chrome and redirects are rejected.
    // This is not original-response interception.
    const options = { signal, credentials: 'same-origin', redirect: 'error' };
    if (httpMedia) {
      const sameOrigin = sameOriginMedia(url, origin);
      options.mode = sameOrigin ? 'same-origin' : 'cors';
      // Browser-enforced CORS only. A cross-origin request cannot inherit
      // this page's credentials unless explicitly approved for that player.
      options.credentials = sameOrigin ? (mediaProof.credentials === 'same-origin' ? 'same-origin' : 'omit') : mediaProof.credentials;
      options.cache = 'no-store';
      options.headers = { 'If-Match': mediaProof.etag };
    }
    let response = await fetcher(url, options);
    let facts, part = null;
    try {
      if (response.url && response.url !== url) throw new Error('Browser file identity changed.');
      if (httpMedia) part = verifiedHttpPart(response, mediaProof, 0);
      facts = fileFacts(response, title, generated, part, generatedData);
      if (generated?.automatic && (!generatedData || generated.expectedLength != null)
          && facts.length !== generated.expectedLength)
        throw new Error('The generated file length changed.');
      if (httpMedia && (response.headers.get('etag') !== mediaProof.etag
          || facts.length !== mediaProof.length
          || (response.headers.get('content-type') || '').split(';')[0].trim().toLowerCase() !== mediaProof.mime))
        throw new Error('The media representation changed. No copy was accepted.');
    }
    catch (error) { await response.body?.cancel().catch(() => {}); throw error; }
    let channel, reader, transferId = null, finishing = false;
    const unanswered = [];
    let sent = 0;
    try {
      channel = openChannel(connect, signal);
      const mime = mediaProof?.mime ?? (response.headers.get('content-type') || '').split(';')[0].trim().toLowerCase();
      const mediaCheck = generated ? undefined : mediaCheckFor(mime);
      const begin = await channel.send({ operation: 'begin', ...facts, ...(generated?.automatic ? { automatic: true } : {}), ...(mediaCheck ? { mediaCheck } : {}) });
      transferId = begin.transferId;
      requireNativeBegin(begin, mediaCheck);
      reader = response.body.getReader();
      onProgress(0, facts.length);
      // Aggregate small stream reads into one fixed buffer. SubtleCrypto copies its input
      // when digest() is called and Base64 encoding copies it too, so the buffer is reused at once.
      const buffer = new Uint8Array(CHUNK);
      let used = 0;
      async function flush() {
        if (!used) return;
        if (signal?.aborted) throw new Error('Browser copy cancelled.');
        const bytes = buffer.subarray(0, used);
        const digest = crypto.subtle.digest('SHA-256', bytes);
        const chunk = encode(bytes);
        const offset = sent, next = sent + used;
        sent = next;
        used = 0;
        const call = channel.send({ operation: 'append', offset, chunk, digest: hex(await digest) }).then(reply => {
          if (reply.offset !== next || reply.accepted)
            throw new Error('VESPER returned an inconsistent browser copy offset.');
          onProgress(next, facts.length);
        });
        call.catch(() => {});            // Observed below; this only prevents an unhandled-rejection report.
        unanswered.push(call);
        while (unanswered.length >= WINDOW) await unanswered.shift();
      }
      let parts = 1;
      while (true) {
        if (signal?.aborted) throw new Error('Browser copy cancelled.');
        const { done, value } = await reader.read();
        if (done) {
          if (!part) break;
          if (sent + used !== part.end) throw new Error('The browser range ended before its declared length.');
          if (part.end === facts.length) break;
          if (++parts > 256) throw new Error('The browser returned too many small ranges.');
          const next = part.end;
          reader.releaseLock(); reader = null;
          response = await fetcher(url, { ...options, headers: { ...options.headers, Range: `bytes=${next}-` } });
          part = verifiedHttpPart(response, mediaProof, next);
          reader = response.body.getReader();
          continue;
        }
        const limit = part?.end ?? facts.length ?? Math.min(MAX_BYTES, url.length * 4);
        if (!(value instanceof Uint8Array) || value.length > limit - sent - used)
          throw new Error('The browser returned more bytes than allowed.');
        for (let start = 0; start < value.length;) {
          const count = Math.min(CHUNK - used, value.length - start);
          buffer.set(value.subarray(start, start + count), used);
          used += count;
          start += count;
          if (used === CHUNK) await flush();
        }
      }
      if (sent + used === 0 || facts.length !== null && sent + used !== facts.length)
        throw new Error('The browser file ended before its declared length.');
      await flush();
      while (unanswered.length) await unanswered.shift();
      finishing = true;
      const done = await channel.send({ operation: 'finish' });
      if (!done.accepted || done.offset !== sent || typeof done.jobId !== 'string')
        throw new Error('VESPER did not confirm acceptance of the browser file.');
      return done;
    } catch (error) {
      if (finishing && transferId && error?.code !== 'invalid-media') {
        // Even explicit cancellation can race the durable native commit. A
        // receipt query is read-only and must never start a second transfer.
        const receipt = { transferId, length: sent };
        pendingReceipts.set(receiptKey, receipt);
        const accepted = await resolveReceipt(queryReceipt, receipt);
        if (accepted) {
          pendingReceipts.delete(receiptKey);
          return accepted;
        }
        throw uncertainFinish();
      }
      if (!finishing && channel && transferId && !signal?.aborted) await channel.send({ operation: 'abort' }).catch(() => {});
      throw error;
    } finally {
      for (const call of unanswered) call.catch(() => {});
      if (reader) { await reader.cancel().catch(() => {}); reader.releaseLock(); }
      else await response.body?.cancel().catch(() => {});
      channel?.close();
    }
  }

  // Runs in the extension worker, not the page. Only content-script ports from an
  // identified browser document are accepted. Native provenance is derived here.
  // Export already-buffered MSE SourceBuffer tracks (1 or 2 tracks) directly from MAIN world into native remuxer!
  async function exportMseBuffer({ player, tracks, title, pageUrl, signal, onProgress = () => {},
    connect = options => chrome.runtime.connect(options) }) {
    if (!player || !Array.isArray(tracks) || tracks.length < 1 || tracks.length > 2)
      throw new Error('No buffered MSE tracks available.');
    const isWebm = tracks.every(t => String(t.mimeType || '').toLowerCase().includes('webm'));
    const outputContainer = tracks.length === 1 && String(tracks[0].mimeType || '').toLowerCase().startsWith('audio/')
      ? (isWebm ? 'webm' : 'm4a') : (isWebm ? 'webm' : 'mp4');
    const fileName = makeMediaFileName(title, outputContainer);
    const totalEstimate = tracks.reduce((sum, t) => sum + (t.retainedBytes || 0), 0);
    const trackSpecs = tracks.map(t => ({
      role: t.role,
      container: String(t.mimeType || '').toLowerCase().includes('webm') ? 'webm' : (t.role === 'audio' ? 'm4a' : 'mp4'),
      length: t.retainedBytes
    }));

    const channel = openChannel(connect, signal);
    let transferId = null, finishing = false, sent = 0;
    const unanswered = [];
    try {
      const mediaCheck = tracks.length === 1 && !isWebm ? 'mp4' : undefined;
      const begin = await channel.send({
        operation: 'begin',
        fileName,
        automatic: false,
        pageUrl,
        outputContainer,
        length: totalEstimate,
        tracks: trackSpecs,
        ...(mediaCheck ? { mediaCheck } : {})
      });
      transferId = begin.transferId;
      requireNativeBegin(begin, mediaCheck);
      const buffer = new Uint8Array(CHUNK);
      let used = 0, activeTrackIdx = 0;

      async function flush(trackIdx) {
        if (!used) return;
        const bytes = buffer.subarray(0, used);
        const digest = await crypto.subtle.digest('SHA-256', bytes);
        const chunk = encode(bytes);
        const offset = sent, next = sent + used;
        sent = next;
        used = 0;
        const call = channel.send({ operation: 'append', offset, chunk, digest: hex(digest), trackIndex: trackIdx }).then(reply => {
          if (reply.offset !== next || reply.accepted) throw new Error('Inconsistent MSE export offset.');
          onProgress(next, Math.max(totalEstimate, next));
        });
        call.catch(() => {});
        unanswered.push(call);
        while (unanswered.length >= WINDOW) await unanswered.shift();
      }

      for (let ti = 0; ti < tracks.length; ti++) {
        activeTrackIdx = ti;
        let partIndex = 0, subOffset = 0;
        while (true) {
          if (signal?.aborted) throw new Error('MSE export cancelled.');
          player.removeAttribute('data-vesper-mse-export-chunk');
          player.setAttribute('data-vesper-mse-export-query', JSON.stringify({
            trackId: tracks[ti].id, length: tracks[ti].retainedBytes, partIndex, subOffset
          }));
          player.dispatchEvent(new Event('vesper-mse-export-request', { bubbles: true, composed: true }));
          const raw = player.getAttribute('data-vesper-mse-export-chunk');
          player.removeAttribute('data-vesper-mse-export-chunk');
          if (!raw) throw new Error('MSE buffer is no longer available in the player.');
          const parsed = JSON.parse(raw);
          if (parsed.done || typeof parsed.chunk !== 'string' || parsed.chunk.length > 524288
              || !Number.isSafeInteger(parsed.nextPartIndex) || !Number.isSafeInteger(parsed.nextSubOffset)
              || parsed.nextPartIndex !== partIndex + 1 && (parsed.nextPartIndex !== partIndex || parsed.nextSubOffset <= subOffset))
            throw new Error('The MSE buffer changed during the copy.');
          const bytes = decodeBase64(parsed.chunk);
          if (bytes.length < 1 || sent + used + bytes.length > totalEstimate)
            throw new Error('The MSE buffer changed during the copy.');
          for (let start = 0; start < bytes.length;) {
            const count = Math.min(CHUNK - used, bytes.length - start);
            buffer.set(bytes.subarray(start, start + count), used);
            used += count;
            start += count;
            if (used === CHUNK) await flush(activeTrackIdx);
          }
          partIndex = parsed.nextPartIndex;
          subOffset = parsed.nextSubOffset;
          if (parsed.finishedAfterThis) break;
        }
        await flush(activeTrackIdx);
      }

      while (unanswered.length) await unanswered.shift();
      finishing = true;
      const done = await channel.send({ operation: 'finish' });
      if (!done.accepted || done.offset !== totalEstimate || sent !== totalEstimate || typeof done.jobId !== 'string')
        throw new Error('VESPER did not confirm the MSE stream.');
      return done;
    } catch (error) {
      if (finishing && error.code !== 'invalid-media') throw uncertainFinish();
      if (!finishing && transferId && !signal?.aborted) await channel.send({ operation: 'abort' }).catch(() => {});
      throw error;
    } finally {
      for (const call of unanswered) call.catch(() => {});
      channel.close();
    }
  }

  async function streamFromWorker({ proof, title, origin, nativeHost, connectNative, makeId,
    transferId, signal, onProgress, onFinalizing = () => {}, fetcher = globalThis.fetch }) {
    if (signal.aborted) throw new Error('Browser copy cancelled.');
    const { url, etag, mime, length, minimumLength, credentials } = proof;
    const headers = etag ? { 'If-Match': etag } : undefined;
    const fetchOptions = { credentials, redirect: 'error', cache: 'no-store', signal, headers };
    let response = await fetcher(url, fetchOptions);
    const type = response.headers.get('content-type')?.split(';', 1)[0]?.trim().toLowerCase();
    const encoding = response.headers.get('content-encoding');
    const lengthHeader = response.headers.get('content-length');
    const declared = /^\d+$/.test(lengthHeader || '') ? Number(lengthHeader) : null;
    let part = null;
    try {
      if (response.status === 206 && etag && length) {
        part = verifiedHttpPart(response, proof, 0);
      } else if (response.status !== 200 || !response.body || response.url && response.url !== url
          || response.headers.get('content-range') || encoding && encoding.toLowerCase() !== 'identity'
          || !Object.hasOwn(EXTENSIONS, type) || mime && mime !== type
          || etag && response.headers.get('etag') !== etag
          || declared !== null && (!Number.isSafeInteger(declared) || declared < 1 || declared > MAX_BYTES
              || length && declared !== length || minimumLength && declared < minimumLength)) {
        throw new Error('Chrome did not return the selected complete media file.');
      }
    } catch (error) {
      await response.body?.cancel().catch(() => {});
      throw error;
    }

    let native, reader, sent = 0, finishing = false;
    const pending = new Map();
    const close = () => {
      for (const call of pending.values()) call.reject(new Error('The native VESPER connection closed.'));
      pending.clear();
      try { native?.disconnect(); } catch {}
    };
    const sendNative = (operation, payload = {}) => new Promise((resolve, reject) => {
      if (signal.aborted) return reject(new Error('Browser copy cancelled.'));
      const id = makeId();
      pending.set(id, { resolve, reject });
      try { native.postMessage({ v: PROTOCOL, id, type: 'browser-body', transferId, operation, session: true, ...payload }); }
      catch (error) { close(); }
    });
    signal.addEventListener('abort', close, { once: true });
    try {
      native = connectNative(nativeHost);
      native.onDisconnect.addListener(close);
      native.onMessage.addListener(reply => {
        const call = pending.get(reply?.id);
        if (!call) return close();
        pending.delete(reply.id);
        if (reply.ok) call.resolve(reply.data || {});
        else {
          const error = new Error(reply.message || 'Native receiver rejected the stream.');
          error.code = reply.code;
          call.reject(error);
        }
      });

      const expected = part?.total ?? declared ?? length;
      const mediaCheck = mediaCheckFor(type);
      const begin = await sendNative('begin', {
        origin: origin + '/',
        fileName: makeMediaFileName(title, EXTENSIONS[type]),
        automatic: false,
        ...(expected ? { length: expected } : {}),
        ...(mediaCheck ? { mediaCheck } : {})
      });
      requireNativeBegin(begin, mediaCheck);
      reader = response.body.getReader();
      const buffer = new Uint8Array(CHUNK);
      let used = 0;
      async function flush() {
        if (!used) return;
        const bytes = buffer.subarray(0, used);
        const digest = hex(await crypto.subtle.digest('SHA-256', bytes));
        const next = sent + used;
        const reply = await sendNative('append', { offset: sent, chunk: encode(bytes), digest });
        if (reply.offset !== next || reply.accepted) throw new Error('Native copy offset mismatch.');
        sent = next;
        used = 0;
        onProgress(sent, expected || 0);
      }
      let parts = 1;
      while (!signal.aborted) {
        const { done, value } = await reader.read();
        if (done) {
          if (!part) break;
          if (sent + used !== part.end) throw new Error('The browser range ended before its declared length.');
          if (part.end === expected) break;
          if (++parts > 256) throw new Error('The browser returned too many small ranges.');
          const offset = part.end;
          reader.releaseLock();
          reader = null;
          response = await fetcher(url, { ...fetchOptions, headers: { ...headers, Range: `bytes=${offset}-` } });
          part = verifiedHttpPart(response, proof, offset);
          reader = response.body.getReader();
          continue;
        }
        if (sent + used + value.length > (part?.end ?? expected ?? MAX_BYTES)) throw new Error('Browser file length changed.');
        for (let start = 0; start < value.length;) {
          const count = Math.min(CHUNK - used, value.length - start);
          buffer.set(value.subarray(start, start + count), used);
          used += count;
          start += count;
          if (used === CHUNK) await flush();
        }
      }
      if (signal.aborted) throw new Error('Browser copy cancelled.');
      await flush();
      if (sent <= 0 || expected && sent !== expected || minimumLength && sent < minimumLength)
        throw new Error('Browser file ended before the selected media was complete.');
      onFinalizing(sent);
      finishing = true;
      const done = await sendNative('finish');
      if (!done.accepted || done.offset !== sent || typeof done.jobId !== 'string') throw uncertainFinish();
      return done;
    } catch (error) {
      if (finishing && error.code !== 'invalid-media') throw uncertainFinish();
      throw error;
    } finally {
      signal.removeEventListener('abort', close);
      if (reader) await reader.cancel().catch(() => {});
      else await response.body.cancel().catch(() => {});
      close();
    }
  }

  async function copyWorkerMedia({ player, title, signal, onProgress, connect = options => chrome.runtime.connect(options),
    queryReceipt = transferId => chrome.runtime.sendMessage({ type: 'browser-body-status', transferId }) }) {
    const key = JSON.stringify(['worker', player.pageUrl, player.playerKey, player.playerUrl, player.generation, player.mediaChangedAt]);
    const prior = pendingReceipts.get(key);
    if (prior) {
      const accepted = await resolveReceipt(queryReceipt, prior);
      if (!accepted) throw uncertainFinish();
      pendingReceipts.delete(key);
      return accepted;
    }
    if (pendingReceipts.size >= MAX_PENDING_RECEIPTS) throw uncertainFinish();
    let receipt;
    const channel = openChannel(connect, signal, onProgress, (transferId, length) => {
      if (typeof transferId === 'string' && Number.isSafeInteger(length) && length > 0) receipt = { transferId, length };
    });
    try { return await channel.send({ operation: 'worker', player, title }); }
    catch (error) {
      if (!receipt || error?.code === 'invalid-media') throw error;
      pendingReceipts.set(key, receipt);
      const accepted = await resolveReceipt(queryReceipt, receipt);
      if (!accepted) throw uncertainFinish();
      pendingReceipts.delete(key);
      return accepted;
    } finally { channel.close(); }
  }

  // Extension worker relay for content-script ports
  function relay(port, { nativeHost, canBegin, connectNative, makeId, stream, rememberReceipt = async () => {} }) {
    const sender = port.sender;
    let origin;
    try { origin = new URL(sender.url).origin; } catch {}
    if (port.name !== PORT || !sender || !Number.isSafeInteger(sender.tab?.id)
        || sender.tab.id < 0 || sender.tab.incognito
        || !globalThis.VesperIntelligence.safeScope({ tabId: sender.tab.id, frameId: sender.frameId ?? 0, documentId: sender.documentId })
        || !/^https?:\/\//.test(origin || '')) return false;

    const transferId = makeId();
    const calls = [];
    let workerController;
    let native = null, phase = 'new', sequence = 0, forwarded = 0, expectedLength = null, closed = false;
    const close = () => {
      if (closed) return;
      closed = true;
      workerController?.abort();
      try { native?.disconnect(); } catch {}
      try { port.disconnect(); } catch {}
    };
    const refuse = message => {
      try { port.postMessage({ sequence: calls[0]?.sequence ?? sequence, ok: false, message }); } catch {}
      close();
    };
    const forward = (call, data) => {
      calls.push(call);
      try { native.postMessage({ v: PROTOCOL, id: call.id, type: 'browser-body', transferId, operation: call.operation, session: true, ...data }); }
      catch { refuse('The native VESPER helper could not receive the stream.'); }
    };
    const onNativeReply = reply => {
      const call = calls[0];
      if (closed || !call || reply?.id !== call.id) return close();
      if (!reply.ok) {
        if (call.operation !== 'finish' || reply.code !== 'invalid-media')
          return refuse(reply.message || 'The native VESPER receiver rejected the stream.');
        calls.shift();
        phase = 'finished';
        try { port.postMessage({ sequence: call.sequence, ok: false, code: reply.code, message: reply.message }); }
        catch { close(); }
        return;
      }
      calls.shift();
      const result = reply.data || {};
      if (call.operation === 'begin') {
        if (result.offset !== 0 || result.accepted) return refuse('Invalid initial native copy state.');
        phase = 'receiving';
        result.transferId = transferId;
      } else if (call.operation === 'append') {
        if (result.offset !== call.next || result.accepted) return refuse('Native copy offset mismatch.');
      } else if (call.operation === 'finish') {
        if (!result.accepted || (expectedLength !== null && result.offset !== expectedLength) || typeof result.jobId !== 'string')
          return refuse('Native copy acceptance was not confirmed.');
        phase = 'finished';
      } else phase = 'finished';
      try { port.postMessage({ sequence: call.sequence, ok: true, data: result }); }
      catch { close(); }
    };
    const onPageMessage = async message => {
      if (closed || !Number.isSafeInteger(message?.sequence) || message.sequence !== sequence + 1) return close();
      sequence = message.sequence;
      const operation = message.operation;
      if (operation === 'worker' && phase === 'new' && stream) {
        phase = 'streaming';
        workerController = new AbortController();
        try {
          await rememberReceipt(transferId, sender);
          if (closed) return;
          const data = await stream(message, sender, {
            transferId, signal: workerController.signal,
            onProgress(received, total) {
              if (!closed) port.postMessage({ sequence, progress: true, received, total });
            },
            onFinalizing(received) {
              if (!closed) port.postMessage({ sequence, finalizing: true, transferId, received });
            }
          });
          if (!closed) port.postMessage({ sequence, ok: true, data });
        } catch (error) {
          if (!closed) port.postMessage({ sequence, ok: false, code: error.code, message: error.message });
        }
        phase = 'finished';
      } else if (operation === 'begin' && phase === 'new') {
        const hasLen = message.length !== undefined && message.length !== null;
        if (message.mediaCheck !== undefined && message.mediaCheck !== 'mp4'
            || (hasLen && (!Number.isSafeInteger(message.length) || message.length < 1 || message.length > MAX_BYTES))
            || typeof message.fileName !== 'string' || !message.fileName || message.fileName.length > 255
            || /[\\/:\u0000-\u001f]/.test(message.fileName))
          return refuse('Invalid browser file metadata.');
        phase = 'opening';
        if (!(await canBegin(sender, message.automatic === true)) || closed) return refuse('VESPER is not accepting browser copies.');
        await rememberReceipt(transferId, sender);
        if (closed) return;
        expectedLength = hasLen ? message.length : null;
        try {
          native = connectNative(nativeHost);
          native.onDisconnect.addListener(() => { if (phase !== 'finished') refuse('The native VESPER connection closed.'); });
          native.onMessage.addListener(onNativeReply);
        } catch { return refuse('The native VESPER helper is unavailable.'); }
        forward({ id: makeId(), sequence, operation }, {
          origin: origin + '/',
          fileName: message.fileName,
          ...(expectedLength !== null ? { length: expectedLength } : {}),
          ...(typeof message.pageUrl === 'string' ? { pageUrl: message.pageUrl } : {}),
          ...(typeof message.outputContainer === 'string' ? { outputContainer: message.outputContainer } : {}),
          ...(Array.isArray(message.tracks) ? { tracks: message.tracks } : {}),
          automatic: message.automatic === true,
          ...(message.mediaCheck ? { mediaCheck: message.mediaCheck } : {})
        });
      } else if (operation === 'append' && phase === 'receiving') {
        if (calls.length >= WINDOW) return refuse('Too many browser copy chunks are waiting.');
        if (message.offset !== forwarded || typeof message.chunk !== 'string'
            || message.chunk.length > Math.ceil(CHUNK / 3) * 4
            || !/^[A-Za-z0-9+/]+={0,2}$/.test(message.chunk) || message.chunk.length % 4 !== 0
            || !/^[a-f0-9]{64}$/i.test(message.digest || '')) return refuse('Invalid browser copy chunk.');
        const size = message.chunk.length / 4 * 3 - (message.chunk.endsWith('==') ? 2 : message.chunk.endsWith('=') ? 1 : 0);
        const maxRemaining = (expectedLength ?? MAX_BYTES) - forwarded;
        if (size < 1 || size > CHUNK || size > maxRemaining) return refuse('Invalid browser copy length.');
        const offset = forwarded;
        forwarded += size;
        const trackIndex = Number.isInteger(message.trackIndex) && message.trackIndex >= 0 && message.trackIndex <= 1 ? message.trackIndex : 0;
        forward({ id: makeId(), sequence, operation, next: forwarded }, { offset, chunk: message.chunk, digest: message.digest, trackIndex });
      } else if (operation === 'finish' && phase === 'receiving' && forwarded > 0 && (expectedLength === null || forwarded === expectedLength) && calls.length === 0) {
        phase = 'finishing';
        forward({ id: makeId(), sequence, operation }, {});
      } else if (operation === 'abort' && phase === 'receiving') {
        phase = 'aborting';
        forward({ id: makeId(), sequence, operation }, {});
      } else return refuse('Invalid browser copy sequence.');
    };
    port.onDisconnect.addListener(close);
    port.onMessage.addListener(message => void onPageMessage(message).catch(() => refuse('Browser copy failed.')));
    return true;
  }

  return Object.freeze({
    PORT, CHUNK, WINDOW, MAX_BYTES,
    localBlob, dataUrl, eligibleHttpMedia, sameOriginMedia, validMediaProof,
    generatedLink, automaticGeneratedFile, fileFacts, inspectLocalMedia,
    copyBlob, copyWorkerMedia, exportMseBuffer, streamFromWorker, relay
  });
})();
