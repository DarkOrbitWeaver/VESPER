using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Vesper.Intelligence;

/// <summary>A versioned feature schema. Sign constraints belong to the schema so no model can reward negative evidence.</summary>
public sealed record FeatureSchema(string Name, IReadOnlyList<string> Features, IReadOnlySet<string> Negative)
{
    public static readonly FeatureSchema PlayerIntent = new("vesper.player-intent/2",
        ["area", "visible", "central", "playing", "ready", "progressed", "controls", "audiblePlayback", "incidentalLoop",
         "interaction", "incumbent", "networkBound", "declaredSource", "longForm", "adContext"],
        new HashSet<string>(["incidentalLoop", "adContext"]));

    public static readonly FeatureSchema SourceAssociation = new("vesper.source-association/1",
        ["exactPlayerUrl", "playerBound", "currentGeneration", "manifestParent", "resourceWitness", "wholeFile", "declared",
         "durationAgreement", "adContext", "hypothesized", "stale", "fragmentOnly"],
        new HashSet<string>(["adContext", "hypothesized", "stale", "fragmentOnly"]));

    public static FeatureSchema? Find(string? name) =>
        name == PlayerIntent.Name ? PlayerIntent : name == SourceAssociation.Name ? SourceAssociation : null;
}

/// <summary>One candidate's facts. A null value means "not observed"; it is never read as false.</summary>
public sealed record RankingCandidate(string Id, IReadOnlyDictionary<string, double?> Facts);

public sealed record RankedCandidate(string Id, double Score, double Probability,
    IReadOnlyDictionary<string, double> Terms, IReadOnlyList<string> Missing);

/// <summary>Advisory ranking. <see cref="Decisive"/> may order verification attempts or anchor presentation; it never authorizes acquisition.</summary>
public sealed record RankingDecision(string ModelVersion, string? Selected, bool Decisive, double Confidence, double? Margin,
    string Reason, IReadOnlyList<RankedCandidate> Ranked)
{
    public bool Abstained => !Decisive;
}

public sealed class InvalidModelException(string message) : Exception(message);

/// <summary>
/// Calibrated linear ranking model, format vesper.model/1, shared byte-for-byte with extension/vesper-inference.js.
/// Deterministic and bounded: at most <see cref="MaxCandidates"/> candidates, one pass, no I/O after load.
/// </summary>
public sealed class RankingModel
{
    public const string Format = "vesper.model/1";
    public const int MaxCandidates = 256;
    private const double WeightLimit = 16;

    public FeatureSchema Schema { get; }
    public string Version { get; }
    public IReadOnlyDictionary<string, double> Weights { get; }
    public double Temperature { get; }
    public double Margin { get; }
    public double MinConfidence { get; }
    public string Checksum { get; }
    public bool IsFallback { get; private set; }

    private RankingModel(FeatureSchema schema, string version, Dictionary<string, double> weights,
        double temperature, double margin, double minConfidence, string checksum)
    {
        Schema = schema; Version = version; Weights = weights.ToFrozenDictionary(StringComparer.Ordinal);
        Temperature = temperature; Margin = margin; MinConfidence = minConfidence; Checksum = checksum;
    }

