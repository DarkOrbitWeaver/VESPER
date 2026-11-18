# VESPER — AAA-quality release gates (engineering definition)

**Current release status: NOT APPROVED.** "AAA" here is a quality target, not an external certification. The burden is observable correctness, refined native UX, security, accessibility, performance and repeatable installation—not feature count. Never announce "AAA", "production ready", "better than IDM" or "fully audited" from green unit tests alone.

Every required gate needs a **date, exact commit, environment, executable/build identity, test plan, evidence location, pass/fail result, and unresolved defect links**. Recording "passed" without an artifact or reproducible method is not sufficient. Maintain links in this document or a versioned release-evidence subdirectory.

## Release gates

The owner deferred Q5 on 2026-11-18. It is optional for this release and remains unverified. Its acceptance criteria stay here for future work.

| Gate | Standard for approval | Evidence that must exist | Status |
|---|---|---|---|
| Q0 Original audits | `AUDIT-RECONCILIATION.md` covers every original report line/section, numbered and unnumbered requirements; all high-severity findings proven resolved or explicitly blocked | Line-range traceability, code references, regression results, independent critical-path review | OPEN |
| Q1 Correctness and data safety | No demonstrated corruption, unintended deletion, duplicate publication, silent overwrite or secret leak in supported cases. Cancel removes only exact owned unfinished files; Pause preserves bytes; recovery respects crash/OS failure | Adversarial HTTP fixtures, checksums, failpoints, restart/progress validation, cancellation races and malformed paths | OPEN |
| Q2 Chrome ownership and trust | Chrome-finished/ongoing work never claimed by VESPER. Native acceptance before ownership. Permissions, cookies, signed URL handling, worker restart and private-origin redirection remain safe | Real disposable Chrome/native-host sessions, protocol transcripts with secrets redacted, extension tests and decision logs | OPEN |
| Q3 Media compatibility | Every advertised output preset/format is truthful and usable on the supported Windows codec set. No unsupported DRM/single-use claims. HLS/DASH/resolver failure is explicit and does not publish garbage | Sample corpus, resulting hashes/containers, ffprobe or equivalent developer-side verification, playback, seek, language/HDR/subtitles and repeatability | OPEN |
| Q4 Native UI visual quality | No clipped, ghosted, flashing, overlapping, stale or broken windows/popups in supported resolutions, DPI, zoom and themes. Destructive actions are short and clear. Real butterfly animation is smooth and honors reduced motion | Annotated screenshots/videos taken from **current build**, measured render/scroll traces, keyboard navigation and side-by-side design review | OPEN |
| Q5 Accessibility | Usable keyboard flow, logical focus, screen-reader names and event announcements, sufficient contrast, text scaling and accessible errors | Narrator sessions, keyboard-only test script, focus walkthrough, high contrast and scaling captures | DEFERRED (optional) |
| Q6 Performance/resource efficiency | Idle CPU and memory, cold-start first paint, list scroll p95, media memory peak and sustained throughput are measured; no regressions versus current verified baseline without explained trade-off. Competing-app superiority claimed only from matched repeatable tests | Build/hardware/OS/protocol parameters, raw timings and memory/GDI/handle measurements, multiple repeats, outlier policy, 8h resource slope | OPEN |
| Q7 Installer and update safety | Clean install, repair, version upgrade while app is running, interruption/rollback, portable build and uninstall all behave correctly. Default uninstall never removes downloaded data. Signed binaries/Chrome distribution prerequisites handled before public release | Disposable VM/account snapshots, setup logs, rollback artifacts, signatures and extension identity/permissions | OPEN |
| Q8 Security/privacy | SSRF/redirect/resolution policies, host-credential scope, path canonicalization, reparse/placeholder handling, native messaging authorization and diagnostics redaction tested against hostile input | Threat-case fixtures, independent review, targeted VSP-001–009 evidence, dependency/source integrity review | OPEN |
| Q9 Stability and code quality | Project builds clean, all relevant suites pass repeatedly, no unresolved high-severity bugs, resources are disposed, async races are covered, no dead paths and no unstable global state. Human-readability review complete | CI logs, repeated test runs, coverage of adversarial paths, code review, debugger exception/output summary | OPEN |
| Q10 Documentation and release truth | App's shipped version matches binaries, manifest/host, installer, README and behavior; disclaimers honestly describe unsupported sites/features and manual prerequisites | Release manifest, checksum, docs diff, QA signoff and current change log | OPEN |

## Unambiguous defect severity and stop-ship policy

- **P0/blocker:** data loss/corruption, deleting wrong user's file, credential leak, insecure origin crossing, false completion, common-path installer data loss, or misleading Chrome attribution that publishes browser work as native. No P0 open at release.
- **P1/major:** reliable reproducible failure of normal download/resume/Cancel/Rescue, supported media output invalid, major window unusable on supported DPI, or severe performance regression. Do not call feature complete until resolved; public release requires no unaccepted P1 in advertised core workflows.
- **P2/minor:** polish, limited compatibility exceptions, small-copy defects with truthful explanation and workaround. May be deferred explicitly if documented and not contrary to user goal.
- **P3/idea:** optional additions; not release blockers unless user explicitly promotes them.

A feature is **implemented** when changed in source, **tested** when automated tests run on that exact code, **verified** when real environment proves it, and **release-ready** only once its relevant gates are passed. Distinguish them in every report. Test count alone is never a success metric.

## Humanify and human-written quality criteria

1. Use the installed Humanify skill for implementation and review. It was read and applied to the 2026-11-18 repair batch. Release quality still requires the review and evidence below.
2. Humanify is **not** license to cosmetically rewrite working code or remove useful assertions, comments, logs, tests or safety checks. Favor small readable functions, expressive names, explicit contracts, no invented abstractions, and comments explaining nonobvious invariants. Preserve code style and avoid robotic filler.
3. UI quality requires actual current-build inspection: short natural labels, clear states and recoverable errors, human proportions/spacing, consistent icons and colors, predictable focus and hover, no artificial loading animation or fake progress. Validate with users' expected everyday actions.
4. Review high-impact flows (first run; paste; browser click; quality choice; download start; pause/resume; Cancel; Rescue; error; completion; update/uninstall) for **fewest reasonable steps**, no inconsistent terminology, no hidden destructive side effects, and positive/negative states.
5. Don't let optional N- ideas divert attention from core reliability or visual polish. "Humanify" describes polish and judgment, not a numerical quality certificate.

## Final release signoff procedure

- Re-read both Claude audits in full; complete audit matrix, including unnamed issues and release gates; reconcile current source and owner decisions.
- Run backlog checker; no missing/contradictory statuses. Confirm **no open P0, no unaccepted core P1**, and explicit deferrals for noncritical features.
- Finish automated, real-browser, real-Windows, performance, installer and threat-model validation on the **exact release candidate commit**. Attach evidence with timestamps/versions.
- Obtain an independent critical-path review of the release candidate; resolve failures; rerun affected gates. User decides on final shipping only after reviewing remaining limitations.
- Prepare a signed distribution path where needed; neither this doc nor passing tests authorizes publishing/pushing/installation into the user's production machine.
- Mark a gate PASS only with verifiable evidence and commit. Keep Q5 marked deferred unless it is verified. An unresolved external requirement stays BLOCKED, never silently PASS.
