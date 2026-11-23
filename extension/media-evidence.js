'use strict';

globalThis.VesperMediaEvidence = (() => {
  const MAX_DURATION_SECONDS = 31_536_000;
  const MAX_HEIGHT = 16_384;

  function positiveNumber(value, max) {
    return Number.isFinite(value) && value > 0 && value <= max ? value : undefined;
  }

  function mediaFacts(reply) {
    const data = reply?.data || {};
    const best = data.choices?.[0] || {};
    const detail = [best.detail, best.label].filter(value => typeof value === 'string').join(' ');
    const height = [data.height, data.videoHeight, best.height, best.videoHeight]
      .map(value => Number.isInteger(value) ? positiveNumber(value, MAX_HEIGHT) : undefined).find(value => value !== undefined)
      || detailHeight(detail);
    const hasAudio = trackFlag(data, best, 'audio', detailAudio(detail), /\baudio not confirmed\b/i.test(detail));
    const hasVideo = trackFlag(data, best, 'video', detailVideo(detail));

    return {
      durationSeconds: positiveNumber(data.duration, MAX_DURATION_SECONDS),
      height,
      live: typeof data.isLive === 'boolean' ? data.isLive : undefined,
      hasAudio,
      hasVideo
    };
  }

  function detailHeight(detail) {
    const match = /(?:^|\D)(\d{2,5})p(?:\d{2,3})?\b/i.exec(detail);
    if (!match) return undefined;
    const height = Number(match[1]);
    return height >= 16 && height <= MAX_HEIGHT ? height : undefined;
  }

  function trackFlag(data, choice, kind, hint, unverified = false) {
    const property = kind === 'audio' ? 'hasAudio' : 'hasVideo';
    for (const value of [choice[property], data[property]]) {
      if (typeof value === 'boolean') return value;
    }

    const listName = kind === 'audio' ? 'audioTracks' : 'videoTracks';
    for (const value of [choice[listName], data[listName]]) {
      if (Array.isArray(value) && value.length > 0) return true;
    }

    const tracks = [choice.tracks, data.tracks].filter(Array.isArray).flat();
    if (tracks.some(track => track?.kind === kind)) return true;
    return unverified || hint !== undefined ? hint : undefined;
  }

  function detailAudio(detail) {
    if (/\baudio only\b/i.test(detail)) return true;
    if (/\bno audio\b/i.test(detail)) return false;
    if (/\bwith audio\b|\+ audio\b/i.test(detail)) return true;
    return undefined;
  }

  function detailVideo(detail) {
    if (/\baudio only\b|\bno video\b/i.test(detail)) return false;
    if (/\bvideo only\b|\bwith audio\b|\+ audio\b/i.test(detail)) return true;
    return undefined;
  }

  function playerFacts(value) {
    if (!value || typeof value !== 'object') return {};
    const facts = {};
    const durationSeconds = positiveNumber(value.durationSeconds, MAX_DURATION_SECONDS);
    const height = positiveNumber(value.height, MAX_HEIGHT);
    if (durationSeconds !== undefined) facts.durationSeconds = durationSeconds;
    if (Number.isInteger(height)) facts.height = height;
    if (typeof value.live === 'boolean') facts.live = value.live;
    return facts;
  }

  function score(player, verified) {
    let result = 0;
    if (player.live === true && verified.live === true) result += 5;
    else if (player.live === true && verified.live === false) result -= 1;

    if (player.durationSeconds && verified.durationSeconds) {
      const tolerance = Math.max(1, player.durationSeconds * 0.03);
      result += Math.abs(player.durationSeconds - verified.durationSeconds) <= tolerance ? 5 : -2;
    }

    if (player.height && verified.height) {
      const difference = Math.abs(player.height - verified.height);
      if (difference <= Math.max(16, player.height * 0.03)) result += 3;
      else result -= 1;
    }

    if (player.height && verified.hasVideo === false) result -= 3;
    else if (player.height && verified.hasVideo === true) result += 1;
    return result;
  }

  function trackLabel(facts) {
    if (facts.hasVideo === true && facts.hasAudio === true) return 'audio + video';
    if (facts.hasVideo === true && facts.hasAudio === false) return 'video only';
    if (facts.hasVideo === false && facts.hasAudio === true) return 'audio only';
    return '';
  }

  return { mediaFacts, playerFacts, score, trackLabel };
})();
