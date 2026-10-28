using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class AgentSkillsPage : UserControl
{
    public AgentSkillsPage(AgentSkillsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
