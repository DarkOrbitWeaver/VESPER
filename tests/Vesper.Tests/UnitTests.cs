using System.Net;
using System.Text;
using System.Text.Json;
using Vesper.Engine;

namespace Vesper.Tests;

public class ExtentSetTests
{
    [Fact]
    public void Adding_adjacent_and_overlapping_ranges_merges_them()
    {
        var set = new ExtentSet();
        set.Add(new ByteRange(0, 10));
        set.Add(new ByteRange(20, 30));
        set.Add(new ByteRange(10, 20));
        Assert.Equal("0-30", set.Serialize());
        Assert.Equal(30, set.TotalBytes);
    }

    [Fact]
    public void Gaps_are_the_uncovered_parts()
    {
        var set = new ExtentSet();
        set.Add(new ByteRange(10, 20));
        set.Add(new ByteRange(40, 50));
        Assert.Equal(["0-10", "20-40", "50-100"], set.Gaps(100).Select(g => g.ToString()));
    }

    [Fact]
    public void Remove_splits_a_range()
    {
        var set = new ExtentSet();
        set.Add(new ByteRange(0, 100));
        set.Remove(new ByteRange(40, 60));
        Assert.Equal("0-40,60-100", set.Serialize());
    }

    [Theory]
    [InlineData("5-3")] [InlineData("abc")] [InlineData("-5-10")] [InlineData("1-")]
    public void Parsing_rejects_garbage(string text) => Assert.False(ExtentSet.TryParse(text, out _));

    [Fact]
    public void Random_operations_always_match_a_bitmap_model()
    {
        // Property-style check: the set must agree with a plain bit array after any sequence of adds/removes.
        var random = new Random(42);
        for (int round = 0; round < 200; round++)
        {
            var set = new ExtentSet();
            var model = new bool[500];
            for (int op = 0; op < 30; op++)
            {
                int a = random.Next(0, 500), b = random.Next(a, 501);
                bool add = random.Next(3) != 0;
                if (add) set.Add(new ByteRange(a, b)); else set.Remove(new ByteRange(a, b));
                for (int i = a; i < b; i++) model[i] = add;
            }
            Assert.Equal(model.Count(x => x), set.TotalBytes);
            long previousEnd = -1;
            foreach (ByteRange r in set.Ranges)
            {
                Assert.True(r.Start > previousEnd, "ranges must be sorted and non-adjacent");
                for (long i = r.Start; i < r.End; i++) Assert.True(model[i]);
                previousEnd = r.End;
            }
            Assert.True(ExtentSet.TryParse(set.Serialize(), out ExtentSet again));
            Assert.Equal(set.Serialize(), again.Serialize());
        }
    }
}

