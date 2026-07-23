using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Avalonia.Views;

public partial class ManageIgnoredUpdatesWindow : Window
{
    public ManageIgnoredUpdatesWindow(Window? owner = null)
    {
        var vm = new ManageIgnoredUpdatesViewModel();
        DataContext = vm;
        InitializeComponent();
        UniGetUI.Avalonia.Infrastructure.MicaWindowHelper.Apply(this);

        // Set the size before the window opens so CenterOwner positions it correctly.
        ApplyInitialSize(owner);

        vm.CloseRequested += (_, _) => Close();
    }

    // Restore the user's last size, falling back to the XAML default, and never open larger
    // than the owner window (recovers the adaptive sizing of the pre-Avalonia overlay).
    private void ApplyInitialSize(Window? owner)
    {
        double width = Width;
        double height = Height;

        string saved = Settings.GetValue(Settings.K.IgnoredUpdatesWindowSize);
        if (!string.IsNullOrEmpty(saved))
        {
            string[] parts = saved.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
            {
                width = w;
                height = h;
            }
        }

        if (owner is not null)
        {
            width = Math.Min(width, owner.Width - 40);
            height = Math.Min(height, owner.Height - 40);
        }

        Width = Math.Max(MinWidth, width);
        Height = Math.Max(MinHeight, height);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Persist only the normal (non-maximized) size so the dialog reopens as the user left it.
        if (WindowState == WindowState.Normal)
        {
            try { Settings.SetValue(Settings.K.IgnoredUpdatesWindowSize, $"{(int)Width},{(int)Height}"); }
            catch (Exception ex) { Logger.Error(ex); }
        }
        base.OnClosing(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() =>
        {
            if (((ManageIgnoredUpdatesViewModel)DataContext!).HasEntries)
                IgnoredUpdatesGrid.Focus();
            else
                ResetButton.Focus();
        }, DispatcherPriority.Background);
    }

    private void ResetYes_Click(object? sender, RoutedEventArgs e)
    {
        ((ManageIgnoredUpdatesViewModel)DataContext!).ResetAllCommand.Execute(null);
        ResetButton.Flyout?.Hide();
    }

    private void ResetNo_Click(object? sender, RoutedEventArgs e) =>
        ResetButton.Flyout?.Hide();
}
