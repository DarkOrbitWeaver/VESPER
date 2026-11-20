# Owner design required

This document holds only systems that need a product or architecture decision from the owner. Their approved requirements remain open until the accepted design is implemented and verified.

## Browser media identity and format choices

**Workstream and scope:** Browser player detection, media discovery, and the shared desktop and extension picker.

**Requirements and sources:** VES-024, VES-037, VES-062, USER-INSTAGRAM, USER-UI-CHROME, AUDIT-EXTRA-021. The owner's 2026-11-20 screenshots show a missing butterfly after entering Reels, many numbered MP4 sources for one Reel, and a failed choice for TikTok video 7688010685061664021. The TikTok tab separately showed HTTP 403 on refresh.

**Expected workflow:** Opening the butterfly on the current video shows real choices such as Best quality, Smallest and Audio only when supported. A scroll, in-page navigation, pause, temporary preview, ad, or iframe should never cause a download of a different video.

**Current behavior:** `extension/content.js` selects visible video elements and binds activations to a page and player generation. `extension/background.js` observes media responses and asks the native resolver for options. Only fallback sources are numbered. `BrowserMediaOptions.cs` and the shared media planner already supply validated choices. The extension fixtures cover page binding, late responses, partial ranges and carousel alternatives.

**What is missing:** Timing, frame and MIME type alone do not prove that a request belongs to a particular blob or Media Source player, that two CDN URLs represent the same version, or that a response includes audio. A working page can therefore produce a long fallback list or an option that expires before selection. A signed-in browser and desktop probe can also receive different server responses.

**Why owner design is required:** A common browser-to-desktop evidence model is needed to match observed responses to individual players, group representations and choose when a provider adapter is necessary. Provider-specific sleeps, CDN guesses, and size-based ranking would leave the same failure unresolved.

**Quality requirements:** Bound lookup latency, memory, requests, and concurrent probes. Keep idle work low. Maintain truthful format labels, player identity through scroll and navigation, and explicit uncertainty when a source cannot be verified.

**Constraints:** Keep Chrome MV3, the custom C# engine, existing permissions and credential scope, the butterfly and desktop workflow, and Chrome's ownership of its downloads. Preserve signed URLs only for their bounded lifetimes and keep secrets out of logs and storage. Respect DRM and site access refusals.

**Design questions:** What evidence binds a player to its asset across preloading, ads, adaptive tracks, and frame changes? How do desktop and browser share verified choices and refresh an expired URL safely? Which common checks replace site-specific rules, and which cases still require provider adapters?

**Integration and acceptance:** Test public and authorized TikTok/Instagram videos and carousels, YouTube previews, and a generic HLS/DASH/direct-video corpus in disposable Chrome. Verify the selected item, advertised quality and tracks, playable output, ambiguity and server refusal, and bounded latency and resource use. Include cases where the visible video changes during a lookup.

**Dependencies and independent work:** The design blocks robust automatic identification and grouping of ambiguous network sources. Known navigation, retry and error handling fixes, current Chrome/native verification, desktop workflows and installer checks can proceed independently.

**Status:** OWNER DESIGN REQUIRED

## Media processing performance and architecture

**Affected workstream:** media conversion, remux and merge, output validation, memory and disk use, and performance measurement.

**Related requirements:** VES-006, VES-030, VES-031, VES-033, VES-035, VES-036, USER-MEDIA-MEMORY, USER-PERFORMANCE, VSP-008, AUDIT-EXTRA-007, AUDIT-EXTRA-012, and AUDIT-EXTRA-016. The original audit source index maps VES-006 to OPUS section E, lines 693–744; VES-030 through VES-036 to section H, lines 1185–1268; USER-PERFORMANCE/VSP-008 to section M and the Q.6 benchmark plan. See `CLAUDE-AUDIT-SOURCE-INDEX.md`, `MASTER-TODO.md`, `STATUS.tsv`, `AUDIT-RECONCILIATION.md`, and `VSP-INDEPENDENT-RECONCILIATION.md` for traceability. Historical implementation and media reports are evidence, not current behavior authority.

**What VESPER must accomplish:** Keep every approved media workflow and format truthful and usable. Use stream copy when the selected output can preserve the input codecs. Decode and re-encode only when the requested output genuinely requires it. Preserve sample and timeline correctness, metadata behavior, subtitles and sidecars, cancellation, crash recovery, durable publication, privacy, and the existing C#/.NET 10, WinForms, Chrome MV3, and lightweight-runtime decisions. Make processing fast and resource-efficient on owner-approved modest hardware without unsupported speed claims.

**What exists now:**

- `src/Vesper.Engine/Media/MediaConversion.cs` chooses among compatible stream-copy remux, injected conversion, Windows Media Foundation conversion, native Opus packet copy, and WAV writing. It stages, hashes, validates, tags, and publishes outputs. `WindowsMediaConverter.cs` owns the Media Foundation path.
- `MediaMerger` and the native muxers/demuxers under `src/Vesper.Engine/Media/Mux/` handle MP4, M4A, WebM, and Matroska remuxing. `ValidateRemux` rereads output and compares media structure and samples before publication.
- Known full passes that need phase timing: compatible remux writes the output, `ValidateRemux` rereads it, and final SHA-256 calculation reads it again. Tag embedding can add a second remux plus validation before hashing. These are established code paths, not proven unnecessary work; no current 40 MiB trace measures their cost or proves a pass can be safely combined. WAV clip output decodes the full source, then validates and hashes the prepared WAV. Continuous HLS TS avoids timeline copying; discontinuity rebasing rewrites a segment buffer. Keep these costs visible in the benchmark instead of attributing all post-download time to “conversion.”
- `HlsTransfer.cs`, `DashTransfer.cs`, `MergeTransfer.cs`, and `SegmentedMergeTransfer.cs` handle segmented fetch, merge, and recovery. Segment backing arrays have a weighted 256 MiB / three-buffer admission limit; this is not a process-wide memory cap. Whole-object AES decryption and several scratch buffers remain bounded per object rather than streamed.
- `WaveAudio.cs` writes sample-exact PCM clips and RF64 when needed. WAV clipping decodes the full source. Capability-gated MP3 conversion is now implemented and exercised on this host; comparative timing and player compatibility remain unverified. FLAC output integration, real greater-than-4-GiB WAV conversion, codec/device negotiation, and other player checks remain open in `STATUS.tsv`.
- `TextSubtitleParser.cs` and the current merge path add opt-in SRT/WebVTT embedding where supported while preserving selected sidecars. Current implementation and test status must be rechecked against source when an owner design returns.
- `tests/Vesper.Tests/MediaConversionTests.cs`, `MediaMergeTests.cs`, `MediaMergerPayloadValidationTests.cs`, and related tests cover synthetic fixtures, correctness, recovery, and selected timing paths. `scripts/Measure-Performance.ps1` records comparative system and media measurements.

