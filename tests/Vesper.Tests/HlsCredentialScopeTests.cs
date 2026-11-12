using Vesper.Engine;

namespace Vesper.Tests;

public sealed class HlsCredentialScopeTests
{
    private static readonly IReadOnlyDictionary<string, string> Secrets = new Dictionary<string, string>
    {
        ["Cookie"] = "session=source-secret",
        ["Authorization"] = "Bearer source-secret"
    };

    private static Guid AddHls(Harness harness, FaultServer source, string path) =>
        harness.Engine.Add(new AddRequest(source.Url(path), FileName: "credential-test", Kind: JobKind.Hls,
            Referer: source.Url("/watch?token=referer-secret"), Secrets: Secrets)).JobId;

    [Fact]
    public async Task Credentials_stay_on_the_source_origin_while_same_origin_hls_requests_keep_them()
    {
        await using var source = new FaultServer();
        await using var remote = new FaultServer();
        await source.StartAsync();
        await remote.StartAsync();

        const string playlistPath = "/hls/credential/index.m3u8";
        const string redirectPath = "/hls/credential/redirect.ts";
        const string redirectTargetPath = "/hls/credential/redirected.ts";
        const string remotePath = "/hls/remote/segment.ts";
        source.HlsPlaylists[playlistPath] = $"#EXTM3U\n#EXTINF:4.0,\ns0.ts\n#EXTINF:4.0,\n{redirectPath}\n#EXTINF:4.0,\n{remote.Url(remotePath)}\n#EXT-X-ENDLIST\n";
        source.Redirects[redirectPath] = source.Url(redirectTargetPath);
        source.HlsFiles[redirectTargetPath] = FaultServer.HlsSegment(1);
        remote.HlsFiles[remotePath] = FaultServer.HlsSegment(2);

        using var harness = new Harness();
        Guid id = AddHls(harness, source, playlistPath);
        Assert.Equal(source.BaseUrl + "/", harness.Engine.CredentialsFor(id)?.Origin.AbsoluteUri);
        JobSnapshot done = await harness.WaitForStateAsync(id, JobState.Completed);

        byte[] expected = [.. FaultServer.HlsSegment(0), .. FaultServer.HlsSegment(1), .. FaultServer.HlsSegment(2)];
        Assert.Equal(expected, await File.ReadAllBytesAsync(done.FinalPath!));
        AssertSecrets(Assert.Single(source.RequestsFor(playlistPath)));
        AssertSecrets(Assert.Single(source.RequestsFor("/hls/credential/s0.ts")));
        AssertSecrets(Assert.Single(source.RequestsFor(redirectPath)));
        AssertSecrets(Assert.Single(source.RequestsFor(redirectTargetPath)));

        FaultServer.ObservedHeaders remoteRequest = Assert.Single(remote.RequestsFor(remotePath));
        AssertNoSecrets(remoteRequest);
        Assert.Equal(source.BaseUrl + "/", remoteRequest.Referer);
    }

    [Fact]
    public async Task Credentials_are_not_sent_to_a_cross_origin_hls_key()
    {
        await using var source = new FaultServer();
        await using var remote = new FaultServer();
        await source.StartAsync();
        await remote.StartAsync();

        const string playlistPath = "/hls/enc/index.m3u8";
        const string keyPath = "/hls/remote/key.bin";
        source.HlsPlaylists[playlistPath] = $"#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"{remote.Url(keyPath)}\",IV=0x{Convert.ToHexString(FaultServer.HlsIv)}\n#EXTINF:4.0,\ns0.ts\n#EXT-X-ENDLIST\n";
        remote.HlsFiles[keyPath] = FaultServer.HlsKey;

        using var harness = new Harness();
        Guid id = AddHls(harness, source, playlistPath);
        JobSnapshot done = await harness.WaitForStateAsync(id, JobState.Completed);

        Assert.Equal(FaultServer.HlsSegment(0), await File.ReadAllBytesAsync(done.FinalPath!));
        AssertSecrets(Assert.Single(source.RequestsFor(playlistPath)));
        AssertSecrets(Assert.Single(source.RequestsFor("/hls/enc/s0.ts")));
        AssertNoSecrets(Assert.Single(remote.RequestsFor(keyPath)));
    }

    [Fact]
    public async Task Credentials_are_not_sent_to_cross_origin_hls_initialization_or_fragments()
    {
        await using var source = new FaultServer();
        await using var remote = new FaultServer();
        await source.StartAsync();
        await remote.StartAsync();

        const string playlistPath = "/hls/fmp4/index.m3u8";
        string[] mediaNames = ["av-init.mp4", "av-1.m4s", "av-2.m4s", "av-3.m4s"];
        source.HlsPlaylists[playlistPath] = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-MAP:URI=\"" + remote.Url("/hls/remote/av-init.mp4") + "\"\n" +
            string.Join("\n", mediaNames.Skip(1).Select(name => "#EXTINF:1,\n" + remote.Url("/hls/remote/" + name))) + "\n#EXT-X-ENDLIST\n";
        foreach (string name in mediaNames)
            remote.HlsFiles["/hls/remote/" + name] = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", name));

        using var harness = new Harness();
        Guid id = AddHls(harness, source, playlistPath);
        JobSnapshot done = await harness.WaitForStateAsync(id, JobState.Completed);

        byte[] expected = mediaNames.SelectMany(name => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", name))).ToArray();
        Assert.Equal(expected, await File.ReadAllBytesAsync(done.FinalPath!));
        AssertSecrets(Assert.Single(source.RequestsFor(playlistPath)));
        foreach (string name in mediaNames)
            AssertNoSecrets(Assert.Single(remote.RequestsFor("/hls/remote/" + name)));
    }

    [Fact]
    public async Task Credentials_do_not_follow_a_cross_origin_hls_redirect()
    {
        await using var source = new FaultServer();
        await using var redirect = new FaultServer();
        await using var destination = new FaultServer();
        await source.StartAsync();
        await redirect.StartAsync();
        await destination.StartAsync();

        const string playlistPath = "/hls/redirect/index.m3u8";
        const string redirectPath = "/hls/redirect/segment.ts";
        const string finalPath = "/hls/redirect/final.ts";
        source.HlsPlaylists[playlistPath] = $"#EXTM3U\n#EXTINF:4.0,\n{redirect.Url(redirectPath)}\n#EXT-X-ENDLIST\n";
        redirect.Redirects[redirectPath] = destination.Url(finalPath);
        destination.HlsFiles[finalPath] = FaultServer.HlsSegment(0);

        using var harness = new Harness();
        Guid id = AddHls(harness, source, playlistPath);
        JobSnapshot done = await harness.WaitForStateAsync(id, JobState.Completed);

        Assert.Equal(FaultServer.HlsSegment(0), await File.ReadAllBytesAsync(done.FinalPath!));
        AssertSecrets(Assert.Single(source.RequestsFor(playlistPath)));
        AssertNoSecrets(Assert.Single(redirect.RequestsFor(redirectPath)));
        AssertNoSecrets(Assert.Single(destination.RequestsFor(finalPath)));
    }

    private static void AssertSecrets(FaultServer.ObservedHeaders request)
    {
        Assert.Equal(Secrets["Cookie"], request.Cookie);
        Assert.Equal(Secrets["Authorization"], request.Authorization);
    }

    private static void AssertNoSecrets(FaultServer.ObservedHeaders request)
    {
        Assert.Null(request.Cookie);
        Assert.Null(request.Authorization);
    }
}
