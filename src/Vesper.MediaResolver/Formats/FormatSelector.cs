using Vesper.MediaResolver.Models;
using Vesper.MediaResolver.Utilities;

namespace Vesper.MediaResolver.Formats;

/// <summary>
/// Deterministic format-ranking engine (no UI, no I/O). Same inputs always give the same output: every ordering
/// ends with an ordinal comparison of representation ids. It never invents a format and never reports a
/// video-only stream as having sound.
/// </summary>
public static class FormatSelector
{
    private const double DefaultDurationSeconds = 600;

    public static SelectionResult Select(MediaItem item, FormatPreferences prefs)
    {
        var reps = item.Representations.Where(r => !r.IsLive).ToList();
        if (reps.Count == 0) return Fail(MediaErrors.NoMedia("Item has no downloadable representations", false));

        var audios = reps.Where(r => r.Content == StreamContent.AudioOnly).ToList();
        bool anyVideo = reps.Any(r => r.HasVideo);

        var result = prefs.Mode == QualityMode.AudioOnly || !anyVideo
            ? SelectAudio(item, reps, audios, prefs)
            : SelectVideo(item, reps, audios, prefs);
        return result.Selection is null ? result : result with { Selection = WithSubtitles(result.Selection, item, prefs) };
    }

    // ------------------------------------------------------------------------------------------------ video

    private sealed class Combo
    {
        public Combo(MediaRepresentation v, MediaRepresentation? a, string? preferredContainer, bool nativeMerge = false)
        {
            V = v;
            A = a;
            Container = a is null ? v.Container
                : nativeMerge ? MergeCapability.MergeContainer(v, a, preferredContainer)
                : CodecInfo.ChooseMuxContainer(v.VideoCodec, a.AudioCodec, preferredContainer);
        }

        public MediaRepresentation V { get; }
        public MediaRepresentation? A { get; }
        public string? Container { get; }
        public int Height => V.Height ?? 0;
        public double? Fps => V.FrameRate;
        public string? VCodec => V.VideoCodec;
        public string? ACodec => A?.AudioCodec ?? V.AudioCodec;
        public bool HasAudio => A is not null || V.HasAudio;
        public int Kbps => (V.BitrateKbps ?? 0) + (A?.BitrateKbps ?? 0);
        public long? Size => V.FileSize is { } s && (A is null || A.FileSize is not null) ? s + (A?.FileSize ?? 0) : null;
        public bool IsEstimate => V.FileSizeIsEstimate || (A?.FileSizeIsEstimate ?? false);
    }

