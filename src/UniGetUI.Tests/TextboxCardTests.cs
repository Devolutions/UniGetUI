using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Views.Controls.Settings;
using UniGetUI.Core.Data;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Tests;

public sealed class TextboxCardTests : IDisposable
{
    private readonly HeadlessUnitTestSession _session = HeadlessUnitTestSession.StartNew(typeof(Application));
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), nameof(TextboxCardTests), Guid.NewGuid().ToString("N"));

    public TextboxCardTests()
    {
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        CoreSettings.ResetSettings();
    }

    public void Dispose()
    {
        _session.Dispose();
        CoreSettings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        Directory.Delete(_testRoot, recursive: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com/icons.json")]
    public Task LoadingSavedValueDoesNotNotify(string savedValue) => _session.Dispatch(() =>
    {
        CoreSettings.SetValue(CoreSettings.K.IconDataBaseURL, savedValue);
        int events = 0;
        int commands = 0;
        var card = new TextboxCard
        {
            ValueChangedCommand = new RelayCommand(() => commands++),
        };
        card.ValueChanged += (_, _) => events++;
        card.SettingName = CoreSettings.K.IconDataBaseURL;

        // Avalonia posts TextChanged, so loading the value can reach handlers attached afterwards.
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, events);
        Assert.Equal(0, commands);
        Assert.Equal(savedValue, CoreSettings.GetValue(CoreSettings.K.IconDataBaseURL));
    }, CancellationToken.None);

    [Fact]
    public Task EditingAndClearingValueSavesAndNotifiesOncePerChange() => _session.Dispatch(() =>
    {
        var card = new TextboxCard { SettingName = CoreSettings.K.IconDataBaseURL };
        Dispatcher.UIThread.RunJobs();
        int events = 0;
        int commands = 0;
        card.ValueChanged += (_, _) => events++;
        card.ValueChangedCommand = new RelayCommand(() => commands++);
        TextBox textBox = Assert.Single(Assert.IsType<StackPanel>(card.Content).Children.OfType<TextBox>());

        textBox.Text = "https://example.com/icons.json";
        Dispatcher.UIThread.RunJobs();
        card.SaveValue();

        Assert.Equal(textBox.Text, CoreSettings.GetValue(CoreSettings.K.IconDataBaseURL));
        Assert.Equal(1, events);
        Assert.Equal(1, commands);

        textBox.Text = "";
        Dispatcher.UIThread.RunJobs();
        card.SaveValue();

        Assert.Equal("", CoreSettings.GetValue(CoreSettings.K.IconDataBaseURL));
        Assert.False(CoreSettings.Get(CoreSettings.K.IconDataBaseURL));
        Assert.Equal(2, events);
        Assert.Equal(2, commands);
    }, CancellationToken.None);

    [Fact]
    public Task NumericFilteringNotifiesOnlyWhenSanitizedValueChanges() => _session.Dispatch(() =>
    {
        CoreSettings.SetValue(CoreSettings.K.IconDataBaseURL, "12");
        var card = new TextboxCard { SettingName = CoreSettings.K.IconDataBaseURL, IsNumericOnly = true };
        Dispatcher.UIThread.RunJobs();
        int changes = 0;
        card.ValueChanged += (_, _) => changes++;
        TextBox textBox = Assert.Single(Assert.IsType<StackPanel>(card.Content).Children.OfType<TextBox>());

        textBox.Text = "1a2";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("12", textBox.Text);
        Assert.Equal(0, changes);

        textBox.Text = "3a4";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("34", CoreSettings.GetValue(CoreSettings.K.IconDataBaseURL));
        Assert.Equal(1, changes);
    }, CancellationToken.None);
}
