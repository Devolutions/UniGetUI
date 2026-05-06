using System.ComponentModel;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Serializable;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;

namespace UniGetUI.PackageEngine.Interfaces
{
    public interface IPackage : INotifyPropertyChanged, IEquatable<IPackage>
    {
        public IPackageDetails Details { get; }
        public PackageTag Tag { get; set; }
        public bool IsChecked { get; set; }
        public string Name { get; }
        public string Id { get; }
        public string VersionString { get; }
        public CoreTools.Version NormalizedVersion { get; }
        public CoreTools.Version NormalizedNewVersion { get; }
        public IManagerSource Source { get; }
        public IPackageManager Manager { get; }
        public string NewVersionString { get; }
        public bool IsUpgradable { get; }
        public ref OverridenInstallationOptions OverridenOptions { get; }
        public string AutomationName { get; }

        /// <summary>
        /// Returns an identifier that can be used to compare different package instances that refer to the same package.
        /// What is taken into account:
        ///    - Manager and Source
        ///    - Package Identifier
        /// For more specific comparison use GetVersionedHash()
        /// </summary>
        public long GetHash();

        /// <summary>
        /// Returns an identifier that can be used to compare different package instances that refer to the same package.
        /// What is taken into account:
        ///    - Manager and Source
        ///    - Package Identifier
        ///    - Package version
        ///    - Package new version (if any)
        /// </summary>
        public long GetVersionedHash();

        /// <summary>
        /// Check whether two packages are **REALLY** the same package.
        /// What is taken into account:
        ///    - Manager and Source
        ///    - Package Identifier
        ///    - Package version
        ///    - Package new version (if any)
        /// </summary>
        public bool Equals(object? other);

        /// <summary>
        /// Check whether two package instances represent the same package.
        /// What is taken into account:
        ///    - Manager and Source
        ///    - Package Identifier
        /// For more specific comparison use package.Equals(object? other)
        /// </summary>
        /// <param name="other">A package</param>
        /// <returns>Whether the two instances refer to the same instance</returns>
        public bool IsEquivalentTo(IPackage? other);

        /// <summary>
        /// Load the package's normalized icon id,
        /// </summary>
        /// <returns>a string with the package's normalized icon id</returns>
        public string GetIconId();

        /// <summary>
        /// Get the package's icon url. If the package has no icon, a fallback image is returned.
        /// After calling this method, the returned URL points to a location on the local machine
        /// </summary>
        /// <returns>An always-valid URI object, pointing to a file:// or to a ms-appx:// URL</returns>
        public Uri GetIconUrl();

        /// <summary>
        /// Get the package's icon url. If the package has no icon, null is returned.
        /// After calling this method, the returned URL (if any) points to a location on the local machine
        /// </summary>
        /// <returns>An always-valid URI object, pointing to a file:// or to a ms-appx:// URL</returns>
        public Uri? GetIconUrlIfAny();

        /// <summary>
        /// Retrieves a list og URIs representing the available screenshots for this package.
        /// </summary>
        public IReadOnlyList<Uri> GetScreenshots();

        /// <summary>
        /// Adds the package to the ignored updates list. If no version is provided, all updates are ignored.
        /// Calling this method will override older ignored updates.
        /// </summary>
        public Task AddToIgnoredUpdatesAsync(string version = "*");

        /// <summary>
        /// Removes the package from the ignored updates list, either if it is ignored for all updates or for a specific version only.
        /// </summary>
        public Task RemoveFromIgnoredUpdatesAsync();

        /// <summary>
        /// Returns true if the package's updates are ignored. If the version parameter
        /// is passed it will be checked if that version is ignored. Please note that if
        /// all updates are ignored, calling this method with a specific version will
        /// still return true, although the passed version is not explicitly ignored.
        /// </summary>
        public Task<bool> HasUpdatesIgnoredAsync(string version = "*");

        /// <summary>
        /// Returns (as a string) the version for which a package has been ignored. When no versions
        /// are ignored, an empty string will be returned; and when all versions are ignored an asterisk
        /// will be returned.
        /// </summary>
        public Task<string> GetIgnoredUpdatesVersionAsync();

        /// <summary>
        /// Returns the corresponding installed Package object. Will return null if not applicable
        /// </summary>
        /// <returns>a Package object if found, null if not</returns>
        public IReadOnlyList<IPackage> GetInstalledPackages();

        /// <summary>
        /// Returns the corresponding available Package object. Will return null if not applicable
        /// </summary>
        /// <returns>a Package object if found, null if not</returns>
        public IPackage? GetAvailablePackage();

        /// <summary>
        /// Returns the corresponding upgradable Package object. Will return null if not applicable
        /// </summary>
        /// <returns>a Package object if found, null if not</returns>
        public IPackage? GetUpgradablePackage();

        /// <summary>
        /// Sets the package tag. You may as well use the Tag property.
        /// This function is used for compatibility with the ? operator
        /// </summary>
        public void SetTag(PackageTag tag);

        /// <summary>
        /// Checks whether a new version of this package is installed
        /// </summary>
        public bool NewerVersionIsInstalled();

        /// <summary>
        /// Gets the installer file name for this package
        /// </summary>
        /// <returns></returns>
        public Task<string?> GetInstallerFileName();

        /// <summary>
        /// Checks whether a new update of this package is a minor update or not (0.0.x)
        /// </summary>
        /// <returns>False if the update is a major update or the update doesn't exist, true if it's a minor update</returns>
        public bool IsUpdateMinor();

        /// <summary>
        /// Gets the applicable install options for this package
        /// </summary>
        /// <returns>The package install options, or, if not overriden, the manager install options</returns>
        public Task<InstallOptions> GetInstallOptions();

        public Task<SerializablePackage> AsSerializableAsync();

        public SerializableIncompatiblePackage AsSerializable_Incompatible();
    }
}
