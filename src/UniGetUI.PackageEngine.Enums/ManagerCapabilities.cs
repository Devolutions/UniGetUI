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

        public SourceCapabilities() { }
    }

    public struct ManagerCapabilities
    {
        public bool IsDummy = false;
        public bool CanRunAsAdmin = false;
        public bool CanSkipIntegrityChecks = false;
        public bool CanRunInteractively = false;
        public bool CanRemoveDataOnUninstall = false;
        public bool CanDownloadInstaller = false;
        public bool CanUninstallPreviousVersionsAfterUpdate = false;
        public bool CanListDependencies = false;
        public bool SupportsCustomVersions = false;
        public bool SupportsCustomArchitectures = false;
        public string[] SupportedCustomArchitectures = [];
        public bool SupportsCustomScopes = false;
        public bool SupportsPreRelease = false;
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
