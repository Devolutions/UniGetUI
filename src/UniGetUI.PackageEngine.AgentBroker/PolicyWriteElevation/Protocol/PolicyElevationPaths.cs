namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

/// <summary>
/// The exact packaged layout both peers agree on. The helper is never searched for: it is only
/// ever accepted at <c>&lt;install root&gt;\Assets\Utilities\UniGetUI.PolicyElevator.exe</c>, and
/// the host is only ever accepted at <c>&lt;install root&gt;\UniGetUI.exe</c>.
/// </summary>
public static class PolicyElevationPaths
{
    public static string GetHelperPath(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        return Path.Combine(
            installRoot,
            PolicyElevationProtocol.HelperRelativeDirectory,
            PolicyElevationProtocol.HelperRelativeSubDirectory,
            PolicyElevationProtocol.HelperFileName);
    }

    public static string GetHostPath(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        return Path.Combine(installRoot, PolicyElevationProtocol.HostFileName);
    }

    /// <summary>
    /// Walks a helper path back to its install root, rejecting anything that is not laid out
    /// exactly like the package.
    /// </summary>
    public static bool TryGetInstallRootFromHelperPath(string? helperPath, out string? installRoot)
    {
        installRoot = null;

        if (string.IsNullOrWhiteSpace(helperPath))
        {
            return false;
        }

        if (!string.Equals(
                Path.GetFileName(helperPath),
                PolicyElevationProtocol.HelperFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? utilities = Path.GetDirectoryName(helperPath);
        if (!string.Equals(
                Path.GetFileName(utilities),
                PolicyElevationProtocol.HelperRelativeSubDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? assets = Path.GetDirectoryName(utilities);
        if (!string.Equals(
                Path.GetFileName(assets),
                PolicyElevationProtocol.HelperRelativeDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? root = Path.GetDirectoryName(assets);
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        installRoot = root;
        return true;
    }
}
