using System.Buffers.Binary;
using System.Text;
using System.Net;

namespace Vesper.Engine;

internal sealed record ParsedSubtitle(DemuxedTrack Track, MemoryStream Source);

internal static class TextSubtitleParser
{
    internal const int MaxBytes = 2 * 1024 * 1024;
    private const int MaxCues = 20_000;

    private sealed record Cue(long StartMilliseconds, long DurationMilliseconds, byte[] Text);

    internal static ParsedSubtitle Parse(ReadOnlySpan<byte> bytes, string format, string language, string name,
        double? mediaDurationSeconds, string outputContainer = "mkv", int videoWidth = 0, int videoHeight = 0)
    {
        if (bytes.Length is <= 0 or > MaxBytes)
            throw new MediaContainerException("A selected subtitle file is empty or larger than 2 MiB.");
        if (outputContainer is not ("mkv" or "mp4"))
            throw new MediaContainerException("Embedded subtitles require Matroska or MP4 output.");
        bool mp4 = outputContainer == "mp4";
        if (mp4 && (videoWidth <= 0 || videoHeight <= 0))
            throw new MediaContainerException("MP4 subtitle embedding needs a known video size.");

        string text;
        try
        {
            ReadOnlySpan<byte> content = bytes;
            if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
                content = content[3..];
            if (content.Length >= 2 && (content[0] == 0xFF && content[1] == 0xFE
                || content[0] == 0xFE && content[1] == 0xFF))
                throw new MediaContainerException("Embedded subtitles must use UTF-8 text.");
            text = new UTF8Encoding(false, true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            throw new MediaContainerException("The selected subtitle file is not valid UTF-8.");
        }

        if (text.Any(character => character == '\0'
            || char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new MediaContainerException("The selected subtitle file contains unsupported control characters.");

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        format = format.ToLowerInvariant();
        bool webVtt = format == "vtt";
        if (format is not ("srt" or "vtt"))
            throw new MediaContainerException("Only SRT and WebVTT subtitles can be embedded.");
        if (webVtt && (lines.Length == 0 || !lines[0].StartsWith("WEBVTT", StringComparison.Ordinal)
            || lines[0].Length > 6 && lines[0][6] is not (' ' or '\t')))
            throw new MediaContainerException("The selected WebVTT file has no valid WEBVTT header.");

        int lineIndex = webVtt ? 1 : 0;
        var cues = new List<Cue>();
        long previousStart = -1;
        long previousEnd = -1;
        while (lineIndex < lines.Length)
        {
            while (lineIndex < lines.Length && string.IsNullOrWhiteSpace(lines[lineIndex])) lineIndex++;
            if (lineIndex >= lines.Length) break;

            string first = lines[lineIndex].Trim();
            if (webVtt && (first == "NOTE" || first.StartsWith("NOTE ", StringComparison.Ordinal)
                || first.StartsWith("NOTE\t", StringComparison.Ordinal)))
            {
                while (lineIndex < lines.Length && !string.IsNullOrWhiteSpace(lines[lineIndex])) lineIndex++;
                continue;
            }
            if (webVtt && first is "STYLE" or "REGION")
                throw new MediaContainerException("WebVTT styling and region blocks cannot be preserved in embedded subtitles.");

            if (!TryParseTiming(first, webVtt, out long start, out long end))
            {
                lineIndex++;
                if (lineIndex >= lines.Length || !TryParseTiming(lines[lineIndex].Trim(), webVtt, out start, out end))
                    throw new MediaContainerException("A subtitle cue has an invalid timestamp line.");
                lineIndex++;
            }
            else lineIndex++;

            if (start < previousStart || end <= start || end > 365L * 24 * 60 * 60 * 1000
                || mp4 && start < previousEnd)
                throw new MediaContainerException("Subtitle cue times must be ordered, positive, and no longer than one year.");
            if (mediaDurationSeconds is { } duration && end > duration * 1000 + 2000)
                throw new MediaContainerException("A subtitle cue extends beyond the media duration.");

            int textStart = lineIndex;
            while (lineIndex < lines.Length && !string.IsNullOrWhiteSpace(lines[lineIndex])) lineIndex++;
            if (textStart == lineIndex)
                throw new MediaContainerException("A subtitle cue has no text.");
            string cueText = string.Join('\n', lines[textStart..lineIndex]).Trim();
            if (webVtt)
            {
                if (HasUnsupportedMarkup(cueText) || mp4 && cueText.Contains('<'))
                    throw new MediaContainerException("This subtitle cue uses styling that cannot be preserved in the selected output.");
                cueText = WebUtility.HtmlDecode(cueText) ?? "";
            }
            else if (HasUnsupportedMarkup(cueText) || mp4 && cueText.Contains('<'))
                throw new MediaContainerException("This subtitle cue uses styling that cannot be preserved in the selected output.");
            if (cueText.Any(character => character == '\0'
                || char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
                throw new MediaContainerException("A subtitle cue contains unsupported control characters.");
            byte[] cueBytes = Encoding.UTF8.GetBytes(cueText);
            if (cueBytes.Length is <= 0 or > 64 * 1024)
                throw new MediaContainerException("A subtitle cue is empty or too large.");
            cues.Add(new Cue(start, end - start, cueBytes));
            if (cues.Count > MaxCues)
                throw new MediaContainerException("The selected subtitle file has too many cues.");
            previousStart = start;
            previousEnd = end;
        }

        if (cues.Count == 0)
            throw new MediaContainerException("The selected subtitle file has no readable cues.");

        string outputLanguage = mp4 ? Mp4LanguageCode(language) : language;
        var source = new MemoryStream(Math.Min(bytes.Length + cues.Count * 2, MaxBytes * 2));
        var track = new DemuxedTrack
        {
            Kind = TrackKind.Subtitle,
            Codec = mp4 ? "tx3g" : "utf8",
            SourceContainer = mp4 ? "mp4" : "subtitle",
            MatroskaCodecId = mp4 ? null : "S_TEXT/UTF8",
            Mp4SampleEntry = mp4 ? Mp4Muxer.Tx3gSubtitleSampleEntry(videoWidth, videoHeight) : null,
            Width = videoWidth,
            Height = videoHeight,
            Language = outputLanguage,
            Name = name,
            Timescale = 1000
        };
        for (int index = 0; index < cues.Count; index++)
        {
            Cue cue = cues[index];
            byte[] sample = mp4 ? Tx3gSample(cue.Text) : cue.Text;
            AddSample(source, track, sample, cue.StartMilliseconds, cue.DurationMilliseconds);

            if (!mp4) continue;
            long cueEnd = cue.StartMilliseconds + cue.DurationMilliseconds;
            long nextStart = index + 1 < cues.Count ? cues[index + 1].StartMilliseconds
                : mediaDurationSeconds is { } duration ? (long)Math.Round(duration * 1000) : cueEnd;
            if (cueEnd < nextStart)
                AddSample(source, track, [0, 0], cueEnd, nextStart - cueEnd);
        }
        source.Position = 0;
        return new ParsedSubtitle(track, source);
    }

    private static bool TryParseTiming(string line, bool webVtt, out long start, out long end)
    {
        start = end = 0;
        int arrow = line.IndexOf("-->", StringComparison.Ordinal);
        if (arrow <= 0) return false;
        string left = line[..arrow].Trim();
        string right = line[(arrow + 3)..].Trim();
        int settings = right.IndexOfAny([' ', '\t']);
        if (settings >= 0)
        {
            if (webVtt) return false;
            right = right[..settings];
        }
        if (!TryParseTime(left, webVtt, out start) || !TryParseTime(right, webVtt, out end)) return false;
        return end > start;
    }

    private static bool HasUnsupportedMarkup(string text)
    {
        int position = 0;
        while ((position = text.IndexOf('<', position)) >= 0)
        {
            int end = text.IndexOf('>', position + 1);
            if (end < 0) return true;
            string tag = text[(position + 1)..end].Trim().TrimStart('/');
            if (tag is not ("b" or "i" or "u")) return true;
            position = end + 1;
        }
        return false;
    }

    private static bool TryParseTime(string value, bool webVtt, out long milliseconds)
    {
        milliseconds = 0;
        string[] clock = value.Split(':');
        if (webVtt ? clock.Length is not (2 or 3) : clock.Length != 3) return false;
        string[] seconds = clock[^1].Split(webVtt ? '.' : ',');
        if (seconds.Length != 2 || seconds[1].Length is < 1 or > 3
            || !ParseUnsigned(seconds[0], out long wholeSeconds)
            || !ParseUnsigned(seconds[1].PadRight(3, '0'), out long fraction)
            || wholeSeconds > 59)
            return false;

        long hours = 0;
        long minutes;
        if (clock.Length == 3)
        {
            if (!ParseUnsigned(clock[0], out hours) || !ParseUnsigned(clock[1], out minutes) || minutes > 59)
                return false;
        }
        else
        {
            if (!ParseUnsigned(clock[0], out minutes) || minutes > 59) return false;
        }
        try
        {
            milliseconds = checked(((hours * 60 + minutes) * 60 + wholeSeconds) * 1000 + fraction);
            return true;
        }
        catch (OverflowException) { return false; }
    }

    private static bool ParseUnsigned(string value, out long parsed) =>
        long.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out parsed);

    private static void AddSample(MemoryStream source, DemuxedTrack track, byte[] bytes, long start, long duration)
    {
        if (duration is <= 0 or > int.MaxValue)
            throw new MediaContainerException("A subtitle cue duration cannot be represented in the selected output.");
        long offset = source.Position;
        source.Write(bytes);
        track.Samples.Add(new TrackSample
        {
            Offset = offset,
            Size = bytes.Length,
            Dts = start,
            Duration = (int)duration,
            Key = true
        });
    }

    private static byte[] Tx3gSample(byte[] text)
    {
        if (text.Length > 2048 || text.Length > ushort.MaxValue)
            throw new MediaContainerException("A subtitle cue is too long for MP4 text samples.");
        byte[] sample = new byte[text.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(sample, (ushort)text.Length);
        text.CopyTo(sample, 2);
        return sample;
    }

    private static string Mp4LanguageCode(string language)
    {
        string primary = language.Split('-')[0];
        if (primary.Length == 3) return primary.ToLowerInvariant();
        try { return System.Globalization.CultureInfo.GetCultureInfo(primary).ThreeLetterISOLanguageName.ToLowerInvariant(); }
        catch (System.Globalization.CultureNotFoundException) { return "und"; }
    }
}
