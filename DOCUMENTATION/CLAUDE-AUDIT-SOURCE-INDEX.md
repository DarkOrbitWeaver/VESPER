# VESPER — Claude audit source-location index

**Date:** 2026-11-17 · **Current inspected source baseline:** `cd914a3de37180ba82659082d29162067481dae9` on `codex/vesper-continued`.

**Purpose:** Exact original-report traceability and a work queue, **not** completed source-to-behavior reconciliation or release approval. Status values below are imported verbatim from the existing tracker, not independently proven. Confirm current file/line/test evidence for every row before marking audit reconciliation DONE.

Original reports read contiguously in full from ChatGPT Library this takeover: `OPUS5.5MAX-VESPER-DEFINITIVE-ENGINEERING-EXCELLENCE-AUDIT.md` lines 1–2185; `VESPER-ENGINEERING-AUDIT B.md` lines 1–608; `VESPER-IMPLEMENTATION.md` lines 1–229. The definitive audit covers A–U plus appendix; the 54 numbered VES findings are non-contiguous. The old implementation report is historical, not current-proof.

## Original definitive audit: 54 VES findings

| ID | Original section / line range | Claim (historical) | Current tracker status | Tracker reference |
|---|---|---|---|---|
| VES-001 | E, 409–526 | Security policies are implemented twice, and the weaker copy guards real transfers | Implemented; focused tests | `STATUS.tsv:2` |
| VES-002 | E, 527–572 | The service worker accepts privileged commands from content scripts without sender checks | Preserved + extended | `STATUS.tsv:3` |
| VES-003 | E, 573–621 | "Use site sign-in" replays cookies that the browser itself would withhold | Partial | `STATUS.tsv:4` |
| VES-004 | E, 622–655 | Distribution: Developer-mode extension and unsigned binaries | External dependency | `STATUS.tsv:5` |
| VES-005 | E, 656–692 | Build identity: every build is "1.0.0" and nothing shows which build is installed | Implemented; Windows verify | `STATUS.tsv:6` |
| VES-006 | E, 693–744 | Converted outputs ("M4A · Converts from OPUS", "Compatible MP4") lose all metadata and artwork | Partial | `STATUS.tsv:7` |
| VES-007 | E, 745–767 | The crash-recovery test for MP3 tags is flaky under load | Preserved | `STATUS.tsv:8` |
| VES-008 | E, 768–787 | Public documentation contradicts the implementation | Implemented with historical reports retained | `STATUS.tsv:9` |
| VES-009 | E, 788–823 | Expired-link rescue for merge jobs exists in the engine but no UI reaches it | Partial | `STATUS.tsv:10` |
| VES-010 | F, 837–886 | The connect callback tries addresses one at a time under a single 20 s budget (no Happy Eyeballs) | Implemented; focused tests | `STATUS.tsv:11` |
| VES-011 | F, 887–905 | An invalid custom proxy is silently ignored, and differently by the two stacks | Preserved | `STATUS.tsv:12` |
| VES-012 | F, 906–934 | Checkpoint cost scales with throughput (every 4 MiB: flush, FULL-sync SQLite commit, manifest rewrite) | Preserved; not benchmarked | `STATUS.tsv:13` |
| VES-013 | F, 935–953 | Bodies whose length is defined by connection close can be published truncated | Implemented; focused end-to-end tests | `STATUS.tsv:14` |
| VES-014 | F, 954–970 | A `Content-Encoding` on a 200 response is decoded and saved, even when the encoding *is* the file | Preserved | `STATUS.tsv:15` |
| VES-015 | F, 971–983 | Minor transfer inefficiencies | Implemented | `STATUS.tsv:16` |
| VES-016 | F, 984–1004 | HLS VOD segments are fetched serially and buffered whole (up to 64 MiB) | Partial; solution tests | `STATUS.tsv:17` |
| VES-017 | F, 1005–1022 | The recovery-manifest import trusts the destination and file name from an unauthenticated file | Preserved + repaired | `STATUS.tsv:18` |
| VES-018 | F, 1023–1033 | `If-Range` with a date that may not be a strong validator | Preserved | `STATUS.tsv:19` |
| VES-019 | F, 1034–1044 | The list is refiltered and resorted on every keystroke | Partial; Windows tests | `STATUS.tsv:20` |
| VES-020 | G, 1082–1106 | `webRequest` listeners wake the service worker for every XHR and media response on every site; each media response rewrites session storage | Implemented; Chrome verify | `STATUS.tsv:21` |
| VES-021 | G, 1107–1128 | Chrome-kept downloads have no explanation, so users read correct fallbacks as failures | Partial; browser verification open | `STATUS.tsv:22` |
| VES-022 | G, 1129–1139 | "More formats" in the in-page picker cannot be collapsed | Implemented; Chrome verify | `STATUS.tsv:23` |
| VES-023 | G, 1140–1149 | Trimming and audio-track choice are desktop-only | Implemented; Chrome verify | `STATUS.tsv:24` |
| VES-024 | G, 1150–1159 | The butterfly detaches from the player during scroll; its rest state is fully static | Partial | `STATUS.tsv:25` |
| VES-025 | G, 1160–1171 | Minor extension items | Partial | `STATUS.tsv:26` |
| VES-030 | H, 1185–1194 | No WAV, FLAC, MP3 or Opus output selection | Partial | `STATUS.tsv:27` |
| VES-031 | H, 1195–1216 | Time ranges work only for segmented HLS/DASH, but YouTube's single-file formats are indexed and could be clipped by byte range | Partial | `STATUS.tsv:28` |
| VES-032 | H, 1217–1229 | The "M4A (AAC)" choice prefers transcoding Opus over YouTube's native AAC | Preserved | `STATUS.tsv:29` |
| VES-033 | H, 1230–1240 | Conversion is offered without probing whether this machine has the decoder | Partial; Windows verify | `STATUS.tsv:30` |
| VES-034 | H, 1241–1247 | MP4 tag details (latent) | Preserved | `STATUS.tsv:31` |
| VES-035 | H, 1248–1257 | HLS discontinuities and codec changes are refused rather than handled | Open engineering | `STATUS.tsv:32` |
| VES-036 | H, 1258–1268 | Embedded subtitles: a realistic path | Open engineering | `STATUS.tsv:33` |
| VES-037 | H, 1269–1282 | YouTube depends on a single hard-coded client profile | Partial; resolver tests, live verification open | `STATUS.tsv:34` |
| VES-038 | H, 1283–1289 | The resolver's sanitizer is platform-dependent (deterministic Linux test failure) | Implemented; exact regression passed | `STATUS.tsv:35` |
| VES-040 | I, 1311–1345 | Verification of the Settings rendering repair (`2620e76`) | Partial; Windows verify | `STATUS.tsv:36` |
| VES-041 | I, 1346–1359 | The media picker's absolute layout with fixed-height labels is the same defect class | Partial; Windows verify | `STATUS.tsv:37` |
| VES-042 | I, 1360–1370 | Windows "Text size" scaling is probably ignored | Partial; Windows verify | `STATUS.tsv:38` |
| VES-044 | I, 1371–1378 | Status changes are not announced to screen readers | Preserved; Windows verify | `STATUS.tsv:39` |
| VES-045 | I, 1379–1395 | Visible vocabulary that exposes internals | Partial | `STATUS.tsv:40` |
| VES-046 | I, 1396–1404 | The dangerous-extension list for Open is incomplete | Preserved | `STATUS.tsv:41` |
| VES-050 | L, 1661 | Installer's final message | External + open integration | `STATUS.tsv:42` |
| VES-051 | L, 1662 | No update awareness | Implemented; Windows verify | `STATUS.tsv:43` |
| VES-052 | L, 1663 | Portable build cannot connect Chrome | Implemented; Windows verify | `STATUS.tsv:44` |
| VES-053 | L, 1664 | No "remove my data" on uninstall | Open engineering | `STATUS.tsv:45` |
| VES-054 | L, 1665 | Running-process replacement | Partial; Windows verify | `STATUS.tsv:46` |
| VES-055 | L, 1666 | Asset provenance/naming | Open / owner input | `STATUS.tsv:47` |
| VES-060 | O, 1746 | Two HTTP stacks | Partial | `STATUS.tsv:48` |
| VES-061 | O, 1747 | Duplicated security primitives | Implemented | `STATUS.tsv:49` |
| VES-062 | O, 1748 | Format logic duplicated JS/C# | Partial | `STATUS.tsv:50` |
| VES-063 | O, 1749 | Large absolute-layout forms | Partial | `STATUS.tsv:51` |
| VES-064 | O, 1750 | Cross-project linked planner source | Open engineering | `STATUS.tsv:52` |
| VES-065 | O, 1751 | `Publish` file-name predicate | Preserved | `STATUS.tsv:53` |
| VES-066 | O, 1752 | No guard against unreachable APIs | Open engineering | `STATUS.tsv:54` |
| VES-067 | O, 1753 | Stringly-typed reasons | Open engineering | `STATUS.tsv:55` |

