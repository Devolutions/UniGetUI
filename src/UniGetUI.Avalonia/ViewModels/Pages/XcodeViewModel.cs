using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.Xcode;

namespace UniGetUI.Avalonia.ViewModels.Pages;

/// <summary>
/// The Xcode page: the Xcode versions on this Mac and the ones Apple offers, installed, updated and removed
/// through xcodes. One instance lives for the whole session, so automatic checks and the sidebar badge keep
/// running while the page is closed.
/// </summary>
public partial class XcodeViewModel : ViewModelBase
{
    /// <summary>What an installation writes is kept to read; this many lines is far more than it ever needs.</summary>
    private const int MaxLogLines = 300;

    private readonly XcodesService _xcodes;
    private readonly DispatcherTimer _checkTimer = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<string> _log = [];
    private readonly ConcurrentQueue<XcodesOutputLine> _pendingOutput = new();
    private CancellationTokenSource? _operationCancellation;
    private IReadOnlyList<XcodeRelease> _releases = [];
    private IReadOnlyList<InstalledXcode> _installedXcodes = [];
    private bool _initialized;
    private bool _stopped;
    private bool _missingNoticeShown;

    public XcodeViewModel() : this(new XcodesService()) { }

    internal XcodeViewModel(XcodesService xcodes)
    {
        _xcodes = xcodes;
        _includePrereleases = XcodePreferences.IncludePrereleases;
        _checkTimer.Tick += async (_, _) => await CheckAutomaticallyAsync();
        ConfigureTimer();
    }

    public InfoBarViewModel Notice { get; } = new();

    // What the page asks in dialogs over the main window. The tests have no window, so they answer for it.
    internal Func<XcodeRelease, InstalledXcode?, Task<bool>> ConfirmInstall { get; set; } = ConfirmInstallInWindowAsync;
    internal Func<InstalledXcodeItemViewModel, Task<(bool Confirmed, bool DeleteCompletely)>> ConfirmUninstall { get; set; }
        = ConfirmUninstallInWindowAsync;
    internal Func<XcodesPrompt, CancellationToken, Task<string?>> Ask { get; set; } = XcodePromptDialog.AskAsync;

    // ─── State ───────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isXcodesMissing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(XcodesVersionText))]
    private string _xcodesVersion = "";

    public string XcodesVersionText => XcodesVersion.Length == 0 ? "" : CoreTools.Translate("xcodes {0}", XcodesVersion);

    /// <summary>Something is being read, installed or removed; one thing at a time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartOperation), nameof(ShowBusyBar))]
    private bool _isBusy;

    /// <summary>Whether a button that installs, selects or uninstalls can be used now.</summary>
    public bool CanStartOperation => !IsBusy;

    /// <summary>The bar under the toolbar; an installation shows its own, with the progress.</summary>
    public bool ShowBusyBar => IsBusy && !IsOperationRunning;

