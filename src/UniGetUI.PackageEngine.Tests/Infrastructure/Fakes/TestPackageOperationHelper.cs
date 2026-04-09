using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

public sealed class TestPackageOperationHelper(TestPackageManager manager)
    : BasePkgOperationHelper(manager)
{
    public Func<IPackage, InstallOptions, OperationType, IReadOnlyList<string>> ParametersFactory { get; set; } =
        static (_, _, _) => [];

    public Func<IPackage, OperationType, IReadOnlyList<string>, int, OperationVeredict> ResultFactory { get; set; } =
        static (_, _, _, returnCode) => returnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation
    )
    {
        return ParametersFactory(package, options, operation);
    }

    protected override OperationVeredict _getOperationResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode
    )
    {
        return ResultFactory(package, operation, processOutput, returnCode);
    }
}
