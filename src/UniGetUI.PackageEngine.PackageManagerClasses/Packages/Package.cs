using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UniGetUI.Core.Classes;
using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Packages;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Classes.Serializable;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;

namespace UniGetUI.PackageEngine.PackageClasses
{
    public partial class Package : IPackage
    {
        // Internal properties
        private bool __is_checked;
        public event PropertyChangedEventHandler? PropertyChanged;
        private PackageTag __tag;

        private readonly long _hash;
        private readonly long _versionedHash;
        private readonly string _ignoredId;
        private readonly string _iconId;

        private static readonly ConcurrentDictionary<int, Uri?> _cachedIconPaths = new();

        private IPackageDetails? __details;
        public IPackageDetails Details
        {
            get => __details ??= new PackageDetails(this);
        }

        public PackageTag Tag
        {
            get => __tag;
            set
            {
                __tag = value;
                OnPropertyChanged();
            }
        }

        public bool IsChecked
        {
            get => __is_checked;
            set
            {
                __is_checked = value;
                OnPropertyChanged();
            }
        }

        private OverridenInstallationOptions _overridenOptions;
        public ref OverridenInstallationOptions OverridenOptions
        {
            get => ref _overridenOptions;
        }
        public string Name { get; }
        public string AutomationName { get; }
        public string Id { get; }
        public virtual string VersionString { get; }
        public CoreTools.Version NormalizedVersion { get; }
        public CoreTools.Version NormalizedNewVersion { get; }
        public bool IsPopulated { get; set; }
        public IManagerSource Source { get; }

        /// <summary>
        /// IPackageManager is guaranteed to be PackageManager, but C# doesn't allow covariant attributes
        /// </summary>
        public IPackageManager Manager { get; }
        public string NewVersionString { get; }
        public virtual bool IsUpgradable { get; }

        /// <summary>
        /// Construct a package with a given name, id, version, source and manager, and an optional scope.
        /// </summary>
        public Package(
            string name,
            string id,
            string version,
            IManagerSource source,
            IPackageManager manager,
            OverridenInstallationOptions? options = null
        )
            : this(name, id, version, version, source, manager, options)
        {
            IsUpgradable = false;
        }

        /// <summary>
        /// Creates an UpgradablePackage object representing a package that can be upgraded; given its name, id, installed version, new version, source and manager, and an optional scope.
        /// </summary>
        public Package(
            string name,
            string id,
            string installed_version,
            string new_version,
            IManagerSource source,
            IPackageManager manager,
            OverridenInstallationOptions? options = null
        )
        {
            Name = name;
            Id = id;
            VersionString = installed_version;
            NormalizedVersion = CoreTools.VersionStringToStruct(installed_version);
            NewVersionString = new_version;
            NormalizedNewVersion = CoreTools.VersionStringToStruct(new_version);
            Source = source;
            Manager = manager;

            IsUpgradable = true;
            Tag = PackageTag.Default;

            AutomationName = CoreTools
                .Translate("Package {name} from {manager}")
                .Replace("{name}", name)
                .Replace("{manager}", Source.AsString_DisplayName);

            _overridenOptions = options ?? _overridenOptions;
            _hash = CoreTools.HashStringAsLong(
                $"{Manager.Name}\\{Source.AsString_DisplayName}\\{Id}"
            );
            _versionedHash = CoreTools.HashStringAsLong(
                $"{Manager.Name}\\{Source.AsString_DisplayName}\\{Id}\\{installed_version}"
            );
            _ignoredId = IgnoredUpdatesDatabase.GetIgnoredIdForPackage(this);
            _iconId = GenerateIconId(this);
        }

        public long GetHash() => _hash;

        public long GetVersionedHash() => _versionedHash;

        public bool Equals(IPackage? other) => _versionedHash == other?.GetHash();

        public override int GetHashCode() => (int)_versionedHash;

