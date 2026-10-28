namespace UniGetUI.PackageEngine.ManagerClasses.Manager
{
    public enum ProxySupport
    {
        No,
        Partially,
        Yes,
    }

    public enum PackageReleaseDateSupport
    {
        No,
        Partial,
        Yes,
    }

    public struct SourceCapabilities
    {
        public bool KnowsUpdateDate { get; set; } = false;
        public bool KnowsPackageCount { get; set; } = false;
        public bool MustBeInstalledAsAdmin { get; set; } = false;
        // False when sources are declared elsewhere (such as .pkg feeds in the software catalog), so
        // UniGetUI shows no add or remove controls for them.
        public bool CanBeEdited { get; set; } = true;

        public SourceCapabilities() { }
    }

    public struct ManagerCapabilities
    {
        public bool IsDummy = false;
        public bool CanRunAsAdmin = false;
        public bool CanSkipIntegrityChecks = false;
        public bool CanRunInteractively = false;
        public bool CanRemoveDataOnUninstall = false;
        // False for managers that install but cannot remove, such as macOS .pkg files.
        public bool CanUninstall = true;
        // False when the installed inventory cannot tell which source a package came from (Chocolatey
        // assigns its default source; a macOS .pkg receipt or app records no feed).
        public bool InstalledPackagesKeepTheirSource = true;
        public bool CanDownloadInstaller = false;
        public bool CanUninstallPreviousVersionsAfterUpdate = false;
        public bool CanListDependencies = false;
        public bool SupportsReleaseNotes = false;
        public bool SupportsCustomVersions = false;
        public bool CanListVersions = true;
        public bool SupportsCustomVersionsOnUpdate = true;
        public bool SupportsCustomArchitectures = false;
        public string[] SupportedCustomArchitectures = [];
        public bool SupportsCustomScopes = false;
        // Whether a custom scope can be applied to update/uninstall too, not just install
        // (e.g. Windows PowerShell 5.x's Update-Module has no -Scope parameter)
        public bool SupportsCustomScopesOnUpdate = true;
        public bool SupportsCustomScopesOnUninstall = true;
        public bool SupportsPreRelease = false;
        public bool SupportsEmptySearchQuery = false;
        public bool SerializesOperations = false;
        public bool SupportsCustomLocations = false;
        public bool SupportsCustomSources = false;
        public bool SupportsCustomPackageIcons = false;
        public bool SupportsCustomPackageScreenshots = false;
        public ProxySupport SupportsProxy = ProxySupport.No;
        public bool SupportsProxyAuth = false;
        public PackageReleaseDateSupport KnowsPackageReleaseDate = PackageReleaseDateSupport.No;
        public SourceCapabilities Sources { get; set; }

        public ManagerCapabilities()
        {
            Sources = new SourceCapabilities();
        }

        public ManagerCapabilities(bool IsDummy)
        {
            Sources = new SourceCapabilities();
            this.IsDummy = IsDummy;
        }
    }
}
