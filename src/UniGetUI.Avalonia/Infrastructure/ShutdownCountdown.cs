using Avalonia.Controls;
using Avalonia.Media;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Infrastructure;

internal sealed class ShutdownCountdown
{
    private readonly bool _restart;
    private readonly int _seconds;
    private readonly TextBlock _message;

    public ShutdownCountdown(bool restart, int seconds)
    {
        _restart = restart;
        _seconds = Math.Max(1, seconds);
        Remaining = _seconds;

        _message = new TextBlock
        {
            Text = Message(),
            TextWrapping = TextWrapping.Wrap,
        };

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(_message);
        body.Children.Add(new TextBlock
        {
            Text = CoreTools.Translate("Save your work and close any open applications first."),
            Opacity = 0.82,
            TextWrapping = TextWrapping.Wrap,
        });

        Dialog = new ImmersiveConfirmationDialog(
            restart
                ? CoreTools.Translate("Restarting your computer")
                : CoreTools.Translate("Shutting down your computer"),
            body,
            restart ? CoreTools.Translate("Restart now") : CoreTools.Translate("Shut down now"),
            CoreTools.Translate("Cancel"))
        {
            MaxWidth = 560,
            FocusPrimaryButton = false,
        };
    }

    public ImmersiveConfirmationDialog Dialog { get; }

    public int Remaining { get; private set; }

    public string MessageText => _message.Text ?? "";

    public void Restart()
    {
        if (Dialog.Result is not null) return;

        Remaining = _seconds;
        _message.Text = Message();
    }

    public bool Tick()
    {
        if (Remaining <= 0 || Dialog.Result is not null) return true;

        Remaining--;
        if (Remaining == 0)
        {
            Dialog.Accept();
            return true;
        }

        _message.Text = Message();
        return false;
    }

    private string Message() => _restart
        ? CoreTools.Translate(
            "All operations have finished. Some packages need a restart to finish, so your computer will restart in {0} seconds.",
            Remaining)
        : CoreTools.Translate(
            "All operations have finished. Your computer will shut down in {0} seconds.",
            Remaining);
}