## Independent audit B: nine VSP findings

| ID | Original line range | Claim | Current tracker status | Tracker reference |
|---|---|---|---|---|
| VSP-001 | 171–194 | HLS can forward source cookies and Authorization to unrelated origins | Partial; independent audit verification open | `STATUS.tsv:84` |
| VSP-002 | 195–218 | Any `SqliteException` can be misclassified as database corruption | Partial; targeted proof open | `STATUS.tsv:85` |
| VSP-003 | 219–242 | The documented ZIP installer is not transactional | Partial; packaged-path verification open | `STATUS.tsv:86` |
| VSP-004 | 243–266 | R0 rescue can accept a validator downgrade that the runner then rejects | Partial; end-to-end rescue proof open | `STATUS.tsv:87` |
| VSP-005 | 267–290 | DPAPI failures can escape startup error handling | Partial; Windows negative-case verification open | `STATUS.tsv:88` |
| VSP-006 | 291–314 | Format deduplication discards user-meaningful variants before selection | Partial; regression coverage audit open | `STATUS.tsv:89` |
| VSP-007 | 315–336 | Successful media-cache hits can outlive their source URL expiry | Partial; clock-boundary test open | `STATUS.tsv:90` |
| VSP-008 | 337–358 | The stated performance gates have no complete reproducible baseline harness | Open engineering | `STATUS.tsv:91` |
| VSP-009 | 359–378 | Primary documentation describes a different application state | Partial; documentation reconciliation open | `STATUS.tsv:92` |

