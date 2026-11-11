'use strict';

VesperTheme.install();
const $ = id => document.getElementById(id);

async function init() {
  const prefs = await chrome.storage.sync.get({ theme: 'system', handoff: 'download', intercept: true, useSignIn: true });
  $('theme').value = prefs.theme;
  $('handoff').value = prefs.handoff;
  $('intercept').checked = prefs.intercept;

  $('theme').onchange = () => chrome.storage.sync.set({ theme: $('theme').value });
  $('handoff').onchange = () => chrome.storage.sync.set({ handoff: $('handoff').value });
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
