# VESPER Intelligence

## Goal

Find the intended file or media program even when a site's presentation changes. Use provider adapters as specialist evidence, not as the only route. Keep inference portable, local, bounded, inspectable and replaceable. Intelligence must not be able to authorize a download, bypass protection or compromise file ownership.

## Components and current implementation

| Layer | Source | Responsibility |
|---|---|---|
| Player/control intent | extension/vesper-intelligence.js, vesper-control-evidence.js | Pure bounded facts, ranking, explicit abstention, control relationships and batch intent |
| Browser observation | extension/content.js, media-source-witness.js, player-source.js | Observe actual playback, generation changes, document scope, MSE provenance and user interaction |
| Source association | extension/media-evidence.js, background.js | Rank independently verified sources; ambiguous sources require a selection |
| Structured discovery | src/Vesper.MediaResolver/Discovery/PlayerConfigDiscovery.cs, PageScanner.cs, CandidateProcessor.cs | Bounded declared player configuration, generic extraction, protocol/format verification |
| Specialist knowledge | src/Vesper.MediaResolver/Extractors | Provider identities, metadata, known API and stream semantics |
| Acquisition | src/Vesper.Engine, Vesper.MediaPlanning, extension/browser-downloads.js | Validate transfer plan and actual bytes; preserve ownership; recover safely |

The new player model has versioned feature names, independent weights and a decision margin. Its default weights are an authored baseline, not a trained neural model or a calibrated probability. `createPlayerRanker({version:1,weights,margin})` accepts a validated independent model; `playerFeatures` exposes the exact feature transformation used at runtime. The browser supplies facts only. The model cannot perform network calls, modify the DOM or trigger actions.

The configuration discovery component tolerates common structural changes but still understands a bounded vocabulary of player and source concepts. It does not evaluate arbitrary JavaScript, crawl every URL, or combine unrelated source groups.

## Offline learning, not production self-modification

`scripts/train-player-intent.mjs` fits a small pairwise logistic ranking model to labeled pages. Separate train, validation and untouched holdout partitions must have disjoint site-family identifiers. Validation selects the margin; the holdout compares the candidate with the baseline. The script produces a candidate report, never overwrites a bundled model and never deploys it.

Example command (all inputs are independently labeled JSON arrays; use Git-ignored release/qa-* output):

```
node scripts/train-player-intent.mjs train.json validation.json holdout.json candidate.json
```

Each case has a stable site-family group, a chosen ephemeral candidate ID or null for ambiguity, and bounded numerical/boolean player facts. Required candidate fields: id, area, visibleFraction, playing, ready, progressed, controls, muted, incidentalLoop, interacted, incumbent. Do not record signed URLs, cookies, authorization, private text or persistent user identifiers.

Adapter results are useful as weak teachers: extract the expected program ID, source relationships, known format combinations and verified outcomes. They are not automatically correct labels. Actual user-selected player identity, independently observed provenance, decoded media, byte comparison and manual review determine ground truth. Group all variations of the same site or fixture family in the same partition, including mirrors and structural mutations. Keep a later time-based holdout for site changes.

The current training implementation is verified against synthetic fixtures only. A production learned model requires enough independent real labels, measured holdout improvement, regression checks, and explicit release review. Runtime never retrains itself or downloads executable model code.

## Roadmap and release gates

1. **Collect and label**: document/frame/generation-scoped evidence, actual target identity, structured sources, independent media facts, user gesture, final ownership and result. Create public/consented fixtures; no normal browser profile instrumentation. Store only bounded, redacted features and reproducible public case references.
2. **Generalize**: add generic DOM/JSON/accessibility relationships; model negative evidence, multiple players, stale generations, nested frames, delayed controls, and independent source proofs. Keep one reusable policy rather than provider-specific ranking branches.
3. **Plan and verify**: choose bounded candidate acquisition routes; independently verify selected identity, file/stream compatibility and permissions. Use adapters as specialist providers or fallback. Expired/failed sources go through existing recovery state machines, never a blind retry.
4. **Learn and compare**: fit lightweight local candidate models offline, compare to the baseline and adapters on disjoint site families. Promote only after holdout improvement without integrity or latency regressions. Keep a versioned rollback model.
5. **Scale QA**: public direct files and downloads, content-disposition/redirects/ranges/countdowns, HLS/DASH/HTTP, selected audio/subtitles/clips, feeds and reused players, blob/MSE/authorized session paths, negative/protected cases, crash/restart, app and extension together. Preserve real failures and rerun after remediation. Include novel site families, not just permutations of known fixtures.
6. **Benchmark and ship**: use matched eligible tasks against IDM, FDM, MDM and yt-dlp; compare coverage, correct native acquisition, output integrity, latency and resource use. Do not declare a competitor surpassed without measured evidence.

Proposed release acceptance targets (not measured current percentages):

| Metric | Target and denominator |
|---|---|
| Ordinary native interception | At least 99% of eligible actual user-initiated attachment/download cases; Chrome-owned completions remain misses |
| Clear supported media | At least 95% correct native publication on independent eligible cases, including requested output semantics |
| Decisive player intent | At least 99% correct decisive selections, at least 95% recall on unambiguous labeled cases; ambiguity decisions counted separately |
| Integrity and ownership | Zero observed wrong-file publications, silent truncations, duplicate ownership, unauthorized actions, credential leaks or lost Chrome downloads |
| Crash recovery | Every supported fault point passes byte/identity/ownership recovery checks; distinguish safe attention from automatic recovery |

Require at least 200 independent real eligible inputs per ordinary-file and clear-media track, stratified by mechanism and site family, with actual registered browser-to-native handoff in the test population. Include sample counts and uncertainty. Do not exclude a failed supported case simply because the implementation missed it. Report unavailable/refused/protected/ambiguous cases separately with the reason; never convert safe refusals into successful downloads. Tests of parser or decision variants are useful but not substitutes for real independent inputs.

Before promoting a model, require no new safety violations, meaningful recall at the required precision, no regressions in exact identity cases, bounded CPU/memory, and real browser integration. Compare matched source/output requests, not marketing claims.

## Limits

No browser observer can guarantee universal success. Unavailable servers, DRM, one-use responses, protected content, signed-session constraints, unknown codecs, and ambiguous user intent are fundamentally different from resolvable code bugs. The engine should recognize these limitations, preserve the working browser path and report truthful outcomes.

No new GPU, paid model, external API, runtime Python/Node dependency, remote inference, telemetry, or bundled yt-dlp is required for this architecture. Keep the existing native Windows app and MV3 extension.
