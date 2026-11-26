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
    const following = message.type === 'continue-managed';
    $('result').textContent = following ? 'Connecting to VESPER…' : 'Sending to VESPER…';
    try {
      const reply = await chrome.runtime.sendMessage(message);
      if (reply?.ok) {
        action.textContent = following ? 'Following' : 'Sent';
        $('result').textContent = reply.message || (following ? 'Chrome is downloading this file.' : 'Sent to VESPER.');
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
  const [state, chromeDownloads] = await Promise.all([
    chrome.runtime.sendMessage({ type: 'tab-state' }),
    chrome.runtime.sendMessage({ type: 'managed-downloads' })
  ]);
  const pending = state?.pending || [];
  const files = chromeDownloads?.files || [];
  $('chrome-active').hidden = files.length === 0;
  for (const file of files)
    row('chrome-files', file.name, 'Follow in VESPER', { type: 'continue-managed', id: file.id, startTime: file.startTime });
  $('unconfirmed').hidden = pending.length === 0;
  for (const entry of pending)
    row('pending', new Date(entry.at).toLocaleString(), 'Send again', { type: 'retry', id: entry.id });
  if (!pending.length && !files.length) $('result').textContent = 'No active Chrome downloads or unconfirmed requests.';
}
void load().catch(() => { $('result').textContent = 'The browser connection is unavailable.'; });
