using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.Views;

public partial class ManageShortcutsWindow : UniGetUI.Avalonia.Views.DialogPages.ImmersiveDialog
{
    public ManageShortcutsWindow(
        System.Collections.Generic.IReadOnlyList<string>? shortcuts = null,
        ShortcutDialogScope scope = ShortcutDialogScope.All
    )
    {
        var vm = new ManageShortcutsViewModel(shortcuts, scope);
        DataContext = vm;
        InitializeComponent();
        vm.CloseRequested += (_, _) => Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (DataContext is ManageShortcutsViewModel
                    {
                        SelectedTabIndex: ManageShortcutsViewModel.DesktopTabIndex
                    })
                    ShortcutsGrid.Focus();
            },
            DispatcherPriority.Background
        );
    }

    private void FolderOption_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: StartMenuFolderRuleViewModel rule } box)
            return;

        if (!rule.IsCreatingFolder || box.Parent is not Grid row)
            return;

        var field = row.Children.OfType<TextBox>()
            .FirstOrDefault(child => child.Classes.Contains("newFolderName"));

        if (field is null)
            return;

        // The field is only made visible by the binding this event precedes.
        Dispatcher.UIThread.Post(
            () =>
            {
                field.Focus();
                field.SelectAll();
            },
            DispatcherPriority.Background
        );
    }

    private void ResetYes_Click(object? sender, RoutedEventArgs e)
    {
        ((ManageShortcutsViewModel)DataContext!).ResetAllCommand.Execute(null);
        ResetButton.Flyout?.Hide();
    }

    private void ResetNo_Click(object? sender, RoutedEventArgs e) =>
        ResetButton.Flyout?.Hide();
}
