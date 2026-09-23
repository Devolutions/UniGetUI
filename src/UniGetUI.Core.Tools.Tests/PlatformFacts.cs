namespace UniGetUI.Core.Tools.Tests;

// xUnit 2 has no runtime skip; setting Skip in the attribute makes a test for another platform
// report as skipped instead of passing without running.

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Runs on Windows only";
        }
    }
}

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Runs on Linux and macOS only";
        }
    }
}

public sealed class MacOSFactAttribute : FactAttribute
{
    public MacOSFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "Runs on macOS only";
        }
    }
}
