// Player discovery is local to the page. The desktop app checks formats after a click.
(() => {
  'use strict';
  if (window.__vesperContent) return;          // never inject twice
  window.__vesperContent = true;

  const MIN_WIDTH = 220, MIN_HEIGHT = 120, FLY_SIZE = 44, FULLSCREEN_RADIUS = 100;
  let video = null;            // the player the butterfly is attached to
  let host = null, root = null, button = null, panel = null, receipt = null;
  let frame = 0, listening = false, pointer = null, panelRequest = 0, unwatchTheme = null;
  let perch = null, receiptTimer = 0, panelMotion = null, settleMotion = null;
  const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
  const protectedVideos = new WeakSet();
  const observed = new Set();
  const resize = new ResizeObserver(schedule);
  const visibility = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.target.isConnected) {
        visibility.unobserve(entry.target);
        observed.delete(entry.target);
      }
    }
    selectPlayer();
  }, { threshold: [0, 0.1, 0.25, 0.5, 0.75, 1] });

  const send = (message) => new Promise((resolve) => {
    try { chrome.runtime.sendMessage(message, (reply) => resolve(chrome.runtime.lastError ? { ok: false, code: 'extension-error' } : reply)); }
    catch { resolve({ ok: false, code: 'extension-error' }); }   // extension reloaded: this script is orphaned
  });

  // ------------------------------------------------------------ noticing media

  function visiblePlayer(el) {
    if (!el?.isConnected) return null;
    const r = el.getBoundingClientRect();
    const left = Math.max(0, r.left), right = Math.min(innerWidth, r.right);
    const top = Math.max(0, r.top), bottom = Math.min(innerHeight, r.bottom);
    if (right - left < MIN_WIDTH || bottom - top < MIN_HEIGHT) return null;
    const style = getComputedStyle(el);
    return style.visibility === 'hidden' || style.visibility === 'collapse' ? null : { left, top, right, bottom };
  }

  function selectPlayer() {
    const fullscreen = document.fullscreenElement;
    let best = null, bestArea = 0;
    for (const el of observed) {
      if (fullscreen && !fullscreen.contains(el)) continue;
      const r = visiblePlayer(el);
      if (!r) continue;
      const area = (r.right - r.left) * (r.bottom - r.top);
      if (area > bestArea) { best = el; bestArea = area; }
    }
    if (best) attach(best);
    else if (video && !video.isConnected) detach();
    else if (video) place();
  }

  function consider(el) {
    if (!(el instanceof HTMLMediaElement)) return;
    const src = el.currentSrc || el.src || '';
    const drm = !!el.mediaKeys || protectedVideos.has(el);
    if (el instanceof HTMLVideoElement && !observed.has(el) &&
        (src || el.srcObject || el.readyState >= 1 || el.querySelector('source[src]'))) {
      observed.add(el);
      visibility.observe(el);
    }
    const title = document.querySelector('meta[property="og:title"]')?.content || document.title || '';
    const poster = el.poster || document.querySelector('meta[property="og:image"]')?.content || '';
    const duration = Number.isFinite(el.duration) ? el.duration : null;
    void send({ type: 'media-element', src: drm || !/^https?:\/\//i.test(src) ? '' : src, title, poster, duration,
      unsupported: drm ? 'drm' : null });
  }

  // Capture-phase listeners on document: media events do not bubble, but they can be captured. One-time cost.
  for (const type of ['loadedmetadata', 'playing']) document.addEventListener(type, (e) => consider(e.target), { capture: true, passive: true });
  document.addEventListener('encrypted', (e) => {
    if (!(e.target instanceof HTMLMediaElement)) return;
    protectedVideos.add(e.target);
    if (e.target === video) closePanel();
    consider(e.target);
  }, { capture: true, passive: true });

  // ------------------------------------------------------------ the butterfly

  function element(tag, className, text = '') {
    const el = document.createElement(tag);
    if (className) el.className = className;
    if (text) el.textContent = text;
    if (tag === 'button') el.type = 'button';
    return el;
  }

  function build() {
    host = document.createElement('div');
    host.dataset.vesperPlayer = '';
    host.style.cssText = 'all:initial;position:fixed;z-index:2147483647;top:0;left:0;width:0;height:0;';
    root = host.attachShadow({ mode: 'closed' });
    const style = element('style');
    style.textContent = VesperTheme.css + `
        :host{all:initial}
        *{box-sizing:border-box;font-family:'Segoe UI',system-ui,sans-serif}
        .fly{position:fixed;width:${FLY_SIZE}px;height:${FLY_SIZE}px;border-radius:6px;border:0;background:none;display:flex;
          align-items:center;justify-content:center;cursor:pointer;padding:0;opacity:0;pointer-events:none;
          transform:translate(4px,-6px) scale(.95);transition:opacity .18s ease,transform .18s ease-out}
        .fly[hidden]{display:none}.fly.show,.fly:focus-visible{opacity:1;transform:none;pointer-events:auto}
        .fly:focus-visible{outline:2px solid #a5cfa8;outline-offset:1px}
        .fly img{width:40px;height:40px;object-fit:contain;pointer-events:none;filter:drop-shadow(0 1px 2px #0008);
          transform-origin:50% 85%;transition:transform .18s ease-out}
        .fly:hover img{transform:translateY(-3px) rotate(-6deg) scale(1.08)}
        .fly.clicked img{animation:perch-tap .24s ease-out}
        @keyframes perch-tap{35%{transform:translateY(-4px) scale(.87,1.07)}70%{transform:translateY(-1px) scale(1.09,.98)}}
        .panel{position:fixed;width:316px;max-width:calc(100vw - 16px);max-height:min(630px,calc(100vh - 16px));
          background:var(--surface);color:var(--text);border:1px solid var(--border);border-radius:10px;display:none;
          box-shadow:0 7px 22px #0003;overflow:auto;font-size:13px;transform-origin:top right}
        .panel.show{display:block;animation:picker-open .16s ease-out}
        @keyframes picker-open{from{opacity:0;transform:translateY(-4px)}to{opacity:1;transform:none}}
        .receipt{position:fixed;max-width:calc(100vw - 16px);padding:6px 10px;border-radius:7px;border:1px solid var(--border);
          background:var(--surface);color:var(--sage);box-shadow:0 3px 10px #0003;font-size:12px;line-height:1.4;
          pointer-events:none;opacity:0;transform:translateX(4px);transition:opacity .16s ease,transform .16s ease-out}
        .receipt.show{opacity:1;transform:none}
        @media(prefers-reduced-motion:reduce){
          .fly,.fly img,.panel.show,.receipt{animation:none;transition:none}
          .fly,.fly:hover img,.receipt{transform:none}
        }
        .head{display:flex;gap:10px;padding:13px 12px;border-bottom:1px solid var(--border);align-items:center;background:var(--paper)}
        .thumb{width:64px;height:41px;border-radius:6px;background:var(--wash) center/cover;flex:none}
        .t{font:600 14px/1.25 Georgia,serif;max-height:36px;overflow:hidden}.d{font-size:11px;color:var(--muted);margin-top:3px}
        .x{margin-left:auto;border:0;background:none;font-size:18px;color:var(--muted);cursor:pointer;align-self:flex-start;padding:0 2px}
        .list{padding:7px 8px}.opt{display:flex;gap:8px;align-items:center;width:100%;min-height:43px;border:1px solid transparent;
          background:none;text-align:left;padding:7px 8px;border-radius:7px;cursor:pointer;font-size:13px;color:var(--text)}
        .opt:hover,.opt[aria-checked=true]{background:var(--wash)}
        .opt[aria-checked=true]{border-color:var(--border)}
        .opt b{font-weight:600}.opt small{display:block;color:var(--muted);font-size:11px;margin-top:2px}
        .icon{width:14px;flex:none;color:var(--sage);text-align:center}
        .icon.best{color:var(--gold)}.icon.balanced{color:var(--blue)}.icon.audio{color:var(--violet)}
        .kind{font-size:10px;color:var(--sage);padding:2px 4px;border-radius:4px;background:var(--wash);margin-left:auto}
        .tick{width:13px;flex:none;color:var(--sage)}
        .more{display:block;width:calc(100% - 16px);margin:0 8px 8px;font-size:12px}
        .foot{display:flex;gap:8px;justify-content:flex-end;padding:10px 12px;border-top:1px solid var(--border);
          position:sticky;bottom:0;background:var(--surface)}
        .msg{padding:14px;font-size:12.5px;line-height:1.45}.msg.bad{color:var(--red)}
        .link{border:0;background:none;color:var(--blue);text-decoration:underline;cursor:pointer;font-size:12.5px;padding:0}
    `;
    button = element('button', 'fly');
    button.title = 'Find media with VESPER';
    button.setAttribute('aria-label', button.title);
    button.setAttribute('aria-expanded', 'false');
    const butterfly = element('img');
    butterfly.alt = '';
    butterfly.src = chrome.runtime.getURL('icons/butterfly-front.png');
    button.append(butterfly);
    panel = element('div', 'panel');
    panel.setAttribute('role', 'dialog');
    panel.setAttribute('aria-label', 'VESPER download options');
    receipt = element('div', 'receipt');
    receipt.setAttribute('role', 'status');
    root.append(style, button, panel, receipt);
    button.addEventListener('click', (e) => {
      e.stopPropagation();
      if (!reducedMotion.matches) button.classList.add('clicked');
      void togglePanel();
    });
    butterfly.addEventListener('animationend', () => button?.classList.remove('clicked'));
    root.addEventListener('keydown', (e) => {
      if (e.key === 'Escape' && panel.classList.contains('show')) {
        e.stopPropagation();
        closePanel();
        button.focus();
      }
    });
    unwatchTheme = VesperTheme.watch(host);
    mount();
  }

  function mount() {
    const fullscreen = document.fullscreenElement;
    // A video element in native fullscreen can't render the overlay as a child.
    const parent = fullscreen && !(fullscreen instanceof HTMLVideoElement) ? fullscreen : document.documentElement;
    if (host.parentElement !== parent) parent.appendChild(host);
    button.querySelector('img').src = chrome.runtime.getURL(fullscreen ? 'icons/butterfly-perched.png' : 'icons/butterfly-front.png');
  }
  document.addEventListener('fullscreenchange', () => {
    pointer = null;
    if (host) mount();
    selectPlayer();
  }, { passive: true });

  function attach(el) {
    if (video === el && host) { place(); return; }
    closePanel();
    resize.disconnect();
    video = el;
    if (!host) build();
    resize.observe(el);
    if (!listening) {
      listening = true;
      document.addEventListener('pointermove', onPointer, { passive: true, capture: true });
      document.addEventListener('pointerleave', clearPointer, { passive: true });
      window.addEventListener('scroll', schedule, { passive: true, capture: true });
      window.addEventListener('resize', schedule, { passive: true });
    }
    place();
  }

  function detach() {
    listening = false;
    closePanel();
    resize.disconnect();
    unwatchTheme?.();
    unwatchTheme = null;
    document.removeEventListener('pointermove', onPointer, true);
    document.removeEventListener('pointerleave', clearPointer);
    window.removeEventListener('scroll', schedule, true);
    window.removeEventListener('resize', schedule);
    cancelAnimationFrame(frame);
    frame = 0;
    perch = null;
    host?.remove(); host = root = button = panel = receipt = null; video = null;
  }

  function place() {
    if (!video || !video.isConnected) return detach();
    const fullscreen = document.fullscreenElement;
    const r = visiblePlayer(video);
    button.hidden = !r || !!fullscreen && (fullscreen instanceof HTMLVideoElement || !fullscreen.contains(video));
    if (button.hidden) { perch = null; closePanel(); return; }
    const x = r.right - FLY_SIZE - 10;
    const y = fullscreen ? r.top + 10 : perchTop(r, x);
    button.style.left = `${x}px`; button.style.top = `${y}px`;
    perch = { x, y };
    updateFlyVisibility();
    if (panel.classList.contains('show')) {
      panel.style.left = `${Math.max(8, x + FLY_SIZE - panel.offsetWidth)}px`;
      panel.style.top = `${Math.max(8, Math.min(y + FLY_SIZE + 5, innerHeight - panel.offsetHeight - 8))}px`;
    }
    if (receiptTimer) {
      const left = x - receipt.offsetWidth - 8;
      const beside = left >= 8;
      receipt.style.left = `${beside ? left : Math.max(8, x + FLY_SIZE - receipt.offsetWidth)}px`;
      receipt.style.top = `${Math.max(8, beside ? y + (FLY_SIZE - receipt.offsetHeight) / 2 : y + FLY_SIZE + 5)}px`;
    }
  }

  function perchTop(rect, x) {
    // When the player has its own strip above the picture, sit in that space.
    // Otherwise try the picture's upper edge, clear of links and player controls.
    function clear(y) {
      return y >= 4 && ![6, FLY_SIZE - 6].some(offset => document.elementsFromPoint(x + offset, y + 12)
        .some(el => el !== host && el.closest('button, a[href], input, [role="button"]')));
    }
    let parent = video.parentElement;
    for (let depth = 0; parent && depth < 4; depth++, parent = parent.parentElement) {
      const box = parent.getBoundingClientRect();
      const strip = rect.top - box.top;
      if (strip >= 14 && strip <= 76 && box.right >= rect.right - 8 && Math.abs(box.left - rect.left) <= 24) {
        const y = rect.top - Math.round(FLY_SIZE * 0.76);
        if (clear(y)) return y;
      }
    }
    const edge = rect.top - Math.round(FLY_SIZE * 0.70);
    return clear(edge) ? edge : rect.top + 8;
  }

  function schedule() { if (!frame) frame = requestAnimationFrame(() => { frame = 0; place(); }); }

  function updateFlyVisibility() {
    if (!perch || !button) return;
    const radius = FULLSCREEN_RADIUS + (button.classList.contains('show') ? 14 : 0);
    const nearby = pointer && Math.hypot(pointer.x - perch.x - FLY_SIZE / 2, pointer.y - perch.y - FLY_SIZE / 2) <= radius;
    button.classList.toggle('show', !document.fullscreenElement || panel.classList.contains('show') || !!receiptTimer || !!nearby);
  }

  function onPointer(e) {
    if (!document.fullscreenElement) return;
    pointer = { x: e.clientX, y: e.clientY };
    updateFlyVisibility();
  }
  function clearPointer() { pointer = null; updateFlyVisibility(); }

  // ------------------------------------------------------------ the quality picker

  let choice = null;
  function dismissReceipt() {
    clearTimeout(receiptTimer);
    receiptTimer = 0;
    receipt?.classList.remove('show');
  }

  function closePanel() {
    panelRequest++;
    panelMotion?.cancel();
    settleMotion?.cancel();
    panelMotion = settleMotion = null;
    dismissReceipt();
    if (receipt) receipt.textContent = '';
    if (panel) {
      panel.classList.remove('show');
      panel.inert = false;
    }
    button?.setAttribute('aria-expanded', 'false');
    updateFlyVisibility();
  }

  async function togglePanel() {
    if (panel.classList.contains('show')) return closePanel();
    await loadFormats();
  }

  async function loadFormats() {
    dismissReceipt();
    receipt.textContent = '';
    settleMotion?.cancel();
    settleMotion = null;
    const request = ++panelRequest;
    panel.classList.add('show');
    button.setAttribute('aria-expanded', 'true');
    const title = document.querySelector('meta[property="og:title"]')?.content || document.title || 'This video';
    const poster = video.poster || document.querySelector('meta[property="og:image"]')?.content || '';
    const seconds = Number.isFinite(video.duration) ? video.duration : null;
    if (video.mediaKeys || protectedVideos.has(video))
      return renderShell(title, poster, seconds, element('div', 'msg bad', 'This video is protected by DRM.'));

    const loading = element('div', 'msg', 'Loading formats…');
    loading.setAttribute('role', 'status');
    renderShell(title, poster, seconds, loading);
    const result = await send({ type: 'media-options', playerUrl: video.currentSrc || video.src || '', title });
    if (request !== panelRequest) return;
    if (!result?.ok || !result.data?.choices?.length) {
      const message = errorMessage(result);
      if (result?.code !== 'protected') appendRetry(message, loadFormats);
      return renderShell(title, poster, seconds, message);
    }
    const formats = result.data;
    choice = formats.choices[0];
    renderChoices(formats.title || title, formats.thumbnail || poster, formats.duration ?? seconds, formats);
  }

  function renderShell(title, poster, seconds, ...body) {
    const clock = seconds ? `${Math.floor(seconds / 3600) ? Math.floor(seconds / 3600) + ':' : ''}${String(Math.floor(seconds / 60) % 60).padStart(Math.floor(seconds / 3600) ? 2 : 1, '0')}:${String(Math.floor(seconds % 60)).padStart(2, '0')}` : '';
    const head = element('div', 'head');
    const thumb = element('div', 'thumb');
    if (/^https?:\/\//i.test(poster || '')) thumb.style.backgroundImage = `url("${poster.replace(/"/g, '%22')}")`;
    const labels = element('div');
    labels.append(element('div', 't', title), element('div', 'd', clock));
    const close = element('button', 'x', '×');
    close.setAttribute('aria-label', 'Close');
    close.onclick = closePanel;
    head.append(thumb, labels, close);
    panel.replaceChildren(head, ...body);
    place();
    return close;
  }

  function renderChoices(title, poster, seconds, formats, expanded = false) {
    const all = expanded ? [...formats.choices, ...formats.more] : formats.choices;
    const list = element('div', 'list');
    list.setAttribute('role', 'radiogroup');
    list.setAttribute('aria-label', 'Download format');
    const rows = [];
    function select(index, focus) {
      choice = all[index];
      for (const [i, option] of rows.entries()) {
        option.setAttribute('aria-checked', String(i === index));
        option.querySelector('.tick').textContent = i === index ? '✓' : '';
        option.tabIndex = i === index ? 0 : -1;
      }
      if (focus) rows[index].focus();
    }
    for (const c of all) {
      const option = element('button', 'opt');
      option.setAttribute('role', 'radio');
      const labels = element('span');
      labels.append(element('b', '', c.label), element('small', '', c.detail || ''));
      const [icon, tone] = { 'Best quality': ['♕', 'best'], Balanced: ['▣', 'balanced'],
        Smallest: ['⌁', ''], 'Audio only': ['♫', 'audio'] }[c.label] || ['›', ''];
      const mark = element('span', 'icon ' + tone, icon);
      mark.setAttribute('aria-hidden', 'true');
      option.append(mark, labels);
      if (c.container) option.append(element('span', 'kind', c.container));
      option.append(element('span', 'tick'));
      const index = rows.length;
      option.onclick = () => select(index, false);
      option.onkeydown = e => {
        const offset = { ArrowUp: -1, ArrowLeft: -1, ArrowDown: 1, ArrowRight: 1 }[e.key];
        if (!offset) return;
        e.preventDefault();
        e.stopPropagation();
        select((index + offset + all.length) % all.length, true);
      };
      rows.push(option);
      list.append(option);
    }
    select(Math.max(0, all.indexOf(choice)), false);
    const body = [list];
    if (!expanded && formats.more.length) {
      const more = element('button', 'vesper-button more', 'More formats ▾');
      more.onclick = () => {
        renderChoices(title, poster, seconds, formats, true);
        panel.querySelectorAll('.opt')[formats.choices.length]?.focus();
      };
      body.push(more);
    }
    const footer = element('div', 'foot');
    for (const mode of ['queue', 'download']) {
      const action = element('button', mode === 'download' ? 'vesper-button primary' : 'vesper-button', mode === 'download' ? 'Download' : 'Queue');
      action.onclick = () => deliver(title, poster, seconds, formats.mediaSession, choice.index, mode);
      footer.append(action);
    }
    renderShell(title, poster, seconds, ...body, footer);
  }

  async function deliver(title, poster, seconds, mediaSession, formatChoice, mode) {
    const request = panelRequest;
    const sending = element('div', 'msg', 'Sending to VESPER…');
    sending.setAttribute('role', 'status');
    const hadFocus = panel.contains(root.activeElement);
    const close = renderShell(title, poster, seconds, sending);
    if (hadFocus) close.focus({ preventScroll: true });
    const reply = await send({ type: 'media-select', mediaSession, formatChoice, mode });
    if (request !== panelRequest) return;
    if (reply?.ok) {
      const status = reply.results?.[0]?.status;
      return confirmSend(status === 'queued' ? 'Queued in VESPER' :
        status === 'already-submitted' ? 'Already in VESPER' : 'Sent to VESPER', request);
    }
    const error = errorMessage(reply);
    appendRetry(error, reply?.code === 'expired-selection' ? loadFormats
      : () => deliver(title, poster, seconds, mediaSession, formatChoice, mode));
    const hasFocus = panel.contains(root.activeElement);
    renderShell(title, poster, seconds, error);
    if (hasFocus) error.querySelector('button')?.focus({ preventScroll: true });
  }

  async function confirmSend(message, request) {
    if (panel.contains(root.activeElement)) button.focus({ preventScroll: true });
    panel.inert = true;
    if (!reducedMotion.matches) {
      panelMotion = panel.animate([{ opacity: 1, transform: 'none' },
        { opacity: 0, transform: 'translate(5px,-7px) scale(.96)' }], { duration: 145, easing: 'ease-in', fill: 'forwards' });
      await panelMotion.finished.catch(() => {});
    }
    if (request !== panelRequest) return;
    closePanel();
    receipt.textContent = message;
    receipt.classList.add('show');
    receiptTimer = setTimeout(() => { dismissReceipt(); updateFlyVisibility(); }, 1750);
    if (!reducedMotion.matches) {
      settleMotion = button.firstElementChild.animate([
        { transform: 'none' },
        { transform: 'translate(-3px,-6px) rotate(-8deg) scaleX(.90)', offset: .42 },
        { transform: 'translate(1px,-2px) rotate(3deg)', offset: .76 },
        { transform: 'none' }
      ], { duration: 340, easing: 'ease-out' });
    }
    place();
  }

  function appendRetry(message, action) {
    const retry = element('button', 'link', 'Try again');
    retry.onclick = action;
    message.append(' ', retry);
  }

  function errorMessage(reply) {
    const text = {
      'host-missing': 'The browser helper needs registration. Use Repair helper in VESPER’s Browser settings.',
      'app-unavailable': 'Could not connect to the desktop helper.',
      'host-error': 'The connection to VESPER failed.',
      duplicate: 'This link is already in VESPER.',
      cancelled: 'Cancelled in VESPER.',
      timeout: 'VESPER didn’t answer in time.',
      unsupported: reply?.message || 'This source can’t be downloaded.'
    }[reply?.code] || reply?.message || 'Something went wrong.';
    return element('div', 'msg bad', text);
  }

  document.addEventListener('pointerdown', (e) => { if (panel?.classList.contains('show') && !e.composedPath().includes(host)) closePanel(); }, { capture: true, passive: true });
  document.querySelectorAll('video').forEach(consider);
})();