## Original section N: 20 ranked opportunities

Original opportunity rows are explicitly **not** automatic release blockers. The current backlog tracks these as N IDs; verify owner priorities before implementing. Mapping uses the original rank printed in section N.

| Backlog ID | Original line | Original ranked idea | Tracker status |
|---|---|---|---|
| N-08 | 1724 | **Smart filename templates** (`{title} - {artist}`, `{site}/{title}`), previewed live | Implemented; focused engine and settings tests |
| N-09 | 1725 | **Completion actions**: open folder, notify, "sleep when queue finishes" (with a 60 s cancelable prompt) | Open engineering |
| N-10 | 1726 | **Metered-connection awareness**: pause new jobs on metered networks unless allowed | Open engineering |
| N-11 | 1727 | **Checksum from page** (detect `SHA256SUMS`/`.sha256` next to a download URL; offer "verify with publisher checksum") | Implemented; solution tests |
| N-12 | 1728 | **Mirror substitution** (user pastes another link; identity rules decide) | Partial |
| N-13 | 1729 | **Batch/playlist quality decision once** ("apply to all 24") | Partial |
| N-14 | 1730 | **Import/export** of the list (JSON/CSV of URLs plus states; no secrets) | Open engineering |
| N-15 | 1731 | **Drag-and-drop** of links and `.url` files onto the window | Implemented; Windows fixture |
| N-16 | 1732 | **Per-site rules** (always audio, always a given folder, never capture) | Open engineering |
| N-17 | 1733 | **Diagnostics bundle** (redacted log, versions, settings; local zip the user can share) | Implemented; solution tests |
| N-18 | 1734 | **Low-disk forecast** ("Queue needs 14 GB; 9 GB free") | Implemented; solution tests |
| N-19 | 1735 | **Podcast feed follow** (new episodes queued on demand, no background polling unless opted in) | Open engineering |
| N-20 | 1736 | **Windows Share target / "Open with VESPER" for .m3u8/.mpd files** | Open engineering |

The original **ranks 1–7** are covered by existing VES/USER work rather than duplicate `N-01`–`N-07` identifiers. Their original claims still need individual dispositions:

| Original rank / line | Opportunity | Existing backlog coverage | Status |
|---|---|---|---|
| 1 / 1717 | Identity-safe expired media URL refresh | VES-009, USER-SOURCE-REFRESH | Partial; unsafe cases refused |
| 2 / 1718 | Explain why Chrome keeps a download | VES-021, USER-CHROME-REAL | Partial; real Chrome verification open |
| 3 / 1719 | Intent-first shared picker | VES-045, VES-062 | Partial; shared capability document redesign open |
| 4 / 1720 | Indexed media clips and WAV output | VES-030, VES-031 | Partial; byte-range clipping open |
| 5 / 1721 | Native Opus remux | VES-030 | Implemented path; Windows playback verification open |
| 6 / 1722 | Metadata/artwork after conversion | VES-006 | Partial; real players and output variants pending |
| 7 / 1723 | Build identity and update awareness | VES-005, VES-051 | Identity implemented; manual update/check release gate incomplete |

## Unnumbered, cross-cutting and acceptance requirements

These are **separate audit obligations**, not all new feature requests. A range may also contain numbered findings; the unnumbered behavior and test requirements still need dispositions.

| Trace ID | Source lines | Additional requirement / claim | Current disposition and evidence gate |
|---|---|---|---|
| AUDIT-EXTRA-001 | A 51–146 | Historical executive scorecard and claims; do not reuse numerical ratings as current evidence | OPEN — Historical context; compare with current Q0–Q10 |
| AUDIT-EXTRA-002 | B 147–220 | Coverage inventory and unexamined code paths | OPEN — Independent current source coverage outstanding |
| AUDIT-EXTRA-003 | C 221–287 | 50-capability feature inventory and exposed/unreachable paths | OPEN — Compare to current limitations and front ends |
| AUDIT-EXTRA-004 | D 288–406 | Architecture, durability state machine, browser handoff sequence, and eight documentation discrepancies | OPEN — Revalidate current code, source ownership and documentation |
| AUDIT-EXTRA-005 | F 824–1044 | HTTP protocol invariants, fault cases and prospective performance fixes beyond numbered headings | OPEN — Verify adversarial Q1/Q8 and performance Q6 |
| AUDIT-EXTRA-006 | G 1045–1171 | Chrome state machine and API ZIP variant matrix, including POST/blob/single-use failures | OPEN — Real Chrome/native-host acceptance and disclosure Q2 |
| AUDIT-EXTRA-007 | H 1172–1308 | Media mux/demux strengths, output-format capability matrix and decoder availability | OPEN — Fixture corpus, Windows codecs and Q3 |
| AUDIT-EXTRA-008 | I 1309–1412 | UI paint audit, nested layout, text-size matrix and artwork provenance | OPEN — Actual DPI/theme/Narrator evidence Q4/Q5 |
| AUDIT-EXTRA-009 | J 1413–1511 | Butterfly motion states, arrival, rigid follow, idle budget and focus contrast | OPEN — Real Chrome motion and reduced-motion acceptance Q2/Q4/Q5 |
| AUDIT-EXTRA-010 | K 1512–1629 | Intent-first picker, shared C# capability document, format honesty, time-range clips, quality presets | OPEN — Compare both front ends; current full redesign remains open |
| AUDIT-EXTRA-011 | L 1630–1669 | First Flight timeline, installer behavior, signing and app/extension distribution requirements | PARTIAL — First Flight UI timing and live-window captures verified on ae9b091. Original timeline, clean Windows install, signing and Store Q7/Q10 remain open. |
| AUDIT-EXTRA-012 | M 1670–1710 | Benchmark interpretation, extension wake-up, disk fsync, 8-hour memory and leak measurements | OPEN — Current reproducible measurements Q6 |
| AUDIT-EXTRA-013 | N 1711–1741 | 20 separately ranked product opportunities; deliberately deferred feature bloat | OPEN — N-ID rows below; P3 unless owner explicitly upgrades |
| AUDIT-EXTRA-014 | O 1742–1766 | Architectural simplification proposals; VES-068 positive comment-quality finding omitted from U index | OPEN — Keep design readable, remove duplication only with proofs |
| AUDIT-EXTRA-015 | P 1767–1882 | Reference sketches and underlying assumptions, including proposed/nonexistent APIs | OPEN — Do not mistake sketches for implemented functions |
| AUDIT-EXTRA-016 | Q 1883–1972 | T0–T3 test tiers, 8×8 failpoint crash matrix ×50, network faults, Chrome test harness, UI matrix, media validation, release signoffs | OPEN — All acceptance plans separately tracked by Q0–Q10; not completed |
| AUDIT-EXTRA-017 | R 1973–2037 | Phase 0–5 dependency roadmap and P0 priorities | OPEN — Reprioritize based on actual current source; no blind rewrites |
| AUDIT-EXTRA-018 | S 2038–2060 | Original release-candidate caveats and explicit broad-release blockers | OPEN — Current decision NOT APPROVED |
| AUDIT-EXTRA-019 | T 2061–2074 | Research: live CMAF, hashes/Metalink, HTTP/3, hardware MFT, accessible waveform, reproducible builds, bridge schema | OPEN — Optional research; require measurements or approval before expansion |
| AUDIT-EXTRA-020 | U 2075–2135 | Original finding index (distinct from all cross-cutting requirements) | OPEN — Reconcile independent of numbering |
| AUDIT-EXTRA-021 | Appendix 2136–2185 | Seven original audit blind spots incl. content.js selection, DownloadGrid accessibility, installer rollback, native reply timings, live media churn and fuzzing | OPEN — Explicit current independent review queue |
| AUDIT-EXTRA-022 | B (independent) 451–608 | Independent audit benchmark limits, P0–P3 prioritized repair plan and acceptance obligations | OPEN — Keep VSP risk gates open until matching proofs |
| AUDIT-EXTRA-023 | Implementation 1–229 | Historical Linux patch verification, 65+ file changes, 87 extension tests and 11 user-facing fixes | OPEN — Historical only; current Windows 947 and extension 126 supersede counts |
| AUDIT-EXTRA-024 | B (independent) 1–170 | Historical executive assessment, audit methodology, architecture, workflows, code strengths and CI restrictions | OPEN — Cross-check current source in `VSP-INDEPENDENT-RECONCILIATION.md`; no old score adopted |
| AUDIT-EXTRA-025 | B (independent) 379–450 | Capability and competitor comparison, product gaps, differentiation and limits | OPEN — Informational, not compatibility or performance proof |

