using System.Text;
using System.Text.RegularExpressions;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal;

internal static partial class InstalledMatcher
{
    internal static Func<string, IReadOnlyList<string>> RelatedProducts { get; set; } = MsiDatabase.RelatedProducts;

    public static ArpEntry? Find(WinFeedItem item, IReadOnlyList<ArpEntry> entries)
    {
        var details = item.Details;
        if (details.UninstallKey is { } key)
            return Prefer(entries.Where(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)), details);

        if (details.UpgradeCode is { } upgradeCode)
        {
            var products = details.Kind == InstallerKind.Msi ? RelatedProducts(upgradeCode) : [];
            if (Prefer(entries.Where(e => products.Contains(e.Key, StringComparer.OrdinalIgnoreCase)
                || e.BundleUpgradeCodes.Contains(upgradeCode, StringComparer.OrdinalIgnoreCase)), details) is { } related)
                return related;
        }

        if (details.ProductCode is { } productCode
            && Prefer(entries.Where(e => string.Equals(e.Key, productCode, StringComparison.OrdinalIgnoreCase)), details) is { } product)
            return product;

        return details.Kind == InstallerKind.Msi ? null : FindByName(item, entries);
    }

    private static ArpEntry? Prefer(IEnumerable<ArpEntry> entries, InstallerDetails details) =>
        entries.OrderByDescending(e => e.PerMachine != details.PerUser).ThenByDescending(e => e, NewestFirst).FirstOrDefault();

    private static readonly IComparer<ArpEntry> NewestFirst = Comparer<ArpEntry>.Create((a, b) =>
        CoreTools.VersionStringToStruct(a.DisplayVersion ?? "").CompareTo(CoreTools.VersionStringToStruct(b.DisplayVersion ?? "")));

    private static ArpEntry? FindByName(WinFeedItem item, IReadOnlyList<ArpEntry> entries)
    {
        var targets = new[] { item.Details.DisplayName, item.Name }
            .Select(Normalize).Where(t => t.Length >= 2).Distinct().ToArray();
        if (targets.Length == 0)
            return null;
        ArpEntry? best = null;
        int bestScore = 0;
        foreach (var entry in entries)
        {
            if (entry.Hidden || entry.DisplayName is null || !PublishersAgree(item.Details.Publisher, entry.Publisher))
                continue;
            string exact = Normalize(entry.DisplayName);
            string cleaned = Normalize(InstallerFile.CleanName(entry.DisplayName));
            int score = targets.Contains(exact) ? 4 : targets.Contains(cleaned) ? 2 : 0;
            bool innoKey = item.Details.Kind == InstallerKind.Inno && entry.Key.EndsWith("_is1", StringComparison.OrdinalIgnoreCase);
            if (score == 0 || (score == 2 && !innoKey && !SameMajorVersion(entry.DisplayName, item.Version)))
                continue;
            if (innoKey)
                score++;
            if (entry.PerMachine != item.Details.PerUser)
                score++;
            if (score > bestScore || (score == bestScore && best is not null && NewestFirst.Compare(entry, best) > 0))
                (best, bestScore) = (entry, score);
        }
        return best;
    }

    [GeneratedRegex(@"\d+(?:\.\d+)+")]
    private static partial Regex VersionInName();

    internal static bool SameMajorVersion(string displayName, string version)
    {
        var inName = VersionInName().Matches(displayName);
        if (inName.Count == 0)
            return true;
        string major = inName[^1].Value.Split('.')[0].TrimStart('0');
        return major == version.Split('.')[0].Trim().TrimStart('0');
    }

    internal static bool PublishersAgree(string? feed, string? installed)
    {
        string a = Normalize(FirstWord(feed)), b = Normalize(FirstWord(installed));
        return a.Length == 0 || b.Length == 0 || a == b;
    }

    private static string? FirstWord(string? text) =>
        text?.Split([' ', ',', '.'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    internal static string Normalize(string? text)
    {
        if (text is null)
            return "";
        var builder = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(c) || c == '+' || c == '#')
                builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }
}
