using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Devolutions.AgentSkills;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;
using SkillsManager = UniGetUI.PackageEngine.Managers.SkillsManager.AgentSkills;

namespace UniGetUI.Tests;

internal sealed class AgentSkillsSettingsTestFixture : IDisposable
{
    // These existing test overrides have private getters. Reflection is restricted to
    // saving/restoring fixture state, never used to bypass a production interaction seam.
    private readonly string? _previousData = ReadOverride(typeof(CoreData), nameof(CoreData.TEST_DataDirectoryOverride));
    private readonly string? _previousSecure = ReadOverride(typeof(SecureSettings), nameof(SecureSettings.TEST_SecureSettingsRootOverride));
    private readonly ConcurrentDictionary<string, bool> _secureCache =
        (ConcurrentDictionary<string, bool>)typeof(SecureSettings).GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    private readonly KeyValuePair<string, bool>[] _previousSecureCache;

    public string Root { get; } = Path.Combine(Path.GetTempPath(),
        "UniGetUI-AgentSkillsSettingsTests", Guid.NewGuid().ToString("N"));
    public string InstalledRoot => Path.Combine(Root, "installed");
    public AgentInfo[] Catalogue { get; }
    public const string TargetMessage = "Select at least one coding agent, or choose Automatic.";
    public const string VersionMessage = "Enter a valid version number, or choose Latest available.";

    public AgentSkillsSettingsTestFixture()
    {
        _previousSecureCache = _secureCache.ToArray();
        _secureCache.Clear();
        CoreData.TEST_DataDirectoryOverride = Path.Combine(Root, "Data");
        SecureSettings.TEST_SecureSettingsRootOverride = Path.Combine(Root, "Secure");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        CoreSettings.ResetSettings();
        Catalogue =
        [
            Agent("detected-a", "Detected A", true),
            Agent("detected-b", "Detected B", true),
            Agent("unavailable-u", "Unavailable U", false),
        ];
    }

    private static string? ReadOverride(Type type, string name) =>
        (string?)type.GetProperty(name)!.GetValue(null);

    private AgentInfo Agent(string id, string name, bool detected) =>
        new(id, name, Path.Combine(InstalledRoot, "project", id, "skills"),
            Path.Combine(InstalledRoot, id, "skills"), false, detected);

    public RecordingSkillsService Service(bool hybrid = false) => new(Catalogue, hybrid);

    public void Seed(string[]? targets = null, bool? disabled = null, string pin = "0.23.8")
    {
        SkillsManager.TargetAgents = targets ?? [];
        CoreSettings.SetDictionaryItem(CoreSettings.K.DisabledPackageManagerNotifications, "Other", true);
        if (disabled.HasValue)
            CoreSettings.SetDictionaryItem(CoreSettings.K.DisabledPackageManagerNotifications, "Skills", disabled.Value);
        CoreSettings.SetValue(CoreSettings.K.SkillsNotionCliVersion, pin);
        CoreSettings.SetDictionaryItem(CoreSettings.K.IgnoredPackageUpdates, "other\\keep", "4.5.6");
        CoreSettings.SetDictionaryItem(CoreSettings.K.IgnoredPackageUpdates, "other\\manual", "*");
    }

    public static bool Disabled => CoreSettings.GetDictionaryItem<string, bool>(
        CoreSettings.K.DisabledPackageManagerNotifications, "Skills");

    public static void AssertOtherPreferences()
    {
        Assert.True(CoreSettings.GetDictionaryItem<string, bool>(CoreSettings.K.DisabledPackageManagerNotifications, "Other"));
        Assert.Equal("4.5.6", IgnoredUpdatesDatabase.GetDatabase()["other\\keep"]);
        Assert.Equal("*", IgnoredUpdatesDatabase.GetDatabase()["other\\manual"]);
    }

    public static string[] Selected(AgentSkillsSettingsViewModel vm) =>
        vm.Agents.Where(row => row.IsSelected).Select(row => row.Id).ToArray();

    public static SkillTargetViewModel Row(AgentSkillsSettingsViewModel vm, string id) =>
        Assert.Single(vm.Agents, row => row.Id == id);

    public static NotionStatus Status(NotionCliState state = NotionCliState.NotInstalled,
        string? workspace = null, string? message = null) => new(state, "0.23.9", workspace, message);

    public static TaskCompletionSource<T> Gate<T>() => new();

    public static Task WhenIdle(AgentSkillsSettingsViewModel vm) => When(vm, () => !vm.IsBusy);

