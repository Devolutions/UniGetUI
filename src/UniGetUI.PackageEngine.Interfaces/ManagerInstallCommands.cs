namespace UniGetUI.PackageEngine.ManagerClasses.Manager
{
    public readonly record struct ManagerInstallCommands(string? Windows = null, string? MacOS = null, string? Linux = null)
    {
        public string? ForCurrentPlatform() =>
            OperatingSystem.IsWindows() ? Windows
            : OperatingSystem.IsMacOS() ? MacOS
            : Linux;
    }
}
