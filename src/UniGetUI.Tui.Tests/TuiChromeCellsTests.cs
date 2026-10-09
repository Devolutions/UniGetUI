using NUnit.Framework;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Views.Controls;
using NAssert = NUnit.Framework.Assert;

namespace UniGetUI.Tui.Tests;

[TestFixture, NonParallelizable]
internal sealed class TuiChromeCellsTests : TuiE2ETestBase
{
    [Test]
    public async Task EmojiAreMeasuredTheWayTheConsoleDrawsThem()
    {
        await OnUi(() =>
        {
            NAssert.That(TuiChrome.Cells("⚙"), Is.EqualTo(2));
            NAssert.That(TuiChrome.Cells("⚙ 设置"), Is.EqualTo(7));

            (_, int variant) = TuiFunctionBar.Fit([], "INFO", ["⚙⚙⚙⚙", ""], 15);
            NAssert.That(variant, Is.EqualTo(1));
        });
    }
}
