using Avalonia.Controls;
using Avalonia.Input;
using NUnit.Framework;
using UniGetUI.Tui.Infrastructure;
using NAssert = NUnit.Framework.Assert;

namespace UniGetUI.Tui.Tests;

[TestFixture, NonParallelizable]
internal sealed class TuiInputGuardControlTests : TuiE2ETestBase
{
    [TestCase(3, 7)]
    [TestCase(7, 3)]
    public async Task SelectedPaste_ReplacesRangeAndCollapsesCaretUsingReplacementCapacity(int start, int end)
    {
        await OnUi(() =>
        {
            var box = new TextBox { Text = "preOLD!post", CaretIndex = 7, SelectionStart = start, SelectionEnd = end };
            var input = new TextInputEventArgs { Text = "A\nB\tC\u0001D" };
            // Length 11, selection 4: five incoming characters fit, not merely one.
            NAssert.That(TuiInputGuard.HandleTextInput(box, input, "test search", 12), Is.True);
            NAssert.That(input.Handled, Is.True);
            NAssert.That(box.Text, Is.EqualTo("preA B Cpost"));
            NAssert.That(box.CaretIndex, Is.EqualTo(8));
            NAssert.That(box.SelectionStart, Is.EqualTo(8));
            NAssert.That(box.SelectionEnd, Is.EqualTo(8));
        });
    }

    [TestCase("preOLDpost", 3, 6, 6, "\u0001\u0002", 30, "prepost", 3,
        TestName = "SanitizedPaste_SelectedRangeFullyFiltered_CollapsesToStart")]
    [TestCase("prepost", 3, 3, 3, "A\nB", 30, "preA Bpost", 6)]
    [TestCase("prepost", 3, 3, 3, "ABCDE", 9, "preABpost", 5)]
    [TestCase("prepost", 3, 3, 3, "\u0001", 30, "prepost", 3,
        TestName = "SanitizedPaste_NoSelectionFullyFiltered_PreservesTextAndCaret")]
    [TestCase("prepost", 3, 3, 3, "A", 7, "prepost", 3)]
    [TestCase("preOLDpost", 3, 6, 6, "A\nB", 9, "preA post", 5)]
    public async Task SanitizedPaste_PreservesExactPrefixSuffixAndPositions(
        string text, int start, int end, int caret, string incoming, int cap, string expected, int insertionEnd)
    {
        await OnUi(() =>
        {
            var box = new TextBox { Text = text, CaretIndex = caret, SelectionStart = start, SelectionEnd = end };
            var input = new TextInputEventArgs { Text = incoming };
            NAssert.That(TuiInputGuard.HandleTextInput(box, input, "test search", cap), Is.True);
            NAssert.That(input.Handled, Is.True);
            NAssert.That(box.Text, Is.EqualTo(expected));
            NAssert.That(box.CaretIndex, Is.EqualTo(insertionEnd));
            NAssert.That(box.SelectionStart, Is.EqualTo(insertionEnd));
            NAssert.That(box.SelectionEnd, Is.EqualTo(insertionEnd));
        });
    }

    [TestCase("abc", 1, 1, "Z")]
    [TestCase("abc", 0, 2, "XY")]
    [TestCase("abc", 2, 0, "")]
    [TestCase("abc", 0, 2, null)]
    public async Task OrdinaryOrEmptyInput_IsLeftForTheControlWithoutChangingSelection(
        string text, int start, int end, string? incoming)
    {
        await OnUi(() =>
        {
            var box = new TextBox { Text = text, CaretIndex = 2, SelectionStart = start, SelectionEnd = end };
            int originalCaret = box.CaretIndex;
            var input = new TextInputEventArgs { Text = incoming };
            NAssert.That(TuiInputGuard.HandleTextInput(box, input, "test search", 5), Is.False);
            NAssert.That(input.Handled, Is.False);
            NAssert.That(box.Text, Is.EqualTo(text));
            NAssert.That(box.CaretIndex, Is.EqualTo(originalCaret));
            NAssert.That(box.SelectionStart, Is.EqualTo(start));
            NAssert.That(box.SelectionEnd, Is.EqualTo(end));
        });
    }
}
