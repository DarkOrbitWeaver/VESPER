using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vesper.Engine;

/// <summary>
/// Downloads a non-DRM VOD HLS playlist into one file. Each segment is fetched whole, optionally decrypted
/// (AES-128 with an openly served key), appended at a known offset and then reported to the same checkpoint
/// pipeline as HTTP ranges, together with the next-segment cursor. Resume therefore restarts at a segment boundary.
/// Output is MPEG-TS (concatenated TS is valid) or fragmented MP4 (init segment + fragments). Separate audio
/// renditions would need a muxer and are refused rather than faked.
/// </summary>
internal sealed class HlsTransfer(DownloadEngine engine, JobSession session, HttpTransport http)
{
    private const long MaxPlaylistBytes = 4 * 1024 * 1024;
    private const long MaxSegmentBytes = 64L * 1024 * 1024; // Bound buffering until streaming decryption is available.
    private sealed record Cursor(string Playlist, int Next, string? PlanHash = null, long Offset = 0,
        string? PrefixHash = null, int InitLength = 0, Mp4Progress? Progress = null);
    private DownloadJob Job => session.Job;

    public async Task<RunOutcome> RunAsync(CancellationToken ct)
    {
        if (!PathSafety.TryResolveFolder(Job.DestinationFolder, create: true, out _, out string folderError))
            return RunOutcome.NeedsAttention("destination", folderError + " Saved data was kept.");
        Cursor? cursor = ReadCursor();
        if (Job.MediaState is not null && cursor is null)
            return RunOutcome.NeedsAttention("cursor", "The saved streaming cursor is invalid. Saved data was kept.");
        string playlistUrl = cursor?.Playlist ?? Job.SourceUrl;
        (string text, Uri baseUri) = await FetchTextAsync(playlistUrl, ct).ConfigureAwait(false);
        if (!HlsParser.IsPlaylist(text)) return RunOutcome.NeedsAttention("not-playlist", "The address did not return an HLS playlist.");
        if (HlsParser.IsMaster(text))
        {
            if (text.Split('\n').Any(l => l.Trim().StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal)))
                return RunOutcome.NeedsAttention("drm", "Master session keys are not supported; no protection is bypassed.");
            IReadOnlyList<HlsVariant> usable = [.. HlsParser.ParseMaster(text, baseUri).Where(v => !v.NeedsSeparateAudio)];
            if (usable.Count == 0)
                return RunOutcome.NeedsAttention("separate-audio", "Every quality keeps audio separate; VESPER does not include a verified muxer for this combination.");
            HlsVariant best = usable.OrderByDescending(v => v.Bandwidth).First();
            (text, baseUri) = await FetchTextAsync(best.Uri, ct).ConfigureAwait(false);
        }
        playlistUrl = baseUri.AbsoluteUri;
        HlsMedia media = HlsParser.ParseMedia(text, baseUri);
        if (media.Unsupported is { } reason) return RunOutcome.NeedsAttention("drm", reason);
        if (!media.IsVod) return RunOutcome.NeedsAttention("live", "The playlist is not ended. VESPER downloads completed VOD playlists only.");
        if (media.Segments.Count == 0) return RunOutcome.Failed("empty", "The playlist contains no media segments.");
        if (media.Segments.Select(segment => segment.Map).Distinct().Count() > 1)
            return RunOutcome.NeedsAttention("unsupported", "Initialization URI, byte range or encryption changes part-way. A remux is required; saved data was kept.");
        string planHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(media.Segments))));
        int next = cursor?.Next ?? 0;
        long offset = Job.Extents.ContiguousFromZero();
        if (next < 0 || next > media.Segments.Count || (next == 0) != (offset == 0) || Job.Extents.TotalBytes != offset)
            return RunOutcome.NeedsAttention("cursor", "Saved progress no longer matches the playlist. Saved data was kept.");
        bool fragmented = media.Segments[0].Map is not null;
        SetOutputName(fragmented ? ".mp4" : ".ts");
        PartialFile file = session.OpenFile();
        if (file.Length < offset) return RunOutcome.NeedsAttention("cursor", "The partial file is shorter than committed progress. Saved data was kept.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        FragmentedMp4? mp4 = null;
        TransportStreamValidator? transport = fragmented ? null : new();
        int initLength = 0;
        var keys = new Dictionary<string, byte[]>();
        try
        {
            if (offset > 0)
            {
                if (cursor?.PlanHash != planHash || cursor.Offset != offset || cursor.PrefixHash is null)
                    return RunOutcome.NeedsAttention("source-changed", "The playlist changed, or this legacy cursor has no verified manifest identity. Restart is required; saved data was kept.");
                MediaPartialIntegrity.HashPrefix(file, offset, hash, ct, transport);
                if (Convert.ToHexString(hash.GetCurrentHash()) != cursor.PrefixHash)
                    return RunOutcome.NeedsAttention("integrity", "The committed HLS prefix failed SHA-256 validation. Saved data was kept.");
                initLength = cursor.InitLength;
                if (fragmented)
                {
                    if (cursor.Progress is null) throw new MediaContainerException("Saved MP4 track state is missing.");
                    mp4 = FragmentedMp4.Open(MediaPartialIntegrity.ReadInitialization(file, initLength, ct));
                    mp4.Restore(cursor.Progress);
                }
            }
            session.SetPhase(JobState.Downloading);
            DateTime lastMessage = DateTime.MinValue;
            for (int i = next; i < media.Segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                HlsSegment segment = media.Segments[i];
                byte[]? initialization = null;
                if (i == 0 && segment.Map is { } map)
                {
                    initialization = await FetchSegmentAsync(map.Uri, map.Offset, map.Length, ct).ConfigureAwait(false);
                    if (map.Key is { } mapKey)
                    {
                        if (mapKey.Iv is null) throw new MediaContainerException("Encrypted initialization requires an explicit IV.");
                        initialization = await DecryptAsync(initialization, mapKey, segment.Sequence, keys, ct).ConfigureAwait(false);
                    }
                    if (initialization.Length > 8 * 1024 * 1024) throw new MediaContainerException("Initialization exceeds the supported size limit.");
                    mp4 = FragmentedMp4.Open(initialization);
                    initLength = initialization.Length;
                }
                byte[] data = await FetchSegmentAsync(segment.Uri, segment.RangeOffset, segment.RangeLength, ct).ConfigureAwait(false);
                if (segment.Key is { } key) data = await DecryptAsync(data, key, segment.Sequence, keys, ct).ConfigureAwait(false);
                if (fragmented) mp4!.ValidateFragment(data);
                else transport!.Validate(data);
                int totalLength = checked((initialization?.Length ?? 0) + data.Length);
                string? root = Path.GetPathRoot(Job.PartialPath);
                if (root is not null && new DriveInfo(root) is { IsReady: true } drive
                    && drive.AvailableFreeSpace < totalLength + engine.Options.FreeSpaceReserve)
                    throw new RunnerStop(RunOutcome.NeedsAttention("disk-full", "Not enough free disk space. Saved data was kept."));
                await session.Limiter.AcquireAsync(totalLength, ct).ConfigureAwait(false);
                await engine.GlobalLimiter.AcquireAsync(totalLength, ct).ConfigureAwait(false);
                if (initialization is not null)
                {
                    await session.WriteAsync(initialization, offset, report: false, ct).ConfigureAwait(false);
                    hash.AppendData(initialization);
                }
                await session.WriteAsync(data, offset + (initialization?.Length ?? 0), report: false, ct).ConfigureAwait(false);
                hash.AppendData(data);
                long end = checked(offset + totalLength);
                session.Report(new ByteRange(offset, end), JsonSerializer.Serialize(new Cursor(playlistUrl, i + 1,
                    planHash, end, Convert.ToHexString(hash.GetCurrentHash()), initLength, mp4?.Snapshot())));
                offset = end;
                await session.MaybeCheckpointAsync(ct).ConfigureAwait(false);
                if (DateTime.UtcNow - lastMessage > TimeSpan.FromSeconds(1))
                {
                    lastMessage = DateTime.UtcNow;
                    engine.UpdateJob(Job, j => j.Message = $"Segment {i + 1} of {media.Segments.Count}");
                }
            }
            mp4?.Finish(requireAudio: Job.MediaProvider is not null, requireVideo: false);
            transport?.Finish(requireAudio: Job.MediaProvider is not null);
        }
        catch (MediaContainerException ex)
        { return RunOutcome.NeedsAttention("invalid-container", ex.Message + " No output was published; saved data was kept."); }
        finally { foreach (byte[] key in keys.Values) CryptographicOperations.ZeroMemory(key); }
        await session.CheckpointAsync(force: true, ct).ConfigureAwait(false);
        engine.UpdateJob(Job, j => { j.TotalBytes = offset; j.Message = ""; });
        return await engine.FinalizeAsync(session, ct).ConfigureAwait(false);
    }

    private Cursor? ReadCursor()
    {
        if (string.IsNullOrEmpty(Job.MediaState)) return null;
        try { return JsonSerializer.Deserialize<Cursor>(Job.MediaState); }
        catch (JsonException) { return null; }
    }

    private void SetOutputName(string extension)
    {
        if (Job.NameLocked && Path.HasExtension(Job.FileName)) return;
        string stem = Path.GetFileNameWithoutExtension(Job.FileName);
        if (stem.Length == 0) stem = "video";
        engine.UpdateJob(Job, j => { j.FileName = stem + extension; j.Category = "Video"; });
    }

    private async Task<(string Text, Uri Base)> FetchTextAsync(string url, CancellationToken ct)
    {
        if (!UrlPolicy.TryParse(url, out Uri uri, out string error)) throw new RunnerStop(RunOutcome.NeedsLink("bad-url", error));
        using HttpResponseMessage response = await http.SendAsync(uri, Spec(null, null), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw StatusStop((int)response.StatusCode);
        byte[] body = await ReadBoundedAsync(response, MaxPlaylistBytes, ct).ConfigureAwait(false);
        return (System.Text.Encoding.UTF8.GetString(body), response.RequestMessage?.RequestUri ?? uri);
    }

    private async Task<byte[]> FetchSegmentAsync(string url, long? offset, long? length, CancellationToken ct)
    {
        if (!UrlPolicy.TryParse(url, out Uri uri, out string error))
            throw new RunnerStop(RunOutcome.NeedsAttention("bad-url", error));
        if (offset is not null && (length is not long count || count <= 0 || offset.Value < 0 || count > MaxSegmentBytes || offset.Value > long.MaxValue - count))
            throw new RunnerStop(RunOutcome.NeedsAttention("invalid-range", "The playlist contains an invalid or oversized byte range."));
        long? end = offset is long start && length is long size ? start + size - 1 : null;
        using HttpResponseMessage response = await http.SendAsync(uri, Spec(offset, end), ct).ConfigureAwait(false);
        int code = (int)response.StatusCode;
        if (code is not (200 or 206)) throw StatusStop(code);
        if (offset is long requestedStart)
        {
            RangeCheck check = HttpFacts.CheckRangeResponse(response, requestedStart, end);
            if (!check.IsValid || check.EndInclusive != end)
                throw new RunnerStop(RunOutcome.NeedsAttention("invalid-range", "The server returned a different byte range than the playlist requested. Saved data was kept."));
            if (!HttpFacts.IsIdentityEncoded(response))
                throw new RunnerStop(RunOutcome.NeedsAttention("encoded-range", "The server compressed a byte-range response; it cannot be assembled safely."));
        }
        else if (code == 206)
            throw new RunnerStop(RunOutcome.NeedsAttention("unexpected-range", "The server unexpectedly returned only part of a full segment."));
        if (HlsLooksLikeHtml(response)) throw new RunnerStop(RunOutcome.NeedsAttention("html", "The server sent a web page instead of video data."));
        byte[] bytes = await ReadBoundedAsync(response, MaxSegmentBytes, ct).ConfigureAwait(false);
        if (length is long expected && bytes.LongLength != expected)
            throw new TransientNetworkException("The HLS byte-range response was incomplete.");
        return bytes;
    }

    private static bool HlsLooksLikeHtml(HttpResponseMessage r) => HttpFacts.IsHtml(r);

    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw new RunnerStop(RunOutcome.NeedsAttention("too-large", "A playlist or segment is unreasonably large."));
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(engine.Options.StallTimeout);
        try
        {
            await using Stream body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            long? knownLength = response.Content.Headers.ContentLength;
            using var buffer = new MemoryStream(knownLength is > 0 ? (int)knownLength.Value : 64 * 1024);
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, stall.Token).ConfigureAwait(false)) > 0)
            {
                stall.CancelAfter(engine.Options.StallTimeout);
                if (buffer.Length + read > limit) throw new RunnerStop(RunOutcome.NeedsAttention("too-large", "A playlist or segment is unreasonably large."));
                buffer.Write(chunk, 0, read);
            }
            if (response.Content.Headers.ContentLength is long declared && buffer.Length != declared)
                throw new TransientNetworkException("A segment ended early.");
            if (buffer.TryGetBuffer(out var complete) && complete.Array is { } array && complete.Count == array.Length)
                return array; // common Content-Length path avoids another full-segment allocation
            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TransientNetworkException("The HLS response stalled."); }
    }

    private async Task<byte[]> DecryptAsync(byte[] data, HlsKey key, long sequence, Dictionary<string, byte[]> cache, CancellationToken ct)
    {
        if (key.Uri is null) throw new RunnerStop(RunOutcome.NeedsAttention("key", "An encrypted stream has no key address."));
        if (!cache.TryGetValue(key.Uri, out byte[]? material))
        {
            UrlPolicy.TryParse(key.Uri, out Uri keyUri, out _);
            using HttpResponseMessage response = await http.SendAsync(keyUri, Spec(null, null), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw StatusStop((int)response.StatusCode);
            material = await ReadBoundedAsync(response, 64, ct).ConfigureAwait(false);
            if (material.Length != 16) throw new RunnerStop(RunOutcome.NeedsAttention("key", "The stream's key is not a valid AES-128 key."));
            if (cache.Count >= 32)
            {
                string old = cache.Keys.First(); CryptographicOperations.ZeroMemory(cache[old]); cache.Remove(old);
            }
            cache[key.Uri] = material;
        }

        byte[] iv = key.Iv ?? SequenceIv(sequence);
        using Aes aes = Aes.Create();
        aes.Key = material;
        try { return aes.DecryptCbc(data, iv, PaddingMode.PKCS7); }
        catch (CryptographicException) { throw new RunnerStop(RunOutcome.NeedsAttention("decrypt", "A segment could not be decrypted with the supplied key.")); }
    }

    private static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        for (int i = 0; i < 8; i++) iv[15 - i] = (byte)(sequence >> (8 * i));
        return iv;
    }

    private RequestSpec Spec(long? start, long? end) =>
        new(start, end, Referer: Job.Referer, UserAgent: Job.UserAgent, Secrets: engine.SecretsFor(Job.Id));

    private static RunnerStop StatusStop(int code) => new(code switch
    {
        401 or 403 or 404 or 410 => RunOutcome.NeedsLink("expired", $"The stream address no longer works (HTTP {code}). Saved data was kept."),
        408 or 429 or 503 or >= 500 => RunOutcome.Retry("server-error", $"The server had a problem (HTTP {code})."),
        _ => RunOutcome.Failed("http-status", $"The server answered HTTP {code}.")
    });
}

