# Discovery benchmark evidence

Date: 2026-11-11. Same Linux x64 sandbox, .NET SDK 10.0.404, Release builds. Baseline is the exact requested commit.
The developer harness was built against a separate detached baseline worktree and the working branch. It is not a replacement application.

## Method

- Cold **resolver**: a new resolver/HTTP handler and empty metadata cache per iteration, in a single process. This is not a new OS process or a cleared DNS/TLS cache per sample.
- Warm: one resolver reused. The first sample is tagged cold and excluded from the warm table. Successful 90-second metadata cache hits can issue zero requests.
- First samples include JIT and connection setup. Medians and nearest-rank p95 are calculated from all tagged samples; small samples do not imply statistically significant differences.
- Fixture modes use a deterministic **40 ms artificial delay per metadata request**, not real-site latency. `browser` supplies a validated observed candidate after a cipher-only synthetic player page. `missing` has no player/media and must remain a failure.
- Live measurements were sequential baseline/branch blocks, not randomized. Network/CDN variation and JIT can dominate; differences are not attributed solely to code changes.
- Request count includes redirects and actual resolver HTTP requests; thumbnail requests use a separate client/count. Source representation latency does not mean that every representation can be transferred or muxed.
- Baseline phase timings were unavailable (null). Earlier baseline raw rows serialize `compatibleSingleStreamFormats: 0` because that bridge counter was not instrumented; it is **not** a statement that baseline HTTP/HLS lacked support. The harness now emits null for uninstrumented baselines.
- CPU is process CPU delta during the operation; allocation is process-wide managed allocated-byte delta. Peak working set is the process-lifetime RSS high-water mark, **not** per-call peak memory.
- Thumbnail timing is bytes available, not WinForms render completion. No idle CPU superiority or current yt-dlp comparison was measured.

## Observed distributions

| Dataset | Samples | Successful discoveries | p50 ms | p95 ms | Requests per lookup |
|---|---:|---:|---:|---:|---|
| baseline-browser-cold | 20 | 20 | 81.421 | 83.405 | 2 |
| baseline-browser-warm | 9 | 9 | 81.973 | 82.887 | 2 |
| baseline-dash-cold | 5 | 5 | 92.920 | 331.822 | 1 |
| baseline-hls-cold | 10 | 10 | 75.624 | 316.123 | 2 |
| baseline-hls-warm | 9 | 9 | 0.009 | 0.054 | 0 |
| baseline-missing-cold | 20 | 0 | 81.978 | 83.171 | 2 |
| baseline-missing-warm | 9 | 0 | 81.376 | 82.304 | 2 |
| baseline-youtube-cold | 3 | 0 | 805.838 | 873.666 | 1 |
| current-browser-cold | 20 | 20 | 40.783 | 42.108 | 1 |
| current-browser-warm | 9 | 9 | 40.780 | 41.718 | 1 |
| current-dash-cold | 5 | 5 | 91.856 | 326.685 | 1 |
| current-hls-cold | 10 | 10 | 47.880 | 276.568 | 2 |
| current-hls-warm | 9 | 9 | 0.011 | 0.133 | 0 |
| current-missing-cold | 20 | 0 | 40.920 | 45.168 | 1 |
| current-missing-warm | 9 | 0 | 41.322 | 42.742 | 1 |
| current-youtube-cold | 3 | 0 | 697.368 | 919.370 | 1 |

The fixture browser success rate stayed 20/20; request count halved. The missing-player case stayed 0/20, with a precise failure and one fewer redundant request.
Live YouTube stayed 0/3; a faster failure is not reported as a successful compatibility improvement. Public HLS/DASH discovery passed at both revisions.

## Actual complete transfers / collection check

- Public low-bandwidth Big Buck Bunny HLS: 22,418,060 bytes; current program-aware pipeline completed in ~10.675 s in the recorded run.
  A previous run took ~19.945 s; these transfer runs are not a controlled before/after speed comparison. Both used the existing engine, with increasing structural checks during development.
