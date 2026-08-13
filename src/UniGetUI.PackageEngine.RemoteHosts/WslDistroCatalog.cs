using System.Security.Cryptography;
using System.Text;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.PackageEngine.RemoteHosts;

public static class WslDistroCatalog
{
    internal static Func<IReadOnlyList<WslDistroInfo>>? ListOverride { get; set; }

    public static Guid CreateHostId(string distroName)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("wsl:" + distroName));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16));
    }

    public static IReadOnlyList<WslDistroInfo> ListInstalled()
    {
        IReadOnlyList<WslDistroInfo> raw;
        if (ListOverride is not null)
        {
            raw = ListOverride();
        }
        else if (!OperatingSystem.IsWindows())
        {
            raw = [];
        }
        else
        {
            try
            {
                raw = WslListParser.ParseVerboseList(WslListParser.DecodeListOutput(RunWslListVerbose()));
            }
            catch (Exception ex)
            {
                Logger.Debug("WSL distro listing failed");
                Logger.Debug(ex);
                raw = [];
            }
        }

        return raw
            .Where(distro =>
                !WslListParser.IsHelperDistro(distro.Name)
                && RemoteHost.IsValidDestination(distro.Name))
            .ToList();
    }

    public static IReadOnlyList<RemoteHost> GetEnabledHosts()
    {
        HashSet<string> disabled = GetDisabledNames();
        return ListInstalled()
            .Where(distro => !disabled.Contains(distro.Name))
            .Select(RemoteHost.ForWsl)
            .ToList();
    }

    public static bool IsEnabled(string distroName)
        => !GetDisabledNames().Contains(distroName);

    public static void SetEnabled(string distroName, bool enabled)
    {
        HashSet<string> disabled = GetDisabledNames();
        if (enabled)
            disabled.Remove(distroName);
        else
            disabled.Add(distroName);

        if (disabled.Count == 0)
            Settings.SetValue(Settings.K.DisabledWslDistros, "");
        else
            Settings.SetValue(Settings.K.DisabledWslDistros, RemoteHostsJson.SerializeStringList([.. disabled]));
    }

    public static HashSet<string> GetDisabledNames()
    {
        string json = Settings.GetValue(Settings.K.DisabledWslDistros);
        if (string.IsNullOrWhiteSpace(json))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            return new HashSet<string>(RemoteHostsJson.DeserializeStringList(json), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not load disabled WSL distros");
            Logger.Error(ex);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static byte[] RunWslListVerbose()
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = "wsl.exe";
        process.StartInfo.ArgumentList.Add("--list");
        process.StartInfo.ArgumentList.Add("--verbose");
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.Start();

        using var buffer = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(buffer);
        process.StandardError.BaseStream.CopyTo(Stream.Null);
        process.WaitForExit(15_000);
        return buffer.ToArray();
    }
}
