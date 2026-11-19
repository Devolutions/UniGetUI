using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Tests;

public sealed class SchedulerViewModelTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(SchedulerViewModelTests),
        Guid.NewGuid().ToString("N")
    );

    public SchedulerViewModelTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
    }

    public void Dispose()
    {
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void TheWindowOpenOptionIsEnabledWhenEitherQuitOptionIsOn(
        bool quitAfterUpdates,
        bool quitWhenNothingToInstall,
        bool expected)
    {
        Settings.Set(Settings.K.QuitAfterAutomaticUpdates, quitAfterUpdates);
        Settings.Set(Settings.K.QuitWhenNothingToInstall, quitWhenNothingToInstall);

        using var viewModel = new SchedulerViewModel();

        Assert.Equal(expected, viewModel.IsAutomaticQuitEnabled);
    }

    [Fact]
    public void TheWindowOpenOptionFollowsTheQuitOptionsWhenTheyAreToggled()
    {
        using var viewModel = new SchedulerViewModel();
        Assert.False(viewModel.IsAutomaticQuitEnabled);

        Settings.Set(Settings.K.QuitWhenNothingToInstall, true);
        viewModel.RefreshAutomaticQuitStateCommand.Execute(null);
        Assert.True(viewModel.IsAutomaticQuitEnabled);

        Settings.Set(Settings.K.QuitWhenNothingToInstall, false);
        viewModel.RefreshAutomaticQuitStateCommand.Execute(null);
        Assert.False(viewModel.IsAutomaticQuitEnabled);
    }
}
