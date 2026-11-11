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

    /// <summary>Merges AdaptationSet-level and Representation-level templates (child attributes override).</summary>
    public static DashTemplate? Merge(XElement? parent, XElement? child)
    {
        if (parent is null && child is null) return null;
        string? A(string n) => child?.Attribute(n)?.Value ?? parent?.Attribute(n)?.Value;
        var timelineEl = child?.Elements().FirstOrDefault(e => e.Name.LocalName == "SegmentTimeline")
                         ?? parent?.Elements().FirstOrDefault(e => e.Name.LocalName == "SegmentTimeline");

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

    public SegmentPlan Expand(string repId, long bandwidth, Uri baseUrl, double? periodDurationSeconds, int maxSegments, bool fragmentedMp4)
    {
        if (Media is null)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentTemplate without a media attribute"));

        Uri? init = null;
        if (Initialization is not null)
            init = ResolveUrl(baseUrl, Substitute(Initialization, repId, StartNumber, bandwidth, 0));

        var segments = new List<MediaSegment>();
        double total = 0;
        long number = StartNumber;

        if (Timeline is not null)
        {
            long time = PresentationTimeOffset;
            bool first = true;
            foreach (var (t, d, r) in Timeline)
            {
                if (t is { } tv) time = tv;
                else if (first) time = PresentationTimeOffset;
                first = false;

                long repeat = r;
                if (repeat < 0)
                {
                    if (periodDurationSeconds is null)
                        throw new MediaResolverException(MediaErrors.UnsupportedManifest("Open-ended S@r without a known period duration"));
                    long end = PresentationTimeOffset + (long)Math.Ceiling(periodDurationSeconds.Value * Timescale);
                    repeat = Math.Max(0, (end - time + d - 1) / d - 1);
                }
                for (long k = 0; k <= repeat; k++)
                {
                    if (segments.Count >= maxSegments)
                        throw new MediaResolverException(MediaErrors.TooLarge("Representation exceeds the segment limit"));
                    var url = ResolveUrl(baseUrl, Substitute(Media, repId, number, bandwidth, time));
                    double dur = (double)d / Timescale;
                    segments.Add(new MediaSegment(url, dur));
                    total += dur;
                    time += d;
                    number++;
                }
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
                long time = PresentationTimeOffset + i * Duration;
                double dur = Math.Min(segSeconds, periodDurationSeconds.Value - i * segSeconds);
                segments.Add(new MediaSegment(ResolveUrl(baseUrl, Substitute(Media, repId, StartNumber + i, bandwidth, time)), dur));
                total += dur;
            }
        }
        else
        {
            // No timeline and no duration: the template describes exactly one segment.
            segments.Add(new MediaSegment(ResolveUrl(baseUrl, Substitute(Media, repId, StartNumber, bandwidth, PresentationTimeOffset)),
                periodDurationSeconds ?? 0));
            total = periodDurationSeconds ?? 0;
        }

        return new SegmentPlan { InitializationUrl = init, Segments = segments, TotalDurationSeconds = total, IsFragmentedMp4 = fragmentedMp4 };
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
