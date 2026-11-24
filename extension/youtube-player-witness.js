// Runs in YouTube's MAIN world so VESPER can read page-created player methods.
// This is an untrusted page hint, not authorization for a native download.
(() => {
  'use strict';
  if (globalThis.__vesperYouTubeWitness) return;
  globalThis.__vesperYouTubeWitness = true;
  const ATTRIBUTE = 'data-vesper-youtube-video-id';
  const VIDEO_ID = /^[A-Za-z0-9_-]{11}$/;

  // Only inspect the exact element the isolated content script requested.
  // No global video scan, timers, cookies, network hooks or playback logging.
  document.addEventListener('vesper-youtube-identity-request', event => {
    const video = event.target;
    if (video?.tagName !== 'VIDEO') return;
    video.removeAttribute(ATTRIBUTE); // A reused player cannot keep yesterday's ID.
    if (video.readyState < 1) return;
    const player = video.closest?.('.html5-video-player');
    if (!player || typeof player.getVideoData !== 'function' || !player.contains(video)) return;
    try {
      const id = player.getVideoData()?.video_id;
      if (VIDEO_ID.test(id || '')) video.setAttribute(ATTRIBUTE, id);
    } catch { /* YouTube can replace the player while navigating. */ }
  }, true);
})();