    private static SelectionResult SelectVideo(MediaItem item, List<MediaRepresentation> reps, List<MediaRepresentation> audios, FormatPreferences p)
    {
        var combos = new List<Combo>();
        foreach (var full in reps.Where(x => x.Content == StreamContent.VideoAndAudio))
            combos.Add(new Combo(full, null, p.Container));

        foreach (var v in reps.Where(x => x.Content == StreamContent.VideoOnly))
        {
            if (audios.Count == 0) combos.Add(new Combo(v, null, p.Container));          // source genuinely has no audio stream
            else if (p.AllowMuxing)
            {
                var pool = p.NativeMergeOnly ? audios.Where(x => MergeCapability.MergeContainer(v, x) is not null).ToList() : audios;
                var a = pool.Count == 0 ? null : PickAudio(pool, p, v);
                if (a is not null) combos.Add(new Combo(v, a, p.Container, p.NativeMergeOnly));
            }
        }

        if (combos.Count == 0)
            return Fail(MediaErrors.NoFormat("Only separate video/audio streams exist and muxing is disabled by preference"));

        // "Best available with audio": never prefer a silent stream over one with sound.
        if (combos.Any(c => c.HasAudio)) combos = combos.Where(c => c.HasAudio).ToList();

        string? eliminatedBy = null;
        combos = Filter(combos, p.Hdr == HdrPreference.SdrOnly, c => c.V.Hdr == HdrKind.Sdr, "SDR-only", ref eliminatedBy);
        combos = Filter(combos, p.Hdr == HdrPreference.HdrOnly, c => c.V.Hdr != HdrKind.Sdr, "HDR-only", ref eliminatedBy);
        combos = Filter(combos, p.RestrictVideoCodecs && p.VideoCodecs.Count > 0,
            c => c.VCodec is not null && p.VideoCodecs.Contains(c.VCodec, StringComparer.OrdinalIgnoreCase), "video codec restriction", ref eliminatedBy);
        combos = Filter(combos, p.ContainerStrict && p.Container is not null,
            c => string.Equals(c.Container, p.Container, StringComparison.OrdinalIgnoreCase), $"container {p.Container}", ref eliminatedBy);
        combos = Filter(combos, p.MaxFrameRate is not null, c => c.Fps is null || c.Fps <= p.MaxFrameRate + 0.5, "frame-rate limit", ref eliminatedBy);
        combos = Filter(combos, p.MaxFileSizeBytes is not null, c => c.Size is null || c.Size <= p.MaxFileSizeBytes, "file-size limit", ref eliminatedBy);
        if (combos.Count == 0) return Fail(MediaErrors.NoFormat($"Nothing satisfies the {eliminatedBy}"));

        bool fallback = false;
        string? reason = null;
        List<Combo> ordered;

        switch (p.Mode)
        {
            case QualityMode.Smallest:
            {
                var suitable = combos.Where(c => c.Height >= p.SuitableMinHeight).ToList();
                List<Combo> tier;
                if (suitable.Count > 0) tier = suitable;
                else
                {
                    int top = combos.Max(c => c.Height);
                    tier = combos.Where(c => c.Height == top).ToList();
                    fallback = true;
                    reason = $"No stream at or above {p.SuitableMinHeight}p; using the largest available ({top}p)";
                }
                double secs = item.Duration?.TotalSeconds ?? DefaultDurationSeconds;
                ordered = tier.OrderBy(c => c.Size is { } s ? (double)s : c.Kbps * 125.0 * secs)
                              .ThenBy(c => c.Height)
                              .ThenBy(c => c.V.Id, StringComparer.Ordinal).ToList();
                break;
            }
            case QualityMode.Balanced:
            {
                int cap = Math.Min(p.MaxHeight ?? int.MaxValue, p.BalancedMaxHeight);
                var under = combos.Where(c => c.Height <= cap).ToList();
                List<Combo> tier;
                if (under.Count == 0)
                {
                    int low = combos.Min(c => c.Height);
                    tier = combos.Where(c => c.Height == low).ToList();
                    fallback = true;
                    reason = $"Everything available is above {cap}p; using the lowest ({low}p)";
                }
                else
                {
                    int top = under.Max(c => c.Height);
                    var atTop = under.Where(c => c.Height == top).ToList();
                    var lowFps = atTop.Where(c => c.Fps is null || c.Fps <= p.BalancedMaxFrameRate + 0.5).ToList();
                    tier = lowFps.Count > 0 ? lowFps : atTop;
                }
                ordered = Order(tier, p, preferLowBitrate: true);
                break;
            }
            default: // Best and Custom
            {
                int? target = p.Mode == QualityMode.Custom ? p.TargetHeight : null;
                var pool = combos;
                if (p.MaxHeight is { } cap)
                {
                    var under = pool.Where(c => c.Height <= cap).ToList();
                    if (under.Count > 0) pool = under;
                    else if (p.Fallback == FallbackPolicy.Fail) return Fail(MediaErrors.NoFormat($"Nothing at or below {cap}p"));
                    else
                    {
                        int low = pool.Min(c => c.Height);
                        pool = pool.Where(c => c.Height == low).ToList();
                        fallback = true;
                        reason = $"Nothing at or below {cap}p; using the lowest available ({low}p)";
                    }
                }

                int chosen;
                if (target is { } t)
                {
                    var heights = pool.Select(c => c.Height).Distinct().ToList();
                    int? r = ResolveHeight(heights, t, p.Fallback);
                    if (r is null) return Fail(MediaErrors.NoFormat($"{t}p is not available and the fallback policy ({p.Fallback}) allows no substitute"));
                    chosen = r.Value;
                    if (chosen != t)
                    {
                        fallback = true;
                        reason = $"{t}p unavailable; selected {chosen}p ({p.Fallback})";
                    }
                }
                else chosen = pool.Max(c => c.Height);

                ordered = Order(pool.Where(c => c.Height == chosen), p, preferLowBitrate: false);
                break;
            }
        }

        return Ok(BuildSelection(ordered[0], p, fallback, reason));
    }

    private static List<Combo> Filter(List<Combo> src, bool active, Func<Combo, bool> pred, string name, ref string? eliminatedBy)
    {
        if (!active || src.Count == 0) return src;
        var r = src.Where(pred).ToList();
        if (r.Count == 0) eliminatedBy ??= name;
        return r;
    }

