using UniGetUI.PackageEngine.Classes.Manager.Providers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

/// <summary>
/// The sources helper of a manager that adds and removes sources inside UniGetUI.
/// </summary>
public sealed class TestInProcessSourceHelper(TestPackageManager manager)
    : BaseSourceHelper(manager),
        IInProcessSourceHelper
{
    private readonly List<IManagerSource> _sources = [manager.DefaultSource];

    public Func<IManagerSource, IOperationOutput, OperationVeredict> AddFactory { get; set; } =
        static (_, _) => OperationVeredict.Success;

    public Func<IManagerSource, IOperationOutput, OperationVeredict> RemoveFactory { get; set; } =
        static (_, _) => OperationVeredict.Success;

    public int AddCalls { get; private set; }

    public int RemoveCalls { get; private set; }

    public Task<OperationVeredict> AddSourceAsync(
        IManagerSource source,
        IOperationOutput output,
        CancellationToken cancellationToken
    )
    {
        AddCalls++;
        var veredict = AddFactory(source, output);
        if (veredict is OperationVeredict.Success)
        {
            _sources.Add(source);
            InvalidateSourcesCache();
        }

        return Task.FromResult(veredict);
    }

    public Task<OperationVeredict> RemoveSourceAsync(
        IManagerSource source,
        IOperationOutput output,
        CancellationToken cancellationToken
    )
    {
        RemoveCalls++;
        var veredict = RemoveFactory(source, output);
        if (veredict is OperationVeredict.Success)
        {
            _sources.RemoveAll(s => s.Name == source.Name);
            InvalidateSourcesCache();
        }

        return Task.FromResult(veredict);
    }

    public override string[] GetAddSourceParameters(IManagerSource source) =>
        throw new InvalidOperationException("This manager runs inside UniGetUI and has no command line");

    public override string[] GetRemoveSourceParameters(IManagerSource source) =>
        throw new InvalidOperationException("This manager runs inside UniGetUI and has no command line");

    protected override OperationVeredict _getAddSourceOperationVeredict(
        IManagerSource source,
        int ReturnCode,
        string[] Output
    ) => throw new InvalidOperationException("This manager runs inside UniGetUI and has no process output");

    protected override OperationVeredict _getRemoveSourceOperationVeredict(
        IManagerSource source,
        int ReturnCode,
        string[] Output
    ) => throw new InvalidOperationException("This manager runs inside UniGetUI and has no process output");

    protected override IReadOnlyList<IManagerSource> GetSources_UnSafe() => _sources.ToArray();
}
