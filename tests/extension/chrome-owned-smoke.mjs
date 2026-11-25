// Opt-in real Chrome check. Default mode never starts the native host; native mode requires an isolated registration and data directory.
// Run with Chrome for Testing or Chromium and set CHROME_PATH:
//   $env:CHROME_PATH = 'C:\\path\\to\\chrome-for-testing\\chrome-win64\\chrome.exe'
//   node tests/extension/chrome-owned-smoke.mjs
// The unpacked extension is loaded into the new profile through CDP Extensions.loadUnpacked.
// Add VESPER_NATIVE_HOST_TEST=1 only when the current account is disposable and the test host is registered there.
// Chrome always gets a fresh isolated profile. The default covers Chrome-owned and page-made files;
// native mode also needs an isolated registration in the current disposable account.
// VESPER_CAPTURE_RACE_TEST=1 compares response timing with real Chrome and a simulated helper.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { access, mkdtemp, mkdir, readFile, readdir, rm } from 'node:fs/promises';
import { dirname, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const extension = join(root, 'extension');
const chromeExe = process.env.CHROME_PATH;
const nativeHostTest = process.env.VESPER_NATIVE_HOST_TEST === '1';
const captureRaceTest = process.env.VESPER_CAPTURE_RACE_TEST === '1';
const playerRaceTest = process.env.VESPER_PLAYER_RACE_TEST === '1';
const nativeDownloadFolder = process.env.VESPER_TEST_DOWNLOAD_FOLDER
  ? resolve(process.env.VESPER_TEST_DOWNLOAD_FOLDER) : null;
const nativeDataDirectory = process.env.VESPER_TEST_DATA_DIRECTORY
  ? resolve(process.env.VESPER_TEST_DATA_DIRECTORY) : null;
const expectedExtensionId = process.env.VESPER_EXPECTED_EXTENSION_ID;
const profileRoot = nativeHostTest && process.env.VESPER_TEST_PROFILE_ROOT
  ? resolve(process.env.VESPER_TEST_PROFILE_ROOT) : root;
const manifest = JSON.parse(await readFile(join(extension, 'manifest.json'), 'utf8'));
const digest = createHash('sha256').update(Buffer.from(manifest.key, 'base64')).digest('hex').slice(0, 32);
const extensionId = [...digest].map(digit => String.fromCharCode(97 + parseInt(digit, 16))).join('');
const nativeFileName = 'vesper-native-capture-fixture.bin';
const refusalFileName = 'vesper-native-refusal-fixture.bin';
const signInFileName = 'vesper-native-signin-fixture.bin';
const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

function installRaceBridge() {
  const smoke = globalThis.__ownershipSmoke;
  smoke.raceCalls = [];
  smoke.managed = [];
  const simulate = (_host, message, callback) => {
    if (message.type === 'browser-managed') {
      smoke.managed.push(message);
      callback({ v: 1, id: message.id, ok: true });
      return;
    }
    smoke.raceCalls.push({ type: message.type, operation: message.operation, id: message.id });
    const reply = fields => callback({ v: 1, id: message.id, ok: true, ...fields });
    if (message.type === 'capture-preflight')
      return reply({ data: { ready: true, siteRules: { neverCaptureHosts: [] } } });
    if (message.type === 'hello')
      return setTimeout(() => reply({ data: { siteRules: { neverCaptureHosts: [] } } }), 650);
    if (message.type === 'browser-capture' || message.type === 'browser-submit')
      return setTimeout(() => reply({ results: [{ status: 'started', transferReady: true }] }), 400);
    if (message.type === 'browser-transfer')
      return reply({ data: { state: 'failed' } });
    throw new Error('Unexpected native request in capture race test: ' + message.type);
  };
  chrome.runtime.sendNativeMessage = simulate;
  chrome.runtime.connectNative = () => {
    let messageListener, disconnected, closed = false;
    return {
      onMessage: { addListener(listener) { messageListener = listener; } },
      onDisconnect: { addListener(listener) { disconnected = listener; } },
      disconnect() { closed = true; disconnected?.(); },
      postMessage(message) {
        if (message.type === 'session-open') {
          setTimeout(() => { if (!closed) messageListener({ok:true,id:message.id,data:{mux:true}}); }, 0);
          return;
        }
        if (message.type === 'browser-managed' && message.operation === 'watch') {
          smoke.managed.push(message);
          setTimeout(() => {
            if (!closed) messageListener({ ok: true, id:message.id, data: { managedDownloads: true, siteRules: { neverCaptureHosts: [] } } });
          }, 40);
          return;
        }
        simulate(null, message, reply => { if (!closed) messageListener(reply); });
      }
    };
  };
  if (chrome.runtime.sendNativeMessage !== simulate) throw new Error('Could not install the simulated native helper.');
}

class Connection {
  constructor(socket) {
    this.socket = socket;
    this.nextId = 0;
    this.pending = new Map();
    this.targetFailures = [];
    socket.addEventListener('message', event => {
      const message = JSON.parse(event.data);
      if (message.method === 'Target.targetCrashed' || message.method === 'Inspector.targetCrashed')
        this.targetFailures.push(message);
      const waiting = this.pending.get(message.id);
      if (!waiting) return;
      this.pending.delete(message.id);
      clearTimeout(waiting.timeout);
      if (message.error) waiting.reject(new Error(message.error.message));
      else waiting.resolve(message.result || {});
    });
  }

  static async open(address) {
    const socket = new WebSocket(address);
    await new Promise((resolve, reject) => {
      socket.addEventListener('open', resolve, { once: true });
      socket.addEventListener('error', event => reject(new Error(
        'Cannot open the isolated Chrome debugger: ' + (event.message || 'connection failed'))), { once: true });
    });
    return new Connection(socket);
  }

  send(method, params = {}, sessionId) {
    const id = ++this.nextId;
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.pending.delete(id);
        const failures = this.targetFailures.length ? ' ' + JSON.stringify(this.targetFailures) : '';
        reject(new Error('Chrome did not answer: ' + method + failures));
      }, 8000);
      this.pending.set(id, { resolve, reject, timeout });
      this.socket.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
    });
  }

  close() {
    this.socket.close();
    for (const waiting of this.pending.values()) {
      clearTimeout(waiting.timeout);
      waiting.reject(new Error('Chrome test ended.'));
    }
    this.pending.clear();
  }
}

