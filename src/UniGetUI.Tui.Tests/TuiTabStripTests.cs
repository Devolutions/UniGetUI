using NUnit.Framework;
using UniGetUI.Tui.Views;
using static UniGetUI.Tui.Views.MainWindow;
using NAssert = NUnit.Framework.Assert;

namespace UniGetUI.Tui.Tests;

[TestFixture]
internal sealed class TuiTabStripTests
{
    private static readonly string[] Names = ["Discover", "Updates", "操作", "设置", "历史记录"];

    private static string Label(int i, TabDensity density) => density switch
    {
        TabDensity.Full => $"{i + 1} {Names[i]}",
        TabDensity.Short => $"{i + 1} {(Names[i].Length > 4 ? Names[i][..4] : Names[i])}",
        TabDensity.Shorter => $"{i + 1} {(Names[i].Length > 3 ? Names[i][..3] : Names[i])}",
        _ => $"{i + 1}",
    };

    [Test]
    public void DoubleWidthTabLabelsAreMeasuredInTerminalCells()
    {
        NAssert.That(MainWindow.FitTabs(Names.Length, 0, Label, 45), Is.EqualTo((TabDensity.Shorter, TabDensity.Full)));
        NAssert.That(MainWindow.FitTabs(Names.Length, 0, Label, 51), Is.EqualTo((TabDensity.Full, TabDensity.Full)));
    }

    [Test]
    public void WhenNothingFitsTheSelectedTabDropsToItsNumberToo()
    {
        NAssert.That(MainWindow.FitTabs(Names.Length, 4, Label, 24), Is.EqualTo((TabDensity.NumberOnly, TabDensity.Full)));
        NAssert.That(MainWindow.FitTabs(Names.Length, 4, Label, 20), Is.EqualTo((TabDensity.NumberOnly, TabDensity.NumberOnly)));
    }

    [Test]
    public void AnUnmeasuredStripKeepsEveryTabFull()
    {
        NAssert.That(MainWindow.FitTabs(Names.Length, 0, Label, 0), Is.EqualTo((TabDensity.Full, TabDensity.Full)));
    }
}
