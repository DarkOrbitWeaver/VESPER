# VESPER — Master engineering TODO

**Purpose:** One durable starting point for every future ChatGPT/Codex/Luna continuation. Updated 2026-11-17. Read this alongside `DOCUMENTATION/STATUS.tsv`; it is a prioritized view, not a replacement for the detailed row-by-row register.

## Source of truth and baseline

- **Authoritative current code:** `C:\DarkOrbitWeaver\VESPER\.ramca-worktrees\vesper-continued` on branch `codex/vesper-continued`, unless Git shows a newer explicitly approved continuation. The repository root `C:\DarkOrbitWeaver\VESPER` is `main`; do not edit or merge it.
- **Authoritative current item states:** `DOCUMENTATION/STATUS.tsv`; its `ID`, `Finding`, `Status` and `Implementation or exact remaining work` columns retain both completed evidence and work still needed. Never replace it with a shorter wish list.
- **Implementation evidence:** `DOCUMENTATION/IMPLEMENTATION-2026-11.md`, `DOCUMENTATION/LUNA-WINDOWS.md`, `DOCUMENTATION/MANUAL_WINDOWS_TEST_CHECKLIST.md`, `MEDIA_ENGINE_LIMITATIONS.md`, `MEDIA_ENGINE_COMPATIBILITY.md`, `reports/BENCHMARKS.md`, `reports/VALIDATION.md`, and current tests/source.
- **Original audits (in the user's ChatGPT Library, search by exact filenames):** `OPUS5.5MAX-VESPER-DEFINITIVE-ENGINEERING-EXCELLENCE-AUDIT.md` (Claude's 2,185-line report, 54 non-contiguous VES IDs and N opportunities); `VESPER-ENGINEERING-AUDIT B.md` (second independent audit, VSP-001–009); `VESPER-IMPLEMENTATION.md` (historical patch and limits); and pasted full Luna conversation `Pasted text(20261117-125009).txt`. If unavailable to a future agent, use their transcribed findings below and explicitly report unavailable sources, not invented details.
- **Historical reports are not present-day validation.** Older `MEDIA_ENGINE_REPORT.md` and `DOCUMENTATION/RELEASE.md` were written for earlier commits; corroborate their assertions with current code and dated tests.
- **At authoring:** 91 registered IDs = 54 original VES + 13 separately tracked N + 15 USER + 9 independent VSP. 38 marked implemented/preserved, 53 not fully closed **including overlaps** between VSP and VES. The 44 items in sections A–G are the preexisting incomplete VES/USER/N items; VSP-001–009 add nine independent audit-verification rows, **not nine unrelated features**. Last reported test result: 941 .NET and 126 extension passed, but rerun after changes.
- **Newest known continuation commit when this document was written:** `bb778df` (bulk Cancel UX), after `f54bd36` (native Cancel). Verify HEAD; never assume these are still latest. Worktree was clean and 9 commits ahead of `origin/main`.

## Hard constraints — don't regress these

- User-facing **Cancel** stops an unfinished native transfer, waits for writers, deletes only its own unfinished files/recovery data and removes the entry. **No retained partial data, no second discard choice, no redundant confirmation.** Pause retains data. A failed/unsafe deletion keeps the entry with an error; never touch unrelated/completed/Chrome-owned files.
- Chrome owns anything it downloaded or has already begun downloading. **No adoption of completed Chrome files, no history mirroring, no taking credit after Chrome saves bytes.** VESPER may accept only a new eligible zero-byte Chrome candidate with native readiness and exact-source checks; acknowledge MV3 limits honestly.
- Preserve today's `.vpart`, `.vmeta`, merge and staging file layout. The user explicitly rejected moving them into an app-managed folder. Do **not** reopen this optional proposal.
- Windows 11 C#/.NET 10 + WinForms and Chrome MV3, native/bounded, lightweight, zero idle polling, KISS. Do not introduce heavy runtime/FFmpeg/yt-dlp as a bundled dependency, cloud services, accounts, telemetry, or speculative abstractions.
- Preserve checkpoint ordering, content/validator identity, no-overwrite publication, trusted-hash semantics, byte-exact recovery, credential origin boundaries, safe error states, offline defaults and low resource use.
- User's installed VESPER is obsolete and is **not** a test target. Use isolated builds, temporary files, disposable Chrome profiles and test fixtures. No unexpected installation/registration/OS changes.
- All changes stay in `codex/vesper-continued` or a new isolated continuation branch. No edits to `main`; **RAMCA git pushes are denied**; do not assume local commits are remote backups.
- Prefer readable, human-made UI and code; check whether a Humanify skill exists and load it if available. Do not falsely claim unavailable skills ran.
- **Every row marked complete requires evidence.** Tests and a build alone do not certify Windows painting, real Chrome, live-site compatibility or signing.

