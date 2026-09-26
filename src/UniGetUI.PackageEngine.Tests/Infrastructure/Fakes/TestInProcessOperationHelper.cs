using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

/// <summary>
/// The operation helper of a manager that performs its package operations inside UniGetUI, so it
/// has neither a command line nor process output.
/// </summary>
public sealed class TestInProcessOperationHelper(TestPackageManager manager)
    : BasePkgOperationHelper(manager),
        IInProcessPackageOperationHelper
{
    public Func<
        IPackage,
        InstallOptions,
        OperationType,
        IOperationOutput,
        CancellationToken,
        Task<OperationVeredict>
    > PerformFactory { get; set; } =
        static (_, _, _, _, _) => Task.FromResult(OperationVeredict.Success);

    public int PerformCalls { get; private set; }

    public Task<OperationVeredict> PerformAsync(
        IPackage package,
        InstallOptions options,
        OperationType operation,
        IOperationOutput output,
        CancellationToken cancellationToken
    )
    {
        PerformCalls++;
        return PerformFactory(package, options, operation, output, cancellationToken);
    }

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation
    ) => throw new InvalidOperationException("This manager runs inside UniGetUI and has no command line");

    protected override OperationVeredict _getOperationResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode
    ) => throw new InvalidOperationException("This manager runs inside UniGetUI and has no process output");
}
