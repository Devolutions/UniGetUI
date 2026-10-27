using System.Text;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// A <c>unigetui://package?manager=…&amp;source=…&amp;id=…</c> deep link. It names a package so
/// that the recipient's UniGetUI can look it up in its own managers and open the package details,
/// from where the user installs it. A link identifies a package and nothing else: it never carries
/// installer URLs, arguments or other install options, which always come from the recipient's
/// own settings.
/// </summary>
internal sealed record PackageLink(string Id, string? Manager, string? Source, string? Version, string? Name)
{
    public const string Action = "package";

    private const string SchemePrefix = "unigetui:";

    // Long enough for any real package identity, short enough that a crafted link cannot hand
    // the package lookup an arbitrarily large query.
    internal const int MaxLinkLength = 2048;
    internal const int MaxValueLength = 256;

    /// <summary>
    /// Whether <paramref name="arg"/> is a <c>unigetui:</c> link to the package action, valid or not.
    /// </summary>
    public static bool IsPackageLink(string? arg)
        => TrySplit(arg, out string action, out _)
           && action.Equals(Action, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a package link, or returns null when <paramref name="arg"/> is not one or is malformed:
    /// no <c>id</c>, a repeated parameter, a control character, or a value or link over the size limits.
    /// Unknown parameters are ignored, so a newer link still opens in an older version.
    /// </summary>
    public static PackageLink? TryParse(string? arg)
    {
        if (arg is null || arg.Length > MaxLinkLength)
            return null;

        if (!TrySplit(arg, out string action, out string query)
            || !action.Equals(Action, StringComparison.OrdinalIgnoreCase))
            return null;

        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=');
            string rawKey = separator < 0 ? pair : pair[..separator];
            string rawValue = separator < 0 ? "" : pair[(separator + 1)..];

            if (!TryDecode(rawKey, out string key) || !TryDecode(rawValue, out string value))
                return null;

            // A repeated parameter makes the link ambiguous about which package it names.
            if (!values.TryAdd(key, value))
                return null;
        }

        if (Get("id") is not { } id)
            return null;

        return new PackageLink(id, Get("manager"), Get("source"), Get("version"), Get("name"));

        string? Get(string key)
            => values.TryGetValue(key, out string? value) && value.Trim() is { Length: > 0 } trimmed
                ? trimmed
                : null;
    }

    /// <summary>
    /// Whether a link can name <paramref name="package"/>: packages that only exist on this
    /// machine, such as the WinGet "Local PC" entries, cannot be found from another one.
    /// </summary>
    public static bool CanLinkTo(IPackage package) => !package.Source.IsVirtualManager;

    public static PackageLink FromPackage(IPackage package) => new(
        package.Id,
        package.Manager.Id,
        // Managers without custom sources only have their default source, so naming it adds nothing.
        package.Manager.Capabilities.SupportsCustomSources ? package.Source.Name : null,
        Version: null,
        // The name is only shown while looking the package up; drop it rather than produce a
        // link that the parser would refuse.
        package.Name.Length <= MaxValueLength ? package.Name : null);

    public string ToUri()
    {
        StringBuilder uri = new($"{SchemePrefix}//{Action}");
        char separator = '?';
        Append("manager", Manager);
        Append("source", Source);
        Append("id", Id);
        Append("version", Version);
        Append("name", Name);
        return uri.ToString();

        void Append(string key, string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            uri.Append(separator).Append(key).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }
    }

    /// <summary>The package as it should be named before it is found: its name and id, or its id.</summary>
    public string DisplayName
        => Name is null || Name.Equals(Id, StringComparison.OrdinalIgnoreCase) ? Id : $"{Name} ({Id})";

    private static bool TrySplit(string? arg, out string action, out string query)
    {
        action = "";
        query = "";

        if (arg is null || !arg.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string rest = arg[SchemePrefix.Length..];

        int fragment = rest.IndexOf('#');
        if (fragment >= 0)
            rest = rest[..fragment];

        int queryStart = rest.IndexOf('?');
        if (queryStart >= 0)
        {
            query = rest[(queryStart + 1)..];
            rest = rest[..queryStart];
        }

        // unigetui://package, unigetui:package and the unigetui://package/ some browsers produce
        action = rest.Trim('/');
        return true;
    }

    private static bool TryDecode(string raw, out string decoded)
    {
        decoded = Uri.UnescapeDataString(raw.Replace('+', ' '));
        return decoded.Length <= MaxValueLength && !decoded.Any(char.IsControl);
    }
}