- Public DASH AAC: 5,278,733 bytes, completed in ~5.172 s; independent probe: 634.354750 s, 14,866 AAC packets; full decode exit 0.
- Independent current FFmpeg probe of HLS: H.264 + AAC, 634.633333 s; full decode exit 0. The earlier static 7.0.2 TS probe/decoder crashed; its empty crash logs are retained and are not considered a validation pass.
- Internet Archive public-domain collection: 12 chapters; metadata ~364.862 ms; enumeration ~387.074 ms; thumbnail bytes ~533.473 ms; exactly two items explicitly queued under a closed download window; no chapter bodies downloaded.

Raw JSONL/probe results and decode logs: `reports/benchmarks/`. No external movie bodies or validation-tool binaries are bundled.

## Reproduce

Build: `dotnet build src/Vesper.MediaResolver.Benchmarks -c Release`

Run with `dotnet run --project src/Vesper.MediaResolver.Benchmarks -c Release --` followed by one of:

- `--fixture browser --iterations 20`
- `--fixture missing --iterations 20`
- `--url https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8 --iterations 10`
- The same command with `--warm` for successful-cache behavior.
- `--url https://dash.akamaized.net/akamai/bbb_30fps/bbb_30fps.mpd --iterations 5`
- `--url https://www.youtube.com/watch?v=aqz-KE-bpKQ --iterations 3`
- `--url https://archive.org/details/adventures_holmes --collection --queue-items 2 --iterations 1 --thumbnail`
- `--url https://test-streams.mux.dev/x36xhzz/url_2/193039199_mp4_h264_aac_ld_7.m3u8 --iterations 1 --download <isolated-folder>`
- `--url https://dash.akamaized.net/akamai/bbb_30fps/bbb_30fps.mpd --iterations 1 --download <isolated-folder>` (audio-only is the implemented complete path)
- `--validate-ts <downloaded-transport-file>` for the C# program/track validator.
- `--cancel-ms 50` to measure discovery cancellation. Regression tests also cover cancellation during bodies and fragment transfers.

`--download` and `--queue-items` are explicit opt-ins. Use only authorized public media. Download state is isolated from the user's normal VESPER database; existing files are never overwritten.
Optional `--system-proxy` delegates connect-time DNS/IP filtering to the configured system proxy, matching the resolver's existing explicit proxy option.
Baseline compilation of this harness uses `-p:Baseline=true -p:VesperSourceRoot=<baseline-worktree>` and excludes the updated bridge/progress APIs.

## Development tools, not dependencies

Temporary tools were outside the source tree: .NET SDK 10.0.404; John Van Sickle FFmpeg/ffprobe 7.0.2 static (GPLv3-enabled build, ~40 MiB compressed download);
BtbN current `N-128728-g49a3b1f259-20261110` GPLv3-enabled build (~149 MiB compressed download).
They were used only for fixture generation/independent validation and are not distributed or invoked by VESPER. No package/runtime dependency was added.

## Extreme performance baseline

Date: 2026-11-13. Baseline `07d6143`; comparison source was HEAD `126465c` plus the then-dirty working-tree index and benchmark changes. The queue index and terminal-entry cleanup are now committed as `dc34329`. Both comparison versions were Release, framework-dependent apphosts using .NET 10.0.15; build/test commands used cached assets with `--no-restore`.

Host: Windows 11 build 26200, AMD Ryzen 5 5600X, 12 logical processors, 15.9 GiB RAM, .NET SDK 11.0.100, x64.

Method: 20 alternating process-start samples; 60-second idle capture per revision; a visible 10,000-row WinForms grid for draw, scroll and three-row progress repaint p95; five alternating throughput runs per revision, each with five 96 MiB hash-checked loopback transfers at one and four connections; five groups of 20 16 KiB direct-transfer samples; 15 samples of adding 2,000 queued jobs; and local HLS/DASH merge plus 600-second fixture remux benchmarks. These are local synthetic workloads, not internet throughput or comparisons with other download managers.

