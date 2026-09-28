using NUnit.Framework;
using UniGetUI.Tui.Infrastructure;

namespace UniGetUI.Tui.Tests;

[TestFixture]
[NonParallelizable]
[Explicit("Diagnostic: prints the rendered screens of every page")]
internal sealed class ScreenDumpTests : TuiE2ETestBase
{
    [Test]
    public async Task DumpPages()
    {
        foreach (string page in TuiPageIds.All)
        {
            await ResetAsync(page);
            await Task.Delay(800);
            await UITest.WaitRendered();
            TestContext.Out.WriteLine($"===== {page} =====");
            TestContext.Out.WriteLine(await ScreenAsync());
        }
    }
}