        /// <summary>
        /// Check whether two package instances represent the same package.
        /// What is taken into account:
        ///    - Manager and Source
        ///    - Package Identifier
        /// For more specific comparison use package.Equals(object? other)
        /// </summary>
        /// <param name="other">A package</param>
        /// <returns>Whether the two instances refer to the same instance</returns>
        public bool IsEquivalentTo(IPackage? other) => _hash == other?.GetHash();

        public string GetIconId() => _iconId;

        public virtual Uri GetIconUrl()
        {
            return GetIconUrlIfAny() ?? new Uri("ms-appx:///Assets/Images/package_color.png");
        }

        public virtual Uri? GetIconUrlIfAny()
        {
            if (_cachedIconPaths.TryGetValue(this.GetHashCode(), out Uri? path))
            {
                return path;
            }
            var CachedIcon = LoadIconUrlIfAny();
            _cachedIconPaths.TryAdd(this.GetHashCode(), CachedIcon);
            return CachedIcon;
        }

        private Uri? LoadIconUrlIfAny()
        {
            try
            {
                CacheableIcon? icon = TaskRecycler<CacheableIcon?>.RunOrAttach(
                    Manager.DetailsHelper.GetIcon,
                    this
                );
                string? path = IconCacheEngine.GetCacheOrDownloadIcon(
                    icon,
                    Manager.Name,
                    CoreTools.MakeValidFileName(Id)
                );
                return path is null ? null : new Uri("file:///" + path);
            }
            catch (Exception ex)
            {
                Logger.Error($"An error occurred while retrieving the icon for package {Id}");
                Logger.Error(ex);
                return null;
            }
        }

        public virtual IReadOnlyList<Uri> GetScreenshots()
        {
            return Manager.DetailsHelper.GetScreenshots(this);
        }

        public virtual async Task AddToIgnoredUpdatesAsync(string version = "*")
        {
            try
            {
                await Task.Run(() => IgnoredUpdatesDatabase.Add(_ignoredId, version));
                foreach (var p in GetInstalledPackages())
                    p.SetTag(PackageTag.Pinned);
            }
            catch (Exception ex)
            {
                Logger.Error($"Could not add package {Id} to ignored updates");
                Logger.Error(ex);
            }
        }

        public virtual async Task RemoveFromIgnoredUpdatesAsync()
        {
            try
            {
                await Task.Run(() => IgnoredUpdatesDatabase.Remove(_ignoredId));
                foreach (var p in GetInstalledPackages())
                    p.SetTag(PackageTag.Default);
            }
            catch (Exception ex)
            {
                Logger.Error($"Could not remove package {Id} from ignored updates");
                Logger.Error(ex);
            }
        }

        /// <summary>
        /// Returns true if the package's updates are ignored. If the version parameter
        /// is passed it will be checked if that version is ignored. Please note that if
        /// all updates are ignored, calling this method with a specific version will
        /// still return true, although the passed version is not explicitly ignored.
        /// </summary>
        public virtual async Task<bool> HasUpdatesIgnoredAsync(string version = "*")
        {
            try
            {
                return await Task.Run(() =>
                    IgnoredUpdatesDatabase.HasUpdatesIgnored(_ignoredId, version)
                );
            }
            catch (Exception ex)
            {
                Logger.Error($"Could not check whether package {Id} has updates ignored");
                Logger.Error(ex);
                return false;
            }
        }

