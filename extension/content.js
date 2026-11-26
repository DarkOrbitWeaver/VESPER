// Observe the current player and prepare formats without starting a download.
(() => {
  'use strict';
  if (window.__vesperContent) return;          // never inject twice
  window.__vesperContent = true;

  const lifetime = new AbortController();
  const { signal } = lifetime;
  const MIN_WIDTH = 220, MIN_HEIGHT = 120, FLY_SIZE = 44, FULLSCREEN_IDLE = 2300, ENDED_PLAYER_TTL = 10 * 60_000;
  const PREVIEW_EXIT_GRACE = 900, PREVIEW_MAX_LATCH = 2200;
  let video = null;            // the player the butterfly is attached to
  let host = null, root = null, button = null, panel = null, receipt = null;
  let frame = 0, listening = false, pointer = null, pointerTarget = null, panelRequest = 0, unwatchTheme = null;
  let perch = null, companion = null, receiptTimer = 0, retryTimer = 0, previewExitTimer = 0;
  let previewRemovedAt = 0, previewPointerDistance = Infinity, panelMotion = null, fullscreenTimer = 0, endedPlayerTimer = 0;
  let fullscreenPlayer = false;
  let manualPlayer = null; // A deliberate drag overrides automatic largest-player selection.
  let drag = null, movedPerch = null, suppressDragClick = false, suppressDragClickTimer = 0, pointerMovedAt = 0;
  let lastPointerPosition = null;
  let contextNotice = null, contextNoticeTimer = 0;
  let activePreview = null;
  let endedPlayer = null;
  const pageAddress = () => location.href.split('#', 1)[0];
  let pageUrl = pageAddress(), mediaChangedAt = 0, panelMedia = null;
  let motionMode = 'animate', unwatchMotion = null;
  let extensionPaused = true, unwatchPause = null, contextPicker = false;
  let prefetchTimer = 0, prefetchTarget = null;
  let hiddenButterflyHosts;
  let retainedPickerDraft = null;
  const chosenSources = new WeakMap();
  let activeBrowserCopy = null;
  let generatedCopy = null;
  let generatedIntentAt = 0;
  const still = () => motionMode === 'still';
  const butterflyHidden = () => extensionPaused || !hiddenButterflyHosts || hiddenButterflyHosts.has((location.hostname || '').toLowerCase());
  function setHiddenButterflyHosts(hosts) {
    hiddenButterflyHosts = new Set(Array.isArray(hosts)
      ? hosts.filter(host => typeof host === 'string').map(host => host.toLowerCase()) : []);
    refreshPlayers();
  }
  function onExtensionPreferencesChanged(changes, areaName) {
    if (areaName === 'local' && changes.vesperHiddenButterflyHosts)
      setHiddenButterflyHosts(changes.vesperHiddenButterflyHosts.newValue);
  }
  chrome.storage?.onChanged?.addListener?.(onExtensionPreferencesChanged);
  const protectedVideos = new WeakSet();
  const playback = new WeakMap();
  const mediaLedger = VesperIntelligence.createMediaLedger();
  const playerEvidenceKeys = new WeakMap();
  let playerInteraction = null;
  const audioControls = new WeakMap();
  let intentCertain = true;
  let evidenceSerial = 0, resourceObserver = null;
  function viewportCenterDistance(rect) {
    const width = globalThis.innerWidth || 1, height = globalThis.innerHeight || 1;
    const dx = (rect.left + rect.right) / 2 - width / 2, dy = (rect.top + rect.bottom) / 2 - height / 2;
    return Math.min(1, Math.hypot(dx, dy) / Math.hypot(width / 2, height / 2));
  }
  // Structural advertisement context: ad-slot tokens on the element or its nearest containers, or an
  // ad-skip control inside the same container. A weak negative feature; never a reason to act.
  const AD_TOKEN = /^(?:ad|ads|advert|adverts|advertisement|adslot|adunit|sponsor|sponsored|promoted|preroll|midroll|postroll|ima)$/;
  function advertisementContext(el) {
    let node = el;
    for (let depth = 0; node && node.nodeType === 1 && depth < 5; depth++, node = node.parentElement) {
      const text = [node.id, typeof node.className === 'string' ? node.className : '', node.getAttribute?.('aria-label') || '',
        node.getAttribute?.('aria-roledescription') || ''].join(' ').slice(0, 400);
      const tokens = text.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase().split(/[^a-z0-9]+/);
      if (tokens.some(token => AD_TOKEN.test(token))) return true;
      if (node.dataset && Object.keys(node.dataset).slice(0, 24).some(name => /^(?:ad|ads|adSlot|adUnit|sponsored)$/.test(name))) return true;
    }
    return false;
  }
  function evidenceKey(el) {
    if (!playerEvidenceKeys.has(el)) playerEvidenceKeys.set(el, 'player-' + ++evidenceSerial);
    return playerEvidenceKeys.get(el);
  }
  const prefetchedPlayers = new WeakMap();
  const prefetchRetried = new WeakMap();
  let generation = 0;
  const observed = new Set();
  let discoveryObserver = null, discoveryFrame = 0, scrollScanTimer = 0;
  const intersecting = new WeakMap();
  const resize = new ResizeObserver(schedule);
  const visibility = new IntersectionObserver((entries) => {
    if (!alive()) return;
    for (const entry of entries) {
      if (!entry.target.isConnected) {
        visibility.unobserve(entry.target);
        observed.delete(entry.target);
      } else {
        intersecting.set(entry.target, entry.isIntersecting);
        if (entry.isIntersecting) consider(entry.target);
      }
    }
    selectPlayer();
  }, { threshold: [0, 0.1, 0.25, 0.5, 0.75, 1] });

  function retire() {
    if (signal.aborted) return;
    lifetime.abort();
    activeBrowserCopy?.controller.abort();
    generatedCopy?.controller.abort();
    visibility.disconnect();
    discoveryObserver?.disconnect();
    if (discoveryFrame) cancelAnimationFrame(discoveryFrame);
    discoveryFrame = 0;
    clearTimeout(scrollScanTimer);
    scrollScanTimer = 0;
    resourceObserver?.disconnect();
    resourceObserver = null;
    observed.clear();
    unwatchMotion?.();
    retainedPickerDraft = null;
    // An extension reload can invalidate storage before page listeners are removed.
    try { unwatchPause?.(); } catch {}
    try { chrome.storage.onChanged.removeListener(onExtensionPreferencesChanged); } catch {}
    clearTimeout(contextNoticeTimer);
    clearTimeout(endedPlayerTimer);
    contextNotice?.remove();
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
    retainedPickerDraft = null;
    mediaChangedAt = Date.now();
    endedPlayer = null;
    clearTimeout(endedPlayerTimer);
    // SPA navigation can leave an intentionally pinned floating player alive.
    if (!manualPlayer?.isConnected) { manualPlayer = null; movedPerch = null; }
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

  function hasOtherPlayer(player) {
    for (const other of observed) {
      if (other !== player && visiblePlayer(other) && !advertisementContext(other)) return true;
    }
    return false;
  }

  function isMutedLoop(player, source) {
    if (!player.defaultMuted || !player.autoplay || !player.loop || player.controls) return false;
    const selected = VesperPlayerSource.http(source)?.href;
    return !!selected && (VesperPlayerSource.http(player.src)?.href === selected
      || [...player.querySelectorAll(':scope > source[src]')].some(child => VesperPlayerSource.http(child.src)?.href === selected));
  }

  // Revalidate live identity before acting on a cached picker. Media events
  // and mutation observers may not yet have observed a recycled feed card.
  function pickerPlayerState(player) {
    return {
      identity: VesperPlayerSource.identity(pageForPlayer(player)),
      current: playerSource(player),
      srcObject: player.srcObject || null,
      assigned: player.src || '',
      alternatives: [...player.querySelectorAll(':scope > source[src]')].slice(0, 12).map(el => el.src)
    };
  }

  function matchesPickerPlayer(media) {
    const { player, playerState: before } = media;
    if (!player?.isConnected || !before || player.mediaKeys || protectedVideos.has(player)) return false;
    const now = pickerPlayerState(player);
    return before.identity === now.identity && before.current === now.current
      && before.srcObject === now.srcObject && before.assigned === now.assigned
      && before.alternatives.length === now.alternatives.length
      && before.alternatives.every((src, i) => src === now.alternatives[i]);
  }

  function isCurrentRequest(request) {
    if (!alive() || request !== panelRequest || !panelMedia) return false;
    const pageMatches = panelMedia.documentIdentity === VesperPlayerSource.identity(pageAddress());
    const previewMatches = !activePreview || activePreview.key === panelMedia.targetKey;
    const binding = panelMedia.player && playback.get(panelMedia.player);
    const sourceMatches = !panelMedia.generation || binding?.generation === panelMedia.generation;
    if (pageMatches && previewMatches && sourceMatches && matchesPickerPlayer(panelMedia)) return true;
    retainedPickerDraft = null;
    closePanel();
    return false;
  }

  // noticing media

  function nativeAudioPlayer(el) {
    return el?.tagName === 'AUDIO' && el.controls === true;
  }

  function playerAnchor(el) {
    if (el?.tagName !== 'AUDIO' || nativeAudioPlayer(el)) return el;
    const control = audioControls.get(el);
    return control?.isConnected && controlledPlayer(control) === el ? control : null;
  }

  function controlledPlayer(control) {
    const controls = control.getAttribute('aria-controls')?.trim();
    if (controls) {
      const target = document.getElementById(controls);
      return target instanceof HTMLMediaElement ? target : null;
    }
    const label = control.getAttribute('aria-label') || control.getAttribute('title') || control.textContent || '';
    if (!/^(?:play|pause|resume|stop)(?:\s|$)/i.test(label.trim().slice(0, 80))) return null;
    for (let parent = control.parentElement, depth = 0; parent && parent !== document.body && depth < 4;
        parent = parent.parentElement, depth++) {
      const media = parent.querySelectorAll('video,audio');
      if (media.length) return media.length === 1 ? media[0] : null;
    }
    return null;
  }

  function renderedPlayerBox(el) {
    // Unlike getBoundingClientRect, this detects opacity and content visibility
    // inherited through containers (including virtualized feed cards).
    if (typeof el.checkVisibility === 'function')
      return el.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true, contentVisibilityAuto: true });
    const style = getComputedStyle(el);
    if (style.visibility === 'hidden' || style.visibility === 'collapse' || style.opacity === '0') return false;
    return !hiddenPlayerContainer(el);
  }

  function hiddenPlayerContainer(el) {
    for (let parent = el.parentElement; parent; parent = parent.parentElement) {
      const style = getComputedStyle(parent);
      if (style.opacity === '0' || style.display === 'none' || style.contentVisibility === 'hidden') return true;
    }
    return false;
  }

  function visiblePlayer(el, minWidth = MIN_WIDTH, minHeight = MIN_HEIGHT) {
    const anchor = playerAnchor(el);
    if (el?.tagName === 'AUDIO') {
      minWidth = anchor === el ? 160 : 20;
      minHeight = anchor === el ? 24 : 20;
    }
    const ended = isCurrentEndedPlayer(el);
    if (!el?.isConnected || !anchor || anchor === el && intersecting.get(el) === false && !ended) return null;
    const r = anchor.getBoundingClientRect();
    if (renderedPlayerBox(anchor)) {
      const rect = clippedPlayerRect(r, minWidth, minHeight);
      if (rect) return rect;
    }
    if (anchor !== el || !ended || hiddenPlayerContainer(el)) return null;

    for (let parent = el.parentElement, depth = 0; parent && parent !== document.body && depth < 5; parent = parent.parentElement, depth++) {
      if (!renderedPlayerBox(parent)) continue;
      const parentBox = parent.getBoundingClientRect();
      const original = endedPlayer.frame;
      const left = parentBox.left + scrollX, top = parentBox.top + scrollY;
      const right = parentBox.right + scrollX, bottom = parentBox.bottom + scrollY;
      const overlapsFrame = Math.max(0, Math.min(right, original.right) - Math.max(left, original.left))
        * Math.max(0, Math.min(bottom, original.bottom) - Math.max(top, original.top));
      const frameArea = Math.max(1, (original.right - original.left) * (original.bottom - original.top));
      const closeSize = parentBox.width >= original.width * 0.5 && parentBox.width <= original.width * 2
        && parentBox.height >= original.height * 0.5 && parentBox.height <= original.height * 2;
      if (overlapsFrame >= frameArea * 0.5 && closeSize) {
        const parentRect = clippedPlayerRect(parentBox, minWidth, minHeight);
        if (parentRect) return parentRect;
      }
    }
    return null;
  }

  function clippedPlayerRect(rect, minWidth = MIN_WIDTH, minHeight = MIN_HEIGHT) {
    if (!rect) return null;
    const left = Math.max(0, rect.left), right = Math.min(innerWidth, rect.right);
    const top = Math.max(0, rect.top), bottom = Math.min(innerHeight, rect.bottom);
    return right - left < minWidth || bottom - top < minHeight ? null : { left, top, right, bottom };
  }

  function isCurrentEndedPlayer(el) {
    return !!endedPlayer && endedPlayer.player === el && Date.now() < endedPlayer.expires
      && endedPlayer.pageIdentity === VesperPlayerSource.identity(pageForPlayer(el))
      && el?.isConnected;
  }

  function rememberEndedPlayer(el) {
    if (el !== video || !observed.has(el)) return;
    const binding = playback.get(el);
    if (!binding) return;
    const rect = (playerAnchor(el) || el).getBoundingClientRect();
    endedPlayer = {
      player: el,
      binding,
      pageIdentity: VesperPlayerSource.identity(binding.page),
      frame: { left: rect.left + scrollX, top: rect.top + scrollY,
        right: rect.right + scrollX, bottom: rect.bottom + scrollY,
        width: rect.width, height: rect.height },
      expires: Date.now() + ENDED_PLAYER_TTL
    };
    clearTimeout(endedPlayerTimer);
    endedPlayerTimer = setTimeout(() => {
      endedPlayerTimer = 0;
      if (endedPlayer?.player !== el) return;
      endedPlayer = null;
      selectPlayer();
    }, ENDED_PLAYER_TTL);
  }

  function cardContext(node, includePlayer) {
    if (!(node instanceof Element)) return null;
    const origin = new URL(pageAddress()).origin;
    for (let card = node, depth = 0; card && depth < 16 && card !== document.body; card = card.parentElement, depth++) {
      const allVideos = [...card.querySelectorAll('video')];
      // Never derive one video's post from a shared multi-player feed container; a single carousel article keeps at most one visible player.
      if (allVideos.length > 1 && !(card.tagName === 'ARTICLE' && allVideos.filter(el => visiblePlayer(el)).length <= 1)) break;
      const videos = includePlayer ? allVideos.filter(el => observed.has(el) && visiblePlayer(el)) : [];
      if (includePlayer && !videos.length) continue;
      const links = [...card.querySelectorAll('a[href]')].slice(0, 32).map(link => ({ href: link.href,
        permalink: !!(link.querySelector?.('time[datetime]') || link.hasAttribute?.('data-permalink')) }));
      try {
        const own = card.getAttribute?.('data-permalink');
        if (own) links.push({ href: new URL(own, origin).href, permalink: true });
      } catch { /* A malformed permalink attribute names nothing. */ }
      const page = VesperPlayerSource.cardPage(links, origin);
      if (!page) continue;
      videos.sort((a, b) => {
        const area = el => { const r = el.getBoundingClientRect(); return r.width * r.height * (el.paused ? 1 : 1.15); };
        return area(b) - area(a);
      });
      return { page, player: videos[0] };
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
    if (activePreview && activePreview.key !== preview.key || panelMedia && panelMedia.targetKey !== preview.key)
      retainedPickerDraft = null;
    activePreview = preview;
    consider(preview.player);
    if (panelMedia && panelMedia.targetKey !== preview.key) closePanel();
    return true;
  }

  function refreshHoveredPreview(player) {
    // Shadow-host hit testing must not search the feed's ancestors for another card.
    if (!pointer || panelMedia || pointerTarget === host || pointerOnButterfly()) return false;
    const target = document.elementFromPoint(pointer.x, pointer.y) || pointerTarget;
    if (target === host) return false;
    const preview = cardPreview(target);
    return preview?.player === player && activatePreview(preview);
  }

  function refreshPlayer(el) {
    if (!observed.has(el)) return;
    refreshHoveredPreview(el);
    selectPlayer();
  }

  function activeYouTubePage(el, contextPage) {
    if (!/(?:^|\.)youtube\.com$/i.test(location.hostname || '')) return null;
    // The page-world witness is invoked on demand for this exact video element.
    // A DOM hint is never an authorization; the native resolver verifies the ID.
    try { el?.dispatchEvent(new Event('vesper-youtube-identity-request', { bubbles: true })); }
    catch { return null; }
    const id = el?.getAttribute?.('data-vesper-youtube-video-id');
    if (!/^[A-Za-z0-9_-]{11}$/.test(id || '')) return null;
    const page = new URL('/watch', location.origin);
    page.searchParams.set('v', id);
    try {
      // The live watch page may retain a playlist that the player card omits.
      for (const value of [location.href, contextPage]) {
        if (!value) continue;
        const context = new URL(value);
        if (context.origin !== page.origin || context.pathname !== '/watch' || context.searchParams.get('v') !== id) continue;
        const list = context.searchParams.get('list');
        if (/^[\w-]{2,128}$/.test(list || '')) page.searchParams.set('list', list);
        break;
      }
    } catch { /* The player ID still identifies the video without a playlist context. */ }
    return page.href;
  }

  function pageForPlayer(el) {
    const contextPage = cardPage(el) || (activePreview?.player === el ? activePreview.page : pageAddress());
    return activeYouTubePage(el, contextPage) || activeTikTokPage(el, contextPage) || contextPage;
  }

  function titleForPlayer(player, targetPage, useDocumentTitle) {
    const identity = VesperPlayerSource.identity(targetPage);
    const documentIdentity = VesperPlayerSource.identity(pageAddress());
    // Timeline titles may describe the outer quoted post or the site itself.
    // Only use page metadata when it identifies this exact media page.
    const social = /(?:^|\.)(?:x\.com|twitter\.com|facebook\.com|instagram\.com|tiktok\.com)$/i.test(location.hostname || '');
    const specific = identity && (!/^https?:/.test(identity)
      || !social && document.querySelectorAll('video,audio').length === 1);
    return (useDocumentTitle && specific && identity === documentIdentity && document.title.replace(/\s+- YouTube$/, ''))
      || player.getAttribute?.('aria-label')?.trim().slice(0, 300)
      || (player.tagName === 'AUDIO' ? 'This audio' : 'This video');
  }
  function activeTikTokPage(el, contextPage) {
    if (!/(?:^|\.)tiktok\.com$/i.test(location.hostname || '') || el?.tagName !== 'VIDEO') return null;
    // The feed keeps its address at /, but its player wrapper carries the
    // actual post ID. Only use the nearest wrapper enclosing this exact video.
    for (let node = el.parentElement, depth = 0; node && depth < 12; node = node.parentElement, depth++) {
      const match = /^xgwrapper-\d+-(\d{10,24})$/.exec(node.id || '');
      if (!match) continue;
      const videos = node.querySelectorAll('video');
      if (videos.length !== 1 || videos[0] !== el) return null;
      const id = 'tiktok:' + match[1];
      if (VesperPlayerSource.identity(contextPage) === id) return contextPage;
      // A DOM hint is not permission or proof of bytes. The native adapter
      // requires its returned item ID to match this explicit ID.
      return new URL('/embed/v2/' + match[1], location.origin).href;
    }
    return null;
  }

  function setIntentCertainty(certain) {
    if (intentCertain === certain) return;
    intentCertain = certain;
    if (!certain) cancelPrefetch();
  }

  function selectPlayer() {
    if (!alive()) return;
    if (butterflyHidden()) {
      if (panelMedia && (contextPicker || activeBrowserCopy?.player === panelMedia.player)) place();
      else if (host) detach(true);
      return;
    }
    if (drag && (manualPlayer === video ? visiblePlayer(video, 110, 70) : visiblePlayer(video))) { place(true); return; }
    // Opening the picker is an explicit selection. Do not switch to a
    // larger or newly autoplaying neighbour until the picker closes.
    // Source-generation and navigation handlers still invalidate stale media.
    if (panelMedia && video === panelMedia.player) {
      place();
      if (panelMedia) return;
    }
    if (holdMissingPreview()) { place(); return; }
    const fullscreen = document.fullscreenElement;
    if (manualPlayer && (!manualPlayer.isConnected || !visiblePlayer(manualPlayer, 110, 70)
        && [...observed].some(el => el !== manualPlayer && visiblePlayer(el)))) {
      manualPlayer = null;
      movedPerch = null;
    }
    if (manualPlayer && (!fullscreen || fullscreen.contains(manualPlayer))) {
      setIntentCertainty(true);
      if (video !== manualPlayer) attach(manualPlayer);
      else place();
      return;
    }
    let best = null;
    if (activePreview && (!fullscreen || fullscreen.contains(activePreview.player))
        && visiblePlayer(activePreview.player)) best = activePreview.player;
    if (!best && isCurrentEndedPlayer(endedPlayer?.player)
        && (!fullscreen || fullscreen.contains(endedPlayer.player))) {
      const newPlayerPlaying = [...observed].some(el => el !== endedPlayer.player && !el.paused && !el.ended
        && (!fullscreen || fullscreen.contains(el)) && visiblePlayer(el));
      const videoAvailable = endedPlayer.player.tagName === 'AUDIO' && [...observed].some(el =>
        el.tagName === 'VIDEO' && (!fullscreen || fullscreen.contains(el)) && visiblePlayer(el) && !advertisementContext(el));
      if (!newPlayerPlaying && !videoAvailable && visiblePlayer(endedPlayer.player)) best = endedPlayer.player;
    }
    const previouslyCertain = intentCertain;
    if (best) setIntentCertainty(true);
    else {
      const candidates = [], players = new Map();
      const now = Date.now();
      for (const el of observed) {
        if (fullscreen && !fullscreen.contains(el)) continue;
        const r = visiblePlayer(el);
        if (!r) continue;
        const area = (r.right - r.left) * (r.bottom - r.top);
        const anchor = playerAnchor(el);
        const bounds = anchor?.getBoundingClientRect?.();
        const fullArea = bounds ? bounds.width * bounds.height : area;
        const id = evidenceKey(el);
        const binding = playback.get(el);
        candidates.push({ id, area, visibleFraction: fullArea > 0 ? area / fullArea : 0,
          playing: !el.paused && !el.ended, ready: el.readyState >= 1,
          progressed: el.currentTime > 1, controls: el.controls || anchor !== el, muted: el.muted,
          incidentalLoop: !!(el.defaultMuted && el.autoplay && el.loop && !el.controls),
          interacted: !!(binding && playerInteraction?.id === id && playerInteraction.generation === binding.generation
            && now - playerInteraction.at <= 30_000),
          incumbent: el === video,
          centerDistance: viewportCenterDistance(r),
          duration: Number.isNaN(el.duration) ? null : el.duration,
          networkBound: !!binding && mediaLedger.matches(id).some(entry => entry.bound),
          declaredSource: !!VesperPlayerSource.http(el.getAttribute?.('src') || el.querySelector?.('source[src]')?.getAttribute?.('src') || ''),
          adContext: advertisementContext(el) });
        players.set(id, el);
        if (candidates.length > VesperIntelligence.PLAYER_POLICY.limit) break;
      }
      // The v2 model abstains on ads, incidental loops, conflicting interaction and missing evidence;
      // VesperIntelligence keeps the v1 ranker as the fallback if the inference runtime is absent.
      const interacted = candidates.filter(candidate => candidate.interacted);
      const main = candidates.filter(candidate => !candidate.adContext && !candidate.incidentalLoop);
      const videos = main.filter(candidate => players.get(candidate.id).tagName !== 'AUDIO');
      let eligible = main.length ? main : candidates;
      if (interacted.length) eligible = interacted;
      else if (videos.length) eligible = videos;
      const intent = (globalThis.VesperInference || VesperIntelligence).rankPlayers(eligible);
      setIntentCertainty(intent.decisive);
      best = players.get(intent.selected);
    }
    if (best) {
      const same = best === video;
      attach(best);
      if (same && !previouslyCertain && intentCertain) schedulePrefetch(best);
    }
    else if (video && !video.isConnected) detach();
    else if (video) place();
  }

  // Lets the background briefly watch this tab for manifests named only by Content-Type.
  let announcedVideoAt = 0;
  // Segment paths the page fetched for media, without query strings. The worker compares them with candidate
  // manifests: a player's own segments live under its manifest's path, while ads and neighbours do not.
  const fragmentLog = [];
  function rememberFragment(url, entry) {
    const parsed = VesperIntelligence.http(url);
    if (!parsed) return;
    const at = Number.isFinite(entry.startTime) ? performance.timeOrigin + entry.startTime : Date.now();
    const path = parsed.origin + parsed.pathname;
    const existing = fragmentLog.findIndex(item => item.path === path);
    if (existing >= 0) fragmentLog.splice(existing, 1);
    fragmentLog.push({ path, at });
    const cutoff = Date.now() - 120_000;
    while (fragmentLog.length > 64 || fragmentLog.length && fragmentLog[0].at < cutoff) fragmentLog.shift();
  }
  function recentFragments(since) {
    const result = [], directories = new Set();
    for (let i = fragmentLog.length - 1; i >= 0 && result.length < 8; i--) {
      const item = fragmentLog[i];
      if (item.at < since - 2000) continue;
      const directory = item.path.slice(0, item.path.lastIndexOf('/') + 1);
      if (directories.has(directory)) continue;
      directories.add(directory);
      result.push(item.path);
    }
    return result;
  }
  function playerFragmentWitness(player, binding) {
    if (!player || !binding || !/^blob:https?:/i.test(playerSource(player))
        || player.mediaKeys || protectedVideos.has(player)) return { paths: [], fingerprints: [] };
    const attribute = 'data-vesper-media-provenance';
    try {
      player.removeAttribute(attribute);
      player.dispatchEvent(new Event('vesper-media-provenance-request', { bubbles: true }));
      const value = player.getAttribute(attribute);
      player.removeAttribute(attribute);
      if (!value || value.length > 32_768) return { paths: [], fingerprints: [] };
      const data = JSON.parse(value);
      if (!Array.isArray(data.paths)) return { paths: [], fingerprints: [] };
      const now = Date.now();
      // The witness belongs to the current Blob/MSE object, which may have been
      // populated before loadedmetadata. It remains untrusted ranking evidence.
      const witnesses = data.paths.slice(0, 12).filter(item => Number.isFinite(item?.seen)
        && item.seen >= now - 120_000
        && item.seen <= now + 1000)
        .map(item => ({ url: VesperPlayerSource.http(item.path), fingerprint: item.fingerprint }))
        .filter(item => item.url && !item.url.search);
      return { paths: [...new Set(witnesses.map(item => item.url.href))],
        fingerprints: [...new Set(witnesses.map(item => item.fingerprint)
          .filter(value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value)))] };
    } catch { return { paths: [], fingerprints: [] }; }
  }
  function watchMediaResources() {
    if (resourceObserver || typeof PerformanceObserver === 'undefined') return;
    try {
      resourceObserver = new PerformanceObserver(list => {
        if (!observed.size || !alive()) return;
        const players = [...observed].filter(el => el.isConnected && (el.readyState >= 1 || !el.paused));
        for (const entry of list.getEntries().slice(-24)) {
          if (entry.entryType !== 'resource'
              || !['fetch', 'xmlhttprequest', 'video', 'audio', 'other'].includes(entry.initiatorType)) continue;
          const url = VesperIntelligence.http(entry.name)?.href;
          const mime = typeof entry.contentType === 'string' ? entry.contentType : '';
          const kind = VesperIntelligence.sourceKind(url || '', mime);
          if (url && (kind === 'fragment' || ['fetch', 'xmlhttprequest'].includes(entry.initiatorType)
              && /^(?:video|audio)\/|mp2t/i.test(mime))) rememberFragment(url, entry);
          if (!url || kind === 'fragment'
              || kind === 'unknown' && !['video', 'audio'].includes(entry.initiatorType)) continue;
          const matchingPlayers = players.filter(el => VesperPlayerSource.http(playerSource(el))?.href === url);
          // Being the sole playing element does not bind all fetches on the page to it.
          const player = matchingPlayers.length === 1 ? matchingPlayers[0] : null;
          const start = Number.isFinite(entry.startTime) && entry.startTime >= 0
            ? performance.timeOrigin + entry.startTime : null;
          if (!Number.isFinite(start)) continue;
          mediaLedger.record({ scope: location.origin, url, mime, type: 'resource',
            playerKey: player ? evidenceKey(player) : null, at: start });
        }
      });
      resourceObserver.observe({ type: 'resource', buffered: true });
    } catch { resourceObserver = null; }
  }
  function announceVideoElement(force = false) {
    const now = Date.now();
    if (!force && now - announcedVideoAt < 60_000) return;
    announcedVideoAt = now;
    watchMediaResources();
    send({ type: 'media-element', watching: true }).catch(() => {});   // a hint only; never surfaces errors
  }

  function canAdoptInitialBinding(player, previous, src, page) {
    if (previous || !panelMedia || panelMedia.player !== player || panelMedia.generation !== 0
        || !panel?.classList.contains('show') || !player.isConnected
        || panelMedia.documentIdentity !== VesperPlayerSource.identity(pageAddress())) return false;
    const identity = VesperPlayerSource.identity(page);
    return !!identity && identity === VesperPlayerSource.identity(panelMedia.pageUrl)
      && (!panelMedia.src || panelMedia.src === src);
  }

  function consider(el) {
    if (!alive() || !(el instanceof HTMLMediaElement)) return;
    const src = el.currentSrc || el.src || '';
    if ((el instanceof HTMLVideoElement || el.tagName === 'AUDIO' && playerAnchor(el)) && !observed.has(el) &&
        (src || el.srcObject || el.readyState >= 1 || el.querySelector('source[src]'))) {
      observed.add(el);
      if (playerAnchor(el) === el) visibility.observe(el);
      announceVideoElement();
    }
    const page = pageForPlayer(el);
    mediaLedger.playback({ key: evidenceKey(el), scope: location.origin, pageUrl: page,
      mediaUrl: src, stableId: VesperPlayerSource.identity(page) || '', duration: el.duration });
    const previous = playback.get(el) || (isCurrentEndedPlayer(el) ? endedPlayer.binding : null);
    if (el.readyState >= 1 && (previous?.src !== src || previous?.page !== page)
        && !(isCurrentEndedPlayer(el) && !src)) {
      const adoptInitial = canAdoptInitialBinding(el, previous, src, page);
      const changedAt = Date.now();
      const binding = { src, page, generation: ++generation, changedAt };
      playback.set(el, binding);
      if (endedPlayer?.player === el && binding.generation !== endedPlayer.binding.generation) {
        endedPlayer = null;
        clearTimeout(endedPlayerTimer);
      }
      if (activePreview?.player === el) {
        activePreview = { ...activePreview, page, src,
          key: `${VesperPlayerSource.identity(page)}|${page}|${src}|${binding.generation}`, binding };
        if (!adoptInitial && panelMedia && panelMedia.targetKey !== activePreview.key) closePanel();
      }
      if (el === video) mediaChangedAt = changedAt;
      if (el === video && panelMedia?.player === el) {
        retainedPickerDraft = null;
        if (adoptInitial) {
          // Invalidate the provisional lookup immediately; the first metadata
          // event is not a replacement of an already bound media source.
          const request = ++panelRequest;
          panelMedia = { ...panelMedia, pageUrl: page, src, formatSrc: src, generation: binding.generation,
            playerState: pickerPlayerState(el),
            targetKey: activePreview?.player === el ? activePreview.key
              : `${VesperPlayerSource.identity(page)}|${page}|${src}|${binding.generation}` };
          Promise.resolve().then(() => {
            if (isCurrentRequest(request) && video === el) void loadFormats(true);
          });
        } else closePanel();
      }
      if (el === video && retainedPickerDraft) retainedPickerDraft = null;
    }
  }

  function mediaTreeChanged(records) {
    if (!alive() || discoveryFrame) return;
    const selector = 'video,audio[controls],source';
    const relevant = (node) => node?.nodeType === 1 && (node.matches?.(selector) || node.querySelector?.(selector));
    // Feed virtualization can insert a ready, paused player or swap post
    // links inside a recycled card without firing a new media event.
    const touchesObserved = target => target?.nodeType === 1 && [...observed].some(el => target.contains?.(el));
    const linkInObservedCard = target => {
      if (target?.tagName !== 'A') return false;
      for (let parent = target.parentElement, depth = 0; parent && depth < 10; parent = parent.parentElement, depth++)
        if (touchesObserved(parent)) return true;
      return false;
    };
    if (host && !host.isConnected || [...observed].some(el => !el.isConnected || audioControls.get(el)?.isConnected === false)
        || records.some(record => record.type === 'attributes'
          ? (record.attributeName === 'href' ? linkInObservedCard(record.target) : relevant(record.target) || touchesObserved(record.target))
          : [...record.addedNodes].some(relevant) || touchesObserved(record.target))) {
      discoveryFrame = requestAnimationFrame(() => {
        discoveryFrame = 0;
        refreshPlayers();
      });
    }
  }

  function refreshPlayers() {
    if (!alive()) return;
    syncPage();
    if (butterflyHidden()) { selectPlayer(); return; }
    for (const el of observed) {
      if (!el.isConnected || !playerAnchor(el)) { visibility.unobserve(el); observed.delete(el); }
    }
    document.querySelectorAll('video,audio[controls]').forEach(consider);
    selectPlayer();
  }

  function cancelPrefetch() {
    if (prefetchTimer) clearTimeout(prefetchTimer);
    prefetchTimer = 0;
    prefetchTarget = null;
  }

  function schedulePrefetch(el, delay = 250) {
    if (!intentCertain || !el || butterflyHidden() || document.hidden || panelMedia || el !== video) return;
    if (prefetchTimer && prefetchTarget === el) return;
    cancelPrefetch();
    prefetchTarget = el;
    prefetchTimer = setTimeout(() => {
      prefetchTimer = 0;
      prefetchTarget = null;
      prefetchPlayer(el);
    }, delay);
  }

  function prefetchPlayer(el) {
    if (!alive() || !intentCertain || document.hidden || butterflyHidden() || panelMedia || el !== video || !(el instanceof HTMLVideoElement) || !observed.has(el)
        || protectedVideos.has(el) || el.mediaKeys || !visiblePlayer(el)) return;
    const ended = isCurrentEndedPlayer(el);
    const src = playerSource(el) || (ended ? endedPlayer.binding.src : '');
    const targetPage = pageForPlayer(el);
    const identity = VesperPlayerSource.identity(targetPage);
    if (!identity) return;
    const binding = playback.get(el) || (ended ? endedPlayer.binding : null);
    const current = binding?.src === src && VesperPlayerSource.identity(binding.page) === identity;
    if (!current || el.readyState < 1 || !src || el.duration === Infinity) return;
    const key = `${identity}|${targetPage}|${src}|${binding.generation}`;
    if (prefetchedPlayers.get(el) === key) return;
    prefetchedPlayers.set(el, key);
    const witness = playerFragmentWitness(el, binding);
    const title = titleForPlayer(el, targetPage, current && activePreview?.player !== el);
    void send({ type: 'media-options', prefetch: true, pageUrl: targetPage, multiplePlayers: hasOtherPlayer(el),
      mediaChangedAt: current ? binding.changedAt : mediaChangedAt, title,
      playerPlaying: !el.paused && !el.ended, playerHasMedia: current && (el.readyState >= 1 || el.ended || ended), playerPageUrl: targetPage,
      playerUrl: current ? src : '', playerKey: evidenceKey(el), generation: current ? binding.generation : 0,
      mutedLoop: current && activePreview?.player !== el && isMutedLoop(el, src),
      playerResourceFingerprints: witness?.fingerprints, playerFragmentUrls: witness?.paths,
      manifestUrl: current && !VesperPlayerSource.http(src) ? declaredManifest(identity) : undefined
    }).then(reply => {
      if (!reply?.data?.prefetchSkipped || prefetchedPlayers.get(el) !== key) return;
      const retry = reply.data.retryAfterMs;
      if (Number.isFinite(retry) && retry > 0 && retry <= 15_000 && prefetchRetried.get(el) !== key) {
        prefetchRetried.set(el, key);
        prefetchedPlayers.delete(el);
        schedulePrefetch(el, retry + 100);
      }
    });
  }

  // Capture-phase listeners on document: media events do not bubble, but they can be captured. One-time cost.
  for (const type of ['loadedmetadata', 'loadeddata', 'canplay', 'playing', 'durationchange']) document.addEventListener(type, (e) => {
    if (type === 'playing') {
      endedPlayer = null;
      clearTimeout(endedPlayerTimer);
    }
    consider(e.target);
    refreshPlayer(e.target);
    if (e.target === video) {
      announceVideoElement();
      schedulePrefetch(e.target);
    }
  }, { capture: true, passive: true, signal });
  // Renew on deliberate playback transitions, not a background timer or timeupdate loop.
  document.addEventListener('seeking', e => {
    if (alive() && e.target === video) announceVideoElement();
  }, { capture: true, passive: true, signal });
  document.addEventListener('ended', e => {
    if (!alive()) return;
    rememberEndedPlayer(e.target);
    consider(e.target);
    refreshPlayer(e.target);
  }, { capture: true, passive: true, signal });
  for (const type of ['emptied', 'loadstart']) document.addEventListener(type, (e) => {
    if (!alive()) return;
    if (e.target instanceof HTMLMediaElement) {
      const page = pageForPlayer(e.target);
      mediaLedger.playback({ key: evidenceKey(e.target), scope: location.origin,
        pageUrl: page, mediaUrl: playerSource(e.target),
        stableId: VesperPlayerSource.identity(page) || '', event: type });
    }
    if (type === 'loadstart') {
      protectedVideos.delete(e.target);
      playback.delete(e.target);
      if (activePreview?.player === e.target) activePreview = null;
      if (e.target === video) {
        mediaChangedAt = Date.now();
        retainedPickerDraft = null;
      }
      if (panelMedia?.player === e.target) {
        retainedPickerDraft = null;
        closePanel();
      }
    } else {
      protectedVideos.delete(e.target);
      if (isCurrentEndedPlayer(e.target)) {
        playback.set(e.target, endedPlayer.binding);
      } else {
        if (activePreview?.player === e.target && playback.has(e.target))
          activePreview = { ...activePreview, binding: playback.get(e.target) };
        playback.delete(e.target);
        if (panelMedia?.player === e.target) {
          retainedPickerDraft = null;
          closePanel();
        }
      }
    }
    if (type === 'loadstart' && endedPlayer?.player === e.target) {
      endedPlayer = null;
      clearTimeout(endedPlayerTimer);
    }
    if (type === 'loadstart' && e.target instanceof HTMLMediaElement && (playerSource(e.target) || e.target.srcObject)) {
      consider(e.target);
      refreshPlayer(e.target);
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

  // the butterfly

  // "1:23", "01:23:45" or plain seconds -> seconds. Empty or nonsense -> null (the field's default applies).
  function parseClock(text) {
    const value = String(text || '').trim();
    if (!value) return null;
    if (!/^\d+(?::[0-5]?\d){0,2}(?:\.\d+)?$/.test(value)) return null;
    const parts = value.split(':').map(Number);
    if (parts.some(part => !Number.isFinite(part))) return null;
    return parts.reduce((total, part) => total * 60 + part, 0);
  }
  function clockText(seconds) {
    const tenths = Math.max(0, Math.round(seconds * 10));
    const total = Math.floor(tenths / 10);
    const fraction = tenths % 10;
    const h = Math.floor(total / 3600), m = Math.floor(total % 3600 / 60), s = total % 60;
    const secondsText = String(s).padStart(2, '0') + (fraction ? `.${fraction}` : '');
    return (h ? `${h}:${String(m).padStart(2, '0')}` : String(m)) + ':' + secondsText;
  }

  function clipAvailability(option, seconds, live = false) {
    if (live) return { available: false, reason: "Trim isn't available for live streams." };
    if (!Number.isFinite(seconds) || seconds < 0.1 || seconds > 31_536_000)
      return { available: false, reason: 'Trim needs a known media length.' };
    if (option?.canClip !== true)
      return { available: false, reason: "This format can't be trimmed. Choose another format." };
    return { available: true, reason: option.clipUsesWholeFile
      ? String(option.container || '').toLowerCase() === 'webm' ? 'Downloads the full video, then cuts at keyframe and frame boundaries; the range may shift.'
        : seconds > 1200 ? 'Downloads the full video, then trims it on your PC.' : ''
      : 'The cut follows segment boundaries and may shift slightly.' };
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
          transition:opacity .18s ease;touch-action:none}
        .fly.dragging{cursor:grabbing}
        .fly[hidden]{display:none}.fly.show,.fly:focus-visible{opacity:1;pointer-events:auto}
        .fly canvas{transition:translate 160ms ease-out}.fly:hover canvas{translate:0 -2px}
        @media (prefers-reduced-motion:reduce){.fly canvas{transition:none}.fly:hover canvas{translate:none}}
        .fly:focus-visible{outline:2px solid #fffbee;outline-offset:2px;box-shadow:0 0 0 4px #1f3d2b;border-radius:8px}
        .fly canvas{width:100%;height:100%;pointer-events:none;filter:drop-shadow(0 1px 2px #0008)}
        .fly-fallback{display:grid;place-items:center;width:29px;height:29px;border-radius:7px;
          background:var(--green);color:#fffbee;font-size:19px;pointer-events:none}
        .fly.companion-ready .fly-fallback{display:none}
        .panel{position:fixed;width:340px;max-width:calc(100vw - 16px);max-height:min(630px,calc(100vh - 16px));
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
        .clip{box-sizing:border-box;display:grid;gap:9px;width:calc(100% - 16px);margin:8px 8px 0;padding:10px 11px;
          border:1px solid var(--border);border-radius:8px;background:var(--surface);font-size:12px;color:var(--text)}
        .clip:not(.expanded){gap:0;padding:5px 10px}
        .clip[hidden],.clip .fields[hidden],.hint[hidden]{display:none}
        .clip .fields{display:grid;gap:8px;width:100%}
        .clip .trim-values{display:flex;align-items:center;gap:10px;width:100%}
        .clip .trim-value{display:grid;grid-template-columns:minmax(0,1fr) minmax(64px,92px);flex:1 1 0;min-width:0;gap:7px;align-items:center;color:var(--muted);font-size:11px}
        .clip .trim-value input{box-sizing:border-box;width:100%;min-width:0;border:1px solid var(--border);border-radius:6px;background:var(--surface);color:var(--text);font:inherit;padding:5px 7px;text-align:center}
        .clip .trim-range{--range-start:0%;--range-end:100%;position:relative;width:100%;height:28px;touch-action:none;
          background:linear-gradient(to right,var(--border) 0,var(--border) var(--range-start),var(--green) var(--range-start),var(--green) var(--range-end),var(--border) var(--range-end),var(--border) 100%) center/100% 4px no-repeat}
        .clip .trim-range input[type=range]{position:absolute;inset:0;width:100%;height:100%;margin:0;appearance:none;background:transparent;pointer-events:none;accent-color:var(--green)}
        .clip .trim-range input[type=range]::-webkit-slider-runnable-track{height:4px;background:transparent}
        .clip .trim-range input[type=range]::-webkit-slider-thumb{appearance:none;width:16px;height:16px;margin-top:-6px;border:2px solid var(--green);border-radius:50%;background:var(--surface);pointer-events:auto;cursor:ew-resize}
        .clip .trim-range input[type=range]:focus-visible::-webkit-slider-thumb{outline:2px solid var(--text);outline-offset:2px}
        .clip input[type=checkbox]{accent-color:var(--green);margin:0}
        .clip>label{display:flex;gap:8px;align-items:center;min-height:20px;font-weight:600}
        .clip-error{color:var(--red);font-size:11px}
        .clip-note{color:var(--muted);font-size:11px;line-height:1.35}
        small.hint{display:block;padding:4px 12px 0;color:var(--muted);font-size:11px}
        .hint.bad{color:var(--red)}
        .collection-browse{flex:none;margin-right:auto}
        .output[hidden]{display:none}
        :is(.output,.audio-track) select{max-width:100%;min-width:0;border:1px solid var(--border);border-radius:6px;background:var(--surface);color:var(--text);
          color-scheme:inherit;padding:4px 6px;font:inherit;cursor:pointer}
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
        .more{display:block;width:calc(100% - 16px);margin:8px 8px 0;font-size:12px;min-height:32px}
        .languages summary{box-sizing:border-box;list-style:none;text-align:center}
        .languages summary::-webkit-details-marker{display:none}
        .languages summary::after{content:' ▾'}
        .languages[open] summary::after{content:' ▴'}
        .language-fields{display:grid;gap:8px;padding:3px 12px 12px;font-size:12px}
        .audio-track{display:grid;grid-template-columns:auto minmax(0,1fr);align-items:center;gap:8px}
        .subtitle-choice{display:flex;align-items:flex-start;gap:8px;overflow-wrap:anywhere}
        .subtitle-choice[hidden],.language-fields .hint[hidden]{display:none}
        .subtitle-choice input{flex:none;margin:2px 0 0;accent-color:var(--green)}
        .subtitle-choice:has(input:disabled){color:var(--muted)}
        .language-fields small.hint{padding:0 0 0 21px}
        .foot{display:flex;gap:8px;align-items:center;justify-content:flex-end;padding:10px 12px;border-top:1px solid var(--border);
          position:sticky;bottom:0;background:var(--surface)}
        .picker-foot{margin-top:8px}
        .msg{padding:14px;font-size:12.5px;line-height:1.45}.msg.bad{color:var(--red)}
        .link{border:0;background:none;color:var(--blue);text-decoration:underline;cursor:pointer;font-size:12.5px;padding:0}
    `;
    button = element('button', 'fly');
    button.title = 'Find media with VESPER (Alt+Shift+V · Shift+Click for quick download)';
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
    button.addEventListener('pointerenter', () => {
      if (alive() && video && !panel?.classList.contains('show')) prefetchPlayer(video);
    }, { passive: true, signal });
    button.addEventListener('click', (e) => {
      e.preventDefault();
      e.stopPropagation();
      if (suppressDragClick && e.detail !== 0) {
        suppressDragClick = false;
        clearTimeout(suppressDragClickTimer);
        suppressDragClickTimer = 0;
        return;
      }
      if (e.shiftKey) void quickDownload();
      else void togglePanel();
    }, { signal });
    // Feed players often handle pointer/mouse events on a common ancestor.
    // Do not let interaction with our overlay toggle their playback controls.
    for (const type of ['pointerdown','pointerup','mousedown','mouseup','dblclick'])
      root.addEventListener(type, e => e.stopPropagation(), { signal });
    button.draggable = false;
    button.addEventListener('pointerdown', startDrag, { signal });
    button.addEventListener('pointermove', moveDrag, { signal });
    button.addEventListener('pointerup', finishDrag, { signal });
    button.addEventListener('pointercancel', finishDrag, { signal });
    button.addEventListener('lostpointercapture', finishDrag, { signal });
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
    lastPointerPosition = null;
    button?.blur();
    stopFullscreenIdle();
    fullscreenPlayer = false;
    if (host) mount();
    selectPlayer();
  }, { passive: true, signal });
  document.addEventListener('visibilitychange', () => {
    if (!alive()) return;
    if (document.hidden) stopFullscreenIdle();
    else if (fullscreenPlayer) wakeFullscreen();
    updateFlyVisibility();
  }, { signal });
  document.addEventListener('keydown', e => {
    if (!alive() || !e.altKey || !e.shiftKey || e.key.toLowerCase() !== 'v' || !perch || button?.hidden) return;
    e.preventDefault();
    button?.focus({ preventScroll: true });
  }, { signal });

  function attach(el) {
    if (video === el && host) {
      if (host.isConnected === false) mount();
      clearTimeout(previewExitTimer);
      previewExitTimer = previewRemovedAt = 0;
      place();
      return;
    }
    cancelPrefetch();
    if (video && video !== el) retainedPickerDraft = null;
    clearTimeout(previewExitTimer);
    previewExitTimer = previewRemovedAt = 0;
    if (drag) finishDrag({ pointerId: drag.id });
    closePanel();
    resize.disconnect();
    video = el;
    if (!host) {
      try { build(chrome.runtime.getURL('icons/vesper-companion-atlas.png')); }
      catch (error) { if (!VesperTheme.contextGone(error)) throw error; retire(); return; }
    }
    resize.observe(playerAnchor(el) || el);
    companion?.scheduleIdle(idleContext);
    if (!listening) {
      listening = true;
      document.addEventListener('pointermove', onPointer, { passive: true, capture: true, signal });
      document.addEventListener('pointerleave', clearPointer, { passive: true, signal });

    }
    place();
    schedulePrefetch(el);
  }

  function detach(preservePickerDraft = false) {
    cancelPrefetch();
    if (!preservePickerDraft) retainedPickerDraft = null;
    manualPlayer = null;
    listening = false;
    activePreview = null;
    companion?.stopIdle();
    stopFullscreenIdle();
    fullscreenPlayer = false;
    drag = null;
    clearTimeout(previewExitTimer);
    previewExitTimer = previewRemovedAt = 0;
    closePanel();
    resize.disconnect();
    unwatchTheme?.();
    unwatchTheme = null;
    document.removeEventListener('pointermove', onPointer, true);
    document.removeEventListener('pointerleave', clearPointer);

    cancelAnimationFrame(frame);
    frame = 0;
    perch = null;
    companion?.dispose();
    companion = null;
    host?.remove(); host = root = button = panel = receipt = null; video = null;
  }

  function pickerTransitionRect(player) {
    // Some players briefly hide their video element when the pointer leaves the
    // controls. An explicit picker is already bound to a concrete source.
    // Never use this exception to select an invisible player automatically.
    if (!perch || !panelMedia || panelMedia.player !== player || !panelMedia.generation
        || !panel?.classList.contains('show') || player.readyState < 1
        || intersecting.get(player) === false || document.fullscreenElement
        || panelMedia.documentIdentity !== VesperPlayerSource.identity(pageAddress())
        || playback.get(player)?.generation !== panelMedia.generation
        || playerSource(player) !== panelMedia.src || !panelMedia.src
        || activePreview && activePreview.key !== panelMedia.targetKey) return null;
    const style = getComputedStyle(player);
    if (style.opacity !== '0' || style.visibility !== 'visible' || style.display === 'none'
        || hiddenPlayerContainer(player)) return null;
    return clippedPlayerRect(player.getBoundingClientRect());
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
      const rect = (manualPlayer === video ? visiblePlayer(video, 110, 70) : visiblePlayer(video))
        || pickerTransitionRect(video);
      button.hidden = !rect || !!fullscreen && (fullscreen instanceof HTMLVideoElement || fullscreen instanceof HTMLIFrameElement || !fullscreen.contains(video));
      if (button.hidden) {
        fullscreenPlayer = false;
        stopFullscreenIdle();
        perch = null;
        closePanel();
        return;
      }
      const watchingFullscreen = Boolean(fullscreen) || fillsFullscreen(rect);
      if (watchingFullscreen !== fullscreenPlayer) {
        fullscreenPlayer = watchingFullscreen;
        if (watchingFullscreen) wakeFullscreen();
        else stopFullscreenIdle();
      }
      perch = playerPerch(rect, fullscreenPlayer);
      if (!perch) {
        button.hidden = true;
        updateFlyVisibility();
        closePanel();
        return;
      }
      companion.setPerch(perch.x, perch.y, perch.faceLeft, fullscreenPlayer, { continuous, immediate: !!drag?.moved });
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

  function fillsFullscreen(rect) {
    const fillsWidth = rect.left <= 8 && rect.right >= innerWidth - 8;
    const fillsHeight = rect.top <= 8 && rect.bottom >= innerHeight - 8;
    const display = window.screen;
    // The parent can fullscreen a cross-origin iframe without setting fullscreenElement inside it.
    if (fillsWidth && fillsHeight && display && Math.abs(display.width - innerWidth) <= 8
        && Math.abs(display.height - innerHeight) <= 8) return true;
    // A feed may fill one axis inside a fixed, viewport-sized app shell.
    // That is not fullscreen playback: keep the butterfly visible there.
    if (!fillsWidth || !fillsHeight) return false;
    // Some players expand a fixed container over the whole tab instead of using the Fullscreen API.
    for (let parent = video, depth = 0; parent && depth < 6; parent = parent.parentElement, depth++) {
      if (getComputedStyle(parent).position !== 'fixed') continue;
      const box = parent.getBoundingClientRect();
      if (box.left <= 8 && box.top <= 8 && box.right >= innerWidth - 8 && box.bottom >= innerHeight - 8)
        return true;
    }
    return false;
  }

  function playerPerch(rect, fullscreen) {
    if (movedPerch) {
      // Bind manual positions to the *unclipped* element box. A clipped rect
      // changes as the page scrolls and otherwise makes the butterfly jump.
      const anchor = (playerAnchor(video) || video).getBoundingClientRect();
      const width = anchor.right - anchor.left - FLY_SIZE;
      const height = anchor.bottom - anchor.top - FLY_SIZE;
      const x = Math.max(8, Math.min(anchor.left + width * movedPerch.x, innerWidth - FLY_SIZE - 8));
      const y = Math.max(8, Math.min(anchor.top + height * movedPerch.y, innerHeight - FLY_SIZE - 8));
      return { x, y, faceLeft: movedPerch.x > 0.5 };
    }
    const preview = activePreview?.player === video && !fullscreen;
    const right = { x: rect.right - FLY_SIZE - 10, faceLeft: true };
    const left = { x: rect.left + 10, faceLeft: false };
    const corners = preview ? [left, right] : [right, left];
    const clear = (x, y) => y >= 4 && ![8, FLY_SIZE / 2, FLY_SIZE - 8].some(offset =>
      (document.elementsFromPoint?.(x + offset, y + FLY_SIZE / 2) || [])
        .some(el => {
          if (el === host || el === video) return false;
          const control = el.closest?.('button, a[href], input, [role="button"]');
          if (!control || control === host || control.contains?.(video)) return false;
          return true;
        }));
    if (video.tagName === 'AUDIO') {
      // Keep the audio controls clear.
      const candidates = [
        ...corners.map(corner => ({ ...corner, y: rect.top - FLY_SIZE - 4 })),
        ...corners.map(corner => ({ ...corner, y: rect.bottom + 4 })),
        { x: rect.right + 4, y: rect.top, faceLeft: true },
        { x: rect.left - FLY_SIZE - 4, y: rect.top, faceLeft: false }
      ];
      return candidates.find(({ x, y }) => x >= 4 && y >= 4 && x + FLY_SIZE <= innerWidth - 4
        && y + FLY_SIZE <= innerHeight - 4 && clear(x, y)) || null;
    }
    const border = Math.abs(video.getBoundingClientRect().top - rect.top) < 2;
    const positions = fullscreen || !border ? [rect.top + 8] : [rect.top - VesperCompanion.footY, rect.top + 8];
    if (rect.bottom - rect.top >= 180) positions.push(rect.top + 56);
    for (const y of positions) {
      const corner = corners.find(({ x }) => clear(x, y));
      if (corner) return { ...corner, y };
    }
    const middle = rect.top + (rect.bottom - rect.top - FLY_SIZE) / 2;
    const side = corners.find(({ x }) => clear(x, middle));
    if (side) return { ...side, y: middle };
    return { ...corners[0], y: rect.top + 8 };
  }

  function startDrag(e) {
    if (e.button !== 0 || !e.isPrimary || !perch || !video) return;
    suppressDragClick = false;
    clearTimeout(suppressDragClickTimer);
    suppressDragClickTimer = 0;
    const box = button.getBoundingClientRect();
    drag = { id: e.pointerId, player: video, x: e.clientX, y: e.clientY,
      grabX: e.clientX - box.left, grabY: e.clientY - box.top, moved: false };
    button.setPointerCapture(e.pointerId);
  }

  function dragPerch(rect, x, y, grabX, grabY) {
    // A manual perch can occupy any visible free space, not only 44px outside
    // the player's rectangle. The viewport clamp keeps the control clickable.
    const left = Math.max(8, Math.min(x - grabX, innerWidth - FLY_SIZE - 8));
    const top = Math.max(8, Math.min(y - grabY, innerHeight - FLY_SIZE - 8));
    return {
      x: (left - rect.left) / Math.max(1, rect.right - rect.left - FLY_SIZE),
      y: (top - rect.top) / Math.max(1, rect.bottom - rect.top - FLY_SIZE)
    };
  }

  function moveDrag(e) {
    if (drag?.id !== e.pointerId || drag.player !== video) return;
    if (!drag.moved && Math.hypot(e.clientX - drag.x, e.clientY - drag.y) < 5) return;
    const rect = manualPlayer === video ? visiblePlayer(video, 110, 70) : visiblePlayer(video);
    if (!rect) return;
    drag.moved = true;
    drag.lastX = e.clientX;
    drag.lastY = e.clientY;
    button.classList.add('dragging');
    movedPerch = dragPerch((playerAnchor(video) || video).getBoundingClientRect(), e.clientX, e.clientY, drag.grabX, drag.grabY);
    place(true);
  }

  function dropTarget(x, y) {
    if (!Number.isFinite(x) || !Number.isFinite(y)) return null;
    let closest = null, bestDistance = 68;
    for (const player of observed) {
      if (player === video || !player.isConnected) continue;
      const fullscreen = document.fullscreenElement;
      if (fullscreen && !fullscreen.contains(player)) continue;
      if (player.readyState < 1 && !playerSource(player)) continue;
      const rect = visiblePlayer(player, 110, 70);
      if (!rect) continue;
      const dx = Math.max(rect.left - x, x - rect.right, 0);
      const dy = Math.max(rect.top - y, y - rect.bottom, 0);
      const distance = Math.hypot(dx, dy);
      if (distance < bestDistance) { closest = player; bestDistance = distance; }
    }
    return closest;
  }

  function finishDrag(e) {
    if (drag?.id !== e.pointerId) return;
    const last = drag;
    suppressDragClick = last.moved;
    clearTimeout(suppressDragClickTimer);
    suppressDragClickTimer = last.moved ? setTimeout(() => {
      suppressDragClick = false;
      suppressDragClickTimer = 0;
    }, 300) : 0;
    drag = null;
    button?.classList.remove('dragging');
    if (last.moved && e.type !== 'pointercancel') {
      const x = Number.isFinite(e.clientX) ? e.clientX : last.lastX;
      const y = Number.isFinite(e.clientY) ? e.clientY : last.lastY;
      const next = dropTarget(x, y);
      if (next && visiblePlayer(next, 110, 70)) {
        movedPerch = dragPerch((playerAnchor(next) || next).getBoundingClientRect(), x, y, last.grabX, last.grabY);
        manualPlayer = next;
        attach(next); // The companion animates the flight unless reduced motion is on.
      } else manualPlayer = video;
    }
    updateFlyVisibility();
  }

  function schedule() {
    if (alive() && !frame) frame = requestAnimationFrame(() => {
      frame = 0;
      // Geometry and focus can change before IntersectionObserver reports
      // a new threshold. Re-rank competing players even if the old one remains visible.
      if (observed.size <= 1 && (manualPlayer === video ? visiblePlayer(video, 110, 70) : visiblePlayer(video))) place(true);
      else selectPlayer();
    });
  }

  function stopFullscreenIdle() {
    clearTimeout(fullscreenTimer);
    fullscreenTimer = 0;
  }

  function wakeFullscreen() {
    pointerMovedAt = Date.now();
    if (fullscreenTimer || document.hidden) return;
    function idle() {
      fullscreenTimer = 0;
      if (!alive() || !fullscreenPlayer || document.hidden) return;
      const remaining = FULLSCREEN_IDLE - (Date.now() - pointerMovedAt);
      if (remaining > 0) fullscreenTimer = setTimeout(idle, remaining);
      else updateFlyVisibility();
    }
    fullscreenTimer = setTimeout(idle, FULLSCREEN_IDLE);
  }

  function updateFlyVisibility() {
    if (!button || !companion) return;
    if (!perch || button.hidden) { companion.setVisible(false); return; }
    const awake = Date.now() - pointerMovedAt < FULLSCREEN_IDLE;
    const show = !butterflyHidden() && !document.hidden && (!fullscreenPlayer || awake || drag
      || panel.classList.contains('show') || !!receiptTimer || button.matches(':focus-visible'));
    button.classList.toggle('show', show);
    companion.setVisible(Boolean(show));
  }

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
    const moved = !lastPointerPosition || lastPointerPosition.x !== e.clientX || lastPointerPosition.y !== e.clientY;
    if (!moved && !drag) return;
    lastPointerPosition = { x: e.clientX, y: e.clientY };
    pointerMovedAt = Date.now();
    if (fullscreenPlayer) {
      wakeFullscreen();
      if (!drag && !panel?.classList.contains('show') && button?.matches(':focus-visible')) button.blur();
    }
    pointer = { x: e.clientX, y: e.clientY };
    if (drag || panelMedia || host && (e.target === host || e.composedPath?.().includes(host))) {
      pointerTarget = e.target;
      updateFlyVisibility();
      return;
    }
    if (e.target !== pointerTarget) {
      pointerTarget = e.target;
      const preview = cardPreview(e.target);
      if (activatePreview(preview)) selectPlayer();
      else if (!preview && activePreview && !previewMissing() && !previewLatched()) {
        activePreview = null;
        selectPlayer();
      }
    }
    updateFlyVisibility();
    holdMissingPreview();
    companion?.pointerMove(e.clientX, e.clientY);
  }
  function clearPointer() {
    pointer = pointerTarget = null;
    lastPointerPosition = null;
    companion?.pointerMove(-Infinity, -Infinity);
    updateFlyVisibility();
    holdMissingPreview();
  }

  function previewLatched() {
    if (panel?.classList.contains('show') || button?.matches(':hover,:focus-visible')) return true;
    return !!pointer && !!perch && Math.hypot(pointer.x - perch.x - FLY_SIZE / 2, pointer.y - perch.y - FLY_SIZE / 2) <= 90;
  }

  function pointerOnButterfly() {
    return !!button?.matches(':hover,:focus-visible');
  }

  function butterflyDistance() {
    return pointer && perch ? Math.hypot(pointer.x - perch.x - FLY_SIZE / 2, pointer.y - perch.y - FLY_SIZE / 2) : Infinity;
  }

  function previewMissing() {
    return !!video && activePreview?.player === video && (video.readyState < 1 || !visiblePlayer(video));
  }

  function holdMissingPreview() {
    if (document.fullscreenElement || !perch || !previewMissing()) return false;
    if (panelMedia?.player === video) return true;
    if (!previewRemovedAt) {
      previewRemovedAt = Date.now();
      previewPointerDistance = butterflyDistance();
    }
    if (!previewExitTimer) {
      const player = video;
      const elapsed = Date.now() - previewRemovedAt;
      // A site ends its hover preview the moment the pointer leaves the thumbnail for the butterfly.
      // While the pointer rests on the butterfly it stays, however long the user takes to click.
      const delay = pointerOnButterfly() ? PREVIEW_EXIT_GRACE
        : Math.min(PREVIEW_EXIT_GRACE, Math.max(0, PREVIEW_MAX_LATCH - elapsed));
      previewExitTimer = setTimeout(() => {
        previewExitTimer = 0;
        if (video !== player || !previewMissing()) { previewRemovedAt = 0; return; }
        if (panelMedia) return;
        const distance = butterflyDistance();
        const approaching = distance < previewPointerDistance - 6;
        previewPointerDistance = distance;
        if (pointerOnButterfly()) {
          holdMissingPreview();
          return;
        }
        // Near the butterfly, or still heading for it: wait, but not forever.
        if ((previewLatched() || approaching) && Date.now() - previewRemovedAt < PREVIEW_MAX_LATCH) {
          holdMissingPreview();
          return;
        }
        activePreview = null;
        detach();
        selectPlayer();
      }, delay);
    }
    return true;
  }

  // the quality picker

  let choice = null;
  function pickerOptionKey(option) {
    return JSON.stringify([option?.label || '', option?.detail || '', option?.container || '',
      option?.canClip === true, option?.clipUsesWholeFile === true]);
  }

  function restorePickerDraft(formats, targetKey, player) {
    const saved = retainedPickerDraft?.targetKey === targetKey
      && (!player || retainedPickerDraft.player === player) ? retainedPickerDraft : null;
    if (!saved) {
      if (retainedPickerDraft) retainedPickerDraft = null;
      return { choice: formats.choices[0], useOriginal: false, useM4a: true };
    }

    const availableTracks = formats.audioTracks || [];
    const availableSubtitles = formats.subtitleOptions || [];
    const audioTrack = availableTracks.some(track => track.id === saved.intent.audioTrack) ? saved.intent.audioTrack : null;
    const subtitles = saved.intent.subtitles.filter(language => availableSubtitles.some(track => track.language === language));
    const embedSubtitles = saved.intent.embedSubtitles && availableSubtitles.some(track =>
      track.embedSupported && subtitles.includes(track.language));
    formats.audioNeedsChoice = !!saved.intent.audioTrack && !audioTrack;
    formats.subtitlesNeedChoice = subtitles.length !== saved.intent.subtitles.length
      || saved.intent.embedSubtitles && !embedSubtitles;
    formats.intent = {
      audioTrack,
      subtitles,
      includeAutoSubtitles: saved.intent.includeAutoSubtitles && subtitles.some(language =>
        availableSubtitles.some(track => track.language === language && track.auto)),
      embedSubtitles
    };
    formats.trimDraft = { ...saved.trimDraft };

    const choices = [
      ...(saved.useOriginal ? formats.originalChoices || [] : formats.choices || []),
      ...formats.more || [],
      ...(formats.audioOutputs || []).map(entry => entry.choice),
      formats.audioM4a,
      ...formats.choices || [],
      ...formats.originalChoices || []
    ].filter(Boolean);
    const selected = choices.find(option => pickerOptionKey(option) === saved.choiceKey);
    if (!selected) {
      formats.selectionNeedsChoice = true;
      formats.selectionNotice = 'Your previous format is no longer available. Choose another before downloading.';
    }
    const selectedOriginal = selected && (formats.originalChoices || []).some(option => pickerOptionKey(option) === saved.choiceKey);
    const selectedAudio = selected && (formats.audioOutputs || []).find(entry => pickerOptionKey(entry.choice) === saved.choiceKey);
    if (selectedAudio) formats.selectedAudio = selectedAudio.choice.index;
    return {
      choice: selected || formats.choices[0],
      useOriginal: selectedOriginal || false,
      useM4a: saved.useM4a
    };
  }

  function dismissReceipt() {
    clearTimeout(receiptTimer);
    receiptTimer = 0;
    receipt?.classList.remove('show');
  }

  function closePanel(acknowledged = false) {
    const wasContextPicker = contextPicker;
    contextPicker = false;
    const invisibleTransition = video?.isConnected && pickerTransitionRect(video);
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
    if (invisibleTransition && button) button.hidden = true;
    updateFlyVisibility();
    holdMissingPreview();
    if ((wasContextPicker || activeBrowserCopy) && butterflyHidden()) queueMicrotask(() => {
      if (host && !panelMedia && !receiptTimer && butterflyHidden() && alive()) detach(true);
    });
  }

  async function quickDownload() {
    if (!alive() || butterflyHidden()) return;
    syncPage();
    if (panel.classList.contains('show')) closePanel();
    await loadFormats(false, true, true);
  }

  async function togglePanel() {
    if (!alive() || butterflyHidden()) return;
    syncPage();
    if (panel.classList.contains('show')) return closePanel();
    await loadFormats();
  }

  async function loadFormats(refresh = false, keepSource = true, quick = false) {
    syncPage();
    if ((!video?.isConnected && activePreview?.player !== video) || !panel) return;
    // A new click also renews the bounded script-response watcher after MV3
    // worker suspension. Existing evidence remains scoped to this document.
    announceVideoElement(true);
    dismissReceipt();
    receipt.textContent = '';
    const player = video;
    cancelPrefetch();
    // The first click can arrive before the observer has bound a loaded player.
    // Establish the current generation synchronously before opening the picker.
    if (player?.isConnected && player.readyState >= 1 && !activeBrowserCopy) consider(player);
    const request = ++panelRequest;
    // Reopening the butterfly must not start a second copy of the same Blob.
    if (activeBrowserCopy && activeBrowserCopy.player === player) {
      panelMedia = { ...activeBrowserCopy.panelMedia };
      panel.classList.add('show');
      button.setAttribute('aria-expanded', 'true');
      companion.setPickerOpen(true, false, true);
      updateFlyVisibility();
      return showBrowserCopy(activeBrowserCopy, request);
    }
    const preview = activePreview?.player === player ? activePreview : null;
    const ended = isCurrentEndedPlayer(player);
    const src = playerSource(player) || preview?.binding?.src || (ended ? endedPlayer.binding.src : '');
    const targetPage = activePreview?.player === player ? activePreview.page : pageForPlayer(player);
    const identity = VesperPlayerSource.identity(targetPage);
    const binding = playback.get(player) || preview?.binding || (ended ? endedPlayer.binding : null);
    const current = !previewMissing() && binding?.src === src && VesperPlayerSource.identity(binding.page) === identity;
    const targetKey = activePreview?.player === player ? activePreview.key
      : `${identity}|${targetPage}|${src}|${current ? binding.generation : 0}`;
    const savedDraft = retainedPickerDraft?.targetKey === targetKey && retainedPickerDraft.player === player;
    if (retainedPickerDraft && !savedDraft) retainedPickerDraft = null;
    refresh = refresh || savedDraft;
    panelMedia = { documentIdentity: VesperPlayerSource.identity(pageAddress()), documentPage: pageAddress(), targetKey,
      player, pageUrl: targetPage, src, formatSrc: current ? src : '', playerKey: evidenceKey(player), playerState: pickerPlayerState(player), generation: current ? binding.generation : 0 };
    panel.classList.add('show');
    button.setAttribute('aria-expanded', 'true');
    companion.setPickerOpen(true, false, true);
    updateFlyVisibility();
    const isPreview = !!preview;
    const onPlayerPage = current && !isPreview && identity === panelMedia.documentIdentity;
    const title = titleForPlayer(player, targetPage, onPlayerPage);
    // A feed's page-wide Open Graph image often belongs to the site rather than this player.
    const playerPageIsSpecific = identity && !/^https?:/.test(identity);
    const poster = current ? player.poster || (onPlayerPage && playerPageIsSpecific && document.querySelector('meta[property="og:image"]')?.content) || '' : '';
    const duration = current && Number.isFinite(player.duration) ? player.duration : null;
    const playerFacts = current ? {} : null;
    if (playerFacts) {
      if (Number.isFinite(player.duration) && player.duration > 0) playerFacts.durationSeconds = player.duration;
      if (Number.isFinite(player.videoHeight) && player.videoHeight > 0) playerFacts.height = player.videoHeight;
      if (player.tagName === 'AUDIO') playerFacts.kind = 'audio';
      if (player.duration === Infinity) playerFacts.live = true;
    }
    if (player.mediaKeys || protectedVideos.has(player))
      return renderShell(title, poster, duration, element('div', 'msg bad', 'This video is protected by DRM.'));

    const loading = element('div', 'msg', 'Loading formats…');
    loading.setAttribute('role', 'status');
    renderShell(title, poster, duration, loading);
    if (!isCurrentRequest(request)) return;
    const playerWitness = current ? playerFragmentWitness(player, binding) : null;
    const result = await send({ type: 'media-options', refresh, pageUrl: targetPage, multiplePlayers: hasOtherPlayer(player),
      mediaChangedAt: current ? binding.changedAt : mediaChangedAt,
      title, playerPlaying: current && !player.paused && !player.ended,
      playerHasMedia: current && (player.readyState >= 1 || player.ended || ended), playerPageUrl: targetPage,
      playerUrl: current ? src : '', playerKey: evidenceKey(player), generation: current ? binding.generation : 0,
      mutedLoop: current && !isPreview && isMutedLoop(player, src),
      playerCrossOrigin: current ? player.crossOrigin : undefined,
      playerFacts: playerFacts && Object.keys(playerFacts).length ? playerFacts : undefined,
      // Timing hints only reorder sources independently checked by the resolver.
      resourceUrls: current ? mediaLedger.matches(evidenceKey(player)).map(item => item.url).slice(0, 12) : undefined,
      fragmentUrls: current ? recentFragments(binding.changedAt) : undefined,
      playerFragmentUrls: playerWitness?.paths,
      playerResourceFingerprints: playerWitness?.fingerprints,
      manifestUrl: current && !VesperPlayerSource.http(src) ? declaredManifest(identity) : undefined });
    if (!isCurrentRequest(request)) return;
    if (result?.ok && result.data?.hasRelatedCollection
        && !result.data?.choices?.length && !result.data?.sourceChoices?.length) {
      const name = collectionName(result.data.collectionKind);
      const note = result.data.collectionIsDynamic
        ? `This is a changing ${name} snapshot. Choose its entries and one quality in VESPER.`
        : 'Choose the items and quality in VESPER. Nothing is added until you confirm.';
      const controls = collectionPickerControls(title, request, result.data);
      const footer = element('div', 'foot');
      footer.append(controls.browse);
      return renderShell(result.data.title || title, poster, duration,
        element('div', 'msg', note), controls.status, footer);
    }
    let formats = result?.data;
    const missingFormats = !result?.ok || (!formats?.choices?.length && !formats?.sourceChoices?.length);
    // Acquisition is a delivery recipe, not a second download UI. Only fall
    // back to the exact current player and never offer a speculative Blob URL.
    if ((missingFormats || formats?.browserChoice)
        && current && !activeBrowserCopy && !player.mediaKeys && !protectedVideos.has(player)
        && playerFacts?.live !== true) {
      let original = formats?.browserBodyAvailable === true ? formats.browserBodyFormat : null;
      if (!original && missingFormats && ['no-media', 'no-reusable-source', 'browser-context', 'unavailable',
          'site-refused', 'source-expired', 'source-unavailable', 'sign-in', 'unsupported'].includes(result?.code)) {
        original = await globalThis.VesperBrowserBody?.inspectLocalMedia(src, location.origin);
      }
      if (!isCurrentRequest(request)) return;
      if (original && playerSource(player) === src && playback.get(player) === binding
          && !player.mediaKeys && !protectedVideos.has(player)) {
        const audio = original.mime.startsWith('audio/');
        const height = !audio && player.videoHeight > 0 ? player.videoHeight + 'p' : '';
        formats = { browserBody: true, title: formats?.title || title, choices: [{
          index: 0, label: audio ? 'Original audio' : 'Original quality',
          detail: height || (audio ? 'Audio' : 'Video'), container: original.container,
          sizeBytes: original.sizeBytes, canClip: false
        }] };
      }
    }
    if (!formats?.browserBody && missingFormats) {
      const message = errorMessage(result, current && player.readyState >= 2);
      if (!['protected', 'blocked'].includes(result?.code))
        appendRetry(message, () => loadFormats(true), result?.data?.retryAt);
      return renderShell(title, poster, duration, message);
    }
    formats.playerLive = playerFacts?.live === true || formats.isLive === true;
    const pickerDuration = formats.playerLive ? null : formats.duration ?? duration;
    if (formats.sourceChoices?.length) {
      const savedSource = keepSource ? chosenSources.get(player) : null;
      if (savedSource?.targetKey === targetKey) {
        const matches = formats.sourceChoices.filter(source => source.fingerprint === savedSource.fingerprint);
        if (matches.length === 1) return inspectSource(matches[0], title, poster, pickerDuration, formats.playerLive, true);
      }
      return renderSourceChoices(title, poster, pickerDuration, formats);
    }
    const restored = restorePickerDraft(formats, targetKey, player);
    choice = restored.choice;
    if (quick && choice && !formats.sourceChoices?.length && !formats.selectionNeedsChoice
        && !formats.audioNeedsChoice && !formats.subtitlesNeedChoice) {
      return deliver(formats.title || title, formats.thumbnail || poster, pickerDuration,
        formats, choice.index, 'download', request, restored.intent);
    }
    renderChoices(formats.title || title, formats.thumbnail || poster, pickerDuration,
      formats, false, restored.useOriginal, restored.useM4a);
  }

  function showBrowserCopy(copy, request) {
    const status = element('div', 'msg', copy.status);
    status.setAttribute('role', 'status');
    copy.statusElement = status;
    copy.request = request;
    const footer = element('div', 'foot');
    const cancel = element('button', 'vesper-button', 'Cancel');
    cancel.onclick = () => {
      copy.controller.abort();
      cancel.disabled = true;
      status.textContent = 'Cancelling…';
    };
    footer.append(cancel);
    renderShell(copy.title, copy.poster, copy.duration, status, footer);
  }

  async function startBrowserCopy({ player, src, title, poster, duration }) {
    if (activeBrowserCopy || generatedCopy || !panelMedia || !isCurrentRequest(panelRequest)
        || player.mediaKeys || protectedVideos.has(player) || playerSource(player) !== src) return;
    const controller = new AbortController();
    const copy = { player, src, title, poster, duration, controller,
      panelMedia: { ...panelMedia }, status: 'Preparing download…' };
    activeBrowserCopy = copy;
    showBrowserCopy(copy, panelRequest);
    // The copy can outlive the picker. Navigation, extension retirement and Cancel stop it.
    let lastUpdate = 0;
    try {
      let mediaProof = null;
      if (!VesperBrowserBody.localBlob(src, location.origin)) {
        const binding = playback.get(player);
        if (!binding || binding.src !== src || binding.generation !== copy.panelMedia.generation)
          throw new Error('The selected player changed.');
        const reply = await send({ type: 'browser-body-media-proof', playerUrl: src,
          pageUrl: copy.panelMedia.pageUrl, playerPageUrl: copy.panelMedia.pageUrl,
          generation: binding.generation, mediaChangedAt: binding.changedAt, playerCrossOrigin: player.crossOrigin });
        if (!reply?.ok || controller.signal.aborted || playback.get(player) !== binding
            || !matchesPickerPlayer(copy.panelMedia))
          throw new Error('No verified browser file is available.');
        mediaProof = reply.data;
      }
      await VesperBrowserBody.copyBlob({ url: src, origin: location.origin, title, mediaProof, signal: controller.signal,
        onProgress(received, total) {
          if (player.mediaKeys || protectedVideos.has(player)) { controller.abort(); return; }
          const now = Date.now();
          if (received !== total && now - lastUpdate < 150) return;
          lastUpdate = now;
          copy.status = received === total ? 'Verifying download…'
            : 'Downloading · ' + Math.floor(received * 100 / total) + '%';
          if (copy.statusElement?.isConnected) copy.statusElement.textContent = copy.status;
        } });
      if (copy.request === panelRequest && isCurrentRequest(copy.request))
        await confirmSend('Sent to VESPER', copy.request);
    } catch (error) {
      if (copy.request === panelRequest && isCurrentRequest(copy.request)) {
        const uncertain = error?.code === 'uncertain-finish';
        const message = element('div', 'msg bad', uncertain
          ? 'Copy outcome is uncertain. Check the VESPER queue before attempting another copy.'
          : controller.signal.aborted ? 'Browser copy stopped. No completion was confirmed.'
          : 'Could not download this format.');
        if (!uncertain) appendRetry(message, () => loadFormats(true));
        renderShell(title, poster, duration, message);
      }
    } finally {
      if (activeBrowserCopy === copy) activeBrowserCopy = null;
    }
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

  function collectionName(kind) {
    return ({ playlist: 'playlist', dynamicmix: 'mix', album: 'album',
      channel: 'channel', feed: 'feed', shortformfeed: 'feed' })[kind] || 'collection';
  }

  function collectionPickerControls(title, request, formats) {
    const name = collectionName(formats.collectionKind);
    const actionLabel = `Browse ${name}`;
    const openedPage = panelMedia?.documentPage || pageAddress();
    const browse = element('button', 'vesper-button collection-browse', actionLabel);
    browse.title = actionLabel;
    browse.setAttribute('aria-label', actionLabel);
    const status = element('small', 'hint');
    status.setAttribute('role', 'status');
    status.hidden = true;
    browse.onclick = async () => {
      if (!browse.isConnected || !panelMedia || !isCurrentRequest(request) || browse.disabled) return;
      const selected = VesperPlayerSource.http(panelMedia.pageUrl);
      const collection = VesperPlayerSource.http(formats.collectionUrl);
      const frame = VesperPlayerSource.http(pageAddress());
      // Keep the resolver's collection URL when it belongs to this frame's origin.
      const pageUrl = collection && collection.origin === frame?.origin ? collection.href
        : selected && selected.origin === frame?.origin ? selected.href : null;
      if (!pageUrl || openedPage !== pageAddress()) {
        status.hidden = false;
        status.textContent = 'The page changed or the collection address is unavailable. Reopen the picker.';
        status.classList.add('bad');
        place(true);
        return;
      }
      browse.disabled = true;
      browse.title = 'Opening VESPER…';
      status.hidden = false;
      status.textContent = 'Choose checked entries and confirm in VESPER.';
      status.classList.remove('bad');
      place(true);
      try {
        const reply = await send({ type: 'native-picker', pageUrl, title });
        if (!browse.isConnected || !isCurrentRequest(request)) return;
        browse.disabled = false;
        browse.title = actionLabel;
        if (reply?.ok) {
          const results = reply.results || [];
          const accepted = results.filter(result => !result.subtitle && acceptedMediaResult(result)).length;
          const refused = results.filter(result => !acceptedMediaResult(result)).length;
          status.textContent = `${accepted} ${accepted === 1 ? 'item' : 'items'} added${refused ? ` · ${refused} couldn’t be added` : ''}.`;
        } else {
          status.textContent = errorMessage(reply).textContent;
          status.classList.toggle('bad', reply?.code !== 'cancelled');
        }
        place(true);
      } catch {
        if (!browse.isConnected || !isCurrentRequest(request)) return;
        browse.disabled = false;
        browse.title = actionLabel;
        status.textContent = `VESPER could not open this ${name}.`;
        status.classList.add('bad');
        place(true);
      }
    };
    return { browse, status };
  }

  function renderChoices(title, poster, seconds, formats, expanded = false, useOriginal = false, useM4a = true) {
    const request = panelRequest;
    const originalPresets = useOriginal ? formats.originalChoices : formats.choices;
    const selectedAudio = formats.audioOutputs?.find(entry => entry.choice.index === formats.selectedAudio)?.choice;
    const audioChoice = selectedAudio || (useM4a ? formats.audioM4a : null);
    const presets = audioChoice ? originalPresets.map(c => c.label === 'Audio only' ? audioChoice : c) : originalPresets;
    const intent = formats.intent ||= { audioTrack: null, subtitles: [], includeAutoSubtitles: false, embedSubtitles: false };
    const draft = formats.trimDraft ||= { enabled: false, start: '', end: '' };
    const visibleIndexes = new Set(presets.map(option => option.index));
    const moreFormats = (formats.more || []).filter(option => {
      if (visibleIndexes.has(option.index)) return false;
      visibleIndexes.add(option.index);
      return true;
    });
    const all = expanded ? [...presets, ...moreFormats] : presets;
    const output = element('label', 'output', 'Output');
    const mode = element('select');
    output.append(mode);
    const list = element('div', 'list');
    list.setAttribute('role', 'radiogroup');
    list.setAttribute('aria-label', 'Download format');
    const rows = [];
    const downloadButtons = [];
    let collectionControls = null;
    let selectionNotice = null;
    const noticeText = () => [formats.selectionNotice,
      formats.audioNeedsChoice && 'Your selected audio track is unavailable. Choose an audio track.',
      formats.subtitlesNeedChoice && 'Your subtitle selection changed. Review it before downloading.'
    ].filter(Boolean).join(' ');
    const needsChoice = () => formats.selectionNeedsChoice || formats.audioNeedsChoice || formats.subtitlesNeedChoice;
    function updateNotice() {
      if (!selectionNotice) return;
      const text = noticeText();
      selectionNotice.textContent = text;
      selectionNotice.hidden = !text;
    }
    function saveDraft() {
      if (!panelMedia?.targetKey || needsChoice()) return;
      retainedPickerDraft = {
        targetKey: panelMedia.targetKey,
        player: panelMedia.player,
        choiceKey: pickerOptionKey(choice),
        useOriginal,
        useM4a,
        intent: { ...intent, subtitles: [...intent.subtitles] },
        trimDraft: { ...draft }
      };
    }
    function updateOutput(index) {
      const isPreset = index >= 0 && index < presets.length;
      const audio = isPreset && choice.label === 'Audio only';
      output.hidden = !isPreset || !(audio ? formats.audioOutputs?.length > 1 || formats.audioM4a : formats.originalChoices?.length);
      if (output.hidden) return;
      const kind = audio ? 'audio' : 'video';
      if (mode.dataset.kind !== kind) {
        const labels = audio ? formats.audioOutputs?.map(entry => entry.label) || ['M4A (compatible)', 'Original audio'] : ['MP4 (compatible)', 'Original format'];
        mode.replaceChildren(...labels.map(name => element('option', '', name)));
        mode.dataset.kind = kind;
      }
      mode.selectedIndex = audio && formats.audioOutputs ? Math.max(0, formats.audioOutputs.findIndex(entry => entry.choice.index === choice.index)) : (audio ? !useM4a : useOriginal) ? 1 : 0;
    }
    function select(index, focus, userSelected = false) {
      if (index >= 0) choice = all[index];
      if (userSelected) {
        formats.selectionNeedsChoice = false;
        formats.selectionNotice = '';
        updateNotice();
      }
      if (pinned && presets.includes(choice)) { pinned.remove(); pinned = null; }
      for (const [i, option] of rows.entries()) {
        option.setAttribute('aria-checked', String(i === index));
        option.querySelector('.tick').textContent = i === index ? '✓' : '';
        option.tabIndex = i === index ? 0 : -1;
      }
      updateOutput(index);
      refreshClip();
      if (userSelected || !formats.selectionNeedsChoice) saveDraft();
      if (focus) rows[index].focus();
    }
    for (const c of all) {
      const option = element('button', 'opt');
      option.setAttribute('role', 'radio');
      const labels = element('span');
      const size = Number.isSafeInteger(c.sizeBytes) && c.sizeBytes > 0
        ? (c.sizeEstimated ? '≈ ' : '') + (c.sizeBytes >= 1073741824
          ? (c.sizeBytes / 1073741824).toFixed(2) + ' GiB'
          : (c.sizeBytes / 1048576).toFixed(1) + ' MiB') : '';
      labels.append(element('b', '', c.label),
        element('small', '', [c.detail, size].filter(Boolean).join(' · ')));
      const [icon, tone] = { 'Best quality': ['♕', 'best'], Balanced: ['▣', 'balanced'],
        Smallest: ['⌁', ''], 'Audio only': ['♫', 'audio'] }[c.label] || ['›', ''];
      const mark = element('span', 'icon ' + tone, icon);
      mark.setAttribute('aria-hidden', 'true');
      option.append(mark, labels);
      if (c.container) option.append(element('span', 'kind', c.container));
      option.append(element('span', 'tick'));
      const index = rows.length;
      option.onclick = () => {
        if (option.isConnected && isCurrentRequest(request)) select(index, false, true);
      };
      option.onkeydown = e => {
        const offset = { ArrowUp: -1, ArrowLeft: -1, ArrowDown: 1, ArrowRight: 1 }[e.key];
        if (!offset || !option.isConnected || !isCurrentRequest(request)) return;
        e.preventDefault();
        e.stopPropagation();
        select((index + offset + all.length) % all.length, true, true);
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
      saveDraft();
      panel.querySelector('.output select')?.focus({ preventScroll: true });
    };
    // A choice picked from the expanded list stays visible when the list collapses.
    let pinned = null;
    if (!expanded && moreFormats.includes(choice)) {
      pinned = element('button', 'opt pinned');
      pinned.setAttribute('role', 'radio');
      pinned.setAttribute('aria-checked', 'true');
      pinned.append(element('b', '', 'Selected: ' + choice.label));
      pinned.onclick = () => {
        if (isCurrentRequest(request)) renderChoices(title, poster, seconds, formats, true, useOriginal, useM4a);
      };
      pinned.onkeydown = event => {
        const direction = { ArrowUp: -1, ArrowLeft: -1, ArrowDown: 1, ArrowRight: 1 }[event.key];
        if (!direction || !pinned?.isConnected || !isCurrentRequest(request)) return;
        event.preventDefault(); event.stopPropagation();
        select(direction > 0 ? 0 : rows.length - 1, true, true);
      };
      list.prepend(pinned);
    }
    const clip = element('div', 'clip');
    const clipToggle = element('label');
    const clipBox = element('input');
    clipBox.type = 'checkbox';
    clipBox.checked = draft.enabled;
    clip.classList.toggle('expanded', clipBox.checked);
    clipToggle.append(clipBox, element('span', '', 'Trim'));
    const clipFields = element('div', 'fields');
    const clipError = element('div', 'clip-error');
    clipError.setAttribute('role', 'status');
    clipError.hidden = true;
    const hasDuration = formats.playerLive !== true && Number.isFinite(seconds)
      && seconds >= 0.1 && seconds <= 31_536_000;
    const startField = element('input'), endField = element('input');
    const timeFields = element('div', 'trim-values');
    for (const [field, key, label] of [[startField, 'start', 'Start'], [endField, 'end', 'End']]) {
      const row = element('label', 'trim-value');
      row.append(element('small', '', label));
      field.type = 'text';
      field.inputMode = 'numeric';
      field.value = clockText(parseClock(draft[key]) ?? (key === 'start' ? 0 : hasDuration ? seconds : 60));
      draft[key] = field.value;
      field.placeholder = key === 'start' ? '0:00' : hasDuration ? clockText(seconds) : '1:00';
      field.setAttribute('aria-label', label + ' time');
      field.oninput = () => { draft[key] = field.value; clipError.hidden = true; saveDraft(); };
      row.append(field);
      timeFields.append(row);
    }
    let rangeTrack = null, startSlider = null, endSlider = null;
    let selectedStart = parseClock(startField.value) ?? 0;
    let selectedEnd = parseClock(endField.value) ?? (hasDuration ? seconds : 60);
    const sliderMax = hasDuration ? Math.floor(seconds * 10) / 10 : 0;
    const timeStep = 0.1;
    const clampTime = value => Math.max(0, Math.min(sliderMax, Math.round(value * 10) / 10));

    if (hasDuration && sliderMax >= timeStep) {
      rangeTrack = element('div', 'trim-range');
      startSlider = element('input');
      endSlider = element('input');
      for (const [slider, label] of [[startSlider, 'Start'], [endSlider, 'End']]) {
        slider.type = 'range';
        slider.min = '0';
        slider.max = String(sliderMax);
        slider.step = String(timeStep);
        slider.setAttribute('aria-label', 'Trim ' + label.toLowerCase());
        rangeTrack.append(slider);
      }
      selectedStart = clampTime(selectedStart);
      selectedEnd = clampTime(selectedEnd);
      if (selectedEnd - selectedStart < timeStep) {
        selectedStart = 0;
        selectedEnd = sliderMax;
      }

      function syncRange(active, updateText) {
        const gap = Math.min(timeStep, sliderMax);
        if (active === 'start') selectedStart = clampTime(startSlider.valueAsNumber);
        if (active === 'end') selectedEnd = clampTime(endSlider.valueAsNumber);
        selectedStart = Math.min(selectedStart, Math.max(0, selectedEnd - gap));
        selectedEnd = Math.max(selectedEnd, Math.min(sliderMax, selectedStart + gap));
        startSlider.max = String(Math.max(0, selectedEnd - gap));
        endSlider.min = String(Math.min(sliderMax, selectedStart + gap));
        startSlider.value = String(selectedStart);
        endSlider.value = String(selectedEnd);
        rangeTrack.style.setProperty('--range-start', `${selectedStart / sliderMax * 100}%`);
        rangeTrack.style.setProperty('--range-end', `${selectedEnd / sliderMax * 100}%`);
        if (updateText) {
          startField.value = draft.start = clockText(selectedStart);
          endField.value = draft.end = clockText(selectedEnd);
        }
      }

      startSlider.oninput = () => { syncRange('start', true); clipError.hidden = true; saveDraft(); };
      endSlider.oninput = () => { syncRange('end', true); clipError.hidden = true; saveDraft(); };
      for (const [field, key] of [[startField, 'start'], [endField, 'end']]) {
        field.onchange = () => {
          const value = parseClock(field.value);
          if (value === null || value < 0 || value > seconds) return;
          const nextStart = key === 'start' ? value : parseClock(startField.value);
          const nextEnd = key === 'end' ? value : parseClock(endField.value);
          if (nextStart === null || nextEnd === null || nextEnd <= nextStart) return;
          selectedStart = clampTime(nextStart);
          selectedEnd = clampTime(nextEnd);
          if (selectedEnd <= selectedStart) return;
          syncRange('', true);
          clipError.hidden = true;
          saveDraft();
        };
      }
      syncRange('', true);
    }
    clipFields.append(timeFields);
    if (rangeTrack) clipFields.append(rangeTrack);
    const clipNote = element('small', 'hint');
    clipNote.className = 'clip-note';
    clipNote.setAttribute('role', 'status');
    clipBox.onchange = () => {
      draft.enabled = clipBox.checked;
      clip.classList.toggle('expanded', clipBox.checked);
      refreshClip();
      saveDraft();
      place(true);
    };
    clip.append(clipToggle, clipFields, clipNote, clipError);

    function refreshClip() {
      const support = clipAvailability(choice, seconds, formats.playerLive === true);
      const canClip = support.available;
      clip.hidden = false;
      clipBox.disabled = !canClip && !draft.enabled;
      clipFields.hidden = !canClip || !draft.enabled;
      clipNote.hidden = canClip && !draft.enabled;
      clipError.hidden = true;
      clipNote.textContent = support.reason;
      for (const action of downloadButtons)
        action.disabled = !!needsChoice() || draft.enabled && !canClip;
    }
    const body = [output, list];
    if (!formats.browserBody || draft.enabled) body.push(clip);
    if (noticeText()) {
      selectionNotice = element('div', 'msg', noticeText());
      body.unshift(selectionNotice);
    }
    if (formats.audioTracks?.length > 1 || formats.subtitleOptions?.length || formats.audioNeedsChoice || formats.subtitlesNeedChoice) {
      const advanced = element('details', 'languages');
      advanced.open = !!(formats.audioNeedsChoice || formats.subtitlesNeedChoice);
      const fields = element('div', 'language-fields');
      advanced.append(element('summary', 'vesper-button more', 'Language and subtitles'), fields);
      advanced.ontoggle = () => { if (advanced.isConnected) place(true); };
      if (formats.audioTracks?.length > 1 || formats.audioNeedsChoice) {
        const label = element('label', 'audio-track', 'Audio track'), tracks = element('select');
        const automatic = element('option', '', 'Original / automatic'); automatic.value = ''; tracks.append(automatic);
        for (const track of formats.audioTracks || []) { const option = element('option', '', track.label); option.value = track.id; tracks.append(option); }
        tracks.value = intent.audioTrack || '';
        if (formats.audioNeedsChoice) tracks.selectedIndex = -1;
        tracks.onchange = () => {
          intent.audioTrack = tracks.value || null;
          formats.audioNeedsChoice = false;
          updateNotice();
          refreshClip();
          saveDraft();
        };
        label.append(tracks); fields.append(label);
      }
      let embedSubtitleControl = null, embedLabel = null, embedHint = null;
      function updateEmbedAvailability() {
        const canEmbed = (formats.subtitleOptions || []).some(option =>
          option.embedSupported && intent.subtitles.includes(option.language));
        if (embedLabel) embedLabel.hidden = !canEmbed;
        if (embedHint) embedHint.hidden = !canEmbed;
        if (embedSubtitleControl) {
          embedSubtitleControl.disabled = !canEmbed;
          if (!canEmbed) {
            intent.embedSubtitles = false;
            embedSubtitleControl.checked = false;
          }
        }
      }
      for (const subtitle of formats.subtitleOptions || []) {
        const label = element('label', 'subtitle-choice'), check = element('input'); check.type = 'checkbox';
        check.checked = intent.subtitles.includes(subtitle.language);
        check.onchange = () => {
          intent.subtitles = check.checked ? [...new Set([...intent.subtitles, subtitle.language])] : intent.subtitles.filter(language => language !== subtitle.language);
          intent.includeAutoSubtitles = !!advanced.querySelector('input[data-auto=true]:checked');
          updateEmbedAvailability();
          saveDraft();
        };
        check.dataset.auto = String(subtitle.auto);
        label.append(check, element('span', '', subtitle.label + (subtitle.auto ? ' (automatic)' : '')));
        fields.append(label);
      }
      if ((formats.subtitleOptions || []).some(subtitle => subtitle.embedSupported)) {
        embedLabel = element('label', 'subtitle-choice');
        embedSubtitleControl = element('input');
        embedSubtitleControl.type = 'checkbox';
        embedSubtitleControl.checked = !!intent.embedSubtitles;
        embedSubtitleControl.onchange = () => { intent.embedSubtitles = embedSubtitleControl.checked; saveDraft(); };
        embedLabel.append(embedSubtitleControl, element('span', '', 'Add subtitles to video'));
        embedHint = element('small', 'hint', 'Adds a subtitle track inside the video. Separate subtitle files remain available.');
        fields.append(embedLabel, embedHint);
        updateEmbedAvailability();
      }
      if (formats.subtitlesNeedChoice) {
        const keep = element('button', 'vesper-button', intent.subtitles.length ? 'Confirm subtitles' : 'Continue without subtitles');
        keep.onclick = () => {
          if (!isCurrentRequest(request)) return;
          formats.subtitlesNeedChoice = false;
          keep.remove();
          updateNotice();
          refreshClip();
          saveDraft();
          (downloadButtons.find(action => !action.disabled) || panel.querySelector('.audio-track select') || list.querySelector('[tabindex="0"]'))?.focus({ preventScroll: true });
        };
        fields.append(keep);
      }
      body.push(advanced);
    }
    if (moreFormats.length) {
      const more = element('button', 'vesper-button more', expanded ? 'Fewer formats ▴' : 'More formats ▾');
      more.setAttribute('aria-expanded', String(expanded));
      more.onclick = () => {
        if (!isCurrentRequest(request)) return;
        renderChoices(title, poster, seconds, formats, !expanded, useOriginal, useM4a);
        panel.querySelector('button.more')?.focus({ preventScroll: true });
      };
      body.push(more);
    }
    if (formats.fromSourceChoice) {
      const sources = element('button', 'vesper-button', 'Sources');
      sources.onclick = () => { if (isCurrentRequest(request)) void loadFormats(true, false); };
      body.push(sources);
    }
    if (formats.hasRelatedCollection && !formats.browserChoice && !formats.browserBody) {
      collectionControls = collectionPickerControls(title, request, formats);
      body.push(collectionControls.status);
    }
    const footer = element('div', 'foot picker-foot');
    if (collectionControls) footer.append(collectionControls.browse);
    for (const mode of formats.browserChoice || formats.browserBody ? ['download'] : ['queue', 'download']) {
      const label = mode === 'download' ? 'Download' : 'Queue';
      const action = element('button', mode === 'download' ? 'vesper-button primary' : 'vesper-button', label);
      action.disabled = !!needsChoice() || draft.enabled && !clipAvailability(choice, seconds, formats.playerLive === true).available;
      downloadButtons.push(action);
      action.onclick = () => {
        if (!action.isConnected) return;
        let range;
        if (draft.enabled) {
          const support = clipAvailability(choice, seconds, formats.playerLive === true);
          const start = draft.start.trim() ? parseClock(draft.start) : 0;
          let end = draft.end.trim() ? parseClock(draft.end) : hasDuration ? seconds : null;
          if (hasDuration && end !== null && end <= seconds + 0.005) end = Math.min(end, seconds);
          if (!support.available || start === null || end === null || end <= start || end > seconds) {
            clipError.textContent = !support.available ? support.reason : 'Enter a valid start and end time within the media.';
            clipError.hidden = false;
            place(true);
            return;
          }
          range = { start, end };
        }
        void deliver(title, poster, seconds, formats, choice.index, mode, request, { ...intent, range });
      };
      footer.append(action);
    }
    select(pinned ? -1 : Math.max(0, all.indexOf(choice)), false);
    renderShell(title, poster, seconds, ...body, footer);
  }

  function renderSourceChoices(title, poster, seconds, formats) {
    const list = element('div', 'list');
    list.setAttribute('role', 'group');
    list.setAttribute('aria-label', 'Media sources');
    const matchesLength = source => Number.isFinite(seconds) && seconds > 0
      && Number.isFinite(source.durationSeconds) && Math.abs(source.durationSeconds - seconds) <= Math.max(1, seconds * 0.03);
    const hasMatch = formats.sourceChoices.some(matchesLength);
    const other = element('details', 'languages');
    other.append(element('summary', 'vesper-button more', 'Other detected sources'));
    const alternateList = element('div', 'list');
    other.append(alternateList);
    other.ontoggle = () => { if (other.isConnected) place(true); };
    for (const source of formats.sourceChoices) {
      const option = element('button', 'opt');
      const labels = element('span');
      labels.append(element('b', '', source.displayLabel || source.label),
        element('small', '', source.displayDetail || source.detail));
      option.append(element('span', 'icon balanced', '›'), labels);
      option.onclick = () => { if (option.isConnected) void inspectSource(source, title, poster, seconds, formats.playerLive === true); };
      // Duration can rank alternatives, but never establishes identity. Keep
      // other videos accessible instead of silently replacing the user's video.
      (hasMatch && !matchesLength(source) ? alternateList : list).append(option);
    }
    const prompt = formats.sourceChoices.length === 1 ? 'Choose the source to see formats.'
      : 'Choose the source that matches your player.';
    renderShell(title, poster, seconds, element('div', 'msg', prompt), list,
      ...(alternateList.childElementCount ? [other] : []));
  }

  async function inspectSource(source, title, poster, seconds, playerLive, remembered = false) {
    if (!panelMedia || !isCurrentRequest(panelRequest)) return;
    const request = panelRequest;
    const { player, targetKey } = panelMedia;
    const loading = element('div', 'msg', 'Checking selected stream…');
    loading.setAttribute('role', 'status');
    renderShell(title, poster, seconds, loading);
    const result = await send({ type: 'media-source-select', sourceChoice: source.token,
      pageUrl: panelMedia.pageUrl, generation: panelMedia.generation, playerKey: panelMedia.playerKey, playerUrl: panelMedia.formatSrc });
    if (!isCurrentRequest(request)) return;
    if (result?.ok && result.data?.choices?.length) {
      const formats = result.data;
      const fingerprint = /^[a-f0-9]{64}$/i.test(formats.deliveryFingerprint || '') ? formats.deliveryFingerprint : null;
      if (remembered && source.fingerprint !== fingerprint) {
        chosenSources.delete(player);
        return loadFormats(true, false);
      }
      const previous = chosenSources.get(player);
      if (!fingerprint || previous?.fingerprint !== fingerprint) retainedPickerDraft = null;
      if (fingerprint) chosenSources.set(player, { targetKey, fingerprint });
      else chosenSources.delete(player);
      formats.fromSourceChoice = true;
      formats.playerLive = playerLive || formats.isLive === true;
      const restored = restorePickerDraft(formats, targetKey, player);
      choice = restored.choice;
      return renderChoices(formats.title || title, formats.thumbnail || poster,
        formats.playerLive ? null : formats.duration ?? seconds, formats, false, restored.useOriginal, restored.useM4a);
    }
    if (remembered) chosenSources.delete(player);
    const message = result?.code === 'unavailable'
      ? element('div', 'msg bad', 'This source couldn’t be opened.')
      : errorMessage(result);
    appendRetry(message, () => loadFormats(true, false), result?.data?.retryAt, 'Back to sources');
    renderShell(title, poster, seconds, message);
  }

  const acceptedMediaResult = result => ['started', 'queued', 'already-submitted'].includes(result?.status);

  async function deliver(title, poster, seconds, formats, formatChoice, mode, request, intent = {}) {
    if (!isCurrentRequest(request)) return;
    if (formats.browserBody) {
      if (mode !== 'download' || intent.range || intent.audioTrack || intent.subtitles?.length) return;
      return startBrowserCopy({ player: panelMedia.player, src: panelMedia.src, title, poster, duration: seconds });
    }
    const sending = element('div', 'msg', formats.browserChoice ? 'Starting in Chrome…' : 'Sending to VESPER…');
    sending.setAttribute('role', 'status');
    const hadFocus = panel.contains(root.activeElement);
    const close = renderShell(title, poster, seconds, sending);
    if (hadFocus) close.focus({ preventScroll: true });
    const reply = await send(formats.browserChoice ? { type: 'browser-media-select', browserChoice: formats.browserChoice,
      pageUrl: panelMedia.pageUrl, generation: panelMedia.generation, playerUrl: panelMedia.src }
      : { type: 'media-select', mediaSession: formats.mediaSession, formatChoice, mode, ...intent,
        pageUrl: panelMedia.pageUrl, generation: panelMedia.generation, playerKey: panelMedia.playerKey, playerUrl: panelMedia.formatSrc });
    if (!isCurrentRequest(request)) return;
    if (reply?.ok) {
      if (formats.browserChoice) return confirmSend('Downloading in Chrome', request);
      const [main, ...subtitles] = reply.results || [];
      const failed = subtitles.filter(result => !acceptedMediaResult(result)).length;
      if (acceptedMediaResult(main)) {
        const status = main.status === 'queued' ? 'Queued in VESPER' :
          main.status === 'already-submitted' ? 'Already in VESPER' : 'Sent to VESPER';
        if (!failed) return confirmSend(status, request);
        const summary = element('div', 'msg', `${status}. ${failed} ${failed === 1 ? 'subtitle' : 'subtitles'} couldn’t be added.`);
        summary.setAttribute('role', 'status');
        const close = renderShell(title, poster, seconds, summary);
        if (hadFocus) close.focus({ preventScroll: true });
        return;
      }
      if (subtitles.some(acceptedMediaResult)) {
        const summary = element('div', 'msg', 'Media wasn’t added. Check VESPER for the accepted subtitles.');
        summary.setAttribute('role', 'status');
        const close = renderShell(title, poster, seconds, summary);
        if (hadFocus) close.focus({ preventScroll: true });
        return;
      }
    }
    const error = errorMessage(reply);
    const refresh = ['expired-selection', 'source-expired', 'source-changed'].includes(reply?.code);
    appendRetry(error, refresh ? () => loadFormats(true)
      : () => deliver(title, poster, seconds, formats, formatChoice, mode, request, intent), reply?.data?.retryAt);
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
    receiptTimer = setTimeout(() => {
      dismissReceipt();
      if (butterflyHidden() && !panelMedia) detach(true);
      else updateFlyVisibility();
    }, 1750);
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

  function errorMessage(reply, playbackReady = false) {
    const text = {
      'host-missing': 'Open VESPER → Settings → Browser to connect or repair the helper.',
      'app-unavailable': 'Could not connect to the desktop helper.',
      'host-error': 'The connection to VESPER failed.',
      'desktop-version-mismatch': 'Update VESPER and its browser helper together. This extension needs a newer desktop build.',
      duplicate: 'This link is already in VESPER.',
      cancelled: 'Cancelled in VESPER.',
      timeout: 'VESPER didn’t answer in time.',
      'no-media': 'VESPER couldn’t find a video to download. Play the video, then try again.',
      'no-reusable-source': 'This player’s video isn’t available as a file or stream VESPER can reuse.',
      'site-refused': reply?.message || 'The site refused the request. Try again from the video page.',
      unavailable: 'VESPER couldn’t open this video. Refresh the page and try again.',
      'source-expired': 'The video link expired. Try again to look for a fresh link.',
      'source-changed': 'The source no longer matches this media. Refresh the formats.',
      'expired-selection': 'Those choices expired. Try again to refresh them.',
      'sign-in': 'Sign in on this site, then try again.',
      'browser-context': playbackReady ? 'VESPER couldn’t verify a downloadable stream from this playback.'
        : 'Start playback in Chrome, then try again.',
      blocked: reply?.message || 'VESPER couldn’t access this source.',
      network: 'VESPER couldn’t reach the media source.',
      'resolver-error': 'VESPER couldn’t load the video choices. Try again in a moment.',
      invalid: reply?.message || 'The current media selection is invalid.',
      unsupported: reply?.message || 'This source can’t be downloaded.'
    }[reply?.code] || reply?.message || 'Something went wrong.';
    return element('div', 'msg bad', text);
  }

  function showContextNotice(reply) {
    clearTimeout(contextNoticeTimer);
    contextNotice?.remove();
    contextNotice = element('div');
    contextNotice.style.cssText = 'all:initial;position:fixed;right:18px;top:18px;z-index:2147483647;pointer-events:none';
    const root = contextNotice.attachShadow({ mode: 'closed' });
    const style = element('style');
    style.textContent = VesperTheme.css + `
      .notice{max-width:min(310px,calc(100vw - 36px));padding:11px 14px;border:1px solid var(--border);
        border-radius:8px;background:var(--surface);color:var(--text);font:13px/1.4 'Segoe UI',sans-serif}
    `;
    const notice = element('div', 'notice', errorMessage(reply).textContent);
    notice.setAttribute('role', 'status');
    root.append(style, notice);
    const fullscreen = document.fullscreenElement;
    (fullscreen?.tagName === 'DIV' ? fullscreen : document.documentElement).append(contextNotice);
    contextNotice.dataset.vesperTheme = host?.dataset.vesperTheme || 'system';
    contextNoticeTimer = setTimeout(() => { contextNotice?.remove(); contextNotice = null; }, 5000);
  }

  // The MAIN-world hook owns the short-lived object URL; only this isolated
  // script can start an automatic native copy. Never block the browser before
  // preferences are loaded or on a page with no current user activation.
  // Independently witness a real same-document gesture for delayed exports.
  // A page-authored event cannot mint or extend this one-shot intent.
  window.addEventListener('click', event => {
    if (!extensionPaused && event.isTrusted && navigator.userActivation?.isActive
        && event.composedPath().some(node => node instanceof Element && node.matches('button,a,input[type=button],input[type=submit],[role=button]')))
      generatedIntentAt = Date.now();
  }, { capture: true, signal });
  function onGeneratedDownload(event) {
    if (!alive() || extensionPaused
        || generatedCopy || activeBrowserCopy || chrome.extension?.inIncognitoContext
        || (!globalThis.navigator?.userActivation?.isActive
          && !(generatedIntentAt > 0 && Date.now() - generatedIntentAt <= 15_000)) || !event.cancelable
        || typeof event.detail !== 'string' || event.detail.length > 6000) return;
    let proposal;
    try { proposal = JSON.parse(event.detail); } catch { return; }
    const link = VesperBrowserBody.automaticGeneratedFile(proposal, location.origin);
    if (!link) return;
    const controller = new AbortController();
    const copy = { controller };
    generatedCopy = copy;
    generatedIntentAt = 0;
    event.preventDefault();
    const notify = message => {
      try { if (!signal.aborted) showContextNotice({ message }); } catch { /* UI is not transfer authority. */ }
    };
    void (async () => {
      let outcome = 'failed';
      try {
        await VesperBrowserBody.copyBlob({ url: link.url, origin: location.origin,
          generated: link, signal: controller.signal });
        outcome = 'accepted';
        notify('Browser file accepted by VESPER.');
      } catch (error) {
        if (error?.code === 'uncertain-finish') {
          outcome = 'uncertain';
          notify('Browser file outcome is uncertain. Check VESPER before retrying.');
        } else if (!signal.aborted) {
          // No native publication was attempted. Retry the retained immutable
          // object URL once through Chrome; do not recapture this alias.
          try {
            const anchor = document.createElement('a');
            anchor.href = link.url;
            anchor.download = link.fileName;
            anchor.click();
          } catch { notify('Browser file could not be saved. Try the original download again.'); }
        }
      } finally {
        if (generatedCopy === copy) generatedCopy = null;
        document.dispatchEvent(new CustomEvent('vesper-generated-download-result', {
          detail: JSON.stringify({ url: link.url, outcome })
        }));
      }
    })();
  }
  document.addEventListener('vesper-generated-download-request', onGeneratedDownload, { signal });

  // Explicit browser context-menu action; no interception of the site's own click.
  // The same finite-body route is used for files generated locally in the page.
  function copyGeneratedLink(url) {
    if (generatedCopy || activeBrowserCopy)
      return { ok: false, message: 'Another browser copy is active.' };
    const link = VesperBrowserBody.generatedLink(document.querySelectorAll('a[download],area[download]'), url, location.origin);
    if (!link) return { ok: false, message: 'The original download link is no longer available or has an ambiguous filename.' };
    const controller = new AbortController();
    const copy = { controller };
    generatedCopy = copy;
    let last = 0;
    showContextNotice({ message: 'Opening browser file…' });
    void VesperBrowserBody.copyBlob({ url: link.url, origin: location.origin, generated: link, signal: controller.signal,
      onProgress(received, total) {
        const now = Date.now();
        if (received !== total && now - last < 1000) return;
        last = now;
        showContextNotice({ message: received === total ? 'Checking and accepting browser file…'
          : 'Copying browser file · ' + Math.floor(received * 100 / total) + '%' });
      } }).then(() => {
      showContextNotice({ message: 'Browser file accepted by VESPER.' });
    }).catch(error => {
      if (!signal.aborted) showContextNotice({ message: error?.code === 'uncertain-finish'
        ? 'Browser copy could not be confirmed. Check VESPER before retrying.'
        : error?.message || 'VESPER couldn’t accept this browser file. Try the original download link.' });
    }).finally(() => { if (generatedCopy === copy) generatedCopy = null; });
    return { ok: true };
  }

  function applyMotion() {
    if (!alive()) return;
    host?.toggleAttribute('data-vesper-still', still());
    companion?.setStill(still());
    if (still()) panelMotion?.cancel();
  }

  function notePlayerGesture(event) {
    if (!event.isTrusted || event.composedPath().includes(host)) return null;
    const control = event.target.closest?.('button, [role="button"], input[type="range"], [role="slider"]');
    const player = observed.has(event.target) ? event.target : control && controlledPlayer(control);
    if (!player?.isConnected) return null;
    if (player.tagName === 'AUDIO' && !nativeAudioPlayer(player) && control) {
      if (audioControls.get(player) !== control) {
        audioControls.set(player, control);
        if (video === player) {
          resize.disconnect();
          resize.observe(control);
        }
      }
      consider(player);
    }
    const binding = playback.get(player);
    if (binding) playerInteraction = { id: evidenceKey(player), generation: binding.generation, at: Date.now() };
    if (!panelMedia) selectPlayer();
    return player;
  }

  // Keep an open picker when the user operates its player or the player's controls.
  document.addEventListener('pointerdown', (e) => {
    if (!alive()) return;
    const player = e.button === 0 ? notePlayerGesture(e) : null;
    if (!panel?.classList.contains('show') || e.composedPath().includes(host)) return;
    if (player && player === video) return;
    const rect = video?.isConnected ? playerAnchor(video)?.getBoundingClientRect() : null;
    if (rect && e.clientX >= rect.left && e.clientX <= rect.right && e.clientY >= rect.top && e.clientY <= rect.bottom) return;
    closePanel();
  }, { capture: true, passive: true, signal });
  document.addEventListener('click', e => {
    if (e.detail === 0 && alive()) notePlayerGesture(e);
  }, { capture: true, passive: true, signal });
  function openContextPicker(url) {
    syncPage();
    const current = video && (playerSource(video) || activePreview?.binding?.src);
    if (!panel || button?.hidden || current !== url) {
      if (!url) return false;
      const players = [...document.querySelectorAll('video,audio')]
        .filter(player => playerSource(player) === url && visiblePlayer(player));
      if (players.length !== 1) return false;
      attach(players[0]);
    }
    if (!panel || button?.hidden || (!video?.isConnected && activePreview?.player !== video)) return false;
    contextPicker = true;
    void loadFormats();
    return true;
  }
  chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    if (sender?.id !== chrome.runtime.id) return false;
    if (message?.type === 'vesper-context-notice' && alive()) {
      showContextNotice(message.reply);
      sendResponse({ ok: true });
      return false;
    }
    if (message?.type === 'vesper-copy-generated' && alive()) {
      sendResponse(copyGeneratedLink(message.url));
      return false;
    }
    if (message?.type === 'vesper-open-picker' && alive())
      sendResponse(openContextPicker(message.playerUrl) ? { ok: true } : { ok: false, code: 'no-player' });
    return false;
  });
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) cancelPrefetch();
    else { refreshPlayers(); schedulePrefetch(video); }
  }, { signal });
  window.addEventListener('pagehide', e => {
    cancelPrefetch();
    activeBrowserCopy?.controller.abort();
    generatedCopy?.controller.abort();
    if (e.persisted) companion?.setVisible(false); else retire();
  }, { signal });
  window.addEventListener('pageshow', refreshPlayers, { signal });
  // These remain installed when the overlay is temporarily absent. No polling.
  const geometryChanged = () => {
    if (observed.size) schedule();
    if (!scrollScanTimer) scrollScanTimer = setTimeout(() => { scrollScanTimer = 0; refreshPlayers(); }, 120);
  };
  window.addEventListener('scroll', geometryChanged, { passive: true, capture: true, signal });
  window.addEventListener('scrollend', refreshPlayers, { passive: true, capture: true, signal });
  window.addEventListener('resize', geometryChanged, { passive: true, signal });
  if (typeof MutationObserver !== 'undefined' && document.documentElement) {
    discoveryObserver = new MutationObserver(mediaTreeChanged);
    discoveryObserver.observe(document.documentElement, { childList: true, subtree: true,
      attributes: true, attributeFilter: ['src', 'href', 'controls', 'id', 'data-permalink'] });
  }
  unwatchMotion = VesperTheme.watchPreference('motion', 'animate', value => {
    motionMode = value;
    applyMotion();
  });
  const hiddenHostsKey = 'vesperHiddenButterflyHosts';
  void chrome.storage.local.get(hiddenHostsKey).catch(() => ({})).then(prefs => {
    if (!hiddenButterflyHosts && alive()) setHiddenButterflyHosts(prefs[hiddenHostsKey]);
  });
  unwatchPause = VesperBrowserPreferences.watchPause(({ paused }) => {
    if (!alive() || extensionPaused === paused) return;
    extensionPaused = paused;
    if (paused) {
      generatedIntentAt = 0;
      cancelPrefetch();
    }
    refreshPlayers();
  });
})();
