// Runs only when explicitly opened from VESPER's context menu. No background page scanning.
(() => {
  'use strict';
  let panel, observer;
  const LINK_SELECTOR = 'a[href],area[href],button[data-url],button[data-href],[data-download-url],[role=button][data-url]';
  const FILE_TYPES = {
    Video: /^(mp4|m4v|webm|mov|mkv|avi|wmv|mpg|mpeg|ogv)$/i,
    Audio: /^(mp3|m4a|aac|flac|wav|ogg|opus|wma|aiff)$/i,
    Archive: /^(zip|7z|rar|tar|gz|bz2|xz|zst|tgz)$/i,
    Document: /^(pdf|txt|rtf|epub|docx?|xlsx?|pptx?|odt|ods|csv)$/i,
    Image: /^(png|jpe?g|webp|avif|gif|svg|bmp|tiff?|ico)$/i,
    Installer: /^(exe|msi|msix|appx|apk|dmg|pkg|deb|rpm)$/i,
    File: /^(json|xml|srt|vtt|torrent|bin|iso|wasm|ps1|patch|diff|cs|csproj|mjs|js|ts)$/i
  };
  const http = value => {
    if (!value) return null;
    try {
      const url = new URL(value, location.href);
      return /^https?:$/.test(url.protocol) && !url.username && !url.password ? url.href : null;
    } catch { return null; }
  };

  function generatedAnchor(element) {
    const value = element.href;
    return element.hasAttribute('download') && globalThis.VesperBrowserBody?.localBlob(value, location.origin)
      ? value : null;
  }

  function fileDetails(element, url, player) {
    const name = element.getAttribute('download') || new URL(url).pathname.split('/').pop() || '';
    const extension = /\.([a-z0-9]{1,8})$/i.exec(name)?.[1];
    const kind = Object.keys(FILE_TYPES).find(type => FILE_TYPES[type].test(extension || ''));
    if (!kind && !player && !element.hasAttribute('download') && !element.dataset.downloadUrl) return null;
    const media = player ? (player.tagName === 'VIDEO' ? 'Video' : 'Audio') : kind;
    const type = extension && kind ? `${extension.toUpperCase()} ${media.toLowerCase()}` : media || 'File';
    const duration = player?.duration;
    return { type, media, duration: Number.isFinite(duration) && duration > 0 && duration <= 31_536_000 ? duration : undefined };
  }

  function durationText(seconds) {
    const total = Math.floor(seconds);
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor(total / 60) % 60;
    const tail = String(total % 60).padStart(2, '0');
    return hours ? `${hours}:${String(minutes).padStart(2, '0')}:${tail}` : `${minutes}:${tail}`;
  }
  function publisherChecksum(element, url) {
    if (typeof element.getClientRects === 'function' && element.getClientRects().length === 0) return null;
    let scope = element.parentElement;
    for (let depth = 0; scope && depth < 3; depth++, scope = scope.parentElement) {
      if (typeof scope.getClientRects === 'function' && scope.getClientRects().length === 0) continue;
      const text = typeof scope.innerText === 'string' ? scope.innerText : '';
      if (!text || text.length > 4096) continue;
      const links = scope.querySelectorAll(LINK_SELECTOR);
      let count = 0, onlyUrl;
      for (const link of links) {
        const target = http(link.getAttribute('href') || link.dataset.downloadUrl || link.dataset.url || link.dataset.href);
        if (target) { count++; onlyUrl = target; if (count > 1) break; }
      }
      if (count !== 1 || onlyUrl !== url) continue;
      const matches = [...text.matchAll(/\bsha[\s-]?256(?:sum)?\b(?:\s+checksum)?\s*(?::|=)?\s*([a-f\d]{64})(?![a-f\d])/gi)];
      if (matches.length === 1) return matches[0][1].toUpperCase();
    }
    return null;
  }
  function open() {
    if (panel) return;
    const host = document.createElement('div');
    panel = host;
    const shadow = host.attachShadow({ mode: 'closed' });
    const style = document.createElement('style');
    style.textContent = `:host{all:initial;position:fixed;inset:0;z-index:2147483647;display:grid;place-items:center;background:#0008;font:14px system-ui;color:#203b2b}*{box-sizing:border-box}section{width:min(650px,94vw);max-height:90vh;display:flex;flex-direction:column;background:#faf9f3;border:1px solid #829780;border-radius:10px;padding:20px;gap:12px;box-shadow:0 10px 50px #0005}h2,p{margin:0}input[type=search]{font:inherit;padding:8px;width:100%}ul{margin:0;padding:0;overflow:auto;min-height:70px;max-height:45vh;list-style:none}li{padding:7px 0;border-bottom:1px solid #dde2d5}label{display:flex;gap:9px;align-items:start}span{overflow-wrap:anywhere}small{display:block;color:#556653}footer{display:flex;gap:8px;align-items:center;flex-wrap:wrap}button{font:inherit;padding:8px 13px;cursor:pointer;background:#edf1e5;border:1px solid #829780;border-radius:4px}button:last-child{background:#315a3c;color:white}button:disabled{opacity:.5;cursor:default}:focus-visible{outline:3px solid #678b4d;outline-offset:2px}`;
    const section = document.createElement('section');
    section.setAttribute('role', 'dialog'); section.setAttribute('aria-modal', 'true'); section.setAttribute('aria-label', 'Smart Select files');
    const heading = document.createElement('h2'); heading.textContent = 'Smart Select';
    const help = document.createElement('p');
    help.textContent = 'Choose files to review in VESPER, or copy generated browser files directly. Use the butterfly for playing videos.';
    const filter = document.createElement('input'); filter.type = 'search'; filter.placeholder = 'Filter by name, address or file type'; filter.setAttribute('aria-label', 'Filter files');
    const list = document.createElement('ul');
    const status = document.createElement('p'); status.setAttribute('role', 'status');
    const footer = document.createElement('footer');
    const close = document.createElement('button'); close.textContent = 'Cancel';
    const all = document.createElement('button'); all.textContent = 'Select visible';
    const guided = document.createElement('button'); guided.textContent = 'Guided batch…';
    guided.addEventListener('click', () => { finish(); globalThis.VesperSmartBatch?.open(); });
    const review = document.createElement('button'); review.textContent = 'Review in VESPER';
    const selected = new Set(), settled = new Set(), uncertain = new Set();
    let candidates = [], scheduled = false, copying = false, copyController = null, copyStatus = '';
    const previousFocus = document.activeElement;
    function scheduleCollect() {
      if (scheduled || copying) return;
      scheduled = true;
      queueMicrotask(() => { if (panel === host) collect(); });
    }
    function finish() {
      copyController?.abort();
      document.removeEventListener('loadedmetadata', scheduleCollect, true);
      observer?.disconnect(); observer = null;
      host.remove(); panel = null; previousFocus?.focus?.();
    }
    function collect() {
      scheduled = false;
      const found = new Map(), generated = new Map(), ambiguous = new Set();
      const players = new Map();
      for (const player of document.querySelectorAll('video, audio')) {
        const source = player.currentSrc && http(player.currentSrc);
        if (source && player.readyState >= 1) players.set(source, player);
      }
      for (const element of document.querySelectorAll(LINK_SELECTOR)) {
        if (found.size >= 1000) break;
        const blob = generatedAnchor(element);
        if (blob) {
          const filename = element.getAttribute('download') || '';
          if (generated.has(blob) && generated.get(blob) !== filename) ambiguous.add(blob);
          else generated.set(blob, filename);
          if (ambiguous.has(blob)) { found.delete(blob); continue; }
        }
        const url = blob || http(element.getAttribute('href') || element.dataset.downloadUrl || element.dataset.url || element.dataset.href);
        if (!url || found.has(url) || url.split('#')[0] === location.href.split('#')[0]) continue;
        const details = fileDetails(element, url, players.get(url));
        if (!details) continue;
        const label = (element.textContent?.trim() || element.getAttribute('aria-label') || element.getAttribute('download') || new URL(url).pathname.split('/').pop() || 'File').trim().replace(/\s+/g, ' ').slice(0, 200);
        found.set(url, { url, label, ...details, generated: Boolean(blob), originalName: blob ? generated.get(blob) : undefined,
          sha256: blob ? null : publisherChecksum(element, url) });
      }
      candidates = [...found.values()];
      for (const url of selected) if (!found.has(url)) selected.delete(url);
      render();
    }
    const matchesFilter = (candidate, query) => `${candidate.label} ${candidate.type} ${candidate.url}`.toLowerCase().includes(query);
    function render() {
      list.replaceChildren();
      const query = filter.value.toLowerCase().trim();
      const visible = candidates.filter(candidate => matchesFilter(candidate, query));
      for (const candidate of visible) {
        const item = document.createElement('li'), label = document.createElement('label');
        const check = document.createElement('input'); check.type = 'checkbox'; check.checked = selected.has(candidate.url);
        check.disabled = copying || settled.has(candidate.url) || uncertain.has(candidate.url) || !check.checked && selected.size >= 50;
        check.addEventListener('change', () => { check.checked ? selected.add(candidate.url) : selected.delete(candidate.url); render(); });
        const text = document.createElement('span'); text.textContent = candidate.label;
        const address = document.createElement('small');
        // Signed query strings are never copied into a label or tooltip.
        const url = new URL(candidate.url);
        const duration = candidate.duration !== undefined ? ` · ${durationText(candidate.duration)}`
          : ['Audio', 'Video'].includes(candidate.media) ? ' · Duration unknown' : '';
        address.textContent = candidate.generated
          ? `${candidate.type} · Generated browser file${settled.has(candidate.url) ? ' · Accepted' : uncertain.has(candidate.url) ? ' · Check VESPER' : ''}`
          : `${candidate.type}${duration} · ${url.host}${url.pathname}`;
        text.append(address);
        if (candidate.sha256) { const checksum = document.createElement('small'); checksum.textContent = 'Page SHA-256 available for desktop review'; text.append(checksum); }
        label.append(check, text); item.append(label); list.append(item);
      }
      const selectedFiles = candidates.filter(item => selected.has(item.url));
      const blobs = selectedFiles.filter(item => item.generated).length;
      const mixed = blobs > 0 && blobs < selectedFiles.length;
      review.textContent = blobs ? 'Copy with VESPER' : 'Review in VESPER';
      status.textContent = copying || copyStatus ? copyStatus : mixed ? 'Select generated browser files separately from ordinary links.'
        : candidates.length ? `${selected.size} selected · ${visible.length} matching files. Maximum 50 per review.`
        : 'No direct file links found on this page.';
      all.disabled = copying || visible.length === 0 || selected.size >= 50;
      guided.disabled = copying;
      filter.disabled = copying;
      review.disabled = copying || selected.size === 0 || mixed;
    }
    filter.addEventListener('input', render);
    all.addEventListener('click', () => {
      const query = filter.value.toLowerCase().trim();
      for (const candidate of candidates) if (selected.size < 50 && matchesFilter(candidate, query)
          && !settled.has(candidate.url) && !uncertain.has(candidate.url)) selected.add(candidate.url);
      render();
    });
    close.addEventListener('click', finish);
    async function copyGeneratedFiles(files) {
      if (copying || files.length === 0 || files.some(item => !item.generated)) return;
      copying = true; copyController = new AbortController();
      const signal = copyController.signal;
      let accepted = 0;
      try {
        for (const item of files) {
          if (signal.aborted || panel !== host) break;
          const link = VesperBrowserBody.generatedLink(document.querySelectorAll('a[download],area[download]'), item.url, location.origin);
          const anchors = [...document.querySelectorAll('a[download],area[download]')]
            .filter(el => el.href === item.url);
          if (!link || !anchors.length || anchors.some(el => (el.getAttribute('download') || '') !== item.originalName)) {
            copyStatus = 'A generated file changed or disappeared. Remaining copies were not started.';
            break;
          }
          copyStatus = `Copying ${accepted + 1} of ${files.length}…`; render();
          let lastUpdate = 0;
          try {
            await VesperBrowserBody.copyBlob({ url: link.url, origin: location.origin, generated: link, signal,
              onProgress(received, total) {
                const now = Date.now();
                if (now - lastUpdate < 300 && received !== total) return;
                lastUpdate = now;
                copyStatus = `Copying ${accepted + 1} of ${files.length} · ${Math.floor(received * 100 / total)}%`;
                if (panel === host) status.textContent = copyStatus;
              } });
            settled.add(item.url); selected.delete(item.url); accepted++;
          } catch {
            // The finish reply may have been lost. Never automatically retry
            // the ambiguous file or silently continue to the rest of the batch.
            uncertain.add(item.url); selected.delete(item.url);
            copyStatus = `${accepted} accepted · one copy unconfirmed. Check VESPER before retrying.`;
            break;
          }
        }
        if (accepted === files.length) copyStatus = `${accepted} browser files accepted by VESPER.`;
      } finally {
        copying = false; copyController = null;
        if (panel === host) collect();
      }
    }
    review.addEventListener('click', async () => {
      const files = candidates.filter(item => selected.has(item.url));
      if (copying || !files.length) return;
      if (files.every(item => item.generated)) return copyGeneratedFiles(files);
      if (files.some(item => item.generated)) return;
      review.disabled = true; status.textContent = 'Review the selected files in the VESPER desktop window…';
      try {
        const reply = await chrome.runtime.sendMessage({ type: 'smart-submit', items: [...selected].map(url => {
          const candidate = candidates.find(item => item.url === url);
          return { url, reviewLabel: candidate?.label, reviewType: candidate?.type, reviewDurationSeconds: candidate?.duration,
            ...(candidate?.sha256 ? { sha256: candidate.sha256 } : {}) };
        }) });
        if (reply?.ok) { finish(); return; }
        status.textContent = reply?.message || 'VESPER did not answer. No files were removed from Chrome.';
      } catch { status.textContent = 'The extension connection was closed. Refresh this page and try again.'; }
      review.disabled = false;
    });
    section.addEventListener('keydown', event => {
      if (event.key === 'Escape') { event.stopPropagation(); finish(); }
      if (event.key !== 'Tab') return;
      const focusable = [...section.querySelectorAll('button:not(:disabled),input:not(:disabled)')];
      const index = focusable.indexOf(shadow.activeElement);
      if (event.shiftKey && index <= 0) { event.preventDefault(); focusable.at(-1)?.focus(); }
      else if (!event.shiftKey && index === focusable.length - 1) { event.preventDefault(); focusable[0]?.focus(); }
    });
    footer.append(close, all, guided, review); section.append(heading, help, filter, list, status, footer); shadow.append(style, section); document.documentElement.append(host);
    document.addEventListener('loadedmetadata', scheduleCollect, true);
    observer = new MutationObserver(scheduleCollect);
    observer.observe(document.body || document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['href', 'download', 'src', 'data-url', 'data-href', 'data-download-url'] });
    collect(); filter.focus();
  }
  chrome.runtime.onMessage.addListener((message, sender) => {
    if (sender.id === chrome.runtime.id && message?.type === 'vesper-smart-select') open();
  });
})();
