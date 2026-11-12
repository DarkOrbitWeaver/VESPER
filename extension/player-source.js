'use strict';

globalThis.VesperPlayerSource = (() => {
  function http(value) {
    if (typeof value !== 'string' || !value || value.length > 4096 || /[\r\n\0]/.test(value)) return null;
    try {
      const url = new URL(value);
      if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password) return null;
      url.hash = '';
      return url;
    } catch { return null; }
  }

  function identity(value) {
    const url = http(value);
    if (!url) return null;
    const host = url.hostname;
    const parts = url.pathname.split('/').filter(Boolean);
    if (/(?:^|\.)(?:youtube\.com|youtube-nocookie\.com|youtu\.be)$/.test(host)) {
      const id = host === 'youtu.be' ? parts[0] : parts[0] === 'watch' ? url.searchParams.get('v')
        : ['shorts', 'embed', 'live'].includes(parts[0]) ? parts[1] : null;
      if (/^[\w-]{11}$/.test(id || '')) return `youtube:${id}`;
    } else if (/(?:^|\.)instagram\.com$/.test(host)) {
      if (['p', 'reel', 'reels', 'tv'].includes(parts[0]) && /^[\w-]+$/.test(parts[1] || '')) return `instagram:${parts[1]}`;
    } else if (/(?:^|\.)tiktok\.com$/.test(host)) {
      const id = parts[0]?.startsWith('@') && parts[1] === 'video' ? parts[2]
        : parts[0] === 'embed' && parts[1] === 'v2' ? parts[2] : null;
      if (/^\d+$/.test(id || '')) return `tiktok:${id}`;
    } else if (/(?:^|\.)facebook\.com$/.test(host)) {
      const index = parts.findIndex(part => part === 'reel' || part === 'videos');
      const id = url.searchParams.get('v') || (index >= 0 ? parts[index + 1] : null);
      if (/^\d+$/.test(id || '')) return `facebook:${id}`;
    }
    return url.href;
  }

  function kind(url, mime = '') {
    if (/\.mpd(?:[?#]|$)/i.test(url) || /dash\+xml/i.test(mime)) return 'dash';
    if (/\.m3u8(?:[?#]|$)/i.test(url) || /mpegurl/i.test(mime)) return 'hls';
    return 'direct';
  }

  return { http, identity, kind };
})();
