using NUnit.Framework;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Views.Controls;
using NAssert = NUnit.Framework.Assert;

namespace UniGetUI.Tui.Tests;

[TestFixture]
internal sealed class TuiFunctionBarTests
{
    // A cell is " key " + " label": "F1 Help" is 4 + 5 = 9 columns; cells are 2 apart.
    private static readonly TuiKeyHint[] Keys =
    [
        new("F1", "Help", 0), new("F5", "Reload", 4), new("F10", "Menu", 1), new("Alt+1-9", "Pages", 3), new("Ctrl+Q", "Quit", 2),
    ];

    [Test]
    public void TheNotificationDropsTheTitleSeparatorAndFallsBackWithoutEllipsis()
    {
        NAssert.That(TuiFunctionBar.NotificationVariants("Updates available", "13 packages can be updated"),
            Is.EqualTo(new[] { "Updates available: 13 packages can be updated", "13 packages can be updated", "Updates available", "" }));
        NAssert.That(TuiFunctionBar.NotificationVariants("", "Done"), Is.EqualTo(new[] { "Done", "" }));
    }

    [Test]
    public void WideBarsShowEveryKeyAndTheFullNotification()
    {
        var variants = TuiFunctionBar.NotificationVariants("Updates available", "13 packages can be updated");
        (List<int> keys, int variant) = TuiFunctionBar.Fit(Keys, "INFO", variants, 150);
        NAssert.That(keys, Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
        NAssert.That(variant, Is.EqualTo(0));
    }

    [Test]
    public void NarrowBarsDropWholeKeysByPriorityThenShortenTheNotification()
    {
        var variants = TuiFunctionBar.NotificationVariants("Updates available", "13 packages can be updated");
        // 100 columns: F1, F10 and Ctrl+Q go first (9 + 10 + 13 + 2 gaps = 36), then the full notification
        // (2 + 6 + 47 = 55, total 91). F5 (11 + 2) no longer fits, and neither does Alt+1-9.
        (List<int> keys, int variant) = TuiFunctionBar.Fit(Keys, "INFO", variants, 100);
        NAssert.That(keys, Is.EqualTo(new[] { 0, 2, 4 }));
        NAssert.That(variant, Is.EqualTo(0));

        // 80 columns: the full text (55) does not fit in the 44 left, the message alone (2 + 6 + 28 = 36) does.
        (keys, variant) = TuiFunctionBar.Fit(Keys, "INFO", variants, 80);
        NAssert.That(keys, Is.EqualTo(new[] { 0, 2, 4 }));
        NAssert.That(variants[variant], Is.EqualTo("13 packages can be updated"));

        // 65 columns: only the title (2 + 6 + 19 = 27) fits in the 29 left; at 45, only the badge (8).
        (_, variant) = TuiFunctionBar.Fit(Keys, "INFO", variants, 65);
        NAssert.That(variants[variant], Is.EqualTo("Updates available"));
        (keys, variant) = TuiFunctionBar.Fit(Keys, "INFO", variants, 45);
        NAssert.That(keys, Is.EqualTo(new[] { 0, 2, 4 }));
        NAssert.That(variants[variant], Is.EqualTo(""));

        // Without a notification the keys get the whole bar.
        (keys, variant) = TuiFunctionBar.Fit(Keys, null, variants, 80);
        NAssert.That(keys, Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
        NAssert.That(variant, Is.EqualTo(-1));
    }
}
