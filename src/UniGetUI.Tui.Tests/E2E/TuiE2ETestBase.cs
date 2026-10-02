using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Consolonia.Core.Drawing.PixelBufferImplementation;
using Consolonia.Fonts;
using Consolonia.NUnit;
using NUnit.Framework;
using UniGetUI.PackageEngine.Operations.History;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.Tui.FakeData;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Views;
using UniGetUI.Tui.Views.Dialogs;

namespace UniGetUI.Tui.Tests;

/// <summary>
/// Base for end-to-end tests: one real TUI instance (shared by the whole run, as Consolonia.NUnit
/// requires) rendered into an in-memory console and driven only through keyboard input. Assertions read
/// the rendered screen, and where an effect lives outside the screen, the fake system state file.
/// </summary>
[NonParallelizable]
internal abstract class TuiE2ETestBase : ConsoloniaAppTestBase<App>
{
    protected static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    protected TuiE2ETestBase() : base(new PixelBufferSize(160, 50))
    {
        Args = [];
    }

    protected override AppBuilder CreateAppBuilder() => base.CreateAppBuilder().WithConsoleFonts();

    internal static MainWindow Window
        => (MainWindow)((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).MainWindow!;

    internal static FakeDataEnvironment Env => FakeDataEnvironment.Current!;

    protected static Task<T> OnUi<T>(Func<T> func) => Dispatcher.UIThread.InvokeAsync(func).GetTask();

    protected static Task OnUi(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    protected static Task<string> ScreenAsync() => OnUi(() => UITest.PixelBuffer.PrintBuffer());

    /// <summary>Brings the app back to a known state: fresh fake system, no dialogs/operations, given page.</summary>
    protected static async Task ResetAsync(string pageId)
    {
        await OnUi(() =>
        {
            TuiModal.CloseAll();
            TuiOperationRegistry.Reset();
        });
        await WaitUntilAsync(AbstractOperationQueueEmpty, "operation queue to drain");
        Env.ResetState();
        OperationHistoryStore.Clear();
        foreach (var (key, _) in UniGetUI.PackageEngine.Classes.Packages.Classes.IgnoredUpdatesDatabase.GetDatabase())
            UniGetUI.PackageEngine.Classes.Packages.Classes.IgnoredUpdatesDatabase.Remove(key);
        UpgradablePackagesLoader.Instance.IgnoredPackages.Clear();
        await OnUi(() =>
        {
            TuiBundleService.Clear();
            DiscoverablePackagesLoader.Instance.ClearPackages(emitFinishSignal: false);
        });
        await InstalledPackagesLoader.Instance.ReloadPackages();
        await UpgradablePackagesLoader.Instance.ReloadPackages();
        await OnUi(() =>
        {
            Window.ResetPages();
            Window.NavigateTo(pageId);
        });
        await UITest.WaitRendered();
    }

    private static bool AbstractOperationQueueEmpty() => UniGetUI.PackageOperations.AbstractOperation.OperationQueue.Count == 0;

    protected static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (DateTime.UtcNow < deadline)
        {
            if (await OnUi(condition)) return;
            await Task.Delay(100);
        }

        NUnit.Framework.Assert.Fail($"Timed out waiting for {what}.\n{await ScreenAsync()}");
    }

    /// <summary>Waits until every text is visible on screen.</summary>
    protected static async Task WaitForTextAsync(params string[] texts)
    {
        var deadline = DateTime.UtcNow + DefaultTimeout;
        string screen = "";
        while (DateTime.UtcNow < deadline)
        {
            await UITest.WaitRendered();
            screen = await ScreenAsync();
            if (texts.All(t => screen.Contains(t, StringComparison.Ordinal))) return;
            await Task.Delay(150);
        }

        NUnit.Framework.Assert.Fail($"Timed out waiting for [{string.Join(", ", texts.Where(t => !screen.Contains(t, StringComparison.Ordinal)))}] on screen:\n{screen}");
    }

    protected static async Task WaitForNoTextAsync(params string[] texts)
    {
        var deadline = DateTime.UtcNow + DefaultTimeout;
        string screen = "";
        while (DateTime.UtcNow < deadline)
        {
            await UITest.WaitRendered();
            screen = await ScreenAsync();
            if (!texts.Any(t => screen.Contains(t, StringComparison.Ordinal))) return;
            await Task.Delay(150);
        }

        NUnit.Framework.Assert.Fail($"Timed out waiting for [{string.Join(", ", texts)}] to disappear from screen:\n{screen}");
    }

    protected static Task Key(Key key, RawModifiers modifiers = RawModifiers.None)
        => UITest.KeyInput(key, (Avalonia.Input.RawInputModifiers)modifiers);

    protected static Task Type(string text) => UITest.StringInput(text);

    /// <summary>Puts keyboard focus back on the page's main control (what Esc does from a field).</summary>
    protected static async Task EnterPage()
    {
        await OnUi(Window.FocusContent);
        await UITest.WaitRendered();
    }

    protected static FakeState State => FakeStateStore.Read(Env.StatePath);

    protected static string? InstalledVersion(string manager, string id)
        => State.Installed.FirstOrDefault(p => p.Manager == manager && p.Id == id)?.Version;

    protected static async Task WaitForOperationsToFinishAsync()
    {
        await Task.Delay(300);
        await WaitUntilAsync(() => TuiOperationRegistry.ActiveCount == 0, "operations to finish", TimeSpan.FromSeconds(40));
        await UITest.WaitRendered();
    }

    /// <summary>A text description of the focused control (its content / text), for keyboard navigation.</summary>
    protected static Task<string> FocusedTextAsync() => OnUi(() =>
    {
        var focused = Avalonia.Controls.TopLevel.GetTopLevel(Window)?.FocusManager?.GetFocusedElement();
        return focused switch
        {
            Avalonia.Controls.ContentControl c => c.Content?.ToString() ?? "",
            Avalonia.Controls.TextBox t => "textbox:" + (t.Text ?? ""),
            null => "",
            _ => focused.GetType().Name,
        };
    });

    /// <summary>Presses <paramref name="key"/> until the focused control's text contains <paramref name="label"/>.</summary>
    protected static async Task FocusFieldAsync(string label, Key key = Avalonia.Input.Key.Down, int max = 80)
    {
        for (int i = 0; i < max; i++)
        {
            if ((await FocusedTextAsync()).Contains(label, StringComparison.Ordinal)) return;
            await Key(key);
        }

        // Not found in that direction: sweep back the other way (Up/Down only; Tab already cycles).
        Key opposite = key switch { Avalonia.Input.Key.Up => Avalonia.Input.Key.Down, Avalonia.Input.Key.Down => Avalonia.Input.Key.Up, _ => key };
        for (int i = 0; i < max && opposite != key; i++)
        {
            if ((await FocusedTextAsync()).Contains(label, StringComparison.Ordinal)) return;
            await Key(opposite);
        }

        NUnit.Framework.Assert.Fail($"Could not move focus to \"{label}\" (focused: {await FocusedTextAsync()}).\n{await ScreenAsync()}");
    }

    /// <summary>Tabs to the dialog button labelled <paramref name="label"/> and presses Enter.</summary>
    protected static async Task PressButtonAsync(string label)
    {
        await FocusFieldAsync(label, Avalonia.Input.Key.Tab, 60);
        await Key(Avalonia.Input.Key.Enter);
    }

    /// <summary>In the open menu, moves to the item starting with <paramref name="label"/> and runs it.</summary>
    protected static async Task RunMenuItemAsync(string label)
    {
        await WaitUntilAsync(() => Window.SelectedMenuActionLabel is not null, "an open menu");
        for (int i = 0; i < 40; i++)
        {
            if ((await OnUi(() => Window.SelectedMenuActionLabel))?.StartsWith(label, StringComparison.Ordinal) == true)
            {
                await Key(Avalonia.Input.Key.Enter);
                return;
            }

            await Key(Avalonia.Input.Key.Down);
        }

        NUnit.Framework.Assert.Fail($"No menu item starting with \"{label}\".\n{await ScreenAsync()}");
    }

    /// <summary>In an open choice list, moves to the entry starting with <paramref name="label"/> and picks it.</summary>
    protected static async Task ChooseAsync(string label)
    {
        await WaitUntilAsync(() => TuiModal.Top is ChoiceDialog, "a choice list");
        await WaitForDialogAsync(await OnUi(() => TuiModal.Top!.Title));
        for (int i = 0; i < 80; i++)
        {
            string selected = await OnUi(() => (TuiModal.Top as ChoiceDialog)?.SelectedLabel ?? "");
            if (selected.StartsWith(label, StringComparison.Ordinal))
            {
                await Key(Avalonia.Input.Key.Enter);
                return;
            }

            await Key(Avalonia.Input.Key.Down);
        }

        NUnit.Framework.Assert.Fail($"No choice starting with \"{label}\".\n{await ScreenAsync()}");
    }

    /// <summary>Replaces the text of the focused text box (Ctrl+A then typing, like a user).</summary>
    protected static async Task ReplaceTextAsync(string text)
    {
        await Key(Avalonia.Input.Key.A, RawModifiers.Control);
        await Key(Avalonia.Input.Key.Back);
        await Type(text);
    }

    protected static async Task WaitForDialogAsync(string title)
    {
        await WaitUntilAsync(() => TuiModal.Top?.Title.Contains(title, StringComparison.Ordinal) == true, $"dialog \"{title}\"");
        await WaitUntilAsync(() => Avalonia.Controls.TopLevel.GetTopLevel(Window)?.FocusManager?.GetFocusedElement() is Avalonia.Visual v
                                   && Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(TuiModal.Top!, v), $"focus inside \"{title}\"");
        await UITest.WaitRendered();
    }

    protected static Task WaitForNoDialogAsync() => WaitUntilAsync(() => !TuiModal.IsOpen, "dialogs to close");

    /// <summary>Filters the current package page to <paramref name="query"/> and moves focus into the list.</summary>
    protected static async Task FilterPackagesAsync(string query)
    {
        await Key(Avalonia.Input.Key.F, RawModifiers.Control);
        await ReplaceTextAsync(query);
        await Key(Avalonia.Input.Key.Enter);
        await UITest.WaitRendered();
    }

    [Flags]
    protected enum RawModifiers
    {
        None = 0,
        Alt = 1,
        Control = 2,
        Shift = 4,
    }
}
