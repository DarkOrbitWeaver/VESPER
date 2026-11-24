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
      if (['p', 'reel', 'reels', 'tv'].includes(parts[0]) && /^[\w-]+$/.test(parts[1] || '')) {
        const index = url.searchParams.get('img_index');
        if (index !== null && !/^[1-9]\d{0,3}$/.test(index)) return url.href;
        return `instagram:${parts[1]}${index && index !== '1' ? ':' + index : ''}`;
      }
    } else if (/(?:^|\.)tiktok\.com$/.test(host)) {
      const id = parts[0]?.startsWith('@') && parts[1] === 'video' ? parts[2]
        : (parts[0] === 'embed' && parts[1] === 'v2' || parts[0] === 'player' && parts[1] === 'v1') ? parts[2] : null;
      if (/^\d+$/.test(id || '')) return `tiktok:${id}`;
    } else if (host === 'dai.ly' || /(?:^|\.)dailymotion\.com$/.test(host)) {
      const video = parts.indexOf('video');
      const id = host === 'dai.ly' ? parts[0] : url.searchParams.get('video')
        || (video >= 0 ? parts[video + 1]?.split('_')[0] : null);
      if (/^[a-z0-9]{5,12}$/i.test(id || '')) return `dailymotion:${id}`;
    } else if (['x.com', 'www.x.com', 'twitter.com', 'www.twitter.com', 'mobile.twitter.com'].includes(host)) {
      // Timeline/profile URLs are not video identities. Keep explicit media
      // indexes separate; a multi-video post must not merge two different clips.
      const status = parts[0] === 'i' && parts[1] === 'web' ? 2 : 1;
      const id = parts[status] === 'status' ? parts[status + 1] : null;
      if (/^\d{5,24}$/.test(id || '')) {
        const rest = parts.slice(status + 2);
        if (!rest.length) return 'x:' + id;
        if (rest.length === 2 && rest[0] === 'video' && /^[1-9]\d?$/.test(rest[1]))
          return 'x:' + id + ':video:' + rest[1];
      }
    } else if (/(?:^|\.)facebook\.com$/.test(host)) {
      const index = parts.findIndex(part => part === 'reel' || part === 'videos');
      const id = url.searchParams.get('v') || (index >= 0 ? parts[index + 1] : null);
      if (/^\d+$/.test(id || '')) return `facebook:${id}`;
    } else if (host === 'redd.it' || host === 'v.redd.it') {
      const id = parts[0];
      if (/^[a-z0-9]+$/i.test(id || '')) return host === 'v.redd.it' ? `reddit-video:${id}` : `reddit:${id}`;
    } else if (/(?:^|\.)reddit\.com$/.test(host)) {
      const comments = parts.indexOf('comments');
      const id = comments >= 0 ? parts[comments + 1] : null;
      if (/^[a-z0-9]+$/i.test(id || '')) return `reddit:${id}`;
    } else if (/(?:^|\.)vimeo\.com$/.test(host)) {
      const idIndex = host === 'player.vimeo.com' && parts[0] === 'video' ? 1
        : parts.findIndex(part => part.length >= 3 && /^\d+$/.test(part));
      const id = parts[idIndex];
      if (/^\d+$/.test(id || '') && (host !== 'player.vimeo.com' || idIndex === 1)) {
        const pathHash = host === 'player.vimeo.com' ? null : parts[idIndex + 1];
        const queryHash = url.searchParams.get('h');
        const hash = pathHash && /^[0-9a-f]{8,12}$/i.test(pathHash) ? pathHash
          : queryHash && /^[0-9a-f]{8,12}$/i.test(queryHash) ? queryHash : null;
        if (queryHash && !/^[0-9a-f]{8,12}$/i.test(queryHash)) return url.href;
        return `vimeo:${id}${hash ? ':h:' + hash : ''}`;
      }
    } else if (/(?:^|\.)bilibili\.com$/.test(host)) {
      const video = parts.indexOf('video');
      const id = video >= 0 ? parts[video + 1] : null;
      if (/^BV[A-Za-z0-9]{10}$/.test(id || '')) {
        const page = url.searchParams.get('p');
        if (page !== null && !/^[1-9]\d{0,4}$/.test(page)) return url.href;
        return `bilibili:${id}${page && page !== '1' ? ':p:' + page : ''}`;
      }
      if (/^av\d+$/i.test(id || '')) {
        const page = url.searchParams.get('p');
        if (page !== null && !/^[1-9]\d{0,4}$/.test(page)) return url.href;
        return `bilibili:${id.toLowerCase()}${page && page !== '1' ? ':p:' + page : ''}`;
      }
    } else {
      const peertubeId = parts.length === 2 && parts[0] === 'w' ? parts[1]
        : parts.length === 3 && parts[0] === 'videos' && ['watch', 'embed'].includes(parts[1]) ? parts[2] : null;
      if (/^[a-z0-9_-]{6,40}$/i.test(peertubeId || '')) return `peertube:${url.origin}:${peertubeId}`;
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
