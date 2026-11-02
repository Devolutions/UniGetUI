using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Avalonia.Views.Pages;

public static class PackageListColumnLayout
{
    public const double FixedColumnMinWidth = 60;
    public const double DefaultFixedColumnWidth = 200;

    public static event Action? WidthsSaved;

    private static readonly ConditionalWeakTable<DataGridColumn, StrongBox<double>> _originalMinWidths = new();

    public static void Apply(DataGrid grid, bool fitToWindow)
    {
        if (fitToWindow)
        {
            grid.ClearValue(DataGrid.HorizontalScrollBarVisibilityProperty);
            grid.ClearValue(DataGrid.FrozenColumnCountProperty);
        }
        else
        {
            if (grid.Columns.FirstOrDefault(c => c.Tag is not string) is { } selector)
                selector.DisplayIndex = 0;
            grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            grid.FrozenColumnCount = 1;
        }

        IReadOnlyDictionary<string, int>? saved = fitToWindow ? null : Settings.GetDictionary<string, int>(Settings.K.PackageListColumnWidths);
        foreach (var col in grid.Columns)
        {
            if (col.Tag is not string tag) continue;
            double originalMinWidth = _originalMinWidths.GetValue(col, c => new StrongBox<double>(c.MinWidth)).Value;
            if (fitToWindow)
            {
                col.MinWidth = originalMinWidth;
                col.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            }
            else
            {
                col.MinWidth = FixedColumnMinWidth;
                double width = saved is not null && saved.TryGetValue(tag, out int w) && w > 0 ? w : DefaultFixedColumnWidth;
                col.Width = new DataGridLength(Math.Max(width, FixedColumnMinWidth));
            }
        }
    }

    public static void SeedFrom(DataGrid grid)
    {
        var widths = new Dictionary<string, int>();
        if (grid.IsEffectivelyVisible && grid.Bounds.Width > 0)
            CollectVisibleWidths(grid, widths);
        Settings.SetDictionary(Settings.K.PackageListColumnWidths, widths);
        WidthsSaved?.Invoke();
    }

    public static void SaveFrom(DataGrid grid)
    {
        var widths = new Dictionary<string, int>();
        if (Settings.GetDictionary<string, int>(Settings.K.PackageListColumnWidths) is { } saved)
            foreach (var (tag, width) in saved)
                widths[tag] = width;
        if (!CollectVisibleWidths(grid, widths)) return;
        Settings.SetDictionary(Settings.K.PackageListColumnWidths, widths);
        WidthsSaved?.Invoke();
    }

    private static bool CollectVisibleWidths(DataGrid grid, Dictionary<string, int> widths)
    {
        bool changed = false;
        foreach (var col in grid.Columns)
        {
            if (col.Tag is not string tag || !col.IsVisible || col.ActualWidth <= 0) continue;
            int width = (int)Math.Round(Math.Max(col.ActualWidth, FixedColumnMinWidth));
            if (widths.TryGetValue(tag, out int old) && old == width) continue;
            widths[tag] = width;
            changed = true;
        }
        return changed;
    }
}
