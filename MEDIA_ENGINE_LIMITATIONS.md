# Media engine limits

Current scope on `main`, 2026-11-13. See the [live compatibility matrix](MEDIA_ENGINE_COMPATIBILITY.md); provider behavior can change without notice.

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

- The extension can keep Chrome-owned transfers when Chrome's network connection is required. Native helper registration does not enable the extension; Chrome approval remains a separate action.
- The current UI test environment exposes no native Chrome browser, so live Chrome interception/native messaging and live YouTube/Instagram/TikTok journeys remain unverified. The manual workflow matrix records the exact checks to perform; extension unit tests do not prove live-site behavior.
- The extension's optional cookie permission defaults off. If the user enables **Use site sign-in**, credentials remain scoped to the consented source origin.
- Recovered media merges require the same provider identity, track roles, format IDs, sizes, and codecs. If a refreshed source cannot prove that identity, VESPER keeps partial data and asks for a new link or user action.

For current build status, automated results, measured performance, and Windows checks still pending, see [release status](DOCUMENTATION/RELEASE.md) and the [manual Windows workflow checklist](DOCUMENTATION/MANUAL_WINDOWS_TEST_CHECKLIST.md).
