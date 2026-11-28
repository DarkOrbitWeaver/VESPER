'use strict';

// Each strategy keeps its own outcomes. Lookup measures verified formats, while takeover measures completed files.
// Evidence decays over time and can only restrict acquisition, never override its safety checks.
const VesperExperience = (() => {
  const KEY = 'vesperExperience';
  const MAX_ENTRIES = 256;
  const HALF_LIFE_MS = 7 * 24 * 60 * 60_000;
  // One refusal can be an expired click; two with no verified file since are a pattern. A pair of refusals
  // fades below this line after about three days, which is when VESPER asks the site again.
  const STAND_DOWN = 1.5;
  // Caps how long repeated refusals can keep VESPER away: about seventeen days at the limit.
  const MAX_WEIGHT = 8;
  let state;
  let writes = Promise.resolve();

  const hex = bytes => Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('');
  const fresh = () => ({ salt: crypto.randomUUID(), entries: {}, receipts: {} });

  function load() {
    state ??= chrome.storage.local.get(KEY).then(saved => {
      const value = saved?.[KEY];
      if (typeof value?.salt !== 'string' || value.salt.length < 8 || value.salt.length > 64
          || !value.entries || typeof value.entries !== 'object') return fresh();
      const entries = {};
      for (const [key, entry] of Object.entries(value.entries).slice(0, MAX_ENTRIES))
        if (/^[0-9a-f]{24}$/.test(key) && Array.isArray(entry) && entry.length === 3
            && entry.every(Number.isFinite) && entry[0] >= 0 && entry[0] <= MAX_WEIGHT
            && entry[1] >= 0 && entry[1] <= MAX_WEIGHT && entry[2] >= 0 && entry[2] <= Date.now() + 2000)
          entries[key] = entry;
      const receipts = {};
      for (const [key, at] of Object.entries(value.receipts || {}).slice(0, MAX_ENTRIES))
        if (/^[0-9a-f]{24}$/.test(key) && Number.isFinite(at) && at >= Date.now() - 86400_000
            && at <= Date.now() + 2000) receipts[key] = at;
      return { salt: value.salt, entries, receipts };
    }).catch(fresh);
    return state;
  }

  async function keyFor(memory, site, strategy) {
    if (typeof site !== 'string' || !site || site.length > 253 || typeof strategy !== 'string' || !strategy) return null;
    const bytes = new TextEncoder().encode(`${memory.salt}\n${strategy}\n${site.toLowerCase()}`);
    return hex(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)).subarray(0, 12));
  }

  // An entry stores positive outcomes, refusals, and the time of the last update.
  function faded(entry, now) {
    const factor = now > entry[2] ? 0.5 ** ((now - entry[2]) / HALF_LIFE_MS) : 1;
    return [entry[0] * factor, entry[1] * factor];
  }

  function save(memory) {
    const keys = Object.keys(memory.entries);
    if (keys.length > MAX_ENTRIES) {
      keys.sort((a, b) => memory.entries[a][2] - memory.entries[b][2]);
      for (const key of keys.slice(0, keys.length - MAX_ENTRIES)) delete memory.entries[key];
    }
    const cutoff = Date.now() - 86400_000;
    for (const key of Object.keys(memory.receipts)) if (memory.receipts[key] < cutoff) delete memory.receipts[key];
    const receipts = Object.keys(memory.receipts).sort((a, b) => memory.receipts[a] - memory.receipts[b]);
    for (const key of receipts.slice(0, Math.max(0, receipts.length - MAX_ENTRIES))) delete memory.receipts[key];
    writes = writes.catch(() => {}).then(() => chrome.storage.local.set({ [KEY]: memory }));
    return writes;
  }

  async function standing(site, strategy, now = Date.now()) {
    const loaded = load();
    const memory = await loaded;
    // Skip hashing when there is no saved evidence.
    if (Object.keys(memory.entries).length === 0) return { verified: 0, refused: 0 };
    const key = await keyFor(memory, site, strategy);
    const entry = state === loaded && key && memory.entries[key];
    if (!entry) return { verified: 0, refused: 0 };
    const [verified, refused] = faded(entry, now);
    return { verified, refused };
  }

  // Cold start answers yes: with no evidence VESPER behaves exactly as it would without this memory.
  async function promising(site, strategy, now = Date.now()) {
    const { verified, refused } = await standing(site, strategy, now);
    return refused < STAND_DOWN || verified >= refused;
  }

  async function note(site, strategy, outcome, now = Date.now(), caseKey = null) {
    if (outcome !== 'verified' && outcome !== 'refused') return;
    const loaded = load();
    const memory = await loaded;
    const key = await keyFor(memory, site, strategy);
    const receipt = caseKey === null ? null : await keyFor(memory, caseKey, 'receipt:' + key + ':' + outcome);
    if (!key || state !== loaded || caseKey !== null && !receipt) return;
    // Reopening the same picker or replaying a reply is not independent experience.
    // One refusal and one subsequent success can be learned for a generation.
    if (receipt && memory.receipts[receipt] >= now - 86400_000) return;
    if (receipt) memory.receipts[receipt] = now;
    const [verified, refused] = memory.entries[key] ? faded(memory.entries[key], now) : [0, 0];
    // A positive outcome clears refusal history for this strategy.
    memory.entries[key] = outcome === 'verified'
      ? [Math.min(MAX_WEIGHT, verified + 1), 0, now]
      : [verified, Math.min(MAX_WEIGHT, refused + 1), now];
    await save(memory);
  }

  async function clear() {
    state = Promise.resolve(fresh());
    writes = writes.catch(() => {}).then(() => chrome.storage.local.remove(KEY));
    await writes;
  }

  async function score(site, strategy) {
    const { verified, refused } = await standing(site, strategy);
    const alpha = verified + 1, beta = refused + 1;
    const total = alpha + beta;
    return alpha / total + Math.sqrt(alpha * beta / (total * total * (total + 1)));
  }

  return { note, promising, standing, score, clear };
})();
