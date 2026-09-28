using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.Tui.FakeData;

namespace UniGetUI.Tui.Infrastructure;

/// <summary>
/// The TUI's single view of the package engine. In normal runs it forwards to <see cref="PEInterface"/>;
/// under <c>--fake-data</c> it serves the fake managers instead and never touches
/// <see cref="PEInterface"/>, whose static initializer would construct the real managers.
/// Every page must go through here rather than referencing <see cref="PEInterface"/> directly.
/// </summary>
internal static class TuiEngine
{
    private static IReadOnlyList<IPackageManager>? _managers;

    public static IReadOnlyList<IPackageManager> Managers
        => _managers ??= FakeDataEnvironment.Current?.Managers ?? PEInterface.Managers;

    public static bool IsFakeData => FakeDataEnvironment.IsActive;

    public static IPackageManager? FindManager(string name)
        => Managers.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                        || m.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)
                                        || m.Id.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Creates the loaders over <see cref="Managers"/>.</summary>
    public static void LoadLoaders()
    {
        if (!IsFakeData)
        {
            PEInterface.LoadLoaders();
            return;
        }

        var managers = Managers;
        DiscoverablePackagesLoader.Instance = new DiscoverablePackagesLoader(managers);
        InstalledPackagesLoader.Instance = new InstalledPackagesLoader(managers);
        UpgradablePackagesLoader.Instance = new UpgradablePackagesLoader(managers);
        PackageBundlesLoader.Instance = new PackageBundlesLoader_I(managers);
    }

    /// <summary>Initializes every manager, then starts the installed/updates loads.</summary>
    public static void LoadManagers()
    {
        if (!IsFakeData)
        {
            PEInterface.LoadManagers();
            return;
        }

        foreach (IPackageManager manager in Managers)
        {
            try
            {
                manager.Initialize();
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        _ = InstalledPackagesLoader.Instance.ReloadPackages();
        _ = UpgradablePackagesLoader.Instance.ReloadPackages();
    }
}
