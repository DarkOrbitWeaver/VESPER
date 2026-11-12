'use strict';

VesperTheme.install();
const $ = id => document.getElementById(id);

function row(list, name, label, message) {
  const item = document.createElement('li');
  const title = document.createElement('b');
  title.textContent = title.title = name;
  const action = document.createElement('button');
  action.className = 'vesper-button';
  action.textContent = label;
  action.onclick = async () => {
    action.disabled = true;
    $('result').textContent = 'Sending to VESPER…';
    try {
      const reply = await chrome.runtime.sendMessage(message);
      if (reply?.ok) {
        action.textContent = 'Sent';
        $('result').textContent = 'Downloading with VESPER.';
        return;
      }
      $('result').textContent = reply?.message || 'VESPER could not accept this file.';
    } catch { $('result').textContent = 'The browser connection is unavailable.'; }
    action.disabled = false;
  };
  item.append(title, action);
  $(list).append(item);
}

async function load() {
  const [active, state] = await Promise.all([
    chrome.runtime.sendMessage({ type: 'chrome-downloads' }), chrome.runtime.sendMessage({ type: 'tab-state' })
  ]);
  for (const download of active?.downloads || [])
    row('active', download.name, 'Move to VESPER', { type: 'move-download', downloadId: download.id });
  const pending = state?.pending || [];
  $('unconfirmed').hidden = pending.length === 0;
  for (const entry of pending)
    row('pending', new Date(entry.at).toLocaleString(), 'Send again', { type: 'retry', id: entry.id });
  if (!active?.downloads?.length && pending.length === 0) $('result').textContent = 'No active Chrome downloads.';
}
void load();
