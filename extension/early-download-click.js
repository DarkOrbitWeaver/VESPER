// Intercept only the default action of a real, ordinary file-link click.
// No global browser UI changes, no synthetic-click trapping, no polling.
(() => {
  'use strict';
  if (chrome.extension?.inIncognitoContext) return;
  let paused = true, pendingUrl = null, intentAt = 0;
  VesperBrowserPreferences.watchPause(state => {
    paused = state.paused;
    if (paused) intentAt = 0;
  });
  const canIntercept = () => !paused && !!chrome.runtime.id;
  window.addEventListener('click', event => {
    if (canIntercept() && event.isTrusted && navigator.userActivation?.isActive
        && event.composedPath().some(node => node?.matches?.('button,a,input[type=button],input[type=submit],[role=button]')))
      intentAt = Date.now();
  }, true);

  window.addEventListener('click', event => {
    if (!canIntercept() || document.querySelector('base[target]')) return;
    const chosen = VesperEarlyDownload.clickCandidate(event, location.href);
    if (!chosen) return;
    const {anchor, url} = chosen;
    if (pendingUrl) {
      if (pendingUrl === url) event.preventDefault();
      return;
    }
    const original = document.createElement('a');
    original.href = url;
    for (const name of ['download', 'referrerpolicy', 'rel', 'target'])
      if (anchor.hasAttribute(name)) original.setAttribute(name, anchor.getAttribute(name));
    event.preventDefault();
    start(url, original);
  }, { capture: true });

  document.addEventListener('vesper-early-download-request', event => {
    // Page-authored events are only proposals. Independently verify a recent,
    // one-shot trusted gesture and a supported download link.
    if (!canIntercept() || pendingUrl || !event.cancelable || !intentAt || Date.now() - intentAt > 15_000
        || typeof event.detail !== 'string' || event.detail.length > 6000) return;
    let proposal;
    try { proposal = JSON.parse(event.detail); } catch { return; }
    if (!proposal || typeof proposal.downloadName !== 'string' || !proposal.downloadName) return;
    const url = VesperEarlyDownload.candidate(proposal.url, location.href, proposal.downloadName);
    if (!url) return;
    const original = document.createElement('a');
    original.href = url;
    original.download = proposal.downloadName;
    event.preventDefault();
    start(url, original);
  });

  function start(url, original) {
    pendingUrl = url;
    intentAt = 0;
    void (async () => {
      try {
        const omitReferrer = original.rel?.split(/\s+/).includes('noreferrer')
          || original.getAttribute('referrerpolicy')?.toLowerCase() === 'no-referrer';
        const reply = await chrome.runtime.sendMessage({type:'early-download', url,
          downloadName:original.getAttribute('download'), ...(omitReferrer ? { omitReferrer: true } : {})});
        if (reply?.ok) return;
        if (reply?.fallback === true) {
          original.click();
          return;
        }
        // A lost reply may mean native accepted. Never silently start a second copy.
        announce(reply?.message || 'VESPER could not confirm this download. Check VESPER before retrying.');
      } catch {
        announce('VESPER could not confirm this download. Check VESPER before retrying.');
      } finally { pendingUrl = null; }
    })();
  }
  function announce(message) {
    const node = document.createElement('div');
    node.setAttribute('role','status');
    node.textContent = message;
    node.style.cssText='position:fixed;top:16px;right:16px;z-index:2147483647;max-width:320px;padding:12px;background:#202b23;color:white;border:1px solid #536c57;border-radius:8px;font:13px/1.4 Segoe UI,sans-serif';
    document.documentElement.append(node);
    setTimeout(() => node.remove(),5000);
  }
})();
