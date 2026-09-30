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
        var panel = new StackPanel { Spacing = 2, MinWidth = 200 };
        panel.Children.Add(new TextBlock
        {
            Text = CoreTools.Translate("Search these package managers"),
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var managers = _loader.GetSearchableManagers();
        if (managers.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = CoreTools.Translate("No package managers are available"),
                Opacity = 0.7,
            });
            return panel;
        }

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
            panel.Children.Add(box);
        }

        return new ScrollViewer
        {
            MaxHeight = 360,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel,
        };
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
