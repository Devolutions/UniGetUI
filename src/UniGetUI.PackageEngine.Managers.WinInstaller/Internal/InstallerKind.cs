using System.Text;

namespace UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal;

internal enum InstallerKind
{
    Msi,
    Inno,
    Nsis,
    Burn,
    InstallShield,
    Exe,
}

internal static class InstallerKinds
{
    public static string Name(InstallerKind kind) => kind switch
    {
        InstallerKind.Msi => "msi",
        InstallerKind.Inno => "inno",
        InstallerKind.Nsis => "nsis",
        InstallerKind.Burn => "burn",
        InstallerKind.InstallShield => "installshield",
        _ => "exe",
    };

    public static InstallerKind? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "msi" => InstallerKind.Msi,
        "inno" => InstallerKind.Inno,
        "nsis" => InstallerKind.Nsis,
        "burn" or "wix" => InstallerKind.Burn,
        "installshield" => InstallerKind.InstallShield,
        "exe" => InstallerKind.Exe,
        _ => null,
    };

    public static string Describe(InstallerKind kind) => kind switch
    {
        InstallerKind.Msi => "Windows Installer (.msi)",
        InstallerKind.Inno => "Inno Setup (.exe)",
        InstallerKind.Nsis => "NSIS (.exe)",
        InstallerKind.Burn => "WiX bundle (.exe)",
        InstallerKind.InstallShield => "InstallShield (.exe)",
        _ => "Installer (.exe)",
    };

    private static readonly byte[] InnoSetupData = Encoding.ASCII.GetBytes("Inno Setup Setup Data");
    private static readonly byte[] NsisHeader = Encoding.ASCII.GetBytes("NullsoftInst");

    public static InstallerKind Detect(PeInfo pe)
    {
        if (pe.Sections.Contains(".wixburn", StringComparer.Ordinal))
            return InstallerKind.Burn;
        if (pe.OverlayHead.AsSpan().IndexOf(InnoSetupData) >= 0
            || pe.String("Comments")?.Contains("Inno Setup", StringComparison.OrdinalIgnoreCase) == true)
            return InstallerKind.Inno;
        if (pe.OverlayHead.AsSpan().IndexOf(NsisHeader) >= 0)
            return InstallerKind.Nsis;
        if (pe.Strings.Values.Any(v => v.Contains("InstallShield", StringComparison.OrdinalIgnoreCase)))
            return InstallerKind.InstallShield;
        return InstallerKind.Exe;
    }

    public static string? InstallArguments(InstallerKind kind) => kind switch
    {
        InstallerKind.Msi => "/qn /norestart",
        InstallerKind.Inno => "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
        InstallerKind.Nsis => "/S",
        InstallerKind.Burn => "/quiet /norestart",
        _ => null,
    };

    public static string? UninstallArguments(InstallerKind kind) => kind switch
    {
        InstallerKind.Inno => "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
        InstallerKind.Nsis => "/S",
        InstallerKind.Burn => "/quiet /norestart",
        _ => null,
    };
}
