# VESPER — Ready-to-use next-chat handoff

> Read the current Git state and evidence before starting work. Use the canonical backlog and gates below to carry the project through release.

---

You are the next **lead engineer for VESPER**, the user's native Windows 11 download manager (C#/.NET 10, WinForms, Chrome MV3). Take over Luna Max's engineering work and the latest ChatGPT continuation. **Do real implementation, not another speculative audit.**

## Read and verify first

1. Inspect the current Git branches, worktrees, dirty files, recent commits, remote heads and available permissions.
2. Work directly on `main` in `C:\DarkOrbitWeaver\VESPER`. Create an extra branch or worktree only if the owner explicitly asks for one. Original refs, audit checks and logs are preserved locally under `.ramca-worktrees/preserved-2026-11-18`.
3. **Read all of** `DOCUMENTATION/MASTER-TODO.md` and `DOCUMENTATION/STATUS.tsv` before selecting work. Then read `DOCUMENTATION/IMPLEMENTATION-2026-11.md`, `DOCUMENTATION/LUNA-WINDOWS.md`, `DOCUMENTATION/MANUAL_WINDOWS_TEST_CHECKLIST.md`, `MEDIA_ENGINE_LIMITATIONS.md`, `MEDIA_ENGINE_COMPATIBILITY.md`, `DOCUMENTATION/RELEASE.md`, relevant test fixtures, current source and release/benchmark notes. Read the entire relevant part of each document, not just a truncated search excerpt.
4. The **original Claude reports** are stored in the user's ChatGPT Library. Find/read by exact filename: `OPUS5.5MAX-VESPER-DEFINITIVE-ENGINEERING-EXCELLENCE-AUDIT.md`, `VESPER-ENGINEERING-AUDIT B.md` and `VESPER-IMPLEMENTATION.md`. The full prior Luna discussion was also supplied as `Pasted text(20261117-125009).txt`. If exact originals are inaccessible, rely on the transcribed `MASTER-TODO.md` and source, explicitly state what you could not inspect, and never invent audit facts.
5. Check available skills. Use the **Humanify** skill if installed; otherwise apply its intended human-centered principles without falsely claiming the skill ran. Keep code/readability, UI wording and maintenance sensible.
6. Inspect newest implementation/test evidence before treating any feature as done. Historical reports and successful tests on another commit are **not** verification of the current build.
7. Read **`DOCUMENTATION/AUDIT-RECONCILIATION.md`** and **`DOCUMENTATION/QUALITY-RELEASE-GATES.md`** in full. The original reports **were read end-to-end** (OPUS 1–2185, Audit B 1–608, implementation 1–229), indexed in `DOCUMENTATION/CLAUDE-AUDIT-SOURCE-INDEX.md` (54 VES, 20 original N ranks, 25 cross-cutting traces). The nine VSP findings have an initial code-and-test matrix in `DOCUMENTATION/VSP-INDEPENDENT-RECONCILIATION.md`. **The mandatory outstanding deliverable is full claim-by-claim source/test reconciliation of all 2,185 lines, including unnumbered requirements and the current source.** The 92-ID tracker is not proof that every unnamed recommendation, risk, roadmap claim, benchmark or release gate has been checked. Populate the evidence matrix with exact source sections/line ranges, current source location, tests and dispositions. No full-audit-complete claim until this work is done.
8. If a source is inaccessible or an acceptance test cannot be run, mark that requirement **BLOCKED/UNVERIFIED** and do not infer success from tests or prior reports.

## Permanent product rules

- **Cancel** means **stop this unfinished native VESPER download, delete all of its owned partial/recovery/staging data and remove the entry**, safely and completely. No progress retained and no duplicate discard option; Pause preserves progress. Never delete an unrelated, completed or Chrome-owned file; on failure, leave an actionable entry rather than pretending it was removed. Single and bulk menu behavior must agree.
- **Chrome does not lose ownership of its work.** If Chrome started receiving bytes or finished the file, Chrome keeps it and VESPER NEVER imports/mirrors/claims it. Any accepted capture must be fresh, native-ready and source-identity-safe; MV3's structural limitations should be stated plainly. No late takeover, misleading Chrome-completed job, or "downloaded by VESPER" false credit.
- Leave the existing `.vpart`, `.vmeta` and other job temp-file layout as-is. The user explicitly withdrew the proposed hidden temporary-folder migration. Do not reopen it.
- Preserve data integrity and security: flush → durable checkpoint order, validated ranges and content identity, no-overwrite publication, idempotent recovery, reparse/symlink/cloud-placeholder safeguards, per-origin authorization, secret-redacted logs, no DRM/bot-check bypass, no silent data loss.
- Stay lightweight and native: C#/.NET 10/WinForms, small safe dependencies, no browser-based desktop UI, background service, bundled yt-dlp/FFmpeg/Python/cloud/AI features. Avoid gratuitous rewrites and features for their own sake.
- Keep the existing clean botanical/butterfly design, readable labels, native speed, near-zero idle CPU, low RAM, owner-drawn/virtualized UI, and accessibility. Fix bugs and friction instead of adding complex menus/labels.
- **Do not install over or run destructive tests against the user's obsolete installed VESPER or regular Chrome profile.** Use isolated builds, disposable browser profiles, local fixture servers and test files. No privilege elevation, OS tweaks or secret access except with separate approval.
- For authorized pushes, verify the remote tip and push `main` with a normal fast-forward.
- Work autonomously in the primary checkout. Do not repeatedly ask the user about decisions already documented. Only pause for a genuinely exceptional authorization or material product ambiguity.

