using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views;

// macOS menu bar. Apple expects every toolbar action to also be a menu command with its ⌘ shortcut;
// items reuse the sidebar commands and the same page actions as the keyboard shortcuts.
public partial class MainWindow
{
    private void SetupMacMenuBar()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var sidebar = ViewModel.Sidebar;
        ICommand navigate = sidebar.RequestNavigationCommand;

        // Application menu: insert ahead of the Services, Hide and Quit items (and their leading separator).
        if (Application.Current is { } app && NativeMenu.GetMenu(app) is { } appMenu)
        {
            NativeMenuItemBase[] appItems =
            [
                Item(CoreTools.Translate("About UniGetUI"), navigate, "About"),
                new NativeMenuItemSeparator(),
                Item(CoreTools.Translate("Settings") + "…", navigate, "Settings", Key.OemComma),
                Item(CoreTools.Translate("Check for UniGetUI updates") + "…", sidebar.CheckForUpdatesCommand),
            ];
            for (int i = 0; i < appItems.Length; i++)
                appMenu.Items.Insert(i, appItems[i]);
        }

        var edit = Submenu(CoreTools.Translate("Edit"),
            Action(CoreTools.Translate("Undo"), () => FocusedTextBox()?.Undo(), Key.Z),
            Action(CoreTools.Translate("Redo"), () => FocusedTextBox()?.Redo(), Key.Z, KeyModifiers.Shift),
            new NativeMenuItemSeparator(),
            Action(CoreTools.Translate("Cut"), () => FocusedTextBox()?.Cut(), Key.X),
            Action(CoreTools.Translate("Copy"), () => FocusedTextBox()?.Copy(), Key.C),
            Action(CoreTools.Translate("Paste"), () => FocusedTextBox()?.Paste(), Key.V),
            Action(CoreTools.Translate("Select all"), SelectAllForFocus, Key.A),
            new NativeMenuItemSeparator(),
            Action(CoreTools.Translate("Find"), TriggerPageSearch, Key.F));

        var view = Submenu(CoreTools.Translate("View"),
            Item(CoreTools.Translate("Discover Packages"), navigate, "Discover", Key.D1),
            Item(CoreTools.Translate("Software Updates"), navigate, "Updates", Key.D2),
            Item(CoreTools.Translate("Installed Packages"), navigate, "Installed", Key.D3),
            Item(CoreTools.Translate("Package Bundles"), navigate, "Bundles", Key.D4),
            Item(CoreTools.Translate("Package Managers"), navigate, "Managers", Key.D6),
            new NativeMenuItemSeparator(),
            Item(CoreTools.Translate("Toggle navigation panel"), ViewModel.ToggleSidebarCommand, null, Key.S, KeyModifiers.Control),
            new NativeMenuItemSeparator(),
            Action(CoreTools.Translate("Reload"), TriggerPageReload, Key.R),
            Action(CoreTools.Translate("Package details"), TriggerPageDetails, Key.I));

        var window = Submenu(CoreTools.Translate("Window"),
            Action(CoreTools.Translate("Minimize"), () => WindowState = WindowState.Minimized, Key.M),
            Action(CoreTools.Translate("Zoom"), () => WindowState =
                WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized));

        var help = Submenu(CoreTools.Translate("Help"),
            Item(CoreTools.Translate("Help"), navigate, "Help"),
            Item(CoreTools.Translate("Release notes"), navigate, "ReleaseNotes"),
            new NativeMenuItemSeparator(),
            Item(CoreTools.Translate("UniGetUI Log"), navigate, "OwnLog"),
            Item(CoreTools.Translate("Package Manager logs"), navigate, "ManagerLog"),
            Item(CoreTools.Translate("Operation history"), navigate, "OperationHistory"));

        NativeMenu.SetMenu(this, new NativeMenu { Items = { edit, view, window, help } });
    }

    private TextBox? FocusedTextBox() => FocusManager?.GetFocusedElement() as TextBox;

    // ⌘A selects text in a focused field, and every package on a package page otherwise.
    private void SelectAllForFocus()
    {
        if (FocusedTextBox() is { } textBox)
            textBox.SelectAll();
        else
            TriggerPageSelectAll();
    }

    private static NativeMenuItem Submenu(string header, params NativeMenuItemBase[] items)
    {
        var menu = new NativeMenu();
        foreach (var item in items)
            menu.Items.Add(item);
        return new NativeMenuItem(header) { Menu = menu };
    }

    private static NativeMenuItem Item(string header, ICommand command, string? parameter = null,
        Key? key = null, KeyModifiers extraModifiers = KeyModifiers.None)
        => new(header)
        {
            Command = command,
            CommandParameter = parameter,
            Gesture = key is { } k ? new KeyGesture(k, KeyModifiers.Meta | extraModifiers) : null,
        };

    private static NativeMenuItem Action(string header, Action action, Key? key = null,
        KeyModifiers extraModifiers = KeyModifiers.None)
    {
        var item = new NativeMenuItem(header)
        {
            Gesture = key is { } k ? new KeyGesture(k, KeyModifiers.Meta | extraModifiers) : null,
        };
        item.Click += (_, _) => action();
        return item;
    }
}
