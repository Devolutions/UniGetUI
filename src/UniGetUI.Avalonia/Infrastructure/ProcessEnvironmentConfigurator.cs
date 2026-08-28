using System.Diagnostics;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class ProcessEnvironmentConfigurator
{
    // #5236: a login shell whose startup files never return (a recursive `exec zsh -l`,
    // a prompt waiting on input, ...) must not hold up startup forever.
    private static readonly TimeSpan LoginShellTimeout = TimeSpan.FromSeconds(5);

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
        // This runs on a thread pool thread whose result is awaited from an `async void`
        // startup path, so it must never throw: a faulty PATH is not worth a crash.
        try
        {
            var startInfo = new ProcessStartInfo("zsh", ["-l", "-c", "printenv PATH"])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };

            if (CoreTools.TryReadStandardOutput(startInfo, LoginShellTimeout, out string shellPath)
                && shellPath.Length > 0)
            {
                Environment.SetEnvironmentVariable("PATH", shellPath);
                return;
            }

            Logger.Warn("Could not read PATH from the login shell; keeping the PATH inherited from the "
                      + "launcher. Package managers installed outside the system directories may not be found.");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to expand the PATH from the login shell:");
            Logger.Error(ex);
        }
    }
}