**Concrete uncertainty:** The owner reports that a file around 40 MB can take minutes after its download finishes. That case has not been reproduced in the current environment: no exact input, source format, selected output, processing steps, machine/storage details, command, or timing trace is available. The time could be dominated by a true decode/re-encode, remuxing, audio/video merge, metadata or format probing, subtitles, output validation, hashing, disk I/O, or repeated copies/reads. These operations have different expected costs and cannot share one meaningful speed target.

The available local remux timing log (`release/audit-evidence/full-engine-batch-20261119.log`) measured 8.9 MiB synthetic MP4 and 9.4 MiB synthetic WebM outputs. MP4 merge p50 was 21.058 ms and validation p50 44.639 ms; WebM merge p50 was 200.761 ms and validation p50 70.135 ms. The run failed later because the Matroska benchmark helper lost edit-list metadata, so these exploratory fixture timings are not a verified performance baseline. They do not measure a 40 MB workload, Windows Media Foundation transcoding, or end-to-end publication. Historical reports linked in the handoff do not resolve that gap.

**Why owner design is required:** The reported delay is material, but its cause and desired user-visible timing are unspecified. Selecting a shared processing pipeline, separate fast paths, different validation scheduling, hardware transforms, streaming conversion, or another architecture would change compatibility, resource use, and correctness trade-offs. The current evidence is not sufficient to justify replacing the existing pipeline or to claim a particular target. This defers only the architecture and target-setting decision; concrete correctness bugs, documented output support, and independent media features continue to be implemented.

**Requirements the design must preserve:**

- Correct stream copy/remux without decoding when codecs and container allow it; correct audio/video synchronization when streams are merged.
- Explicit separation of metadata inspection, tag/artwork work, subtitle processing, decoder/encoder work, validation, hashing, temporary stages, flushes, and final publication.
- No weakening of output validation, ownership checks, atomic publication, cancellation, restart/recovery, privacy, or existing format support to improve benchmark numbers.
- Accurate UI language when an operation must transcode or is unsupported. No implicit FFmpeg, yt-dlp, Python, large runtime, or new framework.
- Measured memory, CPU, disk traffic, and temporary-space behavior under concurrent jobs on owner-defined modest hardware.

**Questions for the owner and design engineer:**

1. Provide the slow case if possible: exact source or a privacy-safe representative file, source/container and codecs, selected VESPER output, URL/provider class, and the steps from completed transfer to final file. Include the current build, hardware, storage type, and observed wall time.
2. Which result is expected for that case: stream copy/remux, merge, metadata-only rewrite, subtitle embed, audio extraction, or true transcoding? What visible progress and cancellation behavior is expected?
3. Define the lowest supported reference Windows CPU/RAM/storage class and whether hardware acceleration may be used when available.
4. Set acceptable p50/p95 wall-time, peak memory, temporary-disk, and concurrent-job budgets separately for remux, merge, metadata/tagging, subtitle embedding, WAV decode, and genuine transcode. If targets depend on codecs or hardware, define those cases separately.
5. Define required output/player compatibility and quality for FLAC. Preserve the already approved output scope; VES-030 and the FLAC entry below track its remaining implementation and verification.
6. Decide whether the proposed design should trade extra reads for stronger validation or recovery guarantees. The design must quantify each option and may not silently remove the current safety guarantees.

**Required benchmark and acceptance protocol:**

1. Freeze an owner-provided reproducible slow input plus checked-in synthetic fixtures. Include 8 MiB, 40 MiB, and 250 MiB remux/merge cases where the codecs permit stream copy; include one compatible audio case, one compatible audio/video case, one metadata/tag case, one subtitle case, one WAV decode, and one incompatible-codec transcode. Record exact hashes, codecs, dimensions, duration, sample counts, and source provenance.
2. Run each operation on the owner-approved reference Windows machine with one warm-up and ten measured runs. Report every raw run, p50, p95, failures, CPU model and utilization, RAM, storage, .NET version, codec/device selection, output equality, allocations, peak process working set, bytes read/written, temporary-space peak, and time spent in probe, decode, encode, merge, validation, hash, tag, and publication phases.
3. Compare the proposed implementation and current main on the same machine, input, settings, and storage. Do not compare a remux result with a transcode result. The owner must approve absolute time/resource thresholds before the result is called a performance pass.
4. For stream-copy cases, prove no decoder or encoder ran and verify packet/sample equality, timestamps, track metadata, and playable output. For transcode cases, verify output codec, duration, synchronization, sample integrity, and agreed quality. For all cases, interrupt at each durable boundary and prove cancellation/restart leaves either the prior valid file or the complete verified output, with no foreign stage deleted.
5. Also run the established 8-hour resource-slope and concurrent-media cases from `USER-PERFORMANCE` and `USER-MEDIA-MEMORY`; retain raw artifacts and tie them to the exact source SHA and environment.

**Integration and acceptance:** The owner approves the architecture, scope, and per-operation budgets before a major replacement is started. Implement the design behind the existing user-facing workflows and persisted plan/recovery contracts unless the approved design states otherwise. Keep current formats and working code until replacements pass byte/sample, metadata, subtitle/sidecar, cancellation, recovery, and publication checks. Run focused tests for each changed path, then the consolidated Release and extension suites and the Windows/media acceptance matrix on the exact candidate. Update the requirement-to-evidence rows; do not close any related requirement from a design proposal alone.

**Dependencies and independent work:** The exact slow sample and reference hardware/performance budgets are missing. They block a reliable diagnosis and absolute performance certification, not routine bug fixes, currently specified format work, subtitles, HLS/DASH correctness, browser work, installer work, or other independent roadmap items. Continue those tasks. Do not remove or replace working media paths while this design remains open.

**Status:** OWNER DESIGN REQUIRED

## FLAC output from lossless sources

**System name and affected workstream:** FLAC output planning, native encoding, container writing, metadata, and publication.

**Related requirements and sources:** VES-030 (OPUS section H, lines 1185–1194; output research in H.2, lines 1290–1306), `MASTER-TODO.md`, `STATUS.tsv`, `CLAUDE-AUDIT-SOURCE-INDEX.md`, and `AUDIT-RECONCILIATION.md`. FLAC remains part of the approved audio output scope and is not complete.

