// Observe explicit browser-generated file clicks before Chrome consumes a short-lived
// object URL. The isolated content script owns the opt-in and the native copy.
(() => {
  'use strict';
  if (!globalThis.isSecureContext || typeof WeakRef !== 'function' || typeof Blob !== 'function'
      || typeof HTMLElement !== 'function' || typeof CustomEvent !== 'function') return;
  const requestEvent = 'vesper-generated-download-request';
  const resultEvent = 'vesper-generated-download-result';
  const known = new Map();
  const create = URL.createObjectURL;
  const revoke = URL.revokeObjectURL;
  const click = HTMLElement.prototype.click;
  const dispatch = EventTarget.prototype.dispatchEvent;
  const listen = EventTarget.prototype.addEventListener;
  const unlisten = EventTarget.prototype.removeEventListener;
  const apply = Reflect.apply;
  let pending = null;
  function replace(owner, name, value) {
    const descriptor = Object.getOwnPropertyDescriptor(owner, name);
    if (!descriptor || typeof descriptor.value !== 'function') return false;
    try { Object.defineProperty(owner, name, { ...descriptor, value }); return true; }
    catch { return false; }
  }
  if (!replace(URL, 'createObjectURL', function (...args) {
    const url = apply(create, this, args);
    try {
      const blob = args[0];
      if (blob instanceof Blob && blob.size > 0 && blob.size <= 16 * 1024 ** 3) {
        known.set(url, new WeakRef(blob));
        while (known.size > 128) known.delete(known.keys().next().value);
      }
    } catch { /* Original object URL remains available. */ }
    return url;
  })) return;
  replace(URL, 'revokeObjectURL', function (...args) {
    const result = apply(revoke, this, args);
    known.delete(args[0]);
    return result;
  });
  function consider(event, anchor) {
    if (pending || event.defaultPrevented || !event.cancelable
        || (!event.isTrusted && !navigator.userActivation?.isActive)) return;
    if (!(anchor instanceof HTMLAnchorElement) || !anchor.hasAttribute('download')) return;
    const name = anchor.getAttribute('download');
    if (!name || name.length > 180 || !/\.(?:zip|7z|rar|tar|gz|tgz|png|jpe?g|webp|gif|txt|md|csv|json|docx|xlsx|pptx|mp4|webm|mp3|wav|ogg|m4a|flac|mkv)$/i.test(name)) return;
    const blob = known.get(anchor.href)?.deref();
    if (!blob || !anchor.href.startsWith('blob:' + location.origin + '/')) return;
    let alias;
    try {
      // Preserve the original Blob without reading or duplicating its bytes.
      // Sites frequently revoke the original URL immediately after anchor.click().
      alias = apply(create, URL, [blob]);
      const proposal = { url: alias, fileName: name, size: blob.size, mime: blob.type };
      const request = new CustomEvent(requestEvent, { detail: JSON.stringify(proposal), cancelable: true });
      pending = proposal;
      if (apply(dispatch, document, [request])) {
        pending = null;
        apply(revoke, URL, [alias]);
        return; // Extension unavailable, disabled or unwilling: Chrome proceeds normally.
      }
      event.preventDefault();
    } catch {
      pending = null;
      if (alias) apply(revoke, URL, [alias]);
    }
  }
  apply(listen, window, ['click', event => {
    const anchor = event.composedPath().find(node => node instanceof HTMLAnchorElement);
    consider(event, anchor);
  }]);
  // Detached programmatic links do not bubble to the document or window.
  // Keep the real click and all existing handlers; add a final local listener.
  replace(HTMLElement.prototype, 'click', function (...args) {
    if (!(this instanceof HTMLAnchorElement) || this.isConnected
        || !navigator.userActivation?.isActive) return apply(click, this, args);
    const handle = event => consider(event, this);
    apply(listen, this, ['click', handle]);
    try { return apply(click, this, args); }
    finally { apply(unlisten, this, ['click', handle]); }
  });
  apply(listen, document, [resultEvent, event => {
    if (!pending || typeof event.detail !== 'string') return;
    let result;
    try { result = JSON.parse(event.detail); } catch { return; }
    if (result?.url !== pending.url || !['accepted', 'failed', 'uncertain'].includes(result.outcome)) return;
    const file = pending;
    pending = null;
    // The isolated world alone retries a known failed copy. A page-authored
    // result event cannot cause a fallback download or native submission.
    if (result.outcome === 'failed')
      setTimeout(() => apply(revoke, URL, [file.url]), 60_000);
    else apply(revoke, URL, [file.url]);
  }]);
  apply(listen, window, ['pagehide', () => {
    if (pending) { apply(revoke, URL, [pending.url]); pending = null; }
    known.clear();
  }]);
})();
