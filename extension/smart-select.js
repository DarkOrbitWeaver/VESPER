// Runs only when explicitly opened from VESPER's context menu. No background page scanning.
(() => {
  'use strict';
  let panel, observer;
  const http = value => {
    try {
      const url = new URL(value, location.href);
      return /^https?:$/.test(url.protocol) && !url.username && !url.password ? url.href : null;
    } catch { return null; }
  };
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
    help.textContent = 'Select reusable file links. VESPER will ask you to review them on the desktop. For a button that creates a file, close this panel and click the original button; Chrome keeps POST and page-created downloads.';
    const filter = document.createElement('input'); filter.type = 'search'; filter.placeholder = 'Filter by name, address or file type'; filter.setAttribute('aria-label', 'Filter files');
    const list = document.createElement('ul');
    const status = document.createElement('p'); status.setAttribute('role', 'status');
    const footer = document.createElement('footer');
    const close = document.createElement('button'); close.textContent = 'Cancel';
    const all = document.createElement('button'); all.textContent = 'Select visible';
    const review = document.createElement('button'); review.textContent = 'Review in VESPER';
    const selected = new Set(); let candidates = []; let scheduled = false;
    const previousFocus = document.activeElement;
    function finish() { observer?.disconnect(); observer = null; host.remove(); panel = null; previousFocus?.focus?.(); }
    function collect() {
      scheduled = false;
      const found = new Map();
      for (const element of document.querySelectorAll('a[href],area[href],button[data-url],button[data-href],[data-download-url],[role=button][data-url]')) {
        if (found.size >= 1000) break;
        const url = http(element.getAttribute('href') || element.dataset.downloadUrl || element.dataset.url || element.dataset.href);
        if (!url || found.has(url) || url.split('#')[0] === location.href.split('#')[0]) continue;
        const label = (element.textContent || element.getAttribute('aria-label') || new URL(url).pathname.split('/').pop() || 'File').trim().replace(/\s+/g, ' ').slice(0, 200);
        found.set(url, { url, label });
      }
      candidates = [...found.values()]; render();
    }
    function render() {
      list.replaceChildren();
      const query = filter.value.toLowerCase().trim();
      const visible = candidates.filter(c => `${c.label} ${c.url}`.toLowerCase().includes(query));
      for (const candidate of visible) {
        const item = document.createElement('li'), label = document.createElement('label');
        const check = document.createElement('input'); check.type = 'checkbox'; check.checked = selected.has(candidate.url);
        check.disabled = !check.checked && selected.size >= 50;
        check.addEventListener('change', () => { check.checked ? selected.add(candidate.url) : selected.delete(candidate.url); render(); });
        const text = document.createElement('span'); text.textContent = candidate.label;
        const address = document.createElement('small');
        // Signed query strings are never copied into a label or tooltip.
        const url = new URL(candidate.url); address.textContent = url.host + url.pathname;
        text.append(address); label.append(check, text); item.append(label); list.append(item);
      }
      status.textContent = `${selected.size} selected · ${visible.length} matching links. Maximum 50 per review.`;
      review.disabled = selected.size === 0;
    }
    filter.addEventListener('input', render);
    all.addEventListener('click', () => { for (const c of candidates) if (selected.size < 50 && `${c.label} ${c.url}`.toLowerCase().includes(filter.value.toLowerCase().trim())) selected.add(c.url); render(); });
    close.addEventListener('click', finish);
    review.addEventListener('click', async () => {
      review.disabled = true; status.textContent = 'Review the selected files in the VESPER desktop window…';
      try {
        const reply = await chrome.runtime.sendMessage({ type: 'smart-submit', items: [...selected].map(url => ({ url })) });
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
    footer.append(close, all, review); section.append(heading, help, filter, list, status, footer); shadow.append(style, section); document.documentElement.append(host);
    observer = new MutationObserver(() => { if (!scheduled) { scheduled = true; queueMicrotask(collect); } });
    observer.observe(document.body || document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['href', 'data-url', 'data-href', 'data-download-url'] });
    collect(); filter.focus();
  }
  chrome.runtime.onMessage.addListener((message, sender) => {
    if (sender.id === chrome.runtime.id && message?.type === 'vesper-smart-select') open();
  });
})();
