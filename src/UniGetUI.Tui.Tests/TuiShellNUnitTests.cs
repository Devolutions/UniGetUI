using Avalonia.Input;
using NUnit.Framework;
using UniGetUI.Tui.Infrastructure;

namespace UniGetUI.Tui.Tests;

[TestFixture]
[NonParallelizable]
internal sealed class TuiShellNUnitTests : TuiE2ETestBase
{
    [Test]
    public async Task ShellRendersTabsFakeBadgeAndMenuBar()
    {
        await ResetAsync(TuiPageIds.Discover);
        await WaitForTextAsync("UniGetUI", "[FAKE DATA]", " 1 Discover ", " 2 Updates (", " 3 Installed ", " 9 History ",
            "Alt+1-9  Pages", "F10  Menu", "Ctrl+Q  Quit");

        await Key(Avalonia.Input.Key.F10);
        await WaitForTextAsync("Search options");
        await Key(Avalonia.Input.Key.Left);
        await WaitForTextAsync("Open existing bundle", "Quit");
        await Key(Avalonia.Input.Key.Escape);
        await WaitForNoTextAsync("Open existing bundle");

        // Focus lives in the page, so a plain digit is not a page jump any more.
        await Key(Avalonia.Input.Key.D9, RawModifiers.Alt);
        await WaitForTextAsync("Operation history");
        await WaitUntilAsync(() => Window.CurrentPageId == TuiPageIds.History, "the History tab");

        // About is a dialog over the current page, not a page with its own tab.
        await Key(Avalonia.Input.Key.H, RawModifiers.Alt);
        await RunMenuItemAsync("About");
        await WaitForDialogAsync("About UniGetUI TUI");
        await WaitForTextAsync("Terminal Diagnostics", "FAKE DATA MODE");
        NUnit.Framework.Assert.That(Window.CurrentPageId, Is.EqualTo(TuiPageIds.History));
        await Key(Avalonia.Input.Key.Escape);
        await WaitForNoDialogAsync();

        // Help (F1) is a dialog too.
        await Key(Avalonia.Input.Key.F1);
        await WaitForDialogAsync("Help");
        await WaitForTextAsync("Keyboard shortcuts");
        NUnit.Framework.Assert.That(Window.CurrentPageId, Is.EqualTo(TuiPageIds.History));
        await Key(Avalonia.Input.Key.Escape);
        await WaitForNoDialogAsync();
    }

    [Test]
    public async Task DialogButtonsStayInsideTheFrame_WhenTheyDoNotFitOnOneRow()
    {
        await ResetAsync(TuiPageIds.Updates);
        await EnterPage();
        await Key(Avalonia.Input.Key.Enter);
        await WaitForDialogAsync("Package details");
        var (frameWidth, buttonRights) = await OnUi(() =>
        {
            var dialog = UniGetUI.Tui.Views.Dialogs.TuiModal.Top!;
            var frame = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.Border>().First();
            var rights = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.Button>()
                .Select(b => Avalonia.VisualExtensions.TranslatePoint(b, new Avalonia.Point(b.Bounds.Width, 0), frame)?.X ?? double.MaxValue).ToList();
            return (frame.Bounds.Width, rights);
        });
        NUnit.Framework.Assert.That(buttonRights, Has.Count.GreaterThan(3));
        NUnit.Framework.Assert.That(buttonRights, Has.All.LessThanOrEqualTo(frameWidth));
        await Key(Avalonia.Input.Key.Escape);
        await WaitForNoDialogAsync();
    }

    [Test]
    public async Task FocusStaysInThePage_AndEscReturnsFromTheFilterToTheList()
    {
        await ResetAsync(TuiPageIds.Installed);
        await WaitForTextAsync("Installed Packages");
        await WaitUntilAsync(FocusIsInPage, "focus in the page after navigating");

        await Key(Avalonia.Input.Key.F, RawModifiers.Control);
        await WaitUntilAsync(FocusedIs<Avalonia.Controls.TextBox>, "the filter box");
        await Key(Avalonia.Input.Key.Escape);
        await WaitUntilAsync(() => FocusIsInPage() && !FocusedIs<Avalonia.Controls.TextBox>(), "focus back on the list");

        // Tab walks the page's controls and never leaves the page.
        for (int i = 0; i < 6; i++)
        {
            await Key(Avalonia.Input.Key.Tab);
            NUnit.Framework.Assert.That(await OnUi(FocusIsInPage), Is.True, "Tab moved focus out of the page");
        }

        await Key(Avalonia.Input.Key.Tab, RawModifiers.Control);
        await WaitForTextAsync("Package Bundles");
        await WaitUntilAsync(FocusIsInPage, "focus in the next page");
    }

