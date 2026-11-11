# Continuation checkpoint

State: the repository is coherent and complete as source (library, tests, fixtures, benchmarks, examples, docs). Nothing has been
compiled or run. The next developer should proceed in this order.

## 1. Make it build
```
dotnet build Vesper.slnx -c Release
```
Riskiest spots for compile errors (written without a compiler):
* `Utilities/JsonExt.cs` - overloads for both `JsonElement` and `JsonElement?` receivers; if overload resolution complains, call `.Value` or add a cast.
* `Extractors/*` - heavy use of `x.P("a").P("b").Str()` chains on `JsonElement?`; pattern forms like `is > 0 and var fps` (rewrite to a local if rejected).
* `Core/SecureHttpClient.cs` - `SocketsHttpHandler.ConnectCallback` exception wrapping, `HttpRequestMessage.Headers.Range`, header APIs.
* `Playlists/CollectionPager.cs` - async iterator with `[EnumeratorCancellation]`.
* `Protocols/Dash/*` - `XmlConvert.ToTimeSpan` formats, tuple-list conversions in `DashTemplate`.
* Tests: raw string literals (`"""`) with XML; `with` on records containing `required` members; xUnit 2.x `Assert.Equal` collection overloads.
* If `Vesper.slnx` is not recognised by your SDK version, create a classic `.sln` with `dotnet new sln` and add the four projects.

## 2. Make the tests pass
`dotnet test Vesper.MediaResolver.Tests -c Release`, then triage. Likely candidates: URL-parsing edge cases (`http://0x7f.0.0.1/`),
`FakeHttpHandler` header recording, dedupe-count expectations in generic page tests, `FormatSelector` ordering expectations in
`FormatSelectorTests` (the intended rules are documented in `FormatSelector.cs` comments - fix the test or the rule, deliberately).

## 3. Verify against the real world (this is what turns NotTested into VerifiedWorking)
1. `VESPER_LIVE_TESTS=1 dotnet test --filter LiveTests` (confirm/replace the four URLs).
2. For each adapter pick one **authorised** public URL, run `Benchmarks -- --live <url>`, compare with yt-dlp via `--compare`.
3. Update fixtures to whatever shape the live site actually returns; add a `CompatibilityRecord` with date and operations; regenerate `COMPATIBILITY.md`.
4. Expect drift and some adapters (TikTok, Bilibili guest limits, YouTube formats) to need changes immediately.

## 4. Highest-value next work (in order)
1. Browser-assisted path end-to-end with the real extension (message schema is final for v1; add the consent UI).
2. SoundCloud, then Twitch clips, Facebook/Instagram/X via browser-supplied candidates; each with fixtures and a live record.
3. YouTube: decide policy for players that need script-derived values (the library intentionally runs no scripts): extension-supplied URLs only.
4. HLS: per-variant playlist probing on demand, `EXT-X-DEFINE`, low-latency; DASH: sidx-aware segmentation, multi-period with identical init.
5. Engine adapter in VESPER: consume `DownloadPlan` (see `docs/INTEGRATION.md`), implement redirect-safe credential scoping.
6. Benchmarks: commit a measured report (synthetic + `--compare` corpus) next to `COMPATIBILITY.md`.

## 5. Things deliberately not done
No DRM handling, no script execution, no bundled muxer, no persistence, no background work, no stealth/anti-bot behaviour, no fabricated results.
