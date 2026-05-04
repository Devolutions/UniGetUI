using System.Diagnostics;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class ProcessEnvironmentConfigurator
{
    public static void PrepareForCurrentPlatform()
    {
        if (OperatingSystem.IsMacOS())
        {
            ExpandMacOSPath();
        }

        ApplyProxySettingsToProcess();
    }

    public static void ApplyProxySettingsToProcess()
    {
        try
        {
            var proxyUri = Settings.GetProxyUrl();
            if (proxyUri is null || !Settings.Get(Settings.K.EnableProxy))
            {
                Environment.SetEnvironmentVariable("HTTP_PROXY", "", EnvironmentVariableTarget.Process);
                return;
            }

            string content;
            if (!Settings.Get(Settings.K.EnableProxyAuth))
            {
                content = proxyUri.ToString();
            }
            else
            {
                var creds = Settings.GetProxyCredentials();
                if (creds is null)
                {
                    content = proxyUri.ToString();
                }
                else
                {
                    content = $"{proxyUri.Scheme}://{Uri.EscapeDataString(creds.UserName)}"
                            + $":{Uri.EscapeDataString(creds.Password)}"
                            + $"@{proxyUri.AbsoluteUri.Replace($"{proxyUri.Scheme}://", "")}";
                }
            }

            Environment.SetEnvironmentVariable("HTTP_PROXY", content, EnvironmentVariableTarget.Process);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to apply proxy settings:");
            Logger.Error(ex);
        }
    }

    private static void ExpandMacOSPath()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("zsh", ["-l", "-c", "printenv PATH"])
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            string shellPath = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            if (!string.IsNullOrEmpty(shellPath))
            {
                Environment.SetEnvironmentVariable("PATH", shellPath);
            }
        }
        catch
        {
            // Keep the existing PATH if the shell can't be launched.
        }
    }
}
