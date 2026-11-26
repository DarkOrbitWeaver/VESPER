using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Protocols.Dash;

/// <summary>SegmentTemplate / SegmentList expansion into explicit segment URLs (ISO/IEC 23009-1 section 5.3.9).</summary>
internal sealed class DashTemplate
{
    public string? Media { get; init; }
    public string? Initialization { get; init; }
    public long StartNumber { get; init; } = 1;
    public long Timescale { get; init; } = 1;
    public long Duration { get; init; }
    public long PresentationTimeOffset { get; init; }
    public List<(long? T, long D, int R)>? Timeline { get; init; }

    /// <summary>Merges inherited templates from outermost to innermost; child attributes/timelines override parents.</summary>
    public static DashTemplate? Merge(params XElement?[] templates)
    {
        var levels = templates.Where(t => t is not null).Reverse().ToArray();
        if (levels.Length == 0) return null;
        string? A(string n) => levels.Select(t => t?.Attribute(n)?.Value).FirstOrDefault(v => v is not null);
        var timelineEl = levels.Select(t => t!.Elements().FirstOrDefault(e => e.Name.LocalName == "SegmentTimeline")).FirstOrDefault(t => t is not null);
        var timeline = ReadTimeline(timelineEl);
        if (timeline is not null && A("duration") is not null)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentTemplate cannot combine duration with SegmentTimeline"));

        return new DashTemplate
        {
            Media = A("media"),
            Initialization = A("initialization"),
            StartNumber = ReadUnsignedAttribute(A("startNumber"), 1, "startNumber", allowZero: true, maxValue: uint.MaxValue),
            Timescale = ReadUnsignedAttribute(A("timescale"), 1, "timescale", allowZero: false, maxValue: uint.MaxValue),
            Duration = ReadUnsignedAttribute(A("duration"), 0, "duration", allowZero: false, maxValue: uint.MaxValue),
            PresentationTimeOffset = ReadUnsignedAttribute(A("presentationTimeOffset"), 0, "presentationTimeOffset", allowZero: true),
            Timeline = timeline
        };
    }

    internal static List<(long? T, long D, int R)>? ReadTimeline(XElement? timelineElement)
    {
        if (timelineElement is null) return null;

        var timeline = new List<(long? T, long D, int R)>();
        foreach (var element in timelineElement.Elements().Where(e => e.Name.LocalName == "S"))
        {
            if (element.Attribute("n") is not null || element.Attribute("k") is not null)
                throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentTimeline sequence attributes are not supported"));

            string? startText = element.Attribute("t")?.Value;
            long? start = null;
            if (startText is not null)
            {
                if (!long.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedStart) || parsedStart < 0)
                    throw new MediaResolverException(MediaErrors.UnsupportedManifest("Invalid SegmentTimeline start time"));
                start = parsedStart;
            }

            if (!long.TryParse(element.Attribute("d")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration) || duration <= 0)
                throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentTimeline entry without a valid duration"));

            string? repeatText = element.Attribute("r")?.Value;
            int repeat = 0;
            if (repeatText is not null &&
                (!int.TryParse(repeatText, NumberStyles.Integer, CultureInfo.InvariantCulture, out repeat) || repeat < -1))
                throw new MediaResolverException(MediaErrors.UnsupportedManifest("Invalid SegmentTimeline repeat count"));

            timeline.Add((start, duration, repeat));
            if (timeline.Count > 200_000)
                throw new MediaResolverException(MediaErrors.TooLarge("SegmentTimeline too long"));
        }