| Measurement | Baseline | Current | Change |
|---|---:|---:|---:|
| Process startup median | 476.28 ms | 473.34 ms | 0.6% faster |
| Idle private memory median | 22.04 MiB | 22.15 MiB | +0.11 MiB |
| Idle CPU over 60.5 s | 0.000% | 0.000% | no measurable CPU use at 0.001% precision |
| Grid draw p95, 10,000 rows | 5.16 ms | 5.44 ms | within the 16.7 ms target |
| Scroll repaint p95 | 4.91 ms | 4.60 ms | 6.3% faster |
| Three-row progress repaint p95 | 1.99 ms | 1.79 ms | 10.1% faster |
| 96 MiB loopback, one connection | 39.55 MiB/s | 38.07 MiB/s | 3.8% lower; gate is 5% |
| 96 MiB loopback, four connections | 78.89 MiB/s | 77.70 MiB/s | 1.5% lower; gate is 5% |
| 16 KiB direct transfer median | 19.42 ms | 19.31 ms | 0.6% faster |
| Add 2,000 queued jobs median | 6,403.74 ms | 5,045.17 ms | 21.2% faster |
| Queue allocations per 2,000 jobs | 1,725.88 MiB | 59.35 MiB | 96.6% lower |

The queue index was the measured improvement. A final review also removed terminal jobs from the index on completion/cancellation and startup recovery, avoiding a second long-lived copy of completed URL keys. The post-cleanup add-only run measured 4,872.93 ms and 59.28 MiB for 2,000 queued jobs, consistent with the full comparison. All startup, idle memory, UI responsiveness, throughput, small-transfer latency and queue gates passed.

Media implementation files were identical at both revisions. The first capture reported MP4 44.83 → 42.02 ms, WebM 65.64 → 62.05 ms, and MKV 62.42 → 83.01 ms; HLS merge 124.64 → 138.55 ms and DASH 127.23 → 132.79 ms. A current-only repeat produced MP4 41.56 ms, WebM 62.16 ms, MKV 62.39 ms, HLS 123.22 ms and DASH 128.67 ms. That repeat matched the baseline closely, including MKV, so the first-run MKV/HLS differences were not treated as code regressions.

Independent output validation used FFmpeg/ffprobe 8.1.1 on files created by VESPER's native remux benchmark. All three fully decoded: MP4, 600.544 s, H.264 128×72 + AAC 48 kHz mono; WebM, 600.348 s, VP9 128×72 + Opus 48 kHz mono; MKV, 600.348 s, H.264 128×72 + Opus 48 kHz mono. The Windows Media Foundation fragmented-AAC conversion also fully decoded as a 1.021 s, 48 kHz mono AAC M4A. These deterministic outputs were temporary and removed after validation; external tools are development-only. The focused index checks, 321 non-benchmark engine tests, 15 Windows tests and all captured baseline/current performance test runs passed. Raw performance logs and JSON are in `release/performance-20261113-175157/`.

## Goal 5 performance follow-up

Date: 2026-11-13, same Windows 11 host and .NET 10.0.15 runtime as the Goal 4 Windows run. The focused `Measure_*` tests used local loopback or checked-in media and ran with `--no-restore`. These measurements were taken before the WebM cover-attachment change; the benchmark media has no cover and does not exercise that branch.

| Measurement | Goal 4 current | Goal 5 follow-up | Change |
|---|---:|---:|---:|
| 96 MiB loopback, one connection median | 38.07 MiB/s | 37.84 MiB/s | 0.6% lower |
| 96 MiB loopback, four connections median | 77.70 MiB/s | 78.63 MiB/s | 1.2% higher |
| 16 KiB direct transfer median | 19.31 ms | 19.27 ms | 0.2% faster |
| Add 2,000 queued jobs median | 5,045 ms | 4,893 ms | 3.0% faster |
| Queue allocations per 2,000 jobs | 59.35 MiB | 59.93 MiB | 1.0% higher |

The measured loopback and small-transfer changes stayed within the existing 5% throughput gate. Local segmented merge medians were 87.86 ms for HLS and 96.13 ms for DASH. Startup, idle use, and visible-grid repaint were not repeated: `scripts/Measure-Performance.ps1` refuses to run while a VESPER process is open, and a pre-existing user-installed instance was left untouched. Direct audio tagging runs only during finalization; WebM attachments are written only when a WebM output has valid source artwork. Neither adds idle work.
