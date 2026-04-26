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
using UniGetUI.PackageEngine.Managers.AptManager;
using UniGetUI.PackageEngine.Managers.DnfManager;
using UniGetUI.PackageEngine.Managers.HomebrewManager;
using UniGetUI.PackageEngine.Managers.PacmanManager;
using UniGetUI.PackageEngine.Managers.SnapManager;
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
        public static readonly Apt Apt = new();
        public static readonly Dnf Dnf = new();
        public static readonly Pacman Pacman = new();
        public static readonly Homebrew Homebrew = new();
        public static readonly Snap Snap = new();
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
            if (OperatingSystem.IsLinux())
            {
                var families = ReadLinuxDistroFamilies();
                // If /etc/os-release is unreadable, include both as a safe fallback.
                bool unknown = families.Count == 0;
                if (unknown || families.Contains("debian") || families.Contains("ubuntu"))
                    managers.Add(Apt);
                if (unknown || families.Contains("fedora") || families.Contains("rhel") || families.Contains("centos"))
                    managers.Add(Dnf);
                if (unknown || families.Contains("arch"))
                    managers.Add(Pacman);
                if (unknown || families.Contains("ubuntu") || families.Contains("debian") || families.Contains("fedora") || families.Contains("arch"))
                    managers.Add(Snap);
            }
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

#if !WINDOWS
        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        private static HashSet<string> ReadLinuxDistroFamilies()
        {
            var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var line in File.ReadLines("/etc/os-release"))
                {
                    if (!line.StartsWith("ID=", StringComparison.Ordinal) &&
                        !line.StartsWith("ID_LIKE=", StringComparison.Ordinal))
                        continue;
                    var value = line[(line.IndexOf('=') + 1)..].Trim('"');
                    foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        families.Add(token);
                }
            }
            catch { /* /etc/os-release not readable — caller will use fallback */ }
            return families;
        }
#endif
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