## A. P0 — Browser ownership, security and identity (6 existing IDs)

- [ ] **VES-003 — Cookie/CHIPS/SameSite edge cases.** Verify same-origin/same-site consent, originating frames, redirects, iframe partitioning and no auth disclosure. Preserve memory-only secrets.
- [ ] **VES-009 — Merge-job Rescue and expired-link refresh.** Confirm UI → shared planner → engine refresh is reachable; test exact identity, signed redirects, plan refusal, overnight pause and restart.
- [ ] **VES-021 — "Chrome keeps" explanations.** Verify real popup clarity and explicit host sign-in consent. No misleading "moved" or "saved by VESPER" claim for Chrome-owned bytes.
- [ ] **USER-SOURCE-REFRESH — signed URL refresh.** Expand only identity-safe refresh cases; verify single origin, redirects and cancellation, no credential or token persistence.
- [ ] **USER-CHROME-REAL — live Chrome/native-host handoff.** Test fresh GET vs POST/blob/bearer/single-use, redirects, Chrome already receiving bytes, partial and completed Chrome copies, native unavailable, slow accept, worker restart, duplicate submissions and manual link flow. Observe real UI/transfer data and maintain source-identity invariants.
- [ ] **USER-UI-CHROME — real butterfly picker/native messaging.** Test isolated profile with installed native helper and real replies, focus, keyboard, scroll, fullscreen, reduced motion, sign-in prompts and error/Chrome-keeps messages.

**Blocker:** regular Chrome 157 no longer supports the unpacked-extension `--load-extension` test flag; isolated Chrome for Testing/Chromium or a carefully isolated permitted install is needed. `tests/extension/chrome-owned-smoke.mjs` has not passed as a real browser integration test. Do not change user's regular Chrome profile or install.

## B. P1 — Media engine and provider compatibility (11 existing IDs)

- [ ] **VES-006 — metadata/cover parity.** Validate supported MP4/M4A/Opus/WAV outputs with Explorer, players, tags and image types; document WAV cover limitations.
- [ ] **VES-016 — HLS segment pipeline.** Validate bounded parallel downloads, buffer ownership, ordering, failures, cancellations and digests under long/multiple jobs.
- [ ] **VES-030 — audio output matrix.** Implement/verify explicitly approved MP3/FLAC and large WAV/RF64 only with supported native codecs and clear fallback; current AAC/M4A/Opus and basic WAV are partial progress, not universal encoding.
- [ ] **VES-031 — indexed single-file byte-range clipping.** Verify MP4 `sidx` / WebM Cues and edit-list/sample-exact clipping, if implemented. No misleading "download part" promise when full source is fetched.
- [ ] **VES-033 — Windows decoder capabilities.** Run real Media Foundation codec probes and conversions, validate correct error feedback and Windows decoder absence.
- [ ] **VES-035 — HLS discontinuity epochs.** Handle timestamps/keys/codec changes safely or keep explicit refusal. Never silently join incompatible epochs.
- [ ] **VES-036 — embedded subtitles.** Add opt-in validated MP4/Matroska subtitle mux where supported; preserve existing sidecars.
- [ ] **VES-037 — YouTube.** Test the current live source ladder, VOD/Shorts/4K/8K when offered, audio languages, formats, playlists, fragile signatures and rate limits. No DRM or bot-check bypass.
- [ ] **USER-INSTAGRAM — Instagram media.** Test permitted live Reel/post/carousel/story workflows, authorization boundaries and upstream changes. Fixture-only success is not live proof.
- [ ] **USER-YOUTUBE-MIX — dynamic Mix.** Test continuation, dedup, bounds, first-page survival when paging fails and honest incomplete snapshots.
- [ ] **USER-MEDIA-MEMORY — bounded streaming media memory.** Measure concurrent HLS/DASH/merge/decryption allocations and long-session growth. Preserve safety and streaming output validation.

## C. P1 — Native UX, browser UI and accessibility (8 existing IDs)

