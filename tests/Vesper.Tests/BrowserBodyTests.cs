using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Vesper.Engine;

namespace Vesper.Tests;

public sealed class BrowserBodyTests
{
    private const string Transfer = "body-copy-fixture-001";
    private const string Origin = "https://example.test/";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static Task<BridgeResponse> Send(Harness h, BrowserBodyRequest request) =>
        h.Engine.ReceiveBrowserBodyAsync("test-request-001", request, default);

    private static BrowserBodyRequest Begin(long size, string name = "clip.mp4") =>
        new("begin", Transfer, Origin, name, size);
    private static BrowserBodyRequest Append(long offset, byte[] bytes) =>
        new("append", Transfer, Offset: offset, Bytes: bytes, Digest: Hash(bytes));
    private static BrowserBodyRequest Finish() => new("finish", Transfer);

    private static Guid AcceptedId(BridgeResponse reply)
    {
        Assert.True(reply.Ok, reply.Message);
        JsonElement data = JsonSerializer.SerializeToElement(reply.Data, BridgeProtocol.Json);
        Assert.True(data.GetProperty("accepted").GetBoolean());
        return data.GetProperty("jobId").GetGuid();
    }

    [Fact]
    public void ProtocolRejectsPathsCredentialsOversizedAndMalformedChunks()
    {
        const string begin = """
            {"v":2,"id":"test-request","type":"browser-body","operation":"begin","transferId":"copy-123456",
             "origin":"https://example.test/","fileName":"clip.mp4","length":123}
            """;
        Assert.True(BridgeProtocol.TryParse(begin, out var request, out var error), error);
        Assert.Equal(123, request.BrowserBody!.Length);
        Assert.True(BridgeProtocol.TryParse(begin.Replace("123}", "123,\"mediaCheck\":\"webm\"}"), out var webm, out error), error);
        Assert.True(webm.BrowserBody!.VerifyWebm);
        Assert.False(webm.BrowserBody.VerifyMp4);
        Assert.False(BridgeProtocol.TryParse(begin.Replace("123}", "123,\"mediaCheck\":\"unknown\"}"), out _, out _));
        Assert.False(BridgeProtocol.TryParse(begin.Replace("clip.mp4", "../clip.mp4"), out _, out _));
        Assert.False(BridgeProtocol.TryParse(begin.Replace("https://example.test/", "https://example.test/private?token=x"), out _, out _));
        Assert.False(BridgeProtocol.TryParse(begin.Replace("https://example.test/", "https://u:p@example.test/"), out _, out _));
        Assert.True(BridgeProtocol.TryParse(begin.Replace("123}", "1073741825}"), out _, out _));
        Assert.False(BridgeProtocol.TryParse(begin.Replace("123}", "17179869185}"), out _, out _));
        byte[] bytes = [1, 2, 3];
        string append = JsonSerializer.Serialize(new { v = BridgeProtocol.Version, id = "test-request", type = "browser-body", operation = "append",
            transferId = Transfer, offset = 0, chunk = Convert.ToBase64String(bytes), digest = Hash(bytes) });
        Assert.True(BridgeProtocol.TryParse(append, out request, out error), error);
        Assert.Equal(bytes, request.BrowserBody!.Bytes);
        Assert.False(BridgeProtocol.TryParse(append.Replace("AQID", "!!!!"), out _, out _));
        Assert.False(BridgeProtocol.TryParse(append.Replace("\"offset\":0", "\"offset\":-1"), out _, out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BrowserTracksAreLengthCheckedAndRemuxedFromStaging(bool repeatedFragment, bool conflictingBytes)
    {
        using var h = new Harness();
        byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", name));
        byte[] video = Fixture("video-only.mp4");
        byte[] init = Fixture("audio-init.mp4");
        byte[] fragment = Fixture("audio-1.m4s");
        byte[] audio = repeatedFragment
            ? [.. WithOpenEndedEdit(init, 1024), .. fragment, .. fragment]
            : [.. init, .. fragment];
        if (conflictingBytes) audio[^1] ^= 1;
        var opened = await Send(h, new("begin", Transfer, Origin, "clip.mp4", null,
            Tracks: [new("video", "mp4", video.Length), new("audio", "m4a", audio.Length)], OutputContainer: "mp4"));
        Assert.True(opened.Ok, opened.Message);
        Assert.False((await Send(h, Append(0, audio) with { TrackIndex = 1 })).Ok);
        Assert.True((await Send(h, Append(0, video))).Ok);
        Assert.False((await Send(h, Finish())).Ok);
        Assert.True((await Send(h, Append(video.Length, audio) with { TrackIndex = 1 })).Ok);
        Guid id = AcceptedId(await Send(h, Finish()));
        JobSnapshot completed = await h.WaitForStateAsync(id, conflictingBytes ? JobState.NeedsAttention : JobState.Completed);
        Assert.Equal(JobKind.Merge, completed.Kind);
        if (conflictingBytes)
        {
            Assert.False(File.Exists(completed.FinalPath));
            Assert.Equal(video.Length + audio.Length, new FileInfo(completed.PartialPath!).Length);
            return;
        }
        Assert.EndsWith(".mp4", completed.FinalPath);
        using (var staged = PartialFile.Open(completed.FinalPath!, create: false))
            MediaTrackProbe.RequireMp4Media(staged, default);
        if (repeatedFragment)
        {
            using var original = new MemoryStream([.. init, .. fragment]);
            DemuxedTrack expected = Assert.Single(Mp4Demuxer.Read(original, original.Length, default));
            using var output = File.OpenRead(completed.FinalPath!);
            DemuxedTrack track = Mp4Demuxer.Read(output, output.Length, default).Single(track => track.Kind == TrackKind.Audio);
            Assert.Equal(expected.Samples.Count, track.Samples.Count);
            Mp4EditListEntry edit = Assert.Single(track.EditList!);
            Assert.Equal(1024, edit.MediaTime);
            Assert.Equal((Int128)(expected.DurationTicks - 1024) * track.EditMovieTimescale,
                (Int128)edit.SegmentDuration * expected.Timescale);
        }
    }

    private static byte[] WithOpenEndedEdit(byte[] init, uint mediaTime)
    {
        // Insert the edit into the checked-in AAC initialization segment before mvex.
        int moov = init.AsSpan().IndexOf("moov"u8) - 4;
        int trak = init.AsSpan().IndexOf("trak"u8) - 4;
        int trackLength = BinaryPrimitives.ReadInt32BigEndian(init.AsSpan(trak));
        var writer = new BoxWriter();
        int edts = writer.Begin("edts");
        int elst = writer.BeginFull("elst", 0, 0);
        writer.U32(1);
        writer.U32(0);
        writer.U32(mediaTime);
        writer.U16(1);
        writer.U16(0);
        writer.End(elst);
        writer.End(edts);
        byte[] edit = writer.ToArray();
        int end = trak + trackLength;
        byte[] result = [.. init.AsSpan(0, end), .. edit, .. init.AsSpan(end)];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(moov), BinaryPrimitives.ReadInt32BigEndian(init.AsSpan(moov)) + edit.Length);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(trak), trackLength + edit.Length);
        return result;
    }

    [Fact]
    public async Task SingleCapturedMp4TrackIsRemuxedWithoutRepeatedFragments()
    {
        using var h = new Harness();
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media");
        byte[] init = File.ReadAllBytes(Path.Combine(fixtures, "audio-init.mp4"));
        byte[] fragment = File.ReadAllBytes(Path.Combine(fixtures, "audio-1.m4s"));
        byte[] audio = [.. init, .. fragment, 0, 0, 0, 8, (byte)'f', (byte)'r', (byte)'e', (byte)'e', .. fragment];
        Assert.True((await Send(h, new("begin", Transfer, Origin, "clip.m4a", audio.Length,
            VerifyMp4: true, Tracks: [new("audio", "m4a", audio.Length)], OutputContainer: "m4a"))).Ok);
        Assert.True((await Send(h, Append(0, audio))).Ok);
        Guid id = AcceptedId(await Send(h, Finish()));
        JobSnapshot completed = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(JobKind.Merge, completed.Kind);
        using var original = new MemoryStream([.. init, .. fragment]);
        using var output = File.OpenRead(completed.FinalPath!);
        var expected = Assert.Single(Mp4Demuxer.Read(original, original.Length, default));
        var actual = Assert.Single(Mp4Demuxer.Read(output, output.Length, default));
        Assert.Equal(expected.Samples.Count, actual.Samples.Count);
        Assert.Equal(expected.DurationTicks, actual.DurationTicks);
        var receipt = await Send(h, new("status", Transfer, Origin));
        Assert.Equal(id, AcceptedId(receipt));
        Assert.Equal(audio.Length, JsonSerializer.SerializeToElement(receipt.Data, BridgeProtocol.Json).GetProperty("offset").GetInt64());
    }

    [Theory]
    [InlineData("muxed.webm", "muxed", false)]
    [InlineData("video-only.webm", "video", false)]
    [InlineData("quiet-track.webm", "audio", false)]
    [InlineData("muxed.webm", "muxed", true)]
    public async Task RetainedWebmUsesVerifiedRemuxAndNeverPublishesMalformedBytes(string fixture, string role, bool truncate)
    {
        using var h = new Harness();
        byte[] original = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", fixture));
        byte[] bytes = truncate ? original[..(original.Length / 2)] : original;
        Assert.True((await Send(h, new("begin", Transfer, Origin, "clip.webm", bytes.Length,
            Tracks: [new(role, "webm", bytes.Length)], OutputContainer: "webm"))).Ok);
        for (int offset = 0; offset < bytes.Length; offset += BrowserBodyReceiver.ChunkBytes)
            Assert.True((await Send(h, Append(offset, bytes.AsSpan(offset, Math.Min(BrowserBodyReceiver.ChunkBytes, bytes.Length - offset)).ToArray()))).Ok);
        Guid id = AcceptedId(await Send(h, Finish()));
        JobSnapshot completed = await h.WaitForStateAsync(id, truncate ? JobState.NeedsAttention : JobState.Completed);
        Assert.Equal(JobKind.Merge, completed.Kind);
        var receipt = await Send(h, new("status", Transfer, Origin));
        Assert.Equal(id, AcceptedId(receipt));
        Assert.Equal(bytes.Length, JsonSerializer.SerializeToElement(receipt.Data, BridgeProtocol.Json).GetProperty("offset").GetInt64());
        if (truncate)
        {
            Assert.False(File.Exists(completed.FinalPath));
            return;
        }
        using var input = new MemoryStream(original);
        using var output = File.OpenRead(completed.FinalPath!);
        var expected = WebmDemuxer.Read(input, input.Length, default).OrderBy(t => t.Kind).ToArray();
        var actual = WebmDemuxer.Read(output, output.Length, default).OrderBy(t => t.Kind).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Kind, actual[i].Kind);
            Assert.Equal(expected[i].Samples.Count, actual[i].Samples.Count);
            for (int n = 0; n < expected[i].Samples.Count; n++)
            {
                var a = expected[i].Samples[n]; var b = actual[i].Samples[n];
                Assert.Equal(a.Size, b.Size);
                byte[] left = new byte[a.Size], right = new byte[b.Size];
                input.Position = a.Offset; input.ReadExactly(left);
                output.Position = b.Offset; output.ReadExactly(right);
                Assert.Equal(left, right);
            }
        }
    }

    [Fact]
    public async Task InsufficientSpaceRefusesBeforeCreatingPrivateInbox()
    {
        using var h = new Harness(freeSpaceReserve: long.MaxValue / 2);
        var result = await Send(h, Begin(64 * 1024 * 1024));
        Assert.False(result.Ok);
        Assert.Equal("storage", result.Code);
        Assert.Empty(Directory.GetFiles(h.Data, "browser-body-*.tmp"));
        Assert.Empty(h.Engine.Snapshots());
    }

    [Fact]
    public async Task BrowserCopyBecomesNativeJobOnlyAfterEveryByteIsVerified()
    {
        using var h = new Harness();
        byte[] bytes = Payloads.Make(700_000, 123);
        Assert.True((await Send(h, Begin(bytes.Length))).Ok);
        Assert.Empty(h.Engine.Snapshots());
        for (int offset = 0; offset < bytes.Length; offset += BrowserBodyReceiver.ChunkBytes)
        {
            byte[] part = bytes.AsSpan(offset, Math.Min(BrowserBodyReceiver.ChunkBytes, bytes.Length - offset)).ToArray();
            Assert.True((await Send(h, Append(offset, part))).Ok);
            // Native messaging can lose an acknowledgement. The same offset is
            // idempotent and cannot append duplicate bytes.
            Assert.True((await Send(h, Append(offset, part))).Ok);
        }
        Guid id = AcceptedId(await Send(h, Finish()));
        Assert.Equal(id, AcceptedId(await Send(h, Finish())));
        JobSnapshot saved = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(JobKind.BrowserStream, saved.Kind);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(saved.FinalPath!));
        Assert.Single(h.Engine.Snapshots());
        Assert.Empty(Directory.GetFiles(h.Data, "browser-body-*.tmp"));
    }

    [Theory]
    [InlineData("av-init.mp4", false)]
    [InlineData("av-1.m4s", false)]
    [InlineData("instinct-mse.mp4", true)]
    [InlineData("video-only.mp4", true)]
    public void BrowserMediaGuardValidatesRealFiles(string fixture, bool playable)
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", fixture));
            File.WriteAllBytes(path, bytes);
            using (var staged = PartialFile.Open(path, create: false))
            {
                if (playable) MediaTrackProbe.RequireMp4Media(staged, default);
                else Assert.Throws<MediaContainerException>(() => MediaTrackProbe.RequireMp4Media(staged, default));
            }
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("av-init.mp4", false)]
    [InlineData("instinct-mse.mp4", true)]
    public async Task ExplicitBrowserMediaCopyValidatesBeforeAcceptance(string fixture, bool playable)
    {
        using var h = new Harness();
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", fixture));
        var opened = await Send(h, Begin(bytes.Length) with { VerifyMp4 = true });
        Assert.True(opened.Ok, opened.Message);
        Assert.Equal("mp4", System.Text.Json.JsonSerializer.SerializeToElement(opened.Data, BridgeProtocol.Json).GetProperty("mediaCheck").GetString());
        for (int offset = 0; offset < bytes.Length; offset += BrowserBodyReceiver.ChunkBytes)
            Assert.True((await Send(h, Append(offset, bytes.AsSpan(offset, Math.Min(BrowserBodyReceiver.ChunkBytes, bytes.Length - offset)).ToArray()))).Ok);
        var result = await Send(h, Finish());
        if (playable)
        {
            var saved = await h.WaitForStateAsync(AcceptedId(result), JobState.Completed);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(saved.FinalPath!));
        }
        else
        {
            Assert.False(result.Ok);
            Assert.Equal("invalid-media", result.Code);
            Assert.Empty(h.Engine.Snapshots());
            Assert.False((await Send(h, new("status", Transfer, Origin))).Ok);
        }
    }

    [Theory]
    [InlineData("muxed.webm", false)]
    [InlineData("video-only.webm", false)]
    [InlineData("quiet-track.webm", false)]
    [InlineData("muxed.webm", true)]
    [InlineData("av-init.mp4", false)]
    public async Task DirectWebmCopyChecksContainerBeforeAcceptance(string fixture, bool truncate)
    {
        using var h = new Harness();
        byte[] bytes = DashTransferTests.Fixture(fixture);
        if (truncate) bytes = bytes[..(bytes.Length / 2)];
        var begin = await Send(h, Begin(bytes.Length, "clip.webm") with { VerifyWebm = true });
        Assert.True(begin.Ok, begin.Message);
        Assert.Equal("webm", JsonSerializer.SerializeToElement(begin.Data, BridgeProtocol.Json).GetProperty("mediaCheck").GetString());
        Assert.False((await Send(h, Begin(bytes.Length, "clip.webm"))).Ok);
        for (int offset = 0; offset < bytes.Length; offset += BrowserBodyReceiver.ChunkBytes)
            Assert.True((await Send(h, Append(offset, bytes.AsSpan(offset, Math.Min(BrowserBodyReceiver.ChunkBytes, bytes.Length - offset)).ToArray()))).Ok);
        var result = await Send(h, Finish());
        if (truncate || !fixture.EndsWith(".webm", StringComparison.Ordinal))
        {
            Assert.False(result.Ok);
            Assert.Equal("invalid-media", result.Code);
            Assert.Empty(h.Engine.Snapshots());
        }
        else
        {
            var saved = await h.WaitForStateAsync(AcceptedId(result), JobState.Completed);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(saved.FinalPath!));
        }
    }

    [Fact]
    public async Task IncompleteWrongOffsetAndWrongDigestNeverBecomeAJob()
    {
        using var h = new Harness();
        byte[] bytes = Payloads.Make(200, 1);
        Assert.True((await Send(h, Begin(400))).Ok);
        Assert.False((await Send(h, Append(1, bytes))).Ok);
        Assert.False((await Send(h, Append(0, bytes) with { Digest = new string('0', 64) })).Ok);
        Assert.True((await Send(h, Append(0, bytes))).Ok);
        Assert.False((await Send(h, Finish())).Ok);
        Assert.False((await Send(h, Append(200, Payloads.Make(201, 2)))).Ok);
        Assert.Empty(h.Engine.Snapshots());
        Assert.True((await Send(h, new("abort", Transfer))).Ok);
        Assert.Empty(Directory.GetFiles(h.Data, "browser-body-*.tmp"));
    }

    [Fact]
    public async Task QueueSurvivesRestartWithoutFetchingThePageOrBrowserUrl()
    {
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        byte[] bytes = Payloads.Make(32_768, 19);
        Assert.True((await Send(h, Begin(bytes.Length))).Ok);
        Assert.True((await Send(h, Append(0, bytes))).Ok);
        Guid id = AcceptedId(await Send(h, Finish()));
        Assert.Equal(JobState.Queued, h.Engine.Snapshot(id)!.State);
        Assert.False(h.Engine.RestartFromScratch(id));
        h.Restart();
        JobSnapshot saved = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(saved.FinalPath!));
        Assert.Equal(id, AcceptedId(await Send(h, Finish())));
        Assert.Equal(id, AcceptedId(await Send(h, new("status", Transfer, Origin))));
        Assert.False((await Send(h, new("status", Transfer, "https://foreign.test/"))).Ok);
        Assert.False((await Send(h, new("status", "missing-copy-123", Origin))).Ok);
    }

    [Fact]
    public async Task ReceiverDisposalDropsIncompletePrivateStagingAndNeverReplays()
    {
        using var h = new Harness();
        Assert.True((await Send(h, Begin(100))).Ok);
        Assert.True((await Send(h, Append(0, [1, 2, 3]))).Ok);
        for (int i = 1; i < BrowserBodyReceiver.MaxConcurrentTransfers; i++)
            Assert.True((await Send(h, Begin(100) with { TransferId = $"other-copy-{i:00000}" })).Ok);
        Assert.Equal("busy", (await Send(h, Begin(100) with { TransferId = "other-copy-extra" })).Code);
        h.Restart();
        Assert.Empty(h.Engine.Snapshots());
        Assert.Empty(Directory.GetFiles(h.Data, "browser-body-*.tmp"));
        Assert.False((await Send(h, Finish())).Ok);
    }

    [Fact]
    public async Task ChangedPartialCannotPublishOrRestartAsAnHttpDownload()
    {
        using var h = new Harness(tweak: options => options.MaxConcurrentJobs = 0);
        byte[] bytes = Payloads.Make(10_000, 9);
        Assert.True((await Send(h, Begin(bytes.Length))).Ok);
        Assert.True((await Send(h, Append(0, bytes))).Ok);
        Guid id = AcceptedId(await Send(h, Finish()));
        string partial = h.Engine.Snapshot(id)!.PartialPath;
        Assert.Equal(h.Engine.PartialsFolder, Path.GetDirectoryName(partial));
        Assert.Empty(h.Files());
        await File.WriteAllBytesAsync(partial, Payloads.WithChangedTail(bytes));
        h.Restart();
        JobSnapshot saved = await h.WaitForStateAsync(id, JobState.Suspect);
        Assert.Null(saved.FinalPath);
        Assert.False(h.Engine.RestartFromScratch(id));
    }

    [Fact]
    public async Task FramedPipeTransfersCompleteBodyWithoutUsingProductionPipe()
    {
        using var h = new Harness();
        string pipe = BridgePipe.NameForCurrentUser("body-fixture-" + Guid.NewGuid().ToString("N"));
        using var server = new BridgeServer(pipe, (request, ct) =>
            request.BrowserBody is { } body
                ? h.Engine.ReceiveBrowserBodyAsync(request.Id, body, ct)
                : Task.FromResult(BridgeResponse.Fail(request.Id, "invalid", "Expected browser body.")));
        server.Start();
        async Task<BridgeResponse> Request(object message)
        {
            string raw = await BridgePipe.RequestAsync(pipe, JsonSerializer.Serialize(message),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), default);
            return JsonSerializer.Deserialize<BridgeResponse>(raw, BridgeProtocol.Json)!;
        }
        byte[] bytes = Payloads.Make(300_000, 88);
        Assert.True((await Request(new { v = BridgeProtocol.Version, id = "body-pipe-begin", type = "browser-body",
            operation = "begin", transferId = Transfer, origin = Origin, fileName = "pipe.mp4", length = bytes.Length })).Ok);
        for (int offset = 0; offset < bytes.Length; offset += BrowserBodyReceiver.ChunkBytes)
        {
            byte[] part = bytes.AsSpan(offset, Math.Min(BrowserBodyReceiver.ChunkBytes, bytes.Length - offset)).ToArray();
            Assert.True((await Request(new { v = BridgeProtocol.Version, id = "body-pipe-append", type = "browser-body",
                operation = "append", transferId = Transfer, offset, chunk = Convert.ToBase64String(part), digest = Hash(part) })).Ok);
        }
        Guid id = AcceptedId(await Request(new { v = BridgeProtocol.Version, id = "body-pipe-finish", type = "browser-body",
            operation = "finish", transferId = Transfer }));
        JobSnapshot saved = await h.WaitForStateAsync(id, JobState.Completed);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(saved.FinalPath!));
    }

    [Fact]
    public async Task PersistentPipePublishesByteExactLargeBodyAndReleasesOnDisconnect()
    {
        using var h = new Harness();
        string pipe = BridgePipe.NameForCurrentUser("body-session-fixture-" + Guid.NewGuid().ToString("N"));
        using var server = new BridgeServer(pipe, (request, ct) =>
            request.BrowserBody is { } body
                ? h.Engine.ReceiveBrowserBodyAsync(request.Id, body, ct)
                : Task.FromResult(BridgeResponse.Fail(request.Id, "invalid", "Expected browser body.")));
        server.Start();
        var abandoned = await BridgePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(5), default);
        async Task<BridgeResponse> Exchange(Stream stream, object frame)
        {
            string raw = await BridgePipe.ExchangeAsync(stream, JsonSerializer.Serialize(frame), TimeSpan.FromSeconds(10), default);
            return JsonSerializer.Deserialize<BridgeResponse>(raw, BridgeProtocol.Json)!;
        }
        object Start(string id, long length) => new { v = BridgeProtocol.Version, id = "session-begin", type = "browser-body",
            operation = "begin", transferId = id, session = true, origin = Origin, fileName = "session.mp4", length };
        Assert.True((await Exchange(abandoned, Start("abandoned-session-01", 1000))).Ok);
        await abandoned.DisposeAsync();
        // The server owns the private inbox lifetime, not a two-minute browser timer.
        for (int i = 0; i < 100 && Directory.GetFiles(h.Data, "browser-body-*.tmp").Length != 0; i++)
            await Task.Delay(10);
        Assert.Empty(Directory.GetFiles(h.Data, "browser-body-*.tmp"));
        Assert.Empty(h.Engine.Snapshots());

        byte[] bytes = Payloads.Make(32 * 1024 * 1024, 21);
        await using var client = await BridgePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(5), default);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Assert.True((await Exchange(client, Start(Transfer, bytes.Length))).Ok);
        await using (var duplicate = await BridgePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(5), default))
        {
            var refused = await Exchange(duplicate, Start(Transfer, bytes.Length));
            Assert.False(refused.Ok);
            Assert.Equal("busy", refused.Code);
        }
        // Closing a refused second connection must not abort the original inbox.
        for (int offset = 0; offset < bytes.Length; offset += BrowserBodyReceiver.ChunkBytes)
        {
            byte[] part = bytes.AsSpan(offset, Math.Min(BrowserBodyReceiver.ChunkBytes, bytes.Length - offset)).ToArray();
            Assert.True((await Exchange(client, new { v = BridgeProtocol.Version, id = "session-append", type = "browser-body",
                operation = "append", transferId = Transfer, session = true, offset,
                chunk = Convert.ToBase64String(part), digest = Hash(part) })).Ok);
        }
        Guid id = AcceptedId(await Exchange(client, new { v = BridgeProtocol.Version, id = "session-finish", type = "browser-body",
            operation = "finish", transferId = Transfer, session = true }));
        JobSnapshot saved = await h.WaitForStateAsync(id, JobState.Completed);
        elapsed.Stop();
        Assert.Equal(bytes, await File.ReadAllBytesAsync(saved.FinalPath!));
        Assert.Single(h.Engine.Snapshots());
        Console.WriteLine($"Browser session fixture: {bytes.Length} bytes in {elapsed.Elapsed.TotalMilliseconds:F0} ms including publication.");
    }

    [Fact]
    public async Task PersistentSessionRejectsChangingTransferIdAndCleansUp()
    {
        using var h = new Harness();
        string pipe = BridgePipe.NameForCurrentUser("body-cross-session-" + Guid.NewGuid().ToString("N"));
        using var server = new BridgeServer(pipe, (request, ct) =>
            h.Engine.ReceiveBrowserBodyAsync(request.Id, request.BrowserBody!, ct));
        server.Start();
        await using var client = await BridgePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(5), default);
        async Task<BridgeResponse> Exchange(object frame) => JsonSerializer.Deserialize<BridgeResponse>(
            await BridgePipe.ExchangeAsync(client, JsonSerializer.Serialize(frame), TimeSpan.FromSeconds(5), default),
            BridgeProtocol.Json)!;
        Assert.True((await Exchange(new { v = BridgeProtocol.Version, id="test-begin", type="browser-body", operation="begin",
            transferId=Transfer, session=true, origin=Origin, fileName="a.mp4", length=12 })).Ok);
        Assert.False((await Exchange(new { v = BridgeProtocol.Version, id="test-finish", type="browser-body", operation="finish",
            transferId="other-session-123", session=true })).Ok);
        for (int i = 0; i < 100 && Directory.GetFiles(h.Data, "browser-body-*.tmp").Length != 0; i++)
            await Task.Delay(10);
        Assert.Empty(Directory.GetFiles(h.Data, "browser-body-*.tmp"));
        Assert.Empty(h.Engine.Snapshots());
    }

    [Theory]
    [InlineData("owned", true)]
    [InlineData("replaced", true)]
    [InlineData("legacy", true)]
    [InlineData("database-lost", true)]
    [InlineData("manifest", true)]
    [InlineData("unclaimed", false)]
    [InlineData("old-folder", false)]
    public void JournalRecoveryRequiresProvenUnownedFile(string scenario, bool retained)
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "vesper-body-recovery-" + Guid.NewGuid().ToString("N"));
        string staging = scenario == "old-folder" ? PartialFile.StagingFolder(root) : PartialFile.PrivateFolder(root);
        Directory.CreateDirectory(staging);
        try
        {
            string path = Path.Combine(staging, "body.12345678.vpart");
            File.WriteAllBytes(path, [1, 2, 3]);
            string? identity;
            using (var file = PathSafety.OpenTemporaryHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                identity = PathSafety.TemporaryFileIdentity(file);
            Assert.NotNull(identity);
            if (scenario == "replaced")
            {
                File.Move(path, path + ".original");
                File.WriteAllBytes(path, [4, 5, 6]);
            }
            if (scenario == "manifest") File.WriteAllText(RecoveryManifest.PathFor(path) + ".previous", "{}");
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (scenario == "owned") owned.Add(path);
            File.WriteAllText(Path.Combine(root, BrowserBodyReceiver.JournalFileName),
                JsonSerializer.Serialize(new[] { new { Id = Transfer, Path = path, State = "receiving",
                    Identity = scenario == "legacy" ? null : identity } }));
            BrowserBodyReceiver.RecoverJournal(root, owned, scenario == "database-lost");
            Assert.Equal(retained, File.Exists(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