    public static Task When(INotifyPropertyChanged subject, Func<bool> condition)
    {
        if (condition()) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (!condition()) return;
            subject.PropertyChanged -= Changed;
            completion.TrySetResult();
        }
        subject.PropertyChanged += Changed;
        if (condition())
        {
            subject.PropertyChanged -= Changed;
            completion.TrySetResult();
        }
        return completion.Task;
    }

    public static PersistenceSnapshot Snapshot() => new();

    internal sealed class PersistenceSnapshot
    {
        private readonly string[] _files =
        [
            CoreSettings.ResolveKey(CoreSettings.K.SkillsNotionCliVersion),
            CoreSettings.ResolveKey(CoreSettings.K.IgnoredPackageUpdates) + ".json",
        ];
        private readonly byte[]?[] _bytes;
        private readonly KeyValuePair<string, string>[] _ignored =
            IgnoredUpdatesDatabase.GetDatabase().OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        private readonly string _pin = CoreSettings.GetValue(CoreSettings.K.SkillsNotionCliVersion);

        public PersistenceSnapshot() => _bytes = _files.Select(ReadBytes).ToArray();
        private static byte[]? ReadBytes(string file)
        {
            string path = Path.Combine(CoreData.UniGetUIUserConfigurationDirectory, file);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        public void AssertUnchanged()
        {
            for (int i = 0; i < _files.Length; i++)
                Assert.Equal(_bytes[i], ReadBytes(_files[i]));
            Assert.Equal(_pin, CoreSettings.GetValue(CoreSettings.K.SkillsNotionCliVersion));
            Assert.Equal(_ignored, IgnoredUpdatesDatabase.GetDatabase().OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
        }
    }

    public void SeedInstalledTree()
    {
        foreach (string directory in Catalogue.Select(agent => agent.GlobalSkillsDirectory!)
                     .Append(Path.Combine(InstalledRoot, ".agents", "skills")))
        {
            Directory.CreateDirectory(Path.Combine(directory, "existing-skill", "empty"));
            File.WriteAllBytes(Path.Combine(directory, "existing-skill", "SKILL.md"),
                Encoding.UTF8.GetBytes("---\nname: existing-skill\n---\nDo not redistribute me.\n"));
            File.WriteAllBytes(Path.Combine(directory, "skills-lock.json"), [0x7B, 0x7D, 0x0A]);
        }
    }

    public string[] InstalledTree() =>
        Directory.EnumerateDirectories(InstalledRoot, "*", SearchOption.AllDirectories)
            .Select(path => "D:" + Path.GetRelativePath(InstalledRoot, path))
            .Concat(Directory.EnumerateFiles(InstalledRoot, "*", SearchOption.AllDirectories)
                .Select(path => "F:" + Path.GetRelativePath(InstalledRoot, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))))
            .Order(StringComparer.Ordinal).ToArray();

    public void Dispose()
    {
        // ResetSettings deletes files: the sandbox MUST remain active until it finishes.
        CoreSettings.ResetSettings();
        _secureCache.Clear();
        foreach (var pair in _previousSecureCache) _secureCache[pair.Key] = pair.Value;
        SecureSettings.TEST_SecureSettingsRootOverride = _previousSecure;
        CoreData.TEST_DataDirectoryOverride = _previousData;
        Directory.Delete(Root, recursive: true);
    }
}

internal sealed class RecordingSkillsService(AgentInfo[] agents, bool hybrid = false) : IAgentSkillsSettingsService
{
    private readonly IAgentSkillsSettingsService? _settings = hybrid ? new AgentSkillsSettingsService(new SkillsManager()) : null;
    private string[] _targets = [];
    private bool _notifications = true;
    public string StoredVersion { get; set; } = "";
    public AgentInfo[] Catalogue { get; set; } = agents;
    public Exception? DiscoveryFailure { get; set; }
    public bool RejectVersion { get; set; }
    public List<string[]> TargetWrites { get; } = [];
    public List<bool> NotificationWrites { get; } = [];
    public List<string> VersionWrites { get; } = [];
    public List<string> Operations { get; } = [];
    public Queue<Task<NotionStatus>> StatusQueue { get; } = new();
    public NotionStatus DefaultStatus { get; set; } = AgentSkillsSettingsTestFixture.Status();
    public bool HasNotionSources { get; set; }
    public int StatusReads { get; private set; }
    public int SignOutCalls { get; private set; }
    public int SourceRefreshes { get; private set; }
    public Func<Task> SignOut { get; set; } = () => Task.CompletedTask;
    public IReadOnlyList<AgentInfo> GetAgents() => DiscoveryFailure is { } error ? throw error : Catalogue;
    public IReadOnlyList<string> TargetAgents
    {
        get => _settings?.TargetAgents ?? _targets.ToArray();
        set
        {
            TargetWrites.Add(value.ToArray());
            if (_settings is not null) _settings.TargetAgents = value;
            else _targets = value.ToArray();
        }
    }
    public bool NotificationsEnabled
    {
        get => _settings?.NotificationsEnabled ?? _notifications;
        set
        {
            NotificationWrites.Add(value);
            if (_settings is not null) _settings.NotificationsEnabled = value;
            else _notifications = value;
        }
    }
    public string SavedVersion => _settings?.SavedVersion ?? StoredVersion;
    public bool SetVersion(string version)
    {
        VersionWrites.Add(version);
        if (RejectVersion) return false;
        if (_settings is not null) return _settings.SetVersion(version);
        StoredVersion = version;
        return true;
    }
    public Task<NotionStatus> GetNotionStatusAsync()
    {
        StatusReads++;
        Operations.Add("status");
        return StatusQueue.TryDequeue(out var task) ? task : Task.FromResult(DefaultStatus);
    }
    public Task SignOutAsync()
    {
        SignOutCalls++;
        Operations.Add("sign-out");
        return SignOut();
    }
    public void RefreshNotionSources()
    {
        SourceRefreshes++;
        Operations.Add("sources");
    }
    public void AssertNoHydrationWrites()
    {
        Assert.Empty(TargetWrites);
        Assert.Empty(NotificationWrites);
        Assert.Empty(VersionWrites);
    }
}

