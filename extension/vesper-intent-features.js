// Shared feature extraction for the offline-trained download-intent advisor.
// The runtime never learns from browsing history or stores page text.
'use strict';
globalThis.VesperIntentFeatures = (() => {
  const SIZE = 512;
  const START = 24;
  const FILE = /\.(?:zip|7z|rar|tar|gz|pdf|epub|docx?|xlsx?|pptx?|exe|msi|msix|apk|iso|dmg|mp4|webm|mkv|mov|mp3|m4a|flac|ogg|wav|png|jpe?g|webp|csv|json)$/i;
  function hash(text) {
    let value = 2166136261;
    for (const ch of text) { value ^= ch.codePointAt(0); value = Math.imul(value, 16777619); }
    return START + (value >>> 0) % (SIZE - START);
  }
  function normalize(value) {
    return String(value || '').normalize('NFKD').replace(/\p{M}/gu, '')
      .toLowerCase().replace(/[^\p{L}\p{N}]+/gu, ' ').trim().slice(0, 160);
  }
  function vector(facts = {}) {
    const result = new Map([[0, 1]]);
    const bit = (index, value) => { if (value) result.set(index, 1); };
    bit(1, facts.downloadAttribute);
    bit(2, facts.tagName === 'BUTTON' || facts.role === 'button');
    bit(3, facts.tagName === 'A' || !!facts.href);
    bit(4, facts.isNavigation || facts.role === 'tab');
    bit(5, facts.isDisabled);
    bit(6, facts.isInlinePdf);
    bit(7, facts.isBackground);
    bit(8, facts.hasDownloadUrl);
    bit(9, facts.stableBinding);
    bit(10, facts.isFormSubmit);
    bit(11, facts.sameOrigin);
    bit(12, facts.targetBlank);
    bit(13, facts.role === 'tab');
    bit(14, facts.isSelectedItem);
    try {
      const url = new URL(facts.href);
      bit(15, FILE.test(url.pathname));
      bit(16, /(?:^|\/)(?:download|export|attachment|files?)(?:\/|$)/i.test(url.pathname));
      bit(17, [...url.searchParams.keys()].some(key => /^(?:download|attachment|export)$/i.test(key)));
      bit(18, /\.(?:m4s|cmfv|cmfa|ts|aac|m3u8|mpd)$/i.test(url.pathname));
    } catch { /* A button can have no link. */ }
    const label = normalize([facts.label, facts.ariaLabel, facts.title].filter(Boolean).join(' '));
    bit(19, !label);
    bit(20, /\d{3,4}p\b/.test(label));
    bit(21, /\d+\s*(?:mb|gb|kb)\b/.test(label));
    const grams = new Set();
    const words = label.split(' ').filter(Boolean).slice(0, 24);
    for (const word of words) {
      grams.add('word:' + word);
      const letters = Array.from('^' + word + '$');
      for (let n = 2; n <= 4; n++)
        for (let i = 0; i <= letters.length - n; i++) grams.add(n + ':' + letters.slice(i, i + n).join(''));
    }
    // Scale the lexical contribution by the number of unique grams: long SEO
    // labels cannot overwhelm the structural evidence by repeating words.
    const scale = grams.size ? 3 / Math.sqrt(grams.size) : 0;
    for (const gram of grams) {
      const index = hash(gram);
      result.set(index, Math.min(1.5, (result.get(index) || 0) + scale));
    }
    return result;
  }
  // Fixed-size relationship features. They contain no raw text, URL or DOM
  // references and are used only to rank controls for the review UI.
  function relationshipVector(facts = {}) {
    const intent = Number.isFinite(facts.intent) ? Math.max(0, Math.min(1, facts.intent)) : 0;
    return [intent, ...['explicitMatch','queryMatch','panelMatch','pathMatch','sameGroup',
      'active','hasUrl','structuralDownload','contradiction','differentPanel','blocked']
      .map(key => facts[key] ? 1 : 0)];
  }
  return Object.freeze({ SIZE, vector, relationshipVector });
})();
