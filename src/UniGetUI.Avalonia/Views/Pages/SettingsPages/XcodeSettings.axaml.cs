using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class XcodeSettings : UserControl, ISettingsPage
{
    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Xcode");

    public event EventHandler? RestartRequired { add { } remove { } }
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }

    public XcodeSettings()
    {
        DataContext = new XcodeSettingsViewModel();
        InitializeComponent();

        foreach ((string label, string value) in XcodeSettingsViewModel.CheckIntervalItems)
            XcodeCheckIntervalCard.AddItem(label, value);
        XcodeCheckIntervalCard.ShowAddedItems();
    }
}
