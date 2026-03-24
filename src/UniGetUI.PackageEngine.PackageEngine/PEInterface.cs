using System.Diagnostics.CodeAnalysis;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.CargoManager;
using UniGetUI.PackageEngine.Managers.DotNetManager;
using UniGetUI.PackageEngine.Managers.NpmManager;
using UniGetUI.PackageEngine.Managers.PipManager;
using UniGetUI.PackageEngine.Managers.PowerShell7Manager;
using UniGetUI.PackageEngine.Managers.VcpkgManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
#if WINDOWS
using UniGetUI.PackageEngine.Managers.ChocolateyManager;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.Managers.ScoopManager;
using UniGetUI.PackageEngine.Managers.WingetManager;
#else
using UniGetUI.PackageEngine.Managers.HomebrewManager;
#endif

namespace UniGetUI.PackageEngine
{
    /// <summary>
    /// The interface/entry point for the UniGetUI Package Engine
    /// </summary>
    public static class PEInterface
    {
        private const int ManagerLoadTimeout = 60; // 60 seconds timeout for Package Manager initialization (in seconds)
#if WINDOWS
        public static readonly WinGet WinGet = new();
        public static readonly Scoop Scoop = new();
        public static readonly Chocolatey Chocolatey = new();
#endif
        public static readonly Npm Npm = new();
        public static readonly Pip Pip = new();
        public static readonly DotNet DotNet = new();
        public static readonly PowerShell7 PowerShell7 = new();
#if WINDOWS
        public static readonly PowerShell PowerShell = new();
#endif
        public static readonly Cargo Cargo = new();
        public static readonly Vcpkg Vcpkg = new();
#if !WINDOWS
        public static readonly Homebrew Homebrew = new();
#endif

        public static readonly IPackageManager[] Managers = CreateManagers();

        private static IPackageManager[] CreateManagers()
        {
            List<IPackageManager> managers = [Npm, Pip, Cargo, Vcpkg, DotNet, PowerShell7];
#if WINDOWS
            managers.InsertRange(0, [WinGet, Scoop, Chocolatey]);
            managers.Add(PowerShell);
#else
            managers.Insert(0, Homebrew);
#endif
            return [.. managers];
        }

        public static void LoadLoaders()
        {
            DiscoverablePackagesLoader.Instance = new DiscoverablePackagesLoader(Managers);
            InstalledPackagesLoader.Instance = new InstalledPackagesLoader(Managers);
            UpgradablePackagesLoader.Instance = new UpgradablePackagesLoader(Managers);
            PackageBundlesLoader.Instance = new PackageBundlesLoader_I(Managers);
        }

        public static void LoadManagers()
        {
            try
            {
                List<Task> initializeTasks = [];

                foreach (IPackageManager manager in Managers)
                {
                    initializeTasks.Add(Task.Run(manager.Initialize));
                }

                Task ManagersMegaTask = Task.WhenAll(initializeTasks);

                if (!ManagersMegaTask.Wait(TimeSpan.FromSeconds(ManagerLoadTimeout)))
                {
                    Logger.Warn("Timeout: Not all package managers have finished initializing.");
                }

                _ = InstalledPackagesLoader.Instance.ReloadPackages();
                _ = UpgradablePackagesLoader.Instance.ReloadPackages();
#if !WINDOWS
                // Re-run any search that was triggered before all managers were ready,
                // so results from managers that finished initializing late are included.
                _ = DiscoverablePackagesLoader.Instance.ReloadPackages();
#endif
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }
    }

    public class PackageBundlesLoader_I : PackageBundlesLoader
    {
        public PackageBundlesLoader_I(IReadOnlyList<IPackageManager> managers)
            : base(managers) { }

        public override async Task AddPackagesAsync(IReadOnlyList<IPackage> foreign_packages)
        {
            List<IPackage> added = new();
            foreach (IPackage foreign in foreign_packages)
            {
                IPackage? package = null;

                if (foreign is not ImportedPackage && foreign is Package native)
                {
                    if (native.Source.IsVirtualManager)
                    {
                        Logger.Debug(
                            $"Adding native package with id={native.Id} to bundle as an INVALID package..."
                        );
                        package = new InvalidImportedPackage(
                            native.AsSerializable_Incompatible(),
                            NullSource.Instance
                        );
                    }
                    else
                    {
                        Logger.Debug(
                            $"Adding native package with id={native.Id} to bundle as a VALID package..."
                        );
                        package = new ImportedPackage(
                            await native.AsSerializableAsync(),
                            native.Manager,
                            native.Source
                        );
                    }
                }
                else if (foreign is ImportedPackage imported)
                {
                    Logger.Debug(
                        $"Adding loaded imported package with id={imported.Id} to bundle..."
                    );
                    package = imported;
                }
                else if (foreign is InvalidImportedPackage invalid)
                {
                    Logger.Debug(
                        $"Adding loaded incompatible package with id={invalid.Id} to bundle..."
                    );
                    package = invalid;
                }
                else
                {
                    Logger.Error(
                        $"An IPackage instance id={foreign.Id} did not match the types Package, ImportedPackage or InvalidImportedPackage. This should never be the case"
                    );
                }

                if (package is not null)
                { // Here, AddForeign is not used so a single PackagesChangedEvent can be invoked.
                    await AddPackage(package);
                    added.Add(package);
                }
            }
            InvokePackagesChangedEvent(true, added, []);
        }
    }
}