/// <summary>Describes a link for the browser's quality picker. One small request; nothing is saved.</summary>
public sealed record MediaProbeResult(string Kind, string Message, string? ContentType, long? ContentLength,
    double? DurationSeconds, IReadOnlyList<MediaVariant> Variants);

public sealed record MediaVariant(string Url, long Bandwidth, int Width, int Height, bool AudioOnly, string? Codecs);

public sealed partial class DownloadEngine
{
    public async Task<MediaProbeResult> ProbeMediaAsync(string url, CancellationToken ct = default)
    {
        if (!UrlPolicy.TryParse(url, out Uri uri, out string error)) return new MediaProbeResult("invalid", error, null, null, null, []);
        try
        {
            UrlPolicy.EnsureAllowed(uri, Options.AllowPrivateNetworks);
            bool playlist = uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
            if (uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
                return new MediaProbeResult("unsupported", "Use Find video to resolve a supported clear fragmented-MP4 DASH plan; raw manifests cannot be downloaded as files.", null, null, null, []);

            using HttpResponseMessage response = await _http.SendAsync(uri, new RequestSpec(playlist ? null : 0, playlist ? null : 0), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new MediaProbeResult("unreachable", $"The server answered HTTP {(int)response.StatusCode}.", null, null, null, []);

            string? type = HttpFacts.ContentType(response);
            if (playlist || type is "application/vnd.apple.mpegurl" or "application/x-mpegurl")
            {
                await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(body, System.Text.Encoding.UTF8, true, 1 << 16, leaveOpen: true);
                var buffer = new char[(int)Math.Min(4 * 1024 * 1024, 1 << 20)];
                int n = await reader.ReadBlockAsync(buffer, ct).ConfigureAwait(false);
                string text = new(buffer, 0, n);
                if (!HlsParser.IsPlaylist(text)) return new MediaProbeResult("unsupported", "That address is not a playlist VESPER understands.", type, null, null, []);
                if (HlsParser.IsMaster(text))
                {
                    var variants = HlsParser.ParseMaster(text, uri).Where(v => !v.NeedsSeparateAudio)
                        .Select(v => new MediaVariant(v.Uri, v.Bandwidth, v.Width, v.Height, v.AudioOnly, v.Codecs)).ToList();
                    return variants.Count == 0
                        ? new MediaProbeResult("unsupported", "Every quality keeps its audio in a separate track, which VESPER cannot join.", type, null, null, [])
                        : new MediaProbeResult("hls-master", "", type, null, null, variants);
                }
                HlsMedia media = HlsParser.ParseMedia(text, uri);
                if (media.Unsupported is { } why) return new MediaProbeResult("unsupported", why, type, null, null, []);
                if (!media.IsVod) return new MediaProbeResult("unsupported", "Live streams are not supported.", type, null, null, []);
                return new MediaProbeResult("hls-media", "", type, null, media.TotalSeconds, []);
            }

            long? length = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
            return new MediaProbeResult("direct", "", type, length, null, []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && (ErrorClassifier.IsTransient(ex) || ErrorClassifier.IsBlocked(ex) || ex is RedirectException))
        {
            return new MediaProbeResult("unreachable", ErrorClassifier.Describe(ex), null, null, null, []);
        }
    }
}
