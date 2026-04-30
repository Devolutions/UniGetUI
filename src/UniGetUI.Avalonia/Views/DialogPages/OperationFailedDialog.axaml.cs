using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class OperationFailedDialog : Window
{
    public OperationFailedDialog(AbstractOperation operation)
    {
        InitializeComponent();
        Title = operation.Metadata.FailureMessage;

        HeaderContent.Text =
            $"{operation.Metadata.FailureMessage}.\n"
            + CoreTools.Translate(
                "Please see the Command-line Output or refer to the Operation History for further information about the issue."
            );

        var errorBrush = new SolidColorBrush(Color.Parse("#FF6B6B"));
        var debugBrush = new SolidColorBrush(Color.Parse("#888888"));
        var normalBrush = Application.Current?.FindResource("SystemControlForegroundBaseHighBrush") as IBrush
                          ?? Brushes.White;

        var lines = new List<OutputLineVm>();
        foreach (var (text, type) in operation.GetOutput())
        {
            IBrush brush = type switch
            {
                AbstractOperation.LineType.Error => errorBrush,
                AbstractOperation.LineType.VerboseDetails => debugBrush,
                _ => normalBrush,
            };
            lines.Add(new OutputLineVm(text, brush));
        }
        OutputLines.ItemsSource = lines;

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

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(OutputScroll.ScrollToEnd, DispatcherPriority.Background);
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

/// <summary>View model for a single colored output line in OperationFailedDialog.</summary>
public sealed class OutputLineVm(string text, IBrush foreground)
{
    public string Text { get; } = text;
    public IBrush Foreground { get; } = foreground;
}
