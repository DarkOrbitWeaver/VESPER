using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vesper.Engine;

internal sealed record HlsWebVttPlan
{
    public int Version { get; init; } = 1;
    public string Type { get; init; } = "hls-webvtt";
    public string OutputFormat { get; init; } = "vtt";

    public string ToJson() => JsonSerializer.Serialize(this);

    internal static bool TryParse(string? json, out HlsWebVttPlan plan)
    {
        plan = new HlsWebVttPlan();
        if (json is null || json.Length > 1024) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryProperty(root, "Version", out JsonElement version)
                || !version.TryGetInt32(out int versionNumber) || versionNumber != 1
                || !TryProperty(root, "Type", out JsonElement type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "hls-webvtt") return false;
            HlsWebVttPlan? parsed = JsonSerializer.Deserialize<HlsWebVttPlan>(json, new JsonSerializerOptions { MaxDepth = 8 });
            if (parsed is not { Version: 1, Type: "hls-webvtt" }
                || parsed.OutputFormat is not ("vtt" or "srt")) return false;
            plan = parsed;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value) =>
        element.TryGetProperty(name, out value) || element.TryGetProperty(char.ToLowerInvariant(name[0]) + name[1..], out value);
}

/// <summary>Downloads a bounded VOD WebVTT rendition and writes one timestamp-correct sidecar file.</summary>
internal sealed class HlsWebVttTransfer(DownloadEngine engine, JobSession session, HttpTransport http)
{
    private const long MaxPlaylistBytes = 4 * 1024 * 1024;
    private const long MaxSegmentBytes = 2 * 1024 * 1024;
    private const long MaxSourceBytes = 32 * 1024 * 1024;
    private const long MaxOutputBytes = 32 * 1024 * 1024;
    private const int MaxSegments = 10_000;
    private const int MaxCues = 100_000;
    private const int ChunkSize = 128 * 1024;

    private sealed record Cursor(int Version, string Fingerprint, string Phase, long Length, string Sha256);
    private DownloadJob Job => session.Job;

    internal async Task<RunOutcome> RunAsync(CancellationToken ct)
    {
        if (!HlsWebVttPlan.TryParse(Job.MediaPlanJson, out HlsWebVttPlan plan))
            return NeedsAttention("subtitle-plan", "The saved HLS subtitle plan is invalid. Saved data was kept.");

        string fingerprint = Fingerprint(Job.MediaPlanJson!);
        Cursor? cursor = ReadCursor();
        if (Job.MediaState is not null && (cursor is null || cursor.Version != 1 || cursor.Phase != "ready"
            || cursor.Fingerprint != fingerprint || cursor.Length <= 0 || !IsSha256(cursor.Sha256)))
            return NeedsAttention("subtitle-cursor", "The saved subtitle output cursor is invalid. Saved data was kept.");

        PartialFile file = session.OpenFile();
        if (cursor is not null)
            return await FinalizeSavedAsync(file, cursor, ct).ConfigureAwait(false);
        if (Job.Extents.TotalBytes != 0)
            return NeedsAttention("subtitle-cursor", "Saved subtitle bytes have no valid output cursor. Saved data was kept.");
        if (file.Length > 0) file.Truncate(0); // A crash before the complete output was committed leaves no usable progress.

        try
        {
            session.SetPhase(JobState.Downloading);
            (string text, Uri baseUri) = await FetchTextAsync(Job.SourceUrl, MaxPlaylistBytes, ct).ConfigureAwait(false);
            if (!HlsParser.IsPlaylist(text)) return NeedsAttention("subtitle-playlist", "The address did not return an HLS subtitle playlist.");
            if (HlsParser.IsMaster(text)) return NeedsAttention("subtitle-playlist", "The HLS subtitle address must resolve directly to its media playlist.");

            HlsMedia media = HlsParser.ParseMedia(text, baseUri, allowTimelineChanges: true);
            if (media.Unsupported is { } unsupported) return NeedsAttention(media.UnsupportedReasonCode, unsupported);
            if (!media.IsVod) return NeedsAttention("subtitle-live", "Segmented subtitles are supported for completed HLS playlists, not live playlists.");
            if (media.Segments.Count is 0 or > MaxSegments)
                return NeedsAttention("subtitle-size", "The HLS subtitle playlist is empty or exceeds the supported segment limit.");
            if (media.Segments.Any(segment => segment.DiscontinuityBefore || segment.IsGap))
                return NeedsAttention("subtitle-timeline", "Discontinuous or missing subtitle segments cannot be merged safely.");
            if (media.Segments.Any(segment => segment.Key is not null))
                return NeedsAttention("subtitle-encrypted", "Encrypted HLS subtitles are not supported.");
            if (media.Segments.Any(segment => segment.Map is not null))
                return NeedsAttention("subtitle-format", "Only text WebVTT segments are supported; this playlist uses an initialization section.");
            if (media.Segments.Any(segment => !double.IsFinite(segment.Duration) || segment.Duration <= 0)
                || media.TotalSeconds > TimeSpan.FromDays(365).TotalSeconds)
                return NeedsAttention("subtitle-timeline", "The HLS subtitle playlist has an invalid duration.");

            byte[] output = await DownloadAndAssembleAsync(media, ct).ConfigureAwait(false);
            if (plan.OutputFormat == "srt") output = WebVtt.ToSubRip(output);
            if (output.Length == 0 || output.LongLength > MaxOutputBytes)
                return NeedsAttention("subtitle-size", "The assembled subtitle file exceeds the supported size.");
            EnsureSpace(output.LongLength);

            string sha = Convert.ToHexString(SHA256.HashData(output));
            Cursor ready = new(1, fingerprint, "ready", output.LongLength, sha);
            for (int offset = 0; offset < output.Length; offset += ChunkSize)
            {
                ct.ThrowIfCancellationRequested();
                int count = Math.Min(ChunkSize, output.Length - offset);
                await session.WriteAsync(output.AsMemory(offset, count), offset, report: false, ct).ConfigureAwait(false);
            }
            session.Report(new ByteRange(0, output.LongLength), JsonSerializer.Serialize(ready));
            await session.CheckpointAsync(force: true, ct).ConfigureAwait(false);
            if (!HashOutput(file, ready.Length, ct).Equals(ready.Sha256, StringComparison.Ordinal))
                return NeedsAttention("subtitle-integrity", "The saved subtitle output failed SHA-256 validation. Saved data was kept.");
            engine.UpdateJob(Job, job => { job.TotalBytes = output.LongLength; job.Category = "Subtitles"; job.Message = ""; });
            return await engine.FinalizeAsync(session, ct).ConfigureAwait(false);
        }
        catch (MediaContainerException ex)
        {
            return NeedsAttention("subtitle-invalid", ex.Message + " Saved data was kept.");
        }
    }

