'use strict';

// What happened to a Chrome download, in plain words. The service worker stores one of these codes per
// download (browser-downloads.js, noteDecision); the popup shows the text and, where it helps, one thing to do.
// Actions: sign-in = enable site sign-in, download-now = stop queueing, check-app = check native connection.
const VesperCaptureReasons = {
  'moved': { text: 'VESPER accepted the download before Chrome saved any of the file.' },
  'chrome-finished': { text: 'Chrome finished this file. It stays in Chrome, not VESPER.' },
  'sign-in': { text: 'This site needs your sign-in to download this file.', action: 'sign-in' },
  'different-signed-out': { text: 'The site offered VESPER something else than it gave Chrome. It may need your sign-in.', action: 'sign-in' },
  'different': { text: 'The site offered VESPER a different file than it gave Chrome.' },
  'site-refused': { text: 'The site refused VESPER even with your sign-in.' },
  'form': { text: 'The site made this file in reply to a form or a sign-in step. Only Chrome can repeat that.' },
  'page-made': { text: 'This file was put together inside the page, so only Chrome can save it.' },
  'compressed': { text: 'The site sent this file compressed, so VESPER couldn’t confirm it would get the same file.' },
  'no-size': { text: 'The site didn’t say how big this file is, so VESPER couldn’t confirm it would get the same file.' },
  'changed': { text: 'The file changed while VESPER was checking it.' },
  'unseen': { text: 'VESPER could not identify the request early enough. Chrome owns this download.' },
  'queued': { text: 'VESPER is set to queue new downloads, so Chrome kept this one going.', action: 'download-now' },
  'busy': { text: 'VESPER was busy. Chrome owns this download.' },
  'slow': { text: 'VESPER could not confirm the source in time. Chrome owns this download.' },
  'not-ready': { text: 'VESPER could not start early enough. Chrome owns this download.' },
  'app-missing': { text: 'Chrome couldn’t find the VESPER app.', action: 'check-app' },
  'app-unavailable': { text: 'The VESPER app didn’t answer.', action: 'check-app' },
  'capture-error': { text: 'VESPER could not verify this request. Chrome owns the download. Start a fresh VESPER download if needed.' },
  'chrome-started': { text: 'Chrome had already begun saving this file. VESPER left it alone.' },
  'duplicate': { text: 'VESPER already has this download.' },
  'refused': { text: 'VESPER can’t download from this address.' }
};
