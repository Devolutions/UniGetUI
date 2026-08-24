using System.Text;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Core.Tools;

public enum InstallerNameScheme
{
    PublisherName,
    NameAndVersion,
    IdAndVersion,
    PublisherNameAndVersion,
}

public static class InstallerFileNaming
{
    public const string PublisherNameValue = "publisher";
    public const string NameAndVersionValue = "name_version";
    public const string IdAndVersionValue = "id_version";
    public const string PublisherNameAndVersionValue = "publisher_version";

    private const string FallbackStem = "installer";
    private const int MaxExtensionLength = 16;

    private static readonly Dictionary<string, string> _extensionsByInstallerType = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["exe"] = ".exe",
        ["burn"] = ".exe",
        ["inno"] = ".exe",
        ["nullsoft"] = ".exe",
        ["wix"] = ".exe",
        ["portable"] = ".exe",
        ["msi"] = ".msi",
        ["msix"] = ".msix",
        ["appx"] = ".appx",
        ["msixbundle"] = ".msixbundle",
        ["appxbundle"] = ".appxbundle",
        ["zip"] = ".zip",
        ["nupkg"] = ".nupkg",
    };

    public static InstallerNameScheme ResolveScheme() =>
        ParseScheme(Settings.GetValue(Settings.K.InstallerFileNameScheme))
        ?? InstallerNameScheme.PublisherName;

    public static InstallerNameScheme? ParseScheme(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            PublisherNameValue => InstallerNameScheme.PublisherName,
            NameAndVersionValue => InstallerNameScheme.NameAndVersion,
            IdAndVersionValue => InstallerNameScheme.IdAndVersion,
            PublisherNameAndVersionValue => InstallerNameScheme.PublisherNameAndVersion,
            _ => null,
        };

    public static string Build(
        string? publisherFileName,
        string? packageName,
        string? packageId,
        string? version,
        string? installerType,
        InstallerNameScheme scheme
    )
    {
        string publisherFile = Sanitize(publisherFileName);
        string publisherExtension = ExtractExtension(publisherFile);
        string publisherStem = Sanitize(publisherFile[..^publisherExtension.Length]);

        string name = Sanitize(packageName);
        string id = Sanitize(packageId);

        if (scheme is InstallerNameScheme.PublisherName)
            return FirstNonEmpty(publisherFile, name, id, FallbackStem);

        string extension = publisherExtension.Length > 0
            ? publisherExtension
            : ResolveExtensionForInstallerType(installerType);
        string normalizedVersion = NormalizeVersion(version);

        string stem = scheme switch
        {
            InstallerNameScheme.NameAndVersion => Join(
                FirstNonEmpty(name, id, publisherStem, FallbackStem),
                normalizedVersion
            ),
            InstallerNameScheme.IdAndVersion => Join(
                FirstNonEmpty(id, name, publisherStem, FallbackStem),
                normalizedVersion
            ),
            InstallerNameScheme.PublisherNameAndVersion => Join(
                FirstNonEmpty(publisherStem, name, id, FallbackStem),
                normalizedVersion
            ),
            _ => publisherStem,
        };

        return TrimTrailingSeparators(
            FirstNonEmpty(stem, publisherStem, name, id, FallbackStem) + extension
        );
    }

    private static string Join(string stem, string version)
    {
        if (version.Length == 0)
            return stem;
        if (stem.Length == 0)
            return version;
        if (stem.Contains(version, StringComparison.OrdinalIgnoreCase))
            return stem;

        return $"{stem}_{version}";
    }

    private static string NormalizeVersion(string? version)
    {
        string normalized = Sanitize(version);
        if (!normalized.Any(char.IsDigit))
            return "";

        StringBuilder builder = new(normalized.Length);
        bool lastWasSeparator = false;
        foreach (char character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!lastWasSeparator)
                    builder.Append('_');
                lastWasSeparator = true;
                continue;
            }

            builder.Append(character);
            lastWasSeparator = character is '_';
        }

        return TrimTrailingSeparators(builder.ToString()).Trim('_');
    }

    public static string ExtractExtension(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        if (!IsExtensionShaped(extension))
            return "";

        string stem = fileName[..^extension.Length];
        string innerExtension = Path.GetExtension(stem);
        if (innerExtension.Equals(".tar", StringComparison.OrdinalIgnoreCase))
            return innerExtension + extension;

        return extension;
    }

    private static bool IsExtensionShaped(string extension) =>
        extension.Length is > 1 and <= MaxExtensionLength
        && extension.Skip(1).All(char.IsLetterOrDigit)
        && extension.Any(char.IsLetter);

    private static string ResolveExtensionForInstallerType(string? installerType) =>
        _extensionsByInstallerType.GetValueOrDefault(
            (installerType ?? "").Trim().TrimStart('.'),
            ""
        );

    private static string Sanitize(string? value)
    {
        StringBuilder builder = new();
        bool lastWasSpace = false;
        foreach (char character in CoreTools.MakeValidFileName(value ?? ""))
        {
            if (char.IsWhiteSpace(character))
            {
                lastWasSpace = true;
                continue;
            }

            if (lastWasSpace && builder.Length > 0)
                builder.Append(' ');
            lastWasSpace = false;
            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string TrimTrailingSeparators(string value) => value.TrimEnd('.', ' ', '_');

    private static string FirstNonEmpty(params string[] candidates) =>
        candidates.FirstOrDefault(candidate => candidate.Length > 0) ?? "";
}
