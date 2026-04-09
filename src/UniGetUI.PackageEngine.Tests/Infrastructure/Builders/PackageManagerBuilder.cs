using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

public sealed class PackageManagerBuilder
{
    private string _name = "TestManager";
    private string _displayName = "Test Manager";
    private Func<ManagerCapabilities, ManagerCapabilities> _capabilitiesFactory = static capabilities => capabilities;
    private Func<TestPackageManager, IReadOnlyList<IManagerSource>> _sourcesFactory = manager => [manager.DefaultSource];
    private Func<TestPackageManager, IReadOnlyList<Package>> _availableUpdatesFactory = static _ => [];
    private Func<TestPackageManager, IReadOnlyList<Package>> _installedPackagesFactory = static _ => [];
    private Action<TestPackageDetailsHelper>? _detailsConfiguration;
    private Action<TestPackageOperationHelper>? _operationConfiguration;
    private Action<TestSourceHelper>? _sourceConfiguration;
    private Action<TestPackageManager>? _managerConfiguration;

    public PackageManagerBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public PackageManagerBuilder WithDisplayName(string displayName)
    {
        _displayName = displayName;
        return this;
    }

    public PackageManagerBuilder ConfigureCapabilities(
        Func<ManagerCapabilities, ManagerCapabilities> capabilitiesFactory
    )
    {
        _capabilitiesFactory = capabilitiesFactory;
        return this;
    }

    public PackageManagerBuilder WithSources(
        Func<TestPackageManager, IReadOnlyList<IManagerSource>> sourcesFactory
    )
    {
        _sourcesFactory = sourcesFactory;
        return this;
    }

    public PackageManagerBuilder WithFindPackages(
        Func<TestPackageManager, string, IReadOnlyList<Package>> findPackagesFactory
    )
    {
        _managerConfiguration += manager => manager.SetFindPackages(query => findPackagesFactory(manager, query));
        return this;
    }

    public PackageManagerBuilder WithInstalledPackages(
        Func<TestPackageManager, IReadOnlyList<Package>> installedPackagesFactory
    )
    {
        _installedPackagesFactory = installedPackagesFactory;
        return this;
    }

    public PackageManagerBuilder WithAvailableUpdates(
        Func<TestPackageManager, IReadOnlyList<Package>> availableUpdatesFactory
    )
    {
        _availableUpdatesFactory = availableUpdatesFactory;
        return this;
    }

    public PackageManagerBuilder ConfigureDetails(Action<TestPackageDetailsHelper> configure)
    {
        _detailsConfiguration = configure;
        return this;
    }

    public PackageManagerBuilder ConfigureOperation(Action<TestPackageOperationHelper> configure)
    {
        _operationConfiguration = configure;
        return this;
    }

    public PackageManagerBuilder ConfigureSources(Action<TestSourceHelper> configure)
    {
        _sourceConfiguration = configure;
        return this;
    }

    public PackageManagerBuilder ConfigureManager(Action<TestPackageManager> configure)
    {
        _managerConfiguration += configure;
        return this;
    }

    public TestPackageManager Build(bool initialize = true)
    {
        var manager = new TestPackageManager(_name, _displayName);
        manager.Capabilities = _capabilitiesFactory(manager.Capabilities);

        _detailsConfiguration?.Invoke(manager.TestDetailsHelper);
        _operationConfiguration?.Invoke(manager.TestOperationHelper);
        _sourceConfiguration?.Invoke(manager.TestSourcesHelper);

        manager.SetKnownSources(_sourcesFactory(manager));
        manager.SetFindPackages(_ => []);
        manager.SetAvailableUpdates(() => _availableUpdatesFactory(manager));
        manager.SetInstalledPackages(() => _installedPackagesFactory(manager));

        _managerConfiguration?.Invoke(manager);

        if (initialize)
        {
            manager.Initialize();
        }

        return manager;
    }
}
