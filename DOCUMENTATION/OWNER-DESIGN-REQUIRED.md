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
