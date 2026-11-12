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

  $('theme').onchange = () => chrome.storage.sync.set({ theme: $('theme').value });
  $('motion').onchange = () => chrome.storage.sync.set({ motion: $('motion').value });
  $('handoff').onchange = () => chrome.storage.sync.set({ handoff: $('handoff').value });
  $('route').onchange = () => chrome.storage.sync.set({ route: $('route').value });
  $('intercept').onchange = () => chrome.storage.sync.set({ intercept: $('intercept').checked });

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
}
void init();
