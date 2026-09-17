using Avalonia.Controls;
using UniGetUI.Avalonia.Views.Pages.SettingsPages.PolicyEditor;

namespace UniGetUI.Tests.PolicyEditor;

public class PolicyEditorFocusNavigationTests
{
    [Fact]
    public void GroupedFindingTargetSelectsFirstEnabledFocusableDescendant()
    {
        var group = new StackPanel();
        group.Children.Add(new Button
        {
            IsEnabled = false,
            Focusable = true,
        });
        var expected = new CheckBox
        {
            IsEnabled = true,
            IsVisible = true,
            Focusable = true,
        };
        group.Children.Add(expected);

        Control? target = PolicyEditorDialog.FindFocusableTarget(group);

        Assert.Same(expected, target);
    }

    [Fact]
    public void DirectFocusableFindingTargetRemainsPreferred()
    {
        var expected = new TextBox
        {
            IsEnabled = true,
            IsVisible = true,
            Focusable = true,
        };
        expected.Text = "value";

        Control? target = PolicyEditorDialog.FindFocusableTarget(expected);

        Assert.Same(expected, target);
    }

    [Fact]
    public void FindingNavigationExpandsAllCollapsedAncestorSections()
    {
        var first = new Expander { IsExpanded = false };
        var second = new Expander { IsExpanded = true };

        bool changed = PolicyEditorDialog.ExpandCollapsedAncestors([first, second]);

        Assert.True(changed);
        Assert.True(first.IsExpanded);
        Assert.True(second.IsExpanded);
        Assert.False(PolicyEditorDialog.ExpandCollapsedAncestors([first, second]));
    }
}
