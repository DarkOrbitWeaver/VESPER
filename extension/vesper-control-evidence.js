// Offline evidence fusion for download controls and item-to-action relationships.
//
// Each observed fact contributes a documented log-odds weight. The sum is converted to a probability
// and to one of three verdicts, with explicit abstention in between. Destructive or commercial
// labels are hard vetoes, never weights that a strong label could outvote.
//
// This replaces the hashed n-gram logistic model (350 synthetic samples) and the 12-weight relationship
// model (60 synthetic pairs). Those learned the generator's own rules back and could not be audited;
// these weights are the same knowledge stated directly, with multilingual coverage they never had.
// A score here never authorizes a click: execution still requires explicit user approval.
'use strict';
globalThis.VesperControlEvidence = (() => {
  const PRIOR = -1.0;                       // most clickable elements on a page are not downloads
  const DOWNLOAD_AT = 1.0;
  const NAVIGATION_AT = -1.5;
  const MARGIN = 1.0;                       // a suggestion must beat the runner-up by this many log-odds units

  // Word-level lexicon (lower-case, accents removed). Positive: the control yields a file.
  const POSITIVE = new Map(Object.entries({
    download: 2.6, downloads: 2.0, save: 1.2, export: 2.0, get: 0.4, obtain: 0.8, mirror: 0.8, direct: 0.4,
    herunterladen: 2.6, speichern: 1.0, telecharger: 2.6, enregistrer: 1.0, descargar: 2.6, guardar: 1.0,
    baixar: 2.6, transferir: 1.4, scarica: 2.6, scaricare: 2.6, salva: 1.0, downloaden: 2.6, opslaan: 1.0,
    pobierz: 2.6, pobieranie: 2.0, zapisz: 1.0, stahnout: 2.6, stiahnut: 2.6, letoltes: 2.6, indir: 2.6,
    unduh: 2.6, muat: 0.8, tai: 0.6, descarca: 2.6, lataa: 2.0, ladda: 1.4, hent: 1.4, skachat: 2.6,
    скачать: 2.6, загрузить: 1.8, скачивание: 2.2, завантажити: 2.6, изтегли: 2.6, preuzmi: 2.6,
    κατεβασμα: 2.6, ληψη: 2.0, הורדה: 2.6, הורד: 2.6, تحميل: 2.6, تنزيل: 2.6, دانلود: 2.6, بارگیری: 2.2,
    indirme: 2.2, डाउनलोड: 2.6, ডাউনলোড: 2.6, ดาวน์โหลด: 2.6
  }));
  // Phrases for scripts without word separators, matched as substrings.
  const POSITIVE_PHRASES = [['下载', 2.6], ['下載', 2.6], ['ダウンロード', 2.6], ['다운로드', 2.6], ['保存', 1.0], ['저장', 1.0],
    ['tải xuống', 2.6], ['ladda ner', 2.6], ['last ned', 2.6], ['save as', 1.4]];
  // Negative: the control plays, navigates or reveals something instead of producing a file.
  const NEGATIVE = new Map(Object.entries({
    play: -2.0, watch: -1.8, preview: -1.8, view: -1.6, open: -1.0, read: -1.6, stream: -1.2, listen: -1.6,
    next: -1.6, previous: -1.6, prev: -1.6, more: -0.8, details: -1.4, expand: -0.8, share: -0.6, like: -1.2,
    comment: -1.4, reply: -1.4, follow: -1.2, menu: -1.2, settings: -1.4, filter: -1.0, sort: -1.0,
    login: -2.4, signin: -2.4, register: -2.0, lecture: -1.6, voir: -1.6, ansehen: -1.6, abspielen: -2.0,
    reproducir: -2.0, ver: -1.4, assistir: -1.8, guarda: -0.6, odtworz: -2.0, смотреть: -1.8, اشاهدة: -1.8
  }));
  // Hard vetoes: never suggest, never treat as an approved workflow action.
  const VETO = new Set(['delete', 'remove', 'logout', 'signout', 'unsubscribe', 'purchase', 'buy', 'checkout', 'pay',
    'payment', 'order', 'subscribe', 'donate', 'report', 'block', 'upgrade', 'premium', 'trial', 'install', 'cart',
    'loschen', 'entfernen', 'kaufen', 'abonnieren', 'supprimer', 'acheter', 'eliminar', 'comprar', 'suscribirse',
    'excluir', 'assinar', 'elimina', 'acquista', 'usun', 'kup', 'удалить', 'купить', 'подписаться', 'حذف', 'شراء', 'اشتراك']);
  const VETO_PHRASES = ['log out', 'sign out', 'sign in', 'add to cart', 'free trial', '删除', '购买', '購入', '削除', '삭제', '구매'];
  // ASCII \b does not see Cyrillic or CJK letters, so those forms are matched without it.
  const COUNTDOWN = /(?:\(\s*\d{1,3}\s*(?:s|sec|secs|seconds?)?\s*\)|\b\d{1,3}\s*(?:s|sec|secs|seconds?|sek|seg|segundos?|secondes?|sekunden?)\b|\d{1,3}\s*секунд[а-я]*|\d{1,3}\s*(?:秒|초)|\bin\s+(?=\d{1,3}\b)|\bwait\b|\bplease wait\b|\bbitte warten\b|\bveuillez patienter\b|\bespere\b|подождите)/i;
  const FILE_PATH = /\.(?:zip|7z|rar|tar|gz|bz2|xz|zst|tgz|pdf|epub|mobi|docx?|xlsx?|pptx?|odt|csv|txt|exe|msi|msix|apk|iso|img|dmg|pkg|deb|rpm|mp4|m4v|webm|mkv|mov|avi|mp3|m4a|flac|ogg|opus|wav|aac|png|jpe?g|gif|webp|srt|vtt|ass|torrent|bin|jar)$/i;
  const SEGMENT_PATH = /\.(?:m4s|cmfv|cmfa|ts|aac|m3u8|mpd)$/i;

  const normalize = value => String(value ?? '').normalize('NFKD').replace(/\p{M}/gu, '').toLowerCase()
    .replace(/\s+/g, ' ').trim().slice(0, 200);
  const words = text => text.split(/[^\p{L}\p{N}]+/u).filter(Boolean).slice(0, 40);
  const sigmoid = value => 1 / (1 + Math.exp(-value));

  function labelOf(facts) {
    return normalize([facts.label, facts.ariaLabel, facts.title].filter(Boolean).join(' '));
  }

  function vetoed(label) {
    const tokens = words(label);
    return tokens.some(token => VETO.has(token)) || VETO_PHRASES.some(phrase => label.includes(phrase));
  }

  function lexical(label, reasons) {
    let best = 0, worst = 0;
    for (const token of words(label)) {
      const positive = POSITIVE.get(token) || 0, negative = NEGATIVE.get(token) || 0;
      if (positive > best) best = positive;
      if (negative < worst) worst = negative;
    }
    for (const [phrase, weight] of POSITIVE_PHRASES) if (weight > best && label.includes(phrase)) best = weight;
    // The strongest positive and strongest negative word each count once, so repeating "download"
    // in a long promotional label cannot outweigh structural evidence.
    if (best) reasons.push(`label +${best}`);
    if (worst) reasons.push(`label ${worst}`);
    return best + worst;
  }

  function hrefFacts(href) {
    try {
      const url = new URL(href);
      return { file: FILE_PATH.test(url.pathname), segment: SEGMENT_PATH.test(url.pathname),
        downloadPath: /(?:^|\/)(?:download|downloads|dl|export|attachment|get|files?)(?:\/|$)/i.test(url.pathname)
          || [...url.searchParams.keys()].some(key => /^(?:download|dl|attachment|export)$/i.test(key)) };
    } catch { return { file: false, segment: false, downloadPath: false }; }
  }

  // Probability that activating this control produces a file. Readiness (disabled, countdown) is
  // reported separately: a disabled Download button is still the right button to wait for.
  function assess(facts = {}) {
    const label = labelOf(facts);
    const reasons = [];
    // A bare disabled Save control is usually a form action, not a file.
    const ambiguousSave = label === 'save' && facts.isDisabled && !facts.downloadAttribute && !facts.hasDownloadUrl;
    if (vetoed(label)) return { verdict: 'blocked', logOdds: -Infinity, probability: 0, reasons: ['vetoed label'],
      countdown: false, ready: false };
    let logOdds = PRIOR + lexical(label, reasons);
    const add = (condition, weight, reason) => { if (condition) { logOdds += weight; reasons.push(`${reason} ${weight > 0 ? '+' : ''}${weight}`); } };
    const href = facts.href ? hrefFacts(facts.href) : { file: false, segment: false, downloadPath: false };
    add(facts.downloadAttribute, 3.0, 'download attribute');
    add(facts.hasDownloadUrl, 2.0, 'download data attribute');
    add(href.file, 1.5, 'file-like address');
    add(href.downloadPath, 1.0, 'download path');
    add(href.segment, -4.0, 'media segment');
    add(facts.role === 'button' || facts.tagName === 'BUTTON', 0.3, 'button');
    add(facts.isNavigation, -2.5, 'navigation region');
    add(facts.role === 'tab', -2.5, 'tab');
    add(facts.isInlinePdf, -1.5, 'inline document');
    add(facts.isBackground, -4.0, 'background');
    add(ambiguousSave, -2.0, 'disabled generic save');
    add(/\b\d+(?:[.,]\d+)?\s*(?:kb|mb|gb|kib|mib|gib)\b/.test(label), 0.8, 'size in label');
    add(/\b\d{3,4}p\b/.test(label), 0.5, 'quality in label');
    add(facts.targetBlank, -0.3, 'opens a new tab');
    add(facts.stableBinding, 0.4, 'stable item binding');
    const countdown = COUNTDOWN.test(label);
    const verdict = logOdds >= DOWNLOAD_AT ? 'download' : logOdds <= NAVIGATION_AT ? 'navigation' : 'uncertain';
    return { verdict, logOdds, probability: sigmoid(logOdds), reasons, countdown,
      ready: !facts.isDisabled && !countdown };
  }

  // Log-odds that a shared action belongs to the currently selected item. Contradictions are vetoes.
  function relation(facts = {}) {
    if (facts.contradiction || facts.differentPanel || facts.blocked) return { logOdds: -Infinity, vetoed: true, reasons: ['contradiction'] };
    let logOdds = -0.5;
    const reasons = [];
    const add = (condition, weight, reason) => { if (condition) { logOdds += weight; reasons.push(`${reason} +${weight}`); } };
    add(facts.explicitMatch, 4.0, 'item id on action');
    add(facts.queryMatch, 3.5, 'item id in action address');
    add(facts.panelMatch, 3.0, 'inside the item panel');
    add(facts.pathMatch, 2.5, 'item id in action path');
    add(facts.sameGroup, 1.2, 'same container');
    add(facts.active, 0.5, 'item is selected');
    add(facts.structuralDownload, 0.8, 'structural download control');
    return { logOdds, vetoed: false, reasons };
  }

  // Ranks candidates by score and abstains unless the best is decisive.
  function decide(candidates, minimum = DOWNLOAD_AT) {
    const ranked = candidates.filter(entry => Number.isFinite(entry.score)).sort((a, b) => b.score - a.score);
    const [best, runnerUp] = ranked;
    if (!best || best.score < minimum || runnerUp && best.score - runnerUp.score < MARGIN) return null;
    return best;
  }

  // Removes recognised countdown fragments so "Download (5)" and "Download" are the same action.
  function stableLabel(value) {
    return normalize(value).replace(new RegExp(COUNTDOWN.source, 'gi'), ' ').replace(/\s+/g, ' ').trim();
  }

  return Object.freeze({ assess, relation, decide, stableLabel, normalize, vetoed: value => vetoed(normalize(value)),
    DOWNLOAD_AT, NAVIGATION_AT, MARGIN });
})();