public class FileNameTests
{
    [Theory]
    [InlineData("..\\..\\evil.txt", "evil.txt")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\system32\\calc.exe", "calc.exe")]
    [InlineData("report.pdf:hidden", "report.pdf_hidden")]
    [InlineData("trailing. . .", "trailing")]
    public void Names_never_contain_directories_or_streams(string raw, string expected) =>
        Assert.Equal(expected, FileNames.Sanitize(raw));

    [Theory]
    [InlineData("CON")] [InlineData("nul.txt")] [InlineData("COM1.zip")]
    public void Reserved_device_names_are_defused(string raw) => Assert.StartsWith("_", FileNames.Sanitize(raw));

    [Theory]
    [InlineData("")] [InlineData("..")] [InlineData("...")] [InlineData("   ")]
    public void Empty_or_dot_names_fall_back(string raw) => Assert.Equal("download", FileNames.Sanitize(raw));

    [Fact]
    public void Very_long_names_keep_their_extension()
    {
        string name = FileNames.Sanitize(new string('a', 400) + ".mkv");
        Assert.True(name.Length <= 150);
        Assert.EndsWith(".mkv", name);
    }

    [Fact]
    public void Unique_path_never_returns_an_existing_name()
    {
        var taken = new HashSet<string> { Path.Combine("d", "a.zip"), Path.Combine("d", "a (1).zip") };
        Assert.Equal(Path.Combine("d", "a (2).zip"), FileNames.UniquePath("d", "a.zip", taken.Contains));
    }
}

public class UrlPolicyTests
{
    [Theory]
    [InlineData("file:///C:/secret.txt")] [InlineData("ftp://host/file")] [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@host/file")] [InlineData("not a url")] [InlineData("")]
    public void Unsafe_or_unsupported_links_are_rejected(string url) => Assert.False(UrlPolicy.TryParse(url, out _, out _));

    [Theory]
    [InlineData("127.0.0.1", false)] [InlineData("10.1.2.3", false)] [InlineData("192.168.0.9", false)] [InlineData("172.20.0.1", false)]
    [InlineData("169.254.1.1", false)] [InlineData("100.64.0.1", false)] [InlineData("::1", false)] [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)] [InlineData("::ffff:10.0.0.1", false)] [InlineData("8.8.8.8", true)] [InlineData("2606:4700:4700::1111", true)]
    public void Address_classification(string address, bool isPublic) =>
        Assert.Equal(isPublic, UrlPolicy.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public void Private_targets_are_blocked_unless_allowed()
    {
        Assert.Throws<BlockedAddressException>(() => UrlPolicy.EnsureAllowed(new Uri("http://localhost/x"), false));
        Assert.Throws<BlockedAddressException>(() => UrlPolicy.EnsureAllowed(new Uri("http://192.168.1.1/x"), false));
        UrlPolicy.EnsureAllowed(new Uri("http://192.168.1.1/x"), true);
    }

    [Fact]
    public void Redaction_drops_query_strings()
    {
        string redacted = UrlPolicy.Redact("https://cdn.example.com/a/b.zip?token=SECRET&sig=abc");
        Assert.DoesNotContain("SECRET", redacted);
        Assert.Contains("cdn.example.com/a/b.zip", redacted);
    }
}

public class PacingTests
{
    [Fact]
    public async Task Rate_limiter_enforces_the_configured_speed()
    {
        var limiter = new RateLimiter();
        limiter.SetRate(200_000);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10; i++) await limiter.AcquireAsync(50_000, CancellationToken.None);   // 500 kB
        Assert.InRange(watch.Elapsed.TotalSeconds, 2.0, 3.5);   // ≈2.5 s at 200 kB/s, minus 250 ms burst credit
    }

    [Fact]
    public async Task Unlimited_rate_does_not_wait()
    {
        var limiter = new RateLimiter();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) await limiter.AcquireAsync(1_000_000, CancellationToken.None);
        Assert.True(watch.Elapsed.TotalMilliseconds < 500);
    }

    [Fact]
    public void Backoff_grows_is_capped_and_honours_retry_after()
    {
        var policy = new RetryPolicy(new RetryOptions { BaseDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(30), MaxAttempts = 5 }, new Random(1));
        Assert.InRange(policy.Delay(0).TotalSeconds, 0.5, 1.0);
        Assert.InRange(policy.Delay(3).TotalSeconds, 4.0, 8.0);
        Assert.InRange(policy.Delay(15).TotalSeconds, 15.0, 30.0);
        Assert.True(policy.Delay(0, TimeSpan.FromSeconds(90)).TotalSeconds >= 90);
        Assert.True(policy.ShouldGiveUp(5));
        Assert.False(policy.ShouldGiveUp(4));
    }

    [Fact]
    public void Advisor_ramps_up_while_it_helps_and_stops_when_it_does_not()
    {
        var advisor = new ConnectionAdvisor(6);
        TimeSpan ramp = TimeSpan.FromSeconds(3), enough = TimeSpan.FromSeconds(4);
        int n = advisor.Recommend(1, 1_000_000, enough, ramp);
        Assert.Equal(2, n);
        n = advisor.Recommend(n, 1_800_000, enough, ramp);
        Assert.Equal(3, n);
        n = advisor.Recommend(n, 1_850_000, enough, ramp);       // < 15 % gain: plateau
        Assert.Equal(3, n);
        Assert.Equal(3, advisor.Recommend(n, 1_900_000, enough, ramp));
        Assert.Equal(1, advisor.OnThrottled(2));
    }

    [Fact]
    public void Advisor_never_changes_faster_than_the_ramp_interval() =>
        Assert.Equal(1, new ConnectionAdvisor(8).Recommend(1, 5_000_000, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)));

    [Fact]
    public void Schedule_window_wraps_midnight()
    {
        var window = new SchedulePolicy(true, new TimeOnly(23, 0), new TimeOnly(6, 0));
        Assert.True(window.IsOpen(new DateTime(2025, 1, 1, 23, 30, 0)));
        Assert.True(window.IsOpen(new DateTime(2025, 1, 2, 5, 59, 0)));
        Assert.False(window.IsOpen(new DateTime(2025, 1, 2, 12, 0, 0)));
        Assert.Equal(new DateTime(2025, 1, 2, 23, 0, 0), window.NextBoundary(new DateTime(2025, 1, 2, 12, 0, 0)));
    }
}

