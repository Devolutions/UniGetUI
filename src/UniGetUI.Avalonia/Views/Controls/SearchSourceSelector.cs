using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;
using Shapes = Avalonia.Controls.Shapes;

namespace UniGetUI.Avalonia.Views.Controls;

public sealed class SearchSourceSelector : Button
{
    private readonly DiscoverablePackagesLoader _loader;
    private readonly TextBlock _summary;
    private readonly Flyout _flyout;
    private bool _selectionChanged;

    protected override Type StyleKeyOverride => typeof(Button);

    public TextBlock SummaryLabel => _summary;

    public event Action? SelectionCommitted;

    public SearchSourceSelector(DiscoverablePackagesLoader loader)
    {
        _loader = loader;

        _summary = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new SvgIcon
        {
            Path = "avares://UniGetUI/Assets/Symbols/Sources.svg",
            Width = 20,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(_summary);
        var chevron = new Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 4,4 L 8,0"),
            StrokeThickness = 1.5,
            StrokeJoin = PenLineJoin.Miter,
            Fill = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        };
        chevron.Bind(Shapes.Shape.StrokeProperty, this.GetObservable(ForegroundProperty));
        content.Children.Add(chevron);

        Height = 40;
        Padding = new Thickness(10, 4);
        CornerRadius = new CornerRadius(4);
        Content = content;

        _flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        _flyout.Opening += (_, _) =>
        {
            _selectionChanged = false;
            RefreshSummary();
            _flyout.Content = BuildFlyoutContent();
        };
        _flyout.Closed += (_, _) =>
        {
            if (!_selectionChanged) return;
            _selectionChanged = false;
            SelectionCommitted?.Invoke();
        };
        Flyout = _flyout;

        ToolTip.SetTip(this, CoreTools.Translate("Select which package managers to search"));
        AutomationProperties.SetName(this, CoreTools.Translate("Select which package managers to search"));

        RefreshSummary();
    }

    public void ShowFlyoutAt(Control anchor) => _flyout.ShowAt(anchor);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RefreshSummary();
    }

    private Control BuildFlyoutContent()
    {
        var root = new StackPanel { Spacing = 6, MinWidth = 220 };
        root.Children.Add(new TextBlock
        {
            Text = CoreTools.Translate("Search these package managers"),
            FontSize = 12,
            Opacity = 0.7,
        });

        var managers = _loader.GetSearchableManagers();
        if (managers.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = CoreTools.Translate("No package managers are available"),
                Opacity = 0.7,
            });
            return root;
        }

        var boxes = new List<CheckBox>();
        var list = new StackPanel { Spacing = 2 };

        foreach (IPackageManager manager in managers)
        {
            var box = new CheckBox
            {
                Content = manager.DisplayName,
                IsChecked = _loader.IsManagerSearched(manager),
            };
            box.IsCheckedChanged += (_, _) =>
            {
                _loader.SetManagerSearched(manager, box.IsChecked is true);
                _selectionChanged = true;
                RefreshSummary();
            };
            boxes.Add(box);
            list.Children.Add(box);
        }

        root.Children.Add(BuildBulkActions(boxes));
        root.Children.Add(new ScrollViewer
        {
            MaxHeight = 320,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = list,
        });

        return root;
    }

    private static Control BuildBulkActions(List<CheckBox> boxes)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 4,
        };

        var selectAll = BuildLinkButton(CoreTools.Translate("Select all"), () => SetAll(boxes, true));
        var clear = BuildLinkButton(CoreTools.Translate("Clear selection"), () => SetAll(boxes, false));

        Grid.SetColumn(selectAll, 0);
        Grid.SetColumn(clear, 1);
        grid.Children.Add(selectAll);
        grid.Children.Add(clear);
        return grid;
    }

    private static Button BuildLinkButton(string label, Action onClick)
    {
        var button = new Button
        {
            Classes = { "filter-hyperlink" },
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static void SetAll(List<CheckBox> boxes, bool selected)
    {
        foreach (CheckBox box in boxes)
            box.IsChecked = selected;
    }

    private void RefreshSummary()
    {
        var managers = _loader.GetSearchableManagers();
        var searched = managers.Where(_loader.IsManagerSearched).ToArray();

        if (searched.Length == 0)
            _summary.Text = CoreTools.Translate("No sources");
        else if (searched.Length == managers.Count)
            _summary.Text = CoreTools.Translate("All sources");
        else if (searched.Length == 1)
            _summary.Text = searched[0].DisplayName;
        else
            _summary.Text = CoreTools.Translate("{0} sources", searched.Length);
    }
}
