namespace UniGetUI.PackageEngine.Structs;

public struct OverridenInstallationOptions
{
    public string? Scope;
    public bool? RunAsAdministrator;
    public bool PowerShell_DoNotSetScopeParameter = false;
    public bool? WinGet_SpecifyVersion = null;
    public bool Pip_BreakSystemPackages = false;

    public OverridenInstallationOptions(string? scope = null, bool? runAsAdministrator = null)
    {
        Scope = scope;
        RunAsAdministrator = runAsAdministrator;
    }

    public override string ToString()
    {
        return $"<Scope={Scope};RunAsAdministrator={RunAsAdministrator};WG_SpecifyVersion={WinGet_SpecifyVersion};PS_NoScope={PowerShell_DoNotSetScopeParameter};Pip_BreakSystemPackages={Pip_BreakSystemPackages}>";
    }
}
