'use strict';

// What happened to a Chrome download, in plain words. The service worker stores one of these codes per
// download (browser-downloads.js, noteDecision); the popup shows the text and, where it helps, one thing to do.
// Actions: sign-in = turn on "Use site sign-in", download-now = stop queueing, check-app = ask VESPER if it
// is running, move = open the Chrome downloads page to move it by hand.
const VesperCaptureReasons = {
  'moved': { text: 'Moved to VESPER.' },
  'chrome-finished': { text: 'Chrome finished it before VESPER could take over. It’s listed in VESPER too.' },
  'sign-in': { text: 'This site needs your sign-in to download this file.', action: 'sign-in' },
  'different-signed-out': { text: 'The site offered VESPER something else than it gave Chrome. It may need your sign-in.', action: 'sign-in' },
  'different': { text: 'The site offered VESPER a different file than it gave Chrome.' },
  'site-refused': { text: 'The site refused VESPER even with your sign-in.' },
  'form': { text: 'The site made this file in reply to a form or a sign-in step. Only Chrome can repeat that.' },
  'page-made': { text: 'This file was put together inside the page, so only Chrome can save it.' },
  'compressed': { text: 'The site sent this file compressed, so VESPER couldn’t confirm it would get the same file.' },
  'no-size': { text: 'The site didn’t say how big this file is, so VESPER couldn’t confirm it would get the same file.' },
  'changed': { text: 'The file changed while VESPER was checking it.' },
  'unseen': { text: 'Chrome didn’t share how this download was requested, so VESPER left it alone.', action: 'move' },
  'queued': { text: 'VESPER is set to queue new downloads, so Chrome kept this one going.', action: 'download-now' },
  'busy': { text: 'VESPER was busy with other downloads.', action: 'move' },
  'slow': { text: 'The site took too long to answer VESPER.', action: 'move' },
  'not-ready': { text: 'VESPER couldn’t take over in time.', action: 'move' },
  'app-missing': { text: 'Chrome couldn’t find the VESPER app.', action: 'check-app' },
  'app-unavailable': { text: 'The VESPER app didn’t answer.', action: 'check-app' },
  'duplicate': { text: 'VESPER already has this download.' },
  'refused': { text: 'VESPER can’t download from this address.' }
};
