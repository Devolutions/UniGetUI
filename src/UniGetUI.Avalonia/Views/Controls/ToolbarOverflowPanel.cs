using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace UniGetUI.Avalonia.Views.Controls;

public sealed class ToolbarOverflowPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ToolbarOverflowPanel, double>(nameof(Spacing), 4.0);

    static ToolbarOverflowPanel()
    {
        AffectsMeasure<ToolbarOverflowPanel>(SpacingProperty);
    }

    private readonly List<Control> _items = new();
    private readonly List<Control> _overflowedItems = new();
    private readonly Dictionary<Control, double> _naturalWidths = new();
    private double _expandedRowWidth = double.NaN;
    private bool _labelsCollapsed;

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public Control? OverflowControl { get; set; }

    public Action<bool>? LabelCollapseRequested { get; set; }

    public IReadOnlyList<Control> OverflowedItems => _overflowedItems;

    protected override Size MeasureOverride(Size availableSize)
    {
        CollectItems();

        double height = MeasureChildren(availableSize.Height);

        if (UpdateLabelState(availableSize.Width))
            height = Math.Max(height, MeasureChildren(availableSize.Height));

        ApplyOverflow(availableSize.Width);
        height = Math.Max(height, MeasureChildren(availableSize.Height));

        double used = UsedWidth();
        return new Size(double.IsInfinity(availableSize.Width) ? used : Math.Min(used, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        CollectItems();

        double x = 0;
        foreach (var item in _items)
        {
            if (!item.IsVisible)
            {
                item.Arrange(new Rect(x, 0, 0, 0));
                continue;
            }

            double itemWidth = NaturalWidthOf(item);
            item.Arrange(new Rect(x, 0, itemWidth, finalSize.Height));
            x += itemWidth + Spacing;
        }

        if (OverflowControl is { } overflow)
        {
            double overflowWidth = overflow.IsVisible ? NaturalWidthOf(overflow) : 0;
            overflow.Arrange(new Rect(x, 0, overflowWidth, overflowWidth > 0 ? finalSize.Height : 0));
        }

        return finalSize;
    }

    private void CollectItems()
    {
        _items.Clear();
        foreach (var child in Children)
        {
            if (ReferenceEquals(child, OverflowControl)) continue;
            _items.Add(child);
        }
    }

    private double MeasureChildren(double availableHeight)
    {
        double height = 0;

        foreach (var item in _items)
        {
            MeasureNaturalWidth(item, availableHeight);
            height = Math.Max(height, item.DesiredSize.Height);
        }

        if (OverflowControl is { } overflow)
        {
            MeasureNaturalWidth(overflow, availableHeight);
            height = Math.Max(height, overflow.DesiredSize.Height);
        }

        return height;
    }

    private void MeasureNaturalWidth(Control control, double availableHeight)
    {
        if (!control.IsVisible) return;

        control.Measure(new Size(double.PositiveInfinity, availableHeight));
        _naturalWidths[control] = control.DesiredSize.Width;
    }

    private double NaturalWidthOf(Control control)
        => _naturalWidths.GetValueOrDefault(control);

    private double RowWidth()
    {
        double total = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            if (i > 0) total += Spacing;
            total += NaturalWidthOf(_items[i]);
        }
        return total;
    }

    private double UsedWidth()
    {
        double total = 0;
        foreach (var item in _items)
        {
            if (!item.IsVisible) continue;
            if (total > 0) total += Spacing;
            total += NaturalWidthOf(item);
        }

        if (OverflowControl is { IsVisible: true } overflow)
        {
            if (total > 0) total += Spacing;
            total += NaturalWidthOf(overflow);
        }

        return total;
    }

    private bool UpdateLabelState(double availableWidth)
    {
        if (double.IsInfinity(availableWidth) || availableWidth <= 1 || LabelCollapseRequested is null) return false;

        double rowWidth = RowWidth();

        if (!_labelsCollapsed)
        {
            _expandedRowWidth = rowWidth;
            if (rowWidth <= availableWidth + 0.5) return false;
            ApplyLabelState(true);
            return true;
        }

        if (double.IsNaN(_expandedRowWidth) || _expandedRowWidth > availableWidth - 0.5) return false;
        ApplyLabelState(false);
        return true;
    }

    private void ApplyLabelState(bool collapsed)
    {
        _labelsCollapsed = collapsed;
        LabelCollapseRequested?.Invoke(collapsed);

        _overflowedItems.Clear();
        foreach (var item in _items)
        {
            item.IsVisible = true;
            _naturalWidths.Remove(item);
            InvalidateMeasureTree(item);
        }
    }

    private static void InvalidateMeasureTree(Control control)
    {
        control.InvalidateMeasure();
        foreach (var descendant in control.GetVisualDescendants())
        {
            if (descendant is Layoutable layoutable) layoutable.InvalidateMeasure();
        }
    }

    private void ApplyOverflow(double availableWidth)
    {
        _overflowedItems.Clear();
        int shownCount = CountFittingItems(availableWidth);

        for (int i = 0; i < _items.Count; i++)
        {
            bool shown = i < shownCount;
            _items[i].IsVisible = shown;
            if (!shown) _overflowedItems.Add(_items[i]);
        }

        if (OverflowControl is { } overflow)
            overflow.IsVisible = _overflowedItems.Exists(item => item is not Separator);
    }

    private int CountFittingItems(double availableWidth)
    {
        if (double.IsInfinity(availableWidth) || RowWidth() <= availableWidth + 0.5) return _items.Count;

        double budget = availableWidth
                        - (OverflowControl is { } overflow ? NaturalWidthOf(overflow) + Spacing : 0);
        double used = 0;
        int count = 0;

        foreach (var item in _items)
        {
            double itemWidth = NaturalWidthOf(item) + (count > 0 ? Spacing : 0);
            if (used + itemWidth > budget) break;
            used += itemWidth;
            count++;
        }

        while (count > 0 && _items[count - 1] is Separator) count--;
        return count;
    }
}
