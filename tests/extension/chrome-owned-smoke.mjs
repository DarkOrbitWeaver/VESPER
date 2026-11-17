// Opt-in real Chrome smoke check: never uses the normal profile or starts VESPER's native host.
// Run with Chrome for Testing or Chromium, NOT regular Chrome 137+:
//   $env:CHROME_PATH = 'C:\\path\\to\\chrome-for-testing\\chrome-win64\\chrome.exe'
//   node tests/extension/chrome-owned-smoke.mjs
// Chrome 137+ ignores --load-extension, so an ordinary installed Chrome cannot run this check.
// The test explicitly disables interception before issuing a local, verifiable browser download.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { access, mkdtemp, mkdir, readFile, rm } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const extension = join(root, 'extension');
const chromeExe = process.env.CHROME_PATH;
const manifest = JSON.parse(await readFile(join(extension, 'manifest.json'), 'utf8'));
const digest = createHash('sha256').update(Buffer.from(manifest.key, 'base64')).digest('hex').slice(0, 32);
const extensionId = [...digest].map(digit => String.fromCharCode(97 + parseInt(digit, 16))).join('');
const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

class Connection {
  constructor(socket) {
    this.socket = socket;
    this.nextId = 0;
    this.pending = new Map();
    socket.addEventListener('message', event => {
      const message = JSON.parse(event.data);
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
      socket.addEventListener('error', () => reject(new Error('Cannot open the isolated Chrome debugger.')), { once: true });
    });
    return new Connection(socket);
  }

  send(method, params = {}, sessionId) {
    const id = ++this.nextId;
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error('Chrome did not answer: ' + method));
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
  await access(chromeExe);
  const profile = await mkdtemp(join(root, '.chrome-ownership-smoke-'));
  const downloadFolder = join(profile, 'downloads');
  await mkdir(downloadFolder);
  const bytes = Buffer.alloc(256 * 1024, 0x5a);
  const server = createServer((request, response) => {
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
      '--no-first-run',
      '--no-default-browser-check',
      '--disable-background-networking',
      '--disable-default-apps',
      '--user-data-dir=' + profile,
      '--remote-debugging-port=0',
      '--remote-allow-origins=*',
      '--disable-extensions-except=' + extension,
      '--load-extension=' + extension,
      'about:blank'
    ], { stdio: 'ignore', windowsHide: true });

    const activePort = await waitFor(async () => {
      try { return (await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).trim(); }
      catch { return null; }
    }, 12000, 'Isolated Chrome did not enable remote debugging.');
    const [debugPort, path] = activePort.split(/\r?\n/);
    browser = await Connection.open('ws://127.0.0.1:' + debugPort + path);

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

    async function evaluate(expression) {
      const reply = await browser.send('Runtime.evaluate',
        { expression, awaitPromise: true, returnByValue: true }, workerSession);
      if (reply.exceptionDetails) throw new Error('Extension worker rejected a test command.');
      return reply.result.value;
    }

    await evaluate("(async () => { await chrome.storage.sync.set({ intercept: false, route: 'chrome' });"
      + "globalThis.__ownershipSmoke = { created: [], changed: [] };"
      + "chrome.downloads.onCreated.addListener(d => __ownershipSmoke.created.push("
      + "{ id:d.id, state:d.state, bytesReceived:d.bytesReceived, totalBytes:d.totalBytes }));"
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
      const matches = await evaluate("chrome.downloads.search({})");
      return matches.find(candidate => candidate.filename?.endsWith('vesper-browser-owned-fixture.bin')
        && candidate.state === 'complete');
    }, 15000, 'Chrome did not finish the isolated fixture download.');
    const observations = await evaluate('globalThis.__ownershipSmoke');
    const saved = await readFile(join(downloadFolder, 'vesper-browser-owned-fixture.bin'));
    assert.equal(saved.compare(bytes), 0, 'Chrome did not save the source bytes exactly.');
    assert.equal(observations.created.length, 1, 'The extension missed or duplicated the Chrome download event.');
    assert.equal(observations.created[0].id, item.id);
    assert.equal(item.state, 'complete');
    assert.ok(item.filename.startsWith(downloadFolder), 'The download escaped the isolated test folder.');
    const decisions = await evaluate("chrome.storage.session.get('vesperCaptureDecisions')");
    assert.ok(!decisions.vesperCaptureDecisions?.some(entry => entry.outcome === 'moved'),
      'A Chrome-owned file was falsely marked as a VESPER download.');
    process.stdout.write(JSON.stringify({
      ok: true,
      browser: 'Chrome (isolated headless profile)',
      createdBytes: observations.created[0].bytesReceived,
      changedEvents: observations.changed.length,
      completedBytes: saved.length,
      keptInChrome: true,
      nativeMessagingInvoked: false
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
  process.stderr.write('This opt-in test needs CHROME_PATH pointing to a separate Chrome for Testing or Chromium executable. Regular Chrome 137+ cannot load unpacked extensions through --load-extension. No browser was started.\n');
  process.exitCode = 2;
} else {
  await main();
}
