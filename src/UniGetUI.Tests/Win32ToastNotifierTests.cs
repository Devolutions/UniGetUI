#if WINDOWS
using System.Xml.Linq;
using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public sealed class Win32ToastNotifierTests
{
    [Fact]
    public void DefaultToast_UsesTheDefaultScenarioWithoutActions()
    {
        XElement toast = XElement.Parse(Win32ToastNotifier.BuildToastXml("Updates found!", "3 packages", "unigetui://showOnUpdatesTab"));

        Assert.Equal("default", (string?)toast.Attribute("scenario"));
        Assert.Null(toast.Element("actions"));
        Assert.Equal("true", (string?)toast.Element("audio")!.Attribute("silent"));
    }

    [Fact]
    public void PersistentToast_UsesTheReminderScenarioWithAnOpenAndADismissButton()
    {
        XElement toast = XElement.Parse(Win32ToastNotifier.BuildToastXml(
            "Updates & \"fixes\"",
            "<3 packages>",
            "unigetui://showOnUpdatesTab",
            untilDismissed: true));

        Assert.Equal("reminder", (string?)toast.Attribute("scenario"));
        Assert.Equal("unigetui://showOnUpdatesTab", (string?)toast.Attribute("launch"));
        Assert.Equal(["Updates & \"fixes\"", "<3 packages>"], toast.Descendants("text").Select(t => t.Value));

        XElement[] actions = toast.Element("actions")!.Elements("action").ToArray();
        Assert.Equal(2, actions.Length);

        Assert.Equal("Open UniGetUI", (string?)actions[0].Attribute("content"));
        Assert.Equal("protocol", (string?)actions[0].Attribute("activationType"));
        Assert.Equal("unigetui://showOnUpdatesTab", (string?)actions[0].Attribute("arguments"));

        Assert.Equal("system", (string?)actions[1].Attribute("activationType"));
        Assert.Equal("dismiss", (string?)actions[1].Attribute("arguments"));
        Assert.Equal("true", (string?)toast.Element("audio")!.Attribute("silent"));
    }

    [Theory]
    [InlineData(WindowsAppNotificationBridge.UpdatesToastTag, true, true)]
    [InlineData(WindowsAppNotificationBridge.SelfUpdateToastTag, true, true)]
    [InlineData(WindowsAppNotificationBridge.UpdatesToastTag, false, false)]
    [InlineData(WindowsAppNotificationBridge.SelfUpdateToastTag, false, false)]
    [InlineData(null, true, false)]
    [InlineData("some-other-tag", true, false)]
    public void OnlyUpdateNoticesStayUntilDismissed_AndOnlyWhenEnabled(string? tag, bool settingEnabled, bool expected)
    {
        Assert.Equal(expected, WindowsAppNotificationBridge.KeepsUntilDismissed(tag, settingEnabled));
    }
}
#endif
