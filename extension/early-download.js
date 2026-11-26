// High-confidence download intent, shared by the isolated click listener and worker.
(() => {
  'use strict';
  const file = /\.(?:zip|7z|rar|tar|gz|xz|bz2|msi|exe|msix|msixbundle|appx|apk|iso|dmg|deb|rpm|bin)$/i;
  function candidate(address, page, downloadName) {
    try {
      const url = new URL(address), origin = new URL(page);
      if (!['http:', 'https:'].includes(url.protocol) || !['http:', 'https:'].includes(origin.protocol)
          || url.username || url.password || url.search || url.hash || url.href === origin.href
          || (url.protocol !== 'https:' && url.origin !== origin.origin)) return null;
      let name = '';
      try { name = decodeURIComponent(url.pathname.split('/').at(-1)); } catch { return null; }
      if (typeof downloadName === 'string' && downloadName.length <= 255 && file.test(downloadName)
          && url.origin === origin.origin) name = downloadName;
      return file.test(name) && !/[\\/\x00-\x1f\x7f]/.test(name) ? url.href : null;
    } catch { return null; }
  }
  function clickCandidate(event, page) {
    if (!event.isTrusted || event.defaultPrevented || !event.cancelable || event.button !== 0
        || event.ctrlKey || event.altKey || event.shiftKey || event.metaKey) return null;
    const anchor = event.composedPath().find(node => node?.tagName === 'A');
    if (!anchor || anchor.hasAttribute('ping') || anchor.hasAttribute('onclick')
        || anchor.hasAttribute('attributionsrc') || anchor.target && anchor.target.toLowerCase() !== '_self'
        || anchor.rel?.split(/\s+/).includes('noreferrer')) return null;
    const url = candidate(anchor.href, page, anchor.getAttribute('download'));
    return url && { anchor, url };
  }
  globalThis.VesperEarlyDownload = Object.freeze({candidate, clickCandidate});
})();
