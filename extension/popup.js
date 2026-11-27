'use strict';

VesperTheme.install();
const $ = id => document.getElementById(id);
const status = $('settings-status');
function settingsStatus(message = '') {
  status.textContent = message;
  status.hidden = !message;
}

$('copy-diagnostics').onclick = async () => {
  try {
    const { vesperFailureLog: entries = [] } = await chrome.storage.local.get('vesperFailureLog');
    if (!entries.length) return settingsStatus('No browser failures recorded yet.');
    const lines = entries.map(({ exported, ...entry }) => JSON.stringify({ ...entry, utc: new Date(entry.utc).toISOString() }));
    await navigator.clipboard.writeText(lines.join('\n'));
    settingsStatus('Failure log copied.');
  } catch {
    settingsStatus('Could not copy the failure log.');
  }
};

const pause = $('pause-vesper');
let savingPause = false;
let pauseState = { ready: false, paused: true };
function showPause() {
  pause.checked = pauseState.paused;
  pause.disabled = savingPause || !pauseState.ready;
  $('header-sub').textContent = pauseState.paused ? 'Paused' : 'Active';
}
VesperBrowserPreferences.watchPause(state => { pauseState = state; showPause(); });
pause.onchange = async () => {
  const paused = pause.checked;
  savingPause = pause.disabled = true;
  settingsStatus();
  try {
    await chrome.storage.local.set({ vesperPaused: paused });
    pauseState = await VesperBrowserPreferences.readPause();
    if (!pauseState.ready) settingsStatus('Couldn’t load settings. Reopen this popup.');
  } catch {
    settingsStatus('Couldn’t save this setting.');
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

  async function readHiddenHosts() {
    const { vesperHiddenButterflyHosts: hosts } = await chrome.storage.local.get('vesperHiddenButterflyHosts');
    return new Set(Array.isArray(hosts) ? hosts.filter(host => typeof host === 'string').map(host => host.toLowerCase()).slice(0, 100) : []);
  }

  const [siteTab] = await chrome.tabs.query({ active: true, currentWindow: true }).catch(() => []);
  let butterflyHost = '';
  try {
    const site = new URL(siteTab?.url || '');
    if (site.protocol === 'http:' || site.protocol === 'https:')
      butterflyHost = site.hostname.toLowerCase();
  } catch {}

  const siteButterfly = $('site-butterfly');
  let hiddenHosts = await readHiddenHosts();
  function showSiteButterfly() {
    const full = hiddenHosts.size >= 100 && !hiddenHosts.has(butterflyHost);
    siteButterfly.disabled = !butterflyHost || full;
    siteButterfly.textContent = hiddenHosts.has(butterflyHost) ? 'Show butterfly on this site' : 'Hide butterfly on this site';
    $('site-butterfly-host').textContent = butterflyHost;
  }
  showSiteButterfly();

  for (const key of ['theme', 'motion']) {
    const control = $(key);
    control.onchange = async () => {
      control.disabled = true;
      settingsStatus();
      try {
        await chrome.storage.sync.set({ [key]: control.value });
        prefs[key] = control.value;
      } catch {
        control.value = prefs[key];
        settingsStatus('Couldn’t save this setting.');
      } finally { control.disabled = false; }
    };
  }

  siteButterfly.onclick = async () => {
    if (!butterflyHost || (hiddenHosts.size >= 100 && !hiddenHosts.has(butterflyHost))) return;
    siteButterfly.disabled = true;
    settingsStatus();
    const hide = !hiddenHosts.has(butterflyHost);
    try {
      const current = await readHiddenHosts();
      if (hide && !current.has(butterflyHost) && current.size >= 100) { hiddenHosts = current; return; }
      if (hide) current.add(butterflyHost);
      else current.delete(butterflyHost);
      await chrome.storage.local.set({ vesperHiddenButterflyHosts: [...current] });
      hiddenHosts = current;
    } catch {
      settingsStatus('Couldn’t save this setting.');
    } finally { showSiteButterfly(); }
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
void init().catch(() => settingsStatus('Couldn’t load settings. Reopen this popup.'));
