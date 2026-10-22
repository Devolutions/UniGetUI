using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(UniGetUI.Tests.AvaloniaTestApp))]

namespace UniGetUI.Tests;

/// <summary>
/// Headless app for the UI tests. It renders text with Skia, as the real app does: the default
/// headless text stub cannot lay out line breaks, so selection and hit-testing across lines would be wrong.
/// </summary>
public sealed class AvaloniaTestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<AvaloniaTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public override void Initialize() => Styles.Add(new FluentTheme());
}
