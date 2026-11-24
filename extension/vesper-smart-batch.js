// Guided Smart Select workflow. Entirely opt-in; no page interaction until the user reviews the batch.
// Uses DOM facts + a tiny local semantic scorer, with deterministic action guards.
(() => {
  'use strict';
  const brain = globalThis.VesperIntelligence;
  if (!brain || globalThis.VesperSmartBatch) return;
  const MAX = 50;
  const TRIGGER_TIMEOUT = 75_000;
  const READY_TIMEOUT = 90_000;
  const IDENT = /^[A-Za-z0-9][\w.-]{0,100}$/;
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
    return clean(el.getAttribute('aria-label') || el.innerText || el.textContent || el.getAttribute('title'));
  }
  function rawItemId(el) {
    const explicit = [...new Set(['data-item-id', 'data-id', 'data-episode-id', 'data-post-id',
      'data-chapter-id', 'data-file-id'].map(attribute => idValue(el.getAttribute(attribute))).filter(Boolean))];
    if (explicit.length > 1) return '';
    const target = brain.http(el.getAttribute('href'), location.href);
    const queryIds = target?.origin === location.origin
      ? [...new Set([...target.searchParams].filter(([key]) => /^(?:item|item_id|episode|episode_id|post_id|chapter|chapter_id|file_id)$/i.test(key))
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
  function navKey(el) {
    const href = brain.http(el.getAttribute('href'), location.href);
    const id = rawItemId(el);
    const fingerprint = [el.tagName, id, href?.href || '', labelFor(el)].join('|');
    return id ? `id-${hash(fingerprint)}` : href ? `link-${hash(fingerprint)}` : `control-${hash(fingerprint)}`;
  }
  function collectNav() {
    const query = 'nav a[href], [role="tab"], [role="option"], [aria-controls], [data-item-id], [data-episode-id], [data-chapter-id], [data-file-id], a[href]';
    const elements = document.querySelectorAll(query);
    const seen = new Set(), ambiguous = new Set(), found = [];
    let inspected = 0;
    for (const element of elements) {
      if (++inspected > 500) break;
      if (dialog?.contains(element) || !shown(element) || element.disabled
          || element.getAttribute('aria-disabled') === 'true' || element.closest?.('form')) continue;
      const href = brain.http(element.getAttribute('href'), location.href);
      if (href && href.origin !== location.origin) continue;
      if (href && brain.sourceKind(href.href) !== 'unknown') continue;
      const label = labelFor(element);
      if (!label || label.length > 150) continue;
      const model = brain.proposeControl({ tagName: element.tagName, role: element.getAttribute('role'), label,
        href: href?.href || '', isNavigation: true });
      if (model.classification === 'download-likely' || /\b(log ?out|delete|remove|purchase|buy|subscribe)\b/i.test(label)) continue;
      const id = rawItemId(element);
      if (!id) continue; // No ambiguous or unidentifiable navigation items in the executable plan.
      const key = navKey(element);
      if (seen.has(key)) { ambiguous.add(key); continue; }
      seen.add(key);
      if (found.length < 120) found.push({ key, id, label, element });
    }
    return found.filter(item => !ambiguous.has(item.key));
  }
  const isActive = el => el?.getAttribute('aria-selected') === 'true'
    || ['page', 'true', 'step'].includes(el?.getAttribute('aria-current'))
    || el?.classList?.contains('active') || el?.classList?.contains('selected')
    || el?.getAttribute('data-active') === 'true';
  function relationshipFacts(control, item, intent) {
    const url = actionUrl(control);
    const bindings = ['data-item-id','data-id','data-episode-id','data-post-id','data-chapter-id','data-file-id']
      .map(attr => control.getAttribute(attr)).filter(Boolean);
    const query = url ? [...url.searchParams.entries()].filter(([key]) =>
      /^(?:item|item_id|episode|episode_id|post_id|chapter|chapter_id|file_id)$/i.test(key)).map(([, value]) => value) : [];
    const panelId = item.element.getAttribute('aria-controls');
    const panels = /^[\w.-]{1,100}$/.test(panelId || '') ? document.querySelectorAll('[id="' + panelId + '"]') : [];
    const panel = panels.length === 1 && panels[0].getAttribute('role') === 'tabpanel' ? panels[0] : null;
    const group = item.element.closest?.('article, li, [role="group"], [role="tabpanel"]');
    const candidatePanel = control.closest?.('[role="tabpanel"]');
    return { intent, explicitMatch: bindings.includes(item.id), queryMatch: query.includes(item.id),
      panelMatch: Boolean(panel?.contains(control)),
      pathMatch: Boolean(url && item.id.length >= 4 && url.pathname.split('/').some(part =>
        part === item.id || part.startsWith(item.id + '.') || part.endsWith('-' + item.id))),
      sameGroup: Boolean(group && group.contains(control)), active: isActive(item.element), hasUrl: !!url,
      structuralDownload: control.hasAttribute('download') || control.hasAttribute('data-download-url'),
      contradiction: [...bindings, ...query].some(value => value !== item.id),
      differentPanel: Boolean(panel && candidatePanel && candidatePanel !== panel),
      blocked: !eligibleAction(control) };
  }
  function suggestedAction(choices) {
    const active = choices.filter(item => isActive(item.element));
    const selected = active.length === 1 ? active[0] : null;
    const ranked = [];
    for (const element of [...document.querySelectorAll('button, a[href], [role="button"]')].slice(0, 240)) {
      if (!shown(element) || dialog?.contains(element)) continue;
      const url = actionUrl(element);
      const facts = {
        tagName: element.tagName, role: element.getAttribute('role'),
        label: labelFor(element), ariaLabel: element.getAttribute('aria-label'),
        title: element.getAttribute('title'), href: url?.href || '',
        downloadAttribute: element.hasAttribute('download'),
        hasDownloadUrl: element.hasAttribute('data-download-url'),
        stableBinding: Boolean(rawItemId(element)),
        isDisabled: element.disabled || element.getAttribute('aria-disabled') === 'true',
        isNavigation: Boolean(element.closest('nav, [role="navigation"]')) || element.getAttribute('role') === 'tab',
        isInlinePdf: Boolean(url && /\.pdf$/i.test(url.pathname) && !element.hasAttribute('download')),
        isFormSubmit: Boolean(element.form && element.type === 'submit'),
        sameOrigin: url?.origin === location.origin,
        targetBlank: element.getAttribute('target') === '_blank'
      };
      if (facts.isDisabled || facts.isNavigation || facts.isInlinePdf || facts.isFormSubmit
          || /\b(buy|purchase|subscribe|delete|remove|logout|log out|sign in)\b/i.test(facts.label)
          || (url && /\.(?:m4s|cmfv|cmfa|ts|aac|m3u8|mpd)$/i.test(url.pathname))) continue;
      const verdict = brain.proposeControl(facts);
      const probability = globalThis.VesperIntentModel?.probability(facts) ?? 0;
      const structural = facts.downloadAttribute || facts.hasDownloadUrl || Boolean(url && facts.stableBinding);
      const relationship = selected ? relationshipFacts(element, selected, probability) : null;
      if (relationship?.blocked || relationship?.contradiction || relationship?.differentPanel) continue;
      const related = relationship && (relationship.explicitMatch || relationship.queryMatch
        || relationship.panelMatch || relationship.pathMatch);
      if (verdict.classification !== 'download-likely' && !(structural && probability >= 0.85)
          && !(related && structural && probability >= 0.35)) continue;
      const relation = relationship ? globalThis.VesperIntentModel?.relationshipScore?.(relationship) ?? 0 : 0;
      ranked.push({ element, score: Math.max(verdict.score, structural && probability >= 0.85 || related && structural ? 6 : 0)
        + probability * 3 + Math.max(-8, Math.min(8, relation)) });
    }
    ranked.sort((a, b) => b.score - a.score);
    if (!ranked.length || ranked[0].score < 6 || ranked[1]?.score >= ranked[0].score - 2) return null;
    return ranked[0].element; // A suggestion for review, never permission to click.
  }
  function actionUrl(button) {
    if (!button) return null;
    // Placeholder anchors commonly invoke a real download through data attributes.
    // Contradictory concrete destinations cannot prove one approved action.
    const href = button.getAttribute('href');
    const claims = [href && !href.trim().startsWith('#') ? href : null,
      button.getAttribute('data-download-url'), button.getAttribute('data-url'), button.getAttribute('formaction')]
      .filter(Boolean).map(value => brain.http(value, location.href));
    if (claims.some(url => !url) || new Set(claims.map(url => url.href)).size !== 1) return null;
    return claims[0];
  }
  function actionKey(el) {
    if (!el) return '';
    // A countdown changes the display text, not the approved Download action.
    // Only normalize a recognized countdown suffix, never arbitrary changed labels.
    const label = labelFor(el).toLowerCase().replace(/\s*(?:in\s+\d{1,3}\s*(?:s|sec(?:onds?)?)|\(\s*\d{1,3}\s*(?:s|sec(?:onds?)?)\s*\))$/i, '').trim();
    return [el.tagName, clean(el.getAttribute('role')), idValue(el.id), label].join('|');
  }
  function matchingAction(key) {
    let match = null;
    for (const el of document.querySelectorAll('button, a[href], [role="button"], input[type="submit"]')) {
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
  function actionProvesItem(button, item, el) {
    if (!item.id || !button) return false;
    const bindings = ['data-item-id', 'data-id', 'data-episode-id', 'data-post-id',
      'data-chapter-id', 'data-file-id'].map(attr => button.getAttribute(attr)).filter(Boolean);
    if (bindings.some(value => value !== item.id)) return false;
    const link = actionUrl(button);
    // Every explicit item claim must agree, not just the first attribute.
    const queryBindings = link ? [...link.searchParams.entries()].filter(([key]) =>
      /^(?:item|item_id|episode|episode_id|post_id|chapter|chapter_id|file_id)$/i.test(key)) : [];
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
      // Some sites enable buttons using script properties or text-only updates.
      // One bounded fallback timer exists only during the approved readiness wait.
      const tick = () => {
        inspect();
        if (!finished) poll = setTimeout(tick, 250);
      };
      const observer = new MutationObserver(inspect);
      observer.observe(document.body || document.documentElement, { subtree: true, childList: true,
        characterData: true, attributes: true, attributeFilter: ['href', 'aria-selected', 'aria-current', 'class',
          'disabled', 'aria-disabled', 'hidden', 'style', 'data-active', 'data-download-url', 'data-url',
          'data-item-id', 'data-id', 'data-episode-id', 'data-post-id', 'data-chapter-id', 'data-file-id',
          'aria-controls', 'aria-labelledby', 'aria-hidden'] });
      signal?.addEventListener('abort', abort, { once: true });
      timer = setTimeout(() => finish(null), timeoutMs);
      tick();
    });
  }
  function eligibleAction(control) {
    if (!control || !shown(control) || control.disabled || control.getAttribute('aria-disabled') === 'true'
        || control.closest?.('nav, [role="navigation"]') || control.getAttribute('role') === 'tab'
        || control.form && control.type === 'submit'
        || /\b(buy|purchase|subscribe|delete|remove|logout|log out|sign in)\b/i.test(labelFor(control))) return false;
    const url = actionUrl(control);
    return !!url && !(brain.sourceKind(url.href) === 'fragment'
      || /\.pdf$/i.test(url.pathname) && !control.hasAttribute('download'));
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
    info.textContent = 'Choose navigation items, then point to their shared Download button. Review before VESPER clicks anything.';
    const items = document.createElement('div'); items.id = 'items';
    const log = document.createElement('div'); log.id = 'log'; log.setAttribute('role', 'status');
    const actionInfo = document.createElement('small');
    actionInfo.textContent = picked ? `Suggested Download control: ${labelFor(picked)}. Review or choose another.`
      : 'Shared Download button: not selected';
    const footer = document.createElement('footer');
    const close = button('Close', () => remove());
    const choose = button('Pick shared Download button', () => chooseControl());
    const start = button('Review and start', () => void startBatch(), 'primary');
    const cancel = button('Cancel remaining', () => { stopBatch(); write('Stopping remaining actions. Downloads already started are left alone.'); });
    const refresh = button('Refresh outcomes', () => void refreshResults());
    const acknowledge = button('I checked previous downloads', () => void acknowledgePrevious());
    acknowledge.hidden = true;
    cancel.disabled = true; refresh.disabled = true;
    const rows = [];
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
      row.append(check, text); items.append(row); rows.push({ choice, check });
    }
    function update() {
      start.disabled = !picked || !checked.size || !!executor || starting || unresolvedBatch;
      choose.disabled = !!executor || selectionMode || starting;
      info.textContent = `${checked.size} selected · ${picked ? 'Action: ' + labelFor(picked) : 'Choose the shared Download button'}`;
    }
    function chooseControl() {
      if (selectionMode) return;
      selectionMode = true; update();
      // Keep the extension overlay as the hit target throughout selection.
      // The site's actual button must never receive the physical selection click.
      // Looking through the overlay with elementsFromPoint is observation only.
      section.hidden = true;
      section.style.display = 'none';
      const previousBackground = host.style.background;
      host.style.background = '#0002';
      host.style.cursor = 'crosshair';
      const hint = document.createElement('div');
      hint.style.cssText = 'position:fixed;top:14px;left:50%;transform:translateX(-50%);background:#faf9f3;color:#283b30;border:1px solid #718776;border-radius:8px;padding:10px 14px;font:13px system-ui;pointer-events:none';
      hint.textContent = 'Choose the shared Download control · Esc to cancel';
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
          .map(el => el.closest?.('button, a[href], [role="button"], input[type="submit"]'))
          .find(el => el && shown(el) && !host.contains(el));
        if (!target) {
          hint.textContent = 'No available control here. Choose another or press Esc.';
          return;
        }
        picked = target;
        actionInfo.textContent = 'Selected: ' + labelFor(picked) + '. Review before starting.';
        finish();
      };
      const keydown = event => {
        if (event.key === 'Escape') { swallow(event); finish(); }
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
    async function refreshResults() {
      if (!activeBatchId || !lastApproved.length) return;
      for (const [index, item] of lastApproved.entries()) {
        const state = await chrome.runtime.sendMessage({ type: 'batch-status', batchId: activeBatchId,
          itemId: activeBatchId + '-' + index }).catch(() => null);
        if (!state?.ok) continue;
        const label = state.completed ? 'Chrome completed (VESPER interception miss)'
          : state.nativeCompleted ? 'VESPER completed; Chrome copy stayed empty'
          : state.status === 'download-observed' ? 'Download started; final owner pending'
          : state.status === 'ownership-unresolved' ? 'Ownership uncertain; inspect downloads'
          : state.status;
        write(`${item.label}: ${label}`);
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
      if (!picked || !key || !selected.length || !selected.every(choice => choice.id)
          || new Set(selected.map(choice => choice.id)).size !== selected.length) {
        write('Each selected item must have a different stable ID. Ambiguous items require manual review.'); return;
      }
      const approved = selected.map(choice => ({ key: choice.key, label: choice.label, id: choice.id,
        original: choice.element }));
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
          const previousActionUrl = actionUrl(matchingAction(key))?.href || '';
          const wasActive = isActive(el);
          if (!wasActive) el.click();
          return { exact: true, element: el, wasActive, previousActionUrl };
        },
        waitReady: async (step, selected) => {
          const item = approved[step.index];
          const value = await waitForChange(() => {
            if (location.origin !== plan.origin) return null;
            const el = selectedItem(item);
            const download = matchingAction(key);
            if (!el || !download || !isActive(el) || !shown(download)
                || !eligibleAction(download)
                || !actionProvesItem(download, item, el)) return null;
            const url = actionUrl(download);
            if (!url) return null;
            const itemBinding = ['data-item-id', 'data-id', 'data-episode-id', 'data-post-id', 'data-chapter-id', 'data-file-id']
              .map(attr => download.getAttribute(attr)).find(Boolean);
            if (!selected.wasActive && url.href === selected.previousActionUrl && itemBinding !== item.id) return null;
            return { verified: true, itemKey: step.key, actionKey: key, url: url.href, button: download };
          }, READY_TIMEOUT, batchController.signal);
          if (!value || batchController.signal.aborted) return { verified: false };
          const armed = await chrome.runtime.sendMessage({ type: 'batch-arm', batchId,
            itemId: batchId + '-' + step.index, expectedUrl: value.url });
          if (!armed?.ok) return { verified: false };
          return value;
        },
        disarm: async step => {
          await chrome.runtime.sendMessage({ type: 'batch-disarm', batchId,
            itemId: batchId + '-' + step.index }).catch(() => {});
        },
        trigger: async (step, evidence) => {
          if (location.origin !== plan.origin) throw new Error('Page origin changed before click');
          const current = matchingAction(key);
          const item = approved[step.index];
          const active = selectedItem(item);
          if (!active || !shown(active) || !isActive(active)
              || !current || current !== evidence.button || !shown(current)
              || !eligibleAction(current)
              || actionUrl(current)?.href !== evidence.url || !actionProvesItem(current, item, active))
            throw new Error('The item or Download button changed before click; no click sent');
          current.click();
        },
        reconcile: async step => {
          const deadline = Date.now() + TRIGGER_TIMEOUT;
          while (Date.now() < deadline) {
            const status = await chrome.runtime.sendMessage({ type: 'batch-status', batchId,
              itemId: batchId + '-' + step.index });
            if (status?.ok && status.matched && status.downloadId !== null) {
              if (!['safe', 'deepScannedSafe'].includes(status.danger))
                return { detail: 'Chrome safety review needed; ownership unresolved' };
              if (status.completed) return { ownership: 'chrome', verified: true, detail: 'Chrome finished (VESPER interception miss)' };
              if (status.nativeCompleted) return { ownership: 'native', verified: true, detail: 'VESPER completed; Chrome copy stayed empty' };
              if (status.status === 'download-observed')
                return { effect: 'started-confirmed', verified: true, detail: 'Chrome created the intended download; final ownership pending' };
              return { detail: 'Download interrupted; origin/ownership unresolved' };
            }
            if (batchController.signal.aborted)
              return { detail: 'Remaining actions cancelled; the already-triggered download may still start. Do not click again automatically.' };
            await sleep(400);
          }
          return { detail: 'No exact correlated Chrome download observed; do not click again automatically' };
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
    footer.append(close, choose, refresh, acknowledge, cancel, start);
    section.append(title, info, actionInfo, items, log, footer);
    shadow.append(css, section);
    document.documentElement.append(host);
    if (!choices.length) write('No selectable navigation items found on this page.');
    update();
    void restorePrevious();
  }
  globalThis.VesperSmartBatch = Object.freeze({ open: show });
})();
