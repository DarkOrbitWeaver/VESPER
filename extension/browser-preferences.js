'use strict';

globalThis.VesperBrowserPreferences = (() => {
  const pauseKeys = ['vesperPaused', 'vesperPauseMigrated'];
  const oldModes = ['intercept', 'route', 'handoff'];
  const migrationDefault = 'vesperPauseOnUpgrade';

  function pauseState(prefs) {
    const ready = prefs.vesperPauseMigrated === true;
    return { ready, paused: !ready || prefs.vesperPaused === true };
  }

  async function readPause() {
    return pauseState(await chrome.storage.local.get(pauseKeys).catch(() => ({})));
  }

  function watchPause(listener) {
    let revision = 0;
    let prefs = null;
    let stopped = false;
    async function load() {
      const current = ++revision;
      const saved = await chrome.storage.local.get(pauseKeys).catch(() => ({}));
      if (stopped || current !== revision) return;
      prefs = saved;
      listener(pauseState(prefs));
    }
    function changed(changes, area) {
      if (area !== 'local' || !pauseKeys.some(key => key in changes)) return;
      if (!prefs) { void load(); return; }
      for (const key of pauseKeys) if (key in changes) prefs[key] = changes[key].newValue;
      return listener(pauseState(prefs));
    }
    chrome.storage.onChanged.addListener(changed);
    void load();
    return () => {
      stopped = true;
      chrome.storage.onChanged.removeListener(changed);
    };
  }

  // Only the service worker migrates settings. Keep the old sync opt-out
  // available to other profiles as they upgrade, without syncing new pauses.
  async function migrate() {
    const local = await chrome.storage.local.get(pauseKeys);
    if (local.vesperPauseMigrated === true) return;
    const old = await chrome.storage.sync.get([...oldModes, migrationDefault]);
    const legacyPause = old.intercept === false || old.route === 'chrome' || old.handoff === 'queue';
    if (legacyPause && old[migrationDefault] !== true)
      await chrome.storage.sync.set({ [migrationDefault]: true });
    await Promise.all([
      chrome.storage.sync.remove(oldModes),
      chrome.storage.local.remove(['prefetchFormats', 'vesperSeen'])
    ]);
    await chrome.storage.local.set({
      vesperPaused: local.vesperPaused === true || legacyPause || old[migrationDefault] === true,
      vesperPauseMigrated: true
    });
  }

  return { migrate, readPause, watchPause };
})();
