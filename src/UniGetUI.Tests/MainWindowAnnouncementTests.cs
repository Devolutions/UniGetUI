using Avalonia.Automation;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Tests;

public class MainWindowAnnouncementTests
{
    private sealed class LiveRegion
    {
        public AutomationLiveSetting LiveSetting = AutomationLiveSetting.Polite;
        public string Text = "Previous announcement";
        public readonly List<(string Text, AutomationLiveSetting LiveSetting)> TextChanges = [];
        public readonly Queue<Action> PendingUpdates = new();

        public void Announce(AccessibilityAnnouncement announcement) =>
            MainWindowViewModel.ApplyAnnouncement(
                announcement,
                liveSetting => LiveSetting = liveSetting,
                text =>
                {
                    Text = text;
                    TextChanges.Add((text, LiveSetting));
                },
                PendingUpdates.Enqueue);

        public void RunPendingUpdates()
        {
            while (PendingUpdates.TryDequeue(out Action? update))
                update();
        }
    }

    [Theory]
    [InlineData(AutomationLiveSetting.Polite)]
    [InlineData(AutomationLiveSetting.Assertive)]
    public void ClearingTheLiveRegionNeverHappensWhileItIsLive(AutomationLiveSetting liveSetting)
    {
        var region = new LiveRegion();

        region.Announce(new AccessibilityAnnouncement("Installed packages", liveSetting));
        region.RunPendingUpdates();

        Assert.Contains(region.TextChanges, change => change.Text.Length == 0);
        Assert.All(
            region.TextChanges.Where(change => change.Text.Length == 0),
            change => Assert.Equal(AutomationLiveSetting.Off, change.LiveSetting));
        Assert.Equal("Installed packages", region.Text);
        Assert.Equal(liveSetting, region.LiveSetting);
    }

    [Fact]
    public void BackToBackAnnouncementsKeepTheLiveRegionSafe()
    {
        var region = new LiveRegion();

        region.Announce(new AccessibilityAnnouncement("Discover packages"));
        region.Announce(new AccessibilityAnnouncement("Operation failed", AutomationLiveSetting.Assertive));
        region.RunPendingUpdates();

        Assert.All(
            region.TextChanges.Where(change => change.Text.Length == 0),
            change => Assert.Equal(AutomationLiveSetting.Off, change.LiveSetting));
        Assert.Equal("Operation failed", region.Text);
        Assert.Equal(AutomationLiveSetting.Assertive, region.LiveSetting);
    }
}
