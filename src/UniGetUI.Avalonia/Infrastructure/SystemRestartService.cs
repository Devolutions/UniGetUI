using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Operations.Reboot;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class SystemRestartService
{
    private const int ShutdownRequestTimeoutMs = 10000;

    public static async Task ConfirmAndRestartAsync(Window owner)
    {
        try
        {
            if (!await ConfirmAsync(owner)) return;

            if (!await RestartAsync()) ReportRestartFailure();
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to prompt for a system restart:");
            Logger.Error(ex);
        }
    }

    private static async Task<bool> ConfirmAsync(Window owner)
    {
        var pending = PendingRebootStore.GetPending();
        if (pending.Count == 0) return false;

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = pending.Count == 1
                ? CoreTools.Translate("1 package cannot finish until your computer restarts.")
                : CoreTools.Translate(
                    "{0} packages cannot finish until your computer restarts.",
                    pending.Count),
            Opacity = 0.82,
            TextWrapping = TextWrapping.Wrap,
        });

        body.Children.Add(new ScrollViewer
        {
            MaxHeight = 180,
            Content = new TextBlock
            {
                Text = string.Join(
                    Environment.NewLine,
                    pending
                        .Select(entry => entry.PackageName.Length > 0 ? entry.PackageName : entry.PackageId)
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .Select(name => "* " + name)),
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
            },
        });

        body.Children.Add(new TextBlock
        {
            Text = CoreTools.Translate(
                "Your computer will restart now. Save your work and close any open applications first."),
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ImmersiveConfirmationDialog(
            CoreTools.Translate("Restart your computer?"),
            body,
            CoreTools.Translate("Restart now"),
            CoreTools.Translate("Not now"))
        {
            MaxWidth = 560,
            MaxHeight = 420,
            FocusPrimaryButton = false,
        };

        await dialog.ShowDialog(owner);
        return dialog.Result is true;
    }

    private static async Task<bool> RestartAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Logger.Warn("A system restart was requested on a platform where UniGetUI cannot trigger one");
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                Arguments = "/r /t 0",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                Logger.Error("Could not start shutdown.exe to request a system restart");
                return false;
            }

            using var timeout = new CancellationTokenSource(ShutdownRequestTimeoutMs);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Logger.Warn("shutdown.exe did not return in time; assuming the restart was requested");
                return true;
            }

            if (process.ExitCode != 0)
            {
                Logger.Error($"shutdown.exe refused the restart request with exit code {process.ExitCode}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to request a system restart:");
            Logger.Error(ex);
            return false;
        }
    }

    private static void ReportRestartFailure()
    {
        Dispatcher.UIThread.Post(() =>
            Views.MainWindow.Instance?.ShowRuntimeNotification(
                CoreTools.Translate("The computer could not be restarted"),
                CoreTools.Translate(
                    "UniGetUI could not start the restart. Please restart your computer manually."),
                Views.MainWindow.RuntimeNotificationLevel.Error));
    }
}
