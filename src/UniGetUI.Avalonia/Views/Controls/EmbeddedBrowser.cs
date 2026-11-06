using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Controls;

public sealed class EmbeddedBrowser : UserControl
{
    private readonly Border _host = new();
    private readonly ProgressBar _progress = new()
    {
        Height = 2,
        CornerRadius = new CornerRadius(0),
        VerticalAlignment = VerticalAlignment.Top,
        IsVisible = false,
    };
    private readonly StackPanel _fallback = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Spacing = 12,
        Margin = new Thickness(24),
        IsVisible = false,
    };
    private readonly TextBlock _fallbackMessage = new()
    {
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 420,
    };

    private UniGetUiWebView? _webView;
    private Uri? _pendingNavigation;
    private bool _adapterReady;
    private bool _disabled;
    private bool _subscribed;

    public Uri? CurrentUri { get; private set; }

    public Func<Uri?, bool>? KeepNavigationInside { get; set; }

    public bool OpenNewWindowsInBrowser { get; set; }

    public event EventHandler? NavigationCompleted;

    public EmbeddedBrowser()
    {
        AutomationProperties.SetAccessibilityView(_progress, AccessibilityView.Raw);

        string openLabel = CoreTools.Translate("Open in browser");
        var openButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = openLabel,
        };
        AutomationProperties.SetName(openButton, openLabel);
        openButton.Click += (_, _) => CoreTools.Launch((CurrentUri ?? _pendingNavigation)?.ToString());
        _fallback.Children.Add(_fallbackMessage);
        _fallback.Children.Add(openButton);

        Content = new Panel { Children = { _host, _progress, _fallback } };

        if (NativeWebViewSupport.IsAvailable)
            CreateWebView();
        else
            ShowFallback();
    }

    public void Navigate(Uri uri)
    {
        CurrentUri = uri;
        if (_disabled || _webView is null)
            return;
        if (_adapterReady)
            _webView.Navigate(uri);
        else
            _pendingNavigation = uri;
    }

    public void SetBrowserVisible(bool visible)
    {
        if (_webView is { } webView)
            webView.IsVisible = visible;
    }

    public void Shutdown()
    {
        Unsubscribe();
        _disabled = true;
        SetLoading(false);
        if (_webView is not { } webView)
            return;

        _webView = null;
        try
        {
            webView.Stop();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex);
        }
        _host.Child = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_disabled)
            return;
        if (!NativeWebViewSupport.IsAvailable)
        {
            ShowFallback();
            return;
        }
        if (!_subscribed)
        {
            NativeWebViewSupport.BecameUnavailable += ShowFallback;
            _subscribed = true;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unsubscribe();
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
            return;
        NativeWebViewSupport.BecameUnavailable -= ShowFallback;
        _subscribed = false;
    }

    private void CreateWebView()
    {
        var webView = new UniGetUiWebView();
        webView.AdapterCreated += (_, _) =>
        {
            if (_disabled)
                return;
            _adapterReady = true;
            if (_pendingNavigation is { } uri)
            {
                _pendingNavigation = null;
                webView.Navigate(uri);
            }
        };
        webView.NavigationStarted += (_, args) =>
        {
            if (_disabled)
                return;
            if (KeepNavigationInside is { } keepInside && !keepInside(args.Request))
            {
                args.Cancel = true;
                if (ReleaseNotesLinks.CanOpenInBrowser(args.Request))
                    CoreTools.Launch(args.Request!.ToString());
                return;
            }
            SetLoading(true);
        };
        webView.NewWindowRequested += (_, args) =>
        {
            if (_disabled || !OpenNewWindowsInBrowser)
                return;
            args.Handled = true;
            if (ReleaseNotesLinks.CanOpenInBrowser(args.Request))
                CoreTools.Launch(args.Request!.ToString());
        };
        webView.NavigationCompleted += (_, _) =>
        {
            if (_disabled)
                return;
            SetLoading(false);
            CurrentUri = webView.Source ?? CurrentUri;
            NavigationCompleted?.Invoke(this, EventArgs.Empty);
        };

        _webView = webView;
        _host.Child = webView;
    }

    private void SetLoading(bool loading)
    {
        _progress.IsIndeterminate = loading;
        _progress.IsVisible = loading;
    }

    private void ShowFallback()
    {
        if (_disabled)
            return;
        _disabled = true;
        _adapterReady = false;
        Unsubscribe();

        _fallbackMessage.Text = NativeWebViewSupport.UnavailableReason;
        _fallback.IsVisible = true;
        SetLoading(false);
        _host.IsVisible = false;
        _webView = null;
        _host.Child = null;
    }
}
