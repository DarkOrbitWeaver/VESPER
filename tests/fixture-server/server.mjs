#!/usr/bin/env node
// VESPER fixture server for ordinary-download interception. No dependencies; binds to 127.0.0.1 only.
//
//   node tests/fixture-server/server.mjs [port]        (default 8765; open http://127.0.0.1:8765/)
//
// Files are generated, never stored: any byte range of a 2 GiB file is produced on demand from a keyed
// counter-mode stream, and its SHA-256 is computed once and cached. See README.md for every route.
import http from 'node:http';
import crypto from 'node:crypto';
import zlib from 'node:zlib';
import { pathToFileURL } from 'node:url';

const BLOCK = 64 * 1024;
const MODIFIED = 'Tue, 02 Jan 2024 03:04:05 GMT';
const UNITS = { k: 1024, m: 1024 ** 2, g: 1024 ** 3 };
const MAX_GZIP = 16 * UNITS.m;
const MAX_LOGGED = 200;
const zeros = Buffer.alloc(BLOCK);

// What each case's response carries. "ranges: false" ignores Range entirely, as RFC 9110 14.2 allows.
export const CASES = {
  etag: { etag: '"fixture-v1"', modified: true, ranges: true, attachment: true },
  dated: { modified: true, ranges: true, attachment: true },
  weak: { etag: 'W/"fixture-v1"', ranges: true, attachment: true },
  bare: { ranges: true, attachment: true },
  norange: { etag: '"fixture-v1"', modified: true, ranges: false, attachment: true },
  inline: { etag: '"fixture-v1"', modified: true, ranges: true, attachment: false },
  gzip: { etag: '"fixture-gz"', ranges: false, attachment: true, gzip: true },
  once: { etag: '"fixture-v1"', modified: true, ranges: true, attachment: true, once: true },
  expiring: { etag: '"fixture-v1"', modified: true, ranges: true, attachment: true, expiring: true },
  cookie: { etag: '"fixture-v1"', modified: true, ranges: true, attachment: true, cookie: true }
};

export function sizeOf(text) {
  const match = /^(\d{1,4})([kmg])$/.exec(text || '');
  const bytes = match ? Number(match[1]) * UNITS[match[2]] : NaN;
  return Number.isSafeInteger(bytes) && bytes > 0 && bytes <= 4 * UNITS.g ? bytes : null;
}

const keyFor = name => crypto.createHash('sha256').update('vesper-fixture:' + name).digest().subarray(0, 16);

// Block n is AES-CTR keystream starting at counter n * 4096, so consecutive blocks never overlap.
function block(key, index) {
  const iv = Buffer.alloc(16);
  iv.writeBigUInt64BE(BigInt(index) * 4096n, 8);
  return crypto.createCipheriv('aes-128-ctr', key, iv).update(zeros);
}

export function* pieces(name, start, end) {
  const key = keyFor(name);
  for (let index = Math.floor(start / BLOCK); index * BLOCK <= end; index++) {
    const from = Math.max(start, index * BLOCK) - index * BLOCK;
    const to = Math.min(end, (index + 1) * BLOCK - 1) - index * BLOCK;
    yield block(key, index).subarray(from, to + 1);
  }
}

const tick = () => new Promise(resolve => setImmediate(resolve));
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