- [ ] **VES-019 — search at scale.** Verify debounce/virtualization when metadata changes, 10k rows, typing speed and unchanged selection.
- [ ] **VES-024 — butterfly behavior.** Real-browser scroll/fullscreen/player switching, hover/idle focus and reduced motion; no permanent timers at idle.
- [ ] **VES-025 — extension remaining details.** First-run controls, keyboard focus/shortcut, timeouts, legacy message paths and error clarity.
- [ ] **VES-040 — WinForms grid/settings rendering.** Reproduce and eliminate stale pixels, repaint ghosts, hover artifacts, clipped Settings, scrolling at 40+ and 10k rows; check light/dark/high-contrast, different refresh rates.
- [ ] **VES-041 — measured picker layout.** Long filenames, languages, subtitles, narrow windows and extra text must not overlap or clip.
- [ ] **VES-042 — Windows text scaling.** 100/125/150/200% DPI and 150% text, keyboard and Narrator, compare actual images not only code assertions.
- [ ] **VES-045 — plain-language choices.** Video/Audio → Quality → Format, useful defaults, readable conversion and failure messages; avoid "AI slop" filler.
- [ ] **VES-062 — shared picker/capability interpretation.** Reduce duplicated JS/C# format ranking and labels without a risky UI rewrite; test parity across direct paste and extension.

## D. P1 — Measured performance (1 existing ID)

- [ ] **USER-PERFORMANCE — comparable benchmarks.** Record current cold start/idle CPU/private bytes/working set/grid paint, conversion/remux, loopback and matched real-network transfers against baseline and IDM/FDM/yt-dlp. Include p50/p95, raw runs, failure rates, tool versions, output equality and 8h resource slope. No claims of outperforming competitors from a single test.

## E. P2 — Packaging, release and distribution (5 existing IDs)

- [ ] **VES-004 — signed shipping and extension distribution.** Certificate, Authenticode, exact version identity, Chrome Web Store listing/permissions, approved user-facing installation. External owner/store dependencies remain.
- [ ] **VES-050 — First Flight finish.** Detect native-host and extension connection, present friendly success without unsupported developer-mode promises.
- [ ] **VES-053 — optional uninstall user-data deletion.** Only with explicit scoped consent; default preserves history/settings/downloads. Never delete downloaded media.
- [ ] **VES-054 — running-app upgrade.** Graceful stop, checkpoint flush, no locks, maintain journal and rollback; test on clean disposable Windows account.
- [ ] **VES-055 — asset provenance.** Confirm licensing and names for approved icon/butterfly/setup art; no redistributing unapproved assets.

## F. P2 — Maintainability and architecture (5 existing IDs)

- [ ] **VES-060 — shared network policies.** Remove redundant HTTP handler/proxy/SSRF/redirect logic only when current cross-origin, proxy and download security tests give proof.
- [ ] **VES-063 — large absolute-layout forms.** Split/measure only problematic surfaces while preserving stable WinForms startup and low working set.
- [ ] **VES-064 — linked planner source.** Consider moving into a proper referenced library once interface is stable; preserve direct-paste/extension parity.
- [ ] **VES-066 — unreachable APIs.** Create explicit caller/entrypoint reachability map and regression for engine commands; do not duplicate dead UI.
- [ ] **VES-067 — string-based errors.** Introduce typed/shared reason catalog with compatibility tests so UI and extension cannot drift.

## G. P3 — Product opportunities, not release blockers by default (8 existing IDs)

- [ ] **N-09 — completion actions:** notifications/open folder; optional sleep/shutdown needs clear confirmation and cancellation.
- [ ] **N-10 — metered-network handling:** user preference, no unexpected polling/service, explicit overrides.
- [ ] **N-12 — safe mirror substitution:** identity/validator proof and safe refusal.
- [ ] **N-13 — batch quality once:** one meaningful choice applied only to compatible selected entries.
- [ ] **N-14 — JSON/CSV import/export:** no secrets; bounded input, filename safety.
- [ ] **N-16 — per-site rules:** minimal explicit user-defined rules, clear precedence.
- [ ] **N-19 — podcast follow:** opt-in and no background polling unless approved. Defer if costly.
- [ ] **N-20 — Windows Share/Open With:** optional native shell integration, registration/unregistration safety.

## H. Independent Claude audit B: VSP-001–009 (9 separately tracked verification rows)

These are **already added to `STATUS.tsv`**, but overlap categories above. The VSP ID must be reconciled individually, citing actual code/test evidence; do not mark done simply because a nearby VES ID is marked done.

