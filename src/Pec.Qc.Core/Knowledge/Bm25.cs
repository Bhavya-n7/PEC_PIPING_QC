using System.Text.RegularExpressions;

namespace Pec.Qc.Core.Knowledge;

/// <summary>Small BM25 ranking over in-memory documents; good enough for a few tens of thousands of chunks.</summary>
public sealed class Bm25<T>
{
    private readonly List<(T Item, Dictionary<string, int> Tf, int Len)> _docs = [];
    private readonly Dictionary<string, int> _df = new();
    private double _avgLen;
    private const double K1 = 1.4, B = 0.75;

    public int Count => _docs.Count;

    public static IEnumerable<string> Tokens(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+(?:[-_.][a-z0-9]+)*").Select(m => m.Value);

    public void Add(T item, string text)
    {
        var tf = new Dictionary<string, int>();
        var len = 0;
        foreach (var t in Tokens(text)) { tf[t] = tf.GetValueOrDefault(t) + 1; len++; }
        foreach (var t in tf.Keys) _df[t] = _df.GetValueOrDefault(t) + 1;
        _docs.Add((item, tf, len));
        _avgLen = _docs.Average(d => d.Len);
    }

    public List<(T Item, double Score)> Search(string query, int max = 5)
    {
        var terms = Tokens(query).Distinct().ToList();
        if (terms.Count == 0 || _docs.Count == 0) return [];
        var n = _docs.Count;
        var scored = new List<(T, double)>();
        foreach (var (item, tf, len) in _docs)
        {
            double s = 0;
            foreach (var t in terms)
            {
                if (!tf.TryGetValue(t, out var f)) continue;
                var idf = Math.Log(1 + (n - _df[t] + 0.5) / (_df[t] + 0.5));
                s += idf * f * (K1 + 1) / (f + K1 * (1 - B + B * len / _avgLen));
            }
            if (s > 0) scored.Add((item, s));
        }
        return scored.OrderByDescending(x => x.Item2).Take(max).ToList();
    }
}