export function createFixtureServer({ port = 8765 } = {}) {
  const stats = new Map();
  const hashes = new Map();
  const usedOnce = new Set();
  const dropped = new Set();
  const gzipped = new Map();

  function note(path, request, status, sent = 0) {
    const entry = stats.get(path) || { requests: 0, ranged: 0, posts: 0, bytes: 0, statuses: {}, log: [] };
    entry.requests++;
    if (request.headers.range) entry.ranged++;
    if (request.method === 'POST') entry.posts++;
    entry.statuses[status] = (entry.statuses[status] || 0) + 1;
    entry.log.push({ at: Date.now(), method: request.method, range: request.headers.range || null, status,
      connection: request.socket.remotePort, sent });
    if (entry.log.length > MAX_LOGGED) entry.log.shift();
    stats.set(path, entry);
    return entry.log.at(-1);
  }

  async function sha(name, size) {
    const id = `${name}:${size}`;
    if (!hashes.has(id)) hashes.set(id, (async () => {
      const hash = crypto.createHash('sha256');
      let count = 0;
      for (const piece of pieces(name, 0, size - 1)) {
        hash.update(piece);
        if (++count % 256 === 0) await tick();
      }
      return hash.digest('hex');
    })());
    return hashes.get(id);
  }

  function json(response, status, value) {
    const body = JSON.stringify(value, null, 2);
    response.writeHead(status, { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body), 'Cache-Control': 'no-store' });
    response.end(body);
  }

  async function sendBody(request, response, name, start, end, { rate, drop, entry }) {
    const total = end - start + 1;
    const cut = drop ? start + Math.floor(total * drop) : Infinity;
    const slice = rate ? Math.max(1024, Math.min(BLOCK, Math.floor(rate / 10))) : BLOCK;
    let position = start;
    const began = Date.now();
    for (const piece of pieces(name, start, end)) {
      for (let offset = 0; offset < piece.length; offset += slice) {
        if (response.destroyed || request.socket.destroyed) return;
        const part = piece.subarray(offset, Math.min(piece.length, offset + slice));
        if (position + part.length > cut) { response.destroy(); return; }
        if (!response.write(part)) await new Promise(resolve => response.once('drain', resolve));
        position += part.length;
        entry.sent = position - start;
        if (rate) {
          const due = began + ((position - start) / rate) * 1000;
          if (due > Date.now()) await sleep(due - Date.now());
        }
      }
    }
    response.end();
  }

  async function serveFile(request, response, url, path, caseName, size, method) {
    const rules = CASES[caseName];
    const query = url.searchParams;
    const name = `${caseName}:${size}`;
    const refuse = status => { note(path, request, status); response.writeHead(status, { 'Content-Length': 0 }); response.end(); };

    if (rules.cookie && !/(?:^|;\s*)fixture_session=ok(?:;|$)/.test(request.headers.cookie || '')) return refuse(401);
    if (rules.expiring) {
      const expires = Number(query.get('exp'));
      if (!query.has('exp')) {
        const signed = new URL(url);
        signed.searchParams.set('exp', String(Date.now() + (Number(query.get('ttl')) || 15_000)));
        note(path, request, 302);
        response.writeHead(302, { Location: signed.pathname + signed.search, 'Content-Length': 0 });
        return response.end();
      }
      if (!Number.isFinite(expires) || Date.now() > expires) return refuse(403);
    }
    if (rules.once) {
      const token = `${path}?${query.get('token') || ''}`;
      if (usedOnce.has(token)) return refuse(410);
      usedOnce.add(token);
    }
    const stall = Number(query.get('stall'));
    if (stall > 0) await sleep(Math.min(stall, 60_000));

    const headers = { 'Content-Type': 'application/octet-stream', 'Cache-Control': 'no-store' };
    if (rules.etag) headers.ETag = rules.etag;
    if (rules.modified) headers['Last-Modified'] = MODIFIED;
    if (rules.ranges) headers['Accept-Ranges'] = 'bytes';
    if (rules.attachment || method === 'POST')
      headers['Content-Disposition'] = `attachment; filename="${(query.get('name') || `${caseName}-${size}.bin`).replace(/[^\w.-]/g, '_')}"`;

    if (rules.gzip) {
      if (size > MAX_GZIP) return refuse(413);
      if (!gzipped.has(name)) gzipped.set(name, zlib.gzipSync(Buffer.concat([...pieces(name, 0, size - 1)])));
      const body = gzipped.get(name);
      note(path, request, 200, body.length);
      response.writeHead(200, { ...headers, 'Content-Encoding': 'gzip', 'Content-Length': body.length });
      return response.end(request.method === 'HEAD' ? undefined : body);
    }

    let start = 0, end = size - 1, status = 200;
    const range = rules.ranges && /^bytes=(\d+)-(\d*)$/.exec(request.headers.range || '');
    const condition = request.headers['if-range'];
    if (range && (!condition || condition === rules.etag || condition === MODIFIED)) {
      start = Number(range[1]);
      end = range[2] ? Math.min(Number(range[2]), size - 1) : size - 1;
      if (start >= size || start > end) {
        note(path, request, 416);
        response.writeHead(416, { 'Content-Range': `bytes */${size}`, 'Content-Length': 0 });
        return response.end();
      }
      status = 206;
      headers['Content-Range'] = `bytes ${start}-${end}/${size}`;
    }
    headers['Content-Length'] = end - start + 1;
    const entry = note(path, request, status);
    response.writeHead(status, headers);
    if (request.method === 'HEAD') return response.end();

    // ?drop=0.4 cuts the connection once per address after 40 % of the body: a fault to recover from.
    const dropKey = path + url.search;
    const drop = Number(query.get('drop')) > 0 && Number(query.get('drop')) < 1 && !dropped.has(dropKey)
      ? Number(query.get('drop')) : 0;
    if (drop) dropped.add(dropKey);
    const rate = Number(query.get('rate')) > 0 ? Number(query.get('rate')) : 0;
    await sendBody(request, response, name, start, end, { rate, drop, entry });
  }

  async function handle(request, response) {
    const url = new URL(request.url, `http://${request.headers.host || '127.0.0.1'}`);
    const path = url.pathname;
    const parts = path.split('/').filter(Boolean);

    if (path === '/' && request.method === 'GET') {
      const body = indexPage();
      response.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Content-Length': Buffer.byteLength(body) });
      return response.end(body);
    }
    if (path === '/stats') return json(response, 200, Object.fromEntries(stats));
    if (path === '/reset' && request.method === 'POST') {
      stats.clear(); usedOnce.clear(); dropped.clear();
      return json(response, 200, { reset: true });
    }
    if (path === '/login') {
      response.writeHead(302, { 'Set-Cookie': 'fixture_session=ok; Path=/; HttpOnly; SameSite=Lax', Location: '/', 'Content-Length': 0 });
      return response.end();
    }
    if (parts[0] === 'sha' && CASES[parts[1]] && sizeOf(parts[2]))
      return json(response, 200, { case: parts[1], size: sizeOf(parts[2]), sha256: await sha(`${parts[1]}:${sizeOf(parts[2])}`, sizeOf(parts[2])) });
    if (parts[0] === 'redirect' && /^[1-9]$/.test(parts[1] || '') && parts.length > 2) {
      const left = Number(parts[1]) - 1;
      const target = '/' + parts.slice(2).join('/') + url.search;
      note(path, request, 302);
      response.writeHead(302, { Location: left ? `/redirect/${left}${target}` : target, 'Content-Length': 0 });
      return response.end();
    }
    if (parts[0] === 'file' && CASES[parts[1]] && sizeOf(parts[2]) && ['GET', 'HEAD'].includes(request.method))
      return serveFile(request, response, url, path, parts[1], sizeOf(parts[2]), request.method);
    // A form submission answered with a file. Every POST is counted: a download manager must never repeat it.
    if (parts[0] === 'form' && sizeOf(parts[1]) && request.method === 'POST') {
      request.resume();
      await new Promise(resolve => request.once('end', resolve));
      return serveFile(request, response, url, path, 'etag', sizeOf(parts[1]), 'POST');
    }
    note(path, request, 404);
    response.writeHead(404, { 'Content-Length': 0 });
    response.end();
  }

  const server = http.createServer((request, response) => {
    handle(request, response).catch(() => { if (!response.headersSent) response.writeHead(500); response.end(); });
  });
  return {
    server, stats, sha,
    listen: () => new Promise((resolve, reject) => {
      server.once('error', reject);
      server.listen(port, '127.0.0.1', () => resolve(`http://127.0.0.1:${server.address().port}`));
    }),
    close: () => new Promise(resolve => { server.closeAllConnections?.(); server.close(resolve); })
  };
}

