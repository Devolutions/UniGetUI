using Microsoft.Win32;

namespace UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal;

internal sealed record ArpEntry(
    string Key,
    bool PerMachine,
    string? DisplayName,
    string? DisplayVersion,
    string? Publisher,
    string? UninstallString,
    string? QuietUninstallString,
    bool WindowsInstaller,
    bool Hidden,
    IReadOnlyList<string> BundleUpgradeCodes);

internal static class ArpEntries
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (RegistryHive Hive, RegistryView View)[] Roots =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Registry64),
        (RegistryHive.CurrentUser, RegistryView.Registry32),
    ];

    internal static Func<IReadOnlyList<ArpEntry>> Source { get; set; } = ReadRegistry;

    public static IReadOnlyList<ArpEntry> Read() => Source();

    internal static Func<string, string?> ProductAssignment { get; set; } =
        productCode => MsiDatabase.ProductInfo(productCode, "AssignmentType");

    internal static IReadOnlyList<ArpEntry> ReadRegistry()
    {
        var entries = new List<ArpEntry>();
        var seen = new HashSet<(bool, string)>();
        foreach (var (hive, view) in Roots)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallKey);
                if (uninstall is null)
                    continue;
                foreach (string name in uninstall.GetSubKeyNames())
                {
                    bool perMachine = hive == RegistryHive.LocalMachine;
                    if (!perMachine && !seen.Add((perMachine, name)))
                        continue;
                    using var key = uninstall.OpenSubKey(name);
                    if (key is null)
                        continue;
                    bool windowsInstaller = key.GetValue("WindowsInstaller") is int installer && installer != 0;
                    if (windowsInstaller && InstallerDetails.Guid(name) is { } productCode)
                    {
                        if (ProductAssignment(productCode) is not { } assignment)
                            continue;
                        perMachine = assignment == "1";
                    }
                    entries.Add(new ArpEntry(name, perMachine,
                        Text(key, "DisplayName"), Text(key, "DisplayVersion"), Text(key, "Publisher"),
                        Text(key, "UninstallString"), Text(key, "QuietUninstallString"),
                        windowsInstaller,
                        (key.GetValue("SystemComponent") is int system && system != 0) || Text(key, "ParentKeyName") is not null,
                        key.GetValue("BundleUpgradeCode") switch
                        {
                            string[] codes => codes.Select(InstallerDetails.Guid).OfType<string>().ToArray(),
                            string code when InstallerDetails.Guid(code) is { } guid => [guid],
                            _ => [],
                        }));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                UniGetUI.Core.Logging.Logger.Warn($"Could not read the installed programs of {hive} ({view}): {ex.Message}");
            }
        }
        return entries;
    }

    private static string? Text(RegistryKey key, string name) =>
        key.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
