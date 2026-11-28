// High-confidence download intent, shared by the isolated click listener and worker.
(() => {
  'use strict';
  const file = /\.(?:zip|7z|rar|tar|gz|xz|bz2|zst|tgz|msi|exe|msix|msixbundle|appx|apk|appimage|pkg|iso|dmg|deb|rpm|bin|whl|patch|diff)$/i;
  const archivePath = /(?:^|\/)(?:zip|tarball|archive)(?:\/|$)/i;
  const downloadPath = /(?:^|\/)(?:download|downloads|attachment|attachments|export)(?:\/|$)/i;
  const downloadLabel = /^(?:download(?:\s+(?:file|archive|attachment|document|report|video|audio|pdf|zip))?|télécharger(?:\s+le\s+fichier)?|descargar(?:\s+archivo)?|تنزيل(?:\s+الملف)?|تحميل(?:\s+الملف)?)$/iu;
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
  function candidate(address, page, downloadName, explicitIntent = false) {
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
      // Only an unambiguous labelled link may admit an extensionless download
      // endpoint; the worker still requires an attachment response.
      if (explicitIntent === true && (downloadPath.test(url.pathname) || url.searchParams.get('download') === '1')) return url.href;
      return (file.test(name) || archivePath.test(url.pathname)) && !/[\\/\x00-\x1f\x7f]/.test(name) ? url.href : null;
    } catch { return null; }
  }
  function referrer(page, target, policy) {
    try {
      const from = new URL(page), to = new URL(target);
      if (!['https:', 'http:'].includes(from.protocol) || from.username || from.password
          || from.protocol === 'https:' && to.protocol !== 'https:') return undefined;
      const rule = String(policy || '').trim().toLowerCase();
      if (rule === 'no-referrer' || rule === 'same-origin' && from.origin !== to.origin) return undefined;
      // Do not disclose a private document path or query to an unrelated CDN.
      if (from.origin !== to.origin || rule === 'origin' || rule === 'strict-origin') return from.origin + '/';
      from.hash = '';
      return from.href;
    } catch { return undefined; }
  }
  function clickCandidate(event, page) {
    if (!event.isTrusted || event.defaultPrevented || !event.cancelable || event.button !== 0
        || event.ctrlKey || event.altKey || event.shiftKey || event.metaKey) return null;
    const anchor = event.composedPath().find(node => node?.tagName === 'A');
    if (!anchor || anchor.hasAttribute('ping') || anchor.hasAttribute('onclick')
        || anchor.hasAttribute('attributionsrc')) return null;
    const label = (anchor.getAttribute('aria-label') || anchor.textContent || '').trim().replace(/\s+/g, ' ');
    const explicitIntent = label.length <= 80 && downloadLabel.test(label);
    const url = candidate(anchor.href, page, anchor.getAttribute('download'), explicitIntent);
    return url && { anchor, url, explicitIntent };
  }
  globalThis.VesperEarlyDownload = Object.freeze({candidate, clickCandidate, explicitName, signedDownload, referrer});
})();
