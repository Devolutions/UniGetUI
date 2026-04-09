using UniGetUI.PackageEngine.Classes.Manager.Providers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

public sealed class TestSourceHelper(TestPackageManager manager) : BaseSourceHelper(manager)
{
    private IReadOnlyList<IManagerSource> _sources = [manager.DefaultSource];

    public Func<IManagerSource, string[]> AddParametersFactory { get; set; } =
        static source => ["source", "add", source.Name];

    public Func<IManagerSource, string[]> RemoveParametersFactory { get; set; } =
        static source => ["source", "remove", source.Name];

    public Func<IManagerSource, int, string[], OperationVeredict> AddVeredictFactory { get; set; } =
        static (_, returnCode, _) => returnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;

    public Func<IManagerSource, int, string[], OperationVeredict> RemoveVeredictFactory { get; set; } =
        static (_, returnCode, _) => returnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;

    public void SetSources(IEnumerable<IManagerSource> sources)
    {
        _sources = sources.ToArray();
    }

    public override string[] GetAddSourceParameters(IManagerSource source)
    {
        return AddParametersFactory(source);
    }

    public override string[] GetRemoveSourceParameters(IManagerSource source)
    {
        return RemoveParametersFactory(source);
    }

    protected override OperationVeredict _getAddSourceOperationVeredict(
        IManagerSource source,
        int ReturnCode,
        string[] Output
    )
    {
        return AddVeredictFactory(source, ReturnCode, Output);
    }

    protected override OperationVeredict _getRemoveSourceOperationVeredict(
        IManagerSource source,
        int ReturnCode,
        string[] Output
    )
    {
        return RemoveVeredictFactory(source, ReturnCode, Output);
    }

    protected override IReadOnlyList<IManagerSource> GetSources_UnSafe()
    {
        return _sources;
    }
}