    /// <summary>Whether the lists have been read at least once, so that an empty one means nothing is there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoneInstalled), nameof(IsAvailableEmpty), nameof(AvailableEmptyText))]
    private bool _hasLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastCheckedText))]
    private DateTime? _lastChecked;

    public string LastCheckedText => LastChecked is { } checkedAt
        ? CoreTools.Translate("Checked {0}", checkedAt.ToString("t", System.Globalization.CultureInfo.CurrentCulture))
        : "";

    // ─── Installed and available versions ───────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstalled), nameof(NoneInstalled))]
    private IReadOnlyList<InstalledXcodeItemViewModel> _installed = [];

    public bool HasInstalled => Installed.Count > 0;
    public bool NoneInstalled => HasLoaded && Installed.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvailable), nameof(IsAvailableEmpty), nameof(AvailableEmptyText))]
    private IReadOnlyList<AvailableXcodeItemViewModel> _available = [];

    public bool HasAvailable => Available.Count > 0;
    public bool IsAvailableEmpty => Available.Count == 0;

    public string AvailableEmptyText
    {
        get
        {
            if (!HasLoaded)
                return CoreTools.Translate("Reading the Xcode versions...");
            return SearchText.Trim().Length > 0
                ? CoreTools.Translate("No version matches your search")
                : CoreTools.Translate("There is no other version to install");
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableEmptyText))]
    private string _searchText = "";

    /// <summary>Whether betas and release candidates are listed, and offered as the update.</summary>
    [ObservableProperty]
    private bool _includePrereleases;

    partial void OnSearchTextChanged(string value) => RebuildAvailable();

    partial void OnIncludePrereleasesChanged(bool value)
    {
        XcodePreferences.IncludePrereleases = value;
        RebuildAvailable();
        UpdateAvailableUpdate();
    }

    // ─── The update ──────────────────────────────────────────────────────────

    /// <summary>The release that is newer than every Xcode installed, when there is one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateCount), nameof(UpdateVersion), nameof(UpdateButtonText))]
    private XcodeRelease? _updateRelease;

    public bool HasUpdate => UpdateRelease is not null;

    /// <summary>Zero or one, shown on the sidebar.</summary>
    public int UpdateCount => UpdateRelease is null ? 0 : 1;

    public string UpdateVersion => UpdateRelease?.Version ?? "";
    public string UpdateButtonText => CoreTools.Translate("Update to Xcode {0}", UpdateVersion);

    private void UpdateAvailableUpdate()
        => UpdateRelease = XcodesService.FindUpdate(_releases, _installedXcodes, IncludePrereleases);

    // ─── The running installation ────────────────────────────────────────────

    /// <summary>The panel about an installation is shown: while it runs, and after it failed until it is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOperationFinished))]
    private bool _hasOperation;

    /// <summary>An installation or a selection is running; closing the app would cut it short.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOperationFinished), nameof(ShowBusyBar))]
    private bool _isOperationRunning;

    public bool IsOperationFinished => HasOperation && !IsOperationRunning;

    [ObservableProperty]
    private string _operationTitle = "";

    [ObservableProperty]
    private string _operationStatus = "";

    [ObservableProperty]
    private string _operationStep = "";

    [ObservableProperty]
    private double _operationProgress;

    [ObservableProperty]
    private bool _operationIsIndeterminate = true;

    [ObservableProperty]
    private string _operationLog = "";

    [ObservableProperty]
    private bool _isLogExpanded;

    // ─── Lifetime ────────────────────────────────────────────────────────────

    private void ConfigureTimer()
    {
        int minutes = XcodePreferences.CheckIntervalMinutes;
        _checkTimer.Stop();
        if (minutes <= 0 || _stopped) return;
        _checkTimer.Interval = TimeSpan.FromMinutes(minutes);
        _checkTimer.Start();
    }

    /// <summary>Ends automatic checks and any installation, as when the Xcode extension is turned off.</summary>
    public void Stop()
    {
        _stopped = true;
        _checkTimer.Stop();
        _lifetime.Cancel();
    }

    /// <summary>Picks up the choices made on its page under Settings &gt; Extensions.</summary>
    public void ReloadPreferences()
    {
        IncludePrereleases = XcodePreferences.IncludePrereleases;
        ConfigureTimer();
    }

    /// <summary>
    /// Finds xcodes and reads what is installed, and what Apple offers. With automatic checks on, it first
    /// downloads the list again. Runs once, at startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await RunExclusiveAsync(() => RefreshAsync(XcodePreferences.CheckIntervalMinutes > 0, announce: false));
    }

    private async Task CheckAutomaticallyAsync()
        => await RunExclusiveAsync(() => RefreshAsync(refresh: true, announce: false));

    /// <summary>Runs one thing at a time: reading, installing and removing all share the page.</summary>
    private async Task RunExclusiveAsync(Func<Task> work)
    {
        if (_stopped || IsBusy) return;
        IsBusy = true;
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Error("An Xcode command failed");
            Logger.Error(ex);
            ShowNotice(CoreTools.Translate("Something went wrong"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ─── Reading ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private Task CheckForUpdates() => RunExclusiveAsync(() => RefreshAsync(refresh: true, announce: true));

    /// <summary>Looks for xcodes again, as after it was installed from Discover Packages.</summary>
    [RelayCommand]
    private Task Recheck() => RunExclusiveAsync(() => RefreshAsync(refresh: false, announce: false));

    private async Task RefreshAsync(bool refresh, bool announce)
    {
        if (await DetectXcodesAsync())
            await LoadAsync(refresh, announce);
    }

    /// <summary>Whether xcodes is there to run; when it is not, says so and how to get it.</summary>
    private async Task<bool> DetectXcodesAsync()
    {
        string? path = _xcodes.FindExecutable();
        string? version = path is null ? null : await _xcodes.GetVersionAsync(_lifetime.Token);
        XcodesVersion = version ?? "";
        IsXcodesMissing = version is null;

        if (version is null)
        {
            _missingNoticeShown = true;
            Notice.Title = path is null
                ? CoreTools.Translate("xcodes was not found")
                : CoreTools.Translate("xcodes could not be started");
            Notice.Message = path is null
                ? CoreTools.Translate("This page installs Xcode with xcodes. Install it from Discover Packages, where it is a Homebrew package, then choose Look again.")
                : CoreTools.Translate("xcodes was found at {0} but did not answer. Check that it runs in a terminal, then choose Look again.", path);
            Notice.Severity = InfoBarSeverity.Error;
            Notice.IsClosable = false;
            Notice.ActionButtonText = CoreTools.Translate("Look again");
            Notice.ActionButtonCommand = RecheckCommand;
            Notice.BodyCommand = null;
            Notice.IsOpen = true;
            return false;
        }

        if (_missingNoticeShown)
        {
            _missingNoticeShown = false;
            Notice.IsOpen = false;
            Notice.ActionButtonText = "";
            Notice.ActionButtonCommand = null;
        }
        return true;
    }

    /// <param name="refresh">Download the list of versions again instead of reading the copy xcodes has.</param>
    /// <param name="announce">Say what was found, as after the button was pressed, rather than only updating the page.</param>
    private async Task LoadAsync(bool refresh, bool announce)
    {
        CancellationToken cancellation = _lifetime.Token;
        string? refreshError = null;
        IReadOnlyList<InstalledXcode> installed;
        IReadOnlyList<XcodeRelease> releases;
        try
        {
            installed = await _xcodes.GetInstalledAsync(cancellation);
            try
            {
                releases = await _xcodes.GetReleasesAsync(refresh, cancellation);
            }
            catch (XcodesException ex) when (refresh)
            {
                // Offline, or Apple could not be reached: the list xcodes already had is still worth showing.
                Logger.Warn($"xcodes could not download the list of Xcode versions: {ex.Message}");
                refreshError = ex.Message;
                releases = await _xcodes.GetReleasesAsync(false, cancellation);
            }
        }
        catch (XcodesException ex)
        {
            Logger.Warn($"xcodes could not list the Xcode versions: {ex.Message}");
            if (announce)
                ShowNotice(CoreTools.Translate("Could not read the Xcode versions"), ex.Message, InfoBarSeverity.Error);
            return;
        }

        _installedXcodes = installed;
        _releases = releases;
        Installed = [.. installed.Select(xcode => new InstalledXcodeItemViewModel(xcode))];
        RebuildAvailable();
        UpdateAvailableUpdate();
        HasLoaded = true;
        if (refresh && refreshError is null)
            LastChecked = DateTime.Now;

        if (announce)
            AnnounceResult(refreshError);
    }

    private void AnnounceResult(string? refreshError)
    {
        string newest = _installedXcodes.Count > 0 ? _installedXcodes[0].Version : "";
        if (refreshError is not null)
            ShowNotice(CoreTools.Translate("Could not check for new versions"),
                CoreTools.Translate("The list xcodes already had is shown instead. {0}", refreshError),
                InfoBarSeverity.Warning);
        else if (UpdateRelease is { } update)
            ShowNotice(CoreTools.Translate("Xcode {0} is available", update.Version),
                CoreTools.Translate("The newest Xcode you have is {0}.", newest), InfoBarSeverity.Informational);
        else if (_installedXcodes.Count == 0)
            ShowNotice(CoreTools.Translate("No Xcode is installed yet"),
                CoreTools.Translate("Choose a version from the list below to install it."), InfoBarSeverity.Informational);
        else
            ShowNotice(CoreTools.Translate("Xcode is up to date"),
                CoreTools.Translate("No release newer than Xcode {0} was found.", newest), InfoBarSeverity.Success);
    }

    private void RebuildAvailable()
    {
        string search = SearchText.Trim();
        if (search.StartsWith("xcode", StringComparison.OrdinalIgnoreCase))
            search = search[5..].Trim();

        // xcodes takes a version by its name alone, so a version listed with several builds is one row.
        Available = [.. _releases
            .Where(release => !IsInstalled(release))
            .Where(release => IncludePrereleases || !release.IsPrerelease)
            .Where(release => search.Length == 0 || Matches(release, search))
            .DistinctBy(release => release.Version, StringComparer.OrdinalIgnoreCase)
            .Select(release => new AvailableXcodeItemViewModel(release))];
    }

    private bool IsInstalled(XcodeRelease release)
        => release.IsInstalled
           || _installedXcodes.Any(xcode => string.Equals(xcode.Version, release.Version, StringComparison.OrdinalIgnoreCase));

    private static bool Matches(XcodeRelease release, string search)
        => release.Version.Contains(search, StringComparison.OrdinalIgnoreCase)
           || release.Build?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

    // ─── Installing and updating ─────────────────────────────────────────────

    [RelayCommand]
    private Task Install(AvailableXcodeItemViewModel? item)
        => item is null ? Task.CompletedTask : InstallVersionAsync(item.Release, replacing: null);

    /// <summary>Installs the newest release next to the Xcode that is there; that one is kept until it is removed.</summary>
    [RelayCommand]
    private Task UpdateXcode()
        => UpdateRelease is { } release
            ? InstallVersionAsync(release, replacing: _installedXcodes.FirstOrDefault())
            : Task.CompletedTask;

    private Task InstallVersionAsync(XcodeRelease release, InstalledXcode? replacing) => RunExclusiveAsync(async () =>
    {
        if (!await ConfirmInstall(release, replacing))
            return;

        XcodeInstallOptions options = XcodePreferences.InstallOptions;
        bool installed = await RunOperationAsync(
            CoreTools.Translate("Installing Xcode {0}", release.Version),
            CoreTools.Translate("Could not install Xcode {0}", release.Version),
            CoreTools.Translate("The installation of Xcode {0} was cancelled", release.Version),
            (prompts, output, cancellation) => _xcodes.InstallAsync(release.Version, options, prompts, output, cancellation));

        // Whatever happened, what is on the disk now is what the page should show.
        await LoadAsync(refresh: false, announce: false);
        if (installed)
            AnnounceInstalled(release, replacing);
    });

    private void AnnounceInstalled(XcodeRelease release, InstalledXcode? replaced)
    {
        InstalledXcodeItemViewModel? installed = Installed.FirstOrDefault(
            xcode => string.Equals(xcode.Version, release.Version, StringComparison.OrdinalIgnoreCase));
        InstalledXcodeItemViewModel? previous = replaced is null
            ? null
            : Installed.FirstOrDefault(xcode => string.Equals(xcode.Version, replaced.Version, StringComparison.OrdinalIgnoreCase)
                                                && !ReferenceEquals(xcode, installed));

        ShowNotice(CoreTools.Translate("Xcode {0} was installed", release.Version),
            previous is not null
                ? CoreTools.Translate("Xcode {0} is still installed.", previous.Version)
                : installed?.Location ?? "",
            InfoBarSeverity.Success);

        if (previous is not null)
        {
            Notice.ActionButtonText = CoreTools.Translate("Uninstall Xcode {0}", previous.Version);
            Notice.ActionButtonCommand = new AsyncRelayCommand(() => UninstallCommand.ExecuteAsync(previous));
        }
    }

    private static async Task<bool> ConfirmInstallInWindowAsync(XcodeRelease release, InstalledXcode? replacing)
        => MainWindow.Instance is { } owner && await ConfirmInstallAsync(release, replacing, owner);

    private static async Task<bool> ConfirmInstallAsync(XcodeRelease release, InstalledXcode? replacing, MainWindow owner)
    {
        var body = new StackPanel { Spacing = 12, MaxWidth = 520 };
        body.Children.Add(Paragraph(CoreTools.Translate(
            "Xcode is downloaded from Apple and unpacked next to the versions you already have. It is several gigabytes, so this can take a while.")));
        body.Children.Add(Paragraph(CoreTools.Translate(
            "You may be asked for your Apple ID, a verification code and your macOS password. UniGetUI hands them to xcodes and never keeps them.")));
        if (replacing is not null)
            body.Children.Add(Paragraph(CoreTools.Translate(
                "Xcode {0} is kept, so nothing is lost if the new version does not suit you. You can uninstall it afterwards.",
                replacing.Version)));
        if (XcodePreferences.SelectAfterInstall)
            body.Children.Add(Paragraph(CoreTools.Translate("It will become the Xcode that command line tools use.")));
        if (release.IsPrerelease)
            body.Children.Add(Warning(CoreTools.Translate("This is a pre-release version of Xcode, meant for testing.")));

        var dialog = new ImmersiveConfirmationDialog(
            replacing is null
                ? CoreTools.Translate("Install Xcode {0}?", release.Version)
                : CoreTools.Translate("Update to Xcode {0}?", release.Version),
            body,
            replacing is null ? CoreTools.Translate("Install") : CoreTools.Translate("Update"),
            CoreTools.Translate("Cancel"));
        await dialog.ShowDialog(owner);
        return dialog.Result is true;
    }

    // ─── Selecting ───────────────────────────────────────────────────────────

    /// <summary>Makes an installed Xcode the one command line tools use.</summary>
    [RelayCommand]
    private Task Select(InstalledXcodeItemViewModel? item)
    {
        if (item is not { CanSelect: true })
            return Task.CompletedTask;

        return RunExclusiveAsync(async () =>
        {
            bool selected = await RunOperationAsync(
                CoreTools.Translate("Selecting Xcode {0}", item.Version),
                CoreTools.Translate("Could not select Xcode {0}", item.Version),
                CoreTools.Translate("Selecting Xcode {0} was cancelled", item.Version),
                (prompts, output, cancellation) => _xcodes.SelectAsync(item.Xcode, prompts, output, cancellation));

            await LoadAsync(refresh: false, announce: false);
            if (selected)
                ShowNotice(CoreTools.Translate("Xcode {0} is now the selected Xcode", item.Version),
                    CoreTools.Translate("Command line tools such as xcodebuild use it."), InfoBarSeverity.Success);
        });
    }

    // ─── Running xcodes ──────────────────────────────────────────────────────

    /// <summary>
    /// Runs one of the commands that may ask a question, showing what it writes. Returns whether it finished; a
    /// failure is reported, and kept on screen with what xcodes wrote, while a success needs no panel.
    /// </summary>
    private async Task<bool> RunOperationAsync(
        string title,
        string failureTitle,
        string cancelledTitle,
        Func<XcodesPromptHandler, Action<XcodesOutputLine>, CancellationToken, Task> run)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationCancellation = cancellation;
        StartOperation(title);
        try
        {
            try
            {
                await run(AskAsync, QueueOutput, cancellation.Token);
            }
            finally
            {
                // What xcodes wrote last is shown before the outcome is reported.
                ShowPendingOutput();
            }
            HasOperation = false;
            return true;
        }
        catch (OperationCanceledException)
        {
            HasOperation = false;
            ShowNotice(cancelledTitle, "", InfoBarSeverity.Informational);
            return false;
        }
        catch (XcodesException ex)
        {
            Logger.Warn($"{title} failed: {ex.Message}");
            FailOperation(failureTitle, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"{title} failed");
            Logger.Error(ex);
            FailOperation(failureTitle, ex.Message);
            return false;
        }
        finally
        {
            _operationCancellation = null;
            IsOperationRunning = false;
        }
    }

    private void StartOperation(string title)
    {
        _pendingOutput.Clear();
        _log.Clear();
        OperationLog = "";
        OperationTitle = title;
        OperationStatus = CoreTools.Translate("Starting xcodes...");
        OperationStep = "";
        OperationProgress = 0;
        OperationIsIndeterminate = true;
        IsLogExpanded = false;
        Notice.IsOpen = false;
        HasOperation = true;
        IsOperationRunning = true;
    }

    private void FailOperation(string title, string reason)
    {
        OperationTitle = title;
        OperationStatus = "";
        OperationStep = "";
        IsLogExpanded = true;
        ShowNotice(title, reason, InfoBarSeverity.Error);
    }

    /// <summary>Called from the thread xcodes is read on, so what it shows is posted to the interface thread.</summary>
    private async Task<string?> AskAsync(XcodesPrompt prompt, CancellationToken cancellation)
    {
        // This job can run after the operation ended, when its panel already shows how it went.
        Dispatcher.UIThread.Post(() =>
        {
            if (IsOperationRunning)
                OperationStatus = CoreTools.Translate("Waiting for your answer...");
        });
        return await Ask(prompt, cancellation);
    }

    /// <summary>Called from the thread xcodes is read on; the lines are shown on the interface thread, in order.</summary>
    private void QueueOutput(XcodesOutputLine line)
    {
        _pendingOutput.Enqueue(line);
        Dispatcher.UIThread.Post(ShowPendingOutput);
    }

    /// <summary>
    /// Shows what xcodes has written so far. The end of an operation does this itself instead of waiting for the
    /// posted jobs, which can run after the continuation that follows them and would then find it over.
    /// </summary>
    private void ShowPendingOutput()
    {
        while (_pendingOutput.TryDequeue(out XcodesOutputLine? line))
            AppendOutput(line);
    }

    private void AppendOutput(XcodesOutputLine line)
    {
        string text = line.Text.TrimEnd();
        if (!IsOperationRunning || text.Length == 0)
            return;

        if (XcodesOutputParser.TryParseProgress(text, out XcodeProgress progress))
        {
            OperationStep = CoreTools.Translate("Step {0} of {1}", progress.Step, progress.StepCount);
            OperationStatus = progress.Text;
            OperationIsIndeterminate = progress.Fraction is null;
            if (progress.Fraction is { } fraction)
                OperationProgress = fraction * 100;
        }

        // A line that only reports progress replaces the one before it, as it would in a terminal.
        if (line.ReplacesPrevious && _log.Count > 0)
            _log[^1] = text;
        else
            _log.Add(text);
        if (_log.Count > MaxLogLines)
            _log.RemoveRange(0, _log.Count - MaxLogLines);
        OperationLog = string.Join(Environment.NewLine, _log);
    }

    /// <summary>Stops the installation or selection that is running; what was downloaded is kept for next time.</summary>
    [RelayCommand]
    public void CancelOperation() => _operationCancellation?.Cancel();

    [RelayCommand]
    private void DismissOperation()
    {
        if (!IsOperationRunning)
            HasOperation = false;
    }

    /// <summary>
    /// Asks, while xcodes is running, whether to stop it so UniGetUI can quit. Yes stops it and waits for it to be
    /// gone, so nothing is left running behind a closed window.
    /// </summary>
    public async Task<bool> ConfirmQuitAsync()
    {
        if (!IsOperationRunning)
            return true;
        if (MainWindow.Instance is not { } owner)
            return false;

        // Quitting can ask while the window is hidden (macOS, tray), and a dialog in a hidden window never completes.
        if (!owner.IsVisible || owner.WindowState == WindowState.Minimized)
            owner.ShowFromTray();

        var dialog = new ImmersiveConfirmationDialog(
            OperationTitle,
            Paragraph(CoreTools.Translate(
                "Quitting UniGetUI stops this. If it was downloading, what was downloaded is kept, so it can usually carry on from where it stopped.")),
            CoreTools.Translate("Stop and quit"),
            CoreTools.Translate("Keep going"));
        await dialog.ShowDialog(owner);
        if (dialog.Result is not true)
            return false;

        CancelOperation();
        for (int waited = 0; IsOperationRunning && waited < 100; waited++)
            await Task.Delay(100);
        return true;
    }

    // ─── Uninstalling ────────────────────────────────────────────────────────

    [RelayCommand]
    private Task Uninstall(InstalledXcodeItemViewModel? item)
    {
        if (item is null)
            return Task.CompletedTask;

        return RunExclusiveAsync(async () =>
        {
            (bool confirmed, bool deleteCompletely) = await ConfirmUninstall(item);
            if (!confirmed)
                return;

            item.IsBusy = true;
            bool removed = false;
            try
            {
                await _xcodes.UninstallAsync(item.Version, deleteCompletely, _lifetime.Token);
                removed = true;
            }
            catch (XcodesException ex)
            {
                Logger.Warn($"Xcode {item.Version} could not be uninstalled: {ex.Message}");
                ShowNotice(CoreTools.Translate("Could not uninstall Xcode {0}", item.Version), ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                item.IsBusy = false;
            }

            await LoadAsync(refresh: false, announce: false);
            if (removed)
                ShowNotice(CoreTools.Translate("Xcode {0} was uninstalled", item.Version),
                    deleteCompletely ? "" : CoreTools.Translate("It is in the Trash."), InfoBarSeverity.Success);
        });
    }

    private static async Task<(bool Confirmed, bool DeleteCompletely)> ConfirmUninstallInWindowAsync(
        InstalledXcodeItemViewModel item)
        => MainWindow.Instance is { } owner ? await ConfirmUninstallAsync(item, owner) : (false, false);

    private static async Task<(bool Confirmed, bool DeleteCompletely)> ConfirmUninstallAsync(
        InstalledXcodeItemViewModel item, MainWindow owner)
    {
        var deleteCompletely = new CheckBox
        {
            Content = CoreTools.Translate("Delete it now instead of moving it to the Trash"),
            IsChecked = false,
        };
        var body = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 520,
            Children =
            {
                Paragraph(CoreTools.Translate("Xcode {0} will be removed from {1}.", item.Version,
                    System.IO.Path.GetDirectoryName(item.Location) ?? item.Location)),
            },
        };
        if (item.IsSelected)
            body.Children.Add(Warning(CoreTools.Translate(
                "It is the Xcode that command line tools use. Select another one afterwards, or they will stop working.")));
        body.Children.Add(deleteCompletely);

        var dialog = new ImmersiveConfirmationDialog(
            CoreTools.Translate("Uninstall Xcode {0}?", item.Version), body,
            CoreTools.Translate("Uninstall"), CoreTools.Translate("Cancel"))
        {
            FocusPrimaryButton = false,
        };
        await dialog.ShowDialog(owner);
        return (dialog.Result is true, deleteCompletely.IsChecked == true);
    }

    // ─── Opening ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private static void Open(InstalledXcodeItemViewModel? item)
    {
        if (item is not null && Directory.Exists(item.Location))
            CoreTools.Launch(item.Location);
    }

    [RelayCommand]
    private static void RevealInFinder(InstalledXcodeItemViewModel? item)
    {
        if (item is null || !Directory.Exists(item.Location))
            return;

        try
        {
            var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            start.ArgumentList.Add("-R");
            start.ArgumentList.Add(item.Location);
            using Process? process = Process.Start(start);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not show {item.Location} in Finder: {ex.Message}");
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Warning(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = FontWeight.SemiBold,
    };

    private void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        if (IsXcodesMissing) return;
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.ActionButtonText = "";
        Notice.ActionButtonCommand = null;
        Notice.BodyCommand = null;
        Notice.IsClosable = true;
        Notice.IsOpen = true;
    }
}
