namespace UniGetUI.PackageEngine.Tests.Infrastructure;

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
