using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Operations : UserControl, ISettingsPage
{
    private OperationsViewModel VM => (OperationsViewModel)DataContext!;

    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Package operation preferences");

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested;

    public Operations()
    {
        DataContext = new OperationsViewModel();
        InitializeComponent();

        VM.RestartRequired += (s, e) => RestartRequired?.Invoke(s, e);
        VM.NavigationRequested += (s, t) => NavigationRequested?.Invoke(s, t);

        foreach (var v in VM.ParallelOpCounts)
            ParallelOperationCount.AddItem(v, v, false);
        ParallelOperationCount.ShowAddedItems();

        InstallerNameSchemeCard.AddItem(
            "Name given by the publisher",
            InstallerFileNaming.PublisherNameValue
        );
        InstallerNameSchemeCard.AddItem(
            "Package name and version",
            InstallerFileNaming.NameAndVersionValue
        );
        InstallerNameSchemeCard.AddItem(
            "Package identifier and version",
            InstallerFileNaming.IdAndVersionValue
        );
        InstallerNameSchemeCard.AddItem(
            "Name given by the publisher, followed by the version",
            InstallerFileNaming.PublisherNameAndVersionValue
        );
        InstallerNameSchemeCard.ShowAddedItems();

        AskToDeleteNewDesktopShortcuts.Click += async (_, _) =>
        {
            if (Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime { MainWindow: { } win })
                await new ManageDesktopShortcutsWindow().ShowDialog(win);
        };
    }
}
