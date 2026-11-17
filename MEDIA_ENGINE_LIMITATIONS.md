# Media engine limits

Current implementation package based on `2620e76`, updated 2026-11-15. See the [live compatibility matrix](MEDIA_ENGINE_COMPATIBILITY.md); provider behavior can change without notice.

## Transfer and media processing

- Direct HTTP and HTTPS files are supported. The native engine also handles completed HLS and clear DASH plans, separate video/audio rendition merging, time ranges on completed segmented HLS/DASH plans, MPEG-TS HLS segments, and recoverable single-rendition HLS live recording.
- HLS live recording requires one usable rendition with audio included. It can resume from committed segment sequences and stop while keeping the playable prefix. Separate live audio/video renditions, live time ranges, and DRM-protected or unsupported encryption formats are not supported.
- Compatible clear audio/video tracks are remuxed without re-encoding into MP4, M4A, WebM, or MKV. Output is validated before publication. Damaged or mismatched tracks and unsupported codecs are not repaired or transcoded into supported codecs.
- Direct MP3 uses ID3v2.4/APIC; FLAC uses Vorbis Comments/PICTURE; Ogg Vorbis and Opus use their codec comment packets and `METADATA_BLOCK_PICTURE`. These rewrites preserve encoded audio bytes. A well-formed ID3v2.4 tag with no global flags can be merged: supplied fields replace matching fields, missing source values keep existing fields, and a valid source cover replaces only the existing front cover. Older ID3 versions, flagged or malformed tags, and ambiguous cover frames are kept unchanged, so new metadata or artwork may not be applied. Ogg tagging rejects chained, multiplexed, malformed, or oversized comment streams and leaves the original audio untouched.
- Native MP4/M4A outputs use MP4 metadata atoms; Matroska outputs use tags, chapters, and cover attachments. WebM outputs use supported tags and can carry a cover attachment. Direct WAV, AIFF, and other unlisted audio formats are downloaded unchanged without automatic metadata embedding.
- `wmplayer.exe` is installed, but this session's UI-control surface exposes no native-app controls, so a Windows player display check was not possible. The real Bandcamp MP3 and generated FLAC/Vorbis/Opus/WebM outputs were inspected with FFprobe and fully decoded with FFmpeg 8.1.1; the manual checklist keeps the player check open.
- VESPER does not bundle or invoke FFmpeg, yt-dlp, or Python. Its Windows Media Foundation conversion path uses codecs available on Windows and is not a general-purpose transcoder. The native media validator does not parse direct MP3, FLAC, Ogg, or Opus files; independent inspection is used for those formats.

## Subtitles and chapters

- Subtitle selection is optional and defaults to Off. Supported direct SRT, VTT, and TTML tracks are saved as separate files. Completed HLS WebVTT playlists can be combined and converted to SRT; encrypted or live HLS subtitles are not supported.
- Embedded subtitle tracks are not supported by the current muxers. The merge plan represents audio/video tracks only, subtitle choices become independent sidecar jobs, and the output demuxers do not validate subtitle tracks. VESPER does not burn subtitles into video.
- Source chapters are written where the selected MP4 or Matroska output supports them. VESPER does not invent missing metadata or artwork.

## Site discovery

- YouTube formats that require player JavaScript signature or `n` challenge execution are omitted. VESPER does not manufacture proof-of-origin tokens or bypass DRM, sign-in, age, or bot checks. Browser-assisted discovery can contribute page evidence, but it does not execute those challenges.
- Live YouTube and other provider-specific live pages may be refused even though the generic engine can record supported HLS live playlists. Completed recordings can be downloaded when a provider exposes a supported completed-file representation.
- Provider support is implementation-specific. The 2026-11-11 live matrix recorded 18 of 20 cases in its defined supported set; YouTube Mix and one Vimeo sample were not resolved. The public Bandcamp test track was rechecked on 2026-11-13 with FFprobe tag/art inspection and a full FFmpeg decode.

## Browser and recovery boundaries

