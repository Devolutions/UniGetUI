using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels.Pages.LogPages;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class OperationFailedDialog : Window
{
    private readonly AbstractOperation _operation;

    public OperationFailedDialog(AbstractOperation operation)
    {
        _operation = operation;
        InitializeComponent();
        UniGetUI.Avalonia.Infrastructure.MicaWindowHelper.Apply(this);
        Title = operation.Metadata.FailureMessage;

        HeaderContent.Text =
            $"{operation.Metadata.FailureMessage}.\n"
            + CoreTools.Translate(
                "Please see the Command-line Output or refer to the Operation History for further information about the issue."
            );

        PopulateOutput();
        // Line brushes are baked per theme; recolor so they follow a live theme switch.
        ActualThemeVariantChanged += (_, _) => PopulateOutput();

        var closeButton = new Button
        {
            Content = CoreTools.Translate("Close"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        closeButton.Click += (_, _) => Close();

        var retryButton = BuildRetryButton(operation);

        ButtonsLayout.Children.Add(retryButton);
        ButtonsLayout.Children.Add(closeButton);
        Grid.SetColumn(retryButton, 0);
        Grid.SetColumn(closeButton, 1);
    }

    private void PopulateOutput()
    {
        // Resolve against the actual theme variant; bare FindResource picks the light-theme
        // foreground (near-black) even in dark mode, making normal lines unreadable (#5032).
        var theme = Infrastructure.ThemeHelper.Variant;
        var errorBrush = new SolidColorBrush(Color.Parse("#FF6B6B"));
        var debugBrush = new SolidColorBrush(Color.Parse("#888888"));
        var normalBrush = LookupBrush("SystemControlForegroundBaseHighBrush", theme, Brushes.White);

        var lines = new List<LogLineItem>();
        foreach (var (text, type) in _operation.GetOutput())
        {
            IBrush brush = type switch
            {
                AbstractOperation.LineType.Error => errorBrush,
                AbstractOperation.LineType.VerboseDetails => debugBrush,
                _ => normalBrush,
            };
            lines.Add(new LogLineItem(text, brush));
        }
        OutputText.SetLines(lines);
    }

    private static IBrush LookupBrush(string key, ThemeVariant theme, IBrush fallback)
    {
        if (Application.Current?.TryGetResource(key, theme, out var resource) == true && resource is IBrush brush)
            return brush;
        return fallback;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(OutputText.ScrollToBottom, DispatcherPriority.Background);
    }

    private Control BuildRetryButton(AbstractOperation operation)
    {
        var retryOptions = new List<MenuItem>();

        if (operation is PackageOperation pkgOp)
        {
            var caps = pkgOp.Package.Manager.Capabilities;

            if (OperatingSystem.IsWindows() && !pkgOp.Options.RunAsAdministrator && caps.CanRunAsAdmin)
                retryOptions.Add(MenuItem(CoreTools.Translate("Retry as administrator"),
                    () => { operation.Retry(AbstractOperation.RetryMode.Retry_AsAdmin); Close(); }));

            if (!pkgOp.Options.InteractiveInstallation && caps.CanRunInteractively)
                retryOptions.Add(MenuItem(CoreTools.Translate("Retry interactively"),
                    () => { operation.Retry(AbstractOperation.RetryMode.Retry_Interactive); Close(); }));

            if (!pkgOp.Options.SkipHashCheck && caps.CanSkipIntegrityChecks)
                retryOptions.Add(MenuItem(CoreTools.Translate("Retry skipping integrity checks"),
                    () => { operation.Retry(AbstractOperation.RetryMode.Retry_SkipIntegrity); Close(); }));
        }
        else if (OperatingSystem.IsWindows() && operation is SourceOperation srcOp && !srcOp.ForceAsAdministrator)
        {
            retryOptions.Add(MenuItem(CoreTools.Translate("Retry as administrator"),
                () => { operation.Retry(AbstractOperation.RetryMode.Retry_AsAdmin); Close(); }));
        }

        if (retryOptions.Count > 0)
        {
            var splitButton = new SplitButton
            {
                Content = CoreTools.Translate("Retry"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            splitButton.Click += (_, _) =>
            {
                operation.Retry(AbstractOperation.RetryMode.Retry);
                Close();
            };
            var flyout = new MenuFlyout();
            foreach (var item in retryOptions)
                flyout.Items.Add(item);
            splitButton.Flyout = flyout;
            return splitButton;
        }
        else
        {
            var button = new Button
            {
                Content = CoreTools.Translate("Retry"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            button.Click += (_, _) =>
            {
                operation.Retry(AbstractOperation.RetryMode.Retry);
                Close();
            };
            return button;
        }
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