    private async Task<RunOutcome> FinalizeSavedAsync(PartialFile file, Cursor cursor, CancellationToken ct)
    {
        long offset = Job.Extents.ContiguousFromZero();
        if (offset != cursor.Length || Job.Extents.TotalBytes != offset || file.Length < offset)
            return NeedsAttention("subtitle-cursor", "The saved subtitle output does not match its committed cursor. Saved data was kept.");
        if (!HashOutput(file, offset, ct).Equals(cursor.Sha256, StringComparison.Ordinal))
            return NeedsAttention("subtitle-integrity", "The saved subtitle output failed SHA-256 validation. Saved data was kept.");
        engine.UpdateJob(Job, job => { job.TotalBytes = cursor.Length; job.Category = "Subtitles"; });
        return await engine.FinalizeAsync(session, ct).ConfigureAwait(false);
    }

    private static string HashOutput(PartialFile file, long length, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[ChunkSize];
        long position = 0;
        while (position < length)
        {
            ct.ThrowIfCancellationRequested();
            int wanted = (int)Math.Min(buffer.Length, length - position);
            int read = file.Read(buffer.AsSpan(0, wanted), position);
            if (read != wanted) throw new LocalStorageException("The saved subtitle output is truncated.", new EndOfStreamException());
            hash.AppendData(buffer, 0, read);
            position += read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task<byte[]> DownloadAndAssembleAsync(HlsMedia media, CancellationToken ct)
    {
        var cues = new List<Cue>();
        var styles = new List<string>();
        var seenStyles = new HashSet<string>(StringComparer.Ordinal);
        var metadata = new List<string>();
        var seenCues = new HashSet<string>(StringComparer.Ordinal);
        long downloaded = 0;
        long? originTimestamp = null, previousTimestamp = null;
        IReadOnlyList<string>? firstMetadata = null;
        bool? mapped = null;
        int order = 0;

        for (int i = 0; i < media.Segments.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            HlsSegment segment = media.Segments[i];
            byte[] bytes = await FetchSegmentAsync(segment, ct).ConfigureAwait(false);
            downloaded = checked(downloaded + bytes.LongLength);
            if (downloaded > MaxSourceBytes)
                throw new MediaContainerException("The HLS subtitle segments exceed the supported download size.");

            ParsedWebVtt parsed = WebVtt.Parse(bytes);
            mapped ??= parsed.MpegTimestamp is not null;
            if (mapped != (parsed.MpegTimestamp is not null))
                throw new MediaContainerException("The HLS subtitle segments mix mapped and unmapped timestamps.");
            if (firstMetadata is null)
            {
                firstMetadata = parsed.Metadata;
                metadata.AddRange(parsed.Metadata);
            }
            else if (!firstMetadata.SequenceEqual(parsed.Metadata))
                throw new MediaContainerException("The WebVTT segment headers do not match.");
            foreach (string style in parsed.Styles)
                if (seenStyles.Add(style)) styles.Add(style);

            long offset = 0;
            if (parsed.MpegTimestamp is ulong rawTimestamp)
            {
                long timestamp = UnwrapTimestamp(rawTimestamp, previousTimestamp);
                if (previousTimestamp is long prior && timestamp < prior)
                    throw new MediaContainerException("The WebVTT timestamp map moves backward without a discontinuity.");
                originTimestamp ??= timestamp;
                previousTimestamp = timestamp;
                offset = checked((long)(((Int128)(timestamp - originTimestamp.Value) * TimeSpan.TicksPerSecond) / 90_000)
                    - parsed.LocalTicks);
            }

            foreach (Cue cue in parsed.Cues)
            {
                long start = checked(cue.StartTicks + offset), end = checked(cue.EndTicks + offset);
                if (end <= 0) continue;
                start = Math.Max(0, start);
                if (end <= start || end > TimeSpan.FromDays(365).Ticks)
                    throw new MediaContainerException("An HLS subtitle cue has an invalid timestamp.");
                string block = WebVtt.FormatCue(cue, start, end);
                if (seenCues.Add(block))
                {
                    if (cues.Count >= MaxCues) throw new MediaContainerException("The HLS subtitle playlist has too many cues.");
                    cues.Add(new Cue(start, end, order++, block));
                }
            }
            engine.UpdateJob(Job, job => job.Message = $"Subtitle segment {i + 1} of {media.Segments.Count}");
        }

        var output = new StringBuilder("WEBVTT\n");
        foreach (string line in metadata)
            if (!line.StartsWith("X-TIMESTAMP-MAP", StringComparison.OrdinalIgnoreCase)) output.Append(line).Append('\n');
        output.Append('\n');
        foreach (string style in styles) output.Append(style).Append("\n\n");
        foreach (Cue cue in cues.OrderBy(cue => cue.StartTicks).ThenBy(cue => cue.Order))
            output.Append(cue.Block).Append("\n\n");
        byte[] result = new UTF8Encoding(false).GetBytes(output.ToString());
        if (result.LongLength > MaxOutputBytes) throw new MediaContainerException("The assembled subtitle file exceeds the supported size.");
        return result;
    }

    private async Task<(string Text, Uri Base)> FetchTextAsync(string url, long limit, CancellationToken ct)
    {
        if (!UrlPolicy.TryParse(url, out Uri uri, out string error)) throw new RunnerStop(RunOutcome.NeedsLink("bad-url", error));
        using HttpResponseMessage response = await http.SendAsync(uri, Spec(), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw StatusStop(response);
        if (!HttpFacts.IsIdentityEncoded(response)) throw new RunnerStop(NeedsAttention("subtitle-encoding", "The server compressed an HLS subtitle playlist."));
        byte[] body = await ReadBoundedAsync(response, limit, ct).ConfigureAwait(false);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(body); }
        catch (DecoderFallbackException) { throw new RunnerStop(NeedsAttention("subtitle-encoding", "The HLS subtitle playlist is not valid UTF-8.")); }
        return (text, response.RequestMessage?.RequestUri ?? uri);
    }

    private async Task<byte[]> FetchSegmentAsync(HlsSegment segment, CancellationToken ct)
    {
        if (!UrlPolicy.TryParse(segment.Uri, out Uri uri, out string error))
            throw new RunnerStop(NeedsAttention("subtitle-url", error));
        long? end = null;
        if (segment.RangeOffset is long start)
        {
            if (segment.RangeLength is not long length || length <= 0 || length > MaxSegmentBytes || start < 0 || start > long.MaxValue - length)
                throw new RunnerStop(NeedsAttention("subtitle-range", "The playlist contains an invalid byte range."));
            end = start + length - 1;
        }
        using HttpResponseMessage response = await http.SendAsync(uri,
            Spec(segment.RangeOffset, end), ct).ConfigureAwait(false);
        int status = (int)response.StatusCode;
        if (status is not (200 or 206)) throw StatusStop(response);
        if (segment.RangeOffset is long rangeStart)
        {
            RangeCheck check = HttpFacts.CheckRangeResponse(response, rangeStart, end);
            if (!check.IsValid || check.EndInclusive != end || !HttpFacts.IsIdentityEncoded(response))
                throw new RunnerStop(NeedsAttention("subtitle-range", "The server returned an unsafe byte-range subtitle segment."));
        }
        else if (status == 206) throw new RunnerStop(NeedsAttention("subtitle-range", "The server returned only part of a subtitle segment."));
        if (!HttpFacts.IsIdentityEncoded(response)) throw new RunnerStop(NeedsAttention("subtitle-encoding", "The server compressed an HLS subtitle segment."));
        if (HttpFacts.IsHtml(response)) throw new RunnerStop(NeedsAttention("subtitle-html", "The server sent a web page instead of a subtitle segment."));
        byte[] bytes = await ReadBoundedAsync(response, MaxSegmentBytes, ct).ConfigureAwait(false);
        if (segment.RangeLength is long expected && bytes.LongLength != expected)
            throw new TransientNetworkException("An HLS subtitle byte-range response was incomplete.");
        return bytes;
    }

    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit)
            throw new RunnerStop(NeedsAttention("subtitle-size", "An HLS subtitle playlist or segment is unreasonably large."));
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(engine.Options.StallTimeout);
        try
        {
            await using Stream body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            long? declaredLength = response.Content.Headers.ContentLength;
            using var buffer = new MemoryStream(declaredLength is > 0 and <= int.MaxValue
                ? (int)declaredLength.Value : 16 * 1024);
            byte[] chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, stall.Token).ConfigureAwait(false)) > 0)
            {
                stall.CancelAfter(engine.Options.StallTimeout);
                if (buffer.Length + read > limit)
                    throw new RunnerStop(NeedsAttention("subtitle-size", "An HLS subtitle playlist or segment is unreasonably large."));
                buffer.Write(chunk, 0, read);
            }
            if (response.Content.Headers.ContentLength is long declared && buffer.Length != declared)
                throw new TransientNetworkException("An HLS subtitle response ended early.");
            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TransientNetworkException("An HLS subtitle response stalled."); }
    }

    private RequestSpec Spec(long? start = null, long? end = null)
    {
        RequestCredentials? credentials = engine.CredentialsFor(Job.Id);
        return new RequestSpec(start, end, Referer: Job.Referer, UserAgent: Job.UserAgent,
            Secrets: credentials?.Headers, CredentialOrigin: credentials?.Origin.AbsoluteUri ?? Job.SourceUrl);
    }

    private static RunnerStop StatusStop(HttpResponseMessage response)
    {
        int code = (int)response.StatusCode;
        return new RunnerStop(code switch
        {
            401 or 403 or 404 or 410 => RunOutcome.NeedsLink("expired", $"The subtitle address no longer works (HTTP {code}). Saved data was kept."),
            408 or 429 or >= 500 => RunOutcome.Retry("server-error", $"The subtitle server had a problem (HTTP {code}).",
                HttpFacts.RetryAfter(response, DateTimeOffset.UtcNow)),
            _ => RunOutcome.Failed("http-status", $"The subtitle server answered HTTP {code}.")
        });
    }

    private void EnsureSpace(long additional)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(Job.PartialPath));
        if (root is not null && new DriveInfo(root) is { IsReady: true } drive
            && drive.AvailableFreeSpace < additional + engine.Options.FreeSpaceReserve)
            throw new RunnerStop(NeedsAttention("disk-full", "There is not enough disk space to save the subtitles. Saved data was kept."));
    }

    private Cursor? ReadCursor()
    {
        if (string.IsNullOrEmpty(Job.MediaState)) return null;
        try { return JsonSerializer.Deserialize<Cursor>(Job.MediaState, new JsonSerializerOptions { MaxDepth = 8 }); }
        catch (JsonException) { return null; }
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static string Fingerprint(string plan) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan)));
    private static long UnwrapTimestamp(ulong timestamp, long? previous)
    {
        const long modulus = 1L << 33, half = modulus / 2;
        if (timestamp >= (ulong)modulus) throw new MediaContainerException("An HLS subtitle timestamp exceeds the MPEG 33-bit range.");
        if (previous is null) return (long)timestamp;
        long raw = (long)timestamp, delta = raw - (previous.Value % modulus);
        if (delta > half) delta -= modulus;
        else if (delta < -half) delta += modulus;
        return checked(previous.Value + delta);
    }

    private static RunOutcome NeedsAttention(string reason, string message) => RunOutcome.NeedsAttention(reason, message);

    private sealed record Cue(long StartTicks, long EndTicks, int Order, string Block);

    private sealed record ParsedWebVtt(IReadOnlyList<string> Metadata, IReadOnlyList<string> Styles,
        IReadOnlyList<Cue> Cues, ulong? MpegTimestamp, long LocalTicks);

    private static class WebVtt
    {
        internal static byte[] ToSubRip(byte[] bytes)
        {
            ParsedWebVtt parsed = Parse(bytes);
            var output = new StringBuilder();
            int number = 0;
            foreach (Cue cue in parsed.Cues.OrderBy(cue => cue.StartTicks).ThenBy(cue => cue.Order))
            {
                string[] lines = cue.Block.Split('\n');
                int timestampLine = Array.FindIndex(lines, line => line.Contains("-->", StringComparison.Ordinal));
                if (timestampLine < 0) throw new MediaContainerException("A WebVTT cue lost its timestamps.");
                string[] text = lines[(timestampLine + 1)..];
                if (text.Length == 0) continue;

                output.Append(++number).Append('\n')
                    .Append(FormatSubRipTime(cue.StartTicks)).Append(" --> ")
                    .Append(FormatSubRipTime(cue.EndTicks)).Append('\n');
                foreach (string line in text) output.Append(line).Append('\n');
                output.Append('\n');
            }

            if (number == 0) throw new MediaContainerException("The HLS subtitle playlist contains no cues to convert to SubRip.");
            return new UTF8Encoding(false).GetBytes(output.ToString());
        }

        internal static ParsedWebVtt Parse(byte[] bytes)
        {
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { throw new MediaContainerException("A WebVTT segment is not valid UTF-8."); }
            if (text.Contains('\0')) throw new MediaContainerException("A WebVTT segment contains a NUL character.");
            text = text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            string[] lines = text.Split('\n');
            if (lines.Length == 0 || !(lines[0] == "WEBVTT" || lines[0].StartsWith("WEBVTT ", StringComparison.Ordinal)
                || lines[0].StartsWith("WEBVTT\t", StringComparison.Ordinal)))
                throw new MediaContainerException("A subtitle segment does not begin with a WebVTT header.");

            var metadata = new List<string>();
            ulong? mpegTimestamp = null;
            long localTicks = 0;
            int contentStart = lines.Length;
            bool headerEnded = false;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) { contentStart = i + 1; headerEnded = true; break; }
                if (line.StartsWith("X-TIMESTAMP-MAP=", StringComparison.OrdinalIgnoreCase))
                {
                    if (mpegTimestamp is not null || !TryParseTimestampMap(line, out localTicks, out ulong value))
                        throw new MediaContainerException("A WebVTT timestamp map is invalid or duplicated.");
                    mpegTimestamp = value;
                }
                else metadata.Add(line);
            }
            if (!headerEnded && lines.Length > 1)
                throw new MediaContainerException("A WebVTT header is not terminated by a blank line.");

            var styles = new List<string>();
            var cues = new List<Cue>();
            int index = contentStart;
            while (index < lines.Length)
            {
                while (index < lines.Length && lines[index].Length == 0) index++;
                if (index >= lines.Length) break;
                int start = index;
                while (index < lines.Length && lines[index].Length > 0) index++;
                string block = string.Join('\n', lines[start..index]);
                string first = lines[start];
                if (first == "STYLE" || first == "REGION") styles.Add(block);
                else if (first == "NOTE" || first.StartsWith("NOTE ", StringComparison.Ordinal)) { }
                else
                {
                    int timestampLine = first.Contains("-->", StringComparison.Ordinal) ? start : start + 1;
                    if (timestampLine >= index || !TryParseCueTime(lines[timestampLine], out long cueStart, out long cueEnd, out string suffix))
                        throw new MediaContainerException("A WebVTT cue has invalid timestamps.");
                    string[] cueLines = lines[start..index];
                    cueLines[timestampLine - start] = FormatTime(cueStart) + " --> " + FormatTime(cueEnd) + suffix;
                    string cueBlock = string.Join('\n', cueLines);
                    if (cueEnd <= cueStart) throw new MediaContainerException("A WebVTT cue ends before it starts.");
                    cues.Add(new Cue(cueStart, cueEnd, cues.Count, cueBlock));
                }
            }
            return new ParsedWebVtt(metadata, styles, cues, mpegTimestamp, localTicks);
        }

        private static string FormatSubRipTime(long ticks)
        {
            long hours = ticks / TimeSpan.TicksPerHour;
            int minutes = (int)(ticks / TimeSpan.TicksPerMinute % 60);
            int seconds = (int)(ticks / TimeSpan.TicksPerSecond % 60);
            int milliseconds = (int)(ticks % TimeSpan.TicksPerSecond / TimeSpan.TicksPerMillisecond);
            return FormattableString.Invariant($"{hours:00}:{minutes:00}:{seconds:00},{milliseconds:000}");
        }

        internal static string FormatCue(Cue cue, long start, long end)
        {
            string[] lines = cue.Block.Split('\n');
            int timestampLine = Array.FindIndex(lines, line => line.Contains("-->"));
            if (timestampLine < 0) throw new MediaContainerException("A WebVTT cue lost its timestamps.");
            string original = lines[timestampLine];
            int arrow = original.IndexOf("-->", StringComparison.Ordinal);
            int suffixStart = arrow + 3;
            while (suffixStart < original.Length && char.IsWhiteSpace(original[suffixStart])) suffixStart++;
            int setting = original.IndexOfAny([' ', '\t'], suffixStart);
            string suffix = setting < 0 ? "" : original[setting..];
            lines[timestampLine] = FormatTime(start) + " --> " + FormatTime(end) + suffix;
            return string.Join('\n', lines);
        }

        private static bool TryParseTimestampMap(string line, out long localTicks, out ulong mpegTimestamp)
        {
            localTicks = 0; mpegTimestamp = 0;
            string value = line["X-TIMESTAMP-MAP=".Length..];
            string[] parts = value.Split(',');
            if (parts.Length != 2 || !parts[0].StartsWith("LOCAL:", StringComparison.Ordinal)
                || !parts[1].StartsWith("MPEGTS:", StringComparison.Ordinal)
                || !TryParseTime(parts[0][6..], out localTicks)
                || !ulong.TryParse(parts[1][7..], NumberStyles.None, CultureInfo.InvariantCulture, out mpegTimestamp)
                || mpegTimestamp >= (1UL << 33)) return false;
            return true;
        }

        private static bool TryParseCueTime(string line, out long start, out long end, out string suffix)
        {
            start = end = 0; suffix = "";
            int arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow <= 0 || line.IndexOf("-->", arrow + 3, StringComparison.Ordinal) >= 0) return false;
            string left = line[..arrow].Trim();
            string right = line[(arrow + 3)..].TrimStart();
            int space = right.IndexOfAny([' ', '\t']);
            string endTime = space < 0 ? right : right[..space];
            suffix = space < 0 ? "" : right[space..];
            return TryParseTime(left, out start) && TryParseTime(endTime, out end);
        }

        private static bool TryParseTime(string value, out long ticks)
        {
            ticks = 0;
            string[] clock = value.Split(':');
            if (clock.Length is not (2 or 3)) return false;
            string seconds = clock[^1];
            int dot = seconds.IndexOf('.');
            if (dot < 0 || seconds.Length - dot != 4 || !int.TryParse(seconds[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int millis)) return false;
            string secondPart = seconds[..dot];
            string minutePart = clock[^2];
            if (secondPart.Length != 2 || minutePart.Length != 2 || !int.TryParse(secondPart, NumberStyles.None, CultureInfo.InvariantCulture, out int second)
                || !int.TryParse(minutePart, NumberStyles.None, CultureInfo.InvariantCulture, out int minute) || second > 59 || minute > 59
                || clock.Length == 3 && (!long.TryParse(clock[0], NumberStyles.None, CultureInfo.InvariantCulture, out long hours) || hours > 8760)) return false;
            long hourCount = clock.Length == 3 ? long.Parse(clock[0], CultureInfo.InvariantCulture) : 0;
            ticks = checked((((hourCount * 60 + minute) * 60 + second) * TimeSpan.TicksPerSecond) + millis * TimeSpan.TicksPerMillisecond);
            return ticks <= TimeSpan.FromDays(365).Ticks;
        }

        private static string FormatTime(long ticks)
        {
            TimeSpan time = TimeSpan.FromTicks(ticks);
            long hours = (long)time.TotalHours;
            return string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}");
        }
    }
}
