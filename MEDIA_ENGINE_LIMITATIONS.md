# Media engine limits

Current scope on `main`, 2026-11-12. Live-site results in [the compatibility matrix](MEDIA_ENGINE_COMPATIBILITY.md) are from 2026-11-11 and may change as providers change their sites.

## Transfer and media processing

- The engine downloads direct HTTP and HTTPS files and supports single-representation HLS and DASH segment plans. It refuses live capture and DRM-protected manifests.
- The in-process C# remuxer joins compatible, clear HTTP video and audio tracks into MP4, WebM, or MKV without re-encoding. It validates the output before publication. Separate HLS audio renditions and separate DASH segment-list tracks are not merged.
- VESPER does not bundle or invoke ffmpeg, yt-dlp, or Python. A narrow Windows Media Foundation conversion path uses codecs available on Windows; it is not a general-purpose transcoder.
- Subtitle and caption metadata may be listed, but subtitle downloads and muxing into media outputs are not supported.

## Site discovery

- YouTube formats that require player JavaScript signature or `n` challenge execution are omitted. VESPER does not manufacture proof-of-origin tokens or bypass DRM, sign-in, age, or bot checks. Browser-assisted discovery can contribute page evidence, but it does not execute those challenges.
- Live streams are detected and refused. A completed recording can be downloaded when its provider exposes a supported completed-file representation.
- Provider support is implementation-specific and can change without notice. The 2026-11-11 live matrix recorded 18 of 20 cases in its defined supported set; YouTube Mix and one Vimeo sample were not resolved successfully.
- The generic remuxer requires clear, structurally supported inputs and known sizes for each track. It does not repair damaged or mismatched tracks, and it does not transcode unsupported codecs into a supported container.

## Browser and recovery boundaries

- The extension can keep Chrome-owned transfers when Chrome's network connection is required. Native helper registration does not enable the extension; Chrome approval remains a separate action.
- The extension's optional cookie permission defaults off. If the user enables **Use site sign-in**, credentials remain scoped to the consented source origin.
- A recovered media merge must retain the same provider identity, track roles, format IDs, sizes, and codecs. If the refreshed source cannot prove that identity, VESPER keeps the partial data and asks for a new link or user action.

For current build status, automated results, measured performance, and Windows checks still pending, see [release status](DOCUMENTATION/RELEASE.md).
