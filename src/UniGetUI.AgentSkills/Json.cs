// JSON with JavaScript semantics over System.Text.Json.Nodes:
//  * Parse = JSON.parse (duplicate keys: last value wins at the first position;
//    numbers are doubles; a leading BOM is an error, as in JSON.parse).
//  * Stringify = JSON.stringify(value, null, indent), byte-for-byte: minimal
//    escaping, JS number formatting, and JS object key order (integer-like keys
//    first, ascending).

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Skills;

internal sealed class JsonParseException(string message) : Exception(message);

internal static class Json
{
    // ─── Parse ───

    public static JsonNode? Parse(string text)
    {
        var p = new Parser(text);
        p.SkipWs();
        var v = p.ParseValue();
        p.SkipWs();
        if (p.Pos != text.Length) throw new JsonParseException($"Unexpected non-whitespace character after JSON at position {p.Pos}");
        return v;
    }

    public static bool TryParse(string text, out JsonNode? node)
    {
        try
        {
            node = Parse(text);
            return true;
        }
        catch (JsonParseException)
        {
            node = null;
            return false;
        }
    }

    private sealed class Parser(string s)
    {
        public int Pos;

        public void SkipWs()
        {
            while (Pos < s.Length && s[Pos] is ' ' or '\t' or '\n' or '\r') Pos++;
        }

        private JsonParseException Error() =>
            new(Pos < s.Length ? $"Unexpected token '{s[Pos]}' at position {Pos}" : "Unexpected end of JSON input");

        public JsonNode? ParseValue()
        {
            if (Pos >= s.Length) throw Error();
            switch (s[Pos])
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return JsonValue.Create(ParseString());
                case 't': Expect("true"); return JsonValue.Create(true);
                case 'f': Expect("false"); return JsonValue.Create(false);
                case 'n': Expect("null"); return null;
                default:
                    if (s[Pos] == '-' || char.IsAsciiDigit(s[Pos])) return JsonValue.Create(ParseNumber());
                    throw Error();
            }
        }

        private void Expect(string word)
        {
            if (string.CompareOrdinal(s, Pos, word, 0, word.Length) != 0) throw Error();
            Pos += word.Length;
        }

