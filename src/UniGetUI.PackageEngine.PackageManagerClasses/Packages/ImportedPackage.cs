using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Serializable;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.PackageClasses
{
    public partial class ImportedPackage : Package
    {
        /// <summary>
        /// Construct an invalid package with a given name, id, version, source and manager, and an optional scope.
        /// </summary>
        public SerializableUpdatesOptions updates_options;
        public InstallOptions installation_options;

        private readonly string _version;
        private static readonly string _latestString = CoreTools.Translate("Latest");

        public override string VersionString
        {
            get
            {
                if (installation_options.Version != "")
                    return installation_options.Version;
                return _latestString;
            }
        }

        public ImportedPackage(
            SerializablePackage raw_data,
            IPackageManager manager,
            IManagerSource source
        )
            : base(raw_data.Name, raw_data.Id, raw_data.Version, source, manager)
        {
            _version = raw_data.Version;
            installation_options = raw_data.InstallationOptions;
            updates_options = raw_data.Updates;
        }

        public async Task<Package> RegisterAndGetPackageAsync()
        {
            var package = new Package(Name, Id, _version, Source, Manager);
            await InstallOptionsFactory.SaveForPackageAsync(installation_options, package);

            if (updates_options.UpdatesIgnored)
                await AddToIgnoredUpdatesAsync(updates_options.IgnoredVersion);

            return package;
        }

        public override Task<InstallOptions> GetInstallOptions() =>
            Task.FromResult(installation_options.Copy());

        public override Task<SerializablePackage> AsSerializableAsync()
        {
            return Task.FromResult(
                new SerializablePackage
                {
                    Id = Id,
                    Name = Name,
                    Version = _version,
                    Source = Source.Name,
                    ManagerName = Manager.Id,
                    InstallationOptions = installation_options.Copy(),
                    Updates = updates_options.Copy(),
                }
            );
        }

        public void FirePackageVersionChangedEvent()
        {
            OnPropertyChanged(nameof(VersionString));
        }
    }
}
