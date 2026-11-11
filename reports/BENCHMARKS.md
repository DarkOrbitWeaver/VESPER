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
