// High-confidence download intent, shared by the isolated click listener and worker.
(() => {
  'use strict';
  const file = /\.(?:zip|7z|rar|tar|gz|xz|bz2|zst|tgz|msi|exe|msix|msixbundle|appx|apk|appimage|pkg|iso|dmg|deb|rpm|bin|whl|patch|diff)$/i;
  const archivePath = /(?:^|\/)(?:zip|tarball|archive)(?:\/|$)/i;
  const queryKeys = new Set(['id','file','filename','name','format','download','version','arch','platform','lang',
    'ref','tag','branch','commit','sha','rev','release','path']);
  const signedKey = /^(?:x-amz-(?:algorithm|credential|date|expires|signedheaders|signature|security-token)|x-goog-(?:algorithm|credential|date|expires|signedheaders|signature)|response-content-(?:disposition|type)|sv|sp|st|se|sr|spr|sig|rscd)$/i;

  function signedDownload(url) {
    const keys = [...url.searchParams.keys()];
    const signed = ['X-Amz-Signature', 'X-Goog-Signature', 'sig'].some(key => url.searchParams.has(key));
    return url.protocol === 'https:' && signed && keys.every(key => queryKeys.has(key.toLowerCase()) || signedKey.test(key));
  }

  function explicitName(url, page, name) {
    return url.origin === page.origin && typeof name === 'string' && name.length <= 255
      && !/[<>:"/\\|?*\x00-\x1f\x7f\u202a-\u202e\u2066-\u2069]|[. ]$/.test(name) ? name : null;
  }
  function candidate(address, page, downloadName) {
    try {
      const url = new URL(address), origin = new URL(page);
      if (!['http:', 'https:'].includes(url.protocol) || !['http:', 'https:'].includes(origin.protocol)
          || url.username || url.password || url.hash || url.href === origin.href
          || (url.protocol !== 'https:' && url.origin !== origin.origin)) return null;
      let name = '';
      try { name = decodeURIComponent(url.pathname.split('/').at(-1)); } catch { return null; }
      const explicit = explicitName(url, origin, downloadName);
      if (url.search && [...url.searchParams.keys()].some(key => !queryKeys.has(key.toLowerCase())) && !signedDownload(url)) return null;
      if (explicit !== null) return url.href;
      return (file.test(name) || archivePath.test(url.pathname)) && !/[\\/\x00-\x1f\x7f]/.test(name) ? url.href : null;
    } catch { return null; }
  }
  function clickCandidate(event, page) {
    if (!event.isTrusted || event.defaultPrevented || !event.cancelable || event.button !== 0
        || event.ctrlKey || event.altKey || event.shiftKey || event.metaKey) return null;
    const anchor = event.composedPath().find(node => node?.tagName === 'A');
    if (!anchor || anchor.hasAttribute('ping') || anchor.hasAttribute('onclick')
        || anchor.hasAttribute('attributionsrc')) return null;
    const url = candidate(anchor.href, page, anchor.getAttribute('download'));
    return url && { anchor, url };
  }
  globalThis.VesperEarlyDownload = Object.freeze({candidate, clickCandidate, explicitName, signedDownload});
})();
