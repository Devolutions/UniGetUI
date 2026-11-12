using System.Text;
using System.Text.RegularExpressions;

namespace UniGetUI.Extensions.Xcode;

/// <summary>Redacts supplied answers and common credential-bearing output before it is retained.</summary>
internal sealed partial class XcodesOutputRedactor
{
    private const string Redacted = "[redacted]";
    private readonly List<string> _sensitiveValues = [];

    public XcodesOutputRedactor(string? environmentPassword) => RememberValue(environmentPassword);

    public void RememberAnswer(XcodesPromptKind kind, string answer)
    {
        if ((kind == XcodesPromptKind.PhoneNumber && answer.Trim() is { Length: > 0 and <= 2 } index
                && index.All(char.IsAsciiDigit))
            || (kind == XcodesPromptKind.TwoFactorCode && answer.Trim().Equals("sms", StringComparison.OrdinalIgnoreCase)))
            return;

        RememberValue(answer);
    }

    /// <summary>Context may contain the initial federated sign-in URL needed to open the browser.</summary>
    public string ForContext(string text)
    {
        string safe = CredentialHeader().Replace(XcodesPrompts.Redact(text), "$1[redacted]");
        MatchCollection addresses = WebAddress().Matches(safe);
        if (addresses.Count == 0)
            return RemoveSensitiveValues(safe);

        var result = new StringBuilder(safe.Length);
        int previous = 0;
        foreach (Match address in addresses)
        {
            result.Append(RemoveSensitiveValues(safe[previous..address.Index]));
            bool isSubmittedUrl = _sensitiveValues.Any(value =>
                (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                && address.Value.Contains(value, StringComparison.Ordinal));
            result.Append(isSubmittedUrl ? RemoveSensitiveValues(address.Value) : address.Value);
            previous = address.Index + address.Length;
        }
        result.Append(RemoveSensitiveValues(safe[previous..]));
        return result.ToString();
    }

    public string ForLog(string text)
        => ForContext(TokenAssignment().Replace(WebAddress().Replace(text, "[URL redacted]"), "$1[redacted]"));

    public string ForError(string text)
        => string.Join('\n', XcodesOutputParser.StripTerminalControl(text).Split('\n').Select(ForLog));

    private void RememberValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        Add(value);
        Add(value.Trim());
        foreach (string part in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            Add(part);
            Add(part.Trim());
        }
        _sensitiveValues.Sort((left, right) => right.Length.CompareTo(left.Length));
    }

    private void Add(string value)
    {
        if (value.Length > 0 && !_sensitiveValues.Contains(value, StringComparer.Ordinal))
            _sensitiveValues.Add(value);
    }

    private string RemoveSensitiveValues(string text)
    {
        if (_sensitiveValues.Count == 0)
            return text;

        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length;)
        {
            string? match = _sensitiveValues.FirstOrDefault(value =>
                text.AsSpan(i).StartsWith(value.AsSpan(), StringComparison.Ordinal));
            if (match is null)
                result.Append(text[i++]);
            else
            {
                result.Append(Redacted);
                i += match.Length;
            }
        }
        return result.ToString();
    }

    [GeneratedRegex(@"(?i)(?<![?&])\b((?:authorization|proxy-authorization|cookie|set-cookie|x-apple-id-session-id|x-apple-session-token)\b[""']?[ \t]*[:=][ \t]*)[^\r\n]+")]
    private static partial Regex CredentialHeader();

    [GeneratedRegex(@"(?i)\bhttps?://[^\s<>""']+")]
    private static partial Regex WebAddress();

    [GeneratedRegex(@"(?i)\b((?:token|access[_-]?token|refresh[_-]?token|id[_-]?token|session[_-]?token)\b[""']?[ \t]*[:=][ \t]*)[^\r\n]+")]
    private static partial Regex TokenAssignment();
}
