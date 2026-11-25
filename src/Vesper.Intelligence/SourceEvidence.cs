namespace Vesper.Intelligence;

/// <summary>How a source became known, strongest first. Only browser observation is evidence of what actually played.</summary>
public enum SourceProvenance { PlayerElement, PlayerGeneration, Declared, PageStructure, Hypothesized }

public enum SourceShape { Unknown, Direct, Hls, Dash, Fragment }

/// <summary>Bounded, redacted facts about one candidate source (schema vesper.evidence/1). No URL is required.</summary>
public sealed record SourceEvidence(
    string Id,
    SourceProvenance Provenance,
    SourceShape Shape,
    bool CurrentGeneration = true,
    bool ResourceWitness = false,
    bool? ManifestParent = null,
    bool IncidentalLoop = false,
    bool AdContext = false)
{
    public RankingCandidate ToCandidate() => new(Id, new Dictionary<string, double?>
    {
        ["exactPlayerUrl"] = Provenance == SourceProvenance.PlayerElement ? 1 : 0,
        ["playerBound"] = Provenance is SourceProvenance.PlayerElement or SourceProvenance.PlayerGeneration ? 1 : 0,
        ["currentGeneration"] = CurrentGeneration ? 1 : 0,
        ["manifestParent"] = ManifestParent is { } parent ? (parent ? 1 : 0) : null,
        ["resourceWitness"] = ResourceWitness ? 1 : 0,
        ["wholeFile"] = Shape switch { SourceShape.Direct => 1, SourceShape.Fragment => 0, _ => null },
        ["declared"] = Provenance == SourceProvenance.Declared ? 1 : 0,
        ["durationAgreement"] = null,
        ["adContext"] = AdContext || IncidentalLoop ? 1 : 0,
        ["hypothesized"] = Provenance is SourceProvenance.PageStructure or SourceProvenance.Hypothesized ? 1 : 0,
        ["stale"] = CurrentGeneration ? 0 : 1,
        ["fragmentOnly"] = Shape == SourceShape.Fragment ? 1 : 0,
    });
}

/// <summary>
/// Orders candidate sources for a bounded verification budget. Candidates carrying negative evidence (stale
/// generation, isolated fragment, incidental loop, advertisement) move behind everything else; the remaining order
/// is the caller's, which already reflects provenance. Every candidate is still independently verified downstream.
/// </summary>
public static class SourcePrioritizer
{
    private static readonly Lazy<RankingModel> Model = new(() => RankingModel.LoadEmbedded("source-association"));

    public static RankingModel AssociationModel => Model.Value;

    public static bool HasNegativeEvidence(SourceEvidence evidence) =>
        !evidence.CurrentGeneration || evidence.Shape == SourceShape.Fragment || evidence.IncidentalLoop || evidence.AdContext;

    public static IReadOnlyList<T> Order<T>(IReadOnlyList<T> items, Func<T, SourceEvidence> describe)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(describe);
        var rows = items.Select((item, index) => (item, index, evidence: describe(item))).ToArray();
        int? preferred = null;
        if (rows.Length <= RankingModel.MaxCandidates)
        {
            // Opaque bounded IDs keep signed/long resource URLs out of the ranking interface.
            var decision = Associate(rows.Select(row => row.evidence with { Id = row.index.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
            if (decision.Decisive && int.TryParse(decision.Selected, out int index)) preferred = index;
        }
        return rows.OrderBy(row => HasNegativeEvidence(row.evidence)).ThenBy(row => row.index == preferred ? 0 : 1)
            .ThenBy(row => row.index).Select(row => row.item).ToArray();
    }

    /// <summary>Advisory association decision for diagnostics, presentation and verification order.</summary>
    public static RankingDecision Associate(IEnumerable<SourceEvidence> sources) =>
        Model.Value.Rank(sources.Take(RankingModel.MaxCandidates + 1).Select(source => source.ToCandidate()).ToArray());
}

public enum Interchangeability { Exact, NeedsVerification, Incompatible }

/// <summary>Validators describing saved bytes or a replacement source.</summary>
public sealed record ResourceIdentity(string? ProgramId = null, string? FormatId = null, long? Length = null, string? ETag = null, string? Url = null);

/// <summary>
/// Whether saved bytes may continue from a replacement. Only matching strong validators prove identity; shared names,
/// hosts, resolution or a model score never do. NeedsVerification means the engine's own refresh checks decide.
/// </summary>
public static class ResourceCompatibility
{
    public static (Interchangeability Verdict, IReadOnlyList<string> Reasons) Compare(ResourceIdentity saved, ResourceIdentity replacement)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(replacement);
        var reasons = new List<string>();
        static bool Strong(string? tag)
        {
            if (tag is not { Length: >= 2 } || tag[0] != '"' || tag[^1] != '"') return false;
            foreach (char c in tag.AsSpan(1, tag.Length - 2))
                if (c != 0x21 && (c < 0x23 || c > 0x7e) && (c < 0x80 || c > 0xff)) return false;
            return true;
        }
        static string? Resource(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme is "http" or "https") && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped) : null;
        if (saved.ProgramId is not null && replacement.ProgramId is not null && saved.ProgramId != replacement.ProgramId) reasons.Add("program-differs");
        if (saved.FormatId is not null && replacement.FormatId is not null && saved.FormatId != replacement.FormatId) reasons.Add("format-differs");
        if (saved.Length is not null && replacement.Length is not null && saved.Length != replacement.Length) reasons.Add("length-differs");
        if (Strong(saved.ETag) && Strong(replacement.ETag) && saved.ETag != replacement.ETag) reasons.Add("validator-differs");
        if (reasons.Count > 0) return (Interchangeability.Incompatible, reasons);
        bool exact = Resource(saved.Url) is { } resource && resource == Resource(replacement.Url)
            && Strong(saved.ETag) && saved.ETag == replacement.ETag && saved.Length is > 0 && saved.Length == replacement.Length;
        return exact ? (Interchangeability.Exact, ["same-resource-strong-validator-and-length"]) : (Interchangeability.NeedsVerification, ["identity-not-proven"]);
    }
}
