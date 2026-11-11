using System.Text;

namespace Vesper.MediaResolver.Core;

public enum CompatibilityStatus
{
    NotTested,
    VerifiedWorking,
    PartiallySupported,
    Regressed,
    Unsupported
}

/// <summary>One recorded outcome of a (live or fixture) verification run for an adapter.</summary>
public sealed record CompatibilityRecord(
    string AdapterId,
    CompatibilityStatus Status,
    DateOnly? LastVerified,
    string Operations,
    string? KnownRegressions = null);

/// <summary>
/// Generates the compatibility report from the adapters' own capability declarations plus recorded verification
/// results. Anything without a record is reported as NotTested: the report never assumes success.
/// </summary>
public static class CompatibilityReport
{
    public static string ToMarkdown(IEnumerable<AdapterDescriptor> adapters, IEnumerable<CompatibilityRecord>? records = null)
    {
        var byId = (records ?? []).GroupBy(r => r.AdapterId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.LastVerified).First());
        var sb = new StringBuilder();
        sb.AppendLine("# Vesper.MediaResolver compatibility report");
        sb.AppendLine();
        sb.AppendLine("Status values: VerifiedWorking, PartiallySupported, Regressed, Unsupported, NotTested.");
        sb.AppendLine("An adapter is only VerifiedWorking after a recorded run against real, authorised media.");
        sb.AppendLine();
        sb.AppendLine("| Adapter | Status | Last verified | Single | Playlist | Audio | Subtitles | Auth | Known unsupported |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");

        foreach (var a in adapters)
        {
            byId.TryGetValue(a.Id, out var rec);
            var c = a.Capabilities;
            sb.Append("| ").Append(a.DisplayName)
              .Append(" | ").Append(rec?.Status ?? CompatibilityStatus.NotTested)
              .Append(" | ").Append(rec?.LastVerified?.ToString("yyyy-MM-dd") ?? "never")
              .Append(" | ").Append(Mark(c.SingleItem))
              .Append(" | ").Append(Mark(c.Playlist))
              .Append(" | ").Append(Mark(c.AudioOnly))
              .Append(" | ").Append(Mark(c.Subtitles))
              .Append(" | ").Append(c.Authentication)
              .Append(" | ").Append(string.Join("; ", c.KnownUnsupported))
              .AppendLine(" |");
        }

        sb.AppendLine();
        foreach (var a in adapters)
        {
            var c = a.Capabilities;
            sb.AppendLine($"## {a.DisplayName} (`{a.Id}`)");
            sb.AppendLine($"- Domains: {string.Join(", ", c.Domains)}");
            sb.AppendLine("- URL patterns:");
            foreach (var p in c.UrlPatterns) sb.AppendLine($"  - `{p}`");
            sb.AppendLine($"- Notes: {c.Notes}");
            if (byId.TryGetValue(a.Id, out var r))
            {
                sb.AppendLine($"- Verified operations: {r.Operations}");
                if (r.KnownRegressions is not null) sb.AppendLine($"- Known regressions: {r.KnownRegressions}");
            }
            sb.AppendLine();
        }
        return sb.ToString();

        static string Mark(bool b) => b ? "yes" : "no";
    }
}
