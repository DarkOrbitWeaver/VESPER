// Player discovery is local to the page. The desktop app checks formats after a click.
(() => {
  'use strict';
  if (window.__vesperContent) return;          // never inject twice
  window.__vesperContent = true;

  const lifetime = new AbortController();
  const { signal } = lifetime;
  const MIN_WIDTH = 220, MIN_HEIGHT = 120, FLY_SIZE = 44, FULLSCREEN_RADIUS = 100;
  const PREVIEW_EXIT_GRACE = 900, PREVIEW_MAX_LATCH = 2200;
  let video = null;            // the player the butterfly is attached to
  let host = null, root = null, button = null, panel = null, receipt = null;
  let frame = 0, listening = false, pointer = null, pointerTarget = null, panelRequest = 0, unwatchTheme = null;
  let perch = null, companion = null, receiptTimer = 0, retryTimer = 0, previewExitTimer = 0;
  let previewRemovedAt = 0, panelMotion = null;
  let activePreview = null;
  const pageAddress = () => location.href.split('#', 1)[0];
  let pageUrl = pageAddress(), mediaChangedAt = 0, panelMedia = null;
  let motionMode = 'animate', unwatchMotion = null;
  const still = () => motionMode === 'still';
  const protectedVideos = new WeakSet();
  const playback = new WeakMap();
  const prefetchedPlayers = new WeakMap();
  let generation = 0;
  const observed = new Set();
  const intersecting = new WeakMap();
  const resize = new ResizeObserver(schedule);
  const visibility = new IntersectionObserver((entries) => {
    if (!alive()) return;
    for (const entry of entries) {
      if (!entry.target.isConnected) {
        visibility.unobserve(entry.target);
        observed.delete(entry.target);
      } else intersecting.set(entry.target, entry.isIntersecting);
    }
    selectPlayer();
  }, { threshold: [0, 0.1, 0.25, 0.5, 0.75, 1] });

  function retire() {
    if (signal.aborted) return;
    lifetime.abort();
    visibility.disconnect();
    observed.clear();
    unwatchMotion?.();
    detach();
    delete window.__vesperContent;
  }

  function alive() {
    if (signal.aborted) return false;
    if (VesperTheme.available()) return true;
    // Reloading the extension can leave page listeners behind until the tab reloads.
    retire();
    return false;
  }

  async function send(message) {
    if (!alive()) return null;
    try { return await chrome.runtime.sendMessage(message); }
    catch (error) {
      if (VesperTheme.contextGone(error)) { retire(); return null; }
      return { ok: false, code: 'extension-error' };
    }
  }

  function syncPage() {
    if (!alive()) return;
    const current = pageAddress();
    if (current === pageUrl) return;
    const changed = VesperPlayerSource.identity(current) !== VesperPlayerSource.identity(pageUrl);
    pageUrl = current;
    if (!changed) return;
    mediaChangedAt = Date.now();
    activePreview = null;
    pointerTarget = null;
    if (panelMedia || receiptTimer) closePanel();
    if (video && !video.isConnected) detach();
  }
  window.navigation?.addEventListener('currententrychange', refreshPlayers, { signal });
  window.addEventListener('popstate', refreshPlayers, { signal });

  function playerSource(player) {
    return player.currentSrc || player.src || '';
  }

  function isCurrentRequest(request) {
    if (!alive() || request !== panelRequest || !panelMedia) return false;
    const pageMatches = panelMedia.documentIdentity === VesperPlayerSource.identity(pageAddress());
    const previewMatches = !activePreview || activePreview.key === panelMedia.targetKey;
    const binding = panelMedia.player && playback.get(panelMedia.player);
    const sourceMatches = !panelMedia.generation || !binding || binding.generation === panelMedia.generation;
    if (pageMatches && previewMatches && sourceMatches) return true;
    closePanel();
    return false;
  }

  // ------------------------------------------------------------ noticing media

  function visiblePlayer(el) {
    if (!el?.isConnected || intersecting.get(el) === false) return null;
    const r = el.getBoundingClientRect();
    const left = Math.max(0, r.left), right = Math.min(innerWidth, r.right);
    const top = Math.max(0, r.top), bottom = Math.min(innerHeight, r.bottom);
    if (right - left < MIN_WIDTH || bottom - top < MIN_HEIGHT) return null;
    const style = getComputedStyle(el);
    return style.visibility === 'hidden' || style.visibility === 'collapse' ? null : { left, top, right, bottom };
  }

  function cardContext(node, includePlayer) {
    if (!(node instanceof Element)) return null;
    const origin = new URL(pageAddress()).origin;
    for (let card = node, depth = 0; card && depth < 9 && card !== document.body; card = card.parentElement, depth++) {
      const allVideos = [...card.querySelectorAll('video')];
      if (allVideos.length > 4) continue;
      const videos = includePlayer ? allVideos.filter(el => observed.has(el) && visiblePlayer(el)) : [];
      if (includePlayer && !videos.length) continue;
      const pages = new Map();
      for (const link of [...card.querySelectorAll('a[href]')].slice(0, 32)) {
        try {
          const url = new URL(link.href);
          if (url.origin !== origin) continue;
          const identity = VesperPlayerSource.identity(url.href);
          if (/^(youtube|instagram|tiktok|facebook):/.test(identity || '')) pages.set(identity, url.href);
        } catch { /* Ignore malformed page links. */ }
      }
      if (pages.size !== 1) continue;
      videos.sort((a, b) => {
        const area = el => { const r = el.getBoundingClientRect(); return r.width * r.height * (el.paused ? 1 : 1.15); };
        return area(b) - area(a);
      });
      return { page: pages.values().next().value, player: videos[0] };
    }
    return null;
  }

  function cardPage(node) { return cardContext(node, false)?.page || null; }

  function cardPreview(target) {
    const card = cardContext(target, true);
    if (card?.player) {
      const page = card.page;
      const player = card.player;
      const src = playerSource(player) || (activePreview?.player === player ? activePreview.binding?.src : '') || '';
      const key = `${VesperPlayerSource.identity(page)}|${page}|${src}|${playback.get(player)?.generation || 0}`;
      return { player, page, key };
    }
    return null;
  }

  function activatePreview(preview) {
    if (!preview || (preview.player === activePreview?.player && preview.key === activePreview?.key)) return false;
    activePreview = preview;
    consider(preview.player);
    if (panelMedia && panelMedia.targetKey !== preview.key) closePanel();
    return true;
  }

  function refreshHoveredPreview(player) {
    if (!pointer) return false;
    const target = document.elementFromPoint(pointer.x, pointer.y) || pointerTarget;
    const preview = cardPreview(target);
    return preview?.player === player && activatePreview(preview);
  }

  function refreshPlayer(el) {
    if (!observed.has(el)) return;
    refreshHoveredPreview(el);
    selectPlayer();
  }

  function pageForPlayer(el) {
    return cardPage(el) || (activePreview?.player === el ? activePreview.page : pageAddress());
  }

  function selectPlayer() {
    if (!alive()) return;
    if (panelMedia && video === panelMedia.player && !video.isConnected) { place(); return; }
    if (holdMissingPreview()) { place(); return; }
    const fullscreen = document.fullscreenElement;
    let best = null, bestArea = 0;
    if (activePreview && (!fullscreen || fullscreen.contains(activePreview.player))) {
      const rect = visiblePlayer(activePreview.player);
      if (rect) { best = activePreview.player; bestArea = Infinity; }
    }
    for (const el of observed) {
      if (bestArea === Infinity) break;
      if (fullscreen && !fullscreen.contains(el)) continue;
      const r = visiblePlayer(el);
      if (!r) continue;
      const area = (r.right - r.left) * (r.bottom - r.top) * (el.paused ? 1 : 1.15);
      if (area > bestArea) { best = el; bestArea = area; }
    }
    if (best) attach(best);
    else if (video && !video.isConnected) detach();
    else if (video) place();
  }

  function consider(el) {
    if (!alive() || !(el instanceof HTMLMediaElement)) return;
    const src = el.currentSrc || el.src || '';
    if (el instanceof HTMLVideoElement && !observed.has(el) &&
        (src || el.srcObject || el.readyState >= 1 || el.querySelector('source[src]'))) {
      observed.add(el);
      visibility.observe(el);
    }
    const page = pageForPlayer(el);
    const previous = playback.get(el);
    if (el.readyState >= 1 && (previous?.src !== src || previous?.page !== page)) {
      const changedAt = Date.now();
      const binding = { src, page, generation: ++generation, changedAt };
      playback.set(el, binding);
      if (activePreview?.player === el) {
        activePreview = { ...activePreview, page, src,
          key: `${VesperPlayerSource.identity(page)}|${page}|${src}|${binding.generation}`, binding };
        if (panelMedia && panelMedia.targetKey !== activePreview.key) closePanel();
      }
      if (el === video) mediaChangedAt = changedAt;
    }
  }

  function refreshPlayers() {
    if (!alive()) return;
    syncPage();
    document.querySelectorAll('video').forEach(consider);
    selectPlayer();
  }

  function prefetchPlayer(el) {
    if (!alive() || el !== video || !(el instanceof HTMLVideoElement) || !observed.has(el)
        || protectedVideos.has(el) || el.mediaKeys || !visiblePlayer(el)) return;
    const src = playerSource(el);
    const targetPage = pageForPlayer(el);
    const identity = VesperPlayerSource.identity(targetPage);
    if (!identity) return;
    const binding = playback.get(el);
    const current = binding?.src === src && VesperPlayerSource.identity(binding.page) === identity;
    const key = `${identity}|${targetPage}|${src}|${current ? binding.generation : 0}`;
    if (prefetchedPlayers.get(el) === key) return;
    prefetchedPlayers.set(el, key);
    const title = current ? document.title.replace(/\s+- YouTube$/, '') || 'This video' : 'This video';
    void send({ type: 'media-options', prefetch: true, pageUrl: targetPage,
      mediaChangedAt: current ? binding.changedAt : mediaChangedAt, title,
      playerPlaying: !el.paused && !el.ended, playerPageUrl: targetPage,
      playerUrl: current ? src : '', generation: current ? binding.generation : 0,
      manifestUrl: current && !VesperPlayerSource.http(src) ? declaredManifest(identity) : undefined
    }).then(reply => {
      if (reply?.data?.prefetchSkipped && prefetchedPlayers.get(el) === key) prefetchedPlayers.delete(el);
    });
  }

  // Capture-phase listeners on document: media events do not bubble, but they can be captured. One-time cost.
  for (const type of ['loadedmetadata', 'playing']) document.addEventListener(type, (e) => {
    consider(e.target);
    refreshPlayer(e.target);
    if (e.target === video) prefetchPlayer(e.target);
  }, { capture: true, passive: true, signal });
  for (const type of ['emptied', 'loadstart']) document.addEventListener(type, (e) => {
    if (!alive()) return;
    if (type === 'emptied') {
      protectedVideos.delete(e.target);
      if (activePreview?.player === e.target && playback.has(e.target))
        activePreview = { ...activePreview, binding: playback.get(e.target) };
      playback.delete(e.target);
    }
    if (e.target !== video) return;
    if (type === 'emptied') {
      mediaChangedAt = Date.now();
      schedule();
    }
    if (!panelMedia && receiptTimer) closePanel();
  }, { capture: true, passive: true, signal });
  document.addEventListener('encrypted', (e) => {
    if (!alive() || !(e.target instanceof HTMLMediaElement)) return;
    protectedVideos.add(e.target);
    if (e.target === video) closePanel();
    consider(e.target);
  }, { capture: true, passive: true, signal });

  // ------------------------------------------------------------ the butterfly

  // "1:23", "01:23:45" or plain seconds -> seconds. Empty or nonsense -> null (the field's default applies).
  function parseClock(text) {
    const value = String(text || '').trim();
    if (!value) return null;
    if (/^\d+(\.\d+)?$/.test(value)) return Number(value);
    const parts = value.split(':').map(Number);
    if (parts.length > 3 || parts.some(part => !Number.isFinite(part) || part < 0)) return null;
    return parts.reduce((total, part) => total * 60 + part, 0);
  }
  function clockText(seconds) {
    const total = Math.max(0, Math.round(seconds));
    const h = Math.floor(total / 3600), m = Math.floor(total % 3600 / 60), s = total % 60;
    return (h ? `${h}:${String(m).padStart(2, '0')}` : String(m)) + ':' + String(s).padStart(2, '0');
  }

  function element(tag, className, text = '') {
    const el = document.createElement(tag);
    if (className) el.className = className;
    if (text) el.textContent = text;
    if (tag === 'button') el.type = 'button';
    return el;
  }

  function build(atlasUrl) {
    host = document.createElement('div');
    host.dataset.vesperPlayer = '';
    host.style.cssText = 'all:initial;position:fixed;z-index:2147483647;top:0;left:0;width:0;height:0;';
    root = host.attachShadow({ mode: 'closed' });
    const style = element('style');
    style.textContent = VesperTheme.css + `
        :host{all:initial}
        *{box-sizing:border-box;font-family:'Segoe UI',system-ui,sans-serif}
        .fly{position:fixed;left:0;top:0;width:${FLY_SIZE}px;height:${FLY_SIZE}px;border-radius:6px;border:0;background:none;display:flex;
          align-items:center;justify-content:center;cursor:pointer;padding:0;opacity:0;pointer-events:none;
          transition:opacity .18s ease;touch-action:manipulation}
        .fly[hidden]{display:none}.fly.show,.fly:focus-visible{opacity:1;pointer-events:auto}
        .fly canvas{transition:translate 160ms ease-out}.fly:hover canvas{translate:0 -2px}
        @media (prefers-reduced-motion:reduce){.fly canvas{transition:none}.fly:hover canvas{translate:none}}
        .fly:focus-visible{outline:2px solid #fffbee;outline-offset:2px;box-shadow:0 0 0 4px #1f3d2b;border-radius:8px}
        .fly canvas{width:100%;height:100%;pointer-events:none;filter:drop-shadow(0 1px 2px #0008)}
        .fly-fallback{display:grid;place-items:center;width:29px;height:29px;border-radius:7px;
          background:var(--green);color:#fffbee;font-size:19px;pointer-events:none}
        .fly.companion-ready .fly-fallback{display:none}
        .panel{position:fixed;width:316px;max-width:calc(100vw - 16px);max-height:min(630px,calc(100vh - 16px));
          background:var(--surface);color:var(--text);border:1px solid var(--border);border-radius:10px;display:none;
          box-shadow:0 7px 22px #0003;overflow-x:hidden;overflow-y:auto;font-size:13px;transform-origin:top right}
        .panel.show{display:block;animation:picker-open .16s ease-out}
        @keyframes picker-open{from{opacity:0;transform:translateY(-4px)}to{opacity:1;transform:none}}
        .receipt{position:fixed;max-width:calc(100vw - 16px);padding:6px 10px;border-radius:7px;border:1px solid var(--border);
          background:var(--surface);color:var(--sage);box-shadow:0 3px 10px #0003;font-size:12px;line-height:1.4;
          pointer-events:none;opacity:0;transform:translateX(4px);transition:opacity .16s ease,transform .16s ease-out}
        .receipt.show{opacity:1;transform:none}
        :host([data-vesper-still]) :is(.fly,.panel.show,.receipt){animation:none;transition:none}
        :host([data-vesper-still]) .receipt{transform:none}
        .head{display:flex;gap:10px;min-width:0;padding:13px 12px;border-bottom:1px solid var(--border);align-items:center;background:var(--paper)}
        .head>div:nth-child(2){flex:1 1 auto;min-width:0;overflow:hidden}
        .thumb{width:64px;height:41px;border-radius:6px;background:var(--wash) center/cover;flex:none}
        .t{display:-webkit-box;-webkit-box-orient:vertical;-webkit-line-clamp:2;font:600 14px/1.25 Georgia,serif;
          overflow:hidden;overflow-wrap:anywhere}.d{font-size:11px;color:var(--muted);margin-top:3px;overflow-wrap:anywhere}
        .x{flex:none;margin-left:auto;border:0;background:none;font-size:18px;color:var(--muted);cursor:pointer;align-self:flex-start;padding:0 2px}
        .output{display:flex;flex-wrap:wrap;min-width:0;justify-content:flex-end;align-items:center;gap:8px;padding:8px 12px 0;color:var(--muted);font-size:11px}
        .clip{display:flex;flex-wrap:wrap;align-items:center;gap:8px;padding:8px 12px 0;font-size:12px;color:var(--text)}
        .clip[hidden],.clip .fields[hidden],.hint[hidden]{display:none}
        .clip .fields{display:flex;align-items:center;gap:6px;margin-left:auto}
        .clip .fields small{color:var(--muted);font-size:11px}
        .clip input[type=text]{width:62px;border:1px solid var(--border);border-radius:6px;background:var(--surface);color:var(--text);font:inherit;padding:3px 6px}
        .clip input[type=checkbox]{accent-color:var(--green);margin:0}
        small.hint{display:block;padding:4px 12px 0;color:var(--muted);font-size:11px}
        .output[hidden]{display:none}
        .output select{max-width:100%;min-width:0;border:1px solid var(--border);border-radius:6px;background:var(--surface);color:var(--text);
          color-scheme:inherit;padding:4px 6px;font:inherit;cursor:pointer}
        .output select:focus-visible{outline:2px solid var(--sage);outline-offset:2px}
        .list{min-width:0;padding:7px 8px}.opt{display:flex;gap:8px;align-items:center;width:100%;min-width:0;min-height:43px;border:1px solid transparent;
          background:none;text-align:left;padding:7px 8px;border-radius:7px;cursor:pointer;font-size:13px;color:var(--text)}
        .opt:hover,.opt[aria-checked=true]{background:var(--wash)}
        .opt[aria-checked=true]{border-color:var(--border)}
        .opt>span:nth-child(2){flex:1 1 auto;min-width:0;overflow-wrap:anywhere}
        .opt b{font-weight:600}.opt small{display:block;color:var(--muted);font-size:11px;margin-top:2px}
        .icon{width:14px;flex:none;color:var(--sage);text-align:center}
        .icon.best{color:var(--gold)}.icon.balanced{color:var(--blue)}.icon.audio{color:var(--violet)}
        .kind{flex:none;max-width:35%;overflow:hidden;text-overflow:ellipsis;font-size:10px;color:var(--sage);padding:2px 4px;border-radius:4px;background:var(--wash);margin-left:auto}
        .tick{width:13px;flex:none;color:var(--sage)}
        .more{display:block;width:calc(100% - 16px);margin:0 8px 8px;font-size:12px}
        .foot{display:flex;gap:8px;justify-content:flex-end;padding:10px 12px;border-top:1px solid var(--border);
          position:sticky;bottom:0;background:var(--surface)}
        .msg{padding:14px;font-size:12.5px;line-height:1.45}.msg.bad{color:var(--red)}
        .link{border:0;background:none;color:var(--blue);text-decoration:underline;cursor:pointer;font-size:12.5px;padding:0}
    `;
    button = element('button', 'fly');
    button.title = 'Find media with VESPER (Alt+Shift+V)';
    button.setAttribute('aria-label', button.title);
    button.setAttribute('aria-expanded', 'false');
    const fallback = element('span', 'fly-fallback', '↓');
    fallback.setAttribute('aria-hidden', 'true');
    button.append(fallback);
    companion = VesperCompanion.create(button, atlasUrl, still());
    host.toggleAttribute('data-vesper-still', still());
    panel = element('div', 'panel');
    panel.setAttribute('role', 'dialog');
    panel.setAttribute('aria-label', 'VESPER download options');
    receipt = element('div', 'receipt');
    receipt.setAttribute('role', 'status');
    root.append(style, button, panel, receipt);
    button.addEventListener('click', (e) => {
      e.stopPropagation();
      void togglePanel();
    }, { signal });
    button.addEventListener('focus', updateFlyVisibility, { signal });
    button.addEventListener('blur', updateFlyVisibility, { signal });
    root.addEventListener('keydown', (e) => {
      if (e.key === 'Escape' && panel.classList.contains('show')) {
        e.stopPropagation();
        closePanel();
        button.focus();
      }
    }, { signal });
    unwatchTheme = VesperTheme.watch(host);
    mount();
  }

  function mount() {
    const fullscreen = document.fullscreenElement;
    // A video element in native fullscreen can't render the overlay as a child.
    const parent = fullscreen && !(fullscreen instanceof HTMLVideoElement) ? fullscreen : document.documentElement;
    if (host.parentElement !== parent) parent.appendChild(host);
  }
  document.addEventListener('fullscreenchange', () => {
    if (!alive()) return;
    pointer = null;
    if (host) mount();
    selectPlayer();
  }, { passive: true, signal });
  document.addEventListener('visibilitychange', () => { if (alive()) updateFlyVisibility(); }, { signal });
  document.addEventListener('keydown', e => {
    if (!alive() || !e.altKey || !e.shiftKey || e.key.toLowerCase() !== 'v' || !perch || button?.hidden) return;
    e.preventDefault();
    button?.focus({ preventScroll: true });
  }, { signal });

  function attach(el) {
    if (video === el && host) {
      clearTimeout(previewExitTimer);
      previewExitTimer = previewRemovedAt = 0;
      place();
      return;
    }
    clearTimeout(previewExitTimer);
    previewExitTimer = previewRemovedAt = 0;
    closePanel();
    resize.disconnect();
    video = el;
    if (!host) {
      try { build(chrome.runtime.getURL('icons/vesper-companion-atlas.png')); }
      catch (error) { if (!VesperTheme.contextGone(error)) throw error; retire(); return; }
    }
    resize.observe(el);
    companion?.scheduleIdle(idleContext);
    if (!listening) {
      listening = true;
      document.addEventListener('pointermove', onPointer, { passive: true, capture: true, signal });
      document.addEventListener('pointerleave', clearPointer, { passive: true, signal });
      window.addEventListener('scroll', schedule, { passive: true, capture: true, signal });
      window.addEventListener('resize', schedule, { passive: true, signal });
    }
    place();
  }

  function detach() {
    listening = false;
    activePreview = null;
    companion?.stopIdle();
    clearTimeout(previewExitTimer);
    previewExitTimer = previewRemovedAt = 0;
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
    companion?.dispose();
    companion = null;
    host?.remove(); host = root = button = panel = receipt = null; video = null;
  }

  function place(continuous = false) {
    if (!alive()) return;
    if (!video) return detach();
    const fullscreen = document.fullscreenElement;
    const held = holdMissingPreview();
    if (held) {
      button.hidden = false;
    } else if (!video.isConnected) {
      if (!perch || !panelMedia || !panel?.classList.contains('show')) return detach();
      button.hidden = true;
    } else {
      const rect = visiblePlayer(video);
      button.hidden = !rect || !!fullscreen && (fullscreen instanceof HTMLVideoElement || fullscreen instanceof HTMLIFrameElement || !fullscreen.contains(video));
      if (button.hidden) { perch = null; closePanel(); return; }
      perch = playerPerch(rect, Boolean(fullscreen));
      companion.setPerch(perch.x, perch.y, perch.faceLeft, Boolean(fullscreen), { continuous });
    }
    if (!perch) return detach();
    const { x, y, faceLeft } = perch;
    updateFlyVisibility();
    if (panel.classList.contains('show')) {
      const left = faceLeft ? x + FLY_SIZE - panel.offsetWidth : x;
      panel.style.left = `${Math.max(8, Math.min(left, innerWidth - panel.offsetWidth - 8))}px`;
      panel.style.top = `${Math.max(8, Math.min(y + FLY_SIZE + 5, innerHeight - panel.offsetHeight - 8))}px`;
    }
    if (receiptTimer) {
      const left = x - receipt.offsetWidth - 8;
      const beside = left >= 8;
      receipt.style.left = `${beside ? left : Math.max(8, x + FLY_SIZE - receipt.offsetWidth)}px`;
      receipt.style.top = `${Math.max(8, beside ? y + (FLY_SIZE - receipt.offsetHeight) / 2 : y + FLY_SIZE + 5)}px`;
    }
  }

  function playerPerch(rect, fullscreen) {
    const preview = activePreview?.player === video && !fullscreen;
    const right = { x: rect.right - FLY_SIZE - 10, faceLeft: true };
    const left = { x: rect.left + 10, faceLeft: false };
    const corners = preview ? [left, right] : [right, left];
    const border = Math.abs(video.getBoundingClientRect().top - rect.top) < 2;
    const clear = (x, y) => y >= 4 && ![8, FLY_SIZE / 2, FLY_SIZE - 8].some(offset =>
      document.elementsFromPoint(x + offset, y + FLY_SIZE / 2)
        .some(el => el !== host && el.closest('button, a[href], input, [role="button"]')));
    const positions = fullscreen || !border ? [rect.top + 8] : [rect.top - VesperCompanion.footY, rect.top + 8];
    for (const y of positions) {
      const corner = corners.find(({ x }) => clear(x, y));
      if (corner) return { ...corner, y };
    }
    return { ...corners[0], y: rect.top + 8 };
  }

  function schedule() { if (alive() && !frame) frame = requestAnimationFrame(() => { frame = 0; place(true); }); }

  function updateFlyVisibility() {
    if (!button || !companion) return;
    if (!perch || button.hidden) { companion.setVisible(false); return; }
    const radius = FULLSCREEN_RADIUS + (button.classList.contains('show') ? 14 : 0);
    const nearby = pointer && Math.hypot(pointer.x - perch.x - FLY_SIZE / 2, pointer.y - perch.y - FLY_SIZE / 2) <= radius;
    const show = !document.fullscreenElement || panel.classList.contains('show') || !!receiptTimer || !!nearby || button.matches(':focus-visible');
    button.classList.toggle('show', show);
    companion.setVisible(show && !document.hidden);
  }

  let pointerMovedAt = 0;
  // What the butterfly needs to know before it may stir on its own (vesper-companion.js scheduleIdle).
  const idleContext = {
    paused: () => !video || video.paused || video.ended,
    mostlyVisible: () => {
      if (!video) return false;
      const r = video.getBoundingClientRect();
      const visible = Math.max(0, Math.min(r.bottom, innerHeight) - Math.max(r.top, 0)) * Math.max(0, Math.min(r.right, innerWidth) - Math.max(r.left, 0));
      return visible >= r.width * r.height * 0.5;
    },
    pointerIdle: ms => Date.now() - pointerMovedAt >= ms
  };

  function onPointer(e) {
    if (!alive()) return;
    pointerMovedAt = Date.now();
    pointer = { x: e.clientX, y: e.clientY };
    if (e.target !== pointerTarget) {
      pointerTarget = e.target;
      if (activatePreview(cardPreview(e.target))) selectPlayer();
    }
    updateFlyVisibility();
    holdMissingPreview();
    companion?.pointerMove(e.clientX, e.clientY);
  }
  function clearPointer() {
    pointer = pointerTarget = null;
    companion?.pointerMove(-Infinity, -Infinity);
    updateFlyVisibility();
    holdMissingPreview();
  }

  function previewLatched() {
    if (panel?.classList.contains('show') || button?.matches(':hover,:focus-visible')) return true;
    return !!pointer && !!perch && Math.hypot(pointer.x - perch.x - FLY_SIZE / 2, pointer.y - perch.y - FLY_SIZE / 2) <= 90;
  }

  function previewMissing() {
    return !!video && activePreview?.player === video && (video.readyState < 1 || !visiblePlayer(video));
  }

  function holdMissingPreview() {
    if (document.fullscreenElement || !perch || !previewMissing()) return false;
    if (panelMedia?.player === video) return true;
    if (!previewRemovedAt) previewRemovedAt = Date.now();
    if (!previewExitTimer) {
      const player = video;
      const elapsed = Date.now() - previewRemovedAt;
      const delay = Math.min(PREVIEW_EXIT_GRACE, Math.max(0, PREVIEW_MAX_LATCH - elapsed));
      previewExitTimer = setTimeout(() => {
        previewExitTimer = 0;
        if (video !== player || !previewMissing()) { previewRemovedAt = 0; return; }
        if (panelMedia) return;
        if (previewLatched() && Date.now() - previewRemovedAt < PREVIEW_MAX_LATCH) {
          holdMissingPreview();
          return;
        }
        activePreview = null;
        detach();
      }, delay);
    }
    return true;
  }

  // ------------------------------------------------------------ the quality picker

  let choice = null;
  function dismissReceipt() {
    clearTimeout(receiptTimer);
    receiptTimer = 0;
    receipt?.classList.remove('show');
  }

  function closePanel(acknowledged = false) {
    panelRequest++;
    panelMedia = null;
    panelMotion?.cancel();
    panelMotion = null;
    clearTimeout(retryTimer);
    retryTimer = 0;
    dismissReceipt();
    if (receipt) receipt.textContent = '';
    if (panel) {
      panel.classList.remove('show');
      panel.inert = false;
    }
    button?.setAttribute('aria-expanded', 'false');
    companion?.setPickerOpen(false, acknowledged);
    updateFlyVisibility();
    holdMissingPreview();
  }

  async function togglePanel() {
    if (!alive()) return;
    syncPage();
    if (panel.classList.contains('show')) return closePanel();
    await loadFormats();
  }

  async function loadFormats() {
    syncPage();
    if ((!video?.isConnected && activePreview?.player !== video) || !panel) return;
    dismissReceipt();
    receipt.textContent = '';
    const request = ++panelRequest;
    const player = video;
    const preview = activePreview?.player === player ? activePreview : null;
    const src = playerSource(player) || preview?.binding?.src || '';
    const targetPage = activePreview?.player === player ? activePreview.page : pageForPlayer(player);
    const identity = VesperPlayerSource.identity(targetPage);
    const binding = playback.get(player) || preview?.binding;
    const current = !previewMissing() && binding?.src === src && VesperPlayerSource.identity(binding.page) === identity;
    const targetKey = activePreview?.player === player ? activePreview.key
      : `${identity}|${targetPage}|${src}|${current ? binding.generation : 0}`;
    panelMedia = { documentIdentity: VesperPlayerSource.identity(pageAddress()), targetKey,
      player, pageUrl: targetPage, src, generation: current ? binding.generation : 0 };
    panel.classList.add('show');
    button.setAttribute('aria-expanded', 'true');
    companion.setPickerOpen(true, false, true);
    updateFlyVisibility();
    const isPreview = !!preview;
    const title = current && !isPreview ? document.title.replace(/\s+- YouTube$/, '') || 'This video' : 'This video';
    const poster = current ? player.poster || (!isPreview && document.querySelector('meta[property="og:image"]')?.content) || '' : '';
    const duration = current && Number.isFinite(player.duration) ? player.duration : null;
    if (player.mediaKeys || protectedVideos.has(player))
      return renderShell(title, poster, duration, element('div', 'msg bad', 'This video is protected by DRM.'));

    const loading = element('div', 'msg', 'Loading formats…');
    loading.setAttribute('role', 'status');
    renderShell(title, poster, duration, loading);
    if (!isCurrentRequest(request)) return;
    const result = await send({ type: 'media-options', pageUrl: targetPage, mediaChangedAt: current ? binding.changedAt : mediaChangedAt,
      title, playerPlaying: current && !player.paused && !player.ended, playerPageUrl: targetPage,
      playerUrl: current ? src : '', generation: current ? binding.generation : 0,
      manifestUrl: current && !VesperPlayerSource.http(src) ? declaredManifest(identity) : undefined });
    if (!isCurrentRequest(request)) return;
    if (result?.ok && result.data?.hasRelatedCollection
        && !result.data?.choices?.length && !result.data?.sourceChoices?.length) {
      const note = result.data.collectionIsDynamic
        ? 'This is a changing playlist snapshot. Choose entries and one quality in VESPER.'
        : 'Choose playlist entries and one quality in VESPER. Nothing is added until you confirm.';
      const controls = collectionPickerControls(title, request);
      return renderShell(result.data.title || title, poster, duration, element('div', 'msg', note), ...controls);
    }
    if (!result?.ok || (!result.data?.choices?.length && !result.data?.sourceChoices?.length)) {
      const message = errorMessage(result);
      if (!['protected', 'blocked', 'chrome-format'].includes(result?.code)) appendRetry(message, loadFormats, result?.data?.retryAt);
      return renderShell(title, poster, duration, message);
    }
    const formats = result.data;
    if (formats.sourceChoices?.length) return renderSourceChoices(title, poster, duration, formats);
    choice = formats.choices[0];
    renderChoices(formats.title || title, formats.thumbnail || poster, formats.duration ?? duration, formats);
  }

  function declaredManifest(identity) {
    for (const script of [...document.querySelectorAll('script[type="application/ld+json"]')].slice(0, 6)) {
      if (script.textContent.length > 64 * 1024) continue;
      try {
        const json = JSON.parse(script.textContent);
        const entries = Array.isArray(json) ? json : Array.isArray(json?.['@graph']) ? json['@graph'] : [json];
        for (const entry of entries.slice(0, 12)) {
          if (entry?.['@type'] !== 'VideoObject' || ![entry.url, entry['@id'], entry.embedUrl].some(url => VesperPlayerSource.identity(url) === identity)) continue;
          const url = VesperPlayerSource.http(entry.contentUrl);
          if (url) return url.href;
        }
      } catch { /* This page did not expose usable video metadata. */ }
    }
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
    close.onclick = () => { closePanel(); button.focus({ preventScroll: true }); };
    head.append(thumb, labels, close);
    panel.replaceChildren(head, ...body);
    place();
    return close;
  }

  function collectionPickerControls(title, request) {
    const browse = element('button', 'vesper-button', 'Browse playlist in VESPER');
    const status = element('small', 'hint', 'VESPER will show the available entries and their quality matches.');
    status.hidden = true;
    browse.onclick = async () => {
      if (!panelMedia || !isCurrentRequest(request) || browse.disabled) return;
      browse.disabled = true;
      browse.textContent = 'Opening VESPER…';
      status.hidden = false;
      status.textContent = 'Choose checked entries and confirm in VESPER.';
      status.classList.remove('bad');
      try {
        const reply = await send({ type: 'native-picker', title });
        if (!isCurrentRequest(request)) return;
        browse.disabled = false;
        browse.textContent = 'Browse playlist in VESPER';
        if (reply?.ok) {
          const accepted = (reply.results || []).filter(result => ['queued', 'started', 'already-submitted'].includes(result.status)).length;
          status.textContent = `${accepted} item(s) accepted in VESPER.`;
        } else {
          status.textContent = errorMessage(reply);
          status.classList.toggle('bad', reply?.code !== 'cancelled');
        }
      } catch {
        if (!isCurrentRequest(request)) return;
        browse.disabled = false;
        browse.textContent = 'Browse playlist in VESPER';
        status.textContent = 'VESPER could not open the playlist picker.';
        status.classList.add('bad');
      }
    };
    return [browse, status];
  }

  function renderChoices(title, poster, seconds, formats, expanded = false, useOriginal = false, useM4a = true) {
    const request = panelRequest;
    const originalPresets = useOriginal ? formats.originalChoices : formats.choices;
    const selectedAudio = formats.audioOutputs?.find(entry => entry.choice.index === formats.selectedAudio)?.choice;
    const audioChoice = selectedAudio || (useM4a ? formats.audioM4a : null);
    const presets = audioChoice ? originalPresets.map(c => c.label === 'Audio only' ? audioChoice : c) : originalPresets;
    const intent = formats.intent ||= { audioTrack: null, subtitles: [], includeAutoSubtitles: false, embedSubtitles: false };
    const moreFormats = formats.more || [];
    const all = expanded ? [...presets, ...moreFormats] : presets;
    const output = element('label', 'output', 'Output');
    const mode = element('select');
    output.append(mode);
    const list = element('div', 'list');
    list.setAttribute('role', 'radiogroup');
    list.setAttribute('aria-label', 'Download format');
    const rows = [];
    function updateOutput(index) {
      const isPreset = index < presets.length;
      const audio = isPreset && choice.label === 'Audio only';
      output.hidden = !isPreset || !(audio ? formats.audioOutputs?.length > 1 || formats.audioM4a : formats.originalChoices?.length);
      if (output.hidden) return;
      const kind = audio ? 'audio' : 'video';
      if (mode.dataset.kind !== kind) {
        const labels = audio ? formats.audioOutputs?.map(entry => entry.label) || ['M4A — plays everywhere', 'Original audio'] : ['Plays everywhere (MP4)', 'Original (best quality)'];
        mode.replaceChildren(...labels.map(name => element('option', '', name)));
        mode.dataset.kind = kind;
      }
      mode.selectedIndex = audio && formats.audioOutputs ? Math.max(0, formats.audioOutputs.findIndex(entry => entry.choice.index === choice.index)) : (audio ? !useM4a : useOriginal) ? 1 : 0;
    }
    function select(index, focus) {
      choice = all[index];
      if (pinned && presets.includes(choice)) { pinned.remove(); pinned = null; }
      for (const [i, option] of rows.entries()) {
        option.setAttribute('aria-checked', String(i === index));
        option.querySelector('.tick').textContent = i === index ? '✓' : '';
        option.tabIndex = i === index ? 0 : -1;
      }
      updateOutput(index);
      refreshClip();
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
      option.onclick = () => {
        if (option.isConnected && isCurrentRequest(request)) select(index, false);
      };
      option.onkeydown = e => {
        const offset = { ArrowUp: -1, ArrowLeft: -1, ArrowDown: 1, ArrowRight: 1 }[e.key];
        if (!offset || !option.isConnected || !isCurrentRequest(request)) return;
        e.preventDefault();
        e.stopPropagation();
        select((index + offset + all.length) % all.length, true);
      };
      rows.push(option);
      list.append(option);
    }
    mode.onchange = () => {
      if (!mode.isConnected || output.hidden || !isCurrentRequest(request)) return;
      let original = useOriginal, m4a = useM4a;
      if (mode.dataset.kind === 'audio') {
        m4a = mode.selectedIndex === 0;
        choice = formats.audioOutputs?.[mode.selectedIndex]?.choice || (m4a ? formats.audioM4a : originalPresets.find(c => c.label === 'Audio only'));
        formats.selectedAudio = choice?.index;
      } else {
        original = mode.selectedIndex === 1;
        const next = original ? formats.originalChoices : formats.choices;
        choice = next.find(c => c.label === choice.label) || next[0];
      }
      renderChoices(title, poster, seconds, formats, expanded, original, m4a);
      panel.querySelector('.output select')?.focus({ preventScroll: true });
    };
    // A choice picked from the expanded list stays visible when the list collapses.
    let pinned = null;
    if (!expanded && moreFormats.includes(choice)) {
      pinned = element('button', 'opt pinned');
      pinned.setAttribute('aria-checked', 'true');
      pinned.append(element('b', '', 'Selected: ' + choice.label));
      pinned.onclick = () => {
        if (isCurrentRequest(request)) renderChoices(title, poster, seconds, formats, true, useOriginal, useM4a);
      };
      list.prepend(pinned);
    }
    // "Download part": only for formats VESPER can cut (segmented HLS/DASH). Start/End as m:ss or seconds.
    const clip = element('label', 'clip');
    const clipBox = element('input');
    clipBox.type = 'checkbox';
    const clipFields = element('span', 'fields');
    const startField = element('input'), endField = element('input');
    for (const [field, name] of [[startField, 'Start'], [endField, 'End']]) {
      field.type = 'text'; field.placeholder = name === 'Start' ? '0:00' : (seconds ? clockText(seconds) : '1:00');
      field.setAttribute('aria-label', name); field.size = 7; field.inputMode = 'numeric';
      clipFields.append(element('small', '', name), field);
    }
    clipFields.hidden = true;
    clipBox.onchange = () => { clipFields.hidden = !clipBox.checked; clipNote.hidden = !clipBox.checked; };
    clip.append(clipBox, element('span', '', 'Download part'), clipFields);
    const clipNote = element('small', 'hint', 'May start a few seconds early: it cuts at the nearest segment.');
    clipNote.hidden = true;
    function clipRange() {
      if (!clipBox.checked || clip.hidden) return undefined;
      const start = parseClock(startField.value) ?? 0;
      const end = parseClock(endField.value) ?? seconds ?? NaN;
      return { start, end };
    }
    function refreshClip() {
      const canClip = !!choice?.canClip;
      clipNote.textContent = choice?.container === 'WAV' ? 'Exact sample boundaries. The entire source is downloaded and decoded.' : 'May start a few seconds early: it cuts at the nearest segment.';
      clip.hidden = !canClip;
      if (!canClip) { clipFields.hidden = true; clipNote.hidden = true; }
    }
    const body = [output, list, clip, clipNote];
    if (formats.audioTracks?.length > 1 || formats.subtitleOptions?.length) {
      const advanced = element('details');
      advanced.append(element('summary', '', 'Language and subtitles'));
      if (formats.audioTracks?.length > 1) {
        const label = element('label', 'output', 'Audio track'), tracks = element('select');
        const automatic = element('option', '', 'Original / automatic'); automatic.value = ''; tracks.append(automatic);
        for (const track of formats.audioTracks) { const option = element('option', '', track.label); option.value = track.id; tracks.append(option); }
        tracks.value = intent.audioTrack || ''; tracks.onchange = () => { intent.audioTrack = tracks.value || null; };
        label.append(tracks); advanced.append(label);
      }
      let embedSubtitleControl = null;
      for (const subtitle of formats.subtitleOptions || []) {
        const label = element('label', 'output'), check = element('input'); check.type = 'checkbox';
        check.checked = intent.subtitles.includes(subtitle.language);
        check.onchange = () => {
          intent.subtitles = check.checked ? [...new Set([...intent.subtitles, subtitle.language])] : intent.subtitles.filter(language => language !== subtitle.language);
          intent.includeAutoSubtitles = !!advanced.querySelector('input[data-auto=true]:checked');
          const canEmbed = (formats.subtitleOptions || []).some(option => option.embedSupported && intent.subtitles.includes(option.language));
          if (embedSubtitleControl) embedSubtitleControl.disabled = !canEmbed;
          if (!canEmbed) {
            intent.embedSubtitles = false;
            if (embedSubtitleControl) embedSubtitleControl.checked = false;
          }
        };
        check.dataset.auto = String(subtitle.auto); label.append(check, document.createTextNode(subtitle.label + (subtitle.auto ? ' (automatic)' : ''))); advanced.append(label);
      }
      if ((formats.subtitleOptions || []).some(subtitle => subtitle.embedSupported)) {
        const embedLabel = element('label', 'output');
        embedSubtitleControl = element('input');
        embedSubtitleControl.type = 'checkbox';
        embedSubtitleControl.checked = !!intent.embedSubtitles;
        embedSubtitleControl.disabled = !(formats.subtitleOptions || []).some(option => option.embedSupported && intent.subtitles.includes(option.language));
        embedSubtitleControl.onchange = () => { intent.embedSubtitles = embedSubtitleControl.checked; };
        embedLabel.append(embedSubtitleControl, document.createTextNode('Embed selected subtitles'));
        const embedHint = element('small', 'hint', 'Separate subtitle files are still queued. Live, segmented, styled, or unsupported tracks stay separate.');
        advanced.append(embedLabel, embedHint);
      }
      body.push(advanced);
    }
    if (moreFormats.length) {
      const more = element('button', 'vesper-button more', expanded ? 'Fewer formats ▴' : 'More formats ▾');
      more.setAttribute('aria-expanded', String(expanded));
      more.onclick = () => {
        if (!isCurrentRequest(request)) return;
        renderChoices(title, poster, seconds, formats, !expanded, useOriginal, useM4a);
        panel.querySelector('.more')?.focus({ preventScroll: true });
      };
      body.push(more);
    }
    if (formats.hasRelatedCollection && !formats.browserChoice)
      body.push(...collectionPickerControls(title, request));
    const footer = element('div', 'foot');
    for (const mode of formats.browserChoice ? ['download'] : ['queue', 'download']) {
      const label = formats.browserChoice ? 'Download with Chrome' : mode === 'download' ? 'Download' : 'Queue';
      const action = element('button', mode === 'download' ? 'vesper-button primary' : 'vesper-button', label);
      action.onclick = () => {
        if (action.isConnected) void deliver(title, poster, seconds, formats, choice.index, mode, request, { ...intent, range: clipRange() });
      };
      footer.append(action);
    }
    select(Math.max(0, all.indexOf(choice)), false);
    renderShell(title, poster, seconds, ...body, footer);
  }

  function renderSourceChoices(title, poster, seconds, formats) {
    const list = element('div', 'list');
    list.setAttribute('role', 'group');
    list.setAttribute('aria-label', 'Choose a stream');
    for (const source of formats.sourceChoices) {
      const option = element('button', 'opt');
      const labels = element('span');
      labels.append(element('b', '', source.label), element('small', '', source.detail));
      option.append(element('span', 'icon balanced', '›'), labels);
      option.onclick = () => { if (option.isConnected) void inspectSource(source.token, title, poster, seconds); };
      list.append(option);
    }
    renderShell(title, poster, seconds, element('div', 'msg', 'Check a source to see its download options.'), list);
  }

  async function inspectSource(token, title, poster, seconds) {
    if (!panelMedia || !isCurrentRequest(panelRequest)) return;
    const request = panelRequest;
    const loading = element('div', 'msg', 'Checking selected stream…');
    loading.setAttribute('role', 'status');
    renderShell(title, poster, seconds, loading);
    const result = await send({ type: 'media-source-select', sourceChoice: token,
      pageUrl: panelMedia.pageUrl, generation: panelMedia.generation, playerUrl: panelMedia.src });
    if (!isCurrentRequest(request)) return;
    if (result?.ok && result.data?.choices?.length) {
      const formats = result.data;
      choice = formats.choices[0];
      return renderChoices(formats.title || title, formats.thumbnail || poster,
        formats.duration ?? seconds, formats);
    }
    const message = result?.code === 'unavailable'
      ? element('div', 'msg bad', 'This source couldn’t be opened.')
      : errorMessage(result);
    appendRetry(message, loadFormats, result?.data?.retryAt, 'Back to sources');
    renderShell(title, poster, seconds, message);
  }

  async function deliver(title, poster, seconds, formats, formatChoice, mode, request, intent = {}) {
    if (!isCurrentRequest(request)) return;
    const sending = element('div', 'msg', formats.browserChoice ? 'Starting in Chrome…' : 'Sending to VESPER…');
    sending.setAttribute('role', 'status');
    const hadFocus = panel.contains(root.activeElement);
    const close = renderShell(title, poster, seconds, sending);
    if (hadFocus) close.focus({ preventScroll: true });
    const reply = await send(formats.browserChoice ? { type: 'browser-media-select', browserChoice: formats.browserChoice,
      pageUrl: panelMedia.pageUrl }
      : { type: 'media-select', mediaSession: formats.mediaSession, formatChoice, mode, ...intent });
    if (!isCurrentRequest(request)) return;
    if (reply?.ok) {
      const status = reply.results?.[0]?.status;
      return confirmSend(formats.browserChoice ? 'Downloading in Chrome' : status === 'queued' ? 'Queued in VESPER' :
        status === 'already-submitted' ? 'Already in VESPER' : 'Sent to VESPER', request);
    }
    const error = errorMessage(reply);
    appendRetry(error, reply?.code === 'expired-selection' ? loadFormats
      : () => deliver(title, poster, seconds, formats, formatChoice, mode, request));
    const hasFocus = panel.contains(root.activeElement);
    renderShell(title, poster, seconds, error);
    if (hasFocus) error.querySelector('button')?.focus({ preventScroll: true });
  }

  async function confirmSend(message, request) {
    if (panel.contains(root.activeElement)) button.focus({ preventScroll: true });
    panel.inert = true;
    if (!still()) {
      panelMotion = panel.animate([{ opacity: 1, transform: 'none' },
        { opacity: 0, transform: 'translate(5px,-7px) scale(.96)' }], { duration: 145, easing: 'ease-in', fill: 'forwards' });
      await panelMotion.finished.catch(() => {});
    }
    if (!isCurrentRequest(request)) return;
    closePanel(true);
    receipt.textContent = message;
    receipt.classList.add('show');
    receiptTimer = setTimeout(() => { dismissReceipt(); updateFlyVisibility(); }, 1750);
    place();
  }

  function appendRetry(message, action, when, label = 'Try again') {
    const retry = element('button', 'link', label);
    retry.onclick = action;
    const retryAt = Date.parse(when);
    if (retryAt > Date.now()) {
      const time = new Date(retryAt).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit', second: '2-digit' });
      message.append(` Try again after ${time}.`);
      retry.hidden = true;
      clearTimeout(retryTimer);
      retryTimer = setTimeout(() => { retry.hidden = false; }, retryAt - Date.now());
    }
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
      'no-media': 'No downloadable media was found for this page.',
      unavailable: 'This media is unavailable or has been removed.',
      'source-expired': 'The media link expired. Reopen the preview to refresh it.',
      'sign-in': 'This media requires a sign-in to access.',
      'browser-context': 'The site requires a browser session or verification step.',
      blocked: 'The site blocked the request or restricted its region.',
      network: 'The media link could not be reached. Check your connection and retry.',
      'resolver-error': 'The media resolver hit a temporary problem. Try again shortly.',
      invalid: reply?.message || 'The current media selection is invalid.',
      unsupported: reply?.message || 'This source can’t be downloaded.'
    }[reply?.code] || reply?.message || 'Something went wrong.';
    return element('div', 'msg bad', text);
  }

  function applyMotion() {
    if (!alive()) return;
    host?.toggleAttribute('data-vesper-still', still());
    companion?.setStill(still());
    if (still()) panelMotion?.cancel();
  }

  document.addEventListener('pointerdown', (e) => {
    if (alive() && panel?.classList.contains('show') && !e.composedPath().includes(host)) closePanel();
  }, { capture: true, passive: true, signal });
  chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    if (sender?.id !== chrome.runtime.id) return false;
    if (message?.type === 'vesper-open-picker') {
      syncPage();
      if ((!video?.isConnected && activePreview?.player !== video) || !panel) {
        sendResponse({ ok: false, code: 'no-player' });
        return false;
      }
      void loadFormats();
      sendResponse({ ok: true });
      return false;
    }
    if (message?.type !== 'vesper-prefetch') return false;
    syncPage();
    if (video) {
      prefetchedPlayers.delete(video);
      prefetchPlayer(video);
    }
    return false;
  });
  window.addEventListener('pagehide', e => { if (e.persisted) companion?.setVisible(false); else retire(); }, { signal });
  window.addEventListener('pageshow', refreshPlayers, { signal });
  unwatchMotion = VesperTheme.watchPreference('motion', 'animate', value => {
    motionMode = value;
    applyMotion();
  });
  refreshPlayers();
})();
