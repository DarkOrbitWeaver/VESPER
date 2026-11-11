using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vesper.MediaResolver.Extractors;

/// <summary>
/// One InnerTube client context. All opaque client identifiers live in this file so a YouTube change is repaired in
/// one place. Profiles never carry cookies or tokens; <see cref="HttpsNeedsPoToken"/> and <see cref="NeedsPlayerJs"/>
/// document why a profile cannot be used natively instead of pretending it works.
/// </summary>
internal sealed record YouTubeClientProfile(
    string Key,
    string ClientName,
    int ClientNameId,
    string ClientVersion,
    string UserAgent,
    IReadOnlyDictionary<string, JsonNode?> ClientFields,
    bool NeedsPlayerJs,
    bool HttpsNeedsPoToken,
    string Notes)
{
    public string BuildPlayerRequest(string videoId, string? visitorData, int? signatureTimestamp)
    {
        var client = new JsonObject
        {
            ["clientName"] = ClientName,
            ["clientVersion"] = ClientVersion,
            ["userAgent"] = UserAgent,
            ["hl"] = "en",
            ["gl"] = "US"
        };
        foreach (var kv in ClientFields) client[kv.Key] = kv.Value?.DeepClone();
        if (visitorData is not null) client["visitorData"] = visitorData;
        var playback = new JsonObject { ["html5Preference"] = "HTML5_PREF_WANTS" };
        if (signatureTimestamp is not null) playback["signatureTimestamp"] = signatureTimestamp.Value;
        return new JsonObject
        {
            ["context"] = new JsonObject { ["client"] = client },
            ["videoId"] = videoId,
            ["playbackContext"] = new JsonObject { ["contentPlaybackContext"] = playback },
            // Acknowledges ordinary content warnings only. Age, login and membership gates are still enforced by YouTube.
            ["contentCheckOk"] = true,
            ["racyCheckOk"] = true
        }.ToJsonString();
    }

    public Dictionary<string, string> Headers(string? visitorData)
    {
        var h = new Dictionary<string, string>
        {
            ["X-YouTube-Client-Name"] = ClientNameId.ToString(CultureInfo.InvariantCulture),
            ["X-YouTube-Client-Version"] = ClientVersion,
            ["Origin"] = "https://www.youtube.com"
        };
        if (visitorData is not null) h["X-Goog-Visitor-Id"] = visitorData;
        return h;
    }
}

internal static class YouTubeClients
{
    /// <summary>
    /// visionOS: no player JavaScript, no proof-of-origin token, URLs without the 'n' throttling challenge.
    /// Verified live on 2026-11-11 (full byte ranges served). Does not serve "made for kids" videos.
    /// </summary>
    public static readonly YouTubeClientProfile VisionOs = new(
        "visionos", "VISIONOS", 101, "1.02",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15",
        new Dictionary<string, JsonNode?>
        {
            ["deviceMake"] = "Apple", ["deviceModel"] = "RealityDevice17,1", ["osName"] = "visionOS", ["osVersion"] = "26.5.23O471"
        },
        NeedsPlayerJs: false, HttpsNeedsPoToken: false,
        "Primary native strategy.");

    /// <summary>Android VR: JS-less, but reported to answer HTTP 403 for all formats since 2026-08-17. Opt-in only.</summary>
    public static readonly YouTubeClientProfile AndroidVr = new(
        "android_vr", "ANDROID_VR", 28, "1.65.10",
        "com.google.android.apps.youtube.vr.oculus/1.65.10 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip",
        new Dictionary<string, JsonNode?>
        {
            ["deviceMake"] = "Oculus", ["deviceModel"] = "Quest 3", ["androidSdkVersion"] = 32, ["osName"] = "Android", ["osVersion"] = "12L"
        },
        NeedsPlayerJs: false, HttpsNeedsPoToken: false,
        "Opt-in fallback; bot-gated from datacenter networks and reported broken upstream.");

    /// <summary>iOS: answers with URLs, but media beyond the first ~1 MiB is refused without a GVS PO token (verified 2026-11-11).</summary>
    public static readonly YouTubeClientProfile Ios = new(
        "ios", "IOS", 5, "20.10.4",
        "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)",
        new Dictionary<string, JsonNode?>
        {
            ["deviceMake"] = "Apple", ["deviceModel"] = "iPhone16,2", ["osName"] = "iPhone", ["osVersion"] = "18.3.2.22D82"
        },
        NeedsPlayerJs: false, HttpsNeedsPoToken: true,
        "Diagnostic only: HTTPS formats require a proof-of-origin token VESPER does not manufacture.");

    public static readonly IReadOnlyList<YouTubeClientProfile> All = [VisionOs, AndroidVr, Ios];

    /// <summary>Profiles in configured order; unknown keys are ignored, duplicates removed.</summary>
    public static IReadOnlyList<YouTubeClientProfile> Resolve(IEnumerable<string> keys) =>
        keys.Select(k => All.FirstOrDefault(p => p.Key.Equals(k, StringComparison.OrdinalIgnoreCase)))
            .Where(p => p is not null).Select(p => p!).Distinct().ToList();
}

/// <summary>Session values from the watch page needed by InnerTube requests. All page regexes live here.</summary>
internal sealed partial record YtPageConfig(string? VisitorData, int? SignatureTimestamp, string? WebClientVersion)
{
    [GeneratedRegex("\"VISITOR_DATA\":\"([A-Za-z0-9_%=\\-]{8,512})\"")]
    private static partial Regex VisitorRegex();
    [GeneratedRegex("\"(?:STS|signatureTimestamp)\":(\\d{5,6})")]
    private static partial Regex StsRegex();
    [GeneratedRegex("\"INNERTUBE_CLIENT_VERSION\":\"(\\d+\\.\\d{8}\\.\\d{2}\\.\\d{2})\"")]
    private static partial Regex WebVersionRegex();

    public const string DefaultWebClientVersion = "2.20261101.00.00";

    public static YtPageConfig Parse(string html)
    {
        string? visitor = VisitorRegex().Match(html) is { Success: true } v ? v.Groups[1].Value : null;
        int? sts = StsRegex().Match(html) is { Success: true } s && int.TryParse(s.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
        string? version = WebVersionRegex().Match(html) is { Success: true } w ? w.Groups[1].Value : null;
        return new YtPageConfig(visitor, sts, version);
    }
}
