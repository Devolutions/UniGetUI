using NUnit.Framework;
using UniGetUI.Tui.Views.Controls;
using NAssert = NUnit.Framework.Assert;

namespace UniGetUI.Tui.Tests;

[TestFixture]
internal sealed class TuiMnemonicsTests
{
    private static string Keys(params string?[] labels)
    {
        int[] indexes = TuiMnemonics.Assign(labels);
        return string.Concat(labels.Select((l, i) => indexes[i] < 0 ? "-" : char.ToLowerInvariant(l![indexes[i]]).ToString()));
    }

    [Test]
    public void EveryLabelKeepsItsFirstLetterWhenFree()
        => NAssert.That(Keys("File", "Page", "View", "Operations", "Help"), Is.EqualTo("fpvoh"));

    [Test]
    public void AnEarlierLabelDoesNotStealALaterLabelsFirstLetter()
        // "Reinstall package" could take P from its second word, but "Package details" owns it.
        => NAssert.That(Keys("Reinstall package", "Package details", "Uninstall selection"), Is.EqualTo("rpu"));

    [Test]
    public void CollisionsFallBackToOtherWordStartsThenAnyLetter()
    {
        NAssert.That(Keys("Clear successful operations", "Clear finished operations"), Is.EqualTo("cf"));
        NAssert.That(Keys("Open", "Options"), Is.EqualTo("op"));
    }

    [Test]
    public void SeparatorsAndExhaustedLabelsGetNoKey()
        => NAssert.That(Keys("Ab", null, "", "Ba", "ab"), Is.EqualTo("a--b-"));

    [Test]
    public void KeysAreCaseInsensitiveAndDigitsWork()
    {
        int[] indexes = TuiMnemonics.Assign(["Élan", "2nd pass"]);
        NAssert.That(TuiMnemonics.Matches("Élan", indexes[0], 'é'), Is.True);
        NAssert.That(TuiMnemonics.Matches("2nd pass", indexes[1], '2'), Is.True);
        NAssert.That(TuiMnemonics.Matches("2nd pass", -1, '2'), Is.False);
    }
}
