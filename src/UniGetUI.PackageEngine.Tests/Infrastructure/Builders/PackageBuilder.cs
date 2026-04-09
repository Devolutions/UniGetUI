using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Structs;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

public sealed class PackageBuilder
{
    private string _name = "Test Package";
    private string _id = "Contoso.Test";
    private string _installedVersion = "1.0.0";
    private string? _availableVersion;
    private IManagerSource? _source;
    private IPackageManager? _manager;
    private OverridenInstallationOptions? _options;

    public PackageBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public PackageBuilder WithId(string id)
    {
        _id = id;
        return this;
    }

    public PackageBuilder WithVersion(string version)
    {
        _installedVersion = version;
        return this;
    }

    public PackageBuilder WithNewVersion(string version)
    {
        _availableVersion = version;
        return this;
    }

    public PackageBuilder WithManager(IPackageManager manager)
    {
        _manager = manager;
        return this;
    }

    public PackageBuilder WithSource(IManagerSource source)
    {
        _source = source;
        return this;
    }

    public PackageBuilder WithOptions(OverridenInstallationOptions options)
    {
        _options = options;
        return this;
    }

    public Package Build()
    {
        var manager = _manager ?? new PackageManagerBuilder().Build();
        var source = _source ?? manager.DefaultSource;

        return _availableVersion is null
            ? new Package(_name, _id, _installedVersion, source, manager, _options)
            : new Package(_name, _id, _installedVersion, _availableVersion, source, manager, _options);
    }
}