internal sealed class AgentSkillsSettingsTestWindow : IDisposable
{
    private readonly ThemeVariant? _previousTheme = Application.Current!.RequestedThemeVariant;
    private readonly FluentTheme? _addedTheme;
    public Window Window { get; }
    public ScrollViewer Scroll { get; }

    public AgentSkillsSettingsTestWindow(Control control, double width = 900, ThemeVariant? theme = null)
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any())
        {
            _addedTheme = new FluentTheme();
            Application.Current.Styles.Add(_addedTheme);
        }
        Application.Current.RequestedThemeVariant = theme ?? ThemeVariant.Light;
        Scroll = new ScrollViewer { Content = control, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Window = new Window { Width = width, Height = 900, Content = Scroll };
        Window.Show();
        Pump();
    }
    public void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        Window.UpdateLayout();
    }
    public void Space(Control control)
    {
        control.BringIntoView();
        Pump();
        Assert.True(control.Focus());
        Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        Window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        Pump();
    }
    public void Click(Control control)
    {
        control.BringIntoView();
        Pump();
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)!.Value;
        Window.MouseDown(point, MouseButton.Left);
        Window.MouseUp(point, MouseButton.Left);
        Pump();
    }
    public void EnterText(TextBox input, string text)
    {
        input.BringIntoView();
        Pump();
        Assert.True(input.Focus());
        input.SelectAll();
        Window.KeyTextInput(text);
        Pump();
    }
    public void Detach()
    {
        Scroll.Content = null;
        Pump();
    }
    public void Attach(Control control)
    {
        Scroll.Content = control;
        Pump();
    }
    public void Dispose()
    {
        Detach();
        Window.Close();
        Dispatcher.UIThread.RunJobs();
        Application.Current!.RequestedThemeVariant = _previousTheme;
        if (_addedTheme is not null) Application.Current.Styles.Remove(_addedTheme);
    }
}

internal sealed class AgentSkillsSettingsLocalManager : IPackageManager
{
    public AgentSkillsSettingsLocalManager()
    {
        DefaultSource = new ManagerSource(this, "Local test source", new Uri(Path.GetTempPath()));
        Properties = new ManagerProperties { Id = Id, Name = Name, DisplayName = DisplayName, DefaultSource = DefaultSource };
    }
    public ManagerProperties Properties { get; }
    public ManagerCapabilities Capabilities { get; } = new() { RunsInProcess = true, KnowsPackageReleaseDate = PackageReleaseDateSupport.Yes };
    public ManagerStatus Status { get; } = new() { Found = false, ExecutableCallArgs = "" };
    public string Id => "settings-test";
    public string Name => "SettingsTest";
    public string DisplayName => "Settings test manager";
    public IManagerSource DefaultSource { get; }
    public IManagerLogger TaskLogger => throw new NotSupportedException("No manager work is permitted.");
    public IMultiSourceHelper SourcesHelper => throw new NotSupportedException("No source operations are permitted.");
    public IPackageDetailsHelper DetailsHelper => throw new NotSupportedException();
    public IPackageOperationHelper OperationHelper => throw new NotSupportedException();
    public IReadOnlyList<ManagerDependency> Dependencies => [];
    public Encoding OutputEncoding => Encoding.UTF8;
    public bool InstallerUrlFollowsPackageVersion => false;
    public bool CommandLineIsShellInterpreted => false;
    public bool IdentifiersAreQuotedOnCommandLine => true;
    public bool LastUpdatesListingFailed => false;
    public bool LastInstalledListingFailed => false;
    public int? CompareVersions(string versionA, string versionB) => throw new NotSupportedException();
    public void Initialize() => throw new NotSupportedException("No initialization is permitted.");
    public bool IsEnabled() => true;
    public bool IsReady() => false;
    public IReadOnlyList<IPackage> FindPackages(string query) => throw new NotSupportedException();
    public IReadOnlyList<IManagerSource> GetBrowsableSources() => [];
    public IReadOnlyList<IPackage> GetAvailableUpdates() => throw new NotSupportedException();
    public IReadOnlyList<IPackage> GetInstalledPackages() => throw new NotSupportedException();
    public void RefreshPackageIndexes() => throw new NotSupportedException();
    public void AttemptFastRepair() => throw new NotSupportedException();
    public IReadOnlyList<string> FindCandidateExecutableFiles() => [];
    public Tuple<bool, string> GetExecutableFile() => Tuple.Create(false, "");
}
