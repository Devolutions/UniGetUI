using Avalonia;
using Avalonia.Input;
using Consolonia.Core.Drawing.PixelBufferImplementation;
using Consolonia.Fonts;
using Consolonia.NUnit;
using NUnit.Framework;
using UniGetUI.Tui;

namespace UniGetUI.Tui.Tests;

[TestFixture]
[NonParallelizable]
internal sealed class TuiShellNUnitTests : ConsoloniaAppTestBase<App>
{
    public TuiShellNUnitTests()
        : base(new PixelBufferSize(100, 32))
    {
        Args = [];
    }

    protected override AppBuilder CreateAppBuilder()
    {
        return base.CreateAppBuilder()
            .WithConsoleFonts();
    }

    [Test]
    public async Task ShellRendersNavigationFooterAndInlineMenu()
    {
        await UITest.AssertHasText("UniGetUI", "Discover", "Installed", "F10 menu");

        await UITest.KeyInput(Key.F10);
        await UITest.AssertHasText("Open bundle", "Exit");

        await UITest.KeyInput(Key.Escape);
        await UITest.AssertHasNoText("Open bundle");

        await UITest.KeyInput(Key.D9);
        await UITest.AssertHasText("About UniGetUI TUI", "Terminal Diagnostics", "Capabilities");
    }
}