        if (timeline.Count == 0)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentTimeline contains no S entries"));
        return timeline;
    }

    private static long ReadUnsignedAttribute(string? value, long defaultValue, string name, bool allowZero,
        long maxValue = long.MaxValue)
    {
        if (value is null) return defaultValue;
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < (allowZero ? 0 : 1) || parsed > maxValue)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest($"Invalid SegmentTemplate @{name}"));
        return parsed;
    }

    public SegmentPlan Expand(string repId, long bandwidth, Uri baseUrl, double? periodDurationSeconds,
        int maxSegments, bool fragmentedMp4, double periodStartSeconds = 0)
    {
        if (Media is null)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentTemplate without a media attribute"));

        Uri? init = null;
        if (Initialization is not null)
            init = ResolveUrl(baseUrl, Substitute(Initialization, repId, StartNumber, bandwidth, 0));

        var segments = new List<MediaSegment>();
        double total = 0;
        bool discontinuities = false;

        if (Timeline is not null)
        {
            long time = 0;
            bool first = true;
            long? previousEndTime = null;
            for (int entry = 0; entry < Timeline.Count; entry++)
            {
                var (t, d, r) = Timeline[entry];
                if (t is { } tv)
                {
                    if (previousEndTime is long previous)
                    {
                        if (tv < previous)
                            throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentTimeline entries overlap"));
                        if (tv > previous) discontinuities = true;
                    }
                    time = tv;
                }
                else if (first) time = 0;
                first = false;

                long repeat = r;
                if (repeat < 0)
                {
                    long? nextTime = entry + 1 < Timeline.Count ? Timeline[entry + 1].T : null;
                    if (entry + 1 < Timeline.Count && nextTime is null)
                        throw new MediaResolverException(MediaErrors.UnsupportedManifest("An open repeat needs the next entry's explicit start time"));
                    if (nextTime is null && periodDurationSeconds is null)
                        throw new MediaResolverException(MediaErrors.UnsupportedManifest("Open-ended S@r without a next start time or period duration"));
                    double boundary = nextTime ?? PresentationTimeOffset + Math.Ceiling(periodDurationSeconds!.Value * Timescale);
                    if (!double.IsFinite(boundary) || boundary > long.MaxValue || boundary <= time)
                        throw new MediaResolverException(MediaErrors.UnsupportedManifest("Invalid open-repeat boundary"));
                    long distance = (long)boundary - time;
                    if (nextTime is not null && distance % d != 0)
                        throw new MediaResolverException(MediaErrors.UnsupportedManifest("The next timeline entry overlaps an open repeat"));
                    repeat = distance / d + (distance % d == 0 ? 0 : 1) - 1;
                }
                for (long k = 0; k <= repeat; k++)
                {
                    if (segments.Count >= maxSegments)
                        throw new MediaResolverException(MediaErrors.TooLarge("Representation exceeds the segment limit"));
                    long number;
                    long segmentEnd;
                    try
                    {
                        number = checked(StartNumber + segments.Count);
                        segmentEnd = checked(time + d);
                    }
                    catch (OverflowException)
                    {
                        throw new MediaResolverException(MediaErrors.UnsupportedManifest("Timeline sequence or time overflows"));
                    }
                    var url = ResolveUrl(baseUrl, Substitute(Media, repId, number, bandwidth, time));
                    double dur = (double)d / Timescale;
                    double start = periodStartSeconds + ((double)time - PresentationTimeOffset) / Timescale;
                    if (!double.IsFinite(start) || start < periodStartSeconds || !double.IsFinite(dur) || dur <= 0)
                        throw new MediaResolverException(MediaErrors.UnsupportedManifest("Invalid segment presentation time"));
                    segments.Add(new MediaSegment(url, dur, StartSeconds: start));
                    total += dur;
                    time = segmentEnd;
                }
                previousEndTime = time;
            }
        }
        else if (Duration > 0)
        {
            if (periodDurationSeconds is null || !double.IsFinite(periodDurationSeconds.Value) || periodDurationSeconds.Value <= 0)
                throw new MediaResolverException(MediaErrors.UnsupportedManifest("Duration-based template without a known period duration"));
            double periodDuration = periodDurationSeconds.Value;
            double segSeconds = (double)Duration / Timescale;
            double countValue = Math.Ceiling(periodDuration / segSeconds);
            if (!double.IsFinite(countValue) || countValue > maxSegments)
                throw new MediaResolverException(MediaErrors.TooLarge("Representation exceeds the segment limit"));
            long count = (long)countValue;
            for (long i = 0; i < count; i++)
            {
                long time;
                long number;
                try
                {
                    time = checked(PresentationTimeOffset + checked(i * Duration));
                    number = checked(StartNumber + i);
                }
                catch (OverflowException)
                {
                    throw new MediaResolverException(MediaErrors.UnsupportedManifest("Duration template time or sequence overflows"));
                }
                double dur = Math.Min(segSeconds, periodDuration - i * segSeconds);
                double start = periodStartSeconds + ((double)time - PresentationTimeOffset) / Timescale;
                if (!double.IsFinite(dur) || dur <= 0 || !double.IsFinite(start) || start < 0)
                    throw new MediaResolverException(MediaErrors.UnsupportedManifest("Invalid duration-based segment timing"));
                segments.Add(new MediaSegment(ResolveUrl(baseUrl, Substitute(Media, repId, number, bandwidth, time)),
                    dur, StartSeconds: start));
                total += dur;
            }
        }
        else
        {
            // No timeline and no duration: the template describes exactly one segment.
            if (periodDurationSeconds is null || !double.IsFinite(periodDurationSeconds.Value) || periodDurationSeconds.Value <= 0)
                throw new MediaResolverException(MediaErrors.UnsupportedManifest("A single-segment template needs a known period duration"));
            if (maxSegments < 1)
                throw new MediaResolverException(MediaErrors.TooLarge("Representation exceeds the segment limit"));
            double periodDuration = periodDurationSeconds.Value;
            segments.Add(new MediaSegment(ResolveUrl(baseUrl, Substitute(Media, repId, StartNumber, bandwidth, PresentationTimeOffset)),
                periodDuration, StartSeconds: periodStartSeconds));
            total = periodDuration;
        }

        return new SegmentPlan { InitializationUrl = init, Segments = segments, TotalDurationSeconds = total,
            IsFragmentedMp4 = fragmentedMp4, HasDiscontinuities = discontinuities };
    }

    /// <summary>Handles $$, $RepresentationID$, $Number$, $Bandwidth$, $Time$ with optional %0Nd width.</summary>
    internal static string Substitute(string template, string repId, long number, long bandwidth, long time)
    {
        var sb = new StringBuilder(template.Length + 16);
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c != '$') { sb.Append(c); i++; continue; }

            int end = template.IndexOf('$', i + 1);
            if (end < 0) throw new MediaResolverException(MediaErrors.UnsupportedManifest("Unterminated $identifier$ in template"));
            string token = template.Substring(i + 1, end - i - 1);
            i = end + 1;
            if (token.Length == 0) { sb.Append('$'); continue; }

            string? format = null;
            int pct = token.IndexOf('%');
            if (pct >= 0) { format = token[pct..]; token = token[..pct]; }

            switch (token)
            {
                case "RepresentationID": sb.Append(repId); break;
                case "Number": sb.Append(FormatNumber(number, format)); break;
                case "Bandwidth": sb.Append(FormatNumber(bandwidth, format)); break;
                case "Time": sb.Append(FormatNumber(time, format)); break;
                default:
                    throw new MediaResolverException(MediaErrors.UnsupportedManifest($"Unknown template identifier '{token}'"));
            }
        }
        return sb.ToString();
    }

    private static string FormatNumber(long value, string? format)
    {
        string s = value.ToString(CultureInfo.InvariantCulture);
        if (format is null) return s;
        // "%05d" -> pad to 5 with zeros
        if (format.Length >= 3 && format[0] == '%' && format[^1] == 'd' &&
            int.TryParse(format.AsSpan(1, format.Length - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) &&
            width is >= 0 and <= 32)
            return s.PadLeft(width, '0');
        throw new MediaResolverException(MediaErrors.UnsupportedManifest($"Unsupported identifier format '{format}'"));
    }

    internal static Uri ResolveUrl(Uri baseUrl, string relative) =>
        UrlUtil.Resolve(baseUrl, relative)
        ?? throw new MediaResolverException(MediaErrors.UnsupportedManifest("Segment URL resolves to a non-http(s) address"));
}
