// Observe browser-generated file clicks (blob: and data: URLs, plus JS-triggered anchor downloads)
// before the page revokes short-lived object URLs.
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
  let pending = null, intentAt = 0;
  const intentWindow = 15_000;
  const clock = Date.now.bind(Date);
  const SAFE_GENERATED_EXT = /\.(?:zip|7z|rar|tar|gz|tgz|xz|zst|png|jpe?g|webp|avif|svg|gif|txt|md|csv|json|xml|yaml|yml|sql|srt|vtt|epub|pdf|docx|xlsx|pptx|mp4|webm|mp3|wav|ogg|opus|m4a|flac|mkv)$/i;

  function recentIntent() { return intentAt > 0 && clock() - intentAt <= intentWindow; }

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
        while (known.size > 192) known.delete(known.keys().next().value);
      }
    } catch {}
    return url;
  })) return;

  replace(URL, 'revokeObjectURL', function (...args) {
    const result = apply(revoke, this, args);
    known.delete(args[0]);
    return result;
  });

  function consider(event, anchor) {
    if (pending || event.defaultPrevented || !event.cancelable
        || (!event.isTrusted && !navigator.userActivation?.isActive && !recentIntent())) return;
    if (!(anchor instanceof HTMLAnchorElement)) return;
    const hasDownload = anchor.hasAttribute('download');
    const name = anchor.getAttribute('download') || '';

    if (!event.isTrusted && name && /^https?:\/\//i.test(anchor.href)
        && !anchor.hasAttribute('ping') && !anchor.hasAttribute('attributionsrc')
        && (!anchor.target || anchor.target.toLowerCase() === '_self')
        && !anchor.rel?.split(/\s+/).includes('noreferrer')) {
      try {
        const proposal = new CustomEvent('vesper-early-download-request', {
          detail: JSON.stringify({ url: anchor.href, downloadName: name }), cancelable: true
        });
        if (!apply(dispatch, document, [proposal])) { intentAt = 0; event.preventDefault(); }
      } catch {}
      return;
    }

    if (!hasDownload || !name || name.length > 180 || !SAFE_GENERATED_EXT.test(name)) return;

    // Support data: URLs with explicit download filenames
    if (anchor.href.startsWith('data:') && anchor.href.length <= 5200) {
      try {
        const mimeMatch = /^data:([a-z0-9!#$&^_.+-]+\/[a-z0-9!#$&^_.+-]+)/i.exec(anchor.href);
        const mime = mimeMatch ? mimeMatch[1].toLowerCase() : 'application/octet-stream';
        const proposal = { url: anchor.href, fileName: name, mime };
        const detail = JSON.stringify(proposal);
        if (detail.length > 6000) return;
        const request = new CustomEvent(requestEvent, { detail, cancelable: true });
        pending = proposal;
        if (apply(dispatch, document, [request])) {
          pending = null;
          return;
        }
        intentAt = 0;
        event.preventDefault();
      } catch { pending = null; }
      return;
    }

    const blob = known.get(anchor.href)?.deref();
    if (!blob || !anchor.href.startsWith('blob:' + location.origin + '/')) return;
    let alias;
    try {
      alias = apply(create, URL, [blob]);
      const proposal = { url: alias, fileName: name, size: blob.size, mime: blob.type || 'application/octet-stream' };
      const request = new CustomEvent(requestEvent, { detail: JSON.stringify(proposal), cancelable: true });
      pending = proposal;
      if (apply(dispatch, document, [request])) {
        pending = null;
        apply(revoke, URL, [alias]);
        return;
      }
      intentAt = 0;
      event.preventDefault();
    } catch {
      pending = null;
      if (alias) apply(revoke, URL, [alias]);
    }
  }

  apply(listen, window, ['click', event => {
    if (event.isTrusted && navigator.userActivation?.isActive
        && event.composedPath().some(node => node instanceof Element && node.matches('button,a,input[type=button],input[type=submit],[role=button]')))
      intentAt = clock();
  }, true]);

  apply(listen, window, ['click', event => {
    const anchor = event.composedPath().find(node => node instanceof HTMLAnchorElement);
    consider(event, anchor);
  }]);

  replace(HTMLElement.prototype, 'click', function (...args) {
    if (!(this instanceof HTMLAnchorElement) || this.isConnected
        || (!navigator.userActivation?.isActive && !recentIntent())) return apply(click, this, args);
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
    if (file.url.startsWith('blob:')) {
      if (result.outcome === 'failed') setTimeout(() => apply(revoke, URL, [file.url]), 60_000);
      else apply(revoke, URL, [file.url]);
    }
  }]);

  apply(listen, window, ['pagehide', () => {
    if (pending?.url?.startsWith('blob:')) apply(revoke, URL, [pending.url]);
    pending = null;
    intentAt = 0;
    known.clear();
  }]);
})();
