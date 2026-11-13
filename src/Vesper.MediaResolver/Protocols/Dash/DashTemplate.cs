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

        List<(long?, long, int)>? timeline = null;
        if (timelineEl is not null)
        {
            timeline = new();
            foreach (var s in timelineEl.Elements().Where(e => e.Name.LocalName == "S"))
            {
                long? t = long.TryParse(s.Attribute("t")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tv) ? tv : null;
                if (!long.TryParse(s.Attribute("d")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) || d <= 0)
                    throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentTimeline entry without a valid duration"));
                int r = int.TryParse(s.Attribute("r")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rv) ? rv : 0;
                if (r < -1 || t < 0) throw new MediaResolverException(MediaErrors.UnsupportedManifest("Invalid negative timeline time/repeat count"));
                timeline.Add((t, d, r));
                if (timeline.Count > 200_000) throw new MediaResolverException(MediaErrors.TooLarge("SegmentTimeline too long"));
            }
        }

        return new DashTemplate
        {
            Media = A("media"),
            Initialization = A("initialization"),
            StartNumber = long.TryParse(A("startNumber"), out var sn) ? sn : 1,
            Timescale = long.TryParse(A("timescale"), out var ts) && ts > 0 ? ts : 1,
            Duration = long.TryParse(A("duration"), out var du) ? du : 0,
            PresentationTimeOffset = long.TryParse(A("presentationTimeOffset"), out var pto) ? pto : 0,
            Timeline = timeline
        };
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
        long number = StartNumber;
        bool discontinuities = false;

        if (Timeline is not null)
        {
            long time = PresentationTimeOffset;
            bool first = true;
            long? previousEndTime = null;
            for (int entry = 0; entry < Timeline.Count; entry++)
            {
                var (t, d, r) = Timeline[entry];
                if (t is { } tv)
                {
                    if (previousEndTime is long previous && tv != previous) discontinuities = true;
                    time = tv;
                }
                else if (first) time = PresentationTimeOffset;
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
                    var url = ResolveUrl(baseUrl, Substitute(Media, repId, number, bandwidth, time));
                    double dur = (double)d / Timescale;
                    double start = periodStartSeconds + ((double)time - PresentationTimeOffset) / Timescale;
                    if (!double.IsFinite(start) || start < 0 || !double.IsFinite(dur) || dur <= 0)
                        throw new MediaResolverException(MediaErrors.UnsupportedManifest("Invalid segment presentation time"));
                    segments.Add(new MediaSegment(url, dur, StartSeconds: start));
                    total += dur;
                    if (time > long.MaxValue - d || number == long.MaxValue)
                        throw new MediaResolverException(MediaErrors.UnsupportedManifest("Timeline arithmetic overflow"));
                    time += d;
                    number++;
                }
                previousEndTime = time;
            }
        }
        else if (Duration > 0)
        {
            if (periodDurationSeconds is null)
                throw new MediaResolverException(MediaErrors.UnsupportedManifest("Duration-based template without a known period duration"));
            double segSeconds = (double)Duration / Timescale;
            long count = (long)Math.Ceiling(periodDurationSeconds.Value / segSeconds);
            if (count > maxSegments) throw new MediaResolverException(MediaErrors.TooLarge("Representation exceeds the segment limit"));
            for (long i = 0; i < count; i++)
            {
                long time;
                try { time = checked(PresentationTimeOffset + checked(i * Duration)); }
                catch (OverflowException) { throw new MediaResolverException(MediaErrors.UnsupportedManifest("Duration template time overflows")); }
                double dur = Math.Min(segSeconds, periodDurationSeconds.Value - i * segSeconds);
                double start = periodStartSeconds + ((double)time - PresentationTimeOffset) / Timescale;
                segments.Add(new MediaSegment(ResolveUrl(baseUrl, Substitute(Media, repId, StartNumber + i, bandwidth, time)),
                    dur, StartSeconds: start));
                total += dur;
            }
        }
        else
        {
            // No timeline and no duration: the template describes exactly one segment.
            segments.Add(new MediaSegment(ResolveUrl(baseUrl, Substitute(Media, repId, StartNumber, bandwidth, PresentationTimeOffset)),
                periodDurationSeconds ?? 0, StartSeconds: periodStartSeconds));
            total = periodDurationSeconds ?? 0;
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
