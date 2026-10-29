using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.NpmManager;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.Tests;

public class IgnoredPackageEntryViewModelTests
{
    private static IgnoredPackageEntryViewModel Entry(Package? update) =>
        new("npm\\example.tool", "example.tool", "Example tool", "Npm", "", "*", "1.0 -> 2.0", update);

    private static Package Update()
    {
        var manager = new Npm();
        return new Package("Example tool", "example.tool", "1.0",
            new ManagerSource(manager, "npm", new Uri("https://example.test/feed")), manager);
    }

    [Fact]
    public void OffersAnUpdateOnlyWhenOneIsWaiting()
    {
        Assert.False(Entry(null).CanUpdate);
        Assert.True(Entry(Update()).CanUpdate);
    }

    [Fact]
    public void HidesTheUpdateButtonWhileUpdating()
    {
        var entry = Entry(Update());
        List<string?> changed = [];
        entry.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        entry.IsUpdating = true;

        Assert.False(entry.CanUpdate);
        Assert.Contains(nameof(IgnoredPackageEntryViewModel.CanUpdate), changed);
    }

    [Theory]
    [InlineData(OperationStatus.InQueue, true)]
    [InlineData(OperationStatus.Running, true)]
    [InlineData(OperationStatus.Failed, false)]
    [InlineData(OperationStatus.Succeeded, false)]
    public void PicksUpAnUpdateStartedBeforeTheRowWasBuilt(OperationStatus status, bool updating)
    {
        var update = Update();
        using var operation = new UpdatePackageOperation(update, new InstallOptions()) { Status = status };
        AvaloniaOperationRegistry.Operations.Add(operation);
        try
        {
            var entry = Entry(update);

            Assert.Equal(updating, entry.IsUpdating);
            Assert.Equal(!updating, entry.CanUpdate);
            entry.Detach();
        }
        finally
        {
            AvaloniaOperationRegistry.Operations.Remove(operation);
        }
    }

    [Fact]
    public async Task UpdateDoesNothingWithoutAWaitingUpdate()
    {
        var entry = Entry(null);

        await entry.UpdateCommand.ExecuteAsync(null);

        Assert.False(entry.IsUpdating);
    }
}
