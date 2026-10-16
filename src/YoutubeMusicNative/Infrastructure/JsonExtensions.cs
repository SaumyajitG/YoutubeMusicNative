using System.Text;
using System.Text.Json.Nodes;

namespace YoutubeMusicNative.Infrastructure;

/// <summary>Null-safe navigation helpers for the deeply nested InnerTube JSON.</summary>
internal static class JsonExtensions
{
    public static JsonNode? Get(this JsonNode? n, string key) =>
        n is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    public static JsonNode? At(this JsonNode? n, int i) =>
        n is JsonArray a && i >= 0 && i < a.Count ? a[i] : null;

    /// <summary>Dotted path, numeric segments index into arrays: "tabs.0.tabRenderer".</summary>
    public static JsonNode? Path(this JsonNode? n, string path)
    {
        foreach (var seg in path.Split('.'))
        {
            if (n is null) return null;
            n = n is JsonArray && int.TryParse(seg, out var i) ? n.At(i) : n.Get(seg);
        }
        return n;
    }

    public static IEnumerable<JsonNode> Items(this JsonNode? n) =>
        n is JsonArray a ? a.OfType<JsonNode>() : [];

    public static string? Str(this JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static string? Str(this JsonNode? n, string path) => n.Path(path).Str();

    public static long? Long(this JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        if (v.TryGetValue<string>(out var s) && long.TryParse(s, out l)) return l;
        return null;
    }

    public static double? Double(this JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    /// <summary>Flattens a "runs" / "simpleText" text object into a plain string.</summary>
    public static string? Text(this JsonNode? n)
    {
        if (n is null) return null;
        if (n.Get("simpleText").Str() is { } simple) return simple;
        if (n.Get("runs") is JsonArray runs)
        {
            var sb = new StringBuilder();
            foreach (var r in runs) sb.Append(r.Get("text").Str());
            return sb.ToString();
        }
        return n.Str();
    }

    /// <summary>Depth-first search for the first property named <paramref name="key"/>.</summary>
    public static JsonNode? FindFirst(this JsonNode? n, string key)
    {
        switch (n)
        {
            case JsonObject o:
                if (o.TryGetPropertyValue(key, out var hit) && hit is not null) return hit;
                foreach (var kv in o)
                    if (kv.Value.FindFirst(key) is { } found) return found;
                break;
            case JsonArray a:
                foreach (var item in a)
                    if (item.FindFirst(key) is { } found) return found;
                break;
        }
        return null;
    }

    /// <summary>All properties named <paramref name="key"/> (does not descend into matches).</summary>
    public static IEnumerable<JsonNode> FindAll(this JsonNode? n, string key)
    {
        var results = new List<JsonNode>();
        Collect(n, key, results);
        return results;

        static void Collect(JsonNode? node, string key, List<JsonNode> acc)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var kv in o)
                    {
                        if (kv.Key == key && kv.Value is not null) acc.Add(kv.Value);
                        else Collect(kv.Value, key, acc);
                    }
                    break;
                case JsonArray a:
                    foreach (var item in a) Collect(item, key, acc);
                    break;
            }
        }
    }
}
