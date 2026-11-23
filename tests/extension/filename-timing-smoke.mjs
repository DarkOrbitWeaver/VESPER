// Measures Chrome's filename-decision timing in a temporary profile with VESPER loaded.
// No native host is registered or invoked.
// Run with CHROME_PATH set to Chrome for Testing or Chromium.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { cp, mkdtemp, mkdir, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const extension = join(root, 'extension');
const chromePath = process.env.CHROME_PATH;
const bytes = Buffer.alloc(1024 * 1024, 0x5a);
const downloads = [];

class Cdp {
  constructor(socket) {
    this.socket = socket;
    this.nextId = 0;
    this.pending = new Map();
    socket.addEventListener('message', event => {
      const message = JSON.parse(event.data);
      const task = this.pending.get(message.id);
      if (!task) return;
      clearTimeout(task.timer);
      this.pending.delete(message.id);
      if (message.error) task.reject(new Error(message.error.message));
      else task.resolve(message.result || {});
    });
  }

  static async open(address) {
    const socket = new WebSocket(address);
    await new Promise((resolve, reject) => {
      socket.addEventListener('open', resolve, { once: true });
      socket.addEventListener('error', reject, { once: true });
    });
    return new Cdp(socket);
  }

  send(method, params = {}, sessionId) {
    const id = ++this.nextId;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error('Chrome did not answer ' + method));
      }, 10000);
      this.pending.set(id, { resolve, reject, timer });
      this.socket.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
    });
  }

  close() {
    this.socket.close();
    for (const task of this.pending.values()) {
      clearTimeout(task.timer);
      task.reject(new Error('Chrome test ended.'));
    }
    this.pending.clear();
  }
}

async function until(read, timeoutMs, reason) {
  const end = Date.now() + timeoutMs;
  let last;
  while (Date.now() < end) {
    last = await read();
    if (last) return last;
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  throw new Error(reason + ' Last observation: ' + JSON.stringify(last));
}

async function evaluateWorker(browser, sessionId, expression) {
  const result = await browser.send('Runtime.evaluate',
    { expression, awaitPromise: true, returnByValue: true }, sessionId);
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.text);
  return result.result.value;
}