public class SegmentBoardTests
{
    [Fact]
    public void Splitting_never_hands_out_overlapping_bytes()
    {
        var board = new SegmentBoard(minSplitBytes: 128 * 1024);
        board.Add(new Segment(0, 10_000_000));
        Segment first = board.Acquire()!;
        Assert.Equal(0, first.Start);
        board.ClaimChunk(first, 65536);          // owner is about to write the first 64 KiB

        Segment second = board.Acquire()!;
        Assert.True(second.Start >= 65536, "a split must not cut below bytes already claimed");
        Assert.Equal(second.Start, first.End);
        Assert.Equal(10_000_000, second.End);
        Assert.Equal(0, second.Start % 65536);
    }

    [Fact]
    public void Small_remainders_are_not_split()
    {
        var board = new SegmentBoard(minSplitBytes: 1_000_000);
        board.Add(new Segment(0, 1_500_000));
        Assert.NotNull(board.Acquire());
        Assert.Null(board.Acquire());
    }
}

public class RescueEvaluatorTests
{
    private static DownloadJob Job(IdentityLevel level = IdentityLevel.StrongEtag, string? etag = "\"p1\"") => new()
    {
        SourceUrl = "https://cdn.example.com/f/file.iso?token=old", TotalBytes = 1000, Identity = level, ETag = etag,
        LastModified = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    };
    private static readonly Uri New = new("https://cdn.example.com/f/file.iso?token=new");
    private static ProbeFacts Probe(string? etag = "\"p1\"", long total = 1000, bool rangeOk = true, DateTimeOffset? lm = null) =>
        new(rangeOk ? 206 : 200, rangeOk, total, etag, lm, true);

    [Fact] public void R0_matching_strong_etag_is_accepted() =>
        Assert.Equal(RescueMode.R0, RescueEvaluator.Evaluate(Job(), New, Probe(), null, null).Mode);

    [Theory]
    [InlineData("https://mirror.example.net/f/file.iso?token=new")]
    [InlineData("https://cdn.example.com/f/other.iso?token=new")]
    [InlineData("http://cdn.example.com/f/file.iso?token=new")]
    public void Matching_etag_does_not_authorize_reuse_from_another_resource(string url)
    {
        RescueDecision result = RescueEvaluator.Evaluate(Job(), new Uri(url), Probe(), null, null);
        Assert.False(result.Accepted);
        Assert.Equal(RescueMode.R4, result.Mode);
    }

    [Fact] public void Different_etag_without_trusted_hash_is_rejected()
    {
        RescueDecision d = RescueEvaluator.Evaluate(Job(), New, Probe("\"other\""), null, null);
        Assert.False(d.Accepted);
        Assert.Equal(RescueMode.R4, d.Mode);
    }

    [Fact] public void Different_etag_with_trusted_hash_becomes_candidate_recovery()
    {
        RescueDecision d = RescueEvaluator.Evaluate(Job(), New, Probe("\"other\""), new string('a', 64), null);
        Assert.True(d.Accepted);
        Assert.Equal(RescueMode.R1, d.Mode);
    }

    [Fact] public void Last_modified_identity_needs_same_resource_and_time()
    {
        var lm = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DownloadJob job = Job(IdentityLevel.LastModified, null);
        Assert.Equal(RescueMode.R0, RescueEvaluator.Evaluate(job, New, Probe(null, lm: lm), null, null).Mode);
        Assert.False(RescueEvaluator.Evaluate(job, new Uri("https://mirror.example.net/other.iso"), Probe(null, lm: lm), null, null).Accepted);
        Assert.False(RescueEvaluator.Evaluate(job, New, Probe(null, lm: lm.AddHours(1)), null, null).Accepted);
    }

    [Fact] public void Size_mismatch_is_always_rejected_even_with_a_hash() =>
        Assert.False(RescueEvaluator.Evaluate(Job(), New, Probe(total: 999), new string('a', 64), null).Accepted);

