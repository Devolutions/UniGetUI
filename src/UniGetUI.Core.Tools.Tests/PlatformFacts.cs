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

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Runs on Linux only";
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

// Root ignores file permissions, so a test that relies on a read-only folder cannot run as root.
public sealed class UnixNonRootFactAttribute : FactAttribute
{
    public UnixNonRootFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Runs on Linux and macOS only";
        }
        else if (Environment.UserName == "root")
        {
            Skip = "Cannot run as root, which ignores file permissions";
        }
    }
}

public sealed class MacOSNonRootFactAttribute : FactAttribute
{
    public MacOSNonRootFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "Runs on macOS only";
        }
        else if (Environment.UserName == "root")
        {
            Skip = "Cannot run as root, which ignores file permissions";
        }
    }
}
