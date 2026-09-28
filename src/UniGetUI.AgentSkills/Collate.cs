// Approximation of String.prototype.localeCompare (ICU root collation).
//
// Hashes such as computeSkillFolderHash sort file paths with localeCompare,
// whose order differs from ordinal order (case-insensitive at the primary level,
// punctuation before digits before letters, lowercase before uppercase as a
// tie-break). Matching it keeps computedHash values identical to the TS CLI's.

namespace Skills;

internal static class Collate
{
    /// Primary weight for ASCII punctuation/symbols in CLDR root order.
    private const string PunctOrder = "_-,;:!?.'\"()[]{}@*/\\&#%`^+<=>|~$";

    private static int? Primary(int c)
    {
        if ((c < 0x20 && c is not ('\t' or '\n' or 0x0b or 0x0c or '\r')) || (c >= 0x7f && c < 0xa0)) return null;
        switch (c)
        {
            case '\t': return 1;
            case '\n': return 2;
            case 0x0b: return 3;
            case 0x0c: return 4;
            case '\r': return 5;
            case ' ': return 6;
        }
        if (c < 0x80)
        {
            var i = PunctOrder.IndexOf((char)c);
            if (i >= 0) return 100 + i;
            if (c is >= '0' and <= '9') return 200 + (c - '0');
            if (c is >= 'a' and <= 'z') return 300 + (c - 'a');
            if (c is >= 'A' and <= 'Z') return 300 + (c - 'A');
        }
        if (c is >= 0xD800 and <= 0xDFFF) return 1000 + c; // lone surrogate
        var lower = CodePoints(char.ConvertFromUtf32(c).ToLowerInvariant()).ToList();
        return 1000 + (lower.Count == 1 ? lower[0] : c);
    }

    private static IEnumerable<int> CodePoints(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            else
            {
                yield return s[i];
            }
        }
    }

    private static int Tertiary(int c)
    {
        if (c is >= 0xD800 and <= 0xDFFF) return 0;
        var s = char.ConvertFromUtf32(c);
        return s != s.ToLowerInvariant() ? 1 : 0;
    }

    private static int CompareLists(List<int> a, List<int> b)
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var c = a[i].CompareTo(b[i]);
            if (c != 0) return c;
        }
        return a.Count.CompareTo(b.Count);
    }

    public static int LocaleCompare(string a, string b)
    {
        var pa = CodePoints(a).Select(Primary).Where(p => p.HasValue).Select(p => p!.Value).ToList();
        var pb = CodePoints(b).Select(Primary).Where(p => p.HasValue).Select(p => p!.Value).ToList();
        var c = CompareLists(pa, pb);
        if (c != 0) return c;
        var ta = CodePoints(a).Where(x => Primary(x).HasValue).Select(Tertiary).ToList();
        var tb = CodePoints(b).Where(x => Primary(x).HasValue).Select(Tertiary).ToList();
        return CompareLists(ta, tb);
    }

    /// Stable sort (JS Array.prototype.sort is stable).
    public static List<T> StableSort<T>(IEnumerable<T> items, Comparison<T> cmp) =>
        items.Select((x, i) => (x, i)).OrderBy(t => t, Comparer<(T x, int i)>.Create((l, r) =>
        {
            var c = cmp(l.x, r.x);
            return c != 0 ? c : l.i.CompareTo(r.i);
        })).Select(t => t.x).ToList();

    /// Default JS sort: UTF-16 code unit order.
    public static int CodeUnit(string a, string b) => string.CompareOrdinal(a, b);
}