    [Fact] public void A_link_without_range_support_is_rejected() =>
        Assert.False(RescueEvaluator.Evaluate(Job(), New, Probe(rangeOk: false), null, null).Accepted);

    [Fact] public void Trusted_piece_hashes_select_R1b()
    {
        var map = new PieceMap(16384, 1000, [new string('a', 64)]);
        Assert.Equal(RescueMode.R1b, RescueEvaluator.Evaluate(Job(IdentityLevel.None, null), New, Probe(null), null, map).Mode);
    }
}

public class PieceMapTests
{
    [Fact]
    public void Valid_json_round_trips()
    {
        string json = JsonSerializer.Serialize(new { pieceLength = 16384, totalLength = 20000, sha256 = new[] { new string('a', 64), new string('B', 64) } });
        Assert.True(PieceMap.TryParse(json, out PieceMap map, out _));
        Assert.Equal(2, map.PieceCount);
        Assert.Equal(new ByteRange(16384, 20000), map.RangeOf(1));
        Assert.True(PieceMap.TryParse(map.ToJson(), out _, out _));
    }

    [Theory]
    [InlineData("{}")] [InlineData("not json")] [InlineData("{\"pieceLength\":100,\"totalLength\":1000,\"sha256\":[]}")]
    public void Invalid_maps_are_rejected(string json) => Assert.False(PieceMap.TryParse(json, out _, out _));
}

public class BridgeProtocolTests
{
    private static string Msg(string body) => "{\"v\":1,\"id\":\"req-12345678\"," + body + "}";

    [Fact] public void Hello_is_accepted() => Assert.True(BridgeProtocol.TryParse(Msg("\"type\":\"hello\""), out BridgeRequest r, out _) && r.Type == "hello");

    [Theory]
    [InlineData("{\"id\":\"req-12345678\",\"type\":\"hello\"}")]                                  // no version
    [InlineData("{\"v\":2,\"id\":\"req-12345678\",\"type\":\"hello\"}")]                          // wrong version
    [InlineData("{\"v\":1,\"id\":\"x\",\"type\":\"hello\"}")]                                     // bad id
    [InlineData("{\"v\":1,\"id\":\"req-12345678\",\"type\":\"format-disk\"}")]                    // unknown type
    [InlineData("[]")] [InlineData("garbage")]
    public void Malformed_requests_are_rejected(string json) => Assert.False(BridgeProtocol.TryParse(json, out _, out _));

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")] [InlineData("javascript:alert(1)")] [InlineData("ftp://x/y")]
    public void Submit_refuses_non_http_urls(string url) =>
        Assert.False(BridgeProtocol.TryParse(Msg($"\"type\":\"submit\",\"items\":[{{\"url\":\"{url}\"}}]"), out _, out _));

