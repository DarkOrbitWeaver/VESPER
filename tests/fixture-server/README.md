# Interception fixture server

A local HTTP server for testing how ordinary downloads are handed from the browser to VESPER.
It has no dependencies, binds to `127.0.0.1` only, and stores nothing: every file is generated on demand,
so a 2 GiB file costs no disk space and any byte range of it can be served immediately.

```
node tests/fixture-server/server.mjs          # http://127.0.0.1:8765/
node tests/fixture-server/server.mjs 9000     # another port
```

Open the index page in the browser under test. It links every case in three sizes and has buttons for a
countdown download, a script-started download, a page-built `blob:` file and a POST form.

## Files

`/file/<case>/<size>` where size is a number followed by `k`, `m` or `g` (`5m`, `200m`, `2g`; at most 4 GiB).

| Case | Response |
|---|---|
| `etag` | strong ETag, Last-Modified, byte ranges, `Content-Disposition: attachment` |
| `dated` | Last-Modified only (no ETag), byte ranges, attachment |
| `weak` | weak ETag, byte ranges, attachment |
| `bare` | no validator at all, byte ranges, attachment |
| `norange` | strong ETag, but `Range` is ignored and the whole file is sent |
| `inline` | like `etag` without `Content-Disposition` (a plain link to a file) |
| `gzip` | `Content-Encoding: gzip` (sizes up to 16 MiB) |
| `once` | the first request for an address gets the file, every later one gets 410 |
| `expiring` | redirects to a signed address (`?exp=`); 403 once it has expired. `?ttl=<ms>` sets its lifetime (default 15 s) |
| `cookie` | 401 unless the request carries the cookie set by `/login` |

Query options, combinable: `?rate=<bytes per second>` throttles, `?stall=<ms>` delays the first byte,
`?drop=<fraction>` cuts the connection once per address after that share of the body, `?name=<file name>`.

## Other routes

| Route | Purpose |
|---|---|
| `/redirect/<n>/<path>` | `n` redirects (1–9), then `<path>` |
| `POST /form/<size>` | a form submission answered with a file; every POST is counted |
| `/login` | sets the cookie for the `cookie` case |
| `/sha/<case>/<size>` | SHA-256 of that file, to compare with what was saved |
| `/stats` | per path: request count, ranged requests, POSTs, status codes, and a log with time, `Range`, status, connection and bytes sent |
| `POST /reset` | clears the statistics and forgets used one-use addresses and applied drops |

## What to check after a download

1. `Get-FileHash <saved file> -Algorithm SHA256` equals `/sha/<case>/<size>`.
2. `/stats` shows who asked for what: one browser request, plus VESPER's ranged requests when it took the file over.
   A POST case must show exactly one POST. A `once` case shows at most one refused request (410).
3. Exactly one finished entry exists, in VESPER or in the browser, never both.

## Replaying the corpus without a browser

`node scripts/replay-interception-corpus.mjs` starts this server in-process and runs every case of
`tests/corpus/interception-corpus.json` through the extension's real takeover code. The HTTP responses and the
extension scripts are real; the browser's download item and the desktop app's answer are modeled. It reports
which branch the code takes for each response. It does not measure a browser.