async function main() {
  assert.ok(chromePath, 'Set CHROME_PATH to Chrome for Testing or Chromium.');
  const profile = await mkdtemp(join(process.env.TEMP || process.env.TMP || '.', 'vesper-filename-timing-'));
  const output = join(profile, 'downloads');
  const testExtension = join(profile, 'extension');
  const chromeProfile = join(profile, 'Default');
  await mkdir(output);
  await mkdir(chromeProfile);
  await writeFile(join(chromeProfile, 'Preferences'), JSON.stringify({
    download: { default_directory: output, prompt_for_download: false, directory_upgrade: true }
  }));
  await cp(extension, testExtension, { recursive: true });
  const managedPath = join(testExtension, 'browser-managed.js');
  let managedSource = await readFile(managedPath, 'utf8');
  const listener = 'chrome.downloads.onDeterminingFilename?.addListener((item, suggest) => {';
  assert.equal(managedSource.split(listener).length - 1, 1, 'Expected one VESPER filename listener.');
  managedSource = managedSource.replace(listener, `chrome.downloads.onDeterminingFilename?.addListener((item, originalSuggest) => {
  globalThis.__filenameTiming.push({ type: 'filename', id: item.id, at: Date.now(),
    bytesReceived: item.bytesReceived, filename: item.filename, state: item.state });
  const suggest = (...args) => {
    const finish = () => {
      globalThis.__filenameTiming.push({ type: 'suggest', id: item.id, at: Date.now() });
      originalSuggest(...args);
    };
    const wait = Number(globalThis.__filenameDelay) || 0;
    if (wait <= 0) finish();
    else setTimeout(finish, wait);
    return true;
  };`);
  const finalSuggestion = `suggest({ filename: \`VESPER/\${name}\`, conflictAction: 'uniquify' });`;
  assert.equal(managedSource.split(finalSuggestion).length - 1, 1, 'Expected the VESPER save-folder suggestion.');
  managedSource = managedSource.replace(finalSuggestion, finalSuggestion + '\n  return true;');
  await writeFile(managedPath, managedSource);
  const serverEvents = [];
  const server = createServer((request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1');
    if (url.pathname === '/launch') {
      const destination = url.searchParams.get('destination');
      response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
      response.end(`<!doctype html><title>Download fixture</title><a id="download" href="${destination}">Download</a>`);
      return;
    }
    if (url.pathname !== '/download') {
      response.writeHead(404).end();
      return;
    }
    const id = url.searchParams.get('id');
    const headerDelay = Number(url.searchParams.get('headers') || 0);
    const slowBody = url.searchParams.get('body') === 'slow';
    const event = { id, start: Date.now(), headerAt: null, firstBodyAt: null, endAt: null };
    serverEvents.push(event);
    setTimeout(() => {
      event.headerAt = Date.now();
      response.writeHead(200, {
        'Content-Type': 'application/octet-stream',
        'Content-Disposition': 'attachment; filename="timing-' + id + '.bin"',
        'Content-Length': bytes.length,
        'Cache-Control': 'no-store'
      });
      response.flushHeaders();
      event.firstBodyAt = Date.now();
      if (slowBody) {
        response.write(bytes.subarray(0, 1));
        setTimeout(() => {
          if (response.destroyed) return;
          response.end(bytes.subarray(1), () => { event.endAt = Date.now(); });
        }, 1800);
      } else {
        response.end(bytes, () => { event.endAt = Date.now(); });
      }
    }, headerDelay);
  });

  let chrome;
  let browser;
  try {
    await new Promise((resolve, reject) => {
      server.once('error', reject);
      server.listen(0, '127.0.0.1', resolve);
    });
    const port = server.address().port;
    chrome = spawn(chromePath, [
      '--headless=new', '--enable-unsafe-extension-debugging', '--no-first-run',
      '--no-default-browser-check', '--disable-background-networking', '--disable-default-apps',
      '--user-data-dir=' + profile, '--remote-debugging-port=0', '--remote-allow-origins=*', 'about:blank'
    ], { stdio: 'ignore', windowsHide: true });

    const portFile = await until(async () => {
      try { return (await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).trim(); }
      catch { return null; }
    }, 15000, 'Chrome did not enable remote debugging.');
    const [debugPort, endpoint] = portFile.split(/\r?\n/);
    browser = await Cdp.open('ws://127.0.0.1:' + debugPort + endpoint);
    const manifest = JSON.parse(await readFile(join(extension, 'manifest.json'), 'utf8'));
    const digest = createHash('sha256').update(Buffer.from(manifest.key, 'base64')).digest('hex').slice(0, 32);
    const extensionId = [...digest].map(value => String.fromCharCode(97 + parseInt(value, 16))).join('');
    await browser.send('Extensions.loadUnpacked', { path: testExtension });
    await browser.send('Target.createTarget', { url: 'chrome-extension://' + extensionId + '/transfers.html' });
    const worker = await until(async () => {
      const targets = (await browser.send('Target.getTargets')).targetInfos;
      return targets.find(target => target.type === 'service_worker'
        && target.url.startsWith('chrome-extension://' + extensionId + '/'));
    }, 10000, 'VESPER service worker did not start.');
    const workerSession = (await browser.send('Target.attachToTarget',
      { targetId: worker.targetId, flatten: true })).sessionId;
    await browser.send('Runtime.enable', {}, workerSession);

    const expression = `(async () => {
      await chrome.storage.sync.set({ intercept: false, route: 'chrome' });
      globalThis.__filenameTiming ||= [];
      chrome.downloads.onCreated.addListener(item => __filenameTiming.push({
        type: 'created', id: item.id, at: Date.now(), bytesReceived: item.bytesReceived,
        filename: item.filename, state: item.state
      }));
      chrome.downloads.onChanged.addListener(change => {
        if (change.bytesReceived || change.state) __filenameTiming.push({
          type: 'changed', id: change.id, at: Date.now(),
          bytesReceived: change.bytesReceived?.current, state: change.state?.current
        });
      });
      return true;
    })()`;
    const configured = await browser.send('Runtime.evaluate',
      { expression, awaitPromise: true, returnByValue: true }, workerSession);
    assert.equal(configured.result.value, true);

    const tab = (await browser.send('Target.createTarget', { url: 'about:blank' })).targetId;
    const session = (await browser.send('Target.attachToTarget',
      { targetId: tab, flatten: true })).sessionId;
    await browser.send('Page.enable', {}, session);
    const delays = [0, 100, 500, 2000];
    const modes = [
      { name: 'immediate-header-fast-body', headers: 0, body: 'fast' },
      { name: 'delayed-header-fast-body', headers: 700, body: 'fast' },
      { name: 'immediate-header-slow-body', headers: 0, body: 'slow' },
      { name: 'delayed-header-slow-body', headers: 700, body: 'slow' }
    ];
    const chromeVersion = (await browser.send('Browser.getVersion')).product;
    for (const mode of modes) for (const wait of delays) {
      const id = mode.name + '-wait-' + wait;
      const before = await evaluateWorker(browser, workerSession, '__filenameTiming.length');
      await browser.send('Runtime.evaluate', { expression: 'globalThis.__filenameDelay = ' + wait }, workerSession);
      const destination = `/download?id=${encodeURIComponent(id)}&headers=${mode.headers}&body=${mode.body}`;
      await browser.send('Page.navigate',
        { url: `http://127.0.0.1:${port}/launch?destination=${encodeURIComponent(destination)}` }, session);
      await browser.send('Runtime.evaluate', {
        expression: "document.querySelector('#download').click()", userGesture: true
      }, session);
      let atSuggest;
      let suggestionError;
      try { atSuggest = await until(async () => {
        const events = await evaluateWorker(browser, workerSession, `__filenameTiming.slice(${before})`);
        const event = events.find(item => item.type === 'suggest');
        if (!event) return null;
        const filenames = await readdir(output);
        const partial = filenames.find(name => name.includes(id) && name.endsWith('.crdownload'));
        return { event, partialVisible: Boolean(partial),
          partialSize: partial ? (await stat(join(output, partial))).size : null };
      }, 8000, 'Filename suggestion did not occur: ' + id); }
      catch (error) { suggestionError = error.message; }
      const complete = await until(async () => evaluateWorker(browser, workerSession,
        `chrome.downloads.search({}).then(items => items.find(item => item.filename.endsWith('timing-${id}.bin') && item.state === 'complete'))`),
      15000, 'Download did not complete: ' + id);
      const filename = join(output, 'timing-' + id + '.bin');
      assert.equal((await readFile(filename)).compare(bytes), 0);
      const observed = await evaluateWorker(browser, workerSession,
        `__filenameTiming.slice(${before}).filter(item => item.id === ${complete.id})`);
      if (!atSuggest) throw new Error(suggestionError + ' Chrome events: ' + JSON.stringify(observed)
        + ' Server: ' + JSON.stringify(serverEvents.find(item => item.id === id)));
      const created = observed.find(item => item.type === 'created');
      const callback = observed.find(item => item.type === 'filename');
      const suggested = observed.find(item => item.type === 'suggest');
      const serverEvent = serverEvents.find(item => item.id === id);
      assert.ok(created && callback && suggested, 'Missing Chrome event for ' + id);
      downloads.push({
        id, chromeVersion, requestedWaitMs: wait, headerDelayMs: mode.headers, bodyMode: mode.body,
        server: serverEvent,
        events: observed.map(item => ({ ...item, offsetMs: item.at - serverEvent.start })),
        onCreatedBytes: created.bytesReceived, onFilenameBytes: callback.bytesReceived,
        partialVisibleAtSuggest: atSuggest.partialVisible, partialSizeAtSuggest: atSuggest.partialSize,
        suggestAt: atSuggest.event.at, finalBytes: complete.fileSize
      });
    }
    process.stdout.write(JSON.stringify({ ok: true, browser: chromeVersion, measurements: downloads }, null, 2) + '\n');
  } finally {
    browser?.close();
    if (chrome && chrome.exitCode === null) {
      chrome.kill();
      if (process.platform === 'win32') {
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

assert.ok(chromePath, 'Set CHROME_PATH to Chrome for Testing or Chromium.');
await main();
