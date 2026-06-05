namespace UniGetUI.PackageEngine.Structs;

public struct OverridenInstallationOptions
{
    public string? Scope;
    public bool? RunAsAdministrator;
    public bool PowerShell_DoNotSetScopeParameter = false;
    public bool? WinGet_SpecifyVersion = null;
    public bool Pip_BreakSystemPackages = false;

    /// <summary>
    /// Path to a pre-downloaded installer (used by WinGet GitHub acceleration).
    /// When set, PackageOperation.PrepareProcessStartInfo runs this local installer
    /// instead of calling winget.exe.
    /// </summary>
    public string? AcceleratedInstallerPath = null;

    /// <summary>
    /// Installer type (msi, exe, inno, nullsoft, wix, msix, portable, burn).
    /// Determines silent-install arguments for the accelerated local installer.
    /// </summary>
    public string? AcceleratedInstallerType = null;

    public OverridenInstallationOptions(string? scope = null, bool? runAsAdministrator = null)
    {
        Scope = scope;
        RunAsAdministrator = runAsAdministrator;
    }

    public override string ToString()
    {
        return $"<Scope={Scope};RunAsAdministrator={RunAsAdministrator};WG_SpecifyVersion={WinGet_SpecifyVersion};PS_NoScope={PowerShell_DoNotSetScopeParameter};Pip_BreakSystemPackages={Pip_BreakSystemPackages};Accelerated={AcceleratedInstallerPath is not null}>";
    }
}