        private double ParseNumber()
        {
            var start = Pos;
            if (s[Pos] == '-') Pos++;
            if (Pos >= s.Length) throw Error();
            if (s[Pos] == '0') Pos++;
            else if (char.IsAsciiDigit(s[Pos])) while (Pos < s.Length && char.IsAsciiDigit(s[Pos])) Pos++;
            else throw Error();
            if (Pos < s.Length && s[Pos] == '.')
            {
                Pos++;
                if (Pos >= s.Length || !char.IsAsciiDigit(s[Pos])) throw Error();
                while (Pos < s.Length && char.IsAsciiDigit(s[Pos])) Pos++;
            }
            if (Pos < s.Length && s[Pos] is 'e' or 'E')
            {
                Pos++;
                if (Pos < s.Length && s[Pos] is '+' or '-') Pos++;
                if (Pos >= s.Length || !char.IsAsciiDigit(s[Pos])) throw Error();
                while (Pos < s.Length && char.IsAsciiDigit(s[Pos])) Pos++;
            }
            return double.Parse(s.AsSpan(start, Pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private string ParseString()
        {
            Pos++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (Pos >= s.Length) throw Error();
                var c = s[Pos++];
                if (c == '"') return sb.ToString();
                if (c < 0x20) { Pos--; throw Error(); }
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (Pos >= s.Length) throw Error();
                var e = s[Pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (Pos + 4 > s.Length || !int.TryParse(s.AsSpan(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp)) throw Error();
                        sb.Append((char)cp);
                        Pos += 4;
                        break;
                    default:
                        Pos--;
                        throw Error();
                }
            }
        }

        private JsonArray ParseArray()
        {
            Pos++;
            var arr = new JsonArray();
            SkipWs();
            if (Pos < s.Length && s[Pos] == ']')
            {
                Pos++;
                return arr;
            }
            while (true)
            {
                SkipWs();
                arr.Add(ParseValue());
                SkipWs();
                if (Pos >= s.Length) throw Error();
                if (s[Pos] == ',') { Pos++; continue; }
                if (s[Pos] == ']') { Pos++; return arr; }
                throw Error();
            }
        }

        private JsonObject ParseObject()
        {
            Pos++;
            var obj = new JsonObject();
            SkipWs();
            if (Pos < s.Length && s[Pos] == '}')
            {
                Pos++;
                return obj;
            }
            while (true)
            {
                SkipWs();
                if (Pos >= s.Length || s[Pos] != '"') throw Error();
                var key = ParseString();
                SkipWs();
                if (Pos >= s.Length || s[Pos] != ':') throw Error();
                Pos++;
                SkipWs();
                obj[key] = ParseValue(); // duplicate keys: last wins, first position kept
                SkipWs();
                if (Pos >= s.Length) throw Error();
                if (s[Pos] == ',') { Pos++; continue; }
                if (s[Pos] == '}') { Pos++; return JsKeyOrder(obj); }
                throw Error();
            }
        }
    }

    // ─── Key order ───

    /// Whether a key is a JS array index ("0", "42", but not "007" or "-1").
    private static bool IsArrayIndex(string k) =>
        k.Length > 0 && k.All(char.IsAsciiDigit) && (k == "0" || k[0] != '0') && ulong.TryParse(k, out var v) && v < uint.MaxValue;

    /// Entries in JS iteration order, without modifying the object.
    public static IEnumerable<KeyValuePair<string, JsonNode?>> OrderedEntries(JsonObject obj)
    {
        if (!obj.Any(kv => IsArrayIndex(kv.Key))) return obj;
        return obj.Where(kv => IsArrayIndex(kv.Key)).OrderBy(kv => ulong.Parse(kv.Key, CultureInfo.InvariantCulture))
            .Concat(obj.Where(kv => !IsArrayIndex(kv.Key)))
            .ToList();
    }

    /// Reorder an object the way a JS object iterates: array-index keys first in
    /// ascending numeric order, then all other keys in insertion order. Consumes
    /// `obj` (its children move to the returned object) when reordering is needed.
    public static JsonObject JsKeyOrder(JsonObject obj)
    {
        if (!obj.Any(kv => IsArrayIndex(kv.Key))) return obj;
        var entries = obj.ToList();
        obj.Clear();
        var result = new JsonObject();
        foreach (var kv in entries.Where(kv => IsArrayIndex(kv.Key)).OrderBy(kv => ulong.Parse(kv.Key, CultureInfo.InvariantCulture)))
            result[kv.Key] = kv.Value;
        foreach (var kv in entries.Where(kv => !IsArrayIndex(kv.Key)))
            result[kv.Key] = kv.Value;
        return result;
    }

    // ─── Stringify ───

    public static string Stringify(JsonNode? node, int indent = 2)
    {
        var sb = new StringBuilder();
        Write(sb, node, indent, 0);
        return sb.ToString();
    }

    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        WriteString(sb, s);
        return sb.ToString();
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        sb.Append(c).Append(s[i + 1]);
                        i++;
                    }
                    else if (char.IsSurrogate(c))
                    {
                        // well-formed JSON.stringify escapes lone surrogates
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }

    /// JS Number.prototype.toString for finite doubles.
    public static string NumberToString(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
        if (d == 0) return "0";
        if (Math.Abs(d) < 1e21 && Math.Abs(d) >= 1e-6 && d == Math.Floor(d)) return ((decimal)d).ToString(CultureInfo.InvariantCulture);
        var r = d.ToString("R", CultureInfo.InvariantCulture);
        if (!r.Contains('E')) return r;
        // C# "1E+21" → JS "1e+21"; "1E-07" → "1e-7"
        var parts = r.Split('E');
        var exp = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var abs = Math.Abs(d);
        if (abs >= 1e-6 && abs < 1e21)
            return d.ToString("0.#####################", CultureInfo.InvariantCulture);
        return parts[0] + "e" + (exp >= 0 ? "+" : "-") + Math.Abs(exp).ToString(CultureInfo.InvariantCulture);
    }

    private static void Write(StringBuilder sb, JsonNode? node, int indent, int level)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject obj:
            {
                var ordered = OrderedEntries(obj).ToList();
                if (ordered.Count == 0)
                {
                    sb.Append("{}");
                    return;
                }
                sb.Append('{');
                var first = true;
                foreach (var kv in ordered)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent, level + 1);
                    WriteString(sb, kv.Key);
                    sb.Append(indent > 0 ? ": " : ":");
                    Write(sb, kv.Value, indent, level + 1);
                }
                NewLine(sb, indent, level);
                sb.Append('}');
                return;
            }
            case JsonArray arr:
            {
                if (arr.Count == 0)
                {
                    sb.Append("[]");
                    return;
                }
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    NewLine(sb, indent, level + 1);
                    Write(sb, arr[i], indent, level + 1);
                }
                NewLine(sb, indent, level);
                sb.Append(']');
                return;
            }
            case JsonValue v:
                if (v.TryGetValue<string>(out var str)) WriteString(sb, str);
                else if (v.TryGetValue<bool>(out var b)) sb.Append(b ? "true" : "false");
                else if (v.TryGetValue<double>(out var d)) sb.Append(NumberToString(d));
                else if (v.TryGetValue<long>(out var l)) sb.Append(l.ToString(CultureInfo.InvariantCulture));
                else if (v.TryGetValue<int>(out var n)) sb.Append(n.ToString(CultureInfo.InvariantCulture));
                else throw new NotSupportedException("Unsupported JSON value type.");
                return;
        }
    }

    private static void NewLine(StringBuilder sb, int indent, int level)
    {
        if (indent <= 0) return;
        sb.Append('\n').Append(' ', indent * level);
    }

    // ─── JS-style accessors ───

    /// Property value if the node is an object containing the key.
    public static JsonNode? Get(JsonNode? node, string key) =>
        node is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    public static bool Has(JsonNode? node, string key) => node is JsonObject o && o.ContainsKey(key);

    /// String value, or null when absent or not a string.
    public static string? Str(JsonNode? node, string key) => AsString(Get(node, key));

    public static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// Truthy string value (JS `entry.x || …` semantics).
    public static string? NonEmpty(JsonNode? node, string key) => Str(node, key) is { Length: > 0 } s ? s : null;

    public static double? AsNumber(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d
        : node is JsonValue v2 && v2.TryGetValue<long>(out var l) ? l
        : node is JsonValue v3 && v3.TryGetValue<int>(out var i) ? i
        : null;

    public static bool? AsBool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static List<string>? StringArray(JsonNode? node, string key) =>
        Get(node, key) is JsonArray a ? a.Select(x => AsString(x) ?? "").ToList() : null;

    /// JS truthiness of a present value (absent → pass `present: false`).
    public static bool Truthy(JsonNode? node, bool present = true)
    {
        if (!present || node == null) return false;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s)) return s.Length > 0;
            var d = AsNumber(v);
            if (d.HasValue) return d.Value != 0 && !double.IsNaN(d.Value);
        }
        return true;
    }

    /// JS truthiness of `obj[key]`.
    public static bool TruthyProp(JsonNode? obj, string key) => Has(obj, key) && Truthy(Get(obj, key));

    public static string TypeOf(JsonNode? obj, string key)
    {
        if (!Has(obj, key)) return "undefined";
        var v = Get(obj, key);
        return v switch
        {
            null => "object",
            JsonObject or JsonArray => "object",
            JsonValue jv when jv.TryGetValue<string>(out _) => "string",
            JsonValue jv when jv.TryGetValue<bool>(out _) => "boolean",
            _ => "number",
        };
    }

    public static JsonNode? Clone(JsonNode? n) => n?.DeepClone();
}
