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
    private sealed record Cursor(string Playlist, int Next);
    private DownloadJob Job => session.Job;

    public async Task<RunOutcome> RunAsync(CancellationToken ct)
    {
        if (!PathSafety.TryResolveFolder(Job.DestinationFolder, create: true, out _, out string folderError))
            return RunOutcome.NeedsAttention("destination", folderError + " Saved data was kept.");

        Cursor? cursor = ReadCursor();
        string playlistUrl = cursor?.Playlist ?? Job.SourceUrl;
        (string text, Uri baseUri) = await FetchTextAsync(playlistUrl, ct).ConfigureAwait(false);
        if (!HlsParser.IsPlaylist(text)) return RunOutcome.NeedsAttention("not-playlist", "The address did not return an HLS playlist.");

        if (HlsParser.IsMaster(text))
        {
            IReadOnlyList<HlsVariant> usable = [.. HlsParser.ParseMaster(text, baseUri).Where(v => !v.NeedsSeparateAudio)];
            if (usable.Count == 0)
                return RunOutcome.NeedsAttention("separate-audio", "Every quality of this stream keeps its audio in a separate track, and joining tracks needs a tool VESPER does not include.");
            HlsVariant best = usable.OrderByDescending(v => v.Bandwidth).First();
            cursor = cursor is null && Job.Extents.IsEmpty ? new Cursor(best.Uri, 0) : cursor;
            (text, baseUri) = await FetchTextAsync(best.Uri, ct).ConfigureAwait(false);
            playlistUrl = best.Uri;
        }

        HlsMedia media = HlsParser.ParseMedia(text, baseUri);
        if (media.Unsupported is { } reason) return RunOutcome.NeedsAttention("drm", reason);
        if (!media.IsVod) return RunOutcome.NeedsAttention("live", "This is a live stream. VESPER downloads on-demand (VOD) streams only.");
        if (media.Segments.Count == 0) return RunOutcome.Failed("empty", "The playlist contains no media segments.");
        if (media.Segments.Select(s => s.Map?.Uri).Distinct().Count() > 1)
            return RunOutcome.NeedsAttention("unsupported", "The stream changes its initialization data part-way, which cannot be joined into one valid file.");

        cursor ??= new Cursor(playlistUrl, 0);
        int next = cursor.Next;
        long offset = Job.Extents.ContiguousFromZero();
        if (next > media.Segments.Count || (next == 0) != (offset == 0))
            return RunOutcome.NeedsAttention("cursor", "Saved progress does not match the playlist any more. Saved data was kept.");

        bool fragmented = media.Segments[0].Map is not null;
        SetOutputName(fragmented ? ".mp4" : ".ts");
        PartialFile file = session.OpenFile();
        session.SetPhase(JobState.Downloading);

        var keys = new Dictionary<string, byte[]>();
        DateTime lastMessage = DateTime.MinValue;
        for (int i = next; i < media.Segments.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            HlsSegment segment = media.Segments[i];
            byte[]? initialization = i == 0 && segment.Map is { } map
                ? await FetchSegmentAsync(map.Uri, map.Offset, map.Length, ct).ConfigureAwait(false)
                : null;
            byte[] data = await FetchSegmentAsync(segment.Uri, segment.RangeOffset, segment.RangeLength, ct).ConfigureAwait(false);
            if (segment.Key is { } key) data = await DecryptAsync(data, key, segment.Sequence, keys, ct).ConfigureAwait(false);

            // Do not concatenate an entire segment into another MemoryStream/array.
            // Commit the map and media together only after both writes succeed.
            int totalLength = checked((initialization?.Length ?? 0) + data.Length);
            await session.Limiter.AcquireAsync(totalLength, ct).ConfigureAwait(false);
            await engine.GlobalLimiter.AcquireAsync(totalLength, ct).ConfigureAwait(false);
            if (initialization is not null)
                await session.WriteAsync(initialization, offset, report: false, ct).ConfigureAwait(false);
            await session.WriteAsync(data, offset + (initialization?.Length ?? 0), report: false, ct).ConfigureAwait(false);
            session.Report(new ByteRange(offset, offset + totalLength), JsonSerializer.Serialize(new Cursor(playlistUrl, i + 1)));
            offset += totalLength;
            await session.MaybeCheckpointAsync(ct).ConfigureAwait(false);

            if (DateTime.UtcNow - lastMessage > TimeSpan.FromSeconds(1))
            {
                lastMessage = DateTime.UtcNow;
                int done = i + 1;
                engine.UpdateJob(Job, j => j.Message = $"Segment {done} of {media.Segments.Count}");
            }
        }

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
        return (System.Text.Encoding.UTF8.GetString(body), uri);
    }

    private async Task<byte[]> FetchSegmentAsync(string url, long? offset, long? length, CancellationToken ct)
    {
        UrlPolicy.TryParse(url, out Uri uri, out _);
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

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw new RunnerStop(RunOutcome.NeedsAttention("too-large", "A playlist or segment is unreasonably large."));
        await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit) throw new RunnerStop(RunOutcome.NeedsAttention("too-large", "A playlist or segment is unreasonably large."));
        }
        if (response.Content.Headers.ContentLength is long declared && buffer.Length != declared)
            throw new TransientNetworkException("A segment ended early.");
        return buffer.ToArray();
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
                return new MediaProbeResult("unsupported", "DASH streams (.mpd) are detected but not downloadable yet.", null, null, null, []);

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
