using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vesper.Engine;

/// <summary>One bounded HTTP object or exact byte range. Contains no cookies or authorization headers.</summary>
public sealed record DashFragment(string Url, long? Offset = null, long? Length = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DurationSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? StartSeconds = null);

/// <summary>
/// Immutable transfer identity, stored separately from the small transactional cursor. Only clear fragmented MP4
/// is implemented. Separate video/audio tracks are not muxed. Refreshing URLs must never mutate a running plan.
/// </summary>
public sealed record DashDownloadPlan
{
    public int Version { get; init; } = 1;
    public required string RepresentationId { get; init; }
    public string? Provider { get; init; }
    public string? MediaId { get; init; }
    public required DashFragment Initialization { get; init; }
    public required IReadOnlyList<DashFragment> Fragments { get; init; }
    public bool RequireAudio { get; init; } = true;
    public bool RequireVideo { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MediaTimeRange? ClipRange { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this);
    internal string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())));

    internal static bool TryParse(string? json, bool allowPrivate, out DashDownloadPlan plan, out string error)
    {
        plan = null!;
        error = "The DASH plan is missing or invalid. Resolve the source again; saved data was kept.";
        if (json is null || json.Length > 12 * 1024 * 1024) return false;
        try
        {
            var value = JsonSerializer.Deserialize<DashDownloadPlan>(json, new JsonSerializerOptions { MaxDepth = 12 });
            if (value is null || value.Version != 1 || string.IsNullOrWhiteSpace(value.RepresentationId)
                || value.RepresentationId.Length > 256 || value.Fragments is not { Count: > 0 and <= 50_000 }
                || value.Initialization is null || (!value.RequireAudio && !value.RequireVideo)) return false;
            foreach (var part in value.Fragments.Prepend(value.Initialization))
            {
                if (part is null || part.Url is not { Length: > 0 and <= 16384 }
                    || !UrlPolicy.TryParse(part.Url, out var uri, out _)) return false;
                UrlPolicy.EnsureAllowed(uri, allowPrivate);
                if ((part.Offset is null) != (part.Length is null) || part.Offset < 0 || part.Length <= 0
                    || part.Length > DashTransfer.MaxObjectBytes
                    || (part.Offset is long start && part.Length is long length && start > long.MaxValue - length)
                    || part.DurationSeconds is double duration && (!double.IsFinite(duration) || duration <= 0 || duration > 31_536_000)
                    || part.StartSeconds is double startTime && (!double.IsFinite(startTime) || startTime < 0)) return false;
            }
            if (value.ClipRange is { IsValid: false }
                || value.ClipRange is not null && value.Fragments.Any(fragment => fragment.DurationSeconds is null)) return false;
            plan = value;
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or BlockedAddressException) { return false; }
    }
}
