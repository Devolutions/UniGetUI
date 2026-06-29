using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;

namespace UniGetUI.Tui.Views.Pages;

/// <summary>
/// An interactive settings page exposing a curated set of UniGetUI's boolean preferences, grouped by
/// category. Values are read from and written to the shared file-backed <see cref="Settings"/> store, so
/// they are immediately consistent with the desktop app. Each row's label states the literal effect of the
/// underlying key (many keys are phrased as "Disable…"), so a checked box always means "this effect is on".
/// </summary>
internal sealed class SettingsPage : UserControl, IFocusablePage
{
    private sealed record Row(string Label, Settings.K? Key)
    {
        public bool IsHeader => Key is null;
        public override string ToString() => Render();

        public string Render()
        {
            if (IsHeader) return Label;
            bool on = Settings.Get(Key!.Value);
            return $"  [{(on ? "x" : " ")}]  {Label}";
        }
    }

    // Curated, high-signal subset mirroring the desktop General/Interface/Updates/Notifications/Backup pages.
    private static readonly Row[] Definitions =
    {
        new("General", null),
        new("Disable telemetry", Settings.K.DisableTelemetry),
        new("Keep successful operations in the list", Settings.K.MaintainSuccessfulInstalls),
        new("Cache administrator rights for the session", Settings.K.DoCacheAdminRights),
        new("Disable the system tray icon", Settings.K.DisableSystemTray),

        new("Interface", null),
        new("Disable icons on package lists", Settings.K.DisableIconsOnPackageLists),
        new("Show the version number on the title bar", Settings.K.ShowVersionNumberOnTitlebar),
        new("Disable instant (as-you-type) search", Settings.K.DisableInstantSearch),

        new("Updates", null),
        new("Disable automatic update checks", Settings.K.DisableAutoCheckforUpdates),
        new("Automatically install available updates", Settings.K.AutomaticallyUpdatePackages),
        new("Do not select updates by default", Settings.K.DisableSelectingUpdatesByDefault),
        new("Ignore updates that are not applicable", Settings.K.IgnoreUpdatesNotApplicable),

        new("Notifications", null),
        new("Disable all notifications", Settings.K.DisableNotifications),
        new("Disable update notifications", Settings.K.DisableUpdatesNotifications),
        new("Disable error notifications", Settings.K.DisableErrorNotifications),
        new("Disable success notifications", Settings.K.DisableSuccessNotifications),

        new("Backup", null),
        new("Enable local package backup", Settings.K.EnablePackageBackup_LOCAL),
        new("Timestamp backup files", Settings.K.EnableBackupTimestamping),
    };

    private readonly ListBox _list;
    private readonly TextBlock _status;

    public SettingsPage()
    {
        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#C0C0C0")),
            Margin = new Thickness(0, 0, 0, 1),
            Text = "↑/↓ move   ·   Space / Enter toggle the selected setting",
        };

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<Row>(
                (row, _) => new TextBlock
                {
                    Text = row?.Render() ?? string.Empty,
                    FontWeight = row?.IsHeader == true ? FontWeight.Bold : FontWeight.Normal,
                    Foreground = row?.IsHeader == true ? DevolutionsPalette.BrandBrush : new SolidColorBrush(Color.Parse("#BBBBBB")),
                    Margin = row?.IsHeader == true ? new Thickness(0, 1, 0, 0) : new Thickness(0),
                },
                supportsRecycling: true),
        };
        _list.ItemsSource = Definitions.ToList();
        _list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        SelectFirstToggle();

        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 1, 2, 1) };
        var header = new StackPanel { Spacing = 0 };
        header.Children.Add(new TextBlock
        {
            Text = "Settings",
            Foreground = DevolutionsPalette.BrandBrush,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 1),
        });
        header.Children.Add(_status);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(new ScrollViewer { Content = _list });
        Content = root;
    }

    public bool FocusPrimary()
    {
        if (_list.SelectedIndex < 0) SelectFirstToggle();

        // A bare ListBox.Focus() does not reliably grab the keyboard when the pane is entered
        // "cold" (i.e. focus arriving from the sidebar with no realized item yet holding focus):
        // the keyboard stays on the sidebar and Space/Enter never reach OnListKeyDown. Focusing the
        // selected row's realized container moves real keyboard focus into the list; fall back to the
        // ListBox itself if the container is not realized.
        if (_list.ContainerFromIndex(_list.SelectedIndex) is Control container && container.Focus())
        {
            return true;
        }

        return _list.Focus();
    }

    private void SelectFirstToggle()
    {
        var rows = (List<Row>)_list.ItemsSource!;
        int idx = rows.FindIndex(r => !r.IsHeader);
        if (idx >= 0) _list.SelectedIndex = idx;
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter or Key.Return)) return;
        e.Handled = true;

        if (_list.SelectedItem is not Row row || row.Key is null) return;

        bool newValue = !Settings.Get(row.Key.Value);
        Settings.Set(row.Key.Value, newValue);
        _status.Text = $"{row.Label}: {(newValue ? "ON" : "off")}";

        // Re-assign the item source so the toggled row's checkbox re-renders, preserving selection.
        // The ListBox virtualizes with recycling and the row text is assigned (not bound) once at
        // container creation, so a plain reassignment can reuse stale containers. Null first to force
        // a full teardown, then reassign and restore the selected index.
        int sel = _list.SelectedIndex;
        var items = Definitions.ToList();
        _list.ItemsSource = null;
        _list.ItemsSource = items;
        // Clamp defensively so future filtering / an empty definition set can't restore an
        // out-of-range index.
        int restored = items.Count == 0 ? -1 : Math.Clamp(sel, 0, items.Count - 1);
        _list.SelectedIndex = restored;

        // The teardown above destroys the focused row container, which would silently drop keyboard
        // focus and make the *next* Space/Enter a no-op. Re-focus the selected container once the new
        // containers have been realized (posted, because realization happens after this layout pass).
        Dispatcher.UIThread.Post(() =>
        {
            // Pages are cached and reused, so this posted callback can run after the user has
            // navigated away. Only re-focus while this list is still attached to a top level and the
            // restored index is still valid; otherwise no-op so focus isn't stolen from the page the
            // user is now on.
            if (TopLevel.GetTopLevel(_list) is null) return;
            if (restored < 0) return;
            if (_list.ContainerFromIndex(restored) is Control container) container.Focus();
            else _list.Focus();
        }, DispatcherPriority.Background);
    }
}
