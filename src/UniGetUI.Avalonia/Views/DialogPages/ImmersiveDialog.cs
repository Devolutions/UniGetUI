using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia.Views.DialogPages;

/// <summary>
/// Base class for modal content hosted inside <see cref="MainWindow"/>.
/// It exposes dialog-oriented show and close operations while the host owns presentation,
/// focus, animation, and stacking.
/// </summary>
public class ImmersiveDialog : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ImmersiveDialog, string?>(nameof(Title));
    public static readonly StyledProperty<bool> IsCloseButtonVisibleProperty =
        AvaloniaProperty.Register<ImmersiveDialog, bool>(nameof(IsCloseButtonVisible), true);

    internal event EventHandler? CloseRequested;
    public event EventHandler<CancelEventArgs>? Closing;

    public Thickness TitleMargin { get; set; } = new(20, 0, 0, 0);

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool IsCloseButtonVisible
    {
        get => GetValue(IsCloseButtonVisibleProperty);
        set => SetValue(IsCloseButtonVisibleProperty, value);
    }

    public Task ShowDialog(Window owner)
    {
        MainWindow host = owner as MainWindow
            ?? throw new ArgumentException(
                "An immersive dialog must be owned by the main window.",
                nameof(owner));
        return host.ShowImmersiveDialogAsync(this);
    }

    public void Show(Window owner) => _ = ShowAndLogFailureAsync(owner);

    private async Task ShowAndLogFailureAsync(Window owner)
    {
        try
        {
            await ShowDialog(owner);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    public void Close()
    {
        var args = new CancelEventArgs();
        Closing?.Invoke(this, args);
        if (!args.Cancel)
            CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void NotifyOpened() => OnOpened(EventArgs.Empty);
    internal void NotifyClosed() => OnClosed(EventArgs.Empty);

    protected virtual void OnOpened(EventArgs e) { }
    protected virtual void OnClosed(EventArgs e) { }
}
