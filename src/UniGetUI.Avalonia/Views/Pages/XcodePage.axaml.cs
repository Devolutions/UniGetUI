using Avalonia.Controls;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels.Pages;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class XcodePage : UserControl
{
    public XcodePage(XcodeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        LogText.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) KeepLogAtEnd();
        };
    }

    /// <summary>Follows what xcodes writes, unless the log was scrolled up to be read.</summary>
    private void KeepLogAtEnd()
    {
        bool atEnd = LogScroll.Offset.Y + LogScroll.Viewport.Height >= LogScroll.Extent.Height - 8;
        if (atEnd)
            Dispatcher.UIThread.Post(LogScroll.ScrollToEnd, DispatcherPriority.Loaded);
    }
}
