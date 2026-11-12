using System.Text.Json;
using System.Text.Json.Serialization;
using Vesper.MediaResolver.Models;

namespace Vesper.MediaResolver.Formats;

public enum QualityMode { Best, Balanced, Smallest, AudioOnly, Custom }

/// <summary>What to do when the requested height is not offered.</summary>
public enum FallbackPolicy
{
    /// <summary>Highest height below the target, else the lowest above it (default).</summary>
    NearestLowerThenHigher,
    /// <summary>Only heights below the target; fail otherwise.</summary>
    LowerOnly,
    /// <summary>Closest height in either direction (ties go lower).</summary>
    Nearest,
    /// <summary>Ignore the target and take the best available.</summary>
    BestAvailable,
    /// <summary>No fallback: report NoMatchingFormat.</summary>
    Fail
}

public enum HdrPreference { Any, SdrOnly, PreferHdr, HdrOnly }

/// <summary>Immutable, JSON-serialisable quality preference. Used per batch, per site and per item.</summary>
public sealed record FormatPreferences
{
    public QualityMode Mode { get; init; } = QualityMode.Best;

    /// <summary>Custom mode: wanted height (e.g. 1080, 2160, 4320).</summary>
    public int? TargetHeight { get; init; }
    public int? MaxHeight { get; init; }
    public double? MaxFrameRate { get; init; }
    public double? TargetFrameRate { get; init; }
    public HdrPreference Hdr { get; init; } = HdrPreference.Any;

    /// <summary>Ordered preference (h264, h265, vp9, av1). With <see cref="RestrictVideoCodecs"/> it becomes a hard filter.</summary>
    public IReadOnlyList<string> VideoCodecs { get; init; } = [];
    public bool RestrictVideoCodecs { get; init; }
    public IReadOnlyList<string> AudioCodecs { get; init; } = [];

    public string? AudioLanguage { get; init; }
    public int? MaxAudioChannels { get; init; }
    public bool PreferSurround { get; init; }

    public string? Container { get; init; }
    public bool ContainerStrict { get; init; }

    /// <summary>False = only single-stream (already muxed) representations may be chosen.</summary>
    public bool AllowMuxing { get; init; } = true;

    /// <summary>Pair video with audio only when VESPER's native remuxer can execute it (see <see cref="MergeCapability"/>).</summary>
    public bool NativeMergeOnly { get; init; }

    public long? MaxFileSizeBytes { get; init; }

    public IReadOnlyList<string> SubtitleLanguages { get; init; } = [];
    public bool IncludeAutoSubtitles { get; init; }

    public FallbackPolicy Fallback { get; init; } = FallbackPolicy.NearestLowerThenHigher;

    /// <summary>"Smallest suitable" never goes below this height when something at/above it exists.</summary>
    public int SuitableMinHeight { get; init; } = 480;
    public int BalancedMaxHeight { get; init; } = 1080;
    public double BalancedMaxFrameRate { get; init; } = 30;

    // ---- convenience presets ----
    public static FormatPreferences BestQuality => new() { Mode = QualityMode.Best };
    public static FormatPreferences BalancedQuality => new() { Mode = QualityMode.Balanced };
    public static FormatPreferences SmallestSuitable => new() { Mode = QualityMode.Smallest };
    public static FormatPreferences AudioOnly => new() { Mode = QualityMode.AudioOnly, AudioCodecs = ["opus"] };
    public static FormatPreferences Height(int height, FallbackPolicy fallback = FallbackPolicy.NearestLowerThenHigher) =>
        new() { Mode = QualityMode.Custom, TargetHeight = height, Fallback = fallback };
}

/// <summary>
/// In-memory preference resolution: per-item override > per-site > default. The host application decides where
/// (and whether) to persist it via <see cref="ToJson"/>/<see cref="FromJson"/>; the resolver has no database.
/// </summary>
public sealed class PreferenceStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Dictionary<string, FormatPreferences> _sites = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FormatPreferences> _items = new(StringComparer.OrdinalIgnoreCase);

    public FormatPreferences Default { get; set; } = new();

    public void SetSite(string site, FormatPreferences prefs) => _sites[site] = prefs;
    public bool ClearSite(string site) => _sites.Remove(site);
    public void SetItemOverride(string site, string mediaId, FormatPreferences prefs) => _items[$"{site}|{mediaId}"] = prefs;
    public bool ClearItemOverride(string site, string mediaId) => _items.Remove($"{site}|{mediaId}");

    /// <summary>Per-item override, else per-site preference; false when the user never customised this item/site.</summary>
    public bool TryGetExplicit(string? site, string? mediaId, out FormatPreferences prefs)
    {
        if (site is not null && mediaId is not null && _items.TryGetValue($"{site}|{mediaId}", out var item)) { prefs = item; return true; }
        if (site is not null && _sites.TryGetValue(site, out var s)) { prefs = s; return true; }
        prefs = Default;
        return false;
    }

    public FormatPreferences Resolve(string? site, string? mediaId)
    {
        if (site is not null && mediaId is not null && _items.TryGetValue($"{site}|{mediaId}", out var item)) return item;
        if (site is not null && _sites.TryGetValue(site, out var s)) return s;
        return Default;
    }

    private sealed record Snapshot(FormatPreferences Default, Dictionary<string, FormatPreferences> Sites, Dictionary<string, FormatPreferences> Items);

    public string ToJson() => JsonSerializer.Serialize(new Snapshot(Default, new(_sites), new(_items)), JsonOpts);

    public static PreferenceStore FromJson(string json)
    {
        var snap = JsonSerializer.Deserialize<Snapshot>(json, JsonOpts) ?? throw new JsonException("Empty preference document");
        var store = new PreferenceStore { Default = snap.Default ?? new() };
        foreach (var kv in snap.Sites ?? new()) store._sites[kv.Key] = kv.Value;
        foreach (var kv in snap.Items ?? new()) store._items[kv.Key] = kv.Value;
        return store;
    }
}
