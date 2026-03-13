using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Views.Pages;

/// <summary>
/// Dialog shown when an operation finishes with <see cref="OperationStatus.Failed"/> or
/// <see cref="OperationStatus.Canceled"/>.  Replays color-coded output and offers
/// basic retry options derived from the operation's manager capabilities.
/// </summary>
public partial class OperationFailedWindow : Window
{
    private readonly AbstractOperation _operation;

    private static readonly FontFamily MonoFont =
        new("Cascadia Mono,Cascadia Code,Consolas,Courier New,monospace");

    public OperationFailedWindow()
    {
        _operation = null!;
        InitializeComponent();
        IsEnabled = false;
    }

    public OperationFailedWindow(AbstractOperation operation)
    {
        _operation = operation;
        Title = CoreTools.Translate("{0} failed", operation.Metadata.Title);

        InitializeComponent();

        var failureMsg = operation.Metadata.FailureMessage.Length > 0
            ? operation.Metadata.FailureMessage
            : CoreTools.Translate("Failed");

        FailureHeaderBlock.Text = failureMsg + ".\n"
            + CoreTools.Translate(
                "Please see the Command-line Output or refer to the Operation History for further information about the issue.");

        StatusHintBlock.Text = CoreTools.Translate("Show the live output");

        foreach (var (text, type) in operation.GetOutput())
            AddLine(text, type);

        BuildRetryFlyout();

        CloseBtn.Content = CoreTools.Translate("Close");
        RetryBtn.Content = CoreTools.Translate("Retry");
    }

    // ── Log rendering ────────────────────────────────────────────────────────

    private void AddLine(string text, AbstractOperation.LineType type)
    {
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = MonoFont,
            FontSize = 12,
        };

        switch (type)
        {
            case AbstractOperation.LineType.Error:
                tb.Foreground = new SolidColorBrush(Color.FromRgb(220, 60, 60));
                break;
            case AbstractOperation.LineType.VerboseDetails:
                tb.Opacity = 0.52;
                break;
        }

        LogLinesPanel.Children.Add(tb);
    }

    // ── Retry flyout ─────────────────────────────────────────────────────────

    private void BuildRetryFlyout()
    {
        var flyout = new MenuFlyout();

        // Plain retry is always the first option
        var plainRetry = new MenuItem { Header = CoreTools.Translate("Retry") };
        plainRetry.Click += (_, _) => { _operation.Retry(AbstractOperation.RetryMode.Retry); Close(); };
        flyout.Items.Add(plainRetry);

        // --- Basic retry-mode options ---
        if (_operation is PackageOperation pkgOp)
        {
            var caps = pkgOp.Package.Manager.Capabilities;

            if (!pkgOp.Options.RunAsAdministrator && caps.CanRunAsAdmin)
                AddRetryItem(flyout, CoreTools.Translate("Retry as administrator"), AbstractOperation.RetryMode.Retry_AsAdmin);

            if (!pkgOp.Options.InteractiveInstallation && caps.CanRunInteractively)
                AddRetryItem(flyout, CoreTools.Translate("Retry interactively"), AbstractOperation.RetryMode.Retry_Interactive);

            if (!pkgOp.Options.SkipHashCheck && caps.CanSkipIntegrityChecks)
                AddRetryItem(flyout, CoreTools.Translate("Retry skipping integrity checks"), AbstractOperation.RetryMode.Retry_SkipIntegrity);
        }
        else if (_operation is SourceOperation srcOp && !srcOp.ForceAsAdministrator)
        {
            AddRetryItem(flyout, CoreTools.Translate("Retry as administrator"), AbstractOperation.RetryMode.Retry_AsAdmin);
        }

        // --- Update-failure-specific actions ---
        if (_operation is UpdatePackageOperation updateOp)
        {
            flyout.Items.Add(new Separator());

            var reinstallItem = new MenuItem { Header = CoreTools.Translate("Reinstall package") };
            reinstallItem.Click += (_, _) =>
            {
                var opts = updateOp.Options.Copy();
                var op = new InstallPackageOperation(updateOp.Package, opts);
                AvaloniaOperationRegistry.Add(op);
                _ = op.MainThread();
                Close();
            };
            flyout.Items.Add(reinstallItem);

            var uninstallReinstallItem = new MenuItem
            {
                Header = CoreTools.Translate("Uninstall package, then reinstall it")
            };
            uninstallReinstallItem.Click += (_, _) =>
            {
                var opts = updateOp.Options.Copy();
                var reinstallOp = new InstallPackageOperation(updateOp.Package, opts);
                var uninstallOp = new UninstallPackageOperation(updateOp.Package, new InstallOptions(), req: reinstallOp);
                AvaloniaOperationRegistry.Add(reinstallOp);
                AvaloniaOperationRegistry.Add(uninstallOp);
                _ = uninstallOp.MainThread();
                Close();
            };
            flyout.Items.Add(uninstallReinstallItem);

            flyout.Items.Add(new Separator());

            var skipVersionItem = new MenuItem { Header = CoreTools.Translate("Skip this version") };
            skipVersionItem.Click += async (_, _) =>
            {
                await updateOp.Package.AddToIgnoredUpdatesAsync(updateOp.Package.NewVersionString);
                Close();
            };
            flyout.Items.Add(skipVersionItem);

            var ignoreItem = new MenuItem { Header = CoreTools.Translate("Ignore updates for this package") };
            ignoreItem.Click += async (_, _) =>
            {
                await updateOp.Package.AddToIgnoredUpdatesAsync();
                Close();
            };
            flyout.Items.Add(ignoreItem);
        }

        // Only attach the flyout (showing "Retry ▾") when there are extra options beyond plain retry
        if (flyout.Items.Count > 1)
        {
            RetryBtn.Flyout = flyout;
            RetryBtn.Content = CoreTools.Translate("Retry failed operations");
        }
    }

    private void AddRetryItem(MenuFlyout flyout, string label, string mode)
    {
        if (flyout.Items.Count == 1) // first extra option — add separator after "Retry"
            flyout.Items.Add(new Separator());

        var item = new MenuItem { Header = label };
        item.Click += (_, _) => { _operation.Retry(mode); Close(); };
        flyout.Items.Add(item);
    }

    // ── Button handlers ──────────────────────────────────────────────────────

    private void CloseBtn_OnClick(object? sender, RoutedEventArgs e) => Close();

    private void RetryBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        // Only invoked when there is no flyout (no extra options).
        _operation.Retry(AbstractOperation.RetryMode.Retry);
        Close();
    }
}
