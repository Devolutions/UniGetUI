using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Tests;

public class MainWindowBundleNavigationTests
{
    [Fact]
    public async Task BundleLoadWaitsForApprovedNavigation()
    {
        var navigation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool loaded = false;

        Task<bool> pending = MainWindowViewModel.NavigateThenLoadBundleAsync(
            () => navigation.Task,
            () =>
            {
                loaded = true;
                return Task.CompletedTask;
            });

        Assert.False(pending.IsCompleted);
        Assert.False(loaded);

        navigation.SetResult(true);

        Assert.True(await pending);
        Assert.True(loaded);
    }

    [Fact]
    public async Task RejectedNavigationDoesNotLoadBundle()
    {
        bool loaded = false;

        bool result = await MainWindowViewModel.NavigateThenLoadBundleAsync(
            () => Task.FromResult(false),
            () =>
            {
                loaded = true;
                return Task.CompletedTask;
            });

        Assert.False(result);
        Assert.False(loaded);
    }
}