**What VESPER must ultimately accomplish:** Provide an honest FLAC output option when VESPER can encode and publish a valid FLAC file on the current Windows system. Preserve exact decoded samples for supported lossless inputs, metadata and artwork that can be carried safely, cancellation, restart, stage ownership and atomic publication. Follow OPUS H.2's recommendation to offer encoding from lossless sources only unless the owner explicitly approves a clearly labeled lossy-to-FLAC path. If a supported encoder/output path is unavailable, keep the original bytes and filename and explain the fallback; never label another container as `.flac`.

**What exists now:** `MediaOutput` has MP3, WAV, Opus, M4A and MP4 choices, but no FLAC value or FLAC conversion path. `CodecCapabilities.Flac` checks decoding only. `FlacMetadataWriter.cs` can update metadata on an existing FLAC stream, but it does not encode PCM or create a FLAC frame stream. `MediaConversion.cs` and `WindowsMediaConverter.cs` have no FLAC output stage. A local read-only Media Foundation query on 2026-11-19 found one audio encoder MFT for subtype `0xF1AC` (`release/audit-evidence/flac-encoder-probe-20261119.log`). That proves only that this host registered an encoder MFT; it does not prove an end-to-end `.flac` sink or support on every target machine. Microsoft documents the [FLAC audio subtype](https://learn.microsoft.com/en-us/windows/win32/medfound/audio-subtype-guids), a separate WinRT [`MediaEncodingProfile.CreateFlac`](https://learn.microsoft.com/en-us/uwp/api/windows.media.mediaproperties.mediaencodingprofile.createflac) API, and FLAC decode/encode support for desktop Windows apps in its [supported-codec matrix](https://learn.microsoft.com/windows/apps/develop/media-authoring-processing/supported-codecs). The Win32 [Media Foundation file-container table](https://learn.microsoft.com/en-us/windows/win32/medfound/supported-media-formats-in-media-foundation) lists MP3, MP4, ASF and 3GP sinks but no FLAC file sink. VESPER's current project targets unpackaged `net10.0-windows` WinForms and has no WinRT projection or FLAC-writing dependency.

**Concrete gap and uncertainty:** The registered FLAC encoder MFT must be connected to a FLAC container writer that emits valid stream metadata and frames. Current source does not establish whether the native MFT can be used through `IMFSinkWriter` for a `.flac` output, whether WinRT transcoding is suitable in this unpackaged desktop app, or whether a VESPER-owned container writer or small codec dependency is required. The available path can also vary by Windows build and installed codecs. The output must not be offered based on the encoder MFT alone if the container sink, sample formats, metadata, or recovery path are missing.

**Why owner design is required:** There are materially different implementation paths: use the Windows WinRT transcoder/profile API, connect the discovered native MFT to a VESPER FLAC container writer, or approve a maintained codec library. These choices affect the Windows SDK/runtime surface, added dependencies and signing/distribution, supported sample formats, output integrity, and how the existing stage/recovery pipeline owns the result. The audit's H.2 row recommends a fixed-predictor encoder only for lossless sources, but implementing a new encoder/container path without an approved platform and quality contract would be a substantial format-engine decision.

**Quality requirements:** Never describe FLAC output from a lossy source as restoring quality. Validate STREAMINFO, frame headers/CRCs, total samples, sample rate, channels, bit depth and the FLAC stream MD5 before publication. Preserve exact decoded PCM for supported lossless sources. Support metadata and artwork within bounded sizes; retain the original or a safe sidecar if an embedded block is unsupported. Stream samples and frames without buffering the full input in memory. Bound output stages and validate free space, cancellation, retries, disk-full behavior and restart ownership. Verify playback in Windows and an independent player; a filename or encoder MFT count alone is not evidence.

**Constraints and existing decisions:** Keep C#/.NET 10, WinForms, Chrome MV3, privacy-first operation and the current `.vpart`, `.vmeta` and conversion-stage layout. Do not add FFmpeg, yt-dlp, Python, a service, account, telemetry, or a heavy runtime. Do not add a package or Windows App SDK dependency without an owner-approved design. Gate the option on the complete encoder/container capability, not only `CodecCapabilities.Flac` decoder support. Preserve direct FLAC downloads and `FlacMetadataWriter` behavior.

**Questions for the owner and design engineer:**

1. Which output path is acceptable for this unpackaged desktop app: the built-in WinRT FLAC transcoder API, the registered Media Foundation encoder plus a VESPER-owned FLAC writer, or an approved small codec library? What Windows SDK projection/build dependency may be added, if any?
2. Should conversion be limited to lossless sources as H.2 recommends? Which source containers and codecs count as lossless for this choice?
3. Which channel counts, sample rates, sample depths, and input sizes must be supported? Is bit-exact decoded PCM the acceptance target for every supported case?
4. Which tags and artwork must be embedded, and what is the fallback when a tag block is invalid or too large?
5. If one supported output path is unavailable on a Windows build, should the FLAC choice be hidden while the original remains available, or may VESPER offer a limited compatible subset with a clear reason?

**Integration and acceptance:** The accepted design must identify the complete per-machine encoder and file-sink capability and its source/container matrix. Add a direct FLAC copy case that proves bytes remain unchanged, then encode approved lossless WAV/FLAC inputs at each supported channel/rate/depth boundary and compare decoded output samples exactly. Verify STREAMINFO, frame CRCs, declared sample totals, MD5, tags, artwork, independent player playback and output MIME/type labels. Inject cancellation, crash, disk-full, stage collision and restart at each durable conversion/publication boundary; prove that only the job-owned stage is resumed or removed and that the original remains intact on failure. Measure CPU, memory, disk and processing time on modest supported hardware before making performance claims. Gate both native and browser pickers on the same verified capability result, run focused engine and Windows UI tests, then the consolidated Release and exact-candidate Windows/media matrix.

**Dependencies and independent work:** This design blocks FLAC output only. It does not block the implemented MP3 path, direct FLAC downloads/tagging, WAV/Opus/M4A/MP4 output, subtitles, browser handoff, installer work or other approved features. Keep VES-030 open until this path and the remaining output/player matrix are implemented and verified.

**Status:** OWNER DESIGN REQUIRED

## Indexed byte-range clipping for single-file media

**Affected workstream:** Media discovery, HTTP range planning, MP4/WebM indexing, clip boundaries, muxing, durable resume, and output validation.

**Related requirements:** VES-031 (OPUS section H, lines 1195–1216: indexed YouTube single-file formats and byte-range clipping), VES-030 (selected output support), and USER-PERFORMANCE. See `CLAUDE-AUDIT-SOURCE-INDEX.md`, `MASTER-TODO.md`, `STATUS.tsv`, and the VES-031 rows in `IMPLEMENTATION-2026-11.md`. This remains approved product scope; the design deferral does not close it.

**What VESPER must ultimately accomplish:** Let the user request a time range from an indexed single-file MP4 or WebM representation and fetch only the data needed to produce that range when the representation and output path support it. Keep audio and video synchronized, preserve required decode dependencies and timestamps, produce a playable result, and be honest about whether a requested boundary is exact or aligned to a keyframe. Do not silently download the whole source and call it a byte-range clip. If a safe partial path is unavailable, explain the actual behavior before queueing and preserve the existing explicitly supported alternatives.

**What exists now:**

- `MediaDiscoveryForm` shows clip controls only when `MediaTransferBridge.SupportsSegmentClip` accepts the prepared choice. That helper allows WAV, completed HLS, DASH plans with bounded durations, and two-track HLS/DASH merge plans. It does not enable single-file MP4 or WebM clips.
- Completed segmented HLS/DASH clips use a persisted `MediaTimeRange`; segment selection can start at a nearby segment boundary and the picker says so. Direct HTTP WAV clips now say that they keep the selected sample range but download and decode the full source; the focused Windows notice tests passed 3/3 (`release/audit-evidence/ves031-clip-notice-focused-20261119.log`). This is whole-segment selection or full-source decode, not single-file index fetching.
- `WaveAudio` can write sample-exact PCM clips, but the current direct-source WAV path downloads and decodes the complete source before writing the selected samples.
- The current native MP4 demuxer/muxer reads supported sample tables for complete files. The WebM demuxer reads Cues for container inspection. These paths are not wired to a clip planner that fetches an index and selected media ranges from a remote single-file representation. MP4 `sidx` range planning and partial MP4/WebM publication are not implemented.
- Existing HTTP transfer code already validates range responses, resource identity, redirects, validators, partial-file ownership, cancellation, recovery, and publication. Any new planner must use those contracts rather than creating a second weaker HTTP path.
- `MediaConversionTests.cs`, `DashTransferTests.cs`, `HlsTests.cs`, `MediaMergeTests.cs`, and `DemuxMalformedInputTests.cs` cover current WAV/conversion, segmented clipping, complete-file mux/demux, and malformed inputs. They do not verify remote MP4 `sidx` or WebM Cue range fetching, indexed clip boundaries, or recovery of a partial single-file clip.

**Concrete gap and uncertainty:** The audit says an indexed YouTube single-file representation “could be clipped by byte range,” but does not define the promised output boundary. Container indexes identify byte/time regions; they do not by themselves guarantee a decodable clip beginning at the exact requested timestamp. MP4 edit lists and decode timestamps, WebM Cue positions and cluster structure, audio priming, codec dependencies, variable GOPs, separate audio/video resources, nested index references, and unaligned end times can require trimming, remuxing, or decoding beyond the selected presentation interval. VESPER has no bundled general-purpose video decoder. The current UI supports segment-aligned HLS/DASH ranges and sample-exact decoded WAV output; those are separate behaviors with different cost and accuracy.

**Why owner design is required:** A reliable implementation needs a product decision between keyframe-aligned fast clips, exact timestamp clips that may require decoding/re-encoding, and refusal or full-download fallback when the source cannot produce the requested boundary. That decision changes compatibility, CPU and bandwidth use, output formats, progress language, and whether “download part” means only selected source bytes. Designing the index reader and recovery format around an unstated boundary would risk an expensive feature that still fails the user’s intended behavior.

**Requirements the design must preserve:**

- Preserve the current C#/.NET 10, WinForms, Chrome MV3, lightweight dependency, and temporary-file layout decisions. Do not add FFmpeg, yt-dlp, Python, a service, or a general media framework.
- Bind every requested range to the exact source identity, representation, validators, redirects, and index bytes. Reject malformed or contradictory index data; bound index size, nested references, request count, response size, and parser work.
- Maintain A/V synchronization, decode dependencies, sample timestamps, metadata that remains valid, sidecar/subtitle behavior, and playable publication. Do not publish a truncated or unverified range.
- Preserve cancellation, restart, retry, disk-full behavior, atomic publication, stage ownership, and existing full-source and segmented download behavior.
- Distinguish exact clipping, keyframe/segment alignment, and full-source download in the picker before the user queues work. Do not imply a performance win unless measured.

**Questions for the owner and design engineer:**

1. For video, is a clip that begins at the preceding keyframe and includes a few extra seconds acceptable, or must the result begin and end at the exact requested presentation timestamps? If exactness is required, which output codecs and quality changes are acceptable when decoding is necessary?
2. Must the first implementation support both MP4 `sidx` and WebM Cues, and only YouTube, or every provider that exposes a compatible indexed HTTP representation?
3. Are separate audio/video resources in scope, and what synchronization tolerance and track-selection behavior are required?
4. Which output containers and codec combinations must be preserved? What should VESPER do when an index is absent, invalid, nested beyond the bound, unsupported, or inconsistent with range responses: refuse, offer full download, or offer a keyframe-aligned result?
5. Set bounded index bytes, maximum index depth/request count, maximum selected range count, minimum/maximum clip duration, and bandwidth/temporary-disk expectations. Define whether redirects and refreshed signed URLs can continue a saved clip and what proof is required.
6. Define how the UI states the exactness, expected extra media, fallback cost, and whether all source bytes will be downloaded before the user confirms.

**Integration and acceptance:** The accepted design must specify the user-visible boundary contract, source formats/providers, selected output matrix, fallback rules, and persisted identity/recovery data before implementation. Tests must cover valid and malformed MP4 `sidx` and WebM Cues (including nested indexes and boundary arithmetic), exact 206/Content-Range and redirect identity, validators changing mid-range, keyframe and edit-list boundaries, A/V sync, unaligned start/end, missing/invalid indexes, and hostile oversized input. A real playable corpus must verify duration, timestamps, first/last decoded samples or frames, track metadata, and whether any decoder/encoder ran. Interrupt and restart at every durable boundary; prove the same requested clip resumes without refetching committed ranges or deleting foreign files. Measure bytes transferred and processing phases against a full download on the same source. The picker must describe the selected behavior before queueing. Run the consolidated Release and extension suites plus the Windows/media acceptance matrix on the exact candidate before closing VES-031.

**Dependencies and independent work:** The open boundary and fallback decisions block the new indexed single-file clip planner and its acceptance claims. They do not block completed HLS/DASH segment clipping, sample-exact WAV writing after full-source decode, ordinary full-file downloads, subtitles, conversion, or other roadmap work. Preserve those existing features while this design is pending.

**Status:** OWNER DESIGN REQUIRED

## First Flight live extension connection and completion flow

**Affected workstream:** First Flight setup, per-user native-host registration, Chrome extension activation, native messaging, desktop startup, and distribution guidance.

**Related requirements:** VES-050 (OPUS line 1661, installer final message), VES-004 (signed shipping and extension distribution), and VSP-003 (transactional install paths). See `CLAUDE-AUDIT-SOURCE-INDEX.md`, `MASTER-TODO.md`, `STATUS.tsv`, `AUDIT-RECONCILIATION.md`, and `QUALITY-RELEASE-GATES.md`. The setup-copy correction is partial progress; it does not satisfy connection detection or distribution requirements.

**What VESPER must ultimately accomplish:** After install or repair, accurately tell the user which parts are installed and registered, guide them through the owner-approved extension distribution path, and report a successful browser connection only after the actual extension has reached the native host and the desktop app has acknowledged it. Missing, disabled, blocked, or unreachable extension states must show a useful next step without claiming that setup enabled Chrome or that a registry value alone proves connectivity.

**What exists now:**

- `src/Vesper.Setup/InstallRegistration.cs` and `InstallTransaction.cs` write and journal the per-user native-host registration and installed files. `src/Vesper.Setup/Program.cs` gives `SetupWindow` a synchronous work callback that returns no connection result.
- `SetupWindow.cs` now says native-host files and browser registration were installed but setup did not verify that the extension can connect. Its extension-files tooltip no longer promises Developer mode or extension enablement. `tests/Vesper.Setup.Tests/FirstFlightUiTests.cs` adds a focused assertion for that truthful copy; the focused Setup Release filter passed 10/10 (`release/audit-evidence/ves050-firstflight-focused-20261119.log`). Full exact-candidate Release verification remains pending.
- `tests/Vesper.Setup.Tests/InstallTransactionJournalTests.cs` covers install transaction recovery, not browser connectivity. There is no Setup test that receives or verifies a real extension-to-app hello.
- `extension/background.js` sends a `hello` request through Chrome Native Messaging. `src/Vesper.NativeHost/Program.cs` forwards the request to the per-user `BridgePipe`, can start VESPER when needed, and returns the desktop response. `BridgeProtocol` and `BrowserBridgeHandler` already process `hello` for an extension request.
- No status path carries that hello result back to the separate Setup process. Setup cannot distinguish registered files from an extension that is actually enabled and has completed a desktop handshake.
- Current `STATUS.tsv` also records that a Chrome Web Store completion button depends on the actual Store listing. The extension is currently included as files for manual loading; no approved Store listing URL or listing identity is available here.

**Concrete gap and uncertainty:** The existing extension-to-host-to-app request path is real, but First Flight does not observe it. To show a live success result, a handshake result must reach Setup, or the setup experience must move its success state into the running app. Those options differ in process ownership, launch timing, timeout, cancellation, browser-profile selection, and behavior when Setup is closed. The official extension install route also depends on an owner-approved Store listing. A local registration check or an app process check would be a false positive for the requirement.

**Why owner design is required:** The current requirement names the desired observation but not the confirmation flow, which process owns it, whether Setup may start the app/browser, how long to wait, how to handle multiple Chrome profiles or an already-running app, or how manual and Store distributions differ. Adding a new status pipe or changing First Flight into a multi-process browser wizard without those decisions could introduce a fragile install contract. The external Store URL and approved distribution identity are also unavailable.

**Requirements the design must preserve:**

- Keep the .NET 10 WinForms installer, per-user setup, transactional journal/rollback, portable mode, and current registration ownership. No admin-only service or permanent polling process.
- Treat the native-host manifest, extension ID, browser profile, pipe name, and returned status as untrusted input. Do not report success on registration alone or leak URLs, cookies, download names, or account data into setup status/logs.
- Bound handshake time and retries; preserve setup cancellation, repair, rollback, uninstall, and running-app behavior. A failed connection must not undo a valid install or change the user's default browser settings.
- Keep the official extension route accurate. Do not imply Chrome enables extensions, disable security controls, bypass managed-browser policy, or promise an unavailable developer mode.
- Use the approved Store URL/listing identity only after the owner supplies and approves it; otherwise give neutral, accurate steps that match the supported package.

**Questions for the owner and design engineer:**

1. Which extension distribution is approved: Chrome Web Store, manual unpacked installation, or both? Provide the final listing URL and extension ID if Store installation is approved.
2. What exact event counts as connected: host registered, Chrome launching the host, a successful `hello` response from the desktop app, or an additional version/identity handshake?
3. Should First Flight launch VESPER and wait for the user to activate the extension, should VESPER show connection status after opening, or should Setup wait and receive the result? What timeout and retry/cancel behavior should the user see?
4. Which Chrome profiles and managed-browser policies must be detected, and what action should be offered if the extension is disabled, missing, blocked, or connected to a different install?
5. What app/host/extension version compatibility result should be shown after an upgrade or rollback?

**Integration and acceptance:** The accepted design must define the approved distribution path and one authoritative connection-success event, its process and message contract, timeout/retry behavior, and version checks before a new cross-process protocol is added. Verify in disposable Windows accounts and Chrome profiles: first install, repair, already-running app, extension absent/disabled, native host missing or invalid, app startup failure, multiple profiles, managed policy, timeout/cancel, upgrade/rollback, and uninstall. Assert success only after a real extension-originated hello is acknowledged by the intended desktop instance. Confirm setup copy and button destination against the approved Store listing, preserve per-user registration, and prove no credentials or page/download data enter setup status. Run the focused Setup suite and full exact-candidate Windows/Chrome matrix before closing VES-050.

**Dependencies and independent work:** The approved Store listing URL/identity is an external owner prerequisite for a Store button. The process/handshake decisions block only live-connection status and the final completion flow; native-host registration, extension behavior, transactional install fixes, portable builds, and other roadmap work continue independently.

**Status:** OWNER DESIGN REQUIRED

## Extension native-request lifecycle and submission progress

**System name and affected workstream:** Extension native-request lifecycle and submission progress; Chrome MV3 service worker, native messaging host, desktop bridge, and browser-facing feedback.

**Related requirements and sources:** VES-025 (OPUS section G, lines 1160–1171), VES-062 (OPUS section O, line 1748, for the separate legacy `describe` path), and `USER-CHROME-REAL` worker-restart, duplicate-submission, and native-response acceptance. See `STATUS.tsv`, `MASTER-TODO.md`, and `CLAUDE-AUDIT-SOURCE-INDEX.md`. These requirements remain open.

**What VESPER must ultimately accomplish:** Ordinary browser-to-native requests must return or fail within a short, clear bound. A download submission that needs desktop confirmation must acknowledge receipt quickly, then report whether the user accepted, declined, or could not complete it through an explicit progress/status flow. A timeout or worker restart must never look like a successful queue operation, lose the user's recoverable request, or create a duplicate job. User-facing format descriptions must ultimately use the same capability meanings as the native picker; that cleanup remains tracked separately under VES-062.

**What exists now:** `extension/background.js` implements `nativeRequest` with `chrome.runtime.sendNativeMessage` and a default timeout of 330,000 ms. `submit` saves a bounded, secret-stripped request in session storage, waits for the one native reply, and keeps the request available for manual retry when the reply fails. The Transfers page lists those unconfirmed requests and offers “Send again”; it does not show in-progress status. The native host routes each message through `BridgePipe` and `BridgeProtocol`; the current extension message surface has no correlated submission-status command. Several non-submit callers override the 330-second default, but others inherit it. The legacy `describe()` function remains in the extension and generates “Best”, “Balanced”, “Smaller”, and raw-kbps labels; VES-062 records that this path must not be removed until its shared capability replacement is available.

**Concrete gap and uncertainty:** One-shot `sendNativeMessage` returns only a single final reply, while a submission can wait on desktop UI. Returning early without a durable correlation/status path would tell Chrome that a job was accepted before VESPER knows whether the user accepted it. Leaving the one-shot request open can hold extension UI for 5.5 minutes and gives no progress. The project does not define whether status should be polled while a relevant page is open or streamed over a native port, what state survives desktop/service-worker restart, or when an unanswered request expires.

**Why owner design is required:** The audit identifies the desired direction (short ordinary timeouts, early submit acknowledgement, then progress/status) but does not specify the acknowledgement contract, status owner, retention, cancellation, or restart semantics. Choosing those details affects Chrome ownership, duplicate prevention, durable job creation, extension lifecycle, and whether the UI may truthfully say “accepted”. This is a cross-process protocol and recovery-state change, not a local timeout constant.

**Quality requirements:** Never show “added” or “accepted” until the defined acceptance point is durable and attributable to the same request ID. Preserve idempotency across retries and worker restarts. Keep user credentials and signed request data in session memory only; do not persist them for progress tracking. Bound ordinary request waits, status lifetime, storage, and polling or port lifetime. Keep idle Chrome and VESPER free of timers or open connections. Provide clear waiting, accepted, declined, timed-out, and retry states without blocking the browser UI.

**Constraints and existing decisions:** Keep Chrome MV3, the current extension and C#/.NET 10 native host, the current per-user desktop bridge, Chrome ownership rules, and the lightweight privacy-first design. Preserve explicit user confirmation, native request identity, and the existing recoverable retry behavior. Do not add a background service or leave a persistent port open while idle. Keep VES-062's legacy `describe` replacement separate from this request-lifecycle decision.

**Questions for the owner and design engineer:**

1. Does “accepted” mean the desktop received the request, the user confirmed it, or the durable job row was committed? What exact wording should appear at each stage?
2. Should final state use short-lived polling while the Transfers page is open, a request-scoped native port, or another event-driven mechanism? Which process is authoritative for that state?
3. Which submission state must survive a Chrome worker restart, a VESPER restart, or both, and for how long? When may stale requests be discarded?
4. How should user cancellation, duplicate clicks, unavailable native host, lost final reply, and a second extension profile behave without creating duplicates or false success?
5. Which current commands may wait for an interactive desktop picker, and what bounded timeout/error should each show? The audit says long waits belong to submissions with progress, but the picker request's user-visible completion contract is not defined.

**Integration and acceptance:** In disposable Chrome profiles, verify ordinary `hello`, probe, options, picker, and submission requests return the specified result within their bounds. Keep a desktop confirmation open past the ordinary timeout and show responsive, truthful browser state; then accept and decline it and verify the correlated durable job/status result. Repeat with a lost reply, duplicate submission, two browser profiles, MV3 worker restart before and after acceptance, VESPER restart, native-host absence, user cancellation, and stale-status expiry. Prove no duplicate job, no false acceptance, no credential or signed URL persisted for polling, and no idle connection/timer. Run the extension suite and registered-host Windows Chrome scenarios on the exact candidate. VES-062 label parity requires its own native-picker/legacy-path tests before removing `describe()`.

**Dependencies and independent work:** This design blocks closing the VES-025 long-timeout/progress requirement and the related runtime portion of `USER-CHROME-REAL`; it does not block normal download, media-picker, manual handoff, or unrelated UI and installer work. VES-062's legacy label replacement remains a separately tracked dependency for deleting `describe()`.

**Status:** OWNER DESIGN REQUIRED

## Recovery manifest identity and legacy V1 handling

**System name and affected workstream:** Recovery manifest identity and legacy V1 handling; temporary-file ownership, crash recovery, conversion/merge cleanup, and upgrade compatibility.

**Related requirements and sources:** `USER-ATOMIC-STATE`, VES-017 (OPUS section F, lines 1005–1022), and AUDIT-EXTRA-004 (Audit B section D, lines 288–406). See `STATUS.tsv`, `MASTER-TODO.md`, `CLAUDE-AUDIT-SOURCE-INDEX.md`, `AUDIT-RECONCILIATION.md`, and the relevant implementation rows in `IMPLEMENTATION-2026-11.md`. These requirements remain open.

**What VESPER must ultimately accomplish:** Recovery and cleanup may use a `.vmeta` sidecar only when it is the exact sidecar VESPER created for that job. Persist a stable identity for the manifest file itself, verify it across restart and before any replace/delete, and preserve the current `.vpart`/`.vmeta` filenames and recoverable progress. Never delete a user replacement just because its body is valid or byte-for-byte copied from an older VESPER manifest.

**What exists now:** `RecoveryManifest.cs` keeps the existing body version/path layout and now writes an additive `ManifestIdentity` field from the `.vmeta.tmp` file handle. Recovery, prior-manifest checks, and cleanup compare it to the opened sidecar handle. `RecoveryManifestInputTests`, `TemporaryFileIdentityDeletionTests`, `MediaConversionStageOwnershipTests`, and `MergeStageOwnershipTests` passed **52/52** in Release (`release/audit-evidence/atomic-sidecar-identity-focused-20261119.log`), including refusal/preservation of a byte-for-byte copied new manifest at a different file ID. Bodies created before this field still have no sidecar identity and follow the legacy path. The tests do not prove ownership of a valid legacy V1 `.vmeta` already replaced before restart/open.

**Concrete gap and uncertainty:** New manifests bind their body to the sidecar file ID. A replacement `.vmeta` containing a copied valid body created before this field was added can still match the job, path, checksum, and partial identity when VESPER first opens it after restart. The old body cannot distinguish the original sidecar from that copy. The additive new-write field closes the gap only for new manifests; it does not define whether a valid legacy V1 file may be adopted, rewritten, used read-only, or cleaned.

**Quality requirements:** Recovery must preserve committed bytes and refuse destructive action when file ownership is uncertain. The body checksum remains unkeyed and must not be described as protection from a same-user process able to rewrite the body and recompute its checksum. Keep parsing bounded and avoid rereading or copying the media file to establish sidecar identity. Preserve usable recovery for valid existing downloads where the chosen policy can do so, with a clear action when recovery is refused. Identity lookup and manifest verification must not add unbounded work to checkpoint or startup paths.

**Why owner design is required:** The new-manifest identity field and verification are implemented. The remaining decision changes whether interrupted downloads with existing identity-less V1 sidecars remain resumable and whether VESPER may replace or clean those sidecars. Silently accepting V1 for destructive cleanup weakens the approved ownership rule; silently refusing V1 can strand existing progress. The owner must choose the compatibility behavior for identity evidence that cannot be reconstructed after the fact.

**Constraints and existing decisions:** Keep the existing temporary-file layout and Windows C#/.NET 10 implementation. The additive field is part of the existing V1 body; it does not change the serialized body version. Never let a V1 migration or a future format change delete/truncate a foreign or user-replaced file. Keep the current bounded parsing, job/path binding, partial-file identity checks, durable flush/commit order, cancellation, retry, conversion, merge, and recovery behavior. No new service or heavy dependency.

**Questions for the owner and design engineer:**

1. For a valid legacy V1 manifest whose job, path, checksum, and partial-file identity match, may VESPER adopt and rewrite it with `ManifestIdentity` after opening and validating it, or must it preserve the V1 sidecar and leave the job in an actionable attention state?
2. If V1 is kept read-only, may VESPER use its data to resume while refusing to replace/delete the manifest file, or must it refuse recovery entirely?
3. May a user explicitly choose to restart from scratch when a valid V1 manifest cannot prove its own identity, and how should VESPER preserve its partial and unrelated files during that choice?
4. Which Windows file identity is authoritative across rename and restart, and what should happen on filesystems that cannot supply it?

**Integration and acceptance:** The additive identity field is implemented for new manifests. In a disposable directory, the focused tests prove an untouched manifest recovers/cleans and a same-body copy with a different file ID is refused and preserved. Full acceptance still requires interruption while creating/flushing/renaming `.vmeta.tmp`, after job-store commit, during restart, and during cleanup. Add V1 fixtures for the owner-approved adoption/read-only/refusal behavior; prove saved partial bytes remain recoverable or safely actionable without deleting a copied or foreign sidecar. Run focused RecoveryManifest, sidecar identity, conversion ownership, and merge ownership tests, then the full Release suite on the exact candidate.

**Dependencies and independent work:** The V1 adoption/cleanup policy blocks a complete ownership claim for already-existing V1 sidecars. New manifests now carry a sidecar identity without changing the temp-file name; validate this implementation on the exact candidate. Downloads, installer work, browser integration, queue workflows, and other independent tasks continue.

**Status:** OWNER DESIGN REQUIRED

## Completed-file Recycle Bin handoff and file identity

**System name and affected workstream:** Completed-file Recycle Bin handoff and file identity; completed-file deletion, Windows Shell integration, and saved job state.

**Related requirements and sources:** `USER-ATOMIC-STATE` (final Recycle Bin path race), VSP-004 (Rescue and durable atomicity, related identity precedent), and the completed-file rows in `STATUS.tsv`, `MASTER-TODO.md`, `AUDIT-RECONCILIATION.md`, and `QUALITY-RELEASE-GATES.md`. The requirement to preserve the Recycle Bin/undo behavior remains in force.

**What VESPER must ultimately accomplish:** When the user confirms deletion of a VESPER-completed file, move that exact verified file object to the Recycle Bin and then update the job to File missing. A path replacement between verification and shell execution must never cause VESPER to recycle a different file. If ownership cannot be confirmed, keep the job and file and show an actionable error.

**What exists now:** `DownloadEngine.Commands.ChangeCompletedFileAsync` opens the saved file, checks its length and SHA-256, then calls `PathStillNamesFile` before invoking the supplied remove callback. `EngineHost.RecycleFile` calls `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, ..., RecycleOption.SendToRecycleBin)`, which accepts a path. `DownloadActions.DeleteFile` supplies that path after the engine's identity checks. Existing `EngineTests` cover a same-size replacement before the post-hash check and prove it is not deleted; there is no regression for replacement in the interval between that final check and the shell call.

**Concrete gap and technical evidence:** The final operation is path-based after the identity-checking handle is opened with delete sharing so the Shell can move the file. That leaves a time-of-check/time-of-use interval in which the path could identify another object. Microsoft documents [`IFileOperation::DeleteItem`](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-deleteitem) as accepting an `IShellItem` and performing the operation later through the shell operation. [`IFileSystemBindData2`](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifilesystembinddata2) can supply a file ID during shell parsing, but the documentation does not state that the file ID pins the object through the later delete. [`SetFileInformationByHandle`](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle) supports handle-based rename/disposition, but disposition marks the file for deletion on close and does not provide Recycle Bin undo semantics.

**Quality requirements:** The operation must never recycle a replacement or report success before the intended item is in the Recycle Bin. Preserve restore/undo behavior and leave both the file and job state intact on uncertainty or failure. The normal path should not add unbounded scans, retries, or memory use; any identity checks and shell work should remain responsive and report a useful result. Do not expose unrelated file details in logs or UI errors.

**Why owner design is required:** The current contract requires both exact-object safety and an undoable Recycle Bin move. The available call paths are handle-bound permanent deletion or shell operations resolved through an item/path; the cited Windows documentation does not establish an object-bound Recycle Bin operation for this architecture. Adding a broker/shell layer, accepting a residual race, or changing to permanent deletion affects data safety and the approved user experience.

**Constraints and existing decisions:** Never delete or recycle a Chrome-owned, unrelated, completed replacement, or user-created file. Keep explicit confirmation, the Recycle Bin restore path, job/file consistency, and per-user Windows behavior. Preserve the current C#/.NET 10 native app, avoid a service or administrator requirement, and do not change the action to permanent deletion without owner approval.

**Questions for the owner and design engineer:**

1. Is an additional per-user shell helper/broker acceptable if it can prove the opened file identity through the Recycle Bin operation? If so, what signing, installation, repair, and uninstall constraints apply?
2. If no identity-bound Recycle Bin operation can be demonstrated, may the product retain the current path-based Recycle Bin behavior with the exact residual race stated, or must Delete be unavailable for that file?
3. Would the owner ever approve a separate handle-bound permanent-delete action, and if so, what distinct confirmation and UI language would it require? No such approval is assumed.
4. What should happen when the file is locked, the Recycle Bin is unavailable, or the file identity cannot be read: keep the row and file, offer a retry, or direct the user to File Explorer?

**Integration and acceptance:** The approved solution must pass a deterministic disposable-file race test that replaces the target after VESPER's last identity check but before Shell handoff. The replacement must remain untouched, the job must not report File missing, and the originally confirmed item must either be moved to the Recycle Bin or remain intact with a clear failure. A normal operation must allow the user to restore the exact file from the Recycle Bin. Test hard links/reparse points, locked files, shell errors, retry, and restart/job-state consistency in a disposable Windows account. Run the completed-file and Windows integration tests on the exact candidate.

**Dependencies and independent work:** This decision blocks a proof of the completed-file Recycle Bin race requirement. It does not block safe Open/Locate behavior, normal downloads, unfinished-job Cancel, or independent product and packaging work. Preserve the current Recycle Bin behavior until the owner approves an alternative.

**Status:** OWNER DESIGN REQUIRED

## Windows Share target and playlist Open With

**Affected workstream:** Windows shell integration, startup activation, First Flight registration, and local HLS/DASH manifest handling.

**Related requirement:** N-20. The original source index maps this to OPUS line 1736: “Windows Share target / ‘Open with VESPER’ for .m3u8/.mpd files.” See `CLAUDE-AUDIT-SOURCE-INDEX.md`, `MASTER-TODO.md`, and `STATUS.tsv`. The requirement remains open; this entry does not remove either named integration.

**What VESPER must ultimately accomplish:** Provide the owner-approved Windows workflow for sending a supported `.m3u8` or `.mpd` file to VESPER and letting the user review and queue its media. The original requirement names both the Windows Share target and the file-association “Open with” route, but does not say whether both are required or how each receives the manifest. Preserve the existing portable and First Flight workflows and require explicit user choice before changing shell registrations or starting downloads.

**What exists now:** `Program.Main` handles only `--ui-preview` and `--background`; it does not dispatch file arguments. Native-host registration is unrelated to shell activation. `UrlListParser` handles pasted URL text, and N-14 list import handles VESPER JSON/CSV transfer lists. The `.url` path in `AddDownloadForm` reads a bounded Internet Shortcut as text and opens normal URL review; it does not parse a local HLS/DASH manifest. No Share Target activation, package identity manifest, or `.m3u8`/`.mpd` association is present in the current app/setup source.

**Concrete ambiguity and technical dependency:** A Windows Share Target and a file association are distinct entry points. A Share Target receives Windows share activation; “Open with” starts the program with a file path. The original audit does not say whether both entry points are mandatory, what Share is expected to share, or whether the local manifest should be queued as a file or parsed into remote media URLs. Current Microsoft guidance says an unpackaged Win32 app needs package identity to register as a Share Target, for example through MSIX or a sparse package with an external location. VESPER currently uses a native WinForms app and First Flight installer, so this choice affects package identity, setup ownership, and removal. See [Microsoft’s Share Target guidance for unpackaged Win32 apps](https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/integrate-sharesheet-receive).

**Why owner design is required:** Choosing a registry association alone could silently satisfy only half of the slash-separated requirement. Adding package identity or a sparse MSIX integration changes installer, update, signing, and uninstall behavior. The local-manifest input semantics also affect security, relative URL resolution, credentials, and the existing HTTP/HLS/DASH engine. The current requirement is not specific enough to select one design without reducing or materially changing the approved shell workflow.

**Requirements the design must preserve:**

- Keep the native C#/.NET 10 WinForms app and the existing portable and First Flight modes. Do not add an always-running service, require administrator access, or silently claim the Windows default for `.m3u8` or `.mpd`.
- Register only owner-approved file types and only after clear consent. Use per-user registration where supported, record exactly what VESPER owns, and restore prior values safely on repair, upgrade, and uninstall.
- Treat incoming paths and manifest contents as untrusted. Bound file size, URL count, parser time, nesting, and redirects; reject unsupported schemes, traversal, credential-bearing manifests, and DRM. Show the parsed result for review before queueing.
- Preserve existing direct, remote HLS and DASH behavior and the ordinary URL paste workflow.

**Questions for the owner and design engineer:**

1. Are both Windows Share and “Open with” required, or is one the approved workflow? Which Windows versions and install modes must expose each?
2. What does Share send to VESPER: a local `.m3u8`/`.mpd` file, a URL to a manifest, or both? Which file types and data formats should the Share Target advertise?
3. For a local manifest, should VESPER resolve relative segment URLs against the file location, require absolute HTTP(S) URLs, or reject local references? What file size, URL count, and nested-manifest limits should apply?
4. Should shell integration be opt-in per user, and what should First Flight, repair, portable mode, another app’s existing association, and uninstall do?
5. When another VESPER instance is running, how should paths and share activation be forwarded without losing quoting, authorization boundaries, or the user’s confirmation step?

**Integration and acceptance:** The owner approves whether both entry points are required, the packaging/registration model, supported manifest semantics, and uninstall ownership before implementation. Test in a disposable Windows account: consented registration and removal, repair/upgrade rollback, preserving another handler and default association, multiple-file activation, already-running activation, valid and malformed manifests, relative and external references, credential and unsupported-scheme refusal, bounded input, and normal review before queueing. Verify both portable and First Flight behavior on the exact candidate. Keep N-20 open until all owner-approved entry points pass those checks.

**Dependencies and independent work:** The unresolved items are the shell workflow choice, manifest semantics, and any package-identity dependency. Existing direct downloads, remote HLS/DASH, list import/export, `.url` review, and unrelated installer fixes can proceed without this design.

**Status:** OWNER DESIGN REQUIRED
