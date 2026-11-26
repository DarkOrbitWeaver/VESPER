// High-confidence download intent, shared by the isolated click listener and worker.
(() => {
  'use strict';
  const file = /\.(?:zip|7z|rar|tar|gz|xz|bz2|msi|exe|msix|msixbundle|appx|apk|iso|dmg|deb|rpm|bin)$/i;
  const archivePath = /(?:^|\/)(?:zip|tarball|archive)(?:\/|$)/i;
  // Explicit same-origin download attributes are stronger evidence than a suffix.
  // Never speculate on signed, one-use or opaque query parameters.
  const explicitFile = /\.(?:txt|md|csv|json|docx|xlsx|pptx|odt|ods|epub|torrent|wasm|map|log|sql|bak)$/i;
  const queryKeys = new Set(['id','file','filename','name','format','download','version','arch','platform','lang',
    'ref','tag','branch','commit','sha','rev','release','path']);
  function explicitName(address, page, name) {
    try {
      return typeof name === 'string' && name.length > 0 && name.length <= 255
        && (file.test(name) || explicitFile.test(name)) && !/[\\/\x00-\x1f\x7f]/.test(name)
        && new URL(address).origin === new URL(page).origin ? name : null;
    } catch { return null; }
  }
  function candidate(address, page, downloadName) {
    try {
      const url = new URL(address), origin = new URL(page);
      if (!['http:', 'https:'].includes(url.protocol) || !['http:', 'https:'].includes(origin.protocol)
          || url.username || url.password || url.hash || url.href === origin.href
          || (url.protocol !== 'https:' && url.origin !== origin.origin)) return null;
      let name = '';
      try { name = decodeURIComponent(url.pathname.split('/').at(-1)); } catch { return null; }
      const explicit = explicitName(url.href, origin.href, downloadName);
      if (url.search && [...url.searchParams.keys()].some(key => !queryKeys.has(key.toLowerCase()))) return null;
      if (explicit) name = explicit;
      const isEligible = file.test(name) || (explicit && explicitFile.test(name)) || archivePath.test(url.pathname);
      return isEligible && !/[\\/\x00-\x1f\x7f]/.test(name) ? url.href : null;
    } catch { return null; }
  }
  function clickCandidate(event, page) {
    if (!event.isTrusted || event.defaultPrevented || !event.cancelable || event.button !== 0
        || event.ctrlKey || event.altKey || event.shiftKey || event.metaKey) return null;
    const anchor = event.composedPath().find(node => node?.tagName === 'A');
    if (!anchor || anchor.hasAttribute('ping') || anchor.hasAttribute('onclick')
        || anchor.hasAttribute('attributionsrc') || anchor.target && anchor.target.toLowerCase() !== '_self'
        || anchor.hasAttribute('referrerpolicy') || anchor.rel?.split(/\s+/).includes('noreferrer')) return null;
    const url = candidate(anchor.href, page, anchor.getAttribute('download'));
    return url && { anchor, url };
  }
  globalThis.VesperEarlyDownload = Object.freeze({candidate, clickCandidate, explicitName});
})();
