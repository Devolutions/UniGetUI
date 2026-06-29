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
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;

namespace UniGetUI.Tui.Views.Pages;

/// <summary>
/// The Package Bundles page: a live view of the in-memory bundle held by
/// <see cref="PackageBundlesLoader.Instance"/>, with import/export/restore/remove actions. Packages are
/// populated from the package-list pages (press <c>b</c> there) or by opening a bundle file. Incompatible
/// packages are shown but cannot be installed.
/// </summary>
internal sealed class BundlesPage : UserControl, IFocusablePage
{
    private enum PromptMode { None, Open, Save }

    private sealed record BundleRow(string Text, IPackage Package, bool Compatible)
    {
        public override string ToString() => Text;
    }

    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly TextBlock _columnHeader;
    private readonly TextBox _filter;
    private readonly StackPanel _details;

    // Rows sit inside a ListBox → ListBoxItem chain that the Consolonia theme indents by 1-char
    // border + 1-char item padding; the sibling column header has no such indent, so nudge it right
    // by the same amount to align the titles with the row columns.
    private const int ListRowLeftIndent = 2; // ListBox border (1) + ListBoxItem padding (1)
    private readonly DockPanel _promptRow;
    private readonly TextBlock _promptLabel;
    private readonly TextBox _promptInput;

    private PromptMode _mode = PromptMode.None;
    private bool _busy;

    private static readonly (string Title, int Width)[] Schema =
        { ("Name", 26), ("Id", 30), ("Version", 14), ("Source", 16), ("", 4) };

    public BundlesPage()
    {
        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#C0C0C0")),
            Margin = new Thickness(0, 0, 0, 1),
        };

