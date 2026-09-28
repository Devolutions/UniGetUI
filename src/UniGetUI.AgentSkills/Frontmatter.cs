// Minimal YAML frontmatter parser (port of frontmatter.ts) plus a small YAML
// emitter modeled on the `yaml` package's stringify defaults, used to rewrite
// Eve SKILL.md frontmatter.
//
// YamlDotNet provides the syntax tree; plain scalars are resolved with the YAML
// 1.2 core schema, which is what the `yaml` npm package uses by default
// (`yes`/`on` stay strings, `0x1F` is a number, …).

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Skills;

internal sealed record FrontmatterResult(JsonObject Data, string Content);

internal sealed class YamlParseException(string message) : Exception(message);

/// How YAML scalars are typed and mappings ordered.
internal enum YamlFlavor
{
    /// The `yaml` npm package (YAML 1.2 core schema, JS object key order), as
    /// in the reference CLI.
    Js,

    /// The Rust port's serde_yaml 0.9 path, used by the extensions: leading-zero
    /// digit strings stay strings, `0b` integers, 128-bit integers are errors,
    /// non-finite floats become null, and keys keep document order.
    Serde,
}

internal static partial class Frontmatter
{
    [GeneratedRegex(@"\A---\r?\n([\s\S]*?)\r?\n---\r?\n?([\s\S]*)\z")]
    private static partial Regex FmRegex();

    /// Parse frontmatter. Only plain YAML `---` blocks are supported (never
    /// `---js`), so there is no code-execution path.
    /// <exception cref="YamlParseException">Invalid YAML.</exception>
    public static FrontmatterResult Parse(string raw, YamlFlavor flavor = YamlFlavor.Js)
    {
        var m = FmRegex().Match(raw);
        if (!m.Success) return new FrontmatterResult(new JsonObject(), raw);
        var value = ParseYaml(m.Groups[1].Value, flavor);
        return new FrontmatterResult(value as JsonObject ?? new JsonObject(), m.Groups[2].Value);
    }

    // ─── YAML → JSON ───

    /// Base of the private-use range used to smuggle non-printable characters
    /// through the YAML parser (libyaml-style parsers reject them; the `yaml`
    /// package accepts them in scalars and the CLI strips them later).
    private const int ShadowBase = 0xF0000;

    private static bool IsNonPrintable(int c) =>
        c is (>= 0x00 and <= 0x08) or 0x0B or 0x0C or (>= 0x0E and <= 0x1F) or 0x7F or (>= 0x80 and <= 0x84) or (>= 0x86 and <= 0x9F) or 0xFFFE or 0xFFFF;

    private static string Shadow(string s)
    {
        if (!s.Any(c => IsNonPrintable(c))) return s;
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(IsNonPrintable(c) ? char.ConvertFromUtf32(ShadowBase + c) : c.ToString());
        return sb.ToString();
    }

