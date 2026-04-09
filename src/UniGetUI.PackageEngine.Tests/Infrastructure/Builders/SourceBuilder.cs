using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

public sealed class SourceBuilder
{
    private IPackageManager? _manager;
    private string _name = "default";
    private Uri _url = new("https://example.test/default");
    private int? _packageCount = 0;
    private string _updateDate = string.Empty;
    private bool _isVirtualManager;

    public SourceBuilder WithManager(IPackageManager manager)
    {
        _manager = manager;
        return this;
    }

    public SourceBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public SourceBuilder WithUrl(string url)
    {
        _url = new Uri(url);
        return this;
    }

    public SourceBuilder WithPackageCount(int? packageCount)
    {
        _packageCount = packageCount;
        return this;
    }

    public SourceBuilder WithUpdateDate(string updateDate)
    {
        _updateDate = updateDate;
        return this;
    }

    public SourceBuilder AsVirtualManager(bool isVirtualManager = true)
    {
        _isVirtualManager = isVirtualManager;
        return this;
    }

    public ManagerSource Build()
    {
        var manager = _manager ?? new PackageManagerBuilder().Build();
        return new ManagerSource(
            manager,
            _name,
            _url,
            _packageCount,
            _updateDate,
            _isVirtualManager
        );
    }
}
