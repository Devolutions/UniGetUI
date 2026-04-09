using System.Reflection;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

internal sealed class TestUpgradablePackagesLoader : UpgradablePackagesLoader
{
    private static readonly FieldInfo UpdatesTimerField = typeof(UpgradablePackagesLoader).GetField(
        "UpdatesTimer",
        BindingFlags.Instance | BindingFlags.NonPublic
    )!;

    public TestUpgradablePackagesLoader(IReadOnlyList<IPackageManager> managers)
        : base(managers) { }

    public Task<bool> EvaluatePackageAsync(IPackage package) => IsPackageValid(package);

    public Task ApplyWhenAddingPackageAsync(IPackage package) => WhenAddingPackage(package);

    public void StartTimer() => StartAutoCheckTimeout();

    public double? GetTimerIntervalMilliseconds() =>
        (UpdatesTimerField.GetValue(this) as System.Timers.Timer)?.Interval;
}
