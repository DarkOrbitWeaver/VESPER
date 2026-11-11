'use strict';

globalThis.VesperTheme = (() => {
  const light = '--paper:#faf6ed;--surface:#fcf8f0;--wash:#f0ede1;--border:#cdc5b3;--text:#262e22;--muted:#646557;--green:#3c5736;--hover:#2f482b;--sage:#62784c;--gold:#8b5c17;--red:#a53c34;--blue:#36688a;--violet:#744d8e;color-scheme:light;';
  const dark = '--paper:#1d251e;--surface:#222a23;--wash:#29332a;--border:#4e5d4b;--text:#f3eddf;--muted:#b9c4b3;--green:#416244;--hover:#365539;--sage:#a7be91;--gold:#e4b96e;--red:#f2a299;--blue:#9ac5e6;--violet:#d9b4ec;color-scheme:dark;';
  const css = `
    :root,:host{${light}}
    :root[data-vesper-theme="dark"],:host([data-vesper-theme="dark"]){${dark}}
    @media(prefers-color-scheme:dark){:root:not([data-vesper-theme="light"]),:host(:not([data-vesper-theme="light"])){${dark}}}
    .vesper-button{font:inherit;color:var(--text);background:var(--surface);border:1px solid var(--border);
      border-radius:6px;padding:7px 12px;cursor:pointer;box-shadow:inset 0 1px #ffffff16}
    .vesper-button:hover{background:var(--wash)}
    .vesper-button.primary{background:var(--green);color:#fffbee;border-color:var(--green);box-shadow:inset 0 1px #ffffff26}
    .vesper-button.primary:hover{background:var(--hover)}
    .vesper-button:active{box-shadow:none}
    .vesper-button:disabled{background:var(--wash);border-color:var(--border);color:var(--muted);cursor:default;box-shadow:none}
    :focus-visible{outline:2px solid var(--sage);outline-offset:2px}
  `;
  function watch(target) {
    let active = true;
    const apply = theme => {
      if (active) target.dataset.vesperTheme = ['light', 'dark'].includes(theme) ? theme : 'system';
    };
    const listener = (changes, area) => {
      if (area === 'sync' && changes.theme) apply(changes.theme.newValue);
    };
    chrome.storage.sync.get({ theme: 'system' }).then(prefs => apply(prefs.theme)).catch(() => {});
    chrome.storage.onChanged.addListener(listener);
    return () => { active = false; chrome.storage.onChanged.removeListener(listener); };
  }
  function install() {
    const style = document.createElement('style');
    style.textContent = css;
    document.head.append(style);
    watch(document.documentElement);
  }
  return { css, watch, install };
})();
