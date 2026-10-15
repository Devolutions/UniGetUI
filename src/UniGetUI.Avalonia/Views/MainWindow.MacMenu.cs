using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views;

// macOS menu bar. Apple expects every toolbar action to also be a menu command with its ⌘ shortcut;
// items reuse the sidebar commands and the same page actions as the keyboard shortcuts.
public partial class MainWindow
{
    // Each item paired with when it applies; inapplicable items are greyed out, as in native menus.
    private readonly List<(NativeMenuItem Item, Func<bool> IsEnabled)> _macMenuStates = [];

    // Text target resolved once per refresh, so the Edit predicates don't each walk the tree.
    private TextBox? _menuTextBox;
    private TextEditor? _menuEditor;

    private NativeMenu? _macViewMenu;
    private NativeMenuItem[] _macPageItems = [];

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
            RefreshOnDemand(appMenu);
        }

        // Text commands apply to a focused text field or log editor, including inside dialogs.
        var edit = Submenu(CoreTools.Translate("Edit"),
            TextAction(CoreTools.Translate("Undo"), t => t.Undo(), e => e.Undo(),
                t => t.CanUndo, e => e.CanUndo, Key.Z),
            TextAction(CoreTools.Translate("Redo"), t => t.Redo(), e => e.Redo(),
                t => t.CanRedo, e => e.CanRedo, Key.Z, KeyModifiers.Shift),
            new NativeMenuItemSeparator(),
            TextAction(CoreTools.Translate("Cut"), t => t.Cut(), e => e.Cut(),
                t => t.CanCut, e => !e.IsReadOnly && e.SelectionLength > 0, Key.X),
            TextAction(CoreTools.Translate("Copy"), t => t.Copy(), e => e.Copy(),
                t => t.CanCopy, e => e.SelectionLength > 0, Key.C),
            TextAction(CoreTools.Translate("Paste"), t => t.Paste(), e => e.Paste(),
                t => t.CanPaste, e => !e.IsReadOnly, Key.V),
            Action(CoreTools.Translate("Select all"), SelectAllForFocus, Key.A,
                enabled: () => _menuTextBox is not null || _menuEditor is not null || PageShortcutListener is not null),
            new NativeMenuItemSeparator(),
            Action(CoreTools.Translate("Find"), TriggerPageSearch, Key.F, enabled: () => PageShortcutListener is not null));

        _macPageItems = CreateMacPageItems();
        var view = Submenu(CoreTools.Translate("View"), [
            .. _macPageItems,
            new NativeMenuItemSeparator(),
            Item(CoreTools.Translate("Toggle navigation panel"), ViewModel.ToggleSidebarCommand, null, Key.S, KeyModifiers.Control),
            new NativeMenuItemSeparator(),
            Action(CoreTools.Translate("Reload"), TriggerPageReload, Key.R, enabled: () => PageShortcutListener is not null),
            Action(CoreTools.Translate("Package details"), TriggerPageDetails, Key.I, enabled: () => PageShortcutListener is not null)]);

        // macOS greys Minimize and Zoom out for a window in full screen.
        var window = Submenu(CoreTools.Translate("Window"),
            Action(CoreTools.Translate("Minimize"), () => WindowState = WindowState.Minimized, Key.M,
                enabled: () => WindowState != WindowState.FullScreen),
            Action(CoreTools.Translate("Zoom"), () => WindowState =
                WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized,
                enabled: () => WindowState != WindowState.FullScreen));

        var help = Submenu(CoreTools.Translate("Help"),
            Item(CoreTools.Translate("Help"), navigate, "Help"),
            Item(CoreTools.Translate("Release notes"), navigate, "ReleaseNotes"),
            new NativeMenuItemSeparator(),
            Item(CoreTools.Translate("UniGetUI Log"), navigate, "OwnLog"),
            Item(CoreTools.Translate("Package Manager logs"), navigate, "ManagerLog"),
            Item(CoreTools.Translate("Operation history"), navigate, "OperationHistory"));

        _macViewMenu = view.Menu;
        sidebar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SidebarViewModel.NavigationPages))
                RefreshMacViewMenu();
        };
        NativeMenu.SetMenu(this, new NativeMenu { Items = { edit, view, window, help } });
        UpdateMacMenuState();
    }

    // Settings is reached from the application menu (⌘,), so the View menu skips it.
    private NativeMenuItem[] CreateMacPageItems()
        => ViewModel.ShortcutPages
            .Select((page, index) => (page, key: Key.D1 + index))
            .Where(p => p.page != PageType.Settings)
            .Select(p => Item(PageMenuTitle(p.page), ViewModel.Sidebar.RequestNavigationCommand, p.page.ToString(), p.key))
            .ToArray();

    private void RefreshMacViewMenu()
    {
        if (_macViewMenu is null)
            return;

        foreach (var item in _macPageItems)
        {
            _macViewMenu.Items.Remove(item);
            _macMenuStates.RemoveAll(state => state.Item == item);
        }

        _macPageItems = CreateMacPageItems();
        for (int i = 0; i < _macPageItems.Length; i++)
            _macViewMenu.Items.Insert(i, _macPageItems[i]);
        UpdateMacMenuState();
    }

    // States are computed as a menu shows. For ⌘ shortcuts a stale disabled item lets the key reach the
    // focused control, and a click re-checks the live state, so the outcome is right either way.
    private void RefreshOnDemand(NativeMenu menu)
    {
        menu.NeedsUpdate += (_, _) => UpdateMacMenuState();
        menu.Opening += (_, _) => UpdateMacMenuState();
    }

    private void UpdateMacMenuState()
    {
        (_menuTextBox, _menuEditor) = ResolveTextTarget();
        foreach (var (item, isEnabled) in _macMenuStates)
            item.IsEnabled = isEnabled();
    }

    // The focused text box or AvaloniaEdit editor; while a dialog is open, only one inside it.
    private (TextBox? TextBox, TextEditor? Editor) ResolveTextTarget()
    {
        if (FocusManager?.GetFocusedElement() is not Visual focused
            || (ModalLayer.IsVisible && !ModalLayer.IsVisualAncestorOf(focused)))
            return (null, null);

        return focused.FindAncestorOfType<TextBox>(includeSelf: true) is { } textBox
            ? (textBox, null)
            : (null, focused.FindAncestorOfType<TextEditor>(includeSelf: true));
    }

    // ⌘A selects text in a focused field or editor, and every package on a package page otherwise.
    private void SelectAllForFocus()
    {
        var (textBox, editor) = ResolveTextTarget();
        if (textBox is not null)
            textBox.SelectAll();
        else if (editor is not null)
            editor.SelectAll();
        else
            TriggerPageSelectAll();
    }

    // Routes a text command to the focused text box, or to the AvaloniaEdit log/JSON editor.
    private NativeMenuItem TextAction(string header, Action<TextBox> onTextBox, Action<TextEditor> onEditor,
        Func<TextBox, bool> canTextBox, Func<TextEditor, bool> canEditor,
        Key key, KeyModifiers extraModifiers = KeyModifiers.None)
        => Action(header, () =>
        {
            var (textBox, editor) = ResolveTextTarget();
            if (textBox is not null)
                onTextBox(textBox);
            else if (editor is not null)
                onEditor(editor);
        }, key, extraModifiers, enabled: () => _menuTextBox is { } textBox ? canTextBox(textBox)
            : _menuEditor is { } editor && canEditor(editor));

    private static string PageMenuTitle(PageType page) => page switch
    {
        PageType.Catalog => CoreTools.Translate("Software Catalog"),
        PageType.Discover => CoreTools.Translate("Discover Packages"),
        PageType.Updates => CoreTools.Translate("Software Updates"),
        PageType.Installed => CoreTools.Translate("Installed Packages"),
        PageType.Bundles => CoreTools.Translate("Package Bundles"),
        PageType.CatalogEditor => CoreTools.Translate("Catalog Editor"),
        PageType.Managers => CoreTools.Translate("Package Managers"),
        _ => CoreTools.Translate("Settings"),
    };

    private NativeMenuItem Submenu(string header, params NativeMenuItemBase[] items)
    {
        var menu = new NativeMenu();
        foreach (var item in items)
            menu.Items.Add(item);
        RefreshOnDemand(menu);
        return new NativeMenuItem(header) { Menu = menu };
    }

    private NativeMenuItem Item(string header, ICommand command, string? parameter = null,
        Key? key = null, KeyModifiers extraModifiers = KeyModifiers.None)
        => Action(header, () => command.Execute(parameter), key, extraModifiers,
            enabled: () => !ModalLayer.IsVisible && command.CanExecute(parameter));

    // By default, like Window_KeyDown, a command doesn't apply while an in-window dialog is open.
    // Clicks re-check the rule, since the state may have changed since the last refresh.
    private NativeMenuItem Action(string header, Action action, Key? key = null,
        KeyModifiers extraModifiers = KeyModifiers.None, Func<bool>? enabled = null)
    {
        Func<bool> isEnabled = enabled ?? (() => !ModalLayer.IsVisible);
        var item = new NativeMenuItem(header)
        {
            Gesture = key is { } k ? new KeyGesture(k, KeyModifiers.Meta | extraModifiers) : null,
        };
        item.Click += (_, _) =>
        {
            (_menuTextBox, _menuEditor) = ResolveTextTarget();
            if (isEnabled())
                action();
        };
        _macMenuStates.Add((item, isEnabled));
        return item;
    }
}
