// Guided Smart Select workflow. Entirely opt-in; no page interaction until the user reviews the batch.
// DOM facts + VesperControlEvidence, with deterministic action guards.
//
// Attribution of each click's download:
//   static   - the shared control names the item (data attribute, query, path, or ARIA panel). The
//              expected destination is armed exactly.
//   temporal - a script-driven button with no destination. Allowed only when exactly one item of the
//              group is selected; the first download the tab creates after the click is attributed,
//              and a second one in the same window stops the batch.
// One action runs at a time. Anything uncertain stops the remaining actions instead of clicking twice.
(() => {
  'use strict';
  const brain = globalThis.VesperIntelligence;
  const evidence = globalThis.VesperControlEvidence;
  if (!brain || !evidence || globalThis.VesperSmartBatch) return;
  const MAX = 50;
  const TRIGGER_TIMEOUT = 75_000;
  const READY_TIMEOUT = 90_000;
  const STABLE_MS = 400;
  const IDENT = /^[A-Za-z0-9][\w.-]{0,100}$/;
  const ITEM_ATTRIBUTES = ['data-item-id', 'data-id', 'data-episode-id', 'data-post-id', 'data-chapter-id', 'data-file-id'];
  const ITEM_QUERY = /^(?:item|item_id|episode|episode_id|post_id|chapter|chapter_id|file_id|part|track)$/i;
  const CONTROL_SELECTOR = 'button, a[href], [role="button"], input[type="submit"], input[type="button"]';
  let dialog = null, executor = null;
  const clean = text => String(text || '').replace(/\s+/g, ' ').trim().slice(0, 150);
  const idValue = value => IDENT.test(String(value || '')) ? String(value) : '';
  const hash = value => {
    let h = 2166136261;
    for (const ch of String(value)) { h ^= ch.charCodeAt(0); h = Math.imul(h, 16777619); }
    return (h >>> 0).toString(36);
  };
  function shown(el) {
    if (!el?.isConnected || !el.getBoundingClientRect) return false;
    const box = el.getBoundingClientRect();
    if (box.width < 5 || box.height < 5) return false;
    const s = getComputedStyle(el);
    return s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0';
  }
  function labelFor(el) {
    return clean(el.getAttribute('aria-label') || el.innerText || el.textContent || el.getAttribute('title') || el.value);
  }
  function rawItemId(el) {
    const explicit = [...new Set(ITEM_ATTRIBUTES.map(attribute => idValue(el.getAttribute(attribute))).filter(Boolean))];
    if (explicit.length > 1) return '';
    const target = brain.http(el.getAttribute('href'), location.href);
    const queryIds = target?.origin === location.origin
      ? [...new Set([...target.searchParams].filter(([key]) => ITEM_QUERY.test(key))
          .map(([, value]) => idValue(value)).filter(Boolean))] : [];
    if (queryIds.length > 1 || explicit.length && queryIds.length && explicit[0] !== queryIds[0]) return '';
    if (explicit.length || queryIds.length) return explicit[0] || queryIds[0];
    const panelId = idValue(el.getAttribute('aria-controls'));
    if (panelId) return panelId;
    if (target?.origin === location.origin) {
      // Malformed percent escapes are valid in some links; one such link must not break the picker.
      try {
        const leaf = decodeURIComponent(target.pathname.split('/').filter(Boolean).at(-1) || '');
        if (idValue(leaf)) return leaf;
      } catch { /* Unusable item identity. */ }
    }
    return '';
  }
  // Activating an item must not leave the document: in-page controls, or links that only change the hash.
  function inPage(el) {
    if (el.tagName !== 'A') return true;
    const raw = (el.getAttribute('href') || '').trim();
    if (!raw || raw.startsWith('#') || /^javascript:/i.test(raw)) return true;
    const target = brain.http(raw, location.href);
    return !!target && target.origin === location.origin && target.pathname === location.pathname && target.search === location.search;
  }
  // The repeated container an item belongs to ("the episode list"): its shape, not its position.
  function groupOf(el) {
    const root = el.closest('li, tr, [role="option"], [role="tab"], [role="treeitem"], [role="listitem"], [role="row"], [role="radio"]') || el;
    const parent = root.parentElement;
    if (!parent) return null;
    const similar = [...parent.children].filter(child => child.tagName === root.tagName
      && (child.getAttribute('role') || '') === (root.getAttribute('role') || ''));
    if (similar.length < 2) return null;
    let signature = '';
    for (let node = parent, depth = 0; node && depth < 6 && node !== document.body; node = node.parentElement, depth++) {
      const role = node.getAttribute('role');
      if (node.id && IDENT.test(node.id)) { signature = `#${node.id}`; break; }
      if (['tablist', 'listbox', 'list', 'tree', 'grid', 'table', 'menu', 'radiogroup'].includes(role)) { signature = `${role}:${depth}`; break; }
      if (['UL', 'OL', 'TABLE', 'TBODY', 'NAV'].includes(node.tagName)) { signature = `${node.tagName}:${depth}`; break; }
    }
    return { root, parent, signature: signature || `${parent.tagName}:${parent.childElementCount}` };
  }
  const isActive = el => el?.getAttribute('aria-selected') === 'true'
    || ['page', 'true', 'step', 'location'].includes(el?.getAttribute('aria-current'))
    || el?.getAttribute('aria-pressed') === 'true' || el?.getAttribute('aria-checked') === 'true'
    || el?.classList?.contains('active') || el?.classList?.contains('selected') || el?.classList?.contains('current')
    || el?.getAttribute('data-active') === 'true' || el?.getAttribute('data-selected') === 'true';
  const activeHere = el => isActive(el) || isActive(groupOf(el)?.root);
  function collectNav() {
    const query = '[role="tab"], [role="option"], [role="treeitem"], [role="radio"], [aria-controls], [data-item-id], [data-episode-id], '
      + '[data-chapter-id], [data-file-id], [data-id], li > button, li > a[href], tr button, nav a[href], a[href]';
    const seen = new Set(), ambiguous = new Set(), found = [];
    let inspected = 0;
    for (const element of document.querySelectorAll(query)) {
      if (++inspected > 600) break;
      if (dialog?.contains(element) || !shown(element) || element.disabled
          || element.getAttribute('aria-disabled') === 'true') continue;
      const href = brain.http(element.getAttribute('href'), location.href);
      if (href && href.origin !== location.origin) continue;
      if (href && brain.sourceKind(href.href) !== 'unknown') continue;
      const label = labelFor(element);
      if (!label || label.length > 150 || evidence.vetoed(label)) continue;
      // A download control is the action, never one of the items.
      if (evidence.assess({ tagName: element.tagName, role: element.getAttribute('role'), label, href: href?.href || '',
          downloadAttribute: element.hasAttribute('download') }).verdict === 'download') continue;
      const id = rawItemId(element);
      const group = groupOf(element);
      // Items without a stable id are allowed only inside a repeated group and only if activating them stays on the page.
      if (!id && (!group || !inPage(element))) continue;
      const key = id ? `id-${hash([element.tagName, id, href?.href || '', label].join('|'))}`
        : `group-${hash([group.signature, group.root.tagName, element.getAttribute('role') || '', evidence.stableLabel(label)].join('|'))}`;
      if (seen.has(key)) { ambiguous.add(key); continue; }
      seen.add(key);
      if (found.length < 120) found.push({ key, id, label, element, group: group?.signature || '' });
    }
    return found.filter(item => !ambiguous.has(item.key));
  }
  function actionUrl(button) {
    if (!button) return null;
    // Placeholder anchors commonly invoke a real download through data attributes.
    // Contradictory concrete destinations cannot prove one approved action.
    const href = button.getAttribute('href');
    const claims = [href && !href.trim().startsWith('#') && !/^javascript:/i.test(href.trim()) ? href : null,
      button.getAttribute('data-download-url'), button.getAttribute('data-url'), button.getAttribute('formaction')]
      .filter(Boolean).map(value => brain.http(value, location.href));
    if (!claims.length || claims.some(url => !url) || new Set(claims.map(url => url.href)).size !== 1) return null;
    return claims[0];
  }
  function controlFacts(element) {
    const url = actionUrl(element);
    return {
      tagName: element.tagName, role: element.getAttribute('role'),
      label: labelFor(element), ariaLabel: element.getAttribute('aria-label'),
      title: element.getAttribute('title'), href: url?.href || '',
      downloadAttribute: element.hasAttribute('download'),
      hasDownloadUrl: element.hasAttribute('data-download-url'),
      stableBinding: Boolean(rawItemId(element)),
      isDisabled: element.disabled || element.getAttribute('aria-disabled') === 'true',
      isNavigation: Boolean(element.closest('nav, [role="navigation"]')) || element.getAttribute('role') === 'tab',
      isInlinePdf: Boolean(url && /\.pdf$/i.test(url.pathname) && !element.hasAttribute('download')),
      targetBlank: element.getAttribute('target') === '_blank'
    };
  }
  function relationshipFacts(control, item) {
    const url = actionUrl(control);
    const bindings = ITEM_ATTRIBUTES.map(attr => control.getAttribute(attr)).filter(Boolean);
    const query = url ? [...url.searchParams.entries()].filter(([key]) => ITEM_QUERY.test(key)).map(([, value]) => value) : [];
    const panelId = item.element.getAttribute('aria-controls');
    const panels = /^[\w.-]{1,100}$/.test(panelId || '') ? document.querySelectorAll('[id="' + panelId + '"]') : [];
    const panel = panels.length === 1 && panels[0].getAttribute('role') === 'tabpanel' ? panels[0] : null;
    const group = item.element.closest?.('article, li, tr, [role="group"], [role="tabpanel"], [role="row"]');
    const candidatePanel = control.closest?.('[role="tabpanel"]');
    const facts = controlFacts(control);
    return { explicitMatch: !!item.id && bindings.includes(item.id), queryMatch: !!item.id && query.includes(item.id),
      panelMatch: Boolean(panel?.contains(control)),
      pathMatch: Boolean(url && item.id.length >= 4 && url.pathname.split('/').some(part =>
        part === item.id || part.startsWith(item.id + '.') || part.endsWith('-' + item.id))),
      sameGroup: Boolean(group && group.contains(control)), active: activeHere(item.element),
      structuralDownload: facts.downloadAttribute || facts.hasDownloadUrl,
      contradiction: !!item.id && [...bindings, ...query].some(value => value !== item.id),
      differentPanel: Boolean(panel && candidatePanel && candidatePanel !== panel),
      blocked: evidence.assess(facts).verdict === 'blocked' };
  }
  function candidateActions(choices) {
    const active = choices.filter(item => activeHere(item.element));
    const selected = active.length === 1 ? active[0] : null;
    const candidates = [];
    for (const element of [...document.querySelectorAll(CONTROL_SELECTOR)].slice(0, 300)) {
      if (!shown(element) || dialog?.contains(element)) continue;
      const verdict = evidence.assess(controlFacts(element));
      if (verdict.verdict !== 'download') continue;
      const relation = selected ? evidence.relation(relationshipFacts(element, selected)) : { logOdds: 0, vetoed: false };
      if (relation.vetoed) continue;
      candidates.push({ element, score: verdict.logOdds + Math.max(-4, Math.min(4, relation.logOdds)) });
    }
    return candidates;
  }
  function suggestedAction(choices) {
    return evidence.decide(candidateActions(choices))?.element || null;
  }
  function actionKey(el) {
    if (!el) return '';
    // A countdown changes the display text, not the approved Download action.
    return [el.tagName, clean(el.getAttribute('role')), idValue(el.id), evidence.stableLabel(labelFor(el))].join('|');
  }
  // The control the user approved: the same element while it stays connected, otherwise one unique rerender.
  function matchingAction(key, approved) {
    if (approved?.isConnected && actionKey(approved) === key && shown(approved)) return approved;
    let match = null;
    for (const el of document.querySelectorAll(CONTROL_SELECTOR)) {
      if (dialog?.contains(el) || actionKey(el) !== key || !shown(el)) continue;
      if (match) return null; // A rerender may leave both controls connected.
      match = el;
    }
    return match;
  }
  function selectedItem(item) {
    const candidates = collectNav().filter(candidate => candidate.key === item.key && candidate.id === item.id);
    return candidates.length === 1 ? candidates[0].element : null;
  }
  // Exactly one item of this group is selected: the state a script-driven shared button reads.
  function uniquelyActive(item, element) {
    if (!activeHere(element)) return false;
    const peers = collectNav().filter(candidate => candidate.group === item.group && candidate.key !== item.key);
    return peers.every(peer => !activeHere(peer.element));
  }
  function actionProvesItem(button, item, el) {
    if (!item.id || !button) return false;
    const bindings = ITEM_ATTRIBUTES.map(attr => button.getAttribute(attr)).filter(Boolean);
    if (bindings.some(value => value !== item.id)) return false;
    const link = actionUrl(button);
    // Every explicit item claim must agree, not just the first attribute.
    const queryBindings = link ? [...link.searchParams.entries()].filter(([key]) => ITEM_QUERY.test(key)) : [];
    if (queryBindings.some(([, value]) => value !== item.id)) return false;
    if (bindings.length || queryBindings.length) return true;
    // Accessibility relationships support pages without bespoke data-item attributes.
    // An active, uniquely named panel must actually contain the approved control.
    const controlled = el?.getAttribute('aria-controls');
    if (controlled && /^[\w.-]{1,100}$/.test(controlled) && el.getAttribute('aria-selected') === 'true') {
      const panels = document.querySelectorAll('[id="' + controlled + '"]');
      if (panels.length === 1 && panels[0].getAttribute('role') === 'tabpanel'
          && panels[0].contains(button) && !panels[0].hidden && panels[0].getAttribute('aria-hidden') !== 'true') {
        const labelledBy = panels[0].getAttribute('aria-labelledby');
        if (/^[\w.-]{1,100}$/.test(el.id || '') && labelledBy?.split(/\s+/).includes(el.id)
            && document.querySelectorAll('[id="' + el.id + '"]').length === 1) return true;
      }
    }
    if (!link || item.id.length < 4) return false;
    // File-name/path tokens and exact query values, never a substring like "1".
    const values = [...link.pathname.split('/').filter(Boolean), ...link.searchParams.values()];
    return values.some(value => value === item.id || value.startsWith(item.id + '.') || value.endsWith('-' + item.id));
  }
  function waitForChange(check, timeoutMs = READY_TIMEOUT, signal) {
    return new Promise(resolve => {
      if (signal?.aborted) return resolve(null);
      let finished = false, poll = 0, timer = 0;
      const finish = result => {
        if (finished) return;
        finished = true;
        observer.disconnect(); clearTimeout(timer); clearTimeout(poll);
        signal?.removeEventListener('abort', abort);
        resolve(result);
      };
      const abort = () => finish(null);
      const inspect = () => {
        if (finished) return;
        try { const value = check(); if (value) finish(value); }
        catch { finish(null); }
      };
      // Some sites enable buttons using script properties or text-only updates, and countdowns need
      // a stability window. One bounded timer exists only during the approved readiness wait.
      const tick = () => {
        inspect();
        if (!finished) poll = setTimeout(tick, 250);
      };
      const observer = new MutationObserver(inspect);
      observer.observe(document.body || document.documentElement, { subtree: true, childList: true,
        characterData: true, attributes: true, attributeFilter: ['href', 'aria-selected', 'aria-current', 'aria-pressed',
          'aria-checked', 'class', 'disabled', 'aria-disabled', 'hidden', 'style', 'data-active', 'data-selected',
          'data-download-url', 'data-url', ...ITEM_ATTRIBUTES, 'aria-controls', 'aria-labelledby', 'aria-hidden'] });
      signal?.addEventListener('abort', abort, { once: true });
      timer = setTimeout(() => finish(null), timeoutMs);
      tick();
    });
  }
  // A control may be clicked when it is visible, enabled, not navigation, not vetoed and not counting down.
  // It no longer needs an address: script-driven buttons use temporal attribution.
  function eligibleAction(control) {
    if (!control || !shown(control) || control.closest?.('nav, [role="navigation"]') || control.getAttribute('role') === 'tab')
      return false;
    const facts = controlFacts(control);
    const verdict = evidence.assess(facts);
    if (verdict.verdict === 'blocked' || !verdict.ready || facts.isInlinePdf) return false;
    const url = actionUrl(control);
    return !(url && brain.sourceKind(url.href) === 'fragment');
  }
  const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
  function button(text, action, kind) {
    const b = document.createElement('button');
    b.type = 'button'; b.textContent = text;
    b.className = kind || '';
    b.addEventListener('click', action);
    return b;
  }
  function show() {
    if (dialog || executor) return;
    const previousFocus = document.activeElement;
    const choices = collectNav();
    let picked = suggestedAction(choices), selectionMode = false, activeBatchId = null, lastApproved = [], starting = false;
    const checked = new Set();
    const host = document.createElement('div');
    dialog = host;
    host.dataset.vesperBatch = '';
    host.style.cssText = 'all:initial;position:fixed;inset:0;z-index:2147483647;background:#0009;display:grid;place-items:center;';
    const shadow = host.attachShadow({ mode: 'closed' });
    const css = document.createElement('style');
    css.textContent = `*{box-sizing:border-box}section{font:13px/1.4 system-ui,sans-serif;color:#283b30;background:#faf9f3;
      border:1px solid #718776;border-radius:10px;max-height:90vh;width:min(620px,94vw);padding:15px;
      box-shadow:0 12px 45px #0005;display:grid;gap:9px}h2,p{margin:0}h2{font-size:17px}
      #items{overflow:auto;max-height:38vh;padding:4px;border:1px solid #dbe0d5;border-radius:7px}
      label{display:flex;gap:8px;align-items:center;padding:5px;border-bottom:1px solid #ebeee6}
      small{color:#586a5d}button{font:inherit;padding:7px 10px;border-radius:7px;cursor:pointer;
      border:1px solid #859780;color:#243e2a;background:#eef1e7}button:disabled{opacity:.48;cursor:default}
      button.primary{background:#335b3b;color:white}footer{display:flex;gap:8px;flex-wrap:wrap;justify-content:flex-end}
      #log{max-height:17vh;overflow:auto;font:12px/1.4 system-ui;white-space:pre-wrap;overflow-wrap:anywhere}
      :focus-visible{outline:2px solid #50853e;outline-offset:2px}`;
    const section = document.createElement('section');
    section.setAttribute('role', 'dialog'); section.setAttribute('aria-modal', 'true');
    section.setAttribute('aria-label', 'VESPER guided Smart Select batch');
    const title = document.createElement('h2'); title.textContent = 'Smart Select · Guided batch';
    const info = document.createElement('p');
    info.textContent = 'Choose items, then point to their shared Download button. Review before VESPER clicks anything.';
    const items = document.createElement('div'); items.id = 'items';
    const log = document.createElement('div'); log.id = 'log'; log.setAttribute('role', 'status');
    const actionInfo = document.createElement('small');
    actionInfo.textContent = picked ? `Suggested Download control: ${labelFor(picked)}. Review or choose another.`
      : 'Shared Download button: not selected';
    const footer = document.createElement('footer');
    const close = button('Close', () => remove());
    const choose = button('Choose download button', () => showChoices());
    const controlChoices = document.createElement('div');
    controlChoices.hidden = true;
    const start = button('Review and start', () => void startBatch(), 'primary');
    const cancel = button('Cancel remaining', () => { stopBatch(); write('Stopping remaining actions. Downloads already started are left alone.'); });
    const refresh = button('Refresh outcomes', () => void refreshResults());
    const acknowledge = button('I checked previous downloads', () => void acknowledgePrevious());
    acknowledge.hidden = true;
    cancel.disabled = true; refresh.disabled = true;
    let closeRequested = false, finishSelection = null, batchController = null, unresolvedBatch = true;
    function write(value) { log.textContent = `${log.textContent}\n${value}`.trim().slice(-3500); }
    function stopBatch() {
      executor?.cancel();
      batchController?.abort();
    }
    function onSettingsChanged(changes, area) {
      if (area === 'local' && changes.vesperPaused?.newValue === true) {
        finishSelection?.();
        stopBatch(); write('VESPER was paused. No further batch actions will start.');
      }
    }
    chrome.storage.onChanged?.addListener?.(onSettingsChanged);
    function remove() {
      finishSelection?.();
      stopBatch(); chrome.storage.onChanged?.removeListener?.(onSettingsChanged);
      if (executor) closeRequested = true;
      // Leave already-triggered watches to their bounded expiry. A late download
      // must not lose attribution merely because the user closes the panel.
      host.remove(); dialog = null;
      if (previousFocus?.isConnected) previousFocus.focus({ preventScroll: true });
    }
    for (const choice of choices) {
      const row = document.createElement('label');
      const check = document.createElement('input'); check.type = 'checkbox';
      check.addEventListener('change', () => {
        if (check.checked && checked.size >= MAX) { check.checked = false; return; }
        if (check.checked) checked.add(choice.key); else checked.delete(choice.key);
        update();
      });
      const text = document.createElement('span'); text.textContent = choice.label;
      row.append(check, text); items.append(row);
    }
    function update() {
      start.disabled = !picked || !checked.size || !!executor || starting || selectionMode || !controlChoices.hidden || unresolvedBatch;
      choose.disabled = !!executor || selectionMode || starting;
      info.textContent = `${checked.size} selected · ${picked ? 'Action: ' + labelFor(picked) : 'Choose the shared Download button'}`;
    }
    function showChoices() {
      const controls = candidateActions(choices).filter(choice => eligibleAction(choice.element))
        .sort((a, b) => b.score - a.score).slice(0, 12).map(choice => choice.element);
      if (controls.length === 0) return chooseControl();
      const select = document.createElement('select');
      select.setAttribute('aria-label', 'Download button');
      for (const [index, control] of controls.entries()) {
        const option = document.createElement('option');
        option.textContent = `${labelFor(control)}${control.id ? ` (${control.id.slice(0, 60)})` : ''}`;
        option.value = String(index);
        select.append(option);
      }
      const dismiss = () => { controlChoices.hidden = true; update(); choose.focus({ preventScroll: true }); };
      controlChoices.replaceChildren(select, button('Confirm', () => {
        const target = controls[Number(select.value)];
        if (!target?.isConnected || !eligibleAction(target)) { write('That download button changed. Choose again.'); dismiss(); return; }
        picked = target;
        actionInfo.textContent = `Selected: ${labelFor(picked)}`;
        dismiss();
      }), button('Point to a button', () => { dismiss(); chooseControl(); }), button('Cancel', dismiss));
      controlChoices.hidden = false;
      update();
      select.focus({ preventScroll: true });
    }
    function chooseControl() {
      if (selectionMode) return;
      selectionMode = true; update();
      // Keep the extension overlay as the hit target throughout selection.
      // The site's actual button must never receive the physical selection click.
      section.hidden = true;
      section.style.display = 'none';
      const previousBackground = host.style.background;
      host.style.background = '#0002';
      host.style.cursor = 'crosshair';
      const hint = document.createElement('div');
      hint.style.cssText = 'position:fixed;top:14px;left:50%;transform:translateX(-50%);background:#faf9f3;color:#283b30;border:1px solid #718776;border-radius:8px;padding:10px 14px;font:13px system-ui;pointer-events:none';
      hint.textContent = 'Choose a download button. Esc to cancel.';
      shadow.append(hint);
      host.tabIndex = -1;
      host.focus({ preventScroll: true });
      let finished = false;
      const swallow = event => { event.preventDefault(); event.stopImmediatePropagation(); };
      const click = event => {
        swallow(event);
        if (event.button !== 0) return;
        const target = document.elementsFromPoint(event.clientX, event.clientY)
          .filter(el => el !== host && !host.contains(el))
          .map(el => el.closest?.(CONTROL_SELECTOR))
          .find(el => el && shown(el) && !host.contains(el));
        if (!target) {
          hint.textContent = 'No available control here. Choose another or press Esc.';
          return;
        }
        if (evidence.assess(controlFacts(target)).verdict === 'blocked') {
          hint.textContent = 'VESPER will not run purchase, account or delete controls. Choose another or press Esc.';
          return;
        }
        picked = target;
        actionInfo.textContent = 'Selected: ' + labelFor(picked) + '. Review before starting.';
        finish();
      };
      const keydown = event => {
        if (event.key === 'Escape') { swallow(event); finish(); }
        if (event.key === 'Tab') swallow(event);
      };
      const bindings = [['pointerdown', swallow], ['pointerup', swallow], ['mousedown', swallow],
        ['mouseup', swallow], ['click', click], ['contextmenu', swallow], ['keydown', keydown]];
      const finish = () => {
        if (finished) return;
        finished = true;
        clearTimeout(timer);
        for (const [event, callback] of bindings) host.removeEventListener(event, callback, true);
        hint.remove();
        host.style.background = previousBackground;
        host.style.cursor = '';
        section.hidden = false;
        section.style.display = '';
        selectionMode = false; finishSelection = null; update();
        if (host.isConnected) choose.focus({ preventScroll: true });
      };
      for (const [event, callback] of bindings) host.addEventListener(event, callback, true);
      const timer = setTimeout(finish, 15000);
      finishSelection = finish;
      write('Choose the control through the VESPER overlay. The site button is not clicked.');
    }
    function outcomeText(state) {
      return state.completed ? 'Finished in Chrome'
        : state.nativeCompleted ? 'Finished in VESPER'
        : state.ambiguous ? 'Couldn’t identify which download came from this action. Check your downloads before continuing.'
        : state.status === 'download-observed' ? 'Download started; final owner pending'
        : state.status === 'ownership-unresolved' ? 'Ownership uncertain; inspect downloads'
        : state.status;
    }
    async function refreshResults() {
      if (!activeBatchId || !lastApproved.length) return;
      for (const [index, item] of lastApproved.entries()) {
        const state = await chrome.runtime.sendMessage({ type: 'batch-status', batchId: activeBatchId,
          itemId: activeBatchId + '-' + index }).catch(() => null);
        if (state?.ok) write(`${item.label}: ${outcomeText(state)}`);
      }
    }
    async function restorePrevious() {
      try {
        const reply = await chrome.runtime.sendMessage({type:'batch-journal-read'});
        if (!host.isConnected) return;
        if (!reply?.ok) throw new Error('Journal unavailable');
        const record = reply.record;
        unresolvedBatch = Boolean(record?.steps.some(step => ['activating','waiting','ready','action-recorded','reconciling','indeterminate'].includes(step.state)));
        if (record) {
          activeBatchId = record.id;
          lastApproved = record.steps.map(step => ({label: choices.find(c => c.key === step.key)?.label || 'Item '+(step.index+1)}));
          refresh.disabled = false;
          if (unresolvedBatch) {
            write('A previous batch was interrupted. Check its downloads before approving another batch.');
            acknowledge.hidden = false;
          }
        }
      } catch { write('Could not check previous batch actions. Close and reopen to retry.'); }
      update();
    }
    async function acknowledgePrevious() {
      if (executor || !activeBatchId || !globalThis.confirm?.('Have you checked the previous downloads in Chrome and VESPER? A new batch will not resume the old one.')) return;
      const result = await chrome.runtime.sendMessage({type:'batch-journal-ack',id:activeBatchId}).catch(()=>null);
      if (!result?.ok) return write('Could not clear the replay guard. No new actions started.');
      unresolvedBatch = false; acknowledge.hidden = true; update();
    }
    async function startBatch() {
      if (starting || executor || !host.isConnected || unresolvedBatch) return;
      const selected = choices.filter(choice => checked.has(choice.key));
      const key = actionKey(picked);
      if (!picked || !key || !selected.length || new Set(selected.map(choice => choice.key)).size !== selected.length) {
        write('Each selected item must be uniquely identifiable. Ambiguous items require manual review.'); return;
      }
      const approved = selected.map(choice => ({ key: choice.key, label: choice.label, id: choice.id, group: choice.group }));
      const confirmed = globalThis.confirm?.(`Run ${approved.length} approved Download actions in order?\n\n` +
        approved.map(choice => choice.label).join('\n').slice(0, 1600));
      if (!confirmed) return;
      starting = true; update();
      if (!host.isConnected || dialog !== host) { starting = false; return; }
      batchController = new AbortController();
      const batchId = 'vesper-' + crypto.randomUUID();
      activeBatchId = batchId;
      lastApproved = approved;
      refresh.disabled = false;
      const approvedControl = picked;
      const plan = brain.makeBatchPlan({ id: batchId, origin: location.href,
        action: { label: labelFor(picked), key }, items: approved });
      const journal = {
        load: async () => null, // Never resume a previous user-approved action automatically.
        save: async value => {
          const reply = await chrome.runtime.sendMessage({type:'batch-journal-write',record:value});
          if (!reply?.ok) throw new Error('Could not persist the batch replay guard');
        }
      };
      const adapter = {
        activate: async step => {
          if (batchController.signal.aborted || location.origin !== plan.origin) return { exact: false };
          const item = approved[step.index];
          const el = selectedItem(item);
          if (!el || !shown(el)) return { exact: false };
          const previousActionUrl = actionUrl(matchingAction(key, approvedControl))?.href || '';
          const wasActive = activeHere(el);
          if (!wasActive) el.click();
          return { exact: true, element: el, wasActive, previousActionUrl };
        },
        waitReady: async (step, selected) => {
          const item = approved[step.index];
          let stableSince = 0, lastLabel = '', lastUrl = '', lastControl = null;
          const value = await waitForChange(() => {
            if (location.origin !== plan.origin) return null;
            const el = selectedItem(item);
            const download = matchingAction(key, approvedControl);
            const url = actionUrl(download);
            if (!el || !download || !url || !eligibleAction(download)
                || !activeHere(el) || !actionProvesItem(download, item, el)) {
              lastControl = null;
              return null;
            }
            // The label must hold still briefly: countdown and "preparing" texts change several times.
            const label = labelFor(download);
            if (label !== lastLabel || url.href !== lastUrl || download !== lastControl) {
              lastLabel = label;
              lastUrl = url.href;
              lastControl = download;
              stableSince = Date.now();
              return null;
            }
            if (Date.now() - stableSince < STABLE_MS) return null;
            const binding = ITEM_ATTRIBUTES.map(attr => download.getAttribute(attr)).find(Boolean);
            if (!selected.wasActive && url.href === selected.previousActionUrl && binding !== item.id) return null;
            return { verified: true, itemKey: step.key, actionKey: key, url: url.href, mode: 'exact', button: download };
          }, READY_TIMEOUT, batchController.signal);
          if (!value || batchController.signal.aborted) return { verified: false };
          const armed = await chrome.runtime.sendMessage({ type: 'batch-arm', batchId,
            itemId: batchId + '-' + step.index, ...(value.mode === 'exact' ? { expectedUrl: value.url } : {}) });
          if (!armed?.ok) return { verified: false };
          return value;
        },
        disarm: async step => {
          await chrome.runtime.sendMessage({ type: 'batch-disarm', batchId,
            itemId: batchId + '-' + step.index }).catch(() => {});
        },
        trigger: async (step, ready) => {
          if (location.origin !== plan.origin) throw new Error('Page origin changed before click');
          const current = matchingAction(key, approvedControl);
          const item = approved[step.index];
          const active = selectedItem(item);
          const stillBound = ready.mode === 'exact'
            ? actionUrl(current)?.href === ready.url && actionProvesItem(current, item, active)
            : uniquelyActive(item, active);
          if (!active || !shown(active) || !activeHere(active) || !current || current !== ready.button
              || !eligibleAction(current) || !stillBound)
            throw new Error('The item or Download button changed before click; no click sent');
          current.click();
        },
        reconcile: async step => {
          const deadline = Date.now() + TRIGGER_TIMEOUT;
          while (Date.now() < deadline) {
            const status = await chrome.runtime.sendMessage({ type: 'batch-status', batchId,
              itemId: batchId + '-' + step.index });
            if (status?.ok && status.ambiguous)
              return { detail: outcomeText(status) };
            if (status?.ok && status.matched && status.downloadId !== null) {
              if (!['safe', 'deepScannedSafe'].includes(status.danger))
                return { detail: 'Chrome safety review needed; ownership unresolved' };
              if (status.completed) return { ownership: 'chrome', verified: true, detail: 'Finished in Chrome' };
              if (status.nativeCompleted) return { ownership: 'native', verified: true, detail: 'Finished in VESPER' };
              if (status.status === 'download-observed') {
                // A temporal match needs the full window to rule out a second download before continuing.
                if (status.mode === 'next') await sleep(1500);
                const settled = await chrome.runtime.sendMessage({ type: 'batch-status', batchId, itemId: batchId + '-' + step.index });
                if (settled?.ambiguous) return { detail: outcomeText(settled) };
                return { effect: 'started-confirmed', verified: true, detail: 'Chrome created the intended download; final ownership pending' };
              }
              return { detail: 'Download interrupted; origin/ownership unresolved' };
            }
            if (batchController.signal.aborted)
              return { detail: 'Remaining actions cancelled; the already-triggered download may still start. Do not click again automatically.' };
            await sleep(400);
          }
          return { detail: 'No correlated Chrome download observed; do not click again automatically' };
        }
      };
      executor = brain.createBatchExecutor({ plan, adapter, journal });
      starting = false;
      start.disabled = choose.disabled = true; cancel.disabled = false;
      write('Approved batch started. An uncertain result stops remaining actions instead of clicking twice.');
      try {
        const states = await executor.run();
        for (const step of states) write(`${step.label}: ${step.state}${step.detail ? ' · ' + step.detail : ''}`);
        unresolvedBatch = states.some(step => ['action-recorded','reconciling','indeterminate'].includes(step.state));
        acknowledge.hidden = !unresolvedBatch;
        write(unresolvedBatch
          ? 'An action is unresolved. Check the download outcome before approving another batch.'
          : 'Batch finished. Submitted items may continue downloading in Chrome or VESPER.');
      } catch (error) { unresolvedBatch = true; acknowledge.hidden = false; write('Batch stopped: ' + String(error?.message || error)); }
      finally {
        cancel.disabled = true; executor = null; batchController = null;
        if (!closeRequested) update();
      }
    }
    section.addEventListener('keydown', event => {
      if (event.key === 'Escape' && !executor && !starting) {
        event.preventDefault(); event.stopPropagation();
        if (!controlChoices.hidden) {
          controlChoices.hidden = true; update(); choose.focus({ preventScroll: true });
        } else remove();
        return;
      }
      if (event.key !== 'Tab') return;
      const focusable = [...section.querySelectorAll('button:not(:disabled), input:not(:disabled), select:not(:disabled)')]
        .filter(control => !control.closest('[hidden]'));
      const position = focusable.indexOf(shadow.activeElement);
      if (event.shiftKey ? position <= 0 : position === focusable.length - 1) {
        event.preventDefault();
        (event.shiftKey ? focusable.at(-1) : focusable[0])?.focus();
      }
    });
    footer.append(close, choose, refresh, acknowledge, cancel, start);
    section.append(title, info, actionInfo, items, controlChoices, log, footer);
    shadow.append(css, section);
    document.documentElement.append(host);
    if (!choices.length) write('No selectable items found on this page.');
    update();
    (items.querySelector('input') || choose).focus({ preventScroll: true });
    void restorePrevious();
  }
  globalThis.VesperSmartBatch = Object.freeze({ open: show });
})();
