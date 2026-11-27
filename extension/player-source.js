'use strict';

globalThis.VesperPlayerSource = (() => {
  const TRACKING_PARAMS = new Set([
    'utm_source', 'utm_medium', 'utm_campaign', 'utm_term', 'utm_content', 'fbclid', 'gclid',
    'igshid', 'igsh', 'si', 'feature', 'pp', 'ref_src', 'ref_url', 'spm_id_from', 'vd_source',
    'is_from_webapp', 'sender_device', 'web_id'
  ]);

  function http(value) {
    if (typeof value !== 'string' || !value || value.length > 4096 || /[\r\n\0]/.test(value)) return null;
    try {
      const url = new URL(value);
      if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password) return null;
      url.hash = '';
      return url;
    } catch { return null; }
  }

  function normalizedPageHref(url) {
    const clean = new URL(url.href);
    for (const key of [...clean.searchParams.keys()]) {
      if (TRACKING_PARAMS.has(key.toLowerCase())) clean.searchParams.delete(key);
    }
    return clean.href;
  }

  function instagramPost(parts) {
    const kinds = ['p', 'reel', 'reels', 'tv'];
    const kindAt = kinds.includes(parts[0]) ? 0
      : parts[0] !== 'stories' && /^[a-z0-9_.]{1,30}$/i.test(parts[0] || '') && kinds.includes(parts[1]) ? 1 : -1;
    if (kindAt < 0 || parts.length !== kindAt + 2) return null;
    const id = parts[kindAt + 1];
    return id !== 'audio' && /^[a-z0-9_-]{1,128}$/i.test(id) ? { kind: parts[kindAt], id } : null;
  }

  const isFacebookHost = host => /(?:^|\.)(?:facebook\.com|fb\.watch)$/.test(host);

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
      const post = instagramPost(parts);
      if (post) {
        const index = url.searchParams.get('img_index');
        if (index !== null && !/^[1-9]\d{0,3}$/.test(index)) return url.href;
        return `instagram:${post.id}${index && index !== '1' ? ':' + index : ''}`;
      }
      if (parts[0] === 'stories' && /^[\w.-]+$/.test(parts[1] || '') && /^\d+$/.test(parts[2] || ''))
        return `instagram:story:${parts[1]}:${parts[2]}`;
    } else if (/(?:^|\.)(?:tiktok\.com|v[mt]\.tiktok\.com)$/.test(host)) {
      const id = parts[0]?.startsWith('@') && parts[1] === 'video' ? parts[2]
        : (parts[0] === 'embed' && parts[1] === 'v2' || parts[0] === 'player' && parts[1] === 'v1') ? parts[2]
        : parts[0] === 'video' ? parts[1] : null;
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
    } else if (isFacebookHost(host)) {
      if (host === 'fb.watch' && parts[0]) return `facebook:watch:${parts[0]}`;
      if (parts[0] === 'stories' && /^\d+$/.test(parts[1] || '')) {
        const item = parts[2] || url.searchParams.get('story_fbid');
        if (/^[a-z0-9_=-]{1,160}$/i.test(item || '')) return `facebook:story:${parts[1]}:${item}`;
      }
      const index = parts.findIndex(part => part === 'reel' || part === 'videos' || part === 'watch');
      const id = url.searchParams.get('v') || (index >= 0 ? parts[index + 1] : null);
      if (/^\d+$/.test(id || '')) return `facebook:${id}`;
      if (parts[0] === 'share' && (parts[1] === 'r' || parts[1] === 'v') && parts[2])
        return `facebook:share:${parts[2]}`;
    } else if (/(?:^|\.)reanime\.[a-z]+$/.test(host)) {
      const ep = parts.findIndex(p => p === 'watch' || p === 'episode');
      if (ep >= 0 && parts[ep + 1])
        return `reanime:${parts.slice(ep + 1).join(':')}`;
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
    return normalizedPageHref(url);
  }

  // The page of the one post a feed card shows, from the card's own same-origin links. A site identity
  // (a watch page, a status, a reel) names it directly. On other sites a card names its post through a
  // timestamp link or an explicit permalink attribute. Two different posts in one card name neither.
  function cardPage(links, origin) {
    const known = new Map();
    const permalinks = new Map();
    for (const link of Array.isArray(links) ? links.slice(0, 32) : []) {
      const url = http(link?.href);
      if (!url || url.origin !== origin) continue;
      const name = identity(url.href);
      if (name && !/^https?:/.test(name)) known.set(name, url.href);
      else if (link.permalink === true && url.pathname.length > 1) permalinks.set(url.href, url.href);
    }
    if (known.size === 1) return known.values().next().value;
    if (known.size === 2) {
      const post = [...known.keys()].find(key => /^x:\d{5,24}$/.test(key));
      if (post) for (const [key, href] of known)
        if (key.startsWith(post + ':video:')) return href;
    }
    if (known.size === 0 && permalinks.size === 1) return permalinks.values().next().value;
    return null;
  }

  function kind(url, mime = '') {
    if (/\.mpd(?:[?#]|$)/i.test(url) || /dash\+xml/i.test(mime)) return 'dash';
    if (/\.m3u8(?:[?#]|$)/i.test(url) || /mpegurl/i.test(mime)) return 'hls';
    return 'direct';
  }

  function isSocialFeedSite(pageUrl) {
    const host = http(pageUrl)?.hostname || '';
    return /(?:^|\.)(?:instagram|tiktok)\.com$/i.test(host) || isFacebookHost(host);
  }

  return { http, identity, cardPage, kind, isSocialFeedSite, instagramPost, isFacebookHost };
})();