    [Test]
    public async Task ThemeCanBeSwitchedLiveFromTheViewMenu_AndIsSaved()
    {
        try
        {
            await ResetAsync(TuiPageIds.Updates);
            await WaitForTextAsync("Software Updates");
            await Key(Avalonia.Input.Key.V, RawModifiers.Alt);
            await WaitForTextAsync("Theme…");
            await RunMenuItemAsync("Theme");
            await ChooseAsync("Dracula");
            await WaitForNoDialogAsync();

            NUnit.Framework.Assert.That(await OnUi(() => UniGetUI.Tui.Theme.TuiPalette.Current.Id), Is.EqualTo("dracula"));
            NUnit.Framework.Assert.That(UniGetUI.Core.SettingsEngine.Settings.GetValue(UniGetUI.Core.SettingsEngine.Settings.K.TuiTheme), Is.EqualTo("dracula"));
            NUnit.Framework.Assert.That(await OnUi(() => UniGetUI.Tui.Theme.TuiPalette.Background.Color),
                Is.EqualTo(UniGetUI.Tui.Theme.TuiThemes.Dracula.Background));

            // The Settings page shows (and can change) the same theme.
            await OnUi(() => Window.NavigateTo(TuiPageIds.Settings));
            await OnUi(() => Window.GetPage<UniGetUI.Tui.Views.Pages.SettingsPage>(TuiPageIds.Settings)
                .ShowSection(UniGetUI.Tui.Views.Pages.SettingsPage.Section.Interface));
            await WaitForTextAsync("Theme: ‹ Dracula ›");
        }
        finally
        {
            await OnUi(() => UniGetUI.Tui.Theme.TuiPalette.Apply(UniGetUI.Tui.Theme.TuiThemes.Default, save: true));
        }
    }

    private static Avalonia.Input.IInputElement? Focused()
        => Avalonia.Controls.TopLevel.GetTopLevel(Window)?.FocusManager?.GetFocusedElement();

    private static bool FocusedIs<T>() => Focused() is T;

    private static bool FocusIsInPage()
        => Focused() is Avalonia.Visual v && Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(Window.CurrentPage, v);

    [Test]
    public async Task AltLetterOpensMenus_AndMenuItemsHaveNoAccessLetters()
    {
        await ResetAsync(TuiPageIds.Discover);
        await WaitForTextAsync("F10  Menu");

        await Key(Avalonia.Input.Key.O, RawModifiers.Alt);
        await WaitForTextAsync("Retry failed operations", "Clear finished operations");

        // Alt+letter switches menus while one is open.
        await Key(Avalonia.Input.Key.F, RawModifiers.Alt);
        await WaitForTextAsync("Open existing bundle", "Quit");
        await WaitForNoTextAsync("Retry failed operations");

        // Only the menu bar headers carry an underlined access letter; the drop-down items do not.
        NUnit.Framework.Assert.That(await OnUi(UnderlinedTextCount), Is.EqualTo(5));

        // A plain letter inside a menu does nothing (Q would be "Quit" if items had access keys).
        await Key(Avalonia.Input.Key.Q);
        await Key(Avalonia.Input.Key.N);
        await WaitForTextAsync("Open existing bundle", "Quit");

        await Key(Avalonia.Input.Key.H, RawModifiers.Alt);
        await RunMenuItemAsync("About");
        await WaitForDialogAsync("About UniGetUI TUI");
        await WaitForNoTextAsync("Open existing bundle");
        await Key(Avalonia.Input.Key.Escape);
        await WaitForNoDialogAsync();
    }

    private static int UnderlinedTextCount()
        => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(Window).OfType<Avalonia.Controls.TextBlock>()
            .Count(t => t.IsEffectivelyVisible && t.TextDecorations is { Count: > 0 });
}
