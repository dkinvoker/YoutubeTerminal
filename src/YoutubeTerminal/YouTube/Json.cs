using System.Text;
using System.Text.Json.Nodes;

namespace YoutubeTerminal.YouTube;

/// <summary>Bezpieczne, tolerancyjne odczyty z drzewa JSON (odpowiedzi InnerTube często zmieniają kształt).</summary>
internal static class J
{
    public static JsonNode? At(this JsonNode? n, params object[] path)
    {
        var cur = n;
        foreach (var p in path)
        {
            if (cur == null) return null;
            if (p is string key)
                cur = cur is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;
            else if (p is int idx)
                cur = cur is JsonArray a && idx >= 0 && idx < a.Count ? a[idx] : null;
            else return null;
        }
        return cur;
    }

    public static string Str(this JsonNode? n, params object[] path)
    {
        var v = path.Length == 0 ? n : n.At(path);
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue(out string? s)) return s ?? "";
            if (jv.TryGetValue(out long l)) return l.ToString();
            if (jv.TryGetValue(out double d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (jv.TryGetValue(out bool b)) return b ? "true" : "false";
        }
        return "";
    }

    public static long Long(this JsonNode? n, params object[] path)
    {
        var v = n.At(path);
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue(out long l)) return l;
            if (jv.TryGetValue(out string? s) && long.TryParse(s, out var p)) return p;
            if (jv.TryGetValue(out double d)) return (long)d;
        }
        return 0;
    }

    public static IEnumerable<JsonNode> Arr(this JsonNode? n, params object[] path)
    {
        var v = path.Length == 0 ? n : n.At(path);
        if (v is JsonArray a)
            foreach (var e in a)
                if (e != null) yield return e;
    }

    /// <summary>Tekst z obiektu YouTube: simpleText / runs[] / content.</summary>
    public static string Text(this JsonNode? n, params object[] path)
    {
        var v = path.Length == 0 ? n : n.At(path);
        if (v == null) return "";
        if (v is JsonValue) return v.Str();
        var simple = v.Str("simpleText");
        if (simple.Length > 0) return simple;
        var content = v.Str("content");
        if (content.Length > 0) return content;
        if (v.At("runs") is JsonArray runs)
        {
            var sb = new StringBuilder();
            foreach (var r in runs) sb.Append(r.Str("text"));
            return sb.ToString();
        }
        return "";
    }

    /// <summary>Szuka w głąb pierwszego klucza o podanej nazwie.</summary>
    public static JsonNode? Find(this JsonNode? n, string key, int maxDepth = 40)
    {
        if (n == null || maxDepth < 0) return null;
        if (n is JsonObject o)
        {
            if (o.TryGetPropertyValue(key, out var v) && v != null) return v;
            foreach (var kv in o)
            {
                var r = Find(kv.Value, key, maxDepth - 1);
                if (r != null) return r;
            }
        }
        else if (n is JsonArray a)
        {
            foreach (var e in a)
            {
                var r = Find(e, key, maxDepth - 1);
                if (r != null) return r;
            }
        }
        return null;
    }

    /// <summary>Zwraca wszystkie wartości dla klucza (w kolejności dokumentu).</summary>
    public static void FindAll(this JsonNode? n, string key, List<JsonNode> results, int maxDepth = 60)
    {
        if (n == null || maxDepth < 0) return;
        if (n is JsonObject o)
        {
            foreach (var kv in o)
            {
                if (kv.Key == key && kv.Value != null) results.Add(kv.Value);
                else FindAll(kv.Value, key, results, maxDepth - 1);
            }
        }
        else if (n is JsonArray a)
        {
            foreach (var e in a) FindAll(e, key, results, maxDepth - 1);
        }
    }

    public static string BestThumb(this JsonNode? thumbs, int preferWidth = 360)
    {
        string best = "";
        long bestDiff = long.MaxValue;
        foreach (var t in thumbs.Arr())
        {
            var url = t.Str("url");
            if (url.Length == 0) continue;
            long w = t.Long("width");
            long diff = w == 0 ? 100000 : Math.Abs(w - preferWidth);
            if (diff < bestDiff) { bestDiff = diff; best = url; }
        }
        if (best.StartsWith("//", StringComparison.Ordinal)) best = "https:" + best;
        return best;
    }
}
