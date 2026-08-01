using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;

namespace UniGetUI.Avalonia.Views.DialogPages;

/// <summary>
/// Shared WinUI-style immersive confirmation surface. Dialog-specific classes supply only
/// their body and labels; title spacing, footer hierarchy, actions, focus, and result handling
/// remain consistent across the application.
/// </summary>
public partial class ImmersiveConfirmationDialog : ImmersiveDialog
{
    public bool? Result { get; private set; }
    public bool RequireChoice { get; set; }
    public bool FocusPrimaryButton { get; set; } = true;

    public ImmersiveConfirmationDialog()
    {
        InitializeComponent();
        PrimaryButton.Click += (_, _) => Complete(true);
        SecondaryButton.Click += (_, _) => Complete(false);
        Closing += (_, e) =>
        {
            if (RequireChoice && Result is null)
                e.Cancel = true;
        };
    }

    public ImmersiveConfirmationDialog(
        string title,
        object body,
        string primaryText,
        string secondaryText) : this()
    {
        Configure(title, body, primaryText, secondaryText);
    }

    protected void Configure(
        string title,
        object body,
        string primaryText,
        string secondaryText)
    {
        Title = title;
        BodyPresenter.Content = body;
        PrimaryButton.Content = primaryText;
        SecondaryButton.Content = secondaryText;
        AutomationProperties.SetName(PrimaryButton, primaryText);
        AutomationProperties.SetName(SecondaryButton, secondaryText);
    }

    protected override void OnOpened(EventArgs e)
    {
        // A dialog instance may be shown again. Never let a previous choice leak into a
        // later presentation (especially when RequireChoice governs close behavior).
        Result = null;
        base.OnOpened(e);
        Dispatcher.UIThread.Post(
            () => (FocusPrimaryButton ? PrimaryButton : SecondaryButton).Focus(),
            DispatcherPriority.Background);
    }

    private void Complete(bool result)
    {
        Result = result;
        Close();
    }
}
