using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Infrastructure;

[SupportedOSPlatform("windows")]
internal static class WindowsAdminFileInstaller
{
    private const int ErrorCancelled = 1223;
    private const int RobocopyFirstFailureCode = 8;

    public static async Task<bool> WriteAsync(string destination, string contents)
    {
        string stagingDirectory = Directory.CreateTempSubdirectory("unigetui-admin-write-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(stagingDirectory, Path.GetFileName(destination)), contents);

            using var process = new Process { StartInfo = CreateStartInfo(stagingDirectory, destination) };
            CoreTools.PrepareForegroundForElevation();
            try
            {
                process.Start();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return false;
            }
            await process.WaitForExitAsync();
            if (process.ExitCode >= RobocopyFirstFailureCode)
                throw new IOException(CoreTools.Translate("Could not copy the file to {0} as an administrator (robocopy exit code {1}).",
                    destination, process.ExitCode));
            return true;
        }
        finally
        {
            try { Directory.Delete(stagingDirectory, recursive: true); }
            catch { }
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string stagingDirectory, string destination)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "robocopy.exe"))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (string argument in new[]
        {
            stagingDirectory, Path.GetDirectoryName(destination)!, Path.GetFileName(destination),
            "/IS", "/IT", "/R:0", "/W:0", "/NP", "/NJH", "/NJS",
        })
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }
}
