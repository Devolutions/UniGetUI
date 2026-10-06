using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Consolonia;
using Consolonia.Controls.DataGrid;
using Consolonia.ManagedWindows;
using Consolonia.Themes;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;
using UniGetUI.Tui.Views;

namespace UniGetUI.Tui;

/// <summary>
/// Code-only Consolonia application (no AXAML) so we never touch the Avalonia 12 compiled-binding
/// pipeline. Picks the truecolor Modern base on RGB-capable terminals and the 16-colour TurboVision
/// base otherwise, then applies the selected TUI colour theme on top (see <see cref="TuiThemes"/>).
/// </summary>
internal sealed class App : Application
{
    static App()
    {
        Thread.CurrentThread.CurrentUICulture = Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        bool rgb = ApplicationLifetime is ConsoloniaLifetime { } lifetime && lifetime.IsRgbColorMode();

        // Use the exact theme types proven to render at runtime in the Consolonia fork (ModernTheme /
        // TurboVisionTheme). The *Dark variants ship no precompiled XAML in the fork and throw at
        // construction. ModernTheme is the truecolor base; TurboVision is the 16-colour fallback.
        Styles.Add(rgb ? new ModernTheme() : new TurboVisionTheme());
        Styles.Add(new AutoDataGridStyles());
        Styles.Add(new AutoManagedWindowStyles());

        // Colours come from the TUI theme (--theme for this session, else the saved one), which overrides
        // Consolonia's theme resources and picks the matching Light/Dark variant.
        TuiPalette.Apply(TuiStartup.Current?.Theme ?? TuiPalette.Saved);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