## Current independent source checks (limited, reproducible)

- `VSP-001`, definitive `VES-003` overlap: `HttpTransport.SendAsync` pins `CredentialOrigin`; `tests/Vesper.Tests/HlsCredentialScopeTests.cs` has four end-to-end, isolated-two-origin cases: same-origin playlist/segments/redirect, cross-origin key, cross-origin initialization/fragments, and cross-origin redirect. **4/4 passed** at HEAD before test-only changes, `dotnet test tests/Vesper.Tests -c Release --no-restore --filter FullyQualifiedName~HlsCredentialScopeTests` (combined focused run 10/10). This does not prove real authenticated Chrome handoff.
- `VSP-002`: `JobStore.Open` quarantines only SQLite primary codes 11 (CORRUPT) or 26 (NOTADB); two new Windows/SQLite tests added 2026-11-17 for exclusive lock preservation and genuine invalid-data quarantine; existing migration rollback test retained. **6/6 passed** focused after adding locked-sidecar rollback, successful sidecar archiving and unique-backup cases. Busy, permission, unclean shutdown and power-loss recovery still need proof.
- `VSP-004`: `RescueEvaluator` accepts a strong-ETag R0 only with a matching strong ETag on the same resource; source's `RescueIdentityRegressionTests.cs` has missing ETag rejection, a trusted hash fallback, and a new real `FaultServer` partially downloaded strong-ETag fixture that rejects a no-ETag replacement without modifying persisted source URL, state or saved bytes. **3/3 passed** in focused batch. Additional restart/resume variants remain open.
- 2026-11-17 Windows Release regression at `cd914a3` baseline: **941/941** .NET and **126/126** mocked extension tests. After the further SQLite recovery hardening: **947/947** .NET (engine 529, resolver 357, WinForms 41, Setup 20), **126/126** extension; no failed or skipped tests in these runs. Test output is not real-browser, visual, signed-distribution or clean-VM evidence.
- 2026-11-17 next continuation: `DownloadEngine.Start` now disposes on recovery/scheduler failure, and `StartSession` removes undispatched resources on a refused SQLite save. New two-case `EngineStartupFailureTests` reproduced a retained SQLite handle before the fix. Added second corrupt-DPAPI ciphertext startup case: **seven focused startup/security tests pass**. Windows Release **950/950** .NET (engine 532, resolver 357, Windows UI 41, setup 20) at the previous checkpoint, no failures or skips. Human UI error-dialog and cross-user DPAPI remain unverified.

