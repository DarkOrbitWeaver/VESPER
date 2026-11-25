// A finite, explicitly selected browser file can be copied to the native engine.
// This is not interception of an existing Chrome download and does not replay HTTP requests.
//
// Transport: content script -> extension port -> service worker relay -> native port -> host -> desktop pipe.
// Chunks are pipelined (WINDOW in flight) because every hop preserves order and the desktop
// handles them strictly sequentially. Memory stays bounded by WINDOW * CHUNK per transfer.
'use strict';
globalThis.VesperBrowserBody = (() => {
  // 512 KiB raw is 699,052 Base64 characters, which keeps every desktop pipe frame below its 1 MiB limit.
  const CHUNK = 512 * 1024;
  const WINDOW = 4;
  const MAX_BYTES = 16 * 1024 * 1024 * 1024;
  const PORT = 'vesper-browser-body-v1';
  // Only uncertain finish receipts, scoped to this document's isolated world.
  // Never evict a live ambiguity to make room for another copy.
  const pendingReceipts = new Map();
  const MAX_PENDING_RECEIPTS = 32;
  const EXTENSIONS = Object.freeze({
    'video/mp4': 'mp4', 'video/webm': 'webm', 'video/quicktime': 'mov',
    'video/x-matroska': 'mkv', 'audio/mp4': 'm4a', 'audio/mpeg': 'mp3',
    'audio/webm': 'webm', 'audio/ogg': 'ogg', 'video/ogg': 'ogv',
    'audio/wav': 'wav', 'audio/x-wav': 'wav', 'audio/flac': 'flac'
  });
  const GENERATED_EXTENSIONS = Object.freeze({
    'application/zip': 'zip', 'application/x-zip-compressed': 'zip',
    'application/pdf': 'pdf', 'application/json': 'json', 'text/csv': 'csv',
    'text/plain': 'txt', 'image/png': 'png', 'image/jpeg': 'jpg',
    'image/webp': 'webp', 'application/gzip': 'gz',
    'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': 'xlsx',
    'application/vnd.openxmlformats-officedocument.wordprocessingml.document': 'docx'
  });
  function safeName(value) {
    return String(value || '').replace(/[<>:"/\\|?*\u0000-\u001f\u007f\u202a-\u202e\u2066-\u2069]/g, '_')
      .trim().replace(/[. ]+$/g, '').slice(0, 180).replace(/[. ]+$/g, '');
  }

  // Match the explicit download link, not an arbitrary Blob found on the page.
  // A reused object URL with contradictory names needs manual resolution.
  function generatedLink(links, url, origin) {
    if (!localBlob(url, origin)) return null;
    const names = new Set();
    for (const link of links) {
      if (!link.isConnected || !link.hasAttribute('download') || link.href !== url) continue;
      names.add(safeName(link.getAttribute('download')));
      if (names.size > 1) return null;
    }
    return names.size === 1 ? { url, fileName: [...names][0] } : null;
  }

  // Automatic interception is limited to explicit data-file download names.
  // Executable, script, HTML and PDF downloads remain under Chrome's policy.
  function automaticGeneratedFile(proposal, origin) {
    const { url, fileName, size, mime } = proposal || {};
    if (!localBlob(url, origin) || !Number.isSafeInteger(size) || size <= 0 || size > MAX_BYTES
        || typeof fileName !== 'string' || safeName(fileName) !== fileName || fileName.length > 180
        || !/\.(?:zip|7z|rar|tar|gz|tgz|png|jpe?g|webp|gif|txt|md|csv|json|docx|xlsx|pptx|mp4|webm|mp3|wav|ogg|m4a|flac|mkv)$/i.test(fileName)
        || typeof mime !== 'string' || /(?:html|javascript|executable|x-msdownload|x-sh|pdf)/i.test(mime))
      return null;
    return { url, fileName, expectedLength: size, automatic: true };
  }

  function localBlob(url, origin) {
    try {
      const value = new URL(url);
      return value.protocol === 'blob:' && value.origin === origin
        && /^https?:\/\//.test(origin) && url.length <= 4096;
    } catch { return false; }
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

  function fileFacts(response, title, generated = null) {
    const mime = (response.headers.get('content-type') || '').split(';')[0].trim().toLowerCase();
    const lengthText = response.headers.get('content-length');
    const length = /^\d+$/.test(lengthText || '') ? Number(lengthText) : NaN;
    const encoding = response.headers.get('content-encoding');
    if (response.status !== 200 || !response.body || response.type === 'opaque'
        || response.redirected || encoding && encoding.toLowerCase() !== 'identity'
        || response.headers.has('content-range') || (!generated && !Object.hasOwn(EXTENSIONS, mime))
        || !Number.isSafeInteger(length) || length <= 0 || length > MAX_BYTES)
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
    return { length, fileName: stem + '.' + extension };
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

  const hex = buffer => Array.from(new Uint8Array(buffer), value => value.toString(16).padStart(2, '0')).join('');

  // Replies arrive in send order. Each call is matched to the oldest unanswered sequence.
  function openChannel(connect, signal) {
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
      try { port.disconnect(); } catch { /* Already disconnected. */ }
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
      pending.shift();
      clearTimeout(call.timer);
      if (reply.ok) call.resolve(reply.data || {});
      else {
        const error = new Error(reply.message || 'VESPER could not accept the browser copy.');
        if (reply.code === 'invalid-media') error.code = reply.code;
        call.reject(error);
        fail(reply.message || 'VESPER could not accept the browser copy.');
      }
    });
    return {
      send(message) {
        if (closed || signal?.aborted || pending.length > WINDOW)
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
      if (reply?.ok && reply.data?.accepted && reply.data.offset === receipt.length
          && typeof reply.data.jobId === 'string') return reply.data;
    } catch { /* A lost or missing receipt does not prove the file was rejected. */ }
    return null;
  }
  function uncertainFinish() {
    const error = new Error('VESPER could not confirm the final browser copy. Check the desktop before retrying.');
    error.code = 'uncertain-finish';
    return error;
  }

  async function copyBlob({ url, origin, title, generated = null, mediaProof = null, signal, onProgress = () => {},
    fetcher = globalThis.fetch, connect = options => chrome.runtime.connect(options),
    queryReceipt = transferId => chrome.runtime.sendMessage({ type: 'browser-body-status', transferId }) }) {
    const isBlob = localBlob(url, origin);
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
    // session. No cookies leave Chrome, no redirects or range requests.
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
    const response = await fetcher(url, options);
    let facts;
    try {
      if (response.url && response.url !== url) throw new Error('Browser file identity changed.');
      facts = fileFacts(response, title, generated);
      if (generated?.automatic && facts.length !== generated.expectedLength)
        throw new Error('The generated file length changed.');
      if (httpMedia && (response.headers.get('etag') !== mediaProof.etag
          || facts.length !== mediaProof.length
          || (response.headers.get('content-type') || '').split(';')[0].trim().toLowerCase() !== mediaProof.mime))
        throw new Error('The media representation changed. No copy was accepted.');
    }
    catch (error) { await response.body?.cancel().catch(() => {}); throw error; }
    let channel, reader, transferId = null, finishing = false;
    const unanswered = [];
    try {
      channel = openChannel(connect, signal);
      const mime = mediaProof?.mime ?? (response.headers.get('content-type') || '').split(';')[0].trim().toLowerCase();
      const mediaCheck = !generated && ['video/mp4', 'audio/mp4', 'video/quicktime'].includes(mime) ? 'mp4' : undefined;
      const begin = await channel.send({ operation: 'begin', ...facts, ...(generated?.automatic ? { automatic: true } : {}), ...(mediaCheck ? { mediaCheck } : {}) });
      if (begin.offset !== 0 || begin.accepted) throw new Error('Unexpected initial browser copy state.');
      transferId = begin.transferId;
      if (mediaCheck && begin.mediaCheck !== mediaCheck)
        throw new Error('Update the desktop helper to verify this browser media.');
      reader = response.body.getReader();
      onProgress(0, facts.length);
      // Aggregate small stream reads into one fixed buffer. SubtleCrypto copies its input
      // when digest() is called and Base64 encoding copies it too, so the buffer is reused at once.
      const buffer = new Uint8Array(CHUNK);
      let used = 0, sent = 0;
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
      while (true) {
        if (signal?.aborted) throw new Error('Browser copy cancelled.');
        const { done, value } = await reader.read();
        if (done) break;
        if (!(value instanceof Uint8Array) || value.length > facts.length - sent - used)
          throw new Error('The browser returned more bytes than declared.');
        for (let start = 0; start < value.length;) {
          const count = Math.min(CHUNK - used, value.length - start);
          buffer.set(value.subarray(start, start + count), used);
          used += count;
          start += count;
          if (used === CHUNK) await flush();
        }
      }
      if (sent + used !== facts.length) throw new Error('The browser file ended before its declared length.');
      await flush();
      while (unanswered.length) await unanswered.shift();
      finishing = true;
      const done = await channel.send({ operation: 'finish' });
      if (!done.accepted || done.offset !== facts.length || typeof done.jobId !== 'string')
        throw new Error('VESPER did not confirm acceptance of the browser file.');
      return done;
    } catch (error) {
      if (finishing && transferId && error?.code !== 'invalid-media') {
        // Even explicit cancellation can race the durable native commit. A
        // receipt query is read-only and must never start a second transfer.
        const receipt = { transferId, length: facts.length };
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
  function relay(port, { nativeHost, canBegin, connectNative, makeId, rememberReceipt = async () => {} }) {
    const sender = port.sender;
    let origin;
    try { origin = new URL(sender.url).origin; } catch { /* Invalid sender. */ }
    if (port.name !== PORT || !sender || !Number.isSafeInteger(sender.tab?.id)
        || sender.tab.id < 0 || sender.tab.incognito
        || !globalThis.VesperIntelligence.safeScope({ tabId: sender.tab.id, frameId: sender.frameId ?? 0, documentId: sender.documentId })
        || !/^https?:\/\//.test(origin || '')) return false;

    const transferId = makeId();
    const calls = [];                  // native calls awaiting their in-order reply
    let native = null, phase = 'new', sequence = 0, forwarded = 0, length = 0, closed = false;
    const close = () => {
      if (closed) return;
      closed = true;
      // A disconnected page cannot leave an unbounded native stream alive.
      // The receiver also expires incomplete transfers independently.
      try { native?.disconnect(); } catch {}
      try { port.disconnect(); } catch {}
    };
    const refuse = message => {
      try { port.postMessage({ sequence: calls[0]?.sequence ?? sequence, ok: false, message }); } catch {}
      close();
    };
    const forward = (call, data) => {
      calls.push(call);
      try { native.postMessage({ v: 1, id: call.id, type: 'browser-body', transferId, operation: call.operation, session: true, ...data }); }
      catch { refuse('The native VESPER helper could not receive the copy.'); }
    };
    const onNativeReply = reply => {
      const call = calls[0];
      if (closed || !call || reply?.id !== call.id) return close();
      if (!reply.ok) {
        if (call.operation !== 'finish' || reply.code !== 'invalid-media')
          return refuse(reply.message || 'The native VESPER receiver rejected the copy.');
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
        if (!result.accepted || result.offset !== length || typeof result.jobId !== 'string')
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
      if (operation === 'begin' && phase === 'new') {
        if (message.mediaCheck !== undefined && message.mediaCheck !== 'mp4'
            || !Number.isSafeInteger(message.length) || message.length < 1 || message.length > MAX_BYTES
            || typeof message.fileName !== 'string' || !message.fileName || message.fileName.length > 255
            || /[\\/:\u0000-\u001f]/.test(message.fileName))
          return refuse('Invalid browser file metadata.');
        phase = 'opening';
        if (!(await canBegin(sender, message.automatic === true)) || closed) return refuse('VESPER is not accepting browser copies.');
        await rememberReceipt(transferId, sender);
        if (closed) return;
        length = message.length;
        try {
          native = connectNative(nativeHost);
          native.onDisconnect.addListener(() => { if (phase !== 'finished') refuse('The native VESPER connection closed.'); });
          native.onMessage.addListener(onNativeReply);
        } catch { return refuse('The native VESPER helper is unavailable.'); }
        forward({ id: makeId(), sequence, operation }, { origin: origin + '/', fileName: message.fileName, length,
          ...(message.mediaCheck ? { mediaCheck: message.mediaCheck } : {}) });
      } else if (operation === 'append' && phase === 'receiving') {
        if (calls.length >= WINDOW) return refuse('Too many browser copy chunks are waiting.');
        if (message.offset !== forwarded || typeof message.chunk !== 'string'
            || message.chunk.length > Math.ceil(CHUNK / 3) * 4
            || !/^[A-Za-z0-9+/]+={0,2}$/.test(message.chunk) || message.chunk.length % 4 !== 0
            || !/^[a-f0-9]{64}$/i.test(message.digest || '')) return refuse('Invalid browser copy chunk.');
        const size = message.chunk.length / 4 * 3 - (message.chunk.endsWith('==') ? 2 : message.chunk.endsWith('=') ? 1 : 0);
        if (size < 1 || size > CHUNK || size > length - forwarded) return refuse('Invalid browser copy length.');
        const offset = forwarded;
        forwarded += size;
        forward({ id: makeId(), sequence, operation, next: forwarded }, { offset, chunk: message.chunk, digest: message.digest });
      } else if (operation === 'finish' && phase === 'receiving' && forwarded === length && calls.length === 0) {
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
  return Object.freeze({ PORT, CHUNK, WINDOW, MAX_BYTES, localBlob, eligibleHttpMedia, sameOriginMedia, validMediaProof, generatedLink, automaticGeneratedFile, fileFacts, inspectLocalMedia, copyBlob, relay });
})();