        _promptLabel = new TextBlock
        {
            Foreground = DevolutionsPalette.BrandBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _promptInput = new TextBox { Watermark = "path…  (Enter confirm · Esc cancel)" };
        _promptInput.KeyDown += OnPromptKeyDown;
        _promptRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 1), IsVisible = false };
        DockPanel.SetDock(_promptLabel, Dock.Left);
        _promptRow.Children.Add(_promptLabel);
        _promptRow.Children.Add(_promptInput);

        _columnHeader = new TextBlock
        {
            Text = HeaderText(),
            Foreground = DevolutionsPalette.BrandBrush,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(ListRowLeftIndent, 0, 0, 0),
        };

        _filter = new TextBox { Watermark = "type to filter by name or id  (Enter/↓ to list)" };
        _filter.TextChanged += (_, _) => Rebuild();
        _filter.KeyDown += OnFilterKeyDown;

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<BundleRow>(
                (row, _) => new TextBlock
                {
                    Text = row?.Text ?? string.Empty,
                    Foreground = row?.Compatible == false
                        ? new SolidColorBrush(Color.Parse("#FFB454"))
                        : new SolidColorBrush(Color.Parse("#BBBBBB")),
                },
                supportsRecycling: true),
        };
        _list.SelectionChanged += (_, _) => UpdateDetails();
        _list.KeyDown += OnListKeyDown;
        _list.AddHandler(TextInputEvent, OnListTextInput, RoutingStrategies.Tunnel);

        _details = new StackPanel { Spacing = 0 };
        ClearDetails();

        Content = BuildLayout();
    }

    private Control BuildLayout()
    {
        var header = new StackPanel { Spacing = 0 };
        header.Children.Add(new TextBlock
        {
            Text = "Package Bundles",
            Foreground = DevolutionsPalette.BrandBrush,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 1),
        });
        header.Children.Add(_status);
        header.Children.Add(_promptRow);
        var filterRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 1) };
        var filterLabel = new TextBlock
        {
            Text = "Filter ",
            Foreground = DevolutionsPalette.BrandBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(filterLabel, Dock.Left);
        filterRow.Children.Add(filterLabel);
        filterRow.Children.Add(_filter);
        header.Children.Add(filterRow);
        header.Children.Add(_columnHeader);

        var detailPane = new DockPanel { LastChildFill = true, Width = 36, Margin = new Thickness(2, 0, 0, 0) };
        var detailHeader = new TextBlock
        {
            Text = "Details",
            Foreground = DevolutionsPalette.BrandBrush,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 1),
        };
        DockPanel.SetDock(detailHeader, Dock.Top);
        detailPane.Children.Add(detailHeader);
        detailPane.Children.Add(_details);

        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(detailPane, Dock.Right);
        body.Children.Add(detailPane);
        body.Children.Add(_list);

        var hint = new TextBlock
        {
            Text = "o open   ·   s save   ·   x install   ·   r remove   ·   n new",
            Foreground = new SolidColorBrush(Color.Parse("#C0C0C0")),
            Margin = new Thickness(0, 1, 0, 0),
        };

        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 1, 2, 1) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(hint);
        root.Children.Add(body);
        return root;
    }

    public bool FocusPrimary() => _filter.Focus();

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return or Key.Down)
        {
            TuiFocus.SeatListFocus(_list);
            e.Handled = true;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        PackageBundlesLoader.Instance.PackagesChanged += OnBundleChanged;
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        PackageBundlesLoader.Instance.PackagesChanged -= OnBundleChanged;
    }

    private void OnBundleChanged(object? sender, PackagesChangedEvent e)
    {
        // The loader fires on the calling thread; marshal to the UI thread before touching controls.
        Dispatcher.UIThread.Post(Rebuild);
    }

    private void Rebuild()
    {
        var prev = (_list.SelectedItem as BundleRow)?.Package;
        var packages = PackageBundlesLoader.Instance.Packages;
        string filter = (_filter.Text ?? string.Empty).Trim();

        IEnumerable<IPackage> filtered = packages;
        if (filter.Length > 0)
        {
            filtered = packages.Where(p =>
                (p.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                || (p.Id?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var rows = filtered.Select(p => new BundleRow(RowText(p), p, p is ImportedPackage)).ToList();
        _list.ItemsSource = rows;

        int incompatible = rows.Count(r => !r.Compatible);
        string filterSuffix = filter.Length > 0 ? "  ·  filtered" : string.Empty;
        _status.Text = packages.Count == 0
            ? "Bundle is empty. Add packages with 'b' from Discover/Installed/Updates, or press 'o' to open a file."
            : rows.Count == 0
                ? "No packages match the current filter."
                : $"{rows.Count} package(s) in bundle"
                  + (incompatible > 0 ? $"  ·  {incompatible} incompatible" : string.Empty)
                  + filterSuffix;

        if (rows.Count == 0)
        {
            ClearDetails();
            return;
        }

        var match = rows.FirstOrDefault(r => ReferenceEquals(r.Package, prev));
        _list.SelectedItem = match ?? rows[0];
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.O:
                BeginPrompt(PromptMode.Open);
                e.Handled = true;
                break;
            case Key.S:
                BeginPrompt(PromptMode.Save);
                e.Handled = true;
                break;
            case Key.X:
                _ = RestoreAsync();
                e.Handled = true;
                break;
            case Key.R:
            case Key.Delete:
                RemoveSelected();
                e.Handled = true;
                break;
            case Key.N:
                PackageBundlesLoader.Instance.ClearPackages();
                _status.Text = "Bundle cleared.";
                e.Handled = true;
                break;
        }
    }

    private void OnListTextInput(object? sender, TextInputEventArgs e)
    {
        // Suppress ListBox type-ahead for our action letters.
        if (e.Text is "o" or "O" or "s" or "S" or "x" or "X" or "r" or "R" or "n" or "N")
            e.Handled = true;
    }

    // Menu-bar entry points: the File menu's Open/Save items drive the same inline path prompt as
    // the list's o/s hotkeys.
    internal void BeginOpenPrompt() => BeginPrompt(PromptMode.Open);
    internal void BeginSavePrompt() => BeginPrompt(PromptMode.Save);

    private void BeginPrompt(PromptMode mode)
    {
        _mode = mode;
        _promptLabel.Text = mode == PromptMode.Open ? "Open bundle  " : "Save bundle  ";
        _promptRow.IsVisible = true;
        _promptInput.Text = string.Empty;
        _promptInput.Focus();
    }

    private void EndPrompt()
    {
        _mode = PromptMode.None;
        _promptRow.IsVisible = false;
        // When the bundle list is empty there is no row to seat focus on and SeatListFocus returns
        // false, leaving focus on the now-hidden _promptInput; fall back to the always-visible filter
        // box so keyboard input still has a live target.
        if (!TuiFocus.SeatListFocus(_list))
            _filter.Focus();
    }

    private async void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EndPrompt();
            e.Handled = true;
            return;
        }
        if (e.Key is not (Key.Enter or Key.Return)) return;

        e.Handled = true;
        string path = (_promptInput.Text ?? string.Empty).Trim().Trim('"');
        var mode = _mode;
        EndPrompt();
        if (path.Length == 0 || _busy) return;

        try
        {
            _busy = true;
            if (mode == PromptMode.Open)
            {
                _status.Text = $"Opening {path}…";
                int n = await TuiBundleService.ImportFileAsync(path);
                _status.Text = $"Imported {n} package(s) from {System.IO.Path.GetFileName(path)}.";
            }
            else if (mode == PromptMode.Save)
            {
                _status.Text = $"Saving {path}…";
                await TuiBundleService.SaveToFileAsync(path, PackageBundlesLoader.Instance.Packages);
                _status.Text = $"Saved {PackageBundlesLoader.Instance.Count()} package(s) to {System.IO.Path.GetFileName(path)}.";
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"Bundle {mode.ToString().ToLowerInvariant()} failed: {ex.Message}";
            Logger.Error(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    private void RemoveSelected()
    {
        if ((_list.SelectedItem as BundleRow)?.Package is not { } package) return;
        PackageBundlesLoader.Instance.RemoveRange(new[] { package });
        _status.Text = $"Removed {package.Name} from the bundle.";
    }

    private async System.Threading.Tasks.Task RestoreAsync()
    {
        if (_busy) return;
        var packages = PackageBundlesLoader.Instance.Packages;
        if (packages.Count == 0) { _status.Text = "Nothing to install — the bundle is empty."; return; }

        try
        {
            _busy = true;
            _status.Text = SimulatedOperation.IsEnabled
                ? "Enqueueing simulated bundle operations (no system changes)…"
                : "Registering and enqueueing bundle operations…";
            int n = await TuiBundleService.RestoreAsync(packages);
            if (n == 0)
            {
                _status.Text = "No compatible packages to install in this bundle.";
            }
            else
            {
                _status.Text = $"Enqueued {n} install operation(s).";
                TuiShell.Navigate("operations");
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not start bundle install: {ex.Message}";
            Logger.Error(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    private void UpdateDetails()
    {
        if ((_list.SelectedItem as BundleRow) is not { } row)
        {
            ClearDetails();
            return;
        }

        var p = row.Package;
        _details.Children.Clear();
        AddDetail("Name", p.Name);
        AddDetail("Id", p.Id);
        AddDetail("Version", p.VersionString);
        AddDetail("Source", p.Source.Name);
        AddDetail("Manager", p.Manager.DisplayName);
        if (!row.Compatible)
        {
            _details.Children.Add(new TextBlock
            {
                Text = "incompatible — cannot be installed",
                Foreground = new SolidColorBrush(Color.Parse("#FFB454")),
                Margin = new Thickness(0, 1, 0, 0),
            });
        }
    }

    private void ClearDetails()
    {
        _details.Children.Clear();
        _details.Children.Add(new TextBlock
        {
            Text = "Select a package to see details.",
            Foreground = new SolidColorBrush(Color.Parse("#BBBBBB")),
        });
    }

    private void AddDetail(string label, string value)
    {
        var row = new DockPanel { LastChildFill = true };
        var key = new TextBlock
        {
            Text = label + "  ",
            Foreground = DevolutionsPalette.BrandBrush,
            Width = 10,
        };
        DockPanel.SetDock(key, Dock.Left);
        row.Children.Add(key);
        row.Children.Add(new TextBlock { Text = value, Foreground = new SolidColorBrush(Color.Parse("#BBBBBB")), TextWrapping = TextWrapping.Wrap });
        _details.Children.Add(row);
    }

    private static string HeaderText()
        => string.Concat(Schema.Select(c => Pad(c.Title, c.Width)));

    private static string RowText(IPackage p)
    {
        string compat = p is ImportedPackage ? "" : "!";
        return Pad(p.Name, Schema[0].Width)
             + Pad(p.Id, Schema[1].Width)
             + Pad(p.VersionString, Schema[2].Width)
             + Pad(p.Source.Name, Schema[3].Width)
             + compat;
    }

    private static string Pad(string s, int width)
    {
        s ??= string.Empty;
        if (s.Length >= width) return s.Length > width ? s[..(width - 1)] + " " : s;
        return s + new string(' ', width - s.Length);
    }
}