    private static string Unshadow(string s)
    {
        if (!s.Any(char.IsSurrogate)) return s;
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                var cp = char.ConvertToUtf32(s[i], s[i + 1]);
                if (cp >= ShadowBase && IsNonPrintable(cp - ShadowBase))
                {
                    sb.Append((char)(cp - ShadowBase));
                    i++;
                    continue;
                }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// Parse YAML to JSON. `YamlFlavor.Js` (the default) follows the `yaml` npm
    /// package; `YamlFlavor.Serde` follows the Rust port's serde_yaml path, which
    /// the extensions use (see <see cref="YamlFlavor"/>).
    public static JsonNode? ParseYaml(string src, YamlFlavor flavor = YamlFlavor.Js)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(Shadow(src)));
        }
        catch (YamlException e)
        {
            throw new YamlParseException(Unshadow(e.InnerException is YamlException inner && inner.Message.Length > 0 ? $"{e.Message} {inner.Message}" : e.Message));
        }
        if (stream.Documents.Count == 0) return null;
        if (stream.Documents.Count > 1) throw new YamlParseException("Source contains multiple documents; please use YAML.parseAllDocuments()");
        return ToJson(stream.Documents[0].RootNode, flavor == YamlFlavor.Serde);
    }

    [GeneratedRegex(@"\A[-+]?[0-9]+\z")]
    private static partial Regex IntRe();

    [GeneratedRegex(@"\A0o[0-7]+\z")]
    private static partial Regex OctRe();

    [GeneratedRegex(@"\A0x[0-9a-fA-F]+\z")]
    private static partial Regex HexRe();

    [GeneratedRegex(@"\A[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?\z")]
    private static partial Regex FloatRe();

    /// YAML 1.2 core schema resolution for a plain scalar.
    private static JsonNode? ResolvePlain(string v)
    {
        switch (v)
        {
            case "" or "~" or "null" or "Null" or "NULL": return null;
            case "true" or "True" or "TRUE": return JsonValue.Create(true);
            case "false" or "False" or "FALSE": return JsonValue.Create(false);
            case ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF": return JsonValue.Create(double.PositiveInfinity);
            case "-.inf" or "-.Inf" or "-.INF": return JsonValue.Create(double.NegativeInfinity);
            case ".nan" or ".NaN" or ".NAN": return JsonValue.Create(double.NaN);
        }
        if (IntRe().IsMatch(v)) return JsonValue.Create(double.Parse(v, CultureInfo.InvariantCulture));
        if (OctRe().IsMatch(v)) return JsonValue.Create((double)Convert.ToUInt64(v[2..], 8));
        if (HexRe().IsMatch(v)) return JsonValue.Create((double)Convert.ToUInt64(v[2..], 16));
        if (FloatRe().IsMatch(v)) return JsonValue.Create(double.Parse(v, CultureInfo.InvariantCulture));
        return JsonValue.Create(Unshadow(v));
    }

    private static JsonNode? ScalarToJson(YamlScalarNode s)
    {
        var value = s.Value ?? "";
        var tag = s.Tag.IsEmpty ? "" : s.Tag.Value;
        if (tag is "tag:yaml.org,2002:str") return JsonValue.Create(Unshadow(value));
        if (tag is "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float" or "tag:yaml.org,2002:bool" or "tag:yaml.org,2002:null") return ResolvePlain(value);
        if (s.Style != ScalarStyle.Plain && s.Style != ScalarStyle.Any) return JsonValue.Create(Unshadow(value));
        return ResolvePlain(value);
    }

    private static string KeyString(YamlNode k) => ToJson(k, false) switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var str) => str,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<double>(out var d) => Json.NumberToString(d),
        var other => Json.Stringify(other, 0),
    };

    private static JsonNode? ToJson(YamlNode node, bool serde) => node switch
    {
        YamlScalarNode s => serde ? SerdeScalarToJson(s) : ScalarToJson(s),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(c => ToJson(c, serde)).ToArray()),
        YamlMappingNode map => MappingToJson(map, serde),
        _ => null,
    };

    private static JsonObject MappingToJson(YamlMappingNode map, bool serde)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in map.Children) obj[Unshadow(serde ? SerdeKeyString(k) : KeyString(k))] = ToJson(v, serde);
        // serde_json's preserve_order maps keep document order; JS objects put
        // array-index keys first.
        return serde ? obj : Json.JsKeyOrder(obj);
    }

    // ─── serde_yaml 0.9 scalar resolution (Rust port) ───

    /// Leading zero(s) followed by digits is a string (YAML 1.2), as in serde_yaml.
    private static bool DigitsButNotNumber(string scalar)
    {
        var s = scalar.Length > 0 && scalar[0] is '-' or '+' ? scalar[1..] : scalar;
        return s.Length > 1 && s[0] == '0' && s.Skip(1).All(char.IsAsciiDigit);
    }

    /// Rust `from_str_radix` into a 128-bit unsigned value (optional leading `+`).
    private static bool TryRadix(string s, int radix, out UInt128 value)
    {
        value = 0;
        if (s.StartsWith('+')) s = s[1..];
        if (s.Length == 0) return false;
        foreach (var c in s)
        {
            var d = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'z' ? c - 'a' + 10 : c is >= 'A' and <= 'Z' ? c - 'A' + 10 : 99;
            if (d >= radix) return false;
            if (value > (UInt128.MaxValue - (UInt128)d) / (UInt128)radix) return false; // overflow
            value = value * (UInt128)radix + (UInt128)d;
        }
        return true;
    }

    private enum SerdeInt
    {
        None,
        Fits64,
        Fits128,
    }

    private static readonly (string Prefix, int Radix)[] RadixPrefixes = [("0x", 16), ("0o", 8), ("0b", 2)];

    /// serde_yaml `visit_int` into a serde_yaml::Value: u64/i64 values become
    /// numbers; values that only fit 128 bits are rejected by the Value visitor.
    private static SerdeInt SerdeIntKind(string v, out double number)
    {
        number = 0;
        var unpositive = v.StartsWith('+') ? v[1..] : v;
        foreach (var (prefix, radix) in RadixPrefixes)
        {
            if (!unpositive.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = unpositive[2..];
            if (rest.StartsWith('+') || rest.StartsWith('-')) return SerdeInt.None;
            if (TryRadix(rest, radix, out var u))
            {
                number = (double)u;
                return u <= ulong.MaxValue ? SerdeInt.Fits64 : SerdeInt.Fits128;
            }
        }
        if (!unpositive.StartsWith('+') && !unpositive.StartsWith('-') && !DigitsButNotNumber(v) && TryRadix(unpositive, 10, out var dec))
        {
            number = (double)dec;
            return dec <= ulong.MaxValue ? SerdeInt.Fits64 : SerdeInt.Fits128;
        }
        if (!v.StartsWith('-')) return SerdeInt.None;
        var negMax = (UInt128)Int128.MaxValue + 1;
        var neg64Max = (UInt128)long.MaxValue + 1;
        foreach (var (prefix, radix) in RadixPrefixes)
        {
            if (!v.StartsWith("-" + prefix, StringComparison.Ordinal)) continue;
            var rest = v[3..];
            if (rest.StartsWith('+') || rest.StartsWith('-')) continue;
            if (TryRadix(rest, radix, out var u) && u <= negMax)
            {
                number = -(double)u;
                return u <= neg64Max ? SerdeInt.Fits64 : SerdeInt.Fits128;
            }
        }
        if (!DigitsButNotNumber(v) && v.Length > 1 && v[1] != '+' && v[1] != '-' && TryRadix(v[1..], 10, out var n) && n <= negMax)
        {
            number = -(double)n;
            return n <= neg64Max ? SerdeInt.Fits64 : SerdeInt.Fits128;
        }
        return SerdeInt.None;
    }

    [GeneratedRegex(@"\A[-+]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][-+]?[0-9]+)?\z")]
    private static partial Regex RustFloatRe();

    [GeneratedRegex(@"\A[-+]?(inf|infinity|nan)\z", RegexOptions.IgnoreCase)]
    private static partial Regex RustFloatWordRe();

    /// serde_yaml `parse_f64`: finite floats, plus the YAML .inf/.nan words.
    private static double? SerdeParseF64(string scalar)
    {
        var unpositive = scalar;
        if (scalar.StartsWith('+'))
        {
            unpositive = scalar[1..];
            if (unpositive.StartsWith('+') || unpositive.StartsWith('-')) return null;
        }
        if (unpositive is ".inf" or ".Inf" or ".INF") return double.PositiveInfinity;
        if (scalar is "-.inf" or "-.Inf" or "-.INF") return double.NegativeInfinity;
        if (scalar is ".nan" or ".NaN" or ".NAN") return double.NaN;
        if (RustFloatWordRe().IsMatch(unpositive) || !RustFloatRe().IsMatch(unpositive)) return null;
        var d = double.Parse(unpositive, NumberStyles.Float, CultureInfo.InvariantCulture);
        return double.IsFinite(d) ? d : null;
    }

    private static YamlParseException SerdeInvalid(string expected) => new($"invalid value, expected {expected}");

    /// serde_yaml `visit_untagged_scalar`, then the Rust port's yaml_to_json
    /// (non-finite floats become null).
    private static JsonNode? SerdeResolvePlain(string v)
    {
        if (v is "" or "null" or "Null" or "NULL" or "~") return null;
        if (v is "true" or "True" or "TRUE") return JsonValue.Create(true);
        if (v is "false" or "False" or "FALSE") return JsonValue.Create(false);
        switch (SerdeIntKind(v, out var n))
        {
            case SerdeInt.Fits64:
                return JsonValue.Create(n);
            case SerdeInt.Fits128:
                throw new YamlParseException("invalid type: integer, expected any valid YAML value");
        }
        if (!DigitsButNotNumber(v) && SerdeParseF64(v) is { } f) return double.IsFinite(f) ? JsonValue.Create(f) : null;
        return JsonValue.Create(Unshadow(v));
    }

    private static JsonNode? SerdeScalarToJson(YamlScalarNode s)
    {
        var value = s.Value ?? "";
        var tag = s.Tag.IsEmpty ? "" : s.Tag.Value;
        switch (tag)
        {
            case "tag:yaml.org,2002:bool":
                return value switch
                {
                    "true" or "True" or "TRUE" => JsonValue.Create(true),
                    "false" or "False" or "FALSE" => JsonValue.Create(false),
                    _ => throw SerdeInvalid("a boolean"),
                };
            case "tag:yaml.org,2002:int":
                return SerdeIntKind(value, out var n) switch
                {
                    SerdeInt.Fits64 => JsonValue.Create(n),
                    SerdeInt.Fits128 => throw new YamlParseException("invalid type: integer, expected any valid YAML value"),
                    _ => throw SerdeInvalid("an integer"),
                };
            case "tag:yaml.org,2002:float":
                if (SerdeParseF64(value) is not { } f) throw SerdeInvalid("a float");
                return double.IsFinite(f) ? JsonValue.Create(f) : null;
            case "tag:yaml.org,2002:null":
                return value is "null" or "Null" or "NULL" or "~" ? null : throw SerdeInvalid("null");
        }
        if (s.Style is ScalarStyle.Plain or ScalarStyle.Any && (tag.Length == 0 || tag.StartsWith('!'))) return SerdeResolvePlain(value);
        return JsonValue.Create(Unshadow(value));
    }

    /// serde_yaml mapping key → string (the Rust port's `yaml_key`).
    private static string SerdeKeyString(YamlNode k)
    {
        var v = ToJson(k, true);
        switch (v)
        {
            case null:
                return "";
            case JsonValue jv when jv.TryGetValue<string>(out var str):
                return str;
            case JsonValue jv when jv.TryGetValue<bool>(out var b):
                return b ? "true" : "false";
            case JsonValue jv when Json.AsNumber(jv) is { } d:
                var isInt = k is YamlScalarNode ks && SerdeIntKind(ks.Value ?? "", out _) == SerdeInt.Fits64;
                if (isInt) return ((decimal)d).ToString(CultureInfo.InvariantCulture);
                // ryu formatting of a float (approximation)
                return d == Math.Floor(d) && Math.Abs(d) < 1e16 ? ((decimal)d).ToString(CultureInfo.InvariantCulture) + ".0" : d.ToString("R", CultureInfo.InvariantCulture);
            default:
                return Json.Stringify(v, 0);
        }
    }

    // ─── YAML emitter ───

    private const int LineWidth = 80;

    private static bool IsReservedPlain(string s)
    {
        var lower = s.ToLowerInvariant();
        return lower is "true" or "false" or "null" or "~" or "yes" or "no" or "on" or "off" or ".nan" or ".inf" or "-.inf" or "+.inf"
            || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            || HexRe().IsMatch(s) || OctRe().IsMatch(s);
    }

    private static bool NeedsQuotes(string s)
    {
        if (s.Length == 0 || IsReservedPlain(s)) return true;
        var first = s[0];
        if ("-?:,[]{}#&*!|>'\"%@`".Contains(first))
        {
            if (first is '-' or '?' or ':')
            {
                if (s.Length < 2 || s[1] is ' ' or '\t') return true;
            }
            else
            {
                return true;
            }
        }
        if (s[0] is ' ' or '\t' || s[^1] is ' ' or '\t') return true;
        if (s.Contains(": ") || s.Contains(" #") || s.EndsWith(':')) return true;
        return s.Any(c => c < 0x20 && c != '\t');
    }

    private static string DoubleQuote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// Fold a plain scalar at spaces so lines stay within LineWidth.
    private static string FoldPlain(string s, int firstCol, int indent)
    {
        if (firstCol + s.Length <= LineWidth) return s;
        var sb = new StringBuilder();
        var col = firstCol;
        var words = s.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (i == 0)
            {
                sb.Append(w);
                col += w.Length;
                continue;
            }
            if (col + 1 + w.Length > LineWidth && w.Length > 0 && col > indent)
            {
                sb.Append('\n').Append(' ', indent).Append(w);
                col = indent + w.Length;
            }
            else
            {
                sb.Append(' ').Append(w);
                col += 1 + w.Length;
            }
        }
        return sb.ToString();
    }

    private static string Scalar(JsonNode? v, int firstCol, int indent)
    {
        switch (v)
        {
            case null: return "null";
            case JsonValue jv when jv.TryGetValue<bool>(out var b): return b ? "true" : "false";
            case JsonValue jv when jv.TryGetValue<string>(out var s):
                if (s.Contains('\n'))
                {
                    var chomp = s.EndsWith('\n') ? "" : "-";
                    var body = s.TrimEnd('\n').Split('\n').Select(l => l.Length == 0 ? "" : new string(' ', indent) + l);
                    return $"|{chomp}\n{string.Join("\n", body)}";
                }
                if (NeedsQuotes(s) || s.Contains("  ")) return DoubleQuote(s);
                return FoldPlain(s, firstCol, indent);
            case JsonValue jv when Json.AsNumber(jv) is { } d:
                return double.IsNaN(d) ? ".nan" : double.IsPositiveInfinity(d) ? ".inf" : double.IsNegativeInfinity(d) ? "-.inf" : Json.NumberToString(d);
            default: return "";
        }
    }

    private static void Emit(JsonNode? v, int indent, StringBuilder sb)
    {
        switch (v)
        {
            case JsonObject map:
                foreach (var (k, val) in Json.OrderedEntries(map))
                {
                    var key = NeedsQuotes(k) ? DoubleQuote(k) : k;
                    sb.Append(' ', indent).Append(key);
                    switch (val)
                    {
                        case JsonObject { Count: > 0 }:
                        case JsonArray { Count: > 0 }:
                            sb.Append(":\n");
                            Emit(val, indent + 2, sb);
                            break;
                        case JsonObject:
                            sb.Append(": {}\n");
                            break;
                        case JsonArray:
                            sb.Append(": []\n");
                            break;
                        default:
                            sb.Append(": ").Append(Scalar(val, indent + key.Length + 2, indent + 2)).Append('\n');
                            break;
                    }
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    sb.Append(' ', indent).Append("- ");
                    switch (item)
                    {
                        case JsonObject { Count: > 0 }:
                        case JsonArray { Count: > 0 }:
                        {
                            var nested = new StringBuilder();
                            Emit(item, indent + 2, nested);
                            sb.Append(nested.ToString().TrimStart());
                            break;
                        }
                        case JsonObject:
                            sb.Append("{}\n");
                            break;
                        case JsonArray:
                            sb.Append("[]\n");
                            break;
                        default:
                            sb.Append(Scalar(item, indent + 2, indent + 2)).Append('\n');
                            break;
                    }
                }
                break;
            default:
                sb.Append(Scalar(v, indent, indent)).Append('\n');
                break;
        }
    }

    /// `yaml.stringify(value)`
    public static string Stringify(JsonNode? v)
    {
        var sb = new StringBuilder();
        Emit(v, 0, sb);
        return sb.ToString();
    }
}
