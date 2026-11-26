'use strict';

VesperTheme.install();
const $ = id => document.getElementById(id);

const pause = $('pause-vesper');
let savingPause = false;
let pauseState = { ready: false, paused: true };
function showPause() {
  pause.checked = pauseState.paused;
  pause.disabled = savingPause || !pauseState.ready;
}
VesperBrowserPreferences.watchPause(state => { pauseState = state; showPause(); });
pause.onchange = async () => {
  const paused = pause.checked;
  savingPause = pause.disabled = true;
  try {
    await chrome.storage.local.set({ vesperPaused: paused }).catch(() => {});
    pauseState = await VesperBrowserPreferences.readPause();
  } finally {
    savingPause = false;
    showPause();
  }
};

async function init() {
  const manifest = chrome.runtime.getManifest();
  $('version').textContent = `Extension ${manifest.version_name || manifest.version}`;
  const prefs = await chrome.storage.sync.get({ theme: 'system', motion: 'animate', useSignIn: true });
  $('theme').value = prefs.theme;
  $('motion').value = prefs.motion === 'still' ? 'still' : 'animate';
  const { vesperHiddenButterflyHosts = [] } =
    await chrome.storage.local.get('vesperHiddenButterflyHosts').catch(() => ({}));
  const [siteTab] = await chrome.tabs.query({ active: true, currentWindow: true }).catch(() => []);
  let butterflyHost = '';
  try {
    const site = new URL(siteTab?.url || '');
    if (site.protocol === 'http:' || site.protocol === 'https:') butterflyHost = site.hostname.toLowerCase();
  } catch { /* Browser pages have no site butterfly. */ }
  const siteButterfly = $('site-butterfly');
  const hiddenHosts = new Set(Array.isArray(vesperHiddenButterflyHosts)
    ? vesperHiddenButterflyHosts.filter(host => typeof host === 'string').map(host => host.toLowerCase()).slice(0, 100) : []);
  function showSiteButterfly() {
    const full = hiddenHosts.size >= 100 && !hiddenHosts.has(butterflyHost);
    siteButterfly.disabled = !butterflyHost || full;
    siteButterfly.title = full ? 'Show the butterfly on another hidden site before adding one.' : '';
    siteButterfly.textContent = hiddenHosts.has(butterflyHost) ? 'Show butterfly on this site' : 'Hide butterfly on this site';
    $('site-butterfly-host').textContent = butterflyHost;
  }
  showSiteButterfly();
  $('theme').onchange = () => chrome.storage.sync.set({ theme: $('theme').value });
  $('motion').onchange = () => chrome.storage.sync.set({ motion: $('motion').value });
  siteButterfly.onclick = async () => {
    if (!butterflyHost || (hiddenHosts.size >= 100 && !hiddenHosts.has(butterflyHost))) return;
    siteButterfly.disabled = true;
    if (hiddenHosts.has(butterflyHost)) hiddenHosts.delete(butterflyHost);
    else hiddenHosts.add(butterflyHost);
    try {
      await chrome.storage.local.set({ vesperHiddenButterflyHosts: [...hiddenHosts].slice(0, 100) });
    } catch {
      if (hiddenHosts.has(butterflyHost)) hiddenHosts.delete(butterflyHost);
      else hiddenHosts.add(butterflyHost);
    }
    showSiteButterfly();
  };
  const signIn = $('signin');
  const access = $('signin-access');
  const permit = $('signin-permission');
  const cookies = { permissions: ['cookies'] };
  const hasAccess = () => chrome.permissions.contains(cookies).catch(() => false);
  let cookiePermission = false;
  function showAccess(allowed) {
    cookiePermission = allowed;
    signIn.checked = prefs.useSignIn;
    permit.hidden = allowed || !signIn.checked;
    access.textContent = '';
  }
  chrome.permissions.onRemoved.addListener(removed => {
    if (removed.permissions?.includes('cookies')) showAccess(false);
  });
  async function allowSignIn() {
    permit.disabled = true;
    try {
      if (!await chrome.permissions.request(cookies)) return false;
      await chrome.storage.sync.set({ useSignIn: true });
      prefs.useSignIn = true;
      return true;
    } catch {
      return false;
    } finally {
      showAccess(await hasAccess());
      permit.disabled = false;
    }
  }
  permit.onclick = async () => {
    if (!await allowSignIn()) access.textContent = 'Chrome sign-in access was not enabled.';
  };
  signIn.onchange = async () => {
    signIn.disabled = true;
    try {
      if (signIn.checked && !cookiePermission) {
        if (!await allowSignIn()) access.textContent = 'Chrome sign-in access was not enabled.';
      } else {
        await chrome.storage.sync.set({ useSignIn: signIn.checked });
        prefs.useSignIn = signIn.checked;
        showAccess(await hasAccess());
      }
    } catch {
      showAccess(await hasAccess());
      access.textContent = 'Couldn’t save this setting.';
    } finally {
      signIn.disabled = false;
    }
  };
  showAccess(await hasAccess());
  signIn.disabled = false;
}
void init();
