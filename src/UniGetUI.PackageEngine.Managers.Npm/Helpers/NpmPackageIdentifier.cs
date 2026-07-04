namespace UniGetUI.PackageEngine.Managers.NpmManager;

/// <summary>
/// Normalizes npm package ids that may represent aliases.
/// npm-aliased dependencies are reported by `npm outdated --json` and `npm list --json`
/// with ids shaped like "localName:targetName@range". Real npm package names cannot contain
/// a colon, so the presence of one identifies an alias.
/// </summary>
internal readonly record struct NpmPackageIdentifier(string LocalName, string TargetName, bool IsAlias)
{
    /// <summary>
    /// Parses an npm package id into local and registry-facing names.
    /// For non-aliased packages, LocalName and TargetName are the same.
    /// </summary>
    public static NpmPackageIdentifier Parse(string id)
    {
        int colonIndex = id.IndexOf(':');
        if (colonIndex <= 0)
        {
            return new NpmPackageIdentifier(id, id, false);
        }

        string aliasLocalName = id[..colonIndex];
        string aliasTargetSpec = id[(colonIndex + 1)..];
        int versionIndex = aliasTargetSpec.LastIndexOf('@');
        string aliasTargetName =
            versionIndex > 0 ? aliasTargetSpec[..versionIndex] : aliasTargetSpec;

        return new NpmPackageIdentifier(aliasLocalName, aliasTargetName, true);
    }

    /// <summary>
    /// Builds the npm install or update specifier for the parsed package id.
    /// Aliases must be reconstructed as "localName@npm:targetName@version".
    /// </summary>
    public string GetInstallSpec(string version) =>
        IsAlias ? $"{LocalName}@npm:{TargetName}@{version}" : $"{LocalName}@{version}";

    /// <summary>
    /// Returns the registry-facing package name used by npm show and package URLs.
    /// </summary>
    public string GetRegistryName() => TargetName;

    /// <summary>
    /// Returns the local on-disk package name used under node_modules.
    /// </summary>
    public string GetInstallLocationName() => LocalName;
}