- 2026-11-17 native HTTP Rescue durable-atomicity fix: `DownloadEngine.Rescue.ApplyRescue` previously changed the live URL and duplicate index before `SaveLocked` accepted an update. A deterministic SQLite trigger test **failed against old code** (memory URL diverged from DB), and two new tests pass after detached-candidate save-first implementation: empty and partially downloaded jobs preserve URLs, bytes, state and index when DB refuses the write; safe retries work and the partial file completes byte-exact. `DownloadJob.CopyForUpdate` intentionally does **not** alter the hot checkpoint `CloneForCommit` behavior. **952/952 Windows Release .NET tests passed** (engine 534, resolver 357, WinForms 41, Setup 20). Independently discovered general `DownloadEngine.UpdateJob` mutates the live job before persistence; this is now a separate **OPEN P1 `USER-ATOMIC-STATE`** item in `STATUS.tsv` and `MASTER-TODO.md`, not silently treated as fixed.

- 2026-11-17 general `USER-ATOMIC-STATE` work on `codex/vesper-continued`: seven UI command fault-injection tests were already green. Four new `TransferMutationAtomicityTests` now validate failed-write source/index/extents/partial preservation; one-time stateful metadata mutation; exact copying of **all public mutable DownloadJob properties**; and a **red→green 8,192-byte** checkpoint/metadata interleave that previously overwrote freshly committed SQLite extents with zero. `DownloadEngine.UpdateJob` stages/saves before publishing onto the same live job; `CommitCheckpoint` now publishes saved progress under the same lock as SQLite commit. **963/963 .NET Release** (engine 545, resolver 357, WinForms 41, setup 20), **126/126 Node extension mocks**, no skips or failures. Further queue/cancel/restart, browser/media source refresh, crash-atomicity, active-session stress and performance remain **OPEN**.

- 2026-11-17 large durable-lifecycle continuation: `QueueMoveAtomicityTests` (4, red→green), `LifecycleCommandAtomicityTests` (4, including genuine loopback HLS recording stop rejection/retry and unsafe later sidecar red→green), `RestartAtomicityTests` (4, initial vs final SQLite failure and path tampering), and `JobStoreDeleteAtomicityTests` (2, either table rollback) were added on `codex/vesper-continued`. `JobStore.SaveQueueOrders` commits one/two position-only updates atomically before publishing live state; `StopAndKeepLive` and idle Cancel save before side effects; `RestartFromScratch` persists `NeedsAttention/restart-cleanup` before exact temporary deletion, then commits zero-progress requeue; `TryRemoveCancelledFiles` now preflights all owned paths. Final Windows solution **977/977 .NET Release** (engine 559, resolver 357, Windows UI 41, setup 20) and **126/126 Node extension mocks** passed. Remaining real crash/recovery, actual Chrome/native messaging, media refresh/browser handoff, direct save paths, Q0–Q10 and original line-by-line reconciliation stay **OPEN**. This batch also exposed and fixed a red→green post-crash recovery bypass: `Resume`/`StartNow` could schedule a parked `restart-cleanup` job with stale/missing partial data, so `Requeue` now refuses that state, as do `StopAndKeepLive`, `ReplaceLinkAsync` and the race-checked `ApplyRescue`. Real process-kill/permissions/real-browser verification remains OPEN.

## Mandatory next reconciliation step

All nine independent B findings now have a current-source evidence ledger in `VSP-INDEPENDENT-RECONCILIATION.md`. For **every remaining** row above, fill a separate evidence record containing: exact current source path and method/lines, behavior verified/rejected/superseded, named test and result + commit, remaining manual gate, and any conflict with user decisions. Cross-check current `STATUS.tsv` and `MASTER-TODO.md` bidirectionally. The source index proves original audit *location* and read coverage only; no item acquires release-ready status from this document alone.
