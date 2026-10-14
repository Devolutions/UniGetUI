using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Controls;

public sealed class CatalogSelector : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);

    private readonly CatalogEditorViewModel _document;
    private bool _syncing;

    public CatalogSelector(CatalogEditorViewModel document, Func<CatalogEditorCatalog, Task>? removeCatalog = null)
    {
        _document = document;
        MinWidth = 180;
        MaxWidth = 300;
        VerticalAlignment = VerticalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        PlaceholderText = CoreTools.Translate("Select a catalog");
        AutomationProperties.SetName(this, CoreTools.Translate("Catalog"));
        ItemsSource = document.Catalogs;
        SelectionBoxItemTemplate = new FuncDataTemplate<CatalogEditorCatalog>(
            (catalog, _) => CreateLabel(catalog), supportsRecycling: false);
        ItemTemplate = removeCatalog is null
            ? SelectionBoxItemTemplate
            : new FuncDataTemplate<CatalogEditorCatalog>(
                (catalog, _) => CreateRemovableItem(catalog, removeCatalog), supportsRecycling: false);
        SelectionChanged += (_, _) =>
        {
            if (!_syncing && document.CanEdit)
                document.SelectedCatalog = SelectedItem as CatalogEditorCatalog;
        };
        document.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CatalogEditorViewModel.SelectedCatalog)
                or nameof(CatalogEditorViewModel.CanEdit))
                SyncSelection();
        };
        SyncSelection();
    }

    private static TextBlock CreateLabel(CatalogEditorCatalog? catalog)
    {
        var label = new TextBlock { Text = catalog?.Name, VerticalAlignment = VerticalAlignment.Center };
        if (catalog is not null)
        {
            void UpdateName(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(CatalogEditorCatalog.Name))
                    label.Text = catalog.Name;
            }
            label.AttachedToVisualTree += (_, _) =>
            {
                label.Text = catalog.Name;
                catalog.PropertyChanged += UpdateName;
            };
            label.DetachedFromVisualTree += (_, _) => catalog.PropertyChanged -= UpdateName;
        }
        return label;
    }

    private Control CreateRemovableItem(CatalogEditorCatalog? catalog, Func<CatalogEditorCatalog, Task> removeCatalog)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(CreateLabel(catalog));
        if (catalog is null) return grid;

        var remove = new Button
        {
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            IsEnabled = _document.CanEdit,
            Content = new SvgIcon { Path = "avares://UniGetUI/Assets/Symbols/cross.svg", Width = 12, Height = 12 },
        };
        // Keep the name in the tooltip and accessible name current when the catalog is renamed.
        void UpdateTip()
        {
            string tip = CoreTools.Translate("Remove catalog {0}", catalog.Name);
            AutomationProperties.SetName(remove, tip);
            ToolTip.SetTip(remove, tip);
        }
        void OnCatalogChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CatalogEditorCatalog.Name)) UpdateTip();
        }
        remove.AttachedToVisualTree += (_, _) =>
        {
            UpdateTip();
            catalog.PropertyChanged += OnCatalogChanged;
        };
        remove.DetachedFromVisualTree += (_, _) => catalog.PropertyChanged -= OnCatalogChanged;
        UpdateTip();
        Grid.SetColumn(remove, 1);
        remove.Click += async (_, e) =>
        {
            e.Handled = true;
            // Keep the list available so several catalogs can be removed in a row; it only steps
            // aside for the confirmation that a catalog with packages asks for.
            bool confirms = catalog.Packages.Count > 0;
            if (confirms) IsDropDownOpen = false;
            await removeCatalog(catalog);
            // Cancelled: nothing changed, so leave the list closed as the dialog left it.
            if (confirms && _document.Catalogs.Contains(catalog)) return;
            if (_document.Catalogs.Count == 0)
            {
                IsDropDownOpen = false;
                return;
            }
            // The open popup keeps shrunken rows after a removal; reopening it lays them out afresh.
            IsDropDownOpen = false;
            Dispatcher.UIThread.Post(() => IsDropDownOpen = true);
        };
        grid.Children.Add(remove);
        return grid;
    }

    private void SyncSelection()
    {
        _syncing = true;
        SelectedItem = _document.SelectedCatalog;
        IsEnabled = _document.CanEdit;
        _syncing = false;
    }

    public void ShowFlyoutAt(Control anchor)
    {
        var flyout = new MenuFlyout();
        foreach (var catalog in _document.Catalogs)
        {
            var item = new MenuItem { Header = catalog.Name, IsEnabled = _document.CanEdit };
            item.Click += (_, _) => _document.SelectedCatalog = catalog;
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
    }
}
