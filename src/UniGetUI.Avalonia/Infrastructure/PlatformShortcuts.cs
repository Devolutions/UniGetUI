using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>Keyboard-shortcut conventions per platform: ⌘ (Meta) on macOS, Ctrl elsewhere.</summary>
internal static class PlatformShortcuts
{
    public static KeyModifiers Primary => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    public static bool HasPrimary(KeyModifiers modifiers) => modifiers.HasFlag(Primary);

    /// <summary>On macOS, rewrites "Ctrl+…" accessibility accelerator keys under <paramref name="root"/> to "Cmd+…".</summary>
    public static void AdaptAcceleratorKeys(Control root)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        foreach (var control in root.GetLogicalDescendants().OfType<Control>().Prepend(root))
        {
            if (AutomationProperties.GetAcceleratorKey(control) is { } key && key.StartsWith("Ctrl+", StringComparison.Ordinal))
                AutomationProperties.SetAcceleratorKey(control, "Cmd+" + key["Ctrl+".Length..]);
        }
    }
}
