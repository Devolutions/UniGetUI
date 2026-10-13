using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
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

        // Text editing also works inside dialogs; everything else waits until the dialog closes.
        var edit = Submenu(CoreTools.Translate("Edit"),
            TextAction(CoreTools.Translate("Undo"), t => t.Undo(), e => e.Undo(), Key.Z),
            TextAction(CoreTools.Translate("Redo"), t => t.Redo(), e => e.Redo(), Key.Z, KeyModifiers.Shift),
            new NativeMenuItemSeparator(),
            TextAction(CoreTools.Translate("Cut"), t => t.Cut(), e => e.Cut(), Key.X),
            TextAction(CoreTools.Translate("Copy"), t => t.Copy(), e => e.Copy(), Key.C),
            TextAction(CoreTools.Translate("Paste"), t => t.Paste(), e => e.Paste(), Key.V),
            Action(CoreTools.Translate("Select all"), SelectAllForFocus, Key.A, allowInDialog: true),
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
            Action(CoreTools.Translate("Minimize"), () => WindowState = WindowState.Minimized, Key.M,
                allowInDialog: true),
            Action(CoreTools.Translate("Zoom"), () => WindowState =
                WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized,
                allowInDialog: true));

        var help = Submenu(CoreTools.Translate("Help"),
            Item(CoreTools.Translate("Help"), navigate, "Help"),
            Item(CoreTools.Translate("Release notes"), navigate, "ReleaseNotes"),
            new NativeMenuItemSeparator(),
            Item(CoreTools.Translate("UniGetUI Log"), navigate, "OwnLog"),
            Item(CoreTools.Translate("Package Manager logs"), navigate, "ManagerLog"),
            Item(CoreTools.Translate("Operation history"), navigate, "OperationHistory"));

        NativeMenu.SetMenu(this, new NativeMenu { Items = { edit, view, window, help } });
    }

    private Visual? FocusedVisual() => FocusManager?.GetFocusedElement() as Visual;

    // ⌘A selects text in a focused field or editor, and every package on a package page otherwise.
    private void SelectAllForFocus()
    {
        Visual? focused = FocusedVisual();
        if (focused?.FindAncestorOfType<TextBox>(includeSelf: true) is { } textBox)
            textBox.SelectAll();
        else if (focused?.FindAncestorOfType<TextEditor>(includeSelf: true) is { } editor)
            editor.SelectAll();
        else if (!ModalLayer.IsVisible)
            TriggerPageSelectAll();
    }

    // Routes a text command to the focused text box, or to the AvaloniaEdit log/JSON editor.
    private NativeMenuItem TextAction(string header, Action<TextBox> onTextBox, Action<TextEditor> onEditor,
        Key key, KeyModifiers extraModifiers = KeyModifiers.None)
        => Action(header, () =>
        {
            Visual? focused = FocusedVisual();
            if (focused?.FindAncestorOfType<TextBox>(includeSelf: true) is { } textBox)
                onTextBox(textBox);
            else if (focused?.FindAncestorOfType<TextEditor>(includeSelf: true) is { } editor)
                onEditor(editor);
        }, key, extraModifiers, allowInDialog: true);

    private static NativeMenuItem Submenu(string header, params NativeMenuItemBase[] items)
    {
        var menu = new NativeMenu();
        foreach (var item in items)
            menu.Items.Add(item);
        return new NativeMenuItem(header) { Menu = menu };
    }

    private NativeMenuItem Item(string header, ICommand command, string? parameter = null,
        Key? key = null, KeyModifiers extraModifiers = KeyModifiers.None)
        => Action(header, () =>
        {
            if (command.CanExecute(parameter))
                command.Execute(parameter);
        }, key, extraModifiers);

    // Like Window_KeyDown, page and navigation commands don't run while an in-window dialog is open.
    private NativeMenuItem Action(string header, Action action, Key? key = null,
        KeyModifiers extraModifiers = KeyModifiers.None, bool allowInDialog = false)
    {
        var item = new NativeMenuItem(header)
        {
            Gesture = key is { } k ? new KeyGesture(k, KeyModifiers.Meta | extraModifiers) : null,
        };
        item.Click += (_, _) =>
        {
            if (allowInDialog || !ModalLayer.IsVisible)
                action();
        };
        return item;
    }
}
