using System.Globalization;
using System.Text.Json;

namespace Vesper.MediaResolver.Utilities;

/// <summary>
/// Null-propagating navigation helpers over <see cref="JsonElement"/>. Missing properties, nulls and type
/// mismatches all yield null instead of throwing, which keeps adapters short and tolerant of source changes.
/// </summary>
public static class JsonExt
{
    public static readonly JsonDocumentOptions DocOptions = new() { MaxDepth = 64, AllowTrailingCommas = true };

    public static JsonDocument? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json, DocOptions); }
        catch (JsonException) { return null; }
    }

    public static JsonElement? P(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public static JsonElement? P(this JsonElement? e, string name) => e is { } x ? x.P(name) : null;

    public static JsonElement? At(this JsonElement e, int i) =>
        e.ValueKind == JsonValueKind.Array && i >= 0 && i < e.GetArrayLength() ? e[i] : null;

    public static JsonElement? At(this JsonElement? e, int i) => e is { } x ? x.At(i) : null;

    public static string? Str(this JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        _ => null
    };

    public static string? Str(this JsonElement? e) => e is { } x ? x.Str() : null;

    public static long? Long(this JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt64(out var l) => l,
        JsonValueKind.Number when e.TryGetDouble(out var d) && d is > -9e18 and < 9e18 => (long)d,
        JsonValueKind.String when long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
        _ => null
    };

    public static long? Long(this JsonElement? e) => e is { } x ? x.Long() : null;

    public static int? Int(this JsonElement e) => e.Long() is { } l && l is >= int.MinValue and <= int.MaxValue ? (int)l : null;

    public static int? Int(this JsonElement? e) => e is { } x ? x.Int() : null;

    public static double? Dbl(this JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetDouble(out var d) => d,
        JsonValueKind.String when double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null
    };

    public static double? Dbl(this JsonElement? e) => e is { } x ? x.Dbl() : null;

    public static bool? Bool(this JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    public static bool? Bool(this JsonElement? e) => e is { } x ? x.Bool() : null;

    public static IEnumerable<JsonElement> Items(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : Enumerable.Empty<JsonElement>();

    public static IEnumerable<JsonElement> Items(this JsonElement? e) => e is { } x ? x.Items() : Enumerable.Empty<JsonElement>();

    public static IEnumerable<JsonProperty> Props(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? e.EnumerateObject() : Enumerable.Empty<JsonProperty>();

    public static IEnumerable<JsonProperty> Props(this JsonElement? e) => e is { } x ? x.Props() : Enumerable.Empty<JsonProperty>();

    /// <summary>Depth-first search for every object stored under a property with the given name (bounded).</summary>
    public static IEnumerable<JsonElement> FindAll(this JsonElement root, string propertyName, int maxResults = 2000)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(root);
        int found = 0;
        int visited = 0;
        while (stack.Count > 0 && found < maxResults && visited++ < 400_000)
        {
            var cur = stack.Pop();
            if (cur.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in cur.EnumerateObject())
                {
                    if (p.Name == propertyName && p.Value.ValueKind == JsonValueKind.Object) { found++; yield return p.Value; }
                    if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) stack.Push(p.Value);
                }
            }
            else if (cur.ValueKind == JsonValueKind.Array)
            {
                // push in reverse so results come out in document order
                var items = cur.EnumerateArray().ToList();
                for (int i = items.Count - 1; i >= 0; i--)
                    if (items[i].ValueKind is JsonValueKind.Object or JsonValueKind.Array) stack.Push(items[i]);
            }
        }
    }
}
