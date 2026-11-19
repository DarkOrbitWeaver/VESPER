# Owner design required

This document holds only systems that need a product or architecture decision from the owner. Their approved requirements remain open until the accepted design is implemented and verified.

## Media processing performance and architecture

**Affected workstream:** media conversion, remux and merge, output validation, memory and disk use, and performance measurement.

**Related requirements:** VES-006, VES-030, VES-031, VES-033, VES-035, VES-036, USER-MEDIA-MEMORY, USER-PERFORMANCE, VSP-008, AUDIT-EXTRA-007, AUDIT-EXTRA-012, and AUDIT-EXTRA-016. The original audit source index maps VES-006 to OPUS section E, lines 693–744; VES-030 through VES-036 to section H, lines 1185–1268; USER-PERFORMANCE/VSP-008 to section M and the Q.6 benchmark plan. See `CLAUDE-AUDIT-SOURCE-INDEX.md`, `MASTER-TODO.md`, `STATUS.tsv`, `AUDIT-RECONCILIATION.md`, and `VSP-INDEPENDENT-RECONCILIATION.md` for traceability. Historical implementation and media reports are evidence, not current behavior authority.

**What VESPER must accomplish:** Keep every approved media workflow and format truthful and usable. Use stream copy when the selected output can preserve the input codecs. Decode and re-encode only when the requested output genuinely requires it. Preserve sample and timeline correctness, metadata behavior, subtitles and sidecars, cancellation, crash recovery, durable publication, privacy, and the existing C#/.NET 10, WinForms, Chrome MV3, and lightweight-runtime decisions. Make processing fast and resource-efficient on owner-approved modest hardware without unsupported speed claims.

**What exists now:**

- `src/Vesper.Engine/Media/MediaConversion.cs` chooses among compatible stream-copy remux, injected conversion, Windows Media Foundation conversion, native Opus packet copy, and WAV writing. It stages, hashes, validates, tags, and publishes outputs. `WindowsMediaConverter.cs` owns the Media Foundation path.
- `MediaMerger` and the native muxers/demuxers under `src/Vesper.Engine/Media/Mux/` handle MP4, M4A, WebM, and Matroska remuxing. `ValidateRemux` rereads output and compares media structure and samples before publication.
- Known full passes that need phase timing: compatible remux writes the output, `ValidateRemux` rereads it, and final SHA-256 calculation reads it again. Tag embedding can add a second remux plus validation before hashing. These are established code paths, not proven unnecessary work; no current 40 MiB trace measures their cost or proves a pass can be safely combined. WAV clip output decodes the full source, then validates and hashes the prepared WAV. Continuous HLS TS avoids timeline copying; discontinuity rebasing rewrites a segment buffer. Keep these costs visible in the benchmark instead of attributing all post-download time to “conversion.”
- `HlsTransfer.cs`, `DashTransfer.cs`, `MergeTransfer.cs`, and `SegmentedMergeTransfer.cs` handle segmented fetch, merge, and recovery. Segment backing arrays have a weighted 256 MiB / three-buffer admission limit; this is not a process-wide memory cap. Whole-object AES decryption and several scratch buffers remain bounded per object rather than streamed.
- `WaveAudio.cs` writes sample-exact PCM clips and RF64 when needed. WAV clipping decodes the full source. MP3/FLAC transcoding, real greater-than-4-GiB WAV conversion, codec/device negotiation, and some player compatibility checks remain open in `STATUS.tsv`.
- `TextSubtitleParser.cs` and the current merge path add opt-in SRT/WebVTT embedding where supported while preserving selected sidecars. Current implementation and test status must be rechecked against source when an owner design returns.
- `tests/Vesper.Tests/MediaConversionTests.cs`, `MediaMergeTests.cs`, `MediaMergerPayloadValidationTests.cs`, and related tests cover synthetic fixtures, correctness, recovery, and selected timing paths. `scripts/Measure-Performance.ps1` records comparative system and media measurements.

**Concrete uncertainty:** The owner reports that a file around 40 MB can take minutes after its download finishes. That case has not been reproduced in the current environment: no exact input, source format, selected output, processing steps, machine/storage details, command, or timing trace is available. The time could be dominated by a true decode/re-encode, remuxing, audio/video merge, metadata or format probing, subtitles, output validation, hashing, disk I/O, or repeated copies/reads. These operations have different expected costs and cannot share one meaningful speed target.

The available local remux timing log (`release/audit-evidence/full-engine-batch-20261119.log`) measured 8.9 MiB synthetic MP4 and 9.4 MiB synthetic WebM outputs. MP4 merge p50 was 21.058 ms and validation p50 44.639 ms; WebM merge p50 was 200.761 ms and validation p50 70.135 ms. The run failed later because the Matroska benchmark helper lost edit-list metadata, so these exploratory fixture timings are not a verified performance baseline. They do not measure a 40 MB workload, Windows Media Foundation transcoding, or end-to-end publication. `MEDIA_ENGINE_REPORT.md` is a historical implementation report and does not resolve that gap.

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
5. Define required output/player compatibility and quality for each requested encoder, including whether MP3/FLAC encoding is part of this approved output matrix or remains limited to available native Windows codecs.
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

## Indexed byte-range clipping for single-file media

**Affected workstream:** Media discovery, HTTP range planning, MP4/WebM indexing, clip boundaries, muxing, durable resume, and output validation.

**Related requirements:** VES-031 (OPUS section H, lines 1195–1216: indexed YouTube single-file formats and byte-range clipping), VES-030 (selected output support), and USER-PERFORMANCE. See `CLAUDE-AUDIT-SOURCE-INDEX.md`, `MASTER-TODO.md`, `STATUS.tsv`, and the VES-031 rows in `IMPLEMENTATION-2026-11.md`. This remains approved product scope; the design deferral does not close it.

**What VESPER must ultimately accomplish:** Let the user request a time range from an indexed single-file MP4 or WebM representation and fetch only the data needed to produce that range when the representation and output path support it. Keep audio and video synchronized, preserve required decode dependencies and timestamps, produce a playable result, and be honest about whether a requested boundary is exact or aligned to a keyframe. Do not silently download the whole source and call it a byte-range clip. If a safe partial path is unavailable, explain the actual behavior before queueing and preserve the existing explicitly supported alternatives.

**What exists now:**

- `MediaDiscoveryForm` shows clip controls only when `MediaTransferBridge.SupportsSegmentClip` accepts the prepared choice. That helper allows WAV, completed HLS, DASH plans with bounded durations, and two-track HLS/DASH merge plans. It does not enable single-file MP4 or WebM clips.
- Completed segmented HLS/DASH clips use a persisted `MediaTimeRange`; segment selection can start at a nearby segment boundary and the picker says so. This is whole-segment selection, not single-file index fetching.
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
