# Browser-to-native discovery message: `vesper.discovery/1`

One JSON object per native-messaging message (UTF-8, camelCase). Maximum 256 K characters. The native host passes the string to
`BrowserMessageParser.Parse(json)` and the result to `MediaDiscoveryRequest.Browser`. **Everything in it is untrusted** and is validated
by `BrowserMessageValidator` before use.

```json
{
  "schema": "vesper.discovery/1",
  "pageUrl": "https://example.com/watch/42",
  "title": "Page title",
  "videoId": "42",
  "referrer": "https://example.com/",
  "userAgent": "Mozilla/5.0 ...",
  "candidates": [
    { "url": "https://cdn.example.net/stream/master.m3u8", "kind": "hls", "mimeType": "application/vnd.apple.mpegurl",
      "width": 1920, "height": 1080, "bitrate": 5000000, "durationSeconds": 212, "label": "1080p", "source": "network" },
    { "url": "https://cdn.example.net/clip.mp4", "kind": "direct", "height": 720, "source": "video-element" }
  ],
  "subtitles":   [ { "url": "https://example.com/subs/en.vtt", "language": "en", "label": "English", "format": "vtt" } ],
  "audioTracks": [ { "language": "en", "label": "English" } ],
  "authorization": {
    "origin": "https://example.com",
    "consentGranted": true,
    "cookieHeader": "sid=...",
    "headers": { "Authorization": "Bearer ..." },
    "allowedHosts": [ "media.example.com" ],
    "expiresAt": "2026-01-01T12:00:00Z"
  }
}
```

| Field | Rules |
|---|---|
| `schema` | must start with `vesper.discovery/1` (required) |
| `pageUrl` | required; http/https, no credentials, public host (private/loopback/link-local rejected) |
| `candidates[]` | at most 64 considered; invalid or private URLs dropped with a warning; `kind` = `direct`/`hls`/`dash`/omitted (sniffed); dimensions sanity-checked; **each candidate is still verified by a probe or a manifest parse** |
| `subtitles[]`, `audioTracks[]` | metadata only; URLs validated |
| `authorization` | optional. **Ignored unless `consentGranted` is true.** `origin` must equal the page origin or the whole message is rejected. Expired -> rejected. |
| `authorization.cookieHeader` | <= 8192 chars, no CR/LF/NUL (header injection -> rejected) |
| `authorization.headers` | only `Authorization` and `Accept-Language` are honoured; others dropped with a warning |
| `authorization.allowedHosts` | at most 5; each must be the page host or one of its subdomains; otherwise dropped. Credentials never go anywhere else (CDNs on other domains get no cookies) |

## What the extension must do (the library cannot enforce this part)

* Ask the user before attaching `authorization`; set `consentGranted` from that explicit answer, per page/origin.
* Never export all cookies. Send only the `Cookie` header value the browser would send to **that page's origin**, and only when the
  discovery without it failed with `BrowserMayHelp`.
* Never store passwords or send unrelated sites' credentials.
* Send candidates the page itself requested (video elements, observed manifest/stream requests); do not scrape or guess.
* Do not implement a downloader: the extension discovers, VESPER downloads.

## Responses

The native host answers with whatever VESPER's UI needs; recommended: `{"ok":true,"menu":[...]}` or
`{"ok":false,"code":"BrowserContextRequired","message":"...","retryable":false}` built from `MediaError`. Never echo `cookieHeader`.