function indexPage() {
  const sizes = ['5m', '200m', '2g'];
  const rows = Object.keys(CASES).filter(name => name !== 'gzip').map(name =>
    `<tr><th>${name}</th>${sizes.map(size => `<td><a href="/file/${name}/${size}">${size}</a> · <a href="/file/${name}/${size}?rate=262144">slow</a></td>`).join('')}</tr>`).join('\n');
  return `<!doctype html><meta charset="utf-8"><title>VESPER interception fixture</title>
<style>body{font:14px system-ui;margin:2rem;max-width:60rem}td,th{padding:.25rem .75rem;text-align:left}code{background:#eee;padding:0 .25rem}</style>
<h1>VESPER interception fixture</h1>
<p>Reset with <code>POST /reset</code>. Read request counts at <a href="/stats">/stats</a> and hashes at <code>/sha/&lt;case&gt;/&lt;size&gt;</code>.
“slow” serves at 256 KiB/s. Add <code>?drop=0.4</code> to cut the connection once at 40 %.</p>
<table>${rows}
<tr><th>gzip</th><td><a href="/file/gzip/5m">5m</a></td><td colspan="2">encoded responses stay with the browser</td></tr></table>
<h2>Other ways a download starts</h2>
<p><a href="/redirect/3/file/etag/5m">Three redirects, then a file</a> · <a href="/login">Sign in for the cookie case</a></p>
<form method="post" action="/form/5m"><input type="hidden" name="order" value="one-time"><button>POST form that answers with a file</button></form>
<p><button id="countdown">Download after a 5 second countdown</button>
<button id="script">Download started by script (fetch, then blob)</button>
<button id="blob">File the page builds itself (blob:)</button></p>
<script>
document.getElementById('countdown').onclick = event => {
  let left = 5;
  const timer = setInterval(() => {
    event.target.textContent = left ? 'Starting in ' + left-- + '…' : 'Download after a 5 second countdown';
    if (left < 0) { clearInterval(timer); location.href = '/file/etag/5m?name=countdown.bin'; }
  }, 1000);
};
document.getElementById('script').onclick = async () => {
  const blob = await (await fetch('/file/etag/5m')).blob();
  const link = Object.assign(document.createElement('a'), { href: URL.createObjectURL(blob), download: 'scripted.bin' });
  link.click();
  setTimeout(() => URL.revokeObjectURL(link.href), 1000);
};
document.getElementById('blob').onclick = () => {
  const link = Object.assign(document.createElement('a'),
    { href: URL.createObjectURL(new Blob([new Uint8Array(1024 * 1024)])), download: 'page-made.bin' });
  link.click();
  setTimeout(() => URL.revokeObjectURL(link.href), 1000);
};
</script>`;
}

if (import.meta.url === pathToFileURL(process.argv[1] || '').href) {
  const fixture = createFixtureServer({ port: Number(process.argv[2]) || 8765 });
  fixture.listen().then(address => console.log(`VESPER fixture server listening on ${address}/`));
}
