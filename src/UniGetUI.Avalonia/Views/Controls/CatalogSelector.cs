using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Controls;

public sealed class CatalogSelector : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);

    private readonly CatalogEditorViewModel _document;
    private bool _syncing;

    public CatalogSelector(CatalogEditorViewModel document)
    {
        _document = document;
        MinWidth = 180;
        MaxWidth = 300;
        VerticalAlignment = VerticalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        PlaceholderText = CoreTools.Translate("Select a catalog");
        AutomationProperties.SetName(this, CoreTools.Translate("Catalog"));
        ItemsSource = document.Catalogs;
        ItemTemplate = new FuncDataTemplate<CatalogEditorCatalog>((catalog, _) =>
        {
            var label = new TextBlock { Text = catalog?.Name };
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
        }, supportsRecycling: false);
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
