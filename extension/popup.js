'use strict';

VesperTheme.install();
const $ = id => document.getElementById(id);

async function init() {
  const manifest = chrome.runtime.getManifest();
  $('version').textContent = `Extension ${manifest.version_name || manifest.version}`;
  const prefs = await chrome.storage.sync.get({ theme: 'system', motion: 'animate', route: 'auto', handoff: 'download', intercept: true, useSignIn: true });
  $('theme').value = prefs.theme;
  $('motion').value = prefs.motion === 'still' ? 'still' : 'animate';
  $('handoff').value = prefs.handoff === 'queue' ? 'queue' : 'download';
  const browserDownloads = $('browser-downloads');
  browserDownloads.value = prefs.route === 'chrome' ? 'chrome' : prefs.intercept ? 'automatic' : 'manual';
  function showDownloadMode() {
    $('manual-downloads').hidden = browserDownloads.value !== 'manual';
    $('handoff-setting').hidden = browserDownloads.value === 'chrome';
  }
  showDownloadMode();
  const { prefetchFormats = false } = await chrome.storage.local.get('prefetchFormats').catch(() => ({}));
  $('prefetch').checked = prefetchFormats;
  // Say what capture does the first time, in plain words.
  const { vesperSeen } = await chrome.storage.local.get('vesperSeen').catch(() => ({}));
  $('first-run').hidden = !!vesperSeen;
  if (!vesperSeen) chrome.storage.local.set({ vesperSeen: true }).catch(() => {});

  $('theme').onchange = () => chrome.storage.sync.set({ theme: $('theme').value });
  $('motion').onchange = () => chrome.storage.sync.set({ motion: $('motion').value });
  $('handoff').onchange = () => chrome.storage.sync.set({ handoff: $('handoff').value });
  browserDownloads.onchange = () => {
    showDownloadMode();
    return chrome.storage.sync.set({
      route: browserDownloads.value === 'chrome' ? 'chrome' : 'auto',
      intercept: browserDownloads.value === 'automatic'
    });
  };
  $('prefetch').onchange = async () => {
    const enabled = $('prefetch').checked;
    await chrome.storage.local.set({ prefetchFormats: enabled });
    if (enabled) {
      const [tab] = await chrome.tabs.query({ active: true, currentWindow: true }).catch(() => []);
      if (tab?.id !== undefined)
        await chrome.tabs.sendMessage(tab.id, { type: 'vesper-prefetch' }).catch(() => {});
    }
  };

  const signIn = $('signin');
  const access = $('signin-access');
  const permit = $('signin-permission');
  const fileSignIn = $('file-sign-in');
  const siteSignIn = $('signin-site');
  const siteHostLabel = $('signin-host');
  const siteHint = $('signin-site-hint');
  const cookies = { permissions: ['cookies'] };
  const hasAccess = () => chrome.permissions.contains(cookies).catch(() => false);
  let cookiePermission = false;
  function httpsHost(value) {
    try {
      const url = new URL(value);
      return url.protocol === 'https:' && !url.username && !url.password ? url.hostname.toLowerCase() : null;
    } catch { return null; }
  }
  const [activeTab] = await chrome.tabs.query({ active: true, currentWindow: true }).catch(() => []);
  const siteHost = httpsHost(activeTab?.url);
  siteHostLabel.textContent = siteHost || 'this site';
  if (siteHost) {
    try {
      const { signInHosts = [] } = await chrome.storage.local.get('signInHosts');
      siteSignIn.checked = Array.isArray(signInHosts) && signInHosts.includes(siteHost);
    } catch {
      siteHint.textContent = 'Couldn’t read this site’s sign-in setting.';
    }
  } else {
    siteHint.textContent = 'Site sign-in is available on HTTPS pages.';
  }
  siteSignIn.onchange = async () => {
    if (!siteHost) return;
    siteSignIn.disabled = true;
    try {
      const { signInHosts = [] } = await chrome.storage.local.get('signInHosts');
      const hosts = new Set(Array.isArray(signInHosts)
        ? signInHosts.filter(host => typeof host === 'string' && host.length <= 253).slice(0, 50) : []);
      if (siteSignIn.checked && !hosts.has(siteHost) && hosts.size >= 50) {
        siteSignIn.checked = false;
        siteHint.textContent = 'Remove an approved host before adding another.';
        return;
      }
      if (siteSignIn.checked) hosts.add(siteHost);
      else hosts.delete(siteHost);
      await chrome.storage.local.set({ signInHosts: [...hosts].slice(0, 50) });
      siteHint.textContent = siteSignIn.checked ? 'VESPER can use your sign-in for files from this site.'
        : 'Turn this on if the file requires your account.';
    } catch {
      siteSignIn.checked = !siteSignIn.checked;
      siteHint.textContent = 'Couldn’t save this site’s sign-in setting.';
    } finally {
      siteSignIn.disabled = fileSignIn.hidden;
    }
  };
  function showAccess(allowed) {
    cookiePermission = allowed;
    signIn.checked = prefs.useSignIn;
    permit.hidden = allowed || !signIn.checked;
    access.textContent = !signIn.checked ? '' : allowed
      ? 'Used automatically when you choose a video on a supported site.'
      : 'Chrome needs your permission to use your sign-in.';
    fileSignIn.hidden = !allowed || !signIn.checked || !siteHost;
    siteSignIn.disabled = fileSignIn.hidden;
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
  await showRecentDownloads(allowSignIn);
}

// One button per reason, and only where the user can actually change the outcome.
const RECENT_ACTIONS = {
  'sign-in': { label: 'Set up sign-in', run: async allowSignIn => {
    $('site-sign-in').open = true;
    if (!await allowSignIn()) return 'Chrome sign-in access was not enabled.';
    $('file-sign-in').open = true;
    return 'Allow file sign-in on the download’s site, then try the download again.';
  } },
  'download-now': { label: 'Download now instead', run: async () => {
    await chrome.storage.sync.set({ handoff: 'download' });
    $('handoff').value = 'download';
    return 'New downloads will start in VESPER right away.';
  } },
  'check-app': { label: 'Check VESPER', run: async () => {
    const reply = await chrome.runtime.sendMessage({ type: 'hello' });
    return reply?.ok ? 'VESPER is running. Start the download again.'
      : 'VESPER didn’t answer. Open the VESPER app once, then try again.';
  } }
};

async function showRecentDownloads(allowSignIn) {
  const { vesperCaptureDecisions: saved } = await chrome.storage.session.get('vesperCaptureDecisions').catch(() => ({}));
  const recent = (Array.isArray(saved) ? saved : []).filter(entry => VesperCaptureReasons[entry?.outcome]).slice(0, 5);
  $('recent').hidden = recent.length === 0;
  for (const entry of recent) {
    const reason = VesperCaptureReasons[entry.outcome];
    const item = document.createElement('li');
    item.className = 'decision';
    const name = document.createElement('b');
    name.textContent = name.title = String(entry.name || 'Download');
    const text = document.createElement('small');
    const site = entry.site ? `${entry.site} · ` : '';
    text.textContent = site + (entry.outcome === 'moved' ? reason.text : `Stayed in Chrome. ${reason.text}`);
    item.append(name, text);
    const action = RECENT_ACTIONS[reason.action];
    if (action) {
      const button = document.createElement('button');
      button.className = 'vesper-button';
      button.textContent = action.label;
      button.onclick = async () => {
        button.disabled = true;
        try { $('recent-result').textContent = await action.run(allowSignIn); }
        catch { $('recent-result').textContent = 'That didn’t work. Try again in a moment.'; }
        button.disabled = false;
      };
      item.append(button);
    }
    $('decisions').append(item);
  }
}
void init();