    [Fact]
    public void Browser_handoff_parses_the_response_proof_and_always_waits_for_confirmation()
    {
        string request = Msg("""
            "type":"browser-submit","mode":"download","items":[{"url":"https://example.com/archive"}],
            "browserProof":{"length":200,"etag":"\"p1\"","contentType":"application/zip","awaitingConfirmation":false}
            """);
        Assert.True(BridgeProtocol.TryParse(request, out BridgeRequest parsed, out string error), error);
        Assert.True(parsed.ConfirmTransfer);
        Assert.Equal(new BrowserTransferProof(200, "\"p1\"", "application/zip"), parsed.BrowserProof);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"length\":-1}")]
    [InlineData("{\"etag\":\"W/\\\"p1\\\"\"}")]
    public void Browser_handoff_rejects_an_invalid_response_proof(string proof)
    {
        string request = Msg($$"""
            "type":"browser-submit","items":[{"url":"https://example.com/archive"}],"browserProof":{{proof}}
            """);
        Assert.False(BridgeProtocol.TryParse(request, out _, out _));
    }

    [Fact]
    public void Browser_media_picker_accepts_bounded_hints_without_paths_or_credentials()
    {
        string request = Msg("\"type\":\"media-picker\",\"url\":\"https://video.example.com/watch/123\"," +
            "\"pageTitle\":\"Sample clip\",\"candidates\":[{" +
            "\"url\":\"https://cdn.example.com/clip.m3u8\",\"kind\":\"hls\",\"mimeType\":\"application/vnd.apple.mpegurl\"}]");
        Assert.True(BridgeProtocol.TryParse(request, out BridgeRequest parsed, out string why), why);
        Assert.Equal("media-picker", parsed.Type);
        Assert.Equal("Sample clip", parsed.PageTitle);
        Assert.Empty(parsed.Items);
        Assert.Equal("hls", Assert.Single(parsed.Candidates!).Kind);
        Assert.DoesNotContain(typeof(BridgeRequest).GetProperties(), p => p.Name.Contains("Destination", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"url\":\"file:///C:/secrets\"}]")]
    [InlineData("[{\"url\":\"https://example.com/a\",\"kind\":\"execute\"}]")]
    public void Browser_media_picker_rejects_malformed_or_dangerous_hints(string hints)
    {
        string request = Msg("\"type\":\"media-picker\",\"url\":\"https://example.com/watch\",\"candidates\":" + hints);
        Assert.False(BridgeProtocol.TryParse(request, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("queue", 2, true)]
    [InlineData("download", 63, true)]
    [InlineData("ask", 2, false)]
    [InlineData("queue", 64, false)]
    public void Browser_media_selection_accepts_only_a_session_and_valid_choice(string mode, int index, bool valid)
    {
        const string session = "edc1604be3d34136a798fca8addbcbcd";
        string request = Msg($$"""
            "type":"media-select","mode":"{{mode}}","mediaSession":"{{session}}","formatChoice":{{index}},
            "url":"https://example.com/unrelated"
            """);
        Assert.Equal(valid, BridgeProtocol.TryParse(request, out BridgeRequest parsed, out _));
        if (!valid) return;
        Assert.Equal(Guid.ParseExact(session, "N"), parsed.MediaSession);
        Assert.Equal(index, parsed.FormatChoice);
        Assert.Null(parsed.Url);
        Assert.Empty(parsed.Items);
    }

    [Fact]
    public void The_browser_cannot_choose_a_destination_or_a_program()
    {
        string json = Msg("\"type\":\"submit\",\"destination\":\"C:\\\\Windows\\\\System32\",\"run\":\"cmd.exe\"," +
            "\"items\":[{\"url\":\"https://example.com/a.zip\",\"destination\":\"C:\\\\evil\",\"exec\":\"calc\"}]");
        Assert.True(BridgeProtocol.TryParse(json, out BridgeRequest request, out _));
        BridgeItem item = Assert.Single(request.Items);
        Assert.Equal("https://example.com/a.zip", item.Url);
        Assert.DoesNotContain(typeof(BridgeItem).GetProperties(), p => p.Name.Contains("Destination", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Credentials_are_only_taken_with_explicit_consent_and_never_with_line_breaks()
    {
        string item = "\"items\":[{\"url\":\"https://example.com/a.zip\",\"cookie\":\"a=b\"}]";
        Assert.True(BridgeProtocol.TryParse(Msg("\"type\":\"submit\"," + item), out BridgeRequest plain, out _));
        Assert.Null(plain.Items[0].Cookie);

        Assert.True(BridgeProtocol.TryParse(Msg("\"type\":\"submit\",\"authorized\":true," + item), out BridgeRequest authorized, out _));
        Assert.Equal("a=b", authorized.Items[0].Cookie);

        string injected = "\"items\":[{\"url\":\"https://example.com/a.zip\",\"referer\":\"https://x/\\r\\nX-Evil: 1\"}]";
        Assert.True(BridgeProtocol.TryParse(Msg("\"type\":\"submit\"," + injected), out BridgeRequest safe, out _));
        Assert.Null(safe.Items[0].Referer);
    }

    [Fact]
    public void Item_names_are_sanitized()
    {
        Assert.True(BridgeProtocol.TryParse(Msg("\"type\":\"submit\",\"items\":[{\"url\":\"https://e.com/a\",\"fileName\":\"..\\\\..\\\\x.exe\"}]"), out BridgeRequest r, out _));
        Assert.Equal("x.exe", r.Items[0].FileName);
    }

    [Fact]
    public async Task Frames_round_trip_and_oversized_frames_are_refused()
    {
        using var stream = new MemoryStream();
        await BridgeProtocol.WriteFrameAsync(stream, "{\"v\":1}", CancellationToken.None);
        stream.Position = 0;
        Assert.Equal("{\"v\":1}", await BridgeProtocol.ReadFrameAsync(stream, CancellationToken.None));

        using var hostile = new MemoryStream(BitConverter.GetBytes(BridgeProtocol.MaxMessageBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => BridgeProtocol.ReadFrameAsync(hostile, CancellationToken.None));
        using var empty = new MemoryStream();
        Assert.Null(await BridgeProtocol.ReadFrameAsync(empty, CancellationToken.None));
    }

    [Fact]
    public async Task Many_simultaneous_browser_requests_are_all_answered()
    {
        string pipe = "vesper-test-" + Guid.NewGuid().ToString("N")[..8];
        using var server = new BridgeServer(pipe, async (request, ct) => { await Task.Delay(300, ct); return BridgeResponse.Success(request.Id, "hi"); });
        server.Start();
        string[] replies = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => BridgePipe.RequestAsync(
            pipe, Msg("\"type\":\"hello\"").Replace("req-12345678", $"req-{i:D8}"), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), CancellationToken.None)));
        Assert.All(replies, r => Assert.Contains("\"ok\":true", r));
    }

    [Fact]
    public async Task The_pipe_server_answers_validated_requests_and_rejects_bad_ones()
    {
        string pipe = "vesper-test-" + Guid.NewGuid().ToString("N")[..8];
        using var server = new BridgeServer(pipe, (request, _) => Task.FromResult(BridgeResponse.Success(request.Id, "hi")));
        server.Start();

        string good = await BridgePipe.RequestAsync(pipe, Msg("\"type\":\"hello\""), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Contains("\"ok\":true", good);
        string bad = await BridgePipe.RequestAsync(pipe, "{\"v\":9}", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Contains("\"code\":\"invalid\"", bad);
    }
}

public class HlsParserTests
{
    private static readonly Uri Base = new("https://cdn.example.com/video/master.m3u8");

    [Fact]
    public void Master_playlist_lists_variants_and_flags_separate_audio()
    {
        const string text = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="en",URI="audio/en.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720,CODECS="avc1.4d401f,mp4a.40.2",AUDIO="aud"
            720/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=640x360,CODECS="avc1.4d401e,mp4a.40.2"
            360/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=128000,CODECS="mp4a.40.2"
            audio/index.m3u8
            """;
        var variants = HlsParser.ParseMaster(text, Base);
        Assert.Equal(3, variants.Count);
        Assert.True(variants[0].NeedsSeparateAudio);
        Assert.False(variants[1].NeedsSeparateAudio);
        Assert.True(variants[2].AudioOnly);
        Assert.Equal("https://cdn.example.com/video/360/index.m3u8", variants[1].Uri);
    }

    [Fact]
    public void Clear_key_AES128_is_supported_but_sample_aes_is_refused()
    {
        const string clear = "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k.bin\"\n#EXTINF:4,\ns1.ts\n#EXT-X-ENDLIST\n";
        HlsMedia ok = HlsParser.ParseMedia(clear, Base);
        Assert.Null(ok.Unsupported);
        Assert.NotNull(ok.Segments[0].Key);
        Assert.True(ok.IsVod);

        const string drm = "#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://x\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n#EXTINF:4,\ns1.ts\n#EXT-X-ENDLIST\n";
        Assert.NotNull(HlsParser.ParseMedia(drm, Base).Unsupported);
    }

    [Fact]
    public void Live_playlists_are_not_vod_and_fmp4_init_segments_are_tracked()
    {
        Assert.False(HlsParser.ParseMedia("#EXTM3U\n#EXTINF:4,\ns1.ts\n", Base).IsVod);
        HlsMedia media = HlsParser.ParseMedia("#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:4,\ns1.m4s\n#EXT-X-ENDLIST\n", Base);
        Assert.Equal("https://cdn.example.com/video/init.mp4", media.Segments[0].Map!.Uri);
    }
}

public class JobStoreTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), "vesper-db-" + Guid.NewGuid().ToString("N")[..8], "t.db");

    [Fact]
    public void Jobs_round_trip_including_extents_and_identity()
    {
        string path = TempDb();
        using (JobStore store = JobStore.Open(path))
        {
            var job = new DownloadJob { SourceUrl = "https://e.com/a", FileName = "a.zip", DestinationFolder = "C:\\d", PartialPath = "C:\\d\\a.vpart", ETag = "\"x\"", Identity = IdentityLevel.StrongEtag, TotalBytes = 100 };
            job.Extents.Add(new ByteRange(0, 40));
            store.Save(job);
            DownloadJob loaded = Assert.Single(store.LoadAll());
            Assert.Equal(job.Id, loaded.Id);
            Assert.Equal("0-40", loaded.Extents.Serialize());
            Assert.Equal(IdentityLevel.StrongEtag, loaded.Identity);
        }
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void A_damaged_database_is_quarantined_not_deleted()
    {
        string path = TempDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("this is definitely not an sqlite database, just noise " + new string('x', 5000)));

        using JobStore store = JobStore.Open(path);
        Assert.True(store.RecoveredFromCorruption);
        Assert.True(File.Exists(store.CorruptBackupPath));
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void A_database_from_a_newer_version_is_left_untouched()
    {
        string path = TempDb();
        using (JobStore.Open(path)) { }
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 999;";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<StoreTooNewException>(() => JobStore.Open(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*"));
    }

    [Fact]
    public void Submissions_are_idempotent()
    {
        string path = TempDb();
        using JobStore store = JobStore.Open(path);
        Guid id = Guid.NewGuid();
        store.RecordSubmission("req-abc", id);
        store.RecordSubmission("req-abc", Guid.NewGuid());
        Assert.True(store.TryGetSubmission("req-abc", out Guid found));
        Assert.Equal(id, found);
    }
}

public class MediaPageLinkTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=EK3tCffac5k&list=RDEK3tCffac5k&start_radio=1&pp=oAcB", "YouTube")]
    [InlineData("https://www.youtube.com/playlist?list=PL123456789", "YouTube")]
    [InlineData("https://www.dailymotion.com/video/x123456", "Dailymotion")]
    [InlineData("https://www.facebook.com/watch/?v=123456", "Facebook")]
    [InlineData("https://x.com/example/status/123456789", "X")]
    [InlineData("https://www.reddit.com/r/example/comments/abc123/title/", "Reddit")]
    [InlineData("https://www.bilibili.com/video/BV123456789", "Bilibili")]
    [InlineData("https://www.twitch.tv/videos/12345678", "Twitch")]
    [InlineData("https://archive.org/details/examplevideo", "Internet Archive")]
    [InlineData("https://artist.bandcamp.com/track/example", "Bandcamp")]
    [InlineData("https://youtu.be/EK3tCffac5k", "YouTube")]
    [InlineData("https://www.youtube.com/shorts/123", "YouTube")]
    [InlineData("https://www.tiktok.com/@creator/video/123456", "TikTok")]
    [InlineData("https://www.instagram.com/reel/abc123/", "Instagram")]
    [InlineData("https://vimeo.com/12345", "Vimeo")]
    public void Known_video_pages_are_not_direct_file_links(string address, string site)
    {
        Assert.True(MediaPageLinks.TryGetSite(new Uri(address), out string actual));
        Assert.Equal(site, actual);
    }

    [Fact]
    public void Engine_rejects_a_video_watch_page_before_creating_a_job()
    {
        using var h = new Harness();
        AddResult result = h.Engine.Add(new AddRequest("https://www.youtube.com/watch?v=EK3tCffac5k&list=RDEK3tCffac5k&start_radio=1&pp=oAcB"));
        Assert.False(result.Ok);
        Assert.Contains("video page", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Engine.Snapshots());
    }

    [Fact]
    public async Task Rescue_rejects_a_video_page_without_changing_the_existing_link()
    {
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        Guid id = h.Add("https://download.example.com/file.zip", "file.zip", startNow: false);
        RescueResult result = await h.Engine.ReplaceLinkAsync(id, "https://www.youtube.com/watch?v=EK3tCffac5k");
        Assert.False(result.Accepted);
        Assert.Equal("https://download.example.com/file.zip", h.Engine.Snapshot(id)!.SourceUrl);
    }

    [Theory]
    [InlineData("https://cdn.example.com/movie.mp4")]
    [InlineData("https://download.example.com/download?id=1")]
    [InlineData("https://www.youtube.com/assets/video.mp4")]
    [InlineData("https://notyoutube.com/watch?v=abc")]
    [InlineData("https://www.youtube.com/")]
    public void Ordinary_and_direct_file_links_are_not_mistaken_for_video_pages(string address) =>
        Assert.False(MediaPageLinks.TryGetSite(new Uri(address), out _));
}
