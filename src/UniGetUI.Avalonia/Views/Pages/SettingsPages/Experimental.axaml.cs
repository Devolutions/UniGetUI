using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Experimental : UserControl, ISettingsPage
{
    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Experimental settings and developer options");

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }

    public Experimental()
    {
        DataContext = new ExperimentalViewModel();
        InitializeComponent();

        var vm = (ExperimentalViewModel)DataContext;
        vm.RestartRequired += (s, e) => RestartRequired?.Invoke(s, e);

        ShowVersionNumberOnTitlebar.Text = CoreTools.Translate("Show UniGetUI's version and build number on the titlebar.");
        IconDatabaseURLCard.HelpUrl = new Uri("https://www.marticliment.com/unigetui/help/icons-and-screenshots#custom-source");
    }
}
