// Standalone use of Vesper.Intelligence without the VESPER application.
//   dotnet run --project samples/Vesper.Intelligence.Sample            (built-in example)
//   dotnet run --project samples/Vesper.Intelligence.Sample -- evidence.json
// evidence.json: [{"id":"a","provenance":"PlayerElement","shape":"Direct","currentGeneration":true}, ...]
using System.Text.Json;
using System.Text.Json.Serialization;
using Vesper.Intelligence;

var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
SourceEvidence[] sources = args.Length > 0
    ? JsonSerializer.Deserialize<SourceEvidence[]>(File.ReadAllText(args[0]), options) ?? []
    :
    [
        new("player-mp4", SourceProvenance.PlayerElement, SourceShape.Direct),
        new("old-manifest", SourceProvenance.PlayerGeneration, SourceShape.Hls, CurrentGeneration: false),
        new("ad-manifest", SourceProvenance.PlayerGeneration, SourceShape.Hls, AdContext: true),
        new("markup-guess", SourceProvenance.PageStructure, SourceShape.Direct),
    ];

var decision = SourcePrioritizer.Associate(sources);
Console.WriteLine($"model {decision.ModelVersion}: selected={decision.Selected ?? "none"} decisive={decision.Decisive} reason={decision.Reason} confidence={decision.Confidence:F3}");
foreach (var row in decision.Ranked)
    Console.WriteLine($"  {row.Id,-14} score={row.Score,7:F2} p={row.Probability:F3} missing=[{string.Join(',', row.Missing)}]");
Console.WriteLine("verification order: " + string.Join(", ", SourcePrioritizer.Order(sources, s => s).Select(s => s.Id)));
Console.WriteLine("A decisive association orders verification only; acquisition still requires explicit intent and verified bytes.");
