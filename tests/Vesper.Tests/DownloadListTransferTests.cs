using Vesper.Engine;

namespace Vesper.Tests;

public sealed class DownloadListTransferTests
{
    [Fact]
    public void Json_round_trips_safe_http_and_hls_entries()
    {
        DownloadListEntry[] entries =
        [
            new("https://example.test/file?lang=en&part=2", JobKind.Http, DownloadListState.Queued),
            new("https://example.test/live/index.m3u8", JobKind.Hls, DownloadListState.Paused)
        ];

        string json = DownloadListTransfer.WriteJson(entries);
        DownloadListReadResult read = DownloadListTransfer.Read(json, ".json");

        Assert.True(read.Success, read.Error);
        Assert.Equal(entries, read.Entries);
        Assert.Contains("\"version\": 1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("cookie", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Csv_round_trips_quoted_url_values()
    {
        DownloadListEntry[] entries =
        [
            new("https://example.test/file?label=a,b&mode=%22quiet%22", JobKind.Http, DownloadListState.Paused)
        ];

        string csv = DownloadListTransfer.WriteCsv(entries);
        DownloadListReadResult read = DownloadListTransfer.Read(csv, ".csv");

        Assert.True(read.Success, read.Error);
        Assert.Equal(entries, read.Entries);
        Assert.Contains("\"https://example.test/file?label=a,b&mode=%22quiet%22\"", csv, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://user:password@example.test/file")]
    [InlineData("https://example.test/file?access_token=private")]
    [InlineData("https://example.test/file?X-Amz-Signature=private")]
    [InlineData("https://example.test/file#access_token=private")]
    [InlineData("https://example.test/file?x=%ZZ")]
    [InlineData("file:///C:/private/file.bin")]
    public void Import_rejects_credential_urls_and_non_web_schemes(string url)
    {
        string json = $"{{\"format\":\"vesper-download-list\",\"version\":1,\"downloads\":[{{\"url\":\"{url}\",\"kind\":\"Http\",\"state\":\"Queued\"}}]}}";

        DownloadListReadResult read = DownloadListTransfer.Read(json, ".json");

        Assert.False(read.Success);
        Assert.Empty(read.Entries);
        Assert.DoesNotContain("private", read.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_rejects_unknown_fields_and_duplicate_json_properties()
    {
        string withSecretField = """
            {"format":"vesper-download-list","version":1,"downloads":[
              {"url":"https://example.test/file","kind":"Http","state":"Queued","authorization":"secret"}
            ]}
            """;
        string withDuplicateUrl = """
            {"format":"vesper-download-list","version":1,"downloads":[
              {"url":"https://example.test/file","url":"https://example.test/other","kind":"Http","state":"Queued"}
            ]}
            """;

        Assert.False(DownloadListTransfer.Read(withSecretField, ".json").Success);
        Assert.False(DownloadListTransfer.Read(withDuplicateUrl, ".json").Success);
    }

    [Fact]
    public void Import_enforces_file_and_entry_limits()
    {
        string oversized = new(' ', DownloadListTransfer.MaxInputBytes + 1);
        string oneEntry = "{\"url\":\"https://example.test/file\",\"kind\":\"Http\",\"state\":\"Queued\"}";
        string tooManyEntries = "{\"format\":\"vesper-download-list\",\"version\":1,\"downloads\":["
            + string.Join(",", Enumerable.Repeat(oneEntry, DownloadListTransfer.MaxEntries + 1)) + "]}";

        Assert.Contains("too large", DownloadListTransfer.Read(oversized, ".json").Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("at most", DownloadListTransfer.Read(tooManyEntries, ".json").Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_rejects_transient_states_and_duplicate_entries()
    {
        string active = DownloadListTransfer.WriteJson(
        [
            new("https://example.test/file", JobKind.Http, DownloadListState.Queued)
        ]).Replace("\"Queued\"", "\"Downloading\"", StringComparison.Ordinal);
        string duplicate = """
            {"format":"vesper-download-list","version":1,"downloads":[
              {"url":"https://example.test/file","kind":"Http","state":"Queued"},
              {"url":"https://example.test/file","kind":"Http","state":"Queued"}
            ]}
            """;

        Assert.False(DownloadListTransfer.Read(active, ".json").Success);
        Assert.False(DownloadListTransfer.Read(duplicate, ".json").Success);
    }

    [Fact]
    public void Export_maps_live_states_and_skips_completed_or_secret_links()
    {
        JobSnapshot[] snapshots =
        [
            new() { Kind = JobKind.Http, SourceUrl = "https://example.test/a", State = JobState.Downloading },
            new() { Kind = JobKind.Hls, SourceUrl = "https://example.test/live.m3u8", State = JobState.NeedsAttention },
            new() { Kind = JobKind.Http, SourceUrl = "https://example.test/file?token=secret", State = JobState.Queued },
            new() { Kind = JobKind.Http, SourceUrl = "https://example.test/done", State = JobState.Completed },
            new() { Kind = JobKind.Dash, SourceUrl = "https://example.test/manifest.mpd", State = JobState.Queued }
        ];

        DownloadListExportResult export = DownloadListTransfer.CreateExport(snapshots);

        Assert.Equal(2, export.Entries.Count);
        Assert.Equal(3, export.Skipped);
        Assert.Equal(1, export.UnsafeUrlSkipped);
        Assert.Equal(1, export.CompletedSkipped);
        Assert.Equal(1, export.UnsupportedSkipped);
        Assert.Equal(DownloadListState.Queued, export.Entries[0].State);
        Assert.Equal(DownloadListState.Paused, export.Entries[1].State);
        Assert.DoesNotContain("secret", DownloadListTransfer.WriteJson(export.Entries), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_skips_nonportable_media_selections_but_keeps_plain_links()
    {
        using var harness = new Harness();
        AddResult clip = harness.Engine.Add(new AddRequest("https://example.test/selected.webm",
            DestinationFolder: harness.Downloads, FileName: "selected.webm", StartNow: false,
            Kind: JobKind.Http, MediaProvider: "generic", MediaFormatId: "webm",
            MediaPlanJson: DirectMediaPlan.WebmClip(new MediaTimeRange(1, 2)), InitiallyPaused: true));
        AddResult hlsClip = harness.Engine.Add(new AddRequest("https://example.test/selected.m3u8",
            DestinationFolder: harness.Downloads, FileName: "selected.m3u8", StartNow: false,
            Kind: JobKind.Hls, MediaPlanJson: System.Text.Json.JsonSerializer.Serialize(new MediaTimeRange(1, 2)),
            InitiallyPaused: true));
        AddResult plain = harness.Engine.Add(new AddRequest("https://example.test/plain.bin",
            DestinationFolder: harness.Downloads, StartNow: false, InitiallyPaused: true));

        Assert.True(clip.Ok, clip.Message);
        Assert.True(hlsClip.Ok, hlsClip.Message);
        Assert.True(plain.Ok, plain.Message);
        Assert.True(harness.Engine.Snapshot(clip.JobId)!.HasNonPortableMediaSelection);
        Assert.True(harness.Engine.Snapshot(hlsClip.JobId)!.HasNonPortableMediaSelection);
        Assert.False(harness.Engine.Snapshot(plain.JobId)!.HasNonPortableMediaSelection);

        DownloadListExportResult export = DownloadListTransfer.CreateExport(harness.Engine.Snapshots());

        Assert.Single(export.Entries);
        Assert.Equal("https://example.test/plain.bin", export.Entries[0].Url);
        Assert.Equal(2, export.NonPortableMediaSelectionSkipped);
        Assert.Equal(2, export.Skipped);
        string json = DownloadListTransfer.WriteJson(export.Entries);
        Assert.DoesNotContain("MediaPlan", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Initially_paused_import_does_not_start_a_request_until_resumed()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var server = new FaultServer(64 * 1024);
        await server.StartAsync();
        using var harness = new Harness(tweak: options => options.MaxConcurrentJobs = 1);

        AddResult added = harness.Engine.Add(new AddRequest(server.Url("/file"), StartNow: false, InitiallyPaused: true));

        Assert.True(added.Ok, added.Message);
        Assert.Equal(JobState.Paused, harness.Engine.Snapshot(added.JobId)!.State);
        await Task.Delay(300);
        Assert.Equal(0, server.Hits("/file"));

        harness.Restart(tweak: options => options.MaxConcurrentJobs = 1);
        Assert.Equal(JobState.Paused, harness.Engine.Snapshot(added.JobId)!.State);
        Assert.Equal(0, server.Hits("/file"));

        harness.Engine.Resume(added.JobId);
        JobSnapshot complete = await harness.WaitAsync(added.JobId, snapshot => snapshot.State == JobState.Completed);
        Assert.Equal(JobState.Completed, complete.State);
        Assert.Equal(1, server.Hits("/file"));
    }
}