        /// <summary>
        /// Returns (as a string) the version for which a package has been ignored. When no versions
        /// are ignored, an empty string will be returned; and when all versions are ignored an asterisk
        /// will be returned.
        /// </summary>
        public virtual async Task<string> GetIgnoredUpdatesVersionAsync()
        {
            try
            {
                return await Task.Run(() => IgnoredUpdatesDatabase.GetIgnoredVersion(_ignoredId))
                    ?? "";
            }
            catch (Exception ex)
            {
                Logger.Error($"Could not retrieve the ignored updates version for package {Id}");
                Logger.Error(ex);
                return "";
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public IPackage? GetAvailablePackage() =>
            DiscoverablePackagesLoader.Instance.GetEquivalentPackage(this);

        public IPackage? GetUpgradablePackage() =>
            UpgradablePackagesLoader.Instance.GetEquivalentPackage(this);

        public IReadOnlyList<IPackage> GetInstalledPackages() =>
            InstalledPackagesLoader.Instance.GetEquivalentPackages(this);

        public virtual void SetTag(PackageTag tag)
        {
            Tag = tag;
        }

        public virtual bool NewerVersionIsInstalled()
        {
            foreach (var p in GetInstalledPackages())
            {
                if (p.NormalizedVersion >= this.NormalizedNewVersion)
                {
                    return true;
                }
            }

            return false;
        }

        public async Task<string?> GetInstallerFileName()
        {
            if (Manager.Name.StartsWith("PowerShell") || Manager.Name.StartsWith(".NET"))
            {
                return CoreTools.MakeValidFileName($"{Id}.nupkg");
            }
            else
            {
                if (!Details.IsPopulated)
                    await Details.Load();
                if (Details.InstallerUrl is null)
                    return null;
                return await CoreTools.GetFileNameAsync(Details.InstallerUrl);
            }
        }

        public virtual bool IsUpdateMinor()
        {
            if (!IsUpgradable)
                return false;

            return NormalizedVersion.Major == NormalizedNewVersion.Major
                && NormalizedVersion.Minor == NormalizedNewVersion.Minor
                && (
                    NormalizedVersion.Patch != NormalizedNewVersion.Patch
                    || NormalizedVersion.Remainder != NormalizedNewVersion.Remainder
                );
        }

        public virtual Task<InstallOptions> GetInstallOptions() =>
            InstallOptionsFactory.LoadApplicableAsync(this);

        public virtual async Task<SerializablePackage> AsSerializableAsync()
        {
            return new SerializablePackage
            {
                Id = Id,
                Name = Name,
                Version = VersionString,
                Source = Source.Name,
                ManagerName = Manager.Name,
                InstallationOptions = await InstallOptionsFactory.LoadForPackageAsync(this),
                Updates = new SerializableUpdatesOptions
                {
                    IgnoredVersion = await GetIgnoredUpdatesVersionAsync(),
                    UpdatesIgnored = await HasUpdatesIgnoredAsync(),
                },
            };
        }

        public SerializableIncompatiblePackage AsSerializable_Incompatible()
        {
            return new SerializableIncompatiblePackage
            {
                Id = Id,
                Name = Name,
                Version = VersionString,
                Source = Source.Name,
            };
        }

        public static void ResetIconCache()
        {
            _cachedIconPaths.Clear();
        }

        private static string GenerateIconId(Package p)
        {
            string iconId;
            try
            {
                iconId = p.Manager.Name switch
                {
                    "Winget" => p.Source.Name switch
                    {
                        "Steam" => p
                            .Id.ToLower()
                            .Split("\\")[^1]
                            .Replace("steam app ", "steam-")
                            .Trim(),
                        "Local PC" => p.Id.Split("\\")[^1],
                        // If the first underscore is before the period, this ID has no publisher
                        "Microsoft Store" => p.Id.IndexOf('_') < p.Id.IndexOf('.')
                            ?
                            // no publisher: remove `MSIX\`, then the standard ending _version_arch__{random p.Id}
                            string.Join('_', p.Id.Split("\\")[1].Split("_")[0..^4])
                            :
                            // remove the publisher (before the first .), then the standard _version_arch__{random p.Id}
                            string.Join(
                                '_',
                                string.Join('.', p.Id.Split(".")[1..]).Split("_")[0..^4]
                            ),
                        _ => string.Join('.', p.Id.Split(".")[1..]),
                    },
                    "Scoop" => p.Id.Replace(".app", ""),
                    "Chocolatey" => p.Id.Replace(".install", "").Replace(".portable", ""),
                    "vcpkg" => p.Id.Split(":")[0].Split("[")[0],
                    _ => p.Id,
                };
            }
            catch
            {
                iconId = p.Id;
            }

            return iconId
                .ToLower()
                .Replace('_', '-')
                .Replace('.', '-')
                .Replace(' ', '-')
                .Replace('/', '-')
                .Replace(',', '-');
        }
    }
}
