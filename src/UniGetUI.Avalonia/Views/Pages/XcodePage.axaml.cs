using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.ViewModels.Pages;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class XcodePage : UserControl
{
    private XcodeBrowserItemViewModel? _pendingScrollTarget;

    public XcodePage(XcodeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindPoliteStatus(VersionListStatus);
        BindPoliteStatus(CatalogStatus);
        BindPoliteStatus(SimulatorCatalogStatus);

        LogText.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) KeepLogAtEnd();
        };

        PropertyChangedEventHandler onViewModelPropertyChanged = (_, e) =>
        {
            if (e.PropertyName is nameof(XcodeViewModel.SelectedBrowserItem) or nameof(XcodeViewModel.GroupVersions))
                RequestScrollSelectedIntoView(viewModel.SelectedBrowserItem);
        };
        Action onScrollRequested = () => RequestScrollSelectedIntoView(viewModel.SelectedBrowserItem);

        VersionListRegion.SizeChanged += (_, _) =>
        {
            if (_pendingScrollTarget is { } target && ReferenceEquals(target, viewModel.SelectedBrowserItem))
            {
                bool clamped = !double.IsInfinity(VersionBrowser.MaxHeight) && VersionListRegion.Bounds.Height > 0;
                if (clamped)
                    _pendingScrollTarget = null;
                ScrollSelectedVersionIntoView(target);
            }
        };

        viewModel.PropertyChanged += onViewModelPropertyChanged;
        viewModel.ScrollToSelectedRequested += onScrollRequested;
        AttachedToVisualTree += (_, _) =>
        {
            viewModel.PropertyChanged -= onViewModelPropertyChanged;
            viewModel.PropertyChanged += onViewModelPropertyChanged;
            viewModel.ScrollToSelectedRequested -= onScrollRequested;
            viewModel.ScrollToSelectedRequested += onScrollRequested;
            RequestScrollSelectedIntoView(viewModel.SelectedBrowserItem);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            viewModel.PropertyChanged -= onViewModelPropertyChanged;
            viewModel.ScrollToSelectedRequested -= onScrollRequested;
        };
    }

    private void RequestScrollSelectedIntoView(XcodeBrowserItemViewModel? selected)
    {
        _pendingScrollTarget = selected;
        if (selected is not null && !double.IsInfinity(VersionBrowser.MaxHeight) && VersionListRegion.Bounds.Height > 0)
            _pendingScrollTarget = null;
        ScrollSelectedVersionIntoView(selected);
    }

    private void ScrollSelectedVersionIntoView(XcodeBrowserItemViewModel? selected)
    {
        if (selected is null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (VersionList.IsVisible && ReferenceEquals(VersionList.SelectedItem, selected))
            {
                VersionList.ScrollIntoView(selected);
                return;
            }

            if (GroupedVersionList.IsVisible && GroupedVersionList.SelectedItem is XcodeBrowserNodeViewModel node
                && ReferenceEquals(node.Item, selected))
            {
                TreeViewItem? container = GroupedVersionList.GetVisualDescendants()
                    .OfType<TreeViewItem>()
                    .FirstOrDefault(item => ReferenceEquals(item.DataContext, node));
                container?.BringIntoView();
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Keeps polite status labels screen-reader accessible without triggering the macOS Avalonia.Native
    /// crash inside raiseLiveRegionChanged when a live region's AutomationProperties.Name becomes empty.
    /// </summary>
    private static void BindPoliteStatus(TextBlock status)
    {
        bool isDetached = false;

        void Sync()
        {
            string? text = status.Text;
            if (isDetached || string.IsNullOrWhiteSpace(text))
            {
                AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Off);
                AutomationProperties.SetName(status, string.Empty);
            }
            else
            {
                AutomationProperties.SetName(status, text);
                AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            }
        }

        status.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty)
                Sync();
        };
        status.AttachedToVisualTree += (_, _) =>
        {
            isDetached = false;
            Sync();
        };
        status.DetachedFromVisualTree += (_, _) =>
        {
            isDetached = true;
            Sync();
        };
        Sync();
    }

    /// <summary>Follows the operation log, unless the log was scrolled up to be read.</summary>
    private void KeepLogAtEnd()
    {
        bool atEnd = LogScroll.Offset.Y + LogScroll.Viewport.Height >= LogScroll.Extent.Height - 8;
        if (atEnd)
            Dispatcher.UIThread.Post(LogScroll.ScrollToEnd, DispatcherPriority.Loaded);
    }
}

public sealed class XcodePageWidthConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [double viewportWidth, double minWidth, Thickness pageMargin]
            || double.IsNaN(viewportWidth) || double.IsInfinity(viewportWidth) || viewportWidth <= 0)
        {
            return AvaloniaProperty.UnsetValue;
        }

        // Fit the viewport width so wrapped text never causes horizontal overflow in normal windows,
        // while clamping to MinWidth so tiny windows remain horizontally scrollable.
        double availableWidth = viewportWidth - pageMargin.Left - pageMargin.Right;
        return Math.Max(minWidth, availableWidth);
    }
}

public sealed class XcodeBrowserHeightConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [
            double viewportHeight, double browserTop, double listTop, double minimumListHeight,
            Thickness pageMargin, Thickness browserMargin, Thickness borderThickness])
            return AvaloniaProperty.UnsetValue;

        // Keep the list measurable and scrollable even when the page chrome exceeds the viewport.
        double browserPadding = browserMargin.Top + browserMargin.Bottom + borderThickness.Top + borderThickness.Bottom;
        double pagePadding = pageMargin.Top + pageMargin.Bottom;
        return Math.Max(listTop + minimumListHeight + browserPadding, viewportHeight - browserTop - pagePadding);
    }
}
