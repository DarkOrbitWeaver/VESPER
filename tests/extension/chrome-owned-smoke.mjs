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
      callback({ v: 2, id: message.id, ok: true });
      return;
    }
    smoke.raceCalls.push({ type: message.type, operation: message.operation, id: message.id });
    const reply = fields => callback({ v: 2, id: message.id, ok: true, ...fields });
    if (message.type === 'capture-preflight')
      return reply({ data: { ready: true, siteRules: { neverCaptureHosts: [] } } });
    if (message.type === 'hello')
      return setTimeout(() => reply({ data: { siteRules: { neverCaptureHosts: [] } } }), 650);
    if (message.type === 'browser-capture' || message.type === 'browser-submit')
      return setTimeout(() => reply({ results: [{ status: 'started', transferReady: true }] }), 400);
    if (message.type === 'browser-transfer')
      return reply({ data: { state: message.operation === 'accept' ? 'active' : 'failed' } });
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
  const audioClip = await readFile(join(root, 'tests/Vesper.Tests/Fixtures/Media/quiet-track.mp3'));
  const mseClip = await readFile(join(root, 'tests/Vesper.Tests/Fixtures/Media/instinct-mse.mp4'));
  const profile = await mkdtemp(join(profileRoot, '.chrome-ownership-smoke-'));
  assert.equal(dirname(resolve(profile)), resolve(profileRoot));
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
    if (playerRaceTest && request.url === '/worker-mse') {
      response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      response.end(`<!doctype html><title>Dedicated worker fixture</title>
        <video id="worker-player" width="640" height="360" muted controls autoplay></video>
        <script>
          window.workerFixture = { error: null };
          const video = document.querySelector('video');
          video.fixtureLibrary = { manifestUrl: 'https://fixture.invalid/first.mpd' };
          const worker = new Worker('/worker-mse.js');
          worker.onmessage = event => {
            if (typeof event.data === 'string') workerFixture.error = event.data;
            else video.srcObject = event.data;
          };
          worker.onerror = event => workerFixture.error = event.message;
        </script>`);
      return;
    }
    if (playerRaceTest && request.url === '/worker-mse.js') {
      response.writeHead(200, { 'Content-Type': 'application/javascript' });
      response.end(`const source = new MediaSource();
        const handle = source.handle;
        source.addEventListener('sourceopen', async () => {
          try {
            const buffer = source.addSourceBuffer('video/mp4; codecs="avc1.42c00a,mp4a.40.2"');
            const bytes = await (await fetch('/asset-100001/clip.m4s')).arrayBuffer();
            await new Promise((resolve, reject) => {
              buffer.addEventListener('updateend', resolve, {once:true});
              buffer.addEventListener('error', reject, {once:true});
              buffer.appendBuffer(bytes);
            });
            source.endOfStream();
          } catch (error) { postMessage(String(error)); }
        }, {once:true});
        postMessage(handle, [handle]);`);
      return;
    }
    if (playerRaceTest && request.url.startsWith('/worker-transfer?')) {
      const mode = new URL(request.url, 'http://fixture.invalid').searchParams.get('mode');
      if (!['classic','module','blob','copied'].includes(mode)) { response.writeHead(400).end(); return; }
      response.writeHead(200, {'Content-Type':'text/html; charset=utf-8'});
      response.end(`<!doctype html><title>Worker transfer fixture</title><video id="transfer-player" width="640" height="360" muted controls autoplay></video>
        <script>
          window.transferFixture={error:null,ready:false};
          (async()=>{
            const video=document.querySelector('video'), ms=new MediaSource();
            video.src=URL.createObjectURL(ms);
            await new Promise(resolve=>ms.addEventListener('sourceopen',resolve,{once:true}));
            const track=ms.addSourceBuffer('video/mp4; codecs="avc1.42c00a,mp4a.40.2"');
            window.transferTrack=track;
            const mode=${JSON.stringify(mode)};
            let worker;
            if(mode==='blob'){
              const code="onmessage=async e=>{const b=await(await fetch(e.data.url)).arrayBuffer();postMessage(b,[b])}";
              const workerUrl=URL.createObjectURL(new Blob([code],{type:'text/javascript'}));
              worker=new Worker(workerUrl);
              URL.revokeObjectURL(workerUrl);
            }else worker=new Worker('/worker-transfer.js',{type:mode==='module'?'module':'classic'});
            const bytes=await new Promise((resolve,reject)=>{
              worker.onmessage=e=>resolve(e.data); worker.onerror=e=>reject(Error(e.message));
              worker.postMessage({url:location.origin+'/asset-100001/clip.m4s',copy:mode==='copied'});
            });
            worker.terminate();
            await new Promise((resolve,reject)=>{
              track.addEventListener('updateend',resolve,{once:true});
              track.addEventListener('error',()=>reject(Error('append failed')),{once:true});
              track.appendBuffer(bytes);
            });
            ms.endOfStream();
            transferFixture.ready=true;
          })().catch(e=>transferFixture.error=String(e));
        </script>`);
      return;
    }
    if (playerRaceTest && request.url === '/worker-transfer.js') {
      response.writeHead(200, {'Content-Type':'text/javascript'});
      response.end(`self.onmessage=async e=>{
        let bytes=await(await fetch(e.data.url)).arrayBuffer();
        if(e.data.copy) bytes=new Uint8Array(bytes).slice().buffer;
        if(e.data.copy) self.postMessage(bytes); else self.postMessage(bytes,[bytes]);
      };`);
      return;
    }
    if (request.url === '/mse-feed' || request.url === '/mse-prefetch') {
      const prefetch = request.url === '/mse-prefetch';
      response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      response.end(`<!doctype html><title>MSE feed fixture</title>
        <video id="first" width="320" height="180" muted controls></video>
        ${prefetch ? '' : '<video id="second" width="320" height="180" muted controls></video>'}
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
        ${prefetch ? "fetch('/asset-100001/playlist.m3u8').then(response => response.text()).then(() => load(first,'/asset-100001/clip.m4s'))"
          : "Promise.all([load(first,'/asset-100001/clip.mp4?session=fixture-one'), load(second,'/asset-200002/clip.mp4?session=fixture-two',true)])"}
          .then(() => fixture.ready = true, e => fixture.errors.push(String(e)));
        </script>`);
      return;
    }
    if (request.url === '/asset-100001/playlist.m3u8') {
      response.writeHead(200, { 'Content-Type': 'application/vnd.apple.mpegurl' });
      response.end('#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nclip.m4s\n#EXT-X-ENDLIST\n');
      return;
    }
    if (request.url === '/asset-100001/clip.m4s'
        || /^\/asset-(?:100001|200002)\/clip\.mp4\?session=fixture-(?:one|two)$/.test(request.url)) {
      response.writeHead(200, { 'Content-Type': 'video/mp4', 'Content-Length': mseClip.length, 'Cache-Control': 'no-store' });
      response.end(mseClip);
      return;
    }
    if (playerRaceTest && request.url === '/recycled-player') {
      response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      response.end('<!doctype html><title>Recycled player fixture</title><article id="card" data-permalink="/posts/first">'
        + '<video width="640" height="360" src="/player.webm" muted controls preload="auto"></video></article>'
        + '<button class="previous-post" aria-label="Previous post">&lt;</button><button class="next-post" aria-label="Next post">&gt;</button>'
        + '<script>for (const [direction, post] of [["previous","first"], ["next","second"]]) {'
        + 'document.querySelector("." + direction + "-post").onclick = () => {'
        + 'const route = "/posts/" + post; history.pushState({}, "", route); card.dataset.permalink = route;'
        + 'const video = card.querySelector("video"); video.style.opacity = 0;'
        + 'requestAnimationFrame(() => requestAnimationFrame(() => {video.style.opacity = 1}));'
        + '}; }</script>');
      return;
    }
    if (playerRaceTest && request.url === '/scroll-feed') {
      response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      response.end('<!doctype html><style>body{margin:0}#feed{height:98vh;overflow:auto}article{height:430px}</style><main id="feed">'
        + Array.from({ length: 67 }, (_, i) => '<article>' + ([1, 33, 65].includes(i)
          ? `<video id="post-${i}" style="margin-left:${i === 1 ? 0 : i === 33 ? 96 : 192}px" width="480" height="360" src="/player.webm?post=${i}" autoplay muted loop controls></video>`
          : '<span>Earlier post</span>') + '</article>').join('') + '</main>');
      return;
    }
    if (playerRaceTest && request.url.startsWith('/shadow-player?')) {
      const mode = request.url.endsWith('open') ? 'open' : 'closed';
      const video = '<video width="640" height="360" src="/player.webm" autoplay muted loop controls></video>';
      response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      if (request.url.includes('declarative')) {
        response.end(`<!doctype html><section><template shadowrootmode="${mode}">${video}</template></section>`);
      } else {
        response.end('<!doctype html><section></section><script>window.addPlayer = () => {'
          + `document.querySelector("section").attachShadow({mode:"${mode}"}).innerHTML=${JSON.stringify(video)}`
          + '};</script>');
      }
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
    if (request.url === '/custom-audio') {
      response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      response.end('<!doctype html><title>Custom audio fixture</title>'
        + '<video id="ad" class="ad-slot" style="width:440px;height:250px" src="/player.webm" autoplay muted></video>'
        + '<button id="audio-control" style="position:absolute;left:580px;top:250px;width:160px;height:32px" aria-controls="episode">Play episode</button>'
        + '<audio id="episode" src="/episode.mp3" preload="auto"></audio>'
        + '<script>document.getElementById("audio-control").onclick = () => document.getElementById("episode").play();</script>');
      return;
    }
    if (request.url === '/episode.mp3') {
      response.writeHead(200, { 'Content-Type': 'audio/mpeg', 'Content-Length': audioClip.length });
      response.end(audioClip);
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

    const popupTarget = (await browser.send('Target.createTarget', {
      url: 'chrome-extension://' + extensionId + '/popup.html'
    })).targetId;
    const popupSession = (await browser.send('Target.attachToTarget', { targetId: popupTarget, flatten: true })).sessionId;
    const toggleStyles = await waitFor(async () => {
      const { result } = await browser.send('Runtime.evaluate', { expression: `
        [...document.querySelectorAll('label.check')].map(label => getComputedStyle(label).userSelect)`,
        returnByValue: true }, popupSession);
      return result.value?.length ? result.value : null;
    }, 5000, 'Extension settings did not open.');
    assert.deepEqual(toggleStyles, ['none', 'none']);
    await browser.send('Target.closeTarget', { targetId: popupTarget });

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
    await evaluate("Pause automatic capture", "(async () => { await browserPreferencesReady; await chrome.storage.local.set({ vesperPaused: true });"
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
    async function clickDownloadLink(path, name) {
      await browser.send('Page.navigate', { url: 'http://127.0.0.1:' + port + '/page' }, tab);
      await waitFor(async () => (await browser.send('Runtime.evaluate', {
        expression: "location.pathname === '/page' && !!document.body", returnByValue: true
      }, tab)).result.value, 5000, 'The fixture page did not open.');
      const { result } = await browser.send('Runtime.evaluate', { returnByValue: true, expression:
        "(() => { const link = document.createElement('a'); link.href = " + JSON.stringify(path)
        + "; link.download = " + JSON.stringify(name) + "; link.textContent = 'Download';"
        + "link.style = 'position:fixed;top:10px;left:10px;z-index:9999'; document.body.append(link);"
        + "const rect = link.getBoundingClientRect(); return { x: rect.left + rect.width/2, y: rect.top + rect.height/2 }; })()"
      }, tab);
      for (const type of ['mousePressed', 'mouseReleased'])
        await browser.send('Input.dispatchMouseEvent', { type, ...result.value, button: 'left', clickCount: 1 }, tab);
    }
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
    await evaluate('Resume automatic capture', "chrome.storage.local.set({ vesperPaused: false })");
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
    const blobDownload = await waitFor(async () => {
      const matches = await evaluate('Read page-made download', 'chrome.downloads.search({})');
      return matches.find(candidate => candidate.filename?.endsWith(blobName) && candidate.state === 'complete');
    }, 15000, 'Chrome did not finish the page-generated download.');
    assert.equal((await readFile(join(downloadFolder, blobName))).compare(bytes), 0);
    await waitFor(async () => {
      const capture = await evaluate('Read final capture decisions', "chrome.storage.session.get('vesperCaptureDecisions')");
      return capture.vesperCaptureDecisions?.some(entry => entry.id === blobDownload.id
        && entry.outcome === 'failed' && entry.reason === 'browser-generated');
    }, 5000, 'The extension did not record that Chrome owns the page-generated download.');
    let captureRace = null;
    let managedDownloads = null;
    if (captureRaceTest) {
      await browser.send('Runtime.evaluate', { expression:
        "(() => { const form = document.createElement('form'); form.method = 'POST'; form.action = '/single-post';"
        + "document.body.append(form); form.submit(); })()"
      }, tab);
      const post = await waitFor(async () => {
        const matches = await evaluate('Read one-use POST download', 'chrome.downloads.search({})');
        return matches.find(item => item.filename?.endsWith('vesper-single-post.bin') && item.state === 'complete');
      }, 15000, 'Chrome did not finish the one-use POST.');
      assert.ok(post.filename.startsWith(downloadFolder));
      assert.equal((await readFile(post.filename)).compare(bytes), 0);
      assert.deepEqual(serverRequests.filter(entry => entry.path === '/single-post').map(entry => entry.method), ['POST']);
      const reports = await evaluate('Check Chrome-only completion', '__ownershipSmoke.managed');
      assert.equal(reports.some(item => item.operation === 'complete'), false, 'Chrome-owned files must not be imported.');
      const captures = await evaluate('Check generated files were not replayed', '__ownershipSmoke.raceCalls');
      assert.equal(captures.some(call => call.type === 'browser-capture' || call.type === 'browser-submit'), false);
      managedDownloads = { blobKeptInChrome: true, oneUsePostKeptInChrome: true, imported: false };
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
        assert.equal(attempted || reachedAccept, false, 'VESPER tried to take a Chrome-owned file.');
        await waitFor(async () => {
          const logs = await evaluate('Read capture decisions', "chrome.storage.session.get('vesperCaptureDecisions')");
          return logs.vesperCaptureDecisions?.some(entry => entry.id === file.id && entry.outcome === 'failed');
        }, 5000, 'The extension did not record the Chrome miss.');
      }
      const createdBefore = await evaluate('Count Chrome download entries', '__ownershipSmoke.created.length');
      await clickDownloadLink('/race-delayed', 'vesper-race-delayed.bin');
      await waitFor(() => evaluate('Find native acceptance',
        "__ownershipSmoke.raceCalls.some(call => call.operation === 'accept')"), 8000,
        'The trusted download click was not accepted by the simulated helper.');
      assert.equal(await evaluate('Count Chrome entries after acceptance', '__ownershipSmoke.created.length'), createdBefore);
    }
    let nativeCapture = null;
    if (nativeHostTest) {
      assert.equal(await evaluate('Check no native calls before the valid file', '__ownershipSmoke.nativeCalls'), 0,
        'Chrome-owned and page-generated downloads must not call the native host.');

      const hello = await evaluate('Call registered native host',
        "new Promise((resolve, reject) => chrome.runtime.sendNativeMessage('com.vesper.downloader',"
        + "{ v: 2, id: 'chrome-native-host-smoke-hello', type: 'hello' }, response => {"
        + "const error = chrome.runtime.lastError; if (error) reject(new Error(error.message)); else resolve(response); }))");
      assert.equal(hello?.ok, true, 'The registered native host did not answer its hello request.');
      assert.equal(hello?.data?.app, 'VESPER');

      const createdBefore = await evaluate('Count Chrome download entries', '__ownershipSmoke.created.length');
      await clickDownloadLink('/native-redirect', nativeFileName);

      const capturedPath = join(nativeDownloadFolder, nativeFileName);
      const capturedBytes = await waitFor(async () => {
        try {
          const file = await readFile(capturedPath);
          return file.compare(bytes) === 0 ? file : null;
        } catch { return null; }
      }, 25000, 'VESPER did not publish the accepted Chrome transfer in the isolated output folder.');
      assert.equal(await evaluate('Count Chrome entries after publication', '__ownershipSmoke.created.length'), createdBefore);
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
        return state.vesperCaptureDecisions?.find(entry => entry.id === refused.id);
      }, 8000, 'The extension did not explain the refused browser handoff.');
      assert.equal(refusalDecision.outcome, 'failed');

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
        return state.vesperCaptureDecisions?.find(entry => entry.id === signInBrowserFile.id);
      }, 8000, 'The extension did not explain why the signed-in download stayed in Chrome.');
      assert.equal(signInDecision.outcome, 'failed');
      const postScenarioState = await evaluate('Read sign-in native request', 'globalThis.__ownershipSmoke');
      const signInRequest = postScenarioState.nativeMessages.find(message => message.type === 'browser-capture'
        && message.message.items?.some(item => item.url.endsWith('/native-signin-protected')));
      assert.equal(signInRequest, undefined, 'The helper must not replay a Chrome-owned file.');
      assert.equal(serverRequests.some(request => request.path === '/native-signin-protected'
        && request.pageRequest && request.cookiePresent), true,
      'Chrome did not follow the redirect with its own fixture-only session cookie.');
      assert.equal(serverRequests.some(request => request.path === '/native-signin-protected'
        && !request.pageRequest && request.cookiePresent), false,
      'VESPER must not receive Chrome’s fixture-only session cookie when sign-in access is off.');

      nativeCapture = {
        accepted: true,
        redirected: true,
        chromeEntryCreated: false,
        bytes: capturedBytes.length,
        sha256: createHash('sha256').update(capturedBytes).digest('hex'),
        chromeFileRetained: false,
        duplicateSubmission: { status: duplicate.results[0].status, sameJob: duplicate.results[0].jobId === originalSubmission.response.results?.[0]?.jobId },
        refusal: { chromeRetainedBytes: refusedBytes.length, decision: refusalDecision.outcome },
        signIn: { chromeRetainedBytes: (await readFile(signInBrowserFile.filename)).length, decision: signInDecision.outcome,
          chromeCookieObserved: true, credentialSentToNative: false },
        nativeCalls: postScenarioState.nativeCalls,
        nativeTypes: [...new Set(postScenarioState.nativeMessages.map(message => message.type))],
        serverRequests: serverRequests.map(request => ({ ...request }))
      };
    } else {
      assert.equal(await evaluate('Check native isolation', '__ownershipSmoke.nativeCalls'), 0);
    }

    await browser.send('Page.navigate', { url: 'http://127.0.0.1:' + port + '/player' }, tab);
    async function findButterfly() {
      const { root: dom } = await browser.send('DOM.getDocument', { depth: -1, pierce: true }, tab);
      const nodes = [dom];
      for (const node of nodes) {
        nodes.push(...(node.children || []), ...(node.shadowRoots || []));
        if (node.nodeName !== 'BUTTON') continue;
        const index = node.attributes?.indexOf('class') ?? -1;
        const classes = index < 0 ? [] : node.attributes[index + 1].split(' ');
        if (classes.includes('fly')) return { nodeId: node.nodeId, shown: classes.includes('show') && !node.attributes.includes('hidden') };
      }
      return null;
    }
    const flyShown = async () => (await findButterfly())?.shown ?? null;
    await waitFor(() => flyShown(), 5000, 'The butterfly did not appear on the regular player.');
    await evaluate('Pause on a page with a player', 'chrome.storage.local.set({ vesperPaused: true })');
    await waitFor(async () => !(await flyShown()), 5000, 'The butterfly stayed visible during Pause.');
    await evaluate('Resume on the same page', 'chrome.storage.local.set({ vesperPaused: false })');
    await waitFor(() => flyShown(), 5000, 'The butterfly did not return after resuming.');
    for (const [mode, enter, leave, pointerX] of [
      ['wrapper fullscreen', "player.requestFullscreen()", 'document.exitFullscreen()', 400],
      ['expanded player', "player.classList.add('expanded')", "player.classList.remove('expanded')", 440]
    ]) {
      const result = await browser.send('Runtime.evaluate', { expression: enter, awaitPromise: true, userGesture: true }, tab);
      assert.ok(!result.exceptionDetails, 'Could not enter ' + mode);
      await waitFor(async () => (await flyShown()) === false, 5000, 'The butterfly stayed visible in ' + mode);
      await browser.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: pointerX, y: 220 }, tab);
      await waitFor(() => flyShown(), 2500, 'The butterfly did not wake in ' + mode);
      await browser.send('Runtime.evaluate', { expression: leave, awaitPromise: true }, tab);
    }
    const enterVideo = await browser.send('Runtime.evaluate', {
      expression: 'player.querySelector("video").requestFullscreen()', awaitPromise: true, userGesture: true
    }, tab);
    assert.ok(!enterVideo.exceptionDetails, 'Could not enter native video fullscreen.');
    await waitFor(async () => (await flyShown()) === false, 2500, 'Native video fullscreen did not hide the page overlay.');
    await browser.send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'V', code: 'KeyV', modifiers: 9, windowsVirtualKeyCode: 86 }, tab);
    await browser.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'V', code: 'KeyV', modifiers: 9, windowsVirtualKeyCode: 86 }, tab);
    await waitFor(async () => {
      const reply = await browser.send('Runtime.evaluate', { expression: '!document.fullscreenElement', returnByValue: true }, tab);
      const { root } = await browser.send('DOM.getDocument', { depth: -1, pierce: true }, tab);
      const nodes = [root];
      for (const node of nodes) {
        nodes.push(...(node.children || []), ...(node.shadowRoots || []));
        if (node.attributes?.includes('panel show')) return reply.result.value;
      }
      return false;
    }, 3000, 'The shortcut did not leave native fullscreen and open the same player.');
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
    await evaluate('Pause automatic capture on the MediaSource fixture', 'chrome.storage.local.set({ vesperPaused: true })');
    const { result: source } = await browser.send('Runtime.evaluate', { expression: 'first.currentSrc', returnByValue: true }, tab);
    assert.match(source.value, /^blob:/);
    const picker = await evaluate('Open the MediaSource player from the right-click action', `(async () => {
      const active = (await chrome.tabs.query({})).find(tab => tab.url === 'http://127.0.0.1:${port}/mse-feed');
      return chrome.tabs.sendMessage(active.id, { type: 'vesper-open-picker', playerUrl: ${JSON.stringify(source.value)} }, { frameId: 0 });
    })()`);
    assert.equal(picker?.ok, true, 'Pause prevented the explicit player picker from opening.');
    assert.equal(await flyShown(), false, 'Opening the picker during Pause revealed the butterfly.');
    await evaluate('Resume before the intent checks', 'chrome.storage.local.set({ vesperPaused: false })');
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
    await browser.send('Page.navigate', { url: 'http://127.0.0.1:' + port + '/custom-audio' }, tab);
    await waitFor(() => butterflyInside('ad'), 5000, 'The audio fixture did not load the extension.');
    await waitFor(() => playerBounds('episode'), 5000, 'The custom audio source never loaded.');
    const audioControl = await playerBounds('audio-control');
    const x = (audioControl.left + audioControl.right) / 2;
    const y = (audioControl.top + audioControl.bottom) / 2;
    await browser.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 }, tab);
    await browser.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 }, tab);
    await waitFor(() => butterflyInside('audio-control'), 5000, 'The butterfly did not follow the custom audio control.');
    let recycledPlayer = null, preparedBeforeClick = null, scrollHandoffs = [], shadowPlayers = [], workerDeclaration = null, workerTransfers = [];
    if (playerRaceTest) {
      function findButton(...classNames) {
        return waitFor(async () => {
          const { root } = await browser.send('DOM.getDocument', { depth: -1, pierce: true }, tab);
          const nodes = [root];
          for (const node of nodes) {
            nodes.push(...(node.children || []), ...(node.shadowRoots || []));
            const classes = node.attributes?.[node.attributes.indexOf('class') + 1]?.split(' ') || [];
            if (node.nodeName === 'BUTTON' && classNames.some(name => classes.includes(name))) return node;
          }
        }, 5000, 'Could not find fixture button: ' + classNames.join(' or '));
      }
      async function clickWith(className, before = '') {
        const target = await findButton(className);
        const { object } = await browser.send('DOM.resolveNode', { nodeId: target.nodeId }, tab);
        const result = await browser.send('Runtime.callFunctionOn', {
          objectId: object.objectId, functionDeclaration: `function () { ${before}; this.click(); }`, userGesture: true
        }, tab);
        assert.ok(!result.exceptionDetails, JSON.stringify(result.exceptionDetails));
      }
      async function clickButton(className) {
        const { x, y } = await waitFor(async () => {
          const target = await findButton(className);
          try {
            const { model } = await browser.send('DOM.getBoxModel', { nodeId: target.nodeId }, tab);
            const x = (model.border[0] + model.border[4]) / 2, y = (model.border[1] + model.border[5]) / 2;
            return x > 0 && y > 0 ? { x, y } : null;
          } catch { return null; }
        }, 2500, 'No visible button bounds for ' + className);
        await browser.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y }, tab);
        await browser.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 }, tab);
        await browser.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 }, tab);
      }
      // Actual MediaSourceHandle transferred from a dedicated worker. Only the
      // native replies are simulated; no external manifest is contacted.
      await evaluate('Isolate worker declaration lookup', `(() => {
        globalThis.__workerMedia = { checks: [], submits: [] };
        globalThis.__beforeWorkerRequest = nativeRequest;
        nativeRequest = async (...args) => {
          const message = args[0];
          if (message.type === 'media-options' && message.url.endsWith('/worker-mse'))
            return { ok:false, code:'no-media' };
          if (message.type === 'media-source-options' && message.url.endsWith('/worker-mse')) {
            __workerMedia.checks.push({ url:message.candidates[0].url, signed:!!message.authorized });
            return { ok:true, data:{ mediaSession:crypto.randomUUID().replaceAll('-',''),
              choices:[{index:0,label:'Original quality',container:'MP4'}] } };
          }
          if (message.type === 'media-select') {
            __workerMedia.submits.push(message.mediaSession);
            return {ok:true,results:[{status:'queued'}]};
          }
          return __beforeWorkerRequest(...args);
        };
      })()`);
      await browser.send('Page.navigate', {url:'http://127.0.0.1:' + port + '/worker-mse'}, tab);
      const handle = await waitFor(async () => {
        const {result} = await browser.send('Runtime.evaluate', {expression:`(() => {
          if (window.workerFixture?.error) return {error:workerFixture.error};
          const v=document.querySelector('video');
          if (!v || v.readyState<1 || !v.srcObject) return null;
          v.dispatchEvent(new Event('vesper-youtube-identity-request',{bubbles:true}));
          v.removeAttribute('data-vesper-media-provenance');
          v.dispatchEvent(new Event('vesper-media-provenance-request',{bubbles:true}));
          return {kind:v.srcObject.constructor.name, declaration:v.getAttribute('data-vesper-player-manifest'),
            witness: v.getAttribute('data-vesper-media-provenance')};
        })()`,returnByValue:true},tab);
        return result.value;
      },10000,'Dedicated worker media failed to load.');
      assert.equal(handle.kind,'MediaSourceHandle',JSON.stringify(handle));
      assert.equal(handle.declaration,'https://fixture.invalid/first.mpd');
      assert.equal(handle.witness,null,'Worker data must not be represented as exact window provenance.');
      await waitFor(() => flyShown(),5000,'Worker-backed player lost the butterfly.');
      await clickButton('fly');
      await findButton('opt');
      assert.deepEqual(await evaluate('No unconfirmed manifest request','__workerMedia.checks'),[]);
      await clickButton('opt');
      await findButton('primary');
      assert.deepEqual(await evaluate('Confirmed manifest request','__workerMedia.checks'),
        [{url:'https://fixture.invalid/first.mpd',signed:false}]);
      await clickWith('primary', "this.ownerDocument.querySelector('video').fixtureLibrary.manifestUrl='https://fixture.invalid/second.mpd'");
      await delay(250);
      assert.deepEqual(await evaluate('Changed declaration did not submit','__workerMedia.submits'),[]);
      await waitFor(() => flyShown(),5000,'Worker player did not recover after the declaration changed.');
      await clickButton('fly');
      await findButton('opt');
      await clickButton('opt');
      await findButton('primary');
      assert.deepEqual(await evaluate('Fresh declaration checked','__workerMedia.checks'),
        [{url:'https://fixture.invalid/first.mpd',signed:false},{url:'https://fixture.invalid/second.mpd',signed:false}]);
      await clickButton('primary');
      await waitFor(async () => (await evaluate('Read worker submission','__workerMedia.submits.length'))===1,
        5000,'Confirmed current declaration could not submit.');
      // Same real worker-backed playback, but without a usable declaration.
      // Its fragment request is not a complete resource and must not be
      // substituted with an arbitrary nearby file.
      await browser.send('Page.navigate',{url:'http://127.0.0.1:'+port+'/worker-mse'},tab);
      await waitFor(async()=>{
        const {result}=await browser.send('Runtime.evaluate',{expression:"(()=>{const v=document.querySelector('video');if(!v||v.readyState<1||!v.srcObject)return false;delete v.fixtureLibrary;return true})()",returnByValue:true},tab);
        return result.value;
      },10000,'Opaque worker fixture did not load.');
      await waitFor(()=>flyShown(),5000,'Opaque worker lost the butterfly.');
      await clickButton('fly');
      await delay(350);
      assert.deepEqual(await evaluate('No opaque worker candidate verified','__workerMedia.checks'),
        [{url:'https://fixture.invalid/first.mpd',signed:false},{url:'https://fixture.invalid/second.mpd',signed:false}]);
      assert.equal(await evaluate('No opaque worker submission','__workerMedia.submits.length'),1);
      workerDeclaration = {realHandle:true, noUnconfirmedProbe:true, staleSubmissions:0, currentSubmissions:1,opaqueFragmentAbstained:true};
      await evaluate('Restore native fixture routing','nativeRequest = __beforeWorkerRequest');
      // Real worker-owned fetch, with a transferable or structured-cloned buffer
      // appended to a window SourceBuffer. The retained bytes may be playable,
      // but a window hook cannot claim the worker's network response identity.
      for (const mode of ['classic','module','blob','copied']) {
        await browser.send('Page.navigate',{url:'http://127.0.0.1:'+port+'/worker-transfer?mode='+mode},tab);
        const witness = await waitFor(async()=>{
          const {result}=await browser.send('Runtime.evaluate',{expression:`(()=>{
            if(window.transferFixture?.error)return {error:transferFixture.error};
            const v=document.querySelector('video');
            if(!window.transferFixture?.ready||!v||v.readyState<1)return null;
            v.removeAttribute('data-vesper-media-provenance');
            v.dispatchEvent(new Event('vesper-media-provenance-request',{bubbles:true}));
            return JSON.parse(v.getAttribute('data-vesper-media-provenance')||'null');
          })()`,returnByValue:true},tab);
          return result.value;
        },10000,'Worker transfer playback failed: '+mode);
        assert.ok(!witness.error,JSON.stringify(witness));
        assert.equal(witness.kind,'mse');
        assert.deepEqual(witness.paths,[],'Worker-only fetch invented a network identity: '+mode);
        assert.ok(witness.tracks.some(t=>t.retainedBytes>0&&t.hasInit),JSON.stringify(witness));
        workerTransfers.push({mode,networkIdentity:false,retainedTracks:witness.tracks.length,
          complete:witness.tracks.every(t=>t.bufferedComplete)});
      }

      await evaluate('Observe native verification for the prefetch fixture', `(() => {
        globalThis.__warmRequests = [];
        const request = nativeRequest;
        const options = playerOptions;
        playerOptions = async (...args) => {
          if (!args[0].prefetch) globalThis.__foregroundOpened = true;
          return options(...args);
        };
        nativeRequest = async (...args) => {
          if (args[0]?.type !== 'media-source-options' || !args[0].url.endsWith('/mse-prefetch')) return request(...args);
          __warmRequests.push(args[0].candidates[0].url);
          await new Promise(resolve => { globalThis.__finishPrefetch = resolve; });
          return { ok: true, data: { playbackMatches: 1, mediaSession: crypto.randomUUID().replaceAll('-', ''),
            choices: [{ index: 0, label: 'Original quality', container: 'MP4' }] } };
        };
      })()`);
      await browser.send('Page.navigate', { url: 'http://127.0.0.1:' + port + '/mse-prefetch' }, tab);
      await waitFor(async () => (await evaluate('Check early verification', '__warmRequests.length')) === 1,
        7000, 'The active MSE player did not prepare the witnessed stream before opening the picker.');
      await waitFor(() => flyShown(), 3000, 'The MediaSource fly was missing during prefetch.');
      await clickButton('fly');
      await waitFor(() => evaluate('Wait for the click to reach the worker', 'globalThis.__foregroundOpened'), 3000,
        'The foreground click did not reach the worker while prefetch was pending.');
      await evaluate('Release the source verification', '__finishPrefetch()');
      try { await findButton('primary', 'opt'); }
      catch (error) {
        const { root } = await browser.send('DOM.getDocument', { depth: -1, pierce: true }, tab);
        const nodes = [root];
        for (const node of nodes) nodes.push(...(node.children || []), ...(node.shadowRoots || []));
        const messages = nodes.filter(node => node.nodeName === '#text' && /VESPER|media|format|download|stream/i.test(node.nodeValue))
          .map(node => node.nodeValue).filter(value => value.length <= 200);
        const requests = await evaluate('Check prefetch requests after opening the picker', '__warmRequests');
        throw new Error(error.message + ' ' + JSON.stringify({ messages, requests }));
      }
      assert.deepEqual(await evaluate('Check verification was reused', '__warmRequests'),
        ['http://127.0.0.1:' + port + '/asset-100001/playlist.m3u8']);
      preparedBeforeClick = true;

      await evaluate('Stub only the picker lookup and native enqueue', `(() => {
        globalThis.__recycled = { lookedUp: [], submitted: [] };
        playerOptions = async message => {
          if (message.prefetch) return { ok: true, data: { prefetchSkipped: true } };
          const token = crypto.randomUUID().replaceAll('-', '');
          __recycled.lookedUp.push({ page: new URL(message.pageUrl).pathname, token });
          // This stub replaces playerOptions, including its production provenance binding.
          return withFormatEvidence({ ok: true, data: { mediaSession: token, title: 'Reused player',
            choices: [{ index: 0, label: 'Original quality', detail: 'Fixture', container: 'WEBM' }] } },
            {kind:'page', signed:false});
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
      const arrowPages = [];
      for (const [direction, path] of [['previous', '/posts/first'], ['next', '/posts/second'], ['previous', '/posts/first']]) {
        await clickButton(direction + '-post');
        await waitFor(() => flyShown(), 1500, 'Butterfly did not follow the ' + direction + ' post.');
        await clickButton('fly');
        const observed = await waitFor(async () => {
          const { lookedUp } = await evaluate('Check the post opened by the arrow', '__recycled');
          return lookedUp.length > arrowPages.length + 2 ? lookedUp.at(-1).page : null;
        }, 3000, 'The arrow left the picker unavailable.');
        assert.equal(observed, path);
        assert.equal((await identity())[1], beforeRecycle[1]);
        arrowPages.push(observed);
      }
      recycledPlayer = { unchangedDocumentUrl: true, unchangedCurrentSrc: true, staleSubmissions: 0, currentSubmissions: 1, arrowPages };

      await browser.send('Page.navigate', { url: 'http://127.0.0.1:' + port + '/scroll-feed' }, tab);
      await waitFor(() => playerBounds('post-1'), 3000, 'The scroll feed did not load.');
      for (const post of [1, 33, 65, 33, 1, 65]) {
        const started = Date.now();
        await browser.send('Runtime.evaluate', {
          expression: `document.getElementById('post-${post}').scrollIntoView({block:'start'})`
        }, tab);
        await waitFor(async () => {
          const player = await playerBounds('post-' + post);
          const fly = await findButterfly();
          if (!player || !fly?.shown) return false;
          try {
            const { model } = await browser.send('DOM.getBoxModel', { nodeId: fly.nodeId }, tab);
            const x = (model.border[0] + model.border[4]) / 2, y = (model.border[1] + model.border[5]) / 2;
            return Math.abs(x - (player.right - 32)) < 56 && Math.abs(y - player.top) < 64;
          } catch { return false; }
        }, 1500, 'Butterfly did not follow the visible post after scrolling to ' + post);
        scrollHandoffs.push({ post, elapsedMs: Date.now() - started });
      }
      for (const kind of ['declarative-open', 'declarative-closed', 'dynamic-open', 'dynamic-closed']) {
        await browser.send('Page.navigate', { url: 'http://127.0.0.1:' + port + '/shadow-player?' + kind }, tab);
        if (kind.startsWith('dynamic')) {
          await waitFor(async () => (await browser.send('Runtime.evaluate',
            { expression: 'typeof addPlayer === "function"', returnByValue: true }, tab)).result.value, 2000, 'Shadow fixture did not load.');
          await browser.send('Runtime.evaluate', { expression: 'addPlayer()' }, tab);
        }
        await waitFor(() => flyShown(), 2000, 'Butterfly did not attach inside the ' + kind + ' shadow tree.');
        await clickButton('fly');
        await findButton('primary');
        shadowPlayers.push(kind);
      }
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
      pausedContextPicker: true,
      toggleLabelsNonSelectable: true,
      preparedBeforeClick,
      fullscreenButterfly: true,
      nativeFullscreenShortcut: true,
      customAudioButterfly: true,
      intentScenarios,
      msePlayerAssociation: { players: mse.length, transports: ['fetch','xhr'], isolated: true, queriesRemoved: true },
      workerDeclaration,
      workerTransfers,
      nativeMessagingInvoked: finalObservations.nativeCalls > 0,
      nativeCapture, captureRace, managedDownloads, recycledPlayer, scrollHandoffs, shadowPlayers
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