    private static int? ResolveHeight(IReadOnlyCollection<int> heights, int target, FallbackPolicy policy)
    {
        if (heights.Contains(target)) return target;
        int? lower = heights.Where(h => h < target).Select(h => (int?)h).Max();
        int? higher = heights.Where(h => h > target).Select(h => (int?)h).Min();
        return policy switch
        {
            FallbackPolicy.NearestLowerThenHigher => lower ?? higher,
            FallbackPolicy.LowerOnly => lower,
            FallbackPolicy.Nearest => lower is null ? higher : higher is null ? lower : (target - lower <= higher - target ? lower : higher),
            FallbackPolicy.BestAvailable => heights.Max(),
            _ => null
        };
    }

    /// <summary>Tie-break chain inside one height tier. Final key is the id, so the order is total.</summary>
    private static List<Combo> Order(IEnumerable<Combo> tier, FormatPreferences p, bool preferLowBitrate)
    {
        return tier
            .OrderBy(c => p.Container is not null && string.Equals(c.Container, p.Container, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => CodecIndex(p.VideoCodecs, c.VCodec))
            .ThenBy(c => p.TargetFrameRate is { } tf ? Math.Abs((c.Fps ?? 0) - tf) : -(c.Fps ?? 0))
            .ThenBy(c => p.Hdr is HdrPreference.PreferHdr or HdrPreference.HdrOnly ? (c.V.Hdr == HdrKind.Sdr ? 1 : 0) : (c.V.Hdr == HdrKind.Sdr ? 0 : 1))
            .ThenBy(c => preferLowBitrate ? c.Kbps : -c.Kbps)
            .ThenByDescending(c => CodecInfo.CompatibilityRank(c.VCodec))
            .ThenBy(c => c.A is null ? 0 : 1)
            .ThenBy(c => (int)c.V.Protocol)
            .ThenBy(c => c.V.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static int CodecIndex(IReadOnlyList<string> prefs, string? codec)
    {
        if (codec is null) return int.MaxValue;
        for (int i = 0; i < prefs.Count; i++)
            if (string.Equals(prefs[i], codec, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue - 1;
    }

    private static FormatSelection BuildSelection(Combo c, FormatPreferences p, bool fallback, string? reason)
    {
        var req = c.V.Requirements | (c.A?.Requirements ?? ProcessingRequirement.None);
        if (c.A is not null) req |= ProcessingRequirement.MuxAudioVideo;

        string res = c.V.Height is { } h ? $"{h}p" : "unknown resolution";
        string fps = c.Fps is > 30.5 ? $"{Math.Round(c.Fps.Value)}fps " : "";
        string audioNote = c.HasAudio ? (c.A is not null ? "+ separate audio (merge required)" : "with audio")
                                      : "VIDEO ONLY (no audio stream available)";
        return new FormatSelection
        {
            Video = c.V,
            Audio = c.A,
            Requirements = req,
            OutputContainer = c.Container,
            EstimatedSize = c.Size,
            EstimatedSizeIsEstimate = c.IsEstimate,
            IsFallback = fallback,
            FallbackReason = reason,
            Summary = $"{res} {fps}{c.VCodec ?? ""} {audioNote}".Replace("  ", " ").Trim()
        };
    }

    // ------------------------------------------------------------------------------------------------ audio

    private static SelectionResult SelectAudio(MediaItem item, List<MediaRepresentation> reps, List<MediaRepresentation> audios, FormatPreferences p)
    {
        if (audios.Count > 0)
        {
            var a = PickAudio(audios, p, null)!;
            return Ok(new FormatSelection
            {
                Audio = a,
                Requirements = a.Requirements,
                OutputContainer = a.Container,
                EstimatedSize = a.FileSize,
                EstimatedSizeIsEstimate = a.FileSizeIsEstimate,
                Summary = $"audio {a.AudioCodec ?? a.Container ?? ""} {(a.BitrateKbps is { } k ? k + "kbps" : "")}".Trim()
            });
        }

        // No audio-only stream: the only honest answer is a muxed stream plus an explicit extraction requirement.
        var muxed = reps.Where(r => r.Content == StreamContent.VideoAndAudio).ToList();
        if (muxed.Count == 0)
            return Fail(MediaErrors.NoFormat("No stream with audio exists"));

        var smallest = muxed.OrderBy(r => r.Height ?? 0).ThenBy(r => r.BitrateKbps ?? 0).ThenBy(r => r.Id, StringComparer.Ordinal).First();
        return Ok(new FormatSelection
        {
            Video = smallest,
            Requirements = smallest.Requirements | ProcessingRequirement.ExtractAudio,
            OutputContainer = smallest.Container,
            EstimatedSize = smallest.FileSize,
            EstimatedSizeIsEstimate = smallest.FileSizeIsEstimate,
            IsFallback = true,
            FallbackReason = "No audio-only stream exists; the lowest-quality complete stream is selected and audio must be extracted by an external tool",
            Summary = "audio extraction from a complete stream (external tool required)"
        });
    }

    private static MediaRepresentation? PickAudio(List<MediaRepresentation> audios, FormatPreferences p, MediaRepresentation? video)
    {
        IEnumerable<MediaRepresentation> pool = audios;

        // HLS video variants pair only with renditions of their AUDIO group.
        if (video is { Protocol: StreamProtocol.Hls, AudioGroupId: { } group })
        {
            var inGroup = audios.Where(a => a.Protocol == StreamProtocol.Hls && a.AudioGroupId == group).ToList();
            if (inGroup.Count > 0) pool = inGroup;
        }
        else if (video is not null)
        {
            var sameFamily = audios.Where(a => (a.Protocol == StreamProtocol.Hls) == (video.Protocol == StreamProtocol.Hls)).ToList();
            if (sameFamily.Count > 0) pool = sameFamily;
        }

        var list = pool.ToList();
        if (list.Any(a => !a.IsAudioDescription)) list = list.Where(a => !a.IsAudioDescription).ToList();
        if (p.MaxAudioChannels is { } maxCh && list.Any(a => (a.AudioChannels ?? 2) <= maxCh))
            list = list.Where(a => (a.AudioChannels ?? 2) <= maxCh).ToList();

        string? wantLang = string.IsNullOrWhiteSpace(p.AudioLanguage) ? null : TextUtil.NormalizeLang(p.AudioLanguage);

        return list
            .OrderBy(a => LanguageRank(a, wantLang))
            .ThenBy(a => video is not null && a.AudioCodec is not null && video.Container is not null
                         && !CodecInfo.FitsContainer(CodecInfo.ChooseMuxContainer(video.VideoCodec, a.AudioCodec, p.Container), video.VideoCodec, a.AudioCodec) ? 1 : 0)
            .ThenBy(a => CodecIndex(p.AudioCodecs, a.AudioCodec))
            .ThenBy(a => p.PreferSurround ? -(a.AudioChannels ?? 2) : ((a.AudioChannels ?? 2) == 2 ? 0 : (a.AudioChannels ?? 2) < 2 ? 1 : 2))
            .ThenByDescending(a => a.BitrateKbps ?? 0)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static int LanguageRank(MediaRepresentation a, string? wanted)
    {
        if (wanted is null) return a.IsDefaultAudio ? 0 : 1;
        if (a.AudioLanguage is null) return a.IsDefaultAudio ? 3 : 4;
        string have = TextUtil.NormalizeLang(a.AudioLanguage);
        if (have == wanted) return 0;
        if (TextUtil.PrimaryLang(have) == TextUtil.PrimaryLang(wanted)) return 1;
        return a.IsDefaultAudio ? 3 : 4;
    }

    // ------------------------------------------------------------------------------------------------ subtitles

    private static FormatSelection WithSubtitles(FormatSelection sel, MediaItem item, FormatPreferences p)
    {
        if (p.SubtitleLanguages.Count == 0 || item.Subtitles.Count == 0) return sel;
        var chosen = new List<SubtitleTrack>();
        foreach (var lang in p.SubtitleLanguages)
        {
            string want = TextUtil.NormalizeLang(lang);
            var match = item.Subtitles
                .Where(s => p.IncludeAutoSubtitles || !s.IsAutoGenerated)
                .Where(s => TextUtil.NormalizeLang(s.Language) == want || TextUtil.PrimaryLang(s.Language) == TextUtil.PrimaryLang(want))
                .OrderBy(s => TextUtil.NormalizeLang(s.Language) == want ? 0 : 1)
                .ThenBy(s => s.IsAutoGenerated ? 1 : 0)
                .ThenBy(s => s.Url.AbsoluteUri, StringComparer.Ordinal)
                .FirstOrDefault();
            if (match is not null && !chosen.Contains(match)) chosen.Add(match);
        }
        return sel with { Subtitles = chosen };
    }

    private static SelectionResult Ok(FormatSelection s) => new(s, null);
    private static SelectionResult Fail(MediaError e) => new(null, e);
}