- Chrome-owned downloads stay in Chrome. VESPER no longer offers moving active Chrome downloads, scans Chrome history to adopt work, or mirrors Chrome's live downloads into its own job list. Unaccepted and abandoned capture attempts are hidden from the VESPER list, including on restart. Native helper registration does not enable the extension; Chrome approval remains separate.
- Automatic capture is limited to downloads observed at creation in the current browser session (within 15 seconds) for which Chrome reports zero received file bytes. VESPER checks replayability, identity and native transfer readiness before pausing Chrome and accepting its own transfer. If Chrome already received bytes, finishes, changes source, or the native helper fails, Chrome owns the download; VESPER never imports its completed file. This cannot guarantee pre-start interception on Chrome MV3. POST/blob/single-use/bearer-only requests stay in Chrome. Optional site sign-in requires explicit host consent and does not persist credentials.
- The current UI test environment exposes no native Chrome browser, so live Chrome interception/native messaging and live YouTube/Instagram/TikTok journeys remain unverified. The manual workflow matrix records the exact checks to perform; extension unit tests do not prove live-site behavior.
- The extension's optional cookie permission defaults off. If the user enables **Use site sign-in**, credentials remain scoped to the consented source origin.
- Recovered media merges require the same provider identity, track roles, format IDs, sizes, and codecs. If a refreshed source cannot prove that identity, VESPER keeps partial data and asks for a new link or user action.

For current build status, automated results, measured performance, and Windows checks still pending, see [release status](DOCUMENTATION/RELEASE.md) and the [manual Windows workflow checklist](DOCUMENTATION/MANUAL_WINDOWS_TEST_CHECKLIST.md).

## Conversion and metadata (current behaviour)

- "M4A — plays everywhere" copies a native AAC stream when the source has one. Only when there is none is the best audio (usually Opus) decoded and re-encoded to AAC with Windows Media Foundation, and only when the PC has that decoder. Re-encoding cannot add quality.
- "Plays everywhere (MP4)" re-encodes video with Media Foundation when the source codec cannot be copied into MP4; the offer is hidden when this PC lacks the decoder.
- Converted M4A and MP4 files keep the discovered title, artist, album and cover. The converted samples are stream-copied, never re-encoded a second time. If tagging fails the valid untagged file is published with a note.
- HLS VOD segments are fetched three at a time and committed in order; AES-128 segments are still decrypted whole in memory (bounded to 64 MiB per segment).
- Time ranges ("Download part") are supported for segmented HLS/DASH sources and cut at segment boundaries. Native Opus packet-copy output is available for supported unlaced MP4/WebM audio. Windows decode-to-WAV output is offered for available source decoders; its PCM helper supports sample-exact clips and INFO tags. WAV clipping currently downloads and decodes the full source, uses RIFF (not RF64), and does not embed cover art. MP3/FLAC transcoding, indexed byte-range clips, embedded subtitle tracks and HLS discontinuity epochs remain unimplemented.

## Package-specific validation and remaining boundaries

- The supplied public TikTok example (`@skzxtvi/video/7707740473833573665`) was downloaded through the real native resolver/planner/engine on Linux: 400,577 bytes, AAC audio and 1080x1920 HEVC, native validation passed. This is not a Windows playback or Chrome integration result. Signed URLs and anonymous cookies were not bundled in reports.
- TikTok media requests preserve per-response cookie Domain/Path/Secure/expiry rules, bind each resulting credential context to its media origin, and keep secrets in memory. Browser sign-in stays explicitly opt-in; frame-scoped CHIPS lookups use Chrome's own partition key. Unknown-originating-frame Chrome downloads do not gain guessed partition access.
- The browser picker now carries audio-track, subtitle, output and range intent to the same planner as the desktop. Subtitle sidecars never inherit media credentials. Smart Select proposes bounded reusable links and always opens native batch review; URL-less POST/blob-generating buttons remain browser-owned.
- Media Foundation sink throttling is disabled for offline conversion and eligible hardware MFTs are enabled. Actual hardware selection, drivers, WAV sink behavior and speed gains need Windows checks. No DXVA/D3D-device-manager implementation is claimed.
- Both HLS and DASH prefetch three fragments and commit in order. Pending requests are canceled and drained when a transfer stops. Whole-object buffering/decryption limits remain; these are not streaming AES implementations.
- See `DOCUMENTATION/IMPLEMENTATION-2026-11.md`, `DOCUMENTATION/STATUS.tsv`, and `DOCUMENTATION/LUNA-WINDOWS.md` for exact implemented, inherited, open and externally blocked work. This package is not complete closure of the audit backlog.
