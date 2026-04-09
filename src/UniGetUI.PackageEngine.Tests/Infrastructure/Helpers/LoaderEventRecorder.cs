using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

public sealed class LoaderEventRecorder
{
    public LoaderEventRecorder(AbstractPackageLoader loader)
    {
        loader.StartedLoading += (_, _) =>
        {
            StartedLoading = true;
            StartedLoadingCount++;
        };
        loader.FinishedLoading += (_, _) =>
        {
            FinishedLoading = true;
            FinishedLoadingCount++;
        };
        loader.PackagesChanged += (_, args) => Changes.Add(args);
    }

    public bool StartedLoading { get; private set; }

    public int StartedLoadingCount { get; private set; }

    public bool FinishedLoading { get; private set; }

    public int FinishedLoadingCount { get; private set; }

    public List<PackagesChangedEvent> Changes { get; } = [];

    public IReadOnlyList<UniGetUI.PackageEngine.Interfaces.IPackage> AddedPackages =>
        Changes.SelectMany(change => change.AddedPackages).ToArray();

    public IReadOnlyList<UniGetUI.PackageEngine.Interfaces.IPackage> RemovedPackages =>
        Changes.SelectMany(change => change.RemovedPackages).ToArray();
}
