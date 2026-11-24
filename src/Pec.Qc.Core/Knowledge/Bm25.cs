using System.Text.RegularExpressions;

namespace Pec.Qc.Core.Knowledge;

/// <summary>Small BM25 ranking over in-memory documents; good enough for a few tens of thousands of chunks.</summary>
public sealed class Bm25<T>
{
    private readonly List<(T Item, Dictionary<string, int> Tf, int Len)> _docs = [];
    private readonly Dictionary<string, int> _df = new();
    private long _totalLen;
    private double _avgLen;
    private const double K1 = 1.4, B = 0.75;

    public int Count => _docs.Count;

    /// <summary>
    /// Words of a text. A compound such as a line number (<c>2"-CD-60A07900-B400A-N-HS-017</c>) or a tag (<c>V-101A</c>) is kept whole AND split at its
    /// hyphens, underscores and dots, so a search for the class <c>B400A</c> or the area <c>60A07900</c> finds the lines that contain them,
    /// while the whole tag still ranks first for an exact search.
    /// </summary>
    public static IEnumerable<string> Tokens(string text)
    {
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+(?:[-_.][a-z0-9]+)*"))
        {
            yield return m.Value;
            if (m.Value.IndexOfAny(['-', '_', '.']) < 0) continue;
            foreach (var part in m.Value.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)) yield return part;
        }
    }

    public void Add(T item, string text)
    {
        var tf = new Dictionary<string, int>();
        var len = 0;
        foreach (var t in Tokens(text)) { tf[t] = tf.GetValueOrDefault(t) + 1; len++; }
        foreach (var t in tf.Keys) _df[t] = _df.GetValueOrDefault(t) + 1;
        _docs.Add((item, tf, len));
        _totalLen += len;
        _avgLen = (double)_totalLen / _docs.Count;      // running average: adding many chunks stays linear
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
