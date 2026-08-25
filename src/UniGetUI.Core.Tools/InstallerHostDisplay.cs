namespace UniGetUI.Core.Tools;

public static class InstallerHostDisplay
{
    private const int MaxDetailedUrls = 6;

    public static string FromUrls(IEnumerable<string?>? urls)
    {
        if (urls is null)
            return "";

        List<string> hosts = [];
        foreach (string? url in urls)
        {
            string host = ExtractHost(url);
            if (host.Length == 0 || hosts.Contains(host, StringComparer.Ordinal))
                continue;
            hosts.Add(host);
        }

        hosts.Sort(StringComparer.Ordinal);
        return string.Join(", ", hosts);
    }

    public static string JoinUrls(IEnumerable<string?>? urls)
    {
        if (urls is null)
            return "";

        List<string> distinct = [];
        foreach (string? url in urls)
        {
            string trimmed = (url ?? "").Trim();
            if (trimmed.Length == 0 || distinct.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                continue;
            distinct.Add(trimmed);
        }

        if (distinct.Count <= MaxDetailedUrls)
            return string.Join("\n", distinct);

        return string.Join("\n", distinct.Take(MaxDetailedUrls)) + "\n\u2026";
    }

    private static string ExtractHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri))
            return "";

        return uri.IdnHost.ToLowerInvariant();
    }
}
