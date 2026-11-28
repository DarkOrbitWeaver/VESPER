using System.Text.Json;
using System.Text.Json.Nodes;
using Vesper.Intelligence;
using Xunit;

namespace Vesper.Intelligence.Tests;

public class RankingModelTests
{
    private static string ModelPath(string relative) => Path.Combine(AppContext.BaseDirectory, "models", relative);

    public static IEnumerable<object[]> Artifacts() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "models"), "*.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => new object[] { Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "models"), path) });

    [Theory]
    [MemberData(nameof(Artifacts))]
    public void EveryArtifactWrittenByTheJavaScriptToolchainValidatesWithAnIdenticalChecksum(string relative)
    {
        var model = RankingModel.Parse(File.ReadAllText(ModelPath(relative)));
        Assert.Equal(JsonNode.Parse(File.ReadAllText(ModelPath(relative)))!["checksum"]!.GetValue<string>(), model.Checksum);
    }

    [Fact]
    public void EmbeddedModelsLoad()
    {
        Assert.StartsWith("1.", RankingModel.LoadEmbedded("source-association").Version);
        Assert.StartsWith("2.", RankingModel.LoadEmbedded("player-intent").Version);
    }

    [Fact]
    public void ObservedFailuresChangeRouteChoiceAndOlderEvidenceDecays()
    {
        var learner = new OnlineLearner();
        string site = "media.example";
        string[] routes = ["adapter", "observed"];
        var now = new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("adapter", learner.SelectBestRoute(site, routes, now));
        learner.RecordRouteOutcome(site, "adapter", false, now: now);
        learner.RecordRouteOutcome(site, "observed", true, 2_000_000, now);
        Assert.Equal("observed", learner.SelectBestRoute(site, routes, now));
        Assert.Equal("adapter", learner.SelectBestRoute("other.example", routes, now));
        learner.RecordRouteOutcome(site, "adapter", true, 3_000_000, now.AddDays(365));
        Assert.Equal("adapter", learner.SelectBestRoute(site, routes, now.AddDays(365)));
    }

    [Fact]
    public void RouteLearningSurvivesRestartWithoutStoringOriginOrRouteText()
    {
        string root = Path.Combine(Path.GetTempPath(), "vesper-learning-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "outcomes.json");
        try
        {
            var learner = new OnlineLearner(path);
            var now = DateTimeOffset.UtcNow;
            learner.RecordRouteOutcome("https://private.example", "route-a", false, now: now);
            learner.RecordRouteOutcome("https://private.example", "route-b", true, double.NaN, now);
            var reopened = new OnlineLearner(path);
            Assert.Equal("route-b", reopened.SelectBestRoute("https://private.example", ["route-a", "route-b"], now));
            string saved = File.ReadAllText(path);
            Assert.DoesNotContain("private.example", saved);
            Assert.DoesNotContain("route-b", saved);
            Assert.Equal("route-a", reopened.SelectBestRoute("https://other.example", ["route-a", "route-b"], now));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string Mutate(Action<JsonObject> change)
    {
        var node = JsonNode.Parse(File.ReadAllText(ModelPath("active/source-association.json")))!.AsObject();
        change(node);
        return node.ToJsonString();
    }

    [Fact]
    public void TamperedSignViolatingOrIncompatibleModelsAreRejected()
    {
        Assert.Throws<InvalidModelException>(() => RankingModel.Parse(Mutate(m => m["weights"]!["playerBound"] = 5.5)));
        Assert.Throws<InvalidModelException>(() => RankingModel.Parse(Mutate(m => m["weights"]!["stale"] = 1)));
        Assert.Throws<InvalidModelException>(() => RankingModel.Parse(Mutate(m => m["schema"] = "vesper.source-association/9")));
        Assert.Throws<InvalidModelException>(() => RankingModel.Parse(Mutate(m => m["kind"] = "neural")));
        Assert.Throws<InvalidModelException>(() => RankingModel.Parse(Mutate(m => m["weights"]!.AsObject().Remove("declared"))));
        Assert.Throws<InvalidModelException>(() => RankingModel.Parse(Mutate(m => m["format"] = "other")));
    }

    private static readonly RankingModel Source = RankingModel.LoadEmbedded("source-association");

    [Fact]
    public void ObservedPlayerSourceBeatsHypothesizedMarkupDeterministically()
    {
        var sources = new[]
        {
            new SourceEvidence("guess", SourceProvenance.PageStructure, SourceShape.Direct),
            new SourceEvidence("played", SourceProvenance.PlayerElement, SourceShape.Direct),
        };
        var first = SourcePrioritizer.Associate(sources);
        Assert.Equal("played", first.Selected);
        Assert.True(first.Decisive);
        for (int i = 0; i < 10; i++) Assert.Equal(first.Ranked.Select(r => r.Score), SourcePrioritizer.Associate(sources.Reverse()).Ranked.Select(r => r.Score));
    }

    [Theory]
    [InlineData("stale-generation")]
    [InlineData("fragment-only")]
    [InlineData("advertisement-context")]
    [InlineData("hypothesized-source")]
    public void NegativeEvidenceIsVetoedRegardlessOfScore(string reason)
    {
        var evidence = reason switch
        {
            "stale-generation" => new SourceEvidence("s", SourceProvenance.PlayerGeneration, SourceShape.Hls, CurrentGeneration: false),
            "fragment-only" => new SourceEvidence("s", SourceProvenance.PlayerGeneration, SourceShape.Fragment),
            "advertisement-context" => new SourceEvidence("s", SourceProvenance.PlayerGeneration, SourceShape.Hls, AdContext: true),
            _ => new SourceEvidence("s", SourceProvenance.PageStructure, SourceShape.Hls),
        };
        var decision = SourcePrioritizer.Associate([evidence]);
        Assert.False(decision.Decisive);
        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void DuplicateAndOversizedCandidateSetsAbstain()
    {
        var same = new SourceEvidence("x", SourceProvenance.PlayerElement, SourceShape.Direct);
        Assert.Equal("duplicate-identity", SourcePrioritizer.Associate([same, same]).Reason);
        var many = Enumerable.Range(0, 257).Select(i => new RankingCandidate("c" + i, new Dictionary<string, double?> { ["playerBound"] = 1 })).ToArray();
        Assert.Equal("candidate-limit", Source.Rank(many).Reason);
    }

    [Fact]
    public void PartiallyReportedEvidenceIsUncertaintyAndUnreportedEvidenceIsAbsent()
    {
        // "a" leads by more than the margin, but "b" has unreported witness and manifest evidence that could outweigh it.
        var decision = Source.Rank([
            new RankingCandidate("a", new Dictionary<string, double?> { ["exactPlayerUrl"] = 1, ["playerBound"] = 1, ["resourceWitness"] = 0, ["manifestParent"] = 0 }),
            new RankingCandidate("b", new Dictionary<string, double?> { ["playerBound"] = 1, ["resourceWitness"] = null, ["manifestParent"] = null }),
        ]);
        Assert.Equal("a", decision.Selected);
        Assert.False(decision.Decisive);
        Assert.Equal("missing-evidence", decision.Reason);
        Assert.Contains("resourceWitness", decision.Ranked.Single(r => r.Id == "b").Missing);
        Assert.DoesNotContain("declared", decision.Ranked[0].Terms.Keys);
    }

    [Fact]
    public void PrioritizerOnlyDemotesNegativeEvidenceAndOtherwiseKeepsCallerOrder()
    {
        var items = new[]
        {
            new SourceEvidence("loop", SourceProvenance.PlayerGeneration, SourceShape.Direct, IncidentalLoop: true),
            new SourceEvidence("page-hls", SourceProvenance.PageStructure, SourceShape.Hls),
            new SourceEvidence("segment", SourceProvenance.PageStructure, SourceShape.Fragment),
            new SourceEvidence("page-mp4", SourceProvenance.PageStructure, SourceShape.Direct),
        };
        Assert.Equal(["page-hls", "page-mp4", "loop", "segment"], SourcePrioritizer.Order(items, x => x).Select(x => x.Id));
    }

    [Fact]
    public void ResourceCompatibilityRequiresStrongValidators()
    {
        Assert.Equal(Interchangeability.NeedsVerification, ResourceCompatibility.Compare(new(ETag: "\"a\"", Length: 5), new(ETag: "\"a\"", Length: 5)).Verdict);
        var saved = new ResourceIdentity(Length: 5, ETag: "\"a\"", Url: "https://cdn.example/a?token=x");
        Assert.Equal(Interchangeability.Exact, ResourceCompatibility.Compare(saved, saved).Verdict);
        Assert.Equal(Interchangeability.NeedsVerification, ResourceCompatibility.Compare(saved, saved with { Url = "https://cdn.example/a?token=y" }).Verdict);
        Assert.Equal(Interchangeability.NeedsVerification, ResourceCompatibility.Compare(saved, saved with { Url = "https://other.example/a?token=x" }).Verdict);
        Assert.Equal(Interchangeability.Incompatible, ResourceCompatibility.Compare(new(ETag: "\"a\"", Length: 5), new(ETag: "\"b\"", Length: 5)).Verdict);
        Assert.Equal(Interchangeability.Incompatible, ResourceCompatibility.Compare(new(ProgramId: "p", FormatId: "720"), new(ProgramId: "p", FormatId: "1080")).Verdict);
        Assert.Equal(Interchangeability.NeedsVerification, ResourceCompatibility.Compare(new(ETag: "W/\"a\"", Length: 5), new(ETag: "W/\"a\"", Length: 5)).Verdict);
        Assert.Equal(Interchangeability.NeedsVerification, ResourceCompatibility.Compare(new(), new()).Verdict);
    }
}
