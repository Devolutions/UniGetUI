using System.ComponentModel;
using System.Runtime.CompilerServices;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Serializable;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;

namespace UniGetUI.PackageEngine.PackageClasses
{
    public partial class InvalidImportedPackage : IPackage, INotifyPropertyChanged
    {
        public IPackageDetails Details { get; }

        public PackageTag Tag
        {
            get => PackageTag.Unavailable;
            set { }
        }

        private bool __is_checked;
        public bool IsChecked
        {
            get { return __is_checked; }
            set
            {
                __is_checked = value;
                OnPropertyChanged(nameof(IsChecked));
            }
        }

        private readonly long __hash;
        private readonly long __extended_hash;

        private static OverridenInstallationOptions __overriden_options;
        public ref OverridenInstallationOptions OverridenOptions
        {
            get => ref __overriden_options;
        }

        public string Name { get; }

        public string Id { get; }

        public string VersionString { get; }

        public CoreTools.Version NormalizedVersion { get; }

        public CoreTools.Version NormalizedNewVersion
        {
            get => CoreTools.Version.Null;
        }

        public IManagerSource Source { get; }

        public IPackageManager Manager { get; }

        public string NewVersionString
        {
            get => "";
        }

        public bool IsUpgradable
        {
            get => false;
        }

        public string Scope
        {
            get => PackageScope.Local;
            set { }
        }

        public string SourceAsString { get; }

        public string AutomationName { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public InvalidImportedPackage(SerializableIncompatiblePackage data, IManagerSource source)
        {
            Name = data.Name;
            Id = data.Id.Split('\\')[^1];
            VersionString = data.Version;
            NormalizedVersion = CoreTools.VersionStringToStruct(data.Version);
            SourceAsString = data.Source;
            AutomationName = data.Name;
            Manager = source.Manager;
            Source = source;
            Details = new PackageDetails(this);

            __hash = CoreTools.HashStringAsLong(data.Name + data.Id);
            __extended_hash = CoreTools.HashStringAsLong(data.Name + data.Id + data.Version);
        }

        public Task AddToIgnoredUpdatesAsync(string version = "*")
        {
            return Task.CompletedTask;
        }

        public Task<InstallOptions> GetInstallOptions() => Task.FromResult(new InstallOptions());

        public Task<SerializablePackage> AsSerializableAsync()
        {
            throw new NotImplementedException();
        }

        public SerializableIncompatiblePackage AsSerializable_Incompatible()
        {
            return new SerializableIncompatiblePackage
            {
                Name = Name,
                Id = Id,
                Version = VersionString,
                Source = SourceAsString,
            };
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public IPackage? GetAvailablePackage()
        {
            return null;
        }

        public long GetHash()
        {
            return __hash;
        }

        public string GetIconId()
        {
            throw new NotImplementedException();
        }

        public Uri GetIconUrl()
        {
            return new Uri("ms-appx:///Assets/Images/package_color.png");
        }

        public Uri? GetIconUrlIfAny()
        {
            return null;
        }

        public Task<string> GetIgnoredUpdatesVersionAsync()
        {
            return Task.FromResult(String.Empty);
        }

        public IReadOnlyList<IPackage> GetInstalledPackages()
        {
            return [];
        }

        public IReadOnlyList<Uri> GetScreenshots()
        {
            return [];
        }

        public IPackage? GetUpgradablePackage()
        {
            return null;
        }

        public long GetVersionedHash()
        {
            return __extended_hash;
        }

        public async Task<bool> HasUpdatesIgnoredAsync(string version = "*")
        {
            return false;
        }

        public bool Equals(IPackage? other)
        {
            return __hash == other?.GetVersionedHash();
        }

        public bool IsEquivalentTo(IPackage? other)
        {
            return __hash == other?.GetHash();
        }

        public bool NewerVersionIsInstalled()
        {
            return false;
        }

        public Task<string?> GetInstallerFileName()
        {
            return Task.FromResult<string?>("");
        }

        public bool IsUpdateMinor(int level = InstallOptions.DefaultSkipMinorLevel)
        {
            return false;
        }

        public async Task RemoveFromIgnoredUpdatesAsync()
        {
            return;
        }

        public void SetTag(PackageTag tag)
        {
            return;
        }

#pragma warning restore CS1998
    }

    public class NullSource : IManagerSource
    {
        public static NullSource Instance = new(CoreTools.Translate("Unknown"));
        public IconType IconId { get; }
        public bool IsVirtualManager { get; }
        public IPackageManager Manager { get; }
        public string Name { get; }
        public Uri Url { get; set; }
        public int? PackageCount { get; }
        public string? UpdateDate { get; }

        public string AsString
        {
            get => Name;
        }
        public string AsString_DisplayName
        {
            get => Name;
        }

        public NullSource(string name)
        {
            Name = name;
            Url = new Uri("about:blank");
            IsVirtualManager = true;
            IconId = IconType.Help;
            Manager = (IPackageManager)NullPackageManager.Instance;
        }

        /// <summary>
        /// Returns a human-readable string representing the source name
        /// </summary>
        public override string ToString()
        {
            return Name;
        }

        public void RefreshSourceNames() { }
    }
}