- [ ] **VSP-001 (P0 credential security):** HLS segments, init maps, playlists, keys and redirects must never send signed headers to a different origin. Current `HttpTransport` has scoped credentials; require two-origin tests and real HTTP evidence. Overlap: VES-003.
- [ ] **VSP-002 (database preservation):** only actual SQLite corruption codes may trigger quarantine. Exercise locked/busy/permission/migration failures and corruption. Current `JobStore.Open` catches 11/26; prove no valid DB renamed.
- [ ] **VSP-003 (installer path):** every advertised install/upgrade path must be transactional. Existing older ZIP PowerShell installer is no longer shipped; validate current portable ZIP vs First Flight semantics and clean-VM rollback. Overlap: VES-050/054.
- [ ] **VSP-004 (rescue validator downgrade):** original strong ETag to replacement without validator must be handled consistently in Rescue evaluator and runner; verify end-to-end refusal/proven resume. Overlap: VES-009.
- [ ] **VSP-005 (DPAPI startup):** wrong account/malformed protected media state gets actionable error, no database rewrite or resource leak. Current `Program.cs` catches cryptographic errors, but Windows negative tests remain.
- [ ] **VSP-006 (meaningful media variants):** preserve DRC, audio-description, language, default, context distinctions through discovery, dedup and picker. Current key includes these, verify fixtures. Overlap: VES-045.
- [ ] **VSP-007 (expired metadata cache):** `MetadataCache` now caps TTL by representation expiry; require clock-controlled mixed-expiry/boundary tests. Overlap: USER-SOURCE-REFRESH.
- [ ] **VSP-008 (performance gate):** raw repeatable baseline covering startup, grid p95, throughput and memory, with hardware conditions and regression thresholds. Overlap: USER-PERFORMANCE.
- [ ] **VSP-009 (documentation mismatch):** fix release/README/install claims and stale internal links. Explicitly mark historical tests as historical. Overlap: VES-008, currently implemented for earlier docs but not a blanket exemption.

## I. Required acceptance and release gates (not accounted for in row counts)

- [ ] Build and run all four .NET solution test projects and extension tests on the **current HEAD**, ideally repeated with no flaky retries. Use `dotnet test VESPER.slnx -c Release --no-restore --nologo` and `node --test tests/extension/*.test.mjs`.
- [ ] Run adversarial HTTP/HTTPS tests: wrong 206, redirects, truncated bodies, unknown size, HTML/captive-portal responses, proxy PAC/IPv6, private-address rebinding, timeout, disk full, removed USB, same/changed validators and byte-exact output.
- [ ] Run timed crash/failpoint matrix for HTTP, HLS, DASH, merge, conversion and cancellation; restart with original test data. Assert no duplicate published files, corruption, abandoned stages or unintended data deletion.
- [ ] Run **actual Chrome/native-host** test in disposable profile with real transfer acceptance and failures (not mocked native replies). Test worker restarts while Chrome is paused, browser ownership, sign-in and exact source identity.
- [ ] Run representative live-media corpus with current site conditions; validate all offered output choices via `ffprobe` / `ffmpeg` developer tools, playback, seek, codec/audio language/HDR/subtitles and refusal rules. Do not ship these tools as dependencies.
- [ ] Run real WinForms visual review: 100–200% mixed DPI, 150% Windows text, dark/light/high-contrast, 10k rows, hover/scroll/resize/dialog clips, reduced motion, keyboard-only and Narrator.
- [ ] Run native media conversions on the target Windows codec setup, including M4A/Opus/WAV, metadata and sample-accurate short clip.
- [ ] Run clean Windows account/VM installation, repair, upgrade/rollback, uninstall, portable path, registration ownership and user-data preservation.
- [ ] Obtain signing and extension distribution prerequisites before any ordinary-user release; distinguish external owner approval from code changes.
- [ ] Repeat performance comparison against preserved baseline with raw evidence and sensible uncertainty. Never relax fsync, validators or output verification for speed.
- [ ] Verify docs, manifest/app/host version identity and release links against this exact commit; user knows whether an installed build is obsolete.
- [ ] Give the user a manual acceptance package/build **only with explicit approval**; never install into or mutate their current obsolete production app/profile without permission.

## How to work through this checklist

1. Start each session with `DOCUMENTATION/NEXT-CHAT-HANDOFF.md`; inspect Git before editing.
2. Pick **one bounded high-impact slice** from A/H first, or the highest verified defect from B/C. Work autonomously through code → tests → evidence → commit. Avoid multiple simultaneous risky refactors.
3. Run `scripts/Verify-EngineeringBacklog.ps1` before committing documentation; it fails if an unresolved ID is missing from this checklist, duplicated, or prematurely checked.
4. For a finished item update **both** its `STATUS.tsv` row and this checkbox with commit hash, test evidence and any remaining real-world verification caveat. A partial item stays unchecked. If later evidence invalidates a fix, reopen both.
5. If a new defect emerges, create a unique `USER-...` ID in `STATUS.tsv`, place it in this plan with impact and acceptance, and keep it visible.
6. Keep VSP audit verification separate; overlap is fine, silent dismissal is not. Do not let "tests green" stand in for manual Chrome/Windows/installation evidence.
7. End each implementation turn with exact branch/commit/tests/what remains. Never push/merge to `main` under the present RAMCA policy. The next agent should re-read changed docs and the current source, never blindly replay historical patches.
