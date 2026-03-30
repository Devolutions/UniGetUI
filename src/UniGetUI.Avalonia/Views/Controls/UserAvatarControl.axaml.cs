using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Controls;

namespace UniGetUI.Avalonia.Views.Controls;

public partial class UserAvatarControl : UserControl
{
    public UserAvatarControl()
    {
        DataContext = new UserAvatarViewModel();
        InitializeComponent();
    }
}