    public static RankingModel Parse(string json)
    {
        if (json.Length > 64 * 1024) throw new InvalidModelException("Model exceeds size limit");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        double Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.NaN;

        if (Text("format") != Format) throw new InvalidModelException("Unsupported model format");
        var schema = FeatureSchema.Find(Text("schema")) ?? throw new InvalidModelException("Unknown feature schema");
        if (Text("kind") != "calibrated-linear") throw new InvalidModelException("Unsupported model kind");
        string version = Text("version") ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[a-z0-9.]{1,40})?$"))
            throw new InvalidModelException("Invalid model version");
        if (!root.TryGetProperty("weights", out var weightsElement) || weightsElement.ValueKind != JsonValueKind.Object)
            throw new InvalidModelException("Missing weights");
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in weightsElement.EnumerateObject())
        {
            if (!schema.Features.Contains(property.Name) || property.Value.ValueKind != JsonValueKind.Number)
                throw new InvalidModelException("Model features do not match schema");
            if (!weights.TryAdd(property.Name, property.Value.GetDouble())) throw new InvalidModelException("Duplicate model feature");
        }
        if (weights.Count != schema.Features.Count) throw new InvalidModelException("Model features do not match schema");
        foreach (var (name, weight) in weights)
        {
            if (!double.IsFinite(weight) || Math.Abs(weight) > WeightLimit) throw new InvalidModelException("Invalid weight: " + name);
            if (schema.Negative.Contains(name) ? weight > 0 : weight < 0) throw new InvalidModelException("Weight violates sign constraint: " + name);
        }
        double temperature = Number("temperature"), margin = Number("margin"), minConfidence = Number("minConfidence");
        if (!(temperature >= 0.05 && temperature <= 20)) throw new InvalidModelException("Invalid temperature");
        if (!(margin >= 0 && margin <= WeightLimit)) throw new InvalidModelException("Invalid margin");
        if (!(minConfidence >= 0.34 && minConfidence <= 0.999)) throw new InvalidModelException("Invalid confidence threshold");
        string expected = ComputeChecksum(schema, version, weights, temperature, margin, minConfidence);
        if (Text("checksum") != expected) throw new InvalidModelException("Model checksum mismatch");
        return new RankingModel(schema, version, weights, temperature, margin, minConfidence, expected);
    }

    /// <summary>Loads an artifact shipped inside this assembly (intelligence/models/active).</summary>
    public static RankingModel LoadEmbedded(string name)
    {
        if (name is not ("source-association" or "player-intent")) throw new InvalidModelException("Unknown embedded model");
        RankingModel Read(string prefix)
        {
            using var stream = typeof(RankingModel).Assembly.GetManifestResourceStream($"Vesper.Intelligence.{prefix}.{name}.json")
                ?? throw new InvalidModelException("Embedded model not found: " + name);
            if (stream.Length > 64 * 1024) throw new InvalidModelException("Embedded model exceeds size limit");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var result = Parse(reader.ReadToEnd());
            if (result.Schema != (name == "source-association" ? FeatureSchema.SourceAssociation : FeatureSchema.PlayerIntent))
                throw new InvalidModelException("Embedded model schema mismatch");
            return result;
        }
        try { return Read("Models"); }
        catch (Exception ex) when (ex is InvalidModelException or JsonException or FormatException or OverflowException or InvalidOperationException)
        {
            var fallback = Read("Fallback");
            fallback.IsFallback = true;
            return fallback;
        }
    }

    // JavaScript Math.round semantics (half toward +infinity) keep both runtimes' canonical text identical.
    private static string Fixed(double value) =>
        (Math.Floor(value * 1e6 + 0.5) / 1e6).ToString("F6", CultureInfo.InvariantCulture);

    internal static string ComputeChecksum(FeatureSchema schema, string version, IReadOnlyDictionary<string, double> weights,
        double temperature, double margin, double minConfidence)
    {
        string text = string.Join('|', schema.Name, version, "calibrated-linear",
            string.Join(',', schema.Features.Select(name => name + "=" + Fixed(weights[name]))),
            "t=" + Fixed(temperature), "m=" + Fixed(margin), "c=" + Fixed(minConfidence));
        uint hash = 0x811c9dc5;
        foreach (char c in text)
        {
            hash ^= (uint)(c & 0xff);
            hash *= 0x01000193;
        }
        return hash.ToString("x8", CultureInfo.InvariantCulture);
    }

    public RankingDecision Rank(IReadOnlyList<RankingCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        bool truncated = candidates.Count > MaxCandidates;
        bool duplicate = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<RankingCandidate>();
        foreach (var candidate in candidates.Take(MaxCandidates))
        {
            if (string.IsNullOrEmpty(candidate.Id) || candidate.Id.Length > 128) continue;
            if (!seen.Add(candidate.Id)) { duplicate = true; continue; }
            rows.Add(candidate);
        }
        if (rows.Count == 0) return new RankingDecision(Version, null, false, 0, null, "no-candidate", []);

        // A feature nobody reported is absent evidence; one reported for only some candidates is uncertainty.
        var active = Schema.Features.Where(name => rows.Any(row => Value(row, name) is not null)).ToArray();
        var scored = rows.Select(row =>
        {
            var terms = new Dictionary<string, double>(StringComparer.Ordinal);
            var missing = new List<string>();
            double score = 0, upside = 0, downside = 0;
            foreach (var name in active)
            {
                double weight = Weights[name];
                if (Value(row, name) is not { } value)
                {
                    missing.Add(name);
                    if (weight > 0) upside += weight; else downside += weight;
                    continue;
                }
                terms[name] = value * weight;
                score += terms[name];
            }
            return (row, score, upside, downside, terms, missing);
        }).OrderByDescending(x => x.score).ThenBy(x => x.row.Id, StringComparer.Ordinal).ToArray();

        double top = scored[0].score;
        double[] exps = scored.Select(x => Math.Exp((x.score - top) / Temperature)).ToArray();
        double total = exps.Sum();
        var ranked = scored.Select((x, i) => new RankedCandidate(x.row.Id, x.score, exps[i] / total, x.terms, x.missing)).ToArray();
        var best = scored[0];
        double? margin = scored.Length > 1 ? best.score - scored[1].score : null;
        bool robust = scored.Skip(1).All(row => best.score + best.downside >= row.score + row.upside);
        string? veto = IsFallback ? "model-fallback" : truncated ? "candidate-limit" : duplicate ? "duplicate-identity" : Veto(best.row, rows);
        string reason = veto ?? (!robust ? "missing-evidence"
            : (margin is { } m && m < Margin) || ranked[0].Probability < MinConfidence ? "competing-candidates" : "leading-evidence");
        return new RankingDecision(Version, best.row.Id, reason == "leading-evidence", ranked[0].Probability, margin, reason, ranked);
    }

    private static double? Value(RankingCandidate candidate, string name) =>
        candidate.Facts.TryGetValue(name, out var value) && value is { } v && double.IsFinite(v) ? Math.Clamp(v, 0, 1) : null;

    private static bool Is(RankingCandidate candidate, string name) => Value(candidate, name) == 1;

    // Non-learnable safety rules, identical to the extension runtime.
    private string? Veto(RankingCandidate best, IReadOnlyList<RankingCandidate> rows)
    {
        if (Schema == FeatureSchema.PlayerIntent)
        {
            if (Is(best, "incidentalLoop")) return "incidental-loop";
            if (Is(best, "adContext") && !Is(best, "interaction")) return "advertisement-context";
            if (!Is(best, "interaction") && rows.Any(row => !ReferenceEquals(row, best) && Is(row, "interaction"))) return "conflicting-interaction";
            return null;
        }
        if (Is(best, "stale") || Value(best, "currentGeneration") == 0) return "stale-generation";
        if (Is(best, "fragmentOnly")) return "fragment-only";
        if (Is(best, "adContext")) return "advertisement-context";
        if (Is(best, "hypothesized") && !Is(best, "exactPlayerUrl") && !Is(best, "resourceWitness")) return "hypothesized-source";
        if (!Is(best, "exactPlayerUrl") && !Is(best, "resourceWitness") && !Is(best, "manifestParent")) return "unproven-association";
        return null;
    }
    /// <summary>Applies sign-constrained online logistic weight deltas without mutating the immutable base model.</summary>
    public RankingModel WithOnlineAdjustments(IReadOnlyDictionary<string, double>? deltas)
    {
        if (deltas is null || deltas.Count == 0) return this;
        var adjusted = new Dictionary<string, double>(Weights, StringComparer.Ordinal);
        foreach (var (name, delta) in deltas)
        {
            if (!adjusted.TryGetValue(name, out double baseWeight) || !double.IsFinite(delta)) continue;
            double next = Math.Clamp(baseWeight + Math.Clamp(delta, -4.0, 4.0), -WeightLimit, WeightLimit);
            adjusted[name] = Schema.Negative.Contains(name) ? Math.Min(0, next) : Math.Max(0, next);
        }
        string nextChecksum = ComputeChecksum(Schema, Version, adjusted, Temperature, Margin, MinConfidence);
        return new RankingModel(Schema, Version, adjusted, Temperature, Margin, MinConfidence, nextChecksum)
        {
            IsFallback = IsFallback
        };
    }
}
