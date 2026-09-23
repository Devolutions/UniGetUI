using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
    private sealed record BundleRow(
        IPackage Package,
        bool Compatible,
        string Name,
        string Id,
        string Version,
        string Source,
        string Status)
    {
        public override string ToString() => Name;
    }

    private readonly DataGrid _grid;
    private readonly TextBlock _status;
    private readonly AutoCompleteBox _filter;
    private readonly StackPanel _details;

    private bool _busy;

    private static readonly (string Title, string Property, int Width)[] Schema =
    [
        ("Name", nameof(BundleRow.Name), 26),
        ("Id", nameof(BundleRow.Id), 30),
        ("Version", nameof(BundleRow.Version), 14),
        ("Source", nameof(BundleRow.Source), 16),
        ("State", nameof(BundleRow.Status), 8),
    ];

    private static readonly IReadOnlyList<FilePickerFileType> BundleFileTypes =
    [
        new("UniGetUI bundles") { Patterns = ["*.ubundle"] },
        new("All supported bundle formats") { Patterns = ["*.ubundle", "*.json", "*.yaml", "*.yml", "*.xml"] },
        new("All files") { Patterns = ["*"] },
    ];

    public BundlesPage()
    {
        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#C0C0C0")), Margin = new Thickness(0, 0, 0, 1),
        };

        _filter = new AutoCompleteBox
        {
            FilterMode = AutoCompleteFilterMode.Contains,
            IsTextCompletionEnabled = true,
            MinimumPrefixLength = 1,
            PlaceholderText = "type to filter by name or id  (Enter/↓ to list)",
        };
        _filter.TextChanged += (_, _) => Rebuild();
        _filter.KeyDown += OnFilterKeyDown;
        _filter.AddHandler(TextInputEvent, OnFilterTextInput, RoutingStrategies.Tunnel);

        _grid = BuildGrid();
        _grid.SelectionChanged += (_, _) => UpdateDetails();
        _grid.KeyDown += OnGridKeyDown;
        _grid.AddHandler(TextInputEvent, OnGridTextInput, RoutingStrategies.Tunnel);

        _details = new StackPanel { Spacing = 0 };
        ClearDetails();

        Content = BuildLayout();
    }

    private Control BuildLayout()
    {
        var header = new StackPanel { Spacing = 0 };
        header.Children.Add(TuiChrome.PageTitle("[]", "Package Bundles"));
        header.Children.Add(TuiChrome.Separator());
        header.Children.Add(_status);
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
        body.Children.Add(_grid);

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

    private static DataGrid BuildGrid()
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            Background = Brushes.Transparent,
            CanUserSortColumns = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            IsReadOnly = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        foreach ((string title, string property, int width) in Schema)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = title,
                Binding = new Binding(property),
                Width = new DataGridLength(width, DataGridLengthUnitType.Pixel),
            });
        }

        return grid;
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return or Key.Down)
        {
            TuiFocus.SeatDataGridFocus(_grid);
            e.Handled = true;
        }
    }

    private void OnFilterTextInput(object? sender, TextInputEventArgs e)
        => TuiInputGuard.HandleTextInput(_filter, e, "bundle filter");

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
        var prev = (_grid.SelectedItem as BundleRow)?.Package;
        var packages = PackageBundlesLoader.Instance.Packages;
        string filter = (_filter.Text ?? string.Empty).Trim();

        IEnumerable<IPackage> filtered = packages;
        _filter.ItemsSource = BuildSuggestions(packages);
        if (filter.Length > 0)
        {
            filtered = packages.Where(p =>
                (p.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                || (p.Id?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var rows = filtered.Select(BuildRow).ToList();
        _grid.ItemsSource = rows;

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
        _grid.SelectedItem = match ?? rows[0];
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = CopySelectedAsync();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.O:
                _ = OpenBundlePickerAsync();
                e.Handled = true;
                break;
            case Key.S:
                _ = SaveBundlePickerAsync();
                e.Handled = true;
                break;
            case Key.X:
                _ = RestoreAsync();
                e.Handled = true;
                break;
            case Key.R:
            case Key.Delete:
                _ = RemoveSelectedAsync();
                e.Handled = true;
                break;
            case Key.N:
                _ = ClearBundleAsync();
                e.Handled = true;
                break;
        }
    }

    private void OnGridTextInput(object? sender, TextInputEventArgs e)
    {
        // Suppress ListBox type-ahead for our action letters.
        if (e.Text is "o" or "O" or "s" or "S" or "x" or "X" or "r" or "R" or "n" or "N")
            e.Handled = true;
    }

    // Menu-bar entry points: the File menu's Open/Save items drive the same storage picker path as
    // the list's o/s hotkeys.
    internal void OpenBundlePicker() => _ = OpenBundlePickerAsync();
    internal void SaveBundlePicker() => _ = SaveBundlePickerAsync();

    private async System.Threading.Tasks.Task OpenBundlePickerAsync()
    {
        if (_busy) return;

        IStorageProvider? storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider?.CanOpen != true)
        {
            _status.Text = "Bundle file picker is not available in this terminal.";
            TuiNotifications.Warning("Picker unavailable", "This terminal does not expose an open-file picker.");
            return;
        }

        try
        {
            _busy = true;
            IStorageFolder? startLocation = await GetStartLocationAsync(storageProvider);
            IReadOnlyList<IStorageFile> files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open UniGetUI bundle",
                AllowMultiple = false,
                SuggestedStartLocation = startLocation,
                FileTypeFilter = BundleFileTypes,
            });

            if (files.Count == 0)
            {
                _status.Text = "Open bundle canceled.";
                return;
            }

            string path = files[0].Path.LocalPath;
            _status.Text = $"Opening {path}…";
            int n = await TuiBundleService.ImportFileAsync(path);
            _status.Text = $"Imported {n} package(s) from {Path.GetFileName(path)}.";
            TuiNotifications.Success("Bundle imported", $"{n} package(s) from {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            _status.Text = $"Bundle open failed: {ex.Message}";
            TuiNotifications.Error("Bundle open failed", ex.Message);
            Logger.Error(ex);
        }
        finally
        {
            _busy = false;
            RestoreGridOrFilterFocus();
        }
    }

    private async System.Threading.Tasks.Task SaveBundlePickerAsync()
    {
        if (_busy) return;

        IStorageProvider? storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider?.CanSave != true)
        {
            _status.Text = "Bundle save picker is not available in this terminal.";
            TuiNotifications.Warning("Picker unavailable", "This terminal does not expose a save-file picker.");
            return;
        }

        try
        {
            _busy = true;
            IStorageFolder? startLocation = await GetStartLocationAsync(storageProvider);
            IStorageFile? file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save UniGetUI bundle",
                SuggestedStartLocation = startLocation,
                SuggestedFileName = "UniGetUI-bundle.ubundle",
                DefaultExtension = "ubundle",
                FileTypeChoices = BundleFileTypes,
            });

            if (file is null)
            {
                _status.Text = "Save bundle canceled.";
                return;
            }

            string path = file.Path.LocalPath;
            _status.Text = $"Saving {path}…";
            await TuiBundleService.SaveToFileAsync(path, PackageBundlesLoader.Instance.Packages);
            _status.Text = $"Saved {PackageBundlesLoader.Instance.Count()} package(s) to {Path.GetFileName(path)}.";
            TuiNotifications.Success("Bundle saved", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            _status.Text = $"Bundle save failed: {ex.Message}";
            TuiNotifications.Error("Bundle save failed", ex.Message);
            Logger.Error(ex);
        }
        finally
        {
            _busy = false;
            RestoreGridOrFilterFocus();
        }
    }

    private static async System.Threading.Tasks.Task<IStorageFolder?> GetStartLocationAsync(
        IStorageProvider storageProvider)
    {
        string currentDirectory = Path.GetFullPath(Environment.CurrentDirectory);
        if (!currentDirectory.EndsWith(Path.DirectorySeparatorChar))
            currentDirectory += Path.DirectorySeparatorChar;

        return await storageProvider.TryGetFolderFromPathAsync(new Uri(currentDirectory));
    }

    private void RestoreGridOrFilterFocus()
    {
        if (!TuiFocus.SeatDataGridFocus(_grid))
            _filter.Focus();
    }

    private async System.Threading.Tasks.Task ClearBundleAsync()
    {
        if (_busy) return;
        if (PackageBundlesLoader.Instance.Packages.Count == 0)
        {
            _status.Text = "Bundle is already empty.";
            return;
        }

        if (!await TuiDialogs.ConfirmAsync(this, "Clear bundle", "Remove every package from the current bundle?"))
        {
            _status.Text = "Clear bundle canceled.";
            return;
        }

        PackageBundlesLoader.Instance.ClearPackages();
        _status.Text = "Bundle cleared.";
        TuiNotifications.Success("Bundle cleared", "The current bundle is now empty.");
    }

    private async System.Threading.Tasks.Task RemoveSelectedAsync()
    {
        if ((_grid.SelectedItem as BundleRow)?.Package is not { } package) return;
        if (!await TuiDialogs.ConfirmAsync(this, "Remove from bundle",
                $"Remove {package.Name} from the current bundle?"))
        {
            _status.Text = "Remove package canceled.";
            return;
        }

        PackageBundlesLoader.Instance.RemoveRange(new[] { package });
        _status.Text = $"Removed {package.Name} from the bundle.";
        TuiNotifications.Success("Removed from bundle", package.Name);
    }

    private async System.Threading.Tasks.Task CopySelectedAsync()
    {
        if ((_grid.SelectedItem as BundleRow) is not { } row)
        {
            _status.Text = "No bundle package selected to copy.";
            TuiNotifications.Warning("Nothing to copy", "Select a bundle package first.");
            return;
        }

        string text = string.Join(Environment.NewLine,
            $"Name: {row.Name}",
            $"Id: {row.Id}",
            $"Version: {row.Version}",
            $"Source: {row.Source}",
            $"State: {row.Status}");

        if (await TuiClipboard.CopyAsync(this, row.Name, text))
            _status.Text = $"Copied {row.Name} to the clipboard.";
    }

    private async System.Threading.Tasks.Task RestoreAsync()
    {
        if (_busy) return;
        var packages = PackageBundlesLoader.Instance.Packages;
        if (packages.Count == 0)
        {
            _status.Text = "Nothing to install — the bundle is empty.";
            return;
        }

        if (!await TuiDialogs.ConfirmAsync(
                this,
                "Install bundle",
                $"Install {packages.Count} compatible package(s) from the current bundle?"))
        {
            _status.Text = "Bundle install canceled.";
            return;
        }

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
                TuiNotifications.Warning("Bundle install skipped", "No compatible packages were found.");
            }
            else
            {
                _status.Text = $"Enqueued {n} install operation(s).";
                TuiNotifications.Info("Bundle install enqueued", $"{n} operation(s)");
                TuiShell.Navigate("operations");
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not start bundle install: {ex.Message}";
            TuiNotifications.Error("Bundle install failed", ex.Message);
            Logger.Error(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    private void UpdateDetails()
    {
        if ((_grid.SelectedItem as BundleRow) is not { } row)
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
            Text = "Select a package to see details.", Foreground = new SolidColorBrush(Color.Parse("#BBBBBB")),
        });
    }

    private void AddDetail(string label, string value)
    {
        var row = new DockPanel { LastChildFill = true };
        var key = new TextBlock { Text = label + "  ", Foreground = DevolutionsPalette.BrandBrush, Width = 10, };
        DockPanel.SetDock(key, Dock.Left);
        row.Children.Add(key);
        row.Children.Add(new TextBlock
        {
            Text = value, Foreground = new SolidColorBrush(Color.Parse("#BBBBBB")), TextWrapping = TextWrapping.Wrap
        });
        _details.Children.Add(row);
    }

    private static BundleRow BuildRow(IPackage p)
    {
        bool compatible = p is ImportedPackage;
        return new BundleRow(
            p,
            compatible,
            p.Name ?? string.Empty,
            p.Id ?? string.Empty,
            p.VersionString ?? string.Empty,
            p.Source.Name ?? string.Empty,
            compatible ? "ready" : "blocked");
    }

    private static IReadOnlyList<string> BuildSuggestions(IEnumerable<IPackage> packages)
    {
        return packages
            .SelectMany(p => new[]
            {
                p.Name ?? string.Empty, p.Id ?? string.Empty, p.Source.Name ?? string.Empty,
                p.Manager.DisplayName ?? string.Empty,
            })
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Take(150)
            .ToArray();
    }
}
