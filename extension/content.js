// VESPER content script. No MutationObserver and no polling: media elements are noticed through capture-phase
// media events. The butterfly appears only when a downloadable source really exists for this player.
(() => {
  'use strict';
  if (window.__vesperContent) return;          // never inject twice
  window.__vesperContent = true;

  const MIN_WIDTH = 220, MIN_HEIGHT = 120;
  let video = null;            // the player the butterfly is attached to
  let host = null, root = null, button = null, panel = null;
  let revealTimer = 0, frame = 0, listening = false, state = null;

  const send = (message) => new Promise((resolve) => {
    try { chrome.runtime.sendMessage(message, (reply) => resolve(chrome.runtime.lastError ? { ok: false, code: 'extension-error' } : reply)); }
    catch { resolve({ ok: false, code: 'extension-error' }); }   // extension reloaded: this script is orphaned
  });

  // ------------------------------------------------------------ noticing media

  const isPlayable = (el) => el instanceof HTMLVideoElement && el.clientWidth >= MIN_WIDTH && el.clientHeight >= MIN_HEIGHT;

  async function consider(el) {
    if (!(el instanceof HTMLMediaElement)) return;
    const src = el.currentSrc || el.src || '';
    const drm = !!el.mediaKeys;
    const blobOnly = src.startsWith('blob:') || !src;
    const title = document.querySelector('meta[property="og:title"]')?.content || document.title || '';
    const poster = el.poster || document.querySelector('meta[property="og:image"]')?.content || '';
    const duration = Number.isFinite(el.duration) ? el.duration : null;
    const reply = await send({ type: 'media-element', src: blobOnly ? '' : src, title, poster, duration,
      unsupported: drm ? 'drm' : null });
    if (!reply?.ok || !isPlayable(el)) return;

    state = (await send({ type: 'tab-state' }))?.state;
    if (state?.sources?.some((s) => s.kind !== 'dash') || (!blobOnly && !drm)) attach(el);   // actionable source exists
  }

  // Capture-phase listeners on document: media events do not bubble, but they can be captured. One-time cost.
  for (const type of ['loadedmetadata', 'playing']) document.addEventListener(type, (e) => consider(e.target), { capture: true, passive: true });
  document.addEventListener('encrypted', (e) => { send({ type: 'media-element', src: '', unsupported: 'drm' }); }, { capture: true, passive: true });
  document.querySelectorAll('video').forEach((el) => { if (el.readyState >= 1) consider(el); });   // players that loaded before us

  // ------------------------------------------------------------ the butterfly

  function build() {
    host = document.createElement('div');
    host.style.cssText = 'all:initial;position:fixed;z-index:2147483647;top:0;left:0;width:0;height:0;';
    root = host.attachShadow({ mode: 'closed' });
    root.innerHTML = `
      <style>
        :host{all:initial}
        *{box-sizing:border-box;font-family:'Segoe UI',system-ui,sans-serif}
        .fly{position:fixed;width:34px;height:34px;border-radius:10px;border:1px solid #c9d6c0;background:#26402f;display:none;
          align-items:center;justify-content:center;cursor:pointer;padding:0;opacity:.0;transition:opacity .15s;box-shadow:0 1px 4px rgba(0,0,0,.35)}
        .fly.show{display:flex;opacity:.92}.fly:hover,.fly:focus-visible{opacity:1;outline:2px solid #a5cfa8;outline-offset:1px}
        .fly img{width:24px;height:24px;pointer-events:none}
        .panel{position:fixed;width:330px;background:#fbfcf9;color:#2c3630;border:1px solid #d3dccd;border-radius:12px;display:none;
          box-shadow:0 8px 28px rgba(0,0,0,.28);overflow:hidden}
        .panel.show{display:block}
        .head{display:flex;gap:10px;padding:12px 12px 10px;border-bottom:1px solid #e3e9de;align-items:center}
        .thumb{width:72px;height:41px;border-radius:6px;background:#dfe6da center/cover;flex:none}
        .t{font-size:13px;font-weight:600;line-height:1.25;max-height:34px;overflow:hidden}.d{font-size:11px;color:#68746b;margin-top:2px}
        .x{margin-left:auto;border:0;background:none;font-size:18px;color:#68746b;cursor:pointer;align-self:flex-start;padding:0 2px}
        .list{padding:6px}.opt{display:flex;gap:8px;align-items:center;width:100%;border:0;background:none;text-align:left;padding:8px 10px;
          border-radius:8px;cursor:pointer;font-size:13px;color:inherit}
        .opt:hover{background:#eef3ea}.opt[aria-checked=true]{background:#e4efe0}
        .opt b{font-weight:600}.opt small{display:block;color:#68746b;font-size:11px;margin-top:1px}.tick{margin-left:auto;color:#3d694b}
        .more{width:calc(100% - 12px);margin:0 6px 6px;border:0;border-radius:8px;background:#eef1ea;padding:8px;font-size:12px;cursor:pointer;color:#3d694b}
        .foot{display:flex;gap:8px;justify-content:flex-end;padding:10px 12px;border-top:1px solid #e3e9de}
        .btn{border:1px solid #cfd8c9;background:#fff;border-radius:8px;padding:7px 14px;font-size:12.5px;cursor:pointer;color:#2c3630}
        .btn.p{background:#3d694b;border-color:#3d694b;color:#fff}.btn:disabled{opacity:.5;cursor:default}
        .msg{padding:14px 14px 10px;font-size:12.5px;line-height:1.45}.msg.bad{color:#a24338}.msg.ok{color:#3d694b}
        .link{border:0;background:none;color:#3d694b;text-decoration:underline;cursor:pointer;font-size:12.5px;padding:0}
        @media (prefers-color-scheme:dark){.panel{background:#1e2520;color:#dce3d8;border-color:#38453c}.head,.foot{border-color:#2f3b33}
          .opt:hover{background:#27322b}.opt[aria-checked=true]{background:#2d4034}.btn{background:#27322b;color:#dce3d8;border-color:#38453c}
          .more{background:#27322b;color:#a5cfa8}.d,.opt small,.x{color:#a3b3a5}}
        @media (prefers-reduced-motion:reduce){.fly{transition:none}}
      </style>
      <button class="fly" aria-label="Download with VESPER" title="Download with VESPER"><img alt=""></button>
      <div class="panel" role="dialog" aria-label="VESPER download options"></div>`;
    button = root.querySelector('.fly');
    panel = root.querySelector('.panel');
    button.querySelector('img').src = chrome.runtime.getURL('icons/moth-64.png');
    button.addEventListener('click', (e) => { e.stopPropagation(); togglePanel(); });
    root.addEventListener('keydown', (e) => { if (e.key === 'Escape') closePanel(); });
    mount();
  }

  const mount = () => (document.fullscreenElement || document.documentElement).appendChild(host);
  document.addEventListener('fullscreenchange', () => host && mount(), { passive: true });   // the butterfly must live inside a fullscreen player

  function attach(el) {
    if (video === el && host) return;
    video = el;
    if (!host) build();
    if (!listening) {
      listening = true;
      document.addEventListener('mousemove', onPointer, { passive: true });
      window.addEventListener('scroll', schedule, { passive: true, capture: true });
      window.addEventListener('resize', schedule, { passive: true });
    }
    place();
  }

  function detach() {
    listening = false;
    document.removeEventListener('mousemove', onPointer);
    window.removeEventListener('scroll', schedule, true);
    window.removeEventListener('resize', schedule);
    host?.remove(); host = root = button = panel = null; video = null;
  }

  function place() {
    if (!video || !video.isConnected) return detach();
    const r = video.getBoundingClientRect();
    const visible = r.width >= MIN_WIDTH && r.height >= MIN_HEIGHT && r.bottom > 40 && r.top < innerHeight && r.right > 40 && r.left < innerWidth;
    if (!visible) { button.classList.remove('show'); closePanel(); return; }
    // Top-right, clear of the player's own controls (which sit along the bottom).
    const x = Math.min(r.right, innerWidth) - 34 - 10, y = Math.max(r.top, 0) + 10;
    button.style.left = `${x}px`; button.style.top = `${y}px`;
    if (panel.classList.contains('show')) {
      panel.style.left = `${Math.max(8, x + 34 - 330)}px`; panel.style.top = `${y + 40}px`;
    }
  }

  function schedule() { if (!frame) frame = requestAnimationFrame(() => { frame = 0; place(); }); }

  function onPointer(e) {
    if (!video) return;
    const r = video.getBoundingClientRect();
    const over = e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom;
    if (!over) return;
    schedule();
    button.classList.add('show');
    clearTimeout(revealTimer);
    revealTimer = setTimeout(() => { if (!panel.classList.contains('show')) button.classList.remove('show'); }, 2800);
  }

  // ------------------------------------------------------------ the quality picker

  let choice = null;
  function closePanel() { panel?.classList.remove('show'); }

  async function togglePanel() {
    if (panel.classList.contains('show')) return closePanel();
    panel.classList.add('show');
    place();
    const title = document.querySelector('meta[property="og:title"]')?.content || document.title || 'This video';
    const poster = video.poster || document.querySelector('meta[property="og:image"]')?.content || '';
    const seconds = Number.isFinite(video.duration) ? video.duration : null;
    renderShell(title, poster, seconds, '<div class="msg">Checking this video…</div>');

    state = (await send({ type: 'tab-state' }))?.state;
    if (state?.unsupported === 'drm') return renderShell(title, poster, seconds, `<div class="msg bad">This video is protected (DRM). VESPER does not bypass protection, so it can’t be downloaded.</div>`);

    const candidates = (state?.sources || []).filter((s) => s.kind === 'hls' || s.kind === 'direct');
    const direct = (video.currentSrc || '').startsWith('http') ? [{ url: video.currentSrc, kind: /\.m3u8/i.test(video.currentSrc) ? 'hls' : 'direct' }] : [];
    const source = [...candidates.filter((s) => s.kind === 'hls'), ...direct, ...candidates][0];
    if (!source) {
      const dash = (state?.sources || []).some((s) => s.kind === 'dash');
      return renderShell(title, poster, seconds, `<div class="msg bad">${dash ? 'This player uses DASH streaming, which VESPER can’t download yet.' : 'This player streams in a way VESPER can’t access, so there is nothing to download here.'}</div>`);
    }

    const described = await send({ type: 'describe', url: source.url, duration: seconds });
    if (!described?.ok) return renderShell(title, poster, seconds, errorMessage(described));
    choice = described.choices.find((c) => c.recommended) || described.choices[0];
    renderChoices(title, poster, seconds, described);
  }

  function renderShell(title, poster, seconds, bodyHtml, footer = '') {
    const clock = seconds ? `${Math.floor(seconds / 3600) ? Math.floor(seconds / 3600) + ':' : ''}${String(Math.floor(seconds / 60) % 60).padStart(Math.floor(seconds / 3600) ? 2 : 1, '0')}:${String(Math.floor(seconds % 60)).padStart(2, '0')}` : '';
    panel.innerHTML = `<div class="head"><div class="thumb"></div><div><div class="t"></div><div class="d"></div></div><button class="x" aria-label="Close">×</button></div>${bodyHtml}${footer}`;
    panel.querySelector('.t').textContent = title;
    panel.querySelector('.d').textContent = clock;
    if (poster) panel.querySelector('.thumb').style.backgroundImage = `url("${poster.replace(/"/g, '%22')}")`;
    panel.querySelector('.x').onclick = closePanel;
  }

  function renderChoices(title, poster, seconds, described, expanded = false) {
    const all = expanded ? [...described.choices, ...described.more] : described.choices;
    const list = all.map((c, i) => `<button class="opt" role="radio" aria-checked="${c === choice}" data-i="${i}"><span><b></b><small></small></span><span class="tick">${c === choice ? '✓' : ''}</span></button>`).join('');
    const more = !expanded && described.more.length ? `<button class="more">Show more qualities ▾</button>` : '';
    renderShell(title, poster, seconds, `<div class="list" role="radiogroup">${list}</div>${more}`,
      `<div class="foot"><button class="btn" data-mode="queue">Queue</button><button class="btn p" data-mode="download">Download</button></div>`);
    panel.querySelectorAll('.opt').forEach((el, i) => {
      el.querySelector('b').textContent = all[i].label; el.querySelector('small').textContent = all[i].detail || '';
      el.onclick = () => { choice = all[i]; renderChoices(title, poster, seconds, described, expanded); };
    });
    panel.querySelector('.more')?.addEventListener('click', () => renderChoices(title, poster, seconds, described, true));
    panel.querySelectorAll('[data-mode]').forEach((el) => (el.onclick = () => deliver(title, poster, seconds, el.dataset.mode)));
    const nativeButton = document.createElement('button');
    nativeButton.className = 'more';
    nativeButton.textContent = 'More formats in VESPER…';
    nativeButton.onclick = async () => {
      nativeButton.disabled = true;
      nativeButton.textContent = 'Opening VESPER…';
      const reply = await send({ type: 'native-picker', pageUrl: location.href, title });
      if (reply?.ok) closePanel();
      else {
        nativeButton.disabled = false;
        nativeButton.textContent = reply?.message || 'Could not open VESPER · Try again';
      }
    };
    panel.querySelector('.foot')?.before(nativeButton);
  }

  async function deliver(title, poster, seconds, mode) {
    panel.querySelectorAll('.btn').forEach((b) => (b.disabled = true));
    const safeName = title.replace(/[\\/:*?"<>|]+/g, ' ').trim().slice(0, 120) || 'video';
    const reply = await send({ type: 'submit', mode, pageUrl: location.href,
      items: [{ url: choice.url, kind: choice.kind === 'hls' ? 'hls' : 'http', fileName: choice.kind === 'hls' ? safeName : undefined }] });
    if (reply?.ok) {
      const status = reply.results?.[0]?.status;
      return renderShell(title, poster, seconds, `<div class="msg ok">${status === 'queued' ? 'Added to the VESPER queue.' : status === 'already-submitted' ? 'Already sent to VESPER.' : 'Sent to VESPER — the download has started.'}</div>`);
    }
    renderShell(title, poster, seconds, errorMessage(reply, true));
    panel.querySelector('[data-retry]')?.addEventListener('click', () => deliver(title, poster, seconds, mode));
  }

  function errorMessage(reply, canRetry = false) {
    const retry = canRetry ? ` <button class="link" data-retry>Try again</button>` : '';
    const text = {
      'host-missing': 'The VESPER connection isn’t installed. Reinstall or update VESPER to repair its browser helper.',
      'app-unavailable': 'VESPER isn’t running and couldn’t be started.',
      'host-error': 'The connection to VESPER failed.',
      duplicate: 'This link is already in VESPER.',
      cancelled: 'Cancelled in VESPER.',
      timeout: 'VESPER didn’t answer in time.',
      unsupported: reply?.message || 'This source can’t be downloaded.'
    }[reply?.code] || reply?.message || 'Something went wrong.';
    const el = document.createElement('div');
    el.className = 'msg bad'; el.textContent = text;
    return el.outerHTML.replace('</div>', retry + '</div>');
  }

  document.addEventListener('pointerdown', (e) => { if (panel?.classList.contains('show') && !e.composedPath().includes(host)) closePanel(); }, { capture: true, passive: true });
})();