async function waitFor(fn, timeoutMs, reason) {
  const until = Date.now() + timeoutMs;
  while (Date.now() < until) {
    const value = await fn();
    if (value) return value;
    await delay(100);
  }
  throw new Error(reason);
}

async function main() {
  assert.ok(!(nativeHostTest && (captureRaceTest || playerRaceTest)), 'Run the simulated race and registered helper separately.');
  if (nativeHostTest) {
    assert.equal(process.platform, 'win32', 'Native host acceptance requires Windows.');
    assert.ok(nativeDownloadFolder, 'VESPER_TEST_DOWNLOAD_FOLDER must point to the disposable output folder.');
    assert.ok(nativeDataDirectory, 'VESPER_TEST_DATA_DIRECTORY must point to the disposable VESPER data folder.');
    assert.equal(extensionId, expectedExtensionId, 'The test registration must allow the current extension ID.');
    const evidenceRoot = resolve(root, 'release', 'audit-evidence') + sep;
    for (const [name, path] of [['download folder', nativeDownloadFolder], ['data directory', nativeDataDirectory], ['profile root', profileRoot]]) {
      assert.ok(path.startsWith(evidenceRoot), `The native test ${name} must stay under release/audit-evidence.`);
    }
    await access(nativeDownloadFolder);
  }
  await access(chromeExe);
  const playerClip = await readFile(join(root, 'tests/Vesper.Tests/Fixtures/Media/muxed.webm'));
  const mseClip = await readFile(join(root, 'tests/Vesper.Tests/Fixtures/Media/instinct-mse.mp4'));
  const profile = await mkdtemp(join(profileRoot, '.chrome-ownership-smoke-'));
  const downloadFolder = join(profile, 'downloads');
  await mkdir(downloadFolder);
  const bytes = Buffer.alloc(256 * 1024, 0x5a);
  const serverRequests = [];
  const server = createServer((request, response) => {
    const browserRequest = request.headers['sec-fetch-mode'] === 'navigate';
    serverRequests.push({
      method: request.method,
      path: request.url,
      ranged: !!request.headers.range,
      pageRequest: browserRequest,
      cookiePresent: typeof request.headers.cookie === 'string',
      authorizationPresent: typeof request.headers.authorization === 'string'
    });
    if (request.url === '/page') {
      response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      response.end('<!doctype html><title>VESPER smoke fixture</title><body></body>');
      return;
    }
    if (captureRaceTest && request.url === '/single-post') {
      if (request.method !== 'POST' || serverRequests.filter(entry => entry.path === '/single-post').length !== 1) {
        response.writeHead(403).end();
        return;
      }
      response.writeHead(200, {
        'Content-Type': 'application/octet-stream',
        'Content-Disposition': 'attachment; filename="vesper-single-post.bin"',
        'Content-Length': bytes.length,
        'Cache-Control': 'no-store'
      });
      response.flushHeaders();
      setTimeout(() => response.end(bytes), 600);
      return;
    }
    if (request.url === '/mse-feed') {
      response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      response.end(`<!doctype html><title>MSE feed fixture</title>
        <video id="first" width="320" height="180" muted controls></video>
        <video id="second" width="320" height="180" muted controls></video>
        <script>
        window.fixture = { ready: false, errors: [] };
        function xhrBuffer(path) {
          return new Promise((resolve,reject) => {
            const request = new XMLHttpRequest();
            request.open('GET', path); request.responseType = 'arraybuffer';
            request.onload = () => request.status === 200 ? resolve(request.response) : reject(new Error('XHR failed'));
            request.onerror = reject; request.send();
          });
        }
        async function load(player, path, useXhr = false) {
          const media = new MediaSource();
          player.src = URL.createObjectURL(media);
          await new Promise(resolve => media.addEventListener('sourceopen', resolve, {once:true}));
          const track = media.addSourceBuffer('video/mp4; codecs="avc1.42c00a,mp4a.40.2"');
          const bytes = useXhr ? await xhrBuffer(path) : await (await fetch(path)).arrayBuffer();
          await new Promise((resolve,reject) => {
            track.addEventListener('updateend', resolve, {once:true});
            track.addEventListener('error', () => reject(new Error('append failed')), {once:true});
            track.appendBuffer(bytes);
          });
          media.endOfStream();
        }
        Promise.all([load(first,'/asset-100001/clip.mp4?session=fixture-one'),
          load(second,'/asset-200002/clip.mp4?session=fixture-two',true)])
          .then(() => fixture.ready = true, e => fixture.errors.push(String(e)));
        </script>`);
      return;
    }
    if (/^\/asset-(?:100001|200002)\/clip\.mp4\?session=fixture-(?:one|two)$/.test(request.url)) {
      response.writeHead(200, { 'Content-Type': 'video/mp4', 'Content-Length': mseClip.length, 'Cache-Control': 'no-store' });
      response.end(mseClip);
      return;
    }
    if (playerRaceTest && request.url === '/recycled-player') {
      response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      response.end('<!doctype html><title>Recycled player fixture</title><article id="card" data-permalink="/posts/first">'
        + '<video width="640" height="360" src="/player.webm" muted controls preload="auto"></video></article>');
      return;
    }
    if (request.url === '/player') {
      response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      response.end('<!doctype html><title>VESPER player fixture</title><div id="player">'
        + '<video width="640" height="360" src="/player.webm" autoplay muted loop controls></video>'
        + '</div><style>#player:fullscreen video, #player.expanded video{width:100%;height:100%}'
        + '#player.expanded{position:fixed;inset:0}</style>');
      return;
    }
    if (request.url.startsWith('/intent-feed?')) {
      const variant = Number(new URL(request.url, 'http://fixture.invalid').searchParams.get('case')) || 0;
      const reversed = variant % 2 === 1;
      const small = variant < 4 ? 260 : 300;
      const big = variant < 4 ? 360 : 340;
      const main = '<video id="main-media" width="' + small + '" height="160" src="/player.webm" autoplay muted controls></video>';
      const loop = '<video id="incidental-media" width="' + big + '" height="210" src="/player.webm" autoplay muted loop></video>';
      response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      response.end('<!doctype html><title>Intent fixture</title><style>body{margin:12px;display:flex;align-items:flex-start;gap:16px}video{object-fit:contain}</style>'
        + (reversed ? loop + main : main + loop));
      return;
    }
    if (request.url === '/player.webm' || request.url.startsWith('/player.webm?')) {
      response.writeHead(200, { 'Content-Type': 'video/webm', 'Content-Length': playerClip.length });
      response.end(playerClip);
      return;
    }
    const raceBodyDelay = { '/race-fast': 0, '/race-during-check': 200, '/race-policy-window': 800, '/race-delayed': 1600 }[request.url];
    if (captureRaceTest && Number.isInteger(raceBodyDelay)) {
      const headers = {
        'Content-Type': 'application/octet-stream',
        'Content-Disposition': `attachment; filename="vesper${request.url.replace('/', '-')}.bin"`,
        'ETag': '"vesper-capture-race-v1"',
        'X-Content-Type-Options': 'nosniff',
        'Cache-Control': 'no-store'
      };
      // Advertise resumability only on the deliberately delayed fixture and
      // actually implement range responses for it. Others test the late path.
      if (request.url === '/race-delayed') {
        headers['Accept-Ranges'] = 'bytes';
        const range = /^bytes=(\d+)-(\d*)$/.exec(request.headers.range || '');
        if (range) {
          const start = Number(range[1]), end = range[2] ? Math.min(Number(range[2]), bytes.length - 1) : bytes.length - 1;
          if (start > end || start >= bytes.length) {
            response.writeHead(416, {'Content-Range': 'bytes */' + bytes.length}).end();
            return;
          }
          response.writeHead(206, {...headers, 'Content-Range': `bytes ${start}-${end}/${bytes.length}`, 'Content-Length': end-start+1});
          response.end(bytes.subarray(start,end+1));
          return;
        }
      }
      response.writeHead(200, {...headers, 'Content-Length': bytes.length});
      response.flushHeaders();
      if (raceBodyDelay === 0) response.end(bytes);
      else setTimeout(() => response.end(bytes), raceBodyDelay);
      return;
    }
    if (nativeHostTest && request.url === '/native-redirect') {
      response.writeHead(302, { Location: '/native-capture', 'Cache-Control': 'no-store' });
      response.end();
      return;
    }
    if (nativeHostTest && request.url === '/native-refusal-redirect') {
      response.writeHead(302, { Location: '/native-refusal', 'Cache-Control': 'no-store' });
      response.end();
      return;
    }
    if (nativeHostTest && request.url === '/native-signin-redirect') {
      response.writeHead(302, {
        Location: '/native-signin-protected',
        'Set-Cookie': 'vesper_fixture_session=browser-only; Path=/; SameSite=Lax',
        'Cache-Control': 'no-store'
      });
      response.end();
      return;
    }
    if (nativeHostTest && request.url === '/native-signin-protected'
        && !request.headers.cookie?.split(';').some(value => value.trim() === 'vesper_fixture_session=browser-only')) {
      response.writeHead(403, { 'Content-Type': 'text/plain', 'Cache-Control': 'no-store' });
      response.end('A browser sign-in cookie is required.');
      return;
    }
    if (nativeHostTest && ['/native-capture', '/native-refusal', '/native-signin-protected'].includes(request.url)) {
      const fileName = request.url === '/native-refusal' ? refusalFileName
        : request.url === '/native-signin-protected' ? signInFileName : nativeFileName;
      const etag = request.url === '/native-refusal' && !browserRequest
        ? '"vesper-native-refusal-changed-v1"'
        : request.url === '/native-refusal' ? '"vesper-native-refusal-chrome-v1"' : '"vesper-native-capture-v1"';
      const headers = {
        'Content-Type': 'application/octet-stream',
        'Content-Disposition': `attachment; filename="${fileName}"`,
        'Content-Length': bytes.length,
        'ETag': etag,
        'Cache-Control': 'no-store'
      };
      if (request.method === 'HEAD') {
        response.writeHead(200, headers);
        response.end();
        return;
      }
      if (request.headers.range) {
        const match = /^bytes=(\d+)-(\d*)$/.exec(request.headers.range);
        if (!match) {
          response.writeHead(416, { 'Content-Range': `bytes */${bytes.length}` });
          response.end();
          return;
        }
        const start = Number(match[1]);
        const end = match[2] ? Math.min(Number(match[2]), bytes.length - 1) : bytes.length - 1;
        if (start >= bytes.length || end < start) {
          response.writeHead(416, { 'Content-Range': `bytes */${bytes.length}` });
          response.end();
          return;
        }
        response.writeHead(206, {
          ...headers,
          'Content-Length': end - start + 1,
          'Content-Range': `bytes ${start}-${end}/${bytes.length}`
        });
        response.end(bytes.subarray(start, end + 1));
        return;
      }
      response.writeHead(200, headers);
      setTimeout(() => response.end(bytes), browserRequest ? 10000 : 0);
      return;
    }
    if (request.url !== '/fixture') {
      response.writeHead(404);
      response.end();
      return;
    }
    response.writeHead(200, {
      'Content-Type': 'application/octet-stream',
      'Content-Disposition': 'attachment; filename="vesper-browser-owned-fixture.bin"',
      'Content-Length': bytes.length,
      'Cache-Control': 'no-store'
    });
    let offset = 0;
    function next() {
      if (response.destroyed) return;
      if (offset >= bytes.length) { response.end(); return; }
      response.write(bytes.subarray(offset, offset + 16 * 1024));
      offset += 16 * 1024;
      setTimeout(next, 35);
    }
    next();
  });
  let browser;
  let chrome;
  try {
    await new Promise((resolve, reject) => {
      server.once('error', reject);
      server.listen(0, '127.0.0.1', resolve);
    });
    const port = server.address().port;
    chrome = spawn(chromeExe, [
      '--headless=new',
      '--enable-unsafe-extension-debugging',
      '--no-first-run',
      '--no-default-browser-check',
      '--disable-background-networking',
      '--disable-default-apps',
      '--user-data-dir=' + profile,
      '--remote-debugging-port=0',
      '--remote-allow-origins=*',
      'about:blank'
    ], { stdio: 'ignore', windowsHide: true });

    const activePort = await waitFor(async () => {
      try { return (await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).trim(); }
      catch { return null; }
    }, 12000, 'Isolated Chrome did not enable remote debugging.');
    const [debugPort, path] = activePort.split(/\r?\n/);
    browser = await Connection.open('ws://127.0.0.1:' + debugPort + path);

    const loadedExtension = await browser.send('Extensions.loadUnpacked', { path: extension });
    assert.equal(loadedExtension.id, extensionId, 'Chrome loaded a different extension than the registered VESPER origin.');

    if (process.env.VESPER_CFT_LOAD_ONLY === '1') {
      process.stdout.write(JSON.stringify({
        ok: true,
        browser: 'Chrome for Testing (isolated profile)',
        extensionId: loadedExtension.id,
        nativeHostStarted: false,
        workerChecked: false
      }) + '\n');
      return;
    }

    // Trigger the unpacked extension without asking native messaging to launch an installed VESPER.
    await browser.send('Target.createTarget', { url: 'chrome-extension://' + extensionId + '/transfers.html' });
    let targetsSeen = [];
    let worker;
    try {
      worker = await waitFor(async () => {
        const targets = (await browser.send('Target.getTargets')).targetInfos;
        targetsSeen = targets.map(target => ({ type: target.type, url: target.url }));
        return targets.find(target => target.type === 'service_worker'
          && target.url.startsWith('chrome-extension://' + extensionId + '/'));
      }, 10000, 'Chrome did not start the unpacked extension service worker.');
    }
    catch (error) {
      process.stderr.write('Expected extension ID: ' + extensionId + '\n');
      process.stderr.write('Observed Chrome targets: ' + JSON.stringify(targetsSeen) + '\n');
      throw error;
    }
    const workerSession = (await browser.send('Target.attachToTarget',
      { targetId: worker.targetId, flatten: true })).sessionId;
    await browser.send('Runtime.enable', {}, workerSession);

    async function evaluate(label, expression) {
      const reply = await browser.send('Runtime.evaluate',
        { expression, awaitPromise: true, returnByValue: true }, workerSession)
        .catch(error => { throw new Error(label + ': ' + error.message, { cause: error }); });
      if (reply.exceptionDetails) throw new Error(label + ': ' + reply.exceptionDetails.text);
      return reply.result.value;
    }


    await waitFor(() => evaluate('Wait for extension APIs', 'typeof chrome !== "undefined" && !!chrome.storage?.sync && typeof playerOptions === "function"'),
      8000, 'The extension worker did not initialize its Chrome APIs.');
    const messagingHook = captureRaceTest ? '(' + installRaceBridge.toString() + ')();' : nativeHostTest
      ? "const sendNative = chrome.runtime.sendNativeMessage.bind(chrome.runtime);"
        + "const wrapped = (host, message, callback) => { __ownershipSmoke.nativeCalls++;"
        + "const entry = { host, type: message?.type, operation: message?.operation, id: message?.id,"
        + "hasCredentials: !!message?.authorized || !!message?.items?.some(item => item.cookie || item.authorization),"
        + "message: structuredClone(message), response: null };"
        + "__ownershipSmoke.nativeMessages.push(entry);"
        + "return sendNative(host, message, response => { entry.response = response ? { ok: response.ok, code: response.code,"
        + "results: response.results?.map(result => ({ status: result.status, jobId: result.jobId,"
        + "transferReady: result.transferReady, transferReason: result.transferReason })) } : null;"
        + "if (callback) callback(response); }); };"
        + "chrome.runtime.sendNativeMessage = wrapped;"
        + "if (chrome.runtime.sendNativeMessage !== wrapped) throw new Error('Could not observe native host calls');"
      : "const guard = () => { __ownershipSmoke.nativeCalls++; throw new Error('Native calls are disabled in this test'); };"
        + "chrome.runtime.sendNativeMessage = guard;"
        + "if (chrome.runtime.sendNativeMessage !== guard || chrome.runtime.connectNative !== undefined) throw new Error('Could not guard the installed native host');";
    await evaluate("Configure Chrome ownership", "(async () => { await chrome.storage.sync.set({ intercept: false, route: 'chrome' });"
      + "globalThis.__ownershipSmoke = { created: [], changed: [], nativeCalls: 0, nativeMessages: [] };"
      + (captureRaceTest ? '' : 'chrome.runtime.connectNative = undefined;')
      + messagingHook
      + "chrome.downloads.onCreated.addListener(d => __ownershipSmoke.created.push("
      + "{ id:d.id, state:d.state, bytesReceived:d.bytesReceived, totalBytes:d.totalBytes, name:d.filename?.split(/[\\\\/]/).pop() }));"
      + "chrome.downloads.onChanged.addListener(d => __ownershipSmoke.changed.push("
      + "{ id:d.id, state:d.state?.current, bytesReceived:d.bytesReceived?.current }));"
      + "return true; })()");

    await browser.send('Browser.setDownloadBehavior', {
      behavior: 'allow', downloadPath: downloadFolder, eventsEnabled: true
    });
    const page = (await browser.send('Target.createTarget', { url: 'about:blank' })).targetId;
    const tab = (await browser.send('Target.attachToTarget',
      { targetId: page, flatten: true })).sessionId;
    await browser.send('Page.enable', {}, tab);
    await browser.send('Page.navigate', { url: 'http://127.0.0.1:' + port + '/fixture' }, tab);

    const item = await waitFor(async () => {
      const matches = await evaluate("Read Chrome downloads", "chrome.downloads.search({})");
      return matches.find(candidate => candidate.filename?.endsWith('vesper-browser-owned-fixture.bin')
        && candidate.state === 'complete');
    }, 15000, 'Chrome did not finish the isolated fixture download.');
    const observations = await evaluate('Read Chrome events', 'globalThis.__ownershipSmoke');
    const saved = await readFile(join(downloadFolder, 'vesper-browser-owned-fixture.bin'));
    assert.equal(saved.compare(bytes), 0, 'Chrome did not save the source bytes exactly.');
    assert.equal(observations.created.length, 1, 'The extension missed or duplicated the Chrome download event.');
    assert.equal(observations.created[0].id, item.id);
    assert.equal(item.state, 'complete');
    assert.ok(item.filename.startsWith(downloadFolder), 'The download escaped the isolated test folder.');
    const decisions = await evaluate("Read VESPER decisions", "chrome.storage.session.get('vesperCaptureDecisions')");
    assert.ok(!decisions.vesperCaptureDecisions?.some(entry => entry.outcome === 'moved'),
      'A Chrome-owned file was falsely marked as a VESPER download.');

    // With interception enabled, a page-generated download must still stay in Chrome.
    await evaluate('Enable automatic capture', "chrome.storage.sync.set({ intercept: true, route: 'auto' })");
    const blobName = 'vesper-page-made-fixture.bin';
    await browser.send('Page.navigate', { url: 'http://127.0.0.1:' + port + '/page' }, tab);
    await waitFor(async () => {
      const result = await browser.send('Runtime.evaluate',
        { expression: "document.title === 'VESPER smoke fixture' && !!document.body", returnByValue: true }, tab);
      return result.result.value;
    }, 5000, 'The isolated fixture page did not load.');
    const generated = await browser.send('Runtime.evaluate', { expression:
      "(() => { const link = document.createElement('a');"
      + "link.href = URL.createObjectURL(new Blob([new Uint8Array(262144).fill(90)]));"
      + "link.download = 'vesper-page-made-fixture.bin'; document.body.append(link); link.click(); URL.revokeObjectURL(link.href); })()"
    }, tab);
    assert.ok(!generated.exceptionDetails, 'Could not create the isolated Blob download.');
    await waitFor(async () => {
      const matches = await evaluate('Read page-made download', 'chrome.downloads.search({})');
      return matches.find(candidate => candidate.filename?.endsWith(blobName) && candidate.state === 'complete');
    }, 15000, 'Chrome did not finish the page-generated download.');
    assert.equal((await readFile(join(downloadFolder, blobName))).compare(bytes), 0);
    await waitFor(async () => {
      const capture = await evaluate('Read final capture decisions', "chrome.storage.session.get('vesperCaptureDecisions')");
      return capture.vesperCaptureDecisions?.some(entry => entry.outcome === 'page-made');
    }, 5000, 'The extension did not record that Chrome owns the page-generated download.');
    let captureRace = null;
    let managedDownloads = null;
    if (captureRaceTest) {
      await waitFor(async () => {
        const reports = await evaluate('Read blob completion', '__ownershipSmoke.managed');
        return reports.some(item => item.operation === 'complete' && item.path.endsWith(blobName));
      }, 5000, 'VESPER did not receive the completed blob.');
      await browser.send('Runtime.evaluate', { expression:
        "(() => { const form = document.createElement('form'); form.method = 'POST'; form.action = '/single-post';"
        + "document.body.append(form); form.submit(); })()"
      }, tab);
      const post = await waitFor(async () => {
        const reports = await evaluate('Read POST completion', '__ownershipSmoke.managed');
        return reports.find(item => item.operation === 'complete' && item.path.endsWith('vesper-single-post.bin'));
      }, 5000, 'VESPER did not receive the completed one-use POST.');
      assert.ok(post.path.startsWith(downloadFolder));
      assert.equal((await readFile(post.path)).compare(bytes), 0);
      assert.deepEqual(serverRequests.filter(entry => entry.path === '/single-post').map(entry => entry.method), ['POST']);
      managedDownloads = { blob: true, oneUsePost: true };
      captureRace = [];
      for (const fixture of ['fast', 'during-check', 'policy-window', 'delayed']) {
        const name = `vesper-race-${fixture}.bin`;
        const before = await evaluate('Read race call count', '__ownershipSmoke.raceCalls.length');
        await browser.send('Page.navigate', { url: 'http://127.0.0.1:' + port + '/race-' + fixture }, tab);
        const file = await waitFor(async () => {
          const matches = await evaluate('Read fixture download', 'chrome.downloads.search({})');
          return matches.find(candidate => candidate.filename?.endsWith(name) && candidate.state === 'complete');
        }, 15000, 'Chrome did not save the ' + fixture + ' race fixture.');
        assert.ok(file.filename.startsWith(downloadFolder), 'The race fixture escaped the isolated test folder.');
        assert.equal((await readFile(file.filename)).compare(bytes), 0);
        const readCalls = () => evaluate('Read race calls', `__ownershipSmoke.raceCalls.slice(${before})`);
        const calls = await waitFor(async () => {
          const calls = await readCalls();
          const pending = calls.some(call => call.type === 'browser-capture');
          return !pending || calls.some(call => call.operation === 'abandon') ? calls : null;
        }, 5000, 'The extension did not release the simulated transfer for the ' + fixture + ' fixture.');
        const attempted = calls.some(call => call.type === 'browser-capture');
        const reachedAccept = calls.some(call => call.operation === 'accept');
        captureRace.push({ fixture, attempted, reachedAccept, bytes: file.fileSize });
        assert.equal(calls.some(call => call.type === 'hello'), false, 'Unsigned capture sent a separate policy request.');
        // Scheduling differs on real machines. The deliberately slow, resumable
        // fixture must exercise acceptance; fast cases may finish before it.
        if (fixture === 'delayed') assert.ok(attempted && reachedAccept, 'The slow fixture did not exercise native acceptance.');
        const decisions = await evaluate('Read capture decisions', "chrome.storage.session.get('vesperCaptureDecisions')");
        assert.ok(!decisions.vesperCaptureDecisions?.some(entry => entry.name === name && entry.outcome === 'moved'),
          'A simulated failed handoff was falsely credited as native completion.');
      }
      const preflights = await evaluate('Read capture preflights',
        "__ownershipSmoke.raceCalls.filter(call => call.type === 'capture-preflight')");
      assert.ok(preflights.length >= 1 && preflights.length <= 4, 'Preflight rate should remain bounded across four fixtures.');
    }
    let nativeCapture = null;
    if (nativeHostTest) {
      assert.equal(await evaluate('Check no native calls before the valid file', '__ownershipSmoke.nativeCalls'), 0,
        'Chrome-owned and page-generated downloads must not call the native host.');

      const hello = await evaluate('Call registered native host',
        "new Promise((resolve, reject) => chrome.runtime.sendNativeMessage('com.vesper.downloader',"
        + "{ v: 1, id: 'chrome-native-host-smoke-hello', type: 'hello' }, response => {"
        + "const error = chrome.runtime.lastError; if (error) reject(new Error(error.message)); else resolve(response); }))");
      assert.equal(hello?.ok, true, 'The registered native host did not answer its hello request.');
      assert.equal(hello?.data?.app, 'VESPER');

      await browser.send('Page.navigate', { url: 'http://127.0.0.1:' + port + '/native-redirect' }, tab);
      const created = await waitFor(async () => {
        const events = await evaluate('Read native capture creation', 'globalThis.__ownershipSmoke.created');
        return events.find(event => event.name === nativeFileName);
      }, 8000, 'Chrome did not create the redirecting native-capture download.');
      assert.equal(created.bytesReceived, 0, 'Chrome must not own received bytes before VESPER accepts the transfer.');

      const capturedPath = join(nativeDownloadFolder, nativeFileName);
      const capturedBytes = await waitFor(async () => {
        try {
          const file = await readFile(capturedPath);
          return file.compare(bytes) === 0 ? file : null;
        } catch { return null; }
      }, 25000, 'VESPER did not publish the accepted Chrome transfer in the isolated output folder.');
      const decisions = await waitFor(async () => {
        const state = await evaluate('Read native capture decisions', "chrome.storage.session.get('vesperCaptureDecisions')");
        return state.vesperCaptureDecisions?.find(entry => entry.name === nativeFileName && entry.outcome === 'moved');
      }, 8000, 'The extension did not record the accepted VESPER handoff.');
      const chromeFiles = await readdir(downloadFolder);
      assert.equal(chromeFiles.some(name => name.startsWith(nativeFileName)), false,
        'The accepted file must not remain in Chrome’s download folder.');
      const finalState = await evaluate('Read native handoff observations', 'globalThis.__ownershipSmoke');
      assert.ok(finalState.nativeMessages.some(message => message.type === 'browser-capture'));
      assert.ok(finalState.nativeMessages.some(message => message.type === 'browser-transfer' && message.operation === 'accept'));

      const originalSubmission = finalState.nativeMessages.find(message => message.type === 'browser-capture');
      assert.ok(originalSubmission?.response?.ok, 'The captured download did not receive a native response.');
      const duplicate = await evaluate('Repeat the same native submission',
        "(async () => { const original = __ownershipSmoke.nativeMessages.find(message => message.type === 'browser-capture');"
        + "if (!original) throw new Error('The original submission was not observed');"
        + "return await new Promise((resolve, reject) => chrome.runtime.sendNativeMessage('com.vesper.downloader',"
        + "structuredClone(original.message), response => { const error = chrome.runtime.lastError;"
        + "if (error) reject(new Error(error.message)); else resolve(response); })); })()");
      assert.equal(duplicate?.ok, true, 'The registered host rejected an identical browser submission.');
      assert.equal(duplicate?.results?.[0]?.status, 'already-submitted',
        'An identical native submission must resolve to the original VESPER job.');
      assert.equal(duplicate?.results?.[0]?.jobId, originalSubmission.response.results?.[0]?.jobId,
        'Duplicate submission created or returned a different VESPER job.');
      assert.equal(duplicate?.results?.[0]?.transferReady, true,
        'The duplicate response lost the original transfer readiness.');

      await browser.send('Page.navigate', { url: 'http://127.0.0.1:' + port + '/native-refusal-redirect' }, tab);
      const refused = await waitFor(async () => {
        const matches = await evaluate('Read Chrome refusal download', 'chrome.downloads.search({})');
        return matches.find(candidate => candidate.filename?.endsWith(refusalFileName) && candidate.state === 'complete');
      }, 20000, 'Chrome did not keep the download after VESPER refused the changed source identity.');
      assert.ok(refused.filename.startsWith(downloadFolder), 'The refused download escaped Chrome’s isolated output folder.');
      const refusedBytes = await readFile(refused.filename);
      assert.equal(refusedBytes.compare(bytes), 0, 'Chrome did not retain the exact refused source bytes.');
      const refusalDecision = await waitFor(async () => {
        const state = await evaluate('Read refusal decision', "chrome.storage.session.get('vesperCaptureDecisions')");
        return state.vesperCaptureDecisions?.find(entry => entry.name === refusalFileName);
      }, 8000, 'The extension did not explain the refused browser handoff.');
      assert.equal(refusalDecision.outcome, 'different-signed-out',
        'A changed source identity must leave the file in Chrome and explain the mismatch.');

      await browser.send('Page.navigate', { url: 'http://127.0.0.1:' + port + '/native-signin-redirect' }, tab);
      const signInBrowserFile = await waitFor(async () => {
        const matches = await evaluate('Read Chrome sign-in download', 'chrome.downloads.search({})');
        return matches.find(candidate => candidate.filename?.endsWith(signInFileName) && candidate.state === 'complete');
      }, 20000, 'Chrome did not retain its signed-in download after VESPER declined unauthenticated replay.');
      assert.ok(signInBrowserFile.filename.startsWith(downloadFolder), 'The sign-in download escaped Chrome’s isolated output folder.');
      assert.equal((await readFile(signInBrowserFile.filename)).compare(bytes), 0,
        'Chrome did not retain the exact signed-in source bytes.');
      const signInDecision = await waitFor(async () => {
        const state = await evaluate('Read sign-in decision', "chrome.storage.session.get('vesperCaptureDecisions')");
        return state.vesperCaptureDecisions?.find(entry => entry.name === signInFileName);
      }, 8000, 'The extension did not explain why the signed-in download stayed in Chrome.');
      assert.equal(signInDecision.outcome, 'sign-in', 'The sign-in refusal should be explained without transferring ownership.');
      const postScenarioState = await evaluate('Read sign-in native request', 'globalThis.__ownershipSmoke');
      const signInRequest = postScenarioState.nativeMessages.find(message => message.type === 'browser-capture'
        && message.message.items?.some(item => item.url.endsWith('/native-signin-protected')));
      assert.ok(signInRequest, 'The redirected sign-in URL was not independently verified by the native host.');
      assert.equal(signInRequest.hasCredentials, false, 'The extension sent an unapproved sign-in credential.');
      assert.equal(signInRequest.message.items[0].cookie, undefined);
      assert.equal(signInRequest.message.items[0].authorization, undefined);
      assert.equal(serverRequests.some(request => request.path === '/native-signin-protected'
        && request.pageRequest && request.cookiePresent), true,
      'Chrome did not follow the redirect with its own fixture-only session cookie.');
      assert.equal(serverRequests.some(request => request.path === '/native-signin-protected'
        && !request.pageRequest && request.cookiePresent), false,
      'VESPER must not receive Chrome’s fixture-only session cookie when sign-in access is off.');

      nativeCapture = {
        accepted: true,
        redirected: true,
        chromeBytesAtCreation: created.bytesReceived,
        bytes: capturedBytes.length,
        sha256: createHash('sha256').update(capturedBytes).digest('hex'),
        decision: decisions.outcome,
        chromeFileRetained: false,
        duplicateSubmission: { status: duplicate.results[0].status, sameJob: duplicate.results[0].jobId === originalSubmission.response.results?.[0]?.jobId },
        refusal: { chromeRetainedBytes: refusedBytes.length, decision: refusalDecision.outcome },
        signIn: { chromeRetainedBytes: (await readFile(signInBrowserFile.filename)).length, decision: signInDecision.outcome,
          chromeCookieObserved: true, credentialSentToNative: signInRequest.hasCredentials },
        nativeCalls: postScenarioState.nativeCalls,
        nativeTypes: [...new Set(postScenarioState.nativeMessages.map(message => message.type))],
        serverRequests: serverRequests.map(request => ({ ...request }))
      };
    } else {
      assert.equal(await evaluate('Check native isolation', '__ownershipSmoke.nativeCalls'), 0);
    }

    await browser.send('Page.navigate', { url: 'http://127.0.0.1:' + port + '/player' }, tab);
    async function flyShown() {
      const { root: dom } = await browser.send('DOM.getDocument', { depth: -1, pierce: true }, tab);
      const nodes = [dom];
      for (const node of nodes) {
        nodes.push(...(node.children || []), ...(node.shadowRoots || []));
        if (node.nodeName !== 'BUTTON') continue;
        const index = node.attributes?.indexOf('class') ?? -1;
        const classes = index < 0 ? [] : node.attributes[index + 1].split(' ');
        if (classes.includes('fly')) return classes.includes('show');
      }
      return null;
    }
    await waitFor(() => flyShown(), 5000, 'The butterfly did not appear on the regular player.');
    for (const [mode, enter, leave, pointerX] of [
      ['native fullscreen', "player.requestFullscreen()", 'document.exitFullscreen()', 400],
      ['expanded player', "player.classList.add('expanded')", "player.classList.remove('expanded')", 440]
    ]) {
      const result = await browser.send('Runtime.evaluate', { expression: enter, awaitPromise: true, userGesture: true }, tab);
      assert.ok(!result.exceptionDetails, 'Could not enter ' + mode);
      await waitFor(async () => (await flyShown()) === false, 5000, 'The butterfly stayed visible in ' + mode);
      await browser.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: pointerX, y: 220 }, tab);
      await waitFor(() => flyShown(), 2500, 'The butterfly did not wake in ' + mode);
      await browser.send('Runtime.evaluate', { expression: leave, awaitPromise: true }, tab);
    }
    // Actual MSE append calls in two simultaneous players, not a VM mock.
    await browser.send('Page.navigate', { url: 'http://127.0.0.1:' + port + '/mse-feed' }, tab);
    const mse = await waitFor(async () => {
      const reply = await browser.send('Runtime.evaluate', { expression: `(() => {
        if (!window.fixture) return null;
        if (fixture.errors.length) return {errors: fixture.errors};
        if (!fixture.ready || first.readyState < 1 || second.readyState < 1) return null;
        return [first,second].map(player => {
          player.removeAttribute('data-vesper-media-provenance');
          player.dispatchEvent(new Event('vesper-media-provenance-request',{bubbles:true}));
          const data = JSON.parse(player.getAttribute('data-vesper-media-provenance') || '{"paths":[]}');
          player.removeAttribute('data-vesper-media-provenance');
          return {id:player.id, readyState:player.readyState, paths:data.paths.map(item=>item.path), fingerprints:data.paths.map(item=>item.fingerprint)};
        });
      })()`, returnByValue: true }, tab);
      const value = reply.result.value;
      return Array.isArray(value) && value.some(item => !item.fingerprints?.length || item.fingerprints.some(hash => !hash)) ? null : value;
    }, 10000, 'Two real MediaSource players did not load.');
    assert.ok(Array.isArray(mse), JSON.stringify(mse));
    assert.deepEqual(mse.map(x => x.paths), [
      ['http://127.0.0.1:' + port + '/asset-100001/clip.mp4'],
      ['http://127.0.0.1:' + port + '/asset-200002/clip.mp4']
    ], 'Cross-player provenance or signed query leakage.');
    assert.deepEqual(mse.map(x => x.fingerprints), ['one','two'].map((session,index) => [createHash('sha256')
      .update('http://127.0.0.1:' + port + '/asset-' + (index ? '200002' : '100001') + '/clip.mp4?session=fixture-' + session).digest('hex')]),
      'Exact source fingerprints did not match the observed requests.');
    // Test placement through the real closed shadow DOM, without modifying the
    // content script or asking its private ranking function which player won.
    const intentScenarios = [];
    async function playerBounds(id) {
      const result = await browser.send('Runtime.evaluate', { expression: `(() => {
        const el = document.getElementById(${JSON.stringify(id)});
        if (!el || el.readyState < 1) return null;
        const r = el.getBoundingClientRect();
        return {left:r.left,right:r.right,top:r.top,bottom:r.bottom};
      })()`, returnByValue: true }, tab);
      return result.result.value;
    }
    async function butterflyInside(id) {
      const rect = await playerBounds(id);
      if (!rect) return false;
      const { root: dom } = await browser.send('DOM.getDocument', { depth: -1, pierce: true }, tab);
      const nodes = [dom];
      for (const node of nodes) {
        nodes.push(...(node.children || []), ...(node.shadowRoots || []));
        if (node.nodeName !== 'BUTTON') continue;
        const index = node.attributes?.indexOf('class') ?? -1;
        if (index < 0 || !node.attributes[index + 1].split(' ').includes('fly')) continue;
        try {
          const { model } = await browser.send('DOM.getBoxModel', { nodeId: node.nodeId }, tab);
          const x = (model.border[0] + model.border[4]) / 2;
          return x >= rect.left && x <= rect.right;
        } catch { return false; }
      }
      return false;
    }
    for (const variant of [0, 1, 4, 5]) {
      await browser.send('Page.navigate', { url: 'http://127.0.0.1:' + port + '/intent-feed?case=' + variant }, tab);
      await waitFor(() => butterflyInside('main-media'), 8000, 'A larger incidental loop displaced the primary player, case ' + variant);
      intentScenarios.push({ variant, primarySelected: true });
      const target = await waitFor(() => playerBounds('incidental-media'), 5000, 'The secondary player never loaded.');
      const x = (target.left + target.right) / 2, y = (target.top + target.bottom) / 2;
      await browser.send('Input.dispatchMouseEvent', { type:'mousePressed', x, y, button:'left', clickCount:1 }, tab);
      await browser.send('Input.dispatchMouseEvent', { type:'mouseReleased', x, y, button:'left', clickCount:1 }, tab);
      await waitFor(() => butterflyInside('incidental-media'), 5000, 'A deliberate interaction did not select the secondary player, case ' + variant);
      intentScenarios.at(-1).interactionSelected = true;
      await browser.send('Runtime.evaluate', { expression: "document.getElementById('incidental-media').src='/player.webm?next=1'" }, tab);
      await waitFor(() => butterflyInside('main-media'), 5000, 'A reused player retained stale intent, case ' + variant);
      intentScenarios.at(-1).generationReset = true;
    }
    let recycledPlayer = null;
    if (playerRaceTest) {
      await evaluate('Stub only the picker lookup and native enqueue', `(() => {
        globalThis.__recycled = { lookedUp: [], submitted: [] };
        playerOptions = async message => {
          if (message.prefetch) return { ok: true, data: { prefetchSkipped: true } };
          const token = crypto.randomUUID().replaceAll('-', '');
          __recycled.lookedUp.push({ page: new URL(message.pageUrl).pathname, token });
          return { ok: true, data: { mediaSession: token, title: 'Reused player',
            choices: [{ index: 0, label: 'Original quality', detail: 'Fixture', container: 'WEBM' }] } };
        };
        const originalRequest = nativeRequest;
        nativeRequest = async (...args) => {
          if (args[0]?.type !== 'media-select') return originalRequest(...args);
          __recycled.submitted.push(args[0].mediaSession);
          return { ok: true, results: [{ status: 'queued' }] };
        };
        return true;
      })()`);
      await browser.send('Page.navigate', { url: 'http://127.0.0.1:' + port + '/recycled-player' }, tab);
      await waitFor(() => flyShown(), 5000, 'Recycled player did not get a butterfly.');
      async function clickWith(className, before = '') {
        const target = await waitFor(async () => {
          const { root } = await browser.send('DOM.getDocument', { depth: -1, pierce: true }, tab);
          const nodes = [root];
          for (const node of nodes) {
            nodes.push(...(node.children || []), ...(node.shadowRoots || []));
            const classes = node.attributes?.[node.attributes.indexOf('class') + 1]?.split(' ') || [];
            if (node.nodeName === 'BUTTON' && classes.includes(className)) return node;
          }
        }, 5000, 'Could not find fixture button: ' + className);
        const { object } = await browser.send('DOM.resolveNode', { nodeId: target.nodeId }, tab);
        const result = await browser.send('Runtime.callFunctionOn', {
          objectId: object.objectId, functionDeclaration: `function () { ${before}; this.click(); }`, userGesture: true
        }, tab);
        assert.ok(!result.exceptionDetails, JSON.stringify(result.exceptionDetails));
      }
      const identity = () => browser.send('Runtime.evaluate', {
        expression: '[location.href, document.querySelector("video").currentSrc]', returnByValue: true
      }, tab).then(reply => reply.result.value);
      const beforeRecycle = await identity();
      assert.ok(beforeRecycle[1].endsWith('/player.webm'));
      await clickWith('fly');
      await clickWith('primary', "this.ownerDocument.getElementById('card').dataset.permalink = '/posts/second'");
      await delay(250);
      assert.deepEqual(await identity(), beforeRecycle);
      const stale = await evaluate('Check stale submission', '__recycled');
      assert.equal(stale.lookedUp[0]?.page, '/posts/first');
      assert.equal(stale.submitted.length, 0);
      await waitFor(() => flyShown(), 5000, 'Butterfly did not recover after the post changed.');
      await clickWith('fly');
      await clickWith('primary');
      const fresh = await waitFor(async () => {
        const status = await evaluate('Read positive control', '__recycled');
        return status.submitted.length ? status : null;
      }, 5000, 'The current picker could not submit.');
      assert.equal(fresh.lookedUp[1]?.page, '/posts/second');
      assert.deepEqual(fresh.submitted, [fresh.lookedUp[1].token]);
      recycledPlayer = { unchangedDocumentUrl: true, unchangedCurrentSrc: true, staleSubmissions: 0, currentSubmissions: 1 };
    }
    const finalObservations = await evaluate('Read final Chrome events', 'globalThis.__ownershipSmoke');
    process.stdout.write(JSON.stringify({
      ok: true,
      browser: 'Chrome for Testing (isolated headless profile)',
      createdBytes: finalObservations.created[0].bytesReceived,
      changedEvents: finalObservations.changed.length,
      completedBytes: saved.length,
      keptInChrome: true,
      pageMadeKeptInChrome: true,
      fullscreenButterfly: true,
      intentScenarios,
      msePlayerAssociation: { players: mse.length, transports: ['fetch','xhr'], isolated: true, queriesRemoved: true },
      nativeMessagingInvoked: finalObservations.nativeCalls > 0,
      nativeCapture, captureRace, managedDownloads, recycledPlayer
    }) + '\n');
  }
  finally {
    if (browser) {
      try { await browser.send('Browser.close'); } catch {}
      browser.close();
    }
    if (chrome && chrome.exitCode === null) {
      chrome.kill();
      if (process.platform === 'win32') {
        // A private Chrome profile has its own process tree; leave the user's Chrome alone.
        try {
          const { execFileSync } = await import('node:child_process');
          execFileSync('taskkill', ['/PID', String(chrome.pid), '/T', '/F'], { stdio: 'ignore' });
        } catch {}
      }
    }
    await new Promise(resolve => server.close(resolve));
    await rm(profile, { recursive: true, force: true, maxRetries: 8, retryDelay: 250 });
  }
}

if (!chromeExe) {
  process.stderr.write('This opt-in test needs CHROME_PATH pointing to Chrome for Testing or Chromium. It loads the unpacked extension through CDP Extensions.loadUnpacked in a fresh profile. No browser was started.\n');
  process.exitCode = 2;
} else {
  await main();
}
