using Avalonia;
using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Controls;

namespace UniGetUI.Avalonia.Views.Controls;

public partial class UserAvatarControl : UserControl
{
    private readonly UserAvatarViewModel _viewModel;

    public UserAvatarControl()
    {
        _viewModel = new UserAvatarViewModel();
        DataContext = _viewModel;
        InitializeComponent();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // This control belongs to a SidebarView data template, which Avalonia can recreate as
        // the navigation layout changes. Its visual lifetime—not page navigation—is the owner.
        _viewModel.Dispose();
        base.OnDetachedFromVisualTree(e);
    }
}
