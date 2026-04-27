namespace UniGetUI.PackageEngine.Managers.WingetManager;

internal enum WinGetCliToolKind
{
    SystemWinGet,
    BundledPinget,
}

internal enum WinGetCliToolPreference
{
    Default,
    SystemWinGet,
    BundledPinget,
}

internal enum WinGetComApiPolicy
{
    Default,
    Enabled,
    Disabled,
}
