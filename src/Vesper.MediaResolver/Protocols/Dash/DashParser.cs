using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Protocols.Dash;

/// <summary>
/// MPD parser. DTDs are prohibited (no XXE / entity expansion), BaseURL chains are resolved with validation,
/// and anything the library cannot express faithfully (multi-period, malformed templates) is a typed failure,
/// never a partially filled result.
/// </summary>
public static class DashParser
{
    public static DashManifest Parse(string xml, Uri manifestUrl, int maxSegments = 50_000)
    {
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 16_000_000
            };
            using var sr = new StringReader(xml);
            using var xr = XmlReader.Create(sr, settings);
            doc = XDocument.Load(xr);
        }
        catch (XmlException ex)
        {
            throw new MediaResolverException(MediaErrors.UnsupportedManifest($"Invalid MPD XML: {ex.Message}"));
        }

        var mpd = doc.Root;
        if (mpd is null || mpd.Name.LocalName != "MPD")
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("Root element is not MPD"));

        bool dynamic = string.Equals(mpd.Attribute("type")?.Value, "dynamic", StringComparison.OrdinalIgnoreCase);
        TimeSpan? mpdDuration = ParseIsoDuration(mpd.Attribute("mediaPresentationDuration")?.Value);

        var periods = Kids(mpd, "Period").ToList();
        if (periods.Count == 0)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("MPD has no Period"));
        if (periods.Count > 1)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest(
                "Multi-period manifests are not supported: periods may use different initialization segments and cannot be safely concatenated"));

        var period = periods[0];
        var mpdBase = ChildBase(manifestUrl, mpd, out bool mpdHasBase);
        var periodBase = ChildBase(mpdBase, period, out bool periodHasBase);
        double periodStartSeconds = (ParseIsoDuration(period.Attribute("start")?.Value) ?? TimeSpan.Zero).TotalSeconds;
        TimeSpan? periodDuration = ParseIsoDuration(period.Attribute("duration")?.Value) ?? mpdDuration;
        double? periodSeconds = periodDuration?.TotalSeconds;

        var schemes = period.Descendants().Where(e => e.Name.LocalName == "ContentProtection")
            .Select(e => e.Attribute("schemeIdUri")?.Value ?? "unknown").Distinct().ToList();

        var reps = new List<DashRepresentationInfo>();
        var subs = new List<SubtitleTrack>();
        var warnings = new List<string>();
        int asIndex = 0;

        foreach (var aset in Kids(period, "AdaptationSet"))
        {
            asIndex++;
            string asKey = aset.Attribute("id")?.Value ?? asIndex.ToString(CultureInfo.InvariantCulture);
            var asBase = ChildBase(periodBase, aset, out bool asHasBase);
            string? asMime = aset.Attribute("mimeType")?.Value;
            string? asCodecs = aset.Attribute("codecs")?.Value;
            string? asLang = aset.Attribute("lang")?.Value;
            bool describes = Kids(aset, "Role").Any(r => r.Attribute("value")?.Value is "description" or "enhanced-audio-intelligibility");

            foreach (var rep in Kids(aset, "Representation"))
            {
                string repId = rep.Attribute("id")?.Value ?? $"rep{reps.Count}";
                string? mime = rep.Attribute("mimeType")?.Value ?? asMime;
                string? codecs = rep.Attribute("codecs")?.Value ?? asCodecs;
                string? ctype = aset.Attribute("contentType")?.Value ?? rep.Attribute("contentType")?.Value ?? mime?.Split('/')[0];
                if (ctype is null)
                {
                    Formats.CodecInfo.Split(codecs, out var cv, out var ca, out _);
                    ctype = cv is not null ? "video" : ca is not null ? "audio" : null;
                }
                if (mime is not null && (mime.StartsWith("application/ttml") || mime.StartsWith("text/"))) ctype = "text";
                if (ctype is not ("video" or "audio" or "text"))
                {
                    warnings.Add($"Skipped representation '{repId}' with unsupported content type '{ctype}'");
                    continue;
                }

                var repBase = ChildBase(asBase, rep, out bool repHasBase);
                bool hasBase = mpdHasBase || periodHasBase || asHasBase || repHasBase;

                if (ctype == "text")
                {
                    if (hasBase && UrlUtil.Extension(repBase) is "vtt" or "srt" or "ttml" or "xml")
                        subs.Add(new SubtitleTrack(repBase, TextUtil.NormalizeLang(asLang), null, UrlUtil.Extension(repBase) == "xml" ? "ttml" : UrlUtil.Extension(repBase)!));
                    else warnings.Add($"Skipped segmented/unknown text track '{repId}'");
                    continue;
                }

                int bandwidth = int.TryParse(rep.Attribute("bandwidth")?.Value, out var bw) ? bw : 0;
                SegmentPlan? plan = null;
                Uri? progressive = null;

                if (!dynamic)
                {
                    bool fmp4 = mime?.Contains("mp4", StringComparison.OrdinalIgnoreCase) == true;
                    var tmpl = DashTemplate.Merge(Kid(mpd, "SegmentTemplate"), Kid(period, "SegmentTemplate"), Kid(aset, "SegmentTemplate"), Kid(rep, "SegmentTemplate"));
                    var list = MergeSegmentLists(Kid(mpd, "SegmentList"), Kid(period, "SegmentList"),
                        Kid(aset, "SegmentList"), Kid(rep, "SegmentList"));
                    if (tmpl is not null && list is not null)
                        throw new MediaResolverException(MediaErrors.UnsupportedManifest("A representation cannot inherit both SegmentTemplate and SegmentList"));
                    if (tmpl is not null)
                        plan = tmpl.Expand(repId, bandwidth, repBase, periodSeconds, maxSegments, fmp4, periodStartSeconds);
                    else if (list is not null)
                        plan = ExpandList(list, repBase, maxSegments, periodSeconds, fmp4, periodStartSeconds);
                    else if (hasBase)
                        progressive = repBase;
                    else
                    {
                        warnings.Add($"Representation '{repId}' has no segment information");
                        continue;
                    }
                }

                string? Attr(string n) => rep.Attribute(n)?.Value ?? aset.Attribute(n)?.Value;
                var channelEl = Kid(rep, "AudioChannelConfiguration") ?? Kid(aset, "AudioChannelConfiguration");

                reps.Add(new DashRepresentationInfo
                {
                    Id = repId,
                    AdaptationSetKey = asKey,
                    ContentType = ctype,
                    MimeType = mime,
                    Codecs = codecs,
                    Bandwidth = bandwidth > 0 ? bandwidth : null,
                    Width = PositiveInt(Attr("width")),
                    Height = PositiveInt(Attr("height")),
                    FrameRate = ParseFrameRate(Attr("frameRate")),
                    AudioSampleRate = PositiveInt(Attr("audioSamplingRate")),
                    Channels = int.TryParse(channelEl?.Attribute("value")?.Value, out var chans) && chans is > 0 and <= 16 ? chans : null,
                    Lang = asLang,
                    IsAudioDescription = describes,
                    ProgressiveUrl = progressive,
                    Segments = plan
                });
            }
        }

        return new DashManifest
        {
            IsDynamic = dynamic,
            Duration = periodDuration,
            PeriodCount = periods.Count,
            IsProtected = schemes.Count > 0,
            ProtectionSchemes = schemes,
            Representations = reps,
            Subtitles = subs,
            Warnings = warnings
        };
    }

    private static SegmentPlan ExpandList(XElement list, Uri baseUrl, int max, double? periodSeconds, bool fmp4,
        double periodStartSeconds)
    {
        long timescale = ReadOptionalLong(list, "timescale", 1, allowZero: false, maxValue: uint.MaxValue);
        long duration = ReadOptionalLong(list, "duration", 0, allowZero: false, maxValue: uint.MaxValue);
        long presentationTimeOffset = ReadOptionalLong(list, "presentationTimeOffset", 0, allowZero: true);
        _ = ReadOptionalLong(list, "startNumber", 1, allowZero: true, maxValue: uint.MaxValue);
        var timeline = DashTemplate.ReadTimeline(Kid(list, "SegmentTimeline"));
        if (timeline is not null && list.Attribute("duration") is not null)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentList cannot combine duration with SegmentTimeline"));

        Uri? init = null;
        ByteRange? initRange = null;
        var initEl = Kid(list, "Initialization");
        if (initEl is not null)
        {
            string? src = initEl.Attribute("sourceURL")?.Value;
            init = src is null ? baseUrl : DashTemplate.ResolveUrl(baseUrl, src);
            initRange = ParseRange(initEl.Attribute("range")?.Value);
        }

        var segmentUrls = Kids(list, "SegmentURL").ToList();
        if (segmentUrls.Count == 0)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentList contains no SegmentURL"));
        if (segmentUrls.Count > max)
            throw new MediaResolverException(MediaErrors.TooLarge("Representation exceeds the segment limit"));

        var timings = new List<(double Start, double Duration)>(segmentUrls.Count);
        double total = 0;
        bool discontinuities = false;
        if (timeline is not null)
        {
            long time = 0;
            bool first = true;
            long? previousEndTime = null;
            for (int entry = 0; entry < timeline.Count; entry++)
            {
                var (startTime, segmentDuration, repeatCount) = timeline[entry];
                if (startTime is { } explicitStart)
                {
                    if (previousEndTime is long previous)
                    {
                        if (explicitStart < previous)
                            throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentTimeline entries overlap"));
                        if (explicitStart > previous) discontinuities = true;
                    }
                    time = explicitStart;
                }
                else if (first)
                {
                    time = 0;
                }
                first = false;

                long repeat = repeatCount;
                if (repeat < 0)
                {
                    long? nextTime = entry + 1 < timeline.Count ? timeline[entry + 1].T : null;
                    if (entry + 1 < timeline.Count && nextTime is null)
                        throw new MediaResolverException(MediaErrors.UnsupportedManifest("An open SegmentTimeline repeat needs the next entry's explicit start time"));

                    if (nextTime is { } nextStart)
                    {
                        if (nextStart <= time)
                            throw new MediaResolverException(MediaErrors.UnsupportedManifest("Invalid open SegmentTimeline repeat boundary"));
                        long distance = nextStart - time;
                        if (distance % segmentDuration != 0)
                            throw new MediaResolverException(MediaErrors.UnsupportedManifest("An open SegmentTimeline repeat does not end at a segment boundary"));
                        repeat = distance / segmentDuration - 1;
                    }
                    else if (periodSeconds is { } periodDuration)
                    {
                        double boundary = presentationTimeOffset + Math.Ceiling(periodDuration * timescale);
                        if (!double.IsFinite(boundary) || boundary >= (double)long.MaxValue || boundary <= time)
                            throw new MediaResolverException(MediaErrors.UnsupportedManifest("Invalid open SegmentTimeline repeat boundary"));
                        long distance = (long)boundary - time;
                        repeat = distance / segmentDuration + (distance % segmentDuration == 0 ? 0 : 1) - 1;
                    }
                    else
                    {
                        int remaining = segmentUrls.Count - timings.Count;
                        if (remaining <= 0)
                            throw new MediaResolverException(MediaErrors.UnsupportedManifest("Open SegmentTimeline repeat exceeds the SegmentURL list"));
                        repeat = remaining - 1;
                    }
                }

                for (long item = 0; item <= repeat; item++)
                {
                    if (timings.Count >= segmentUrls.Count)
                        throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentTimeline has more entries than SegmentURL"));

                    double seconds = (double)segmentDuration / timescale;
                    double start = periodStartSeconds + ((double)time - presentationTimeOffset) / timescale;
                    if (!double.IsFinite(start) || start < periodStartSeconds || !double.IsFinite(seconds) || seconds <= 0)
                        throw new MediaResolverException(MediaErrors.UnsupportedManifest("Invalid SegmentList presentation time"));
                    timings.Add((start, seconds));
                    total += seconds;

                    try { time = checked(time + segmentDuration); }
                    catch (OverflowException)
                    {
                        throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentList timeline time overflows"));
                    }
                }
                previousEndTime = time;
            }

            if (timings.Count != segmentUrls.Count)
                throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentTimeline and SegmentURL counts do not match"));
        }
        else if (duration > 0)
        {
            if (periodSeconds is null || !double.IsFinite(periodSeconds.Value) || periodSeconds.Value <= 0)
                throw new MediaResolverException(MediaErrors.UnsupportedManifest("Duration-based SegmentList needs a known period duration"));

            double segmentSeconds = (double)duration / timescale;
            double expectedCount = Math.Ceiling(periodSeconds.Value / segmentSeconds);
            if (!double.IsFinite(expectedCount) || expectedCount > max)
                throw new MediaResolverException(MediaErrors.TooLarge("Representation exceeds the segment limit"));
            if (expectedCount != segmentUrls.Count)
                throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentList does not cover its period duration"));

            for (int index = 0; index < segmentUrls.Count; index++)
            {
                double remaining = periodSeconds.Value - total;
                if (!double.IsFinite(remaining) || remaining <= 0)
                    throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentList extends beyond its period"));
                double segmentDuration = Math.Min(segmentSeconds, remaining);

                double start = periodStartSeconds + total;
                if (!double.IsFinite(start) || start < 0 || !double.IsFinite(segmentDuration) || segmentDuration <= 0)
                    throw new MediaResolverException(MediaErrors.UnsupportedManifest("Invalid duration-based SegmentList timing"));
                timings.Add((start, segmentDuration));
                total += segmentDuration;
            }
        }
        else if (segmentUrls.Count == 1 && periodSeconds is { } singleDuration && double.IsFinite(singleDuration) && singleDuration > 0)
        {
            timings.Add((periodStartSeconds, singleDuration));
            total = singleDuration;
        }
        else
        {
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentList needs a duration or SegmentTimeline"));
        }

        var segments = new List<MediaSegment>(segmentUrls.Count);
        for (int index = 0; index < segmentUrls.Count; index++)
        {
            var segmentUrl = segmentUrls[index];
            string? media = segmentUrl.Attribute("media")?.Value;
            var url = media is null ? baseUrl : DashTemplate.ResolveUrl(baseUrl, media);
            var (start, segmentDuration) = timings[index];
            segments.Add(new MediaSegment(url, segmentDuration, ParseRange(segmentUrl.Attribute("mediaRange")?.Value),
                StartSeconds: start));
        }

        return new SegmentPlan
        {
            InitializationUrl = init,
            InitializationRange = initRange,
            Segments = segments,
            TotalDurationSeconds = total,
            IsFragmentedMp4 = fmp4,
            HasDiscontinuities = discontinuities
        };
    }

    private static XElement? MergeSegmentLists(params XElement?[] lists)
    {
        var levels = lists.Where(list => list is not null).Reverse().Select(list => list!).ToArray();
        if (levels.Length == 0) return null;

        var merged = new XElement(levels[0].Name);
        foreach (var level in levels)
        {
            foreach (var attribute in level.Attributes())
            {
                if (merged.Attribute(attribute.Name) is null)
                    merged.Add(new XAttribute(attribute));
            }
        }

        foreach (string childName in new[] { "Initialization", "SegmentTimeline" })
        {
            var child = levels.Select(level => Kid(level, childName)).FirstOrDefault(candidate => candidate is not null);
            if (child is not null) merged.Add(new XElement(child));
        }

        var urlLevel = levels.FirstOrDefault(level => Kids(level, "SegmentURL").Any());
        if (urlLevel is not null)
        {
            foreach (var segmentUrl in Kids(urlLevel, "SegmentURL"))
                merged.Add(new XElement(segmentUrl));
        }

        return merged;
    }

    private static long ReadOptionalLong(XElement element, string name, long defaultValue, bool allowZero,
        long maxValue = long.MaxValue)
    {
        string? value = element.Attribute(name)?.Value;
        if (value is null) return defaultValue;
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < (allowZero ? 0 : 1) || parsed > maxValue)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest($"Invalid SegmentList @{name}"));
        return parsed;
    }

    internal static ByteRange? ParseRange(string? s)
    {
        if (s is null) return null;
        var p = s.Split('-');
        if (p.Length == 2 && long.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var a)
            && long.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var b) && b >= a && b < long.MaxValue)
            return new ByteRange(a, b - a + 1);
        throw new MediaResolverException(MediaErrors.UnsupportedManifest("A DASH byte range is invalid"));
    }

    internal static TimeSpan? ParseIsoDuration(string? s)
    {
        if (s is null) return null;
        TimeSpan duration;
        try { duration = XmlConvert.ToTimeSpan(s); }
        catch (FormatException)
        {
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("A DASH duration value is invalid"));
        }
        catch (OverflowException)
        {
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("A DASH duration value overflows"));
        }

        if (duration < TimeSpan.Zero)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("A DASH duration cannot be negative"));
        return duration;
    }

    internal static double? ParseFrameRate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var p = s.Split('/');
        if (!double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= 0) return null;
        if (p.Length == 2)
            return double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && den > 0 ? Math.Round(n / den, 3) : null;
        return n;
    }

    private static int? PositiveInt(string? s) => int.TryParse(s, out var v) && v > 0 && v <= 1_000_000 ? v : null;

    private static IEnumerable<XElement> Kids(XElement e, string name) => e.Elements().Where(x => x.Name.LocalName == name);

    private static XElement? Kid(XElement e, string name) => Kids(e, name).FirstOrDefault();

    private static Uri ChildBase(Uri parent, XElement el, out bool hasBase)
    {
        string? b = Kid(el, "BaseURL")?.Value.Trim();
        hasBase = !string.IsNullOrEmpty(b);
        if (!hasBase) return parent;
        return UrlUtil.Resolve(parent, b)
            ?? throw new MediaResolverException(MediaErrors.UnsupportedManifest("BaseURL resolves to a non-http(s) address"));
    }
}