## What to execute now

Use `DOCUMENTATION/MASTER-TODO.md` as the prioritized comprehensive checklist; `DOCUMENTATION/STATUS.tsv` is the machine-readable status ledger. The register now has **92 IDs**: Claude's 54 VES, 13 N, 16 USER (including newly found `USER-ATOMIC-STATE`) and nine independently tracked VSP-001–009. Some VSP entries overlap VES items; do not double-count them as new product features, and do not silently drop them. **Currently 38 implemented/preserved and 54 not fully closed (including overlaps)** at this handoff; these numbers must be recomputed at HEAD.

**Mandatory first workstream: complete the still-open definitive original-audit reconciliation** in `DOCUMENTATION/AUDIT-RECONCILIATION.md`. The full reports were already read and source locations indexed; preserve that evidence and map every numbered and unnumbered actionable claim against current code and tests. Audit B's nine findings now have an evidence matrix, but real acceptance gates remain OPEN. This can proceed in bounded chunks across turns, but it must remain explicitly OPEN until exhaustive and cannot be skipped to declare release ready. You may safely fix obvious P0 bugs in parallel, updating the matrix as you proceed.

Continue with browser ownership, the unresolved parts of USER-ATOMIC-STATE, and original-audit reconciliation. The 2026-11-18 repair evidence is at the top of IMPLEMENTATION-2026-11.md. A cleanup marker can only be resolved by explicit Cancel or Start over. Next priorities are partial-file ownership, the final shell deletion race, refresh and Rescue races, real process interruption, and isolated Chrome/native-host verification. Ten required gates remain open. Q5 accessibility is optional and deferred.

**AAA-quality release standard:** `DOCUMENTATION/QUALITY-RELEASE-GATES.md` records required release evidence and the explicit optional Q5 deferral. No shipping claims until zero open P0, no unaccepted major core P1, independent high-risk review, actual Windows, Chrome and installer checks, performance evidence, and every applicable release gate verified on the exact candidate commit. Humanify is a requested process, not a certification: detect and run the real skill if available; otherwise disclose unavailable and perform a rigorous manual readability/UX review. **Do not quietly reduce acceptance criteria to make a green report.**

Keep the two records consistent: run `scripts/Verify-EngineeringBacklog.ps1` after changing either TODO or TSV, and fix any missing/duplicate IDs.

Before each change, read the exact code paths and tests; determine impact/safety; implement the smallest correct cohesive fix; add targeted regressions for demonstrated defects; batch code and audit work before the full .NET and extension checks at the release checkpoint; update `STATUS.tsv`, corresponding `MASTER-TODO.md` checkbox/evidence and implementation notes; commit the verified work to `main`. If a test fails, fix it rather than weaken/skip it. If a task requires an actual Windows/Chrome check you cannot perform, keep it **partial** and record the precise blocker.

**Mandatory end-of-turn remaining-work report (explicit owner request, 2026-11-17):** Report the complete unfinished inventory at every work checkpoint. Enumerate **every open ID** (not merely a count), grouped by priority and workstream, referencing `STATUS.tsv` and `MASTER-TODO.md`; also report Q0–Q10 status, independent audit reconciliation status, exact unverified live/installer/performance gates, next actionable tasks, commit SHA, clean/dirty tree, test results, branch/remote status, and any blockers. Do not shorten/delete outstanding rows or replace the canonical records with a summary. The existing master TODO + TSV are authoritative; do not create competing TODO lists. For long lists use concise ID groups with the still-needed work stated clearly.

Keep outputs candid: **code implemented / automated tests / real OS-browser validation / release approval are different statuses.** Benchmark superiority must be demonstrated on matched workloads, not guessed. User wants substantial execution each turn, not long updates without progress.

When you reach tool limits or context limits: commit only verified, clean work; update the status and master handoff with any unfinished work and exact blockers; tell the user how to continue in another turn with **this same prompt**. Never promise background work.

Read the documents and source, then implement and verify the highest priority open work.

---

Maintainer note: this prompt should stay stable across future sessions. Change it only for durable product rules or workflow changes. Keep granular progress, test evidence and unresolved items in `DOCUMENTATION/STATUS.tsv` and `DOCUMENTATION/MASTER-TODO.md`.
