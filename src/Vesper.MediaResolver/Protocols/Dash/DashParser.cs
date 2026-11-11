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
                    var list = Kid(rep, "SegmentList") ?? Kid(aset, "SegmentList");
                    if (tmpl is not null)
                        plan = tmpl.Expand(repId, bandwidth, repBase, periodSeconds, maxSegments, fmp4);
                    else if (list is not null)
                        plan = ExpandList(list, repBase, maxSegments, periodSeconds, fmp4);
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

    private static SegmentPlan ExpandList(XElement list, Uri baseUrl, int max, double? periodSeconds, bool fmp4)
    {
        long timescale = long.TryParse(list.Attribute("timescale")?.Value, out var ts) && ts > 0 ? ts : 1;
        double segDuration = long.TryParse(list.Attribute("duration")?.Value, out var d) && d > 0 ? (double)d / timescale : 0;

        Uri? init = null;
        ByteRange? initRange = null;
        var initEl = Kid(list, "Initialization");
        if (initEl is not null)
        {
            string? src = initEl.Attribute("sourceURL")?.Value;
            init = src is null ? baseUrl : DashTemplate.ResolveUrl(baseUrl, src);
            initRange = ParseRange(initEl.Attribute("range")?.Value);
        }

        var segs = new List<MediaSegment>();
        double total = 0;
        foreach (var su in Kids(list, "SegmentURL"))
        {
            if (segs.Count >= max) throw new MediaResolverException(MediaErrors.TooLarge("Representation exceeds the segment limit"));
            string? media = su.Attribute("media")?.Value;
            var url = media is null ? baseUrl : DashTemplate.ResolveUrl(baseUrl, media);
            segs.Add(new MediaSegment(url, segDuration, ParseRange(su.Attribute("mediaRange")?.Value)));
            total += segDuration;
        }
        if (segs.Count == 0)
            throw new MediaResolverException(MediaErrors.UnsupportedManifest("SegmentList contains no SegmentURL"));
        if (total == 0 && periodSeconds is { } p) total = p;
        return new SegmentPlan { InitializationUrl = init, InitializationRange = initRange, Segments = segs, TotalDurationSeconds = total, IsFragmentedMp4 = fmp4 };
    }

    internal static ByteRange? ParseRange(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var p = s.Split('-');
        if (p.Length == 2 && long.TryParse(p[0], out var a) && long.TryParse(p[1], out var b) && b >= a && a >= 0)
            return new ByteRange(a, b - a + 1);
        return null;
    }

    internal static TimeSpan? ParseIsoDuration(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return XmlConvert.ToTimeSpan(s); }
        catch (FormatException) { return null; }
        catch (OverflowException) { return null; }
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
