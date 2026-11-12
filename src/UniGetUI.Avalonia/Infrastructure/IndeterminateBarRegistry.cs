using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia.Infrastructure;

internal interface IIndeterminateBarLayer : IDisposable
{
    void Show(IndeterminateBarSnapshot snapshot, double scale);

    void Hide();
}

internal interface IIndeterminateBarSurface : IDisposable
{
    IIndeterminateBarLayer CreateLayer();
}

internal sealed class IndeterminateBarRegistry(Func<Window, IIndeterminateBarSurface?> createSurface)
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMilliseconds(250);

    private readonly Dictionary<ProgressBar, Entry> _entries = [];
    private readonly Dictionary<Window, IIndeterminateBarSurface> _surfaces = [];
    private DispatcherTimer? _timer;
    private bool _syncPosted;

    public bool IsDisabled { get; private set; }

    public bool IsPolling => _timer?.IsEnabled == true;

    public bool IsRegistered(ProgressBar bar) => _entries.ContainsKey(bar);

    public void Register(ProgressBar bar)
    {
        if (IsDisabled || _entries.ContainsKey(bar) || TopLevel.GetTopLevel(bar) is not Window window)
            return;

        try
        {
            if (GetOrCreateSurface(window) is not { } surface)
                return;

            var entry = new Entry(bar, window, surface.CreateLayer());
            _entries[bar] = entry;
            entry.UpdateClass();
        }
        catch (Exception ex)
        {
            Fail(ex);
            return;
        }

        PostSync();
    }

    public void Unregister(ProgressBar bar)
    {
        if (!_entries.Remove(bar, out Entry? entry))
            return;

        entry.Release();
        if (_entries.Count == 0)
            _timer?.Stop();
    }

    public void Refresh()
    {
        if (_entries.Count > 0)
            PostSync();
    }

    public void NotifyAppearanceChanged(Visual? changed)
    {
        if (IsDisabled || _syncPosted)
            return;

        foreach (ProgressBar bar in _entries.Keys)
        {
            if (bar.IsIndeterminate && (changed is null || changed == bar || changed.IsVisualAncestorOf(bar)))
            {
                PostSync();
                return;
            }
        }
    }

    public void SyncAll()
    {
        if (IsDisabled)
            return;

        bool keepPolling = false;
        try
        {
            foreach (Entry entry in _entries.Values.ToList())
                keepPolling |= entry.Sync();
        }
        catch (Exception ex)
        {
            Fail(ex);
            return;
        }

        if (!keepPolling)
        {
            _timer?.Stop();
            return;
        }

        _timer ??= new DispatcherTimer(SyncInterval, DispatcherPriority.Background, (_, _) => SyncAll());
        if (!_timer.IsEnabled)
            _timer.Start();
    }

    private IIndeterminateBarSurface? GetOrCreateSurface(Window window)
    {
        if (_surfaces.TryGetValue(window, out IIndeterminateBarSurface? existing))
            return existing;

        if (createSurface(window) is not { } surface)
            return null;

        _surfaces[window] = surface;
        window.LayoutUpdated += OnLayoutUpdated;
        window.PropertyChanged += OnWindowPropertyChanged;
        window.Closed += OnWindowClosed;
        return surface;
    }

    private void Fail(Exception ex)
    {
        if (IsDisabled)
            return;
        IsDisabled = true;
        _timer?.Stop();

        Logger.Warn("Could not animate progress bars in the system compositor; using the Avalonia animation instead");
        Logger.Warn(ex);

        foreach (Entry entry in _entries.Values.ToList())
            entry.Release();
        _entries.Clear();

        foreach ((Window window, IIndeterminateBarSurface surface) in _surfaces.ToList())
            ReleaseSurface(window, surface);
        _surfaces.Clear();
    }

    private void ReleaseSurface(Window window, IIndeterminateBarSurface surface)
    {
        window.LayoutUpdated -= OnLayoutUpdated;
        window.PropertyChanged -= OnWindowPropertyChanged;
        window.Closed -= OnWindowClosed;
        DisposeQuietly(surface);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => PostSync();

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == Visual.IsVisibleProperty)
            PostSync();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window || !_surfaces.Remove(window, out IIndeterminateBarSurface? surface))
            return;

        foreach (ProgressBar bar in _entries.Where(pair => pair.Value.Window == window).Select(pair => pair.Key).ToList())
            Unregister(bar);
        ReleaseSurface(window, surface);
    }

    private void PostSync()
    {
        if (_syncPosted || IsDisabled)
            return;
        _syncPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _syncPosted = false;
            SyncAll();
        }, DispatcherPriority.Background);
    }

    private static void DisposeQuietly(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex);
        }
    }

    private sealed class Entry(ProgressBar bar, Window window, IIndeterminateBarLayer layer)
    {
        private IndeterminateBarSnapshot? _shown;
        private double _shownScale;

        public Window Window { get; } = window;

        public void UpdateClass() => SetClass(IndeterminateBarSnapshot.IsSupported(bar));

        public bool Sync()
        {
            bool supported = IndeterminateBarSnapshot.IsSupported(bar);
            if (!supported || Window.WindowState == WindowState.Minimized || !Window.IsVisible)
            {
                SetClass(supported);
                Hide();
                return false;
            }

            IndeterminateBarSnapshot? snapshot = IndeterminateBarSnapshot.Capture(bar, Window, out BarCoverage coverage);
            SetClass(coverage != BarCoverage.Partial);
            if (snapshot is null)
            {
                Hide();
                return bar.IsIndeterminate;
            }

            double scale = Window.RenderScaling;
            if (snapshot != _shown || scale != _shownScale)
            {
                layer.Show(snapshot, scale);
                _shown = snapshot;
                _shownScale = scale;
            }

            return true;
        }

        public void Release()
        {
            SetClass(false);
            DisposeQuietly(layer);
        }

        private void Hide()
        {
            if (_shown is null)
                return;
            layer.Hide();
            _shown = null;
        }

        private void SetClass(bool handedOff)
        {
            if (handedOff)
            {
                if (!bar.Classes.Contains(IndeterminateProgressBars.CompositorClass))
                    bar.Classes.Add(IndeterminateProgressBars.CompositorClass);
            }
            else
            {
                bar.Classes.Remove(IndeterminateProgressBars.CompositorClass);
            }
        }
    }
}
