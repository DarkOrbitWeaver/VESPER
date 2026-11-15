'use strict';

VesperTheme.install();
const $ = id => document.getElementById(id);

async function init() {
  const prefs = await chrome.storage.sync.get({ theme: 'system', motion: 'system', route: 'auto', handoff: 'download', intercept: true, useSignIn: false });
  $('theme').value = prefs.theme;
  $('motion').value = ['animate', 'still'].includes(prefs.motion) ? prefs.motion : 'system';
  $('handoff').value = prefs.handoff;
  $('route').value = prefs.route === 'chrome' ? 'chrome' : 'auto';
  $('intercept').checked = prefs.intercept;
  const { prefetchFormats = false } = await chrome.storage.local.get('prefetchFormats').catch(() => ({}));
  $('prefetch').checked = prefetchFormats;
  // Say what capture does the first time, in plain words.
  const { vesperSeen } = await chrome.storage.local.get('vesperSeen').catch(() => ({}));
  $('first-run').hidden = !!vesperSeen;
  if (!vesperSeen) chrome.storage.local.set({ vesperSeen: true }).catch(() => {});

  $('theme').onchange = () => chrome.storage.sync.set({ theme: $('theme').value });
  $('motion').onchange = () => chrome.storage.sync.set({ motion: $('motion').value });
  $('handoff').onchange = () => chrome.storage.sync.set({ handoff: $('handoff').value });
  $('route').onchange = () => chrome.storage.sync.set({ route: $('route').value });
  $('intercept').onchange = () => chrome.storage.sync.set({ intercept: $('intercept').checked });
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
  const cookies = { permissions: ['cookies'] };
  const hasAccess = () => chrome.permissions.contains(cookies).catch(() => false);
  function showAccess(allowed) {
    signIn.checked = prefs.useSignIn && allowed;
    access.textContent = allowed ? 'Access granted in Chrome.' : 'Chrome permission required.';
  }
  chrome.permissions.onRemoved.addListener(removed => {
    if (removed.permissions?.includes('cookies')) showAccess(false);
  });
  signIn.onchange = async () => {
    signIn.disabled = true;
    try {
      const enabled = signIn.checked && await chrome.permissions.request(cookies).catch(() => false);
      await chrome.storage.sync.set({ useSignIn: enabled });
      prefs.useSignIn = enabled;
      showAccess(await hasAccess());
    } catch {
      showAccess(await hasAccess());
      access.textContent = 'Couldn’t save this setting.';
    } finally {
      signIn.disabled = false;
    }
  };
  showAccess(await hasAccess());
  signIn.disabled = false;
  await showRecentDownloads(prefs, signIn);
  void showVersions();
}

// "Extension 1.0.1 · VESPER 1.0.1 (2620e76)". When the two numbers differ, say which one to update.
async function showVersions() {
  const extension = chrome.runtime.getManifest().version;
  const line = $('versions');
  line.textContent = `Extension ${extension}`;
  const reply = await chrome.runtime.sendMessage({ type: 'hello' }).catch(() => null);
  if (!reply?.ok) { line.textContent += ' · VESPER app not reachable'; return; }
  const app = reply.data?.build || reply.appVersion || '';
  line.textContent = `Extension ${extension} · VESPER ${app}`.trim();
  const appNumber = (reply.appVersion || app).split(' ')[0];
  if (appNumber && appNumber !== extension) {
    const older = compareVersions(extension, appNumber) < 0 ? 'extension' : 'VESPER app';
    line.textContent += ` — update the ${older}`;
  }
}

function compareVersions(a, b) {
  const pa = a.split('.').map(Number), pb = b.split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] || 0) - (pb[i] || 0);
    if (d) return d;
  }
  return 0;
}

// One button per reason, and only where the user can actually change the outcome.
const RECENT_ACTIONS = {
  'sign-in': { label: 'Use my sign-in', run: async (prefs, signIn) => {
    signIn.checked = true;
    await signIn.onchange();
    return prefs.useSignIn ? 'Sign-in is on. Start the download again.' : 'Chrome didn’t grant access, so nothing changed.';
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
  } },
  'move': { label: 'Move it now…', run: async () => {
    await chrome.tabs.create({ url: chrome.runtime.getURL('transfers.html') });
    return '';
  } }
};

async function showRecentDownloads(prefs, signIn) {
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
    // Sign-in is already on when the checkbox is ticked; offering it again would do nothing.
    if (action && !(reason.action === 'sign-in' && signIn.checked)) {
      const button = document.createElement('button');
      button.className = 'vesper-button';
      button.textContent = action.label;
      button.onclick = async () => {
        button.disabled = true;
        try { $('recent-result').textContent = await action.run(prefs, signIn); }
        catch { $('recent-result').textContent = 'That didn’t work. Try again in a moment.'; }
        button.disabled = false;
      };
      item.append(button);
    }
    $('decisions').append(item);
  }
}
void init();
