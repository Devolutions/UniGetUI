using System.ComponentModel;
using System.Diagnostics;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.InstallerFeeds;

namespace UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal;

internal sealed record InstallerProcess(string FileName, string Arguments, bool Elevated);

internal static class WinInstallerRunner
{
    private const int ErrorCancelled = 1223;
    private const int ErrorElevationRequired = 740;
    private const int MsiUserExit = 1602;
    private const int RestartRequired = 3010;
    private const int RestartInitiated = 1641;

    internal static string MsiExecPath => Path.Combine(Environment.SystemDirectory, "msiexec.exe");

    internal static Func<InstallerProcess, Task<int>> RunProcess { get; set; } = RunProcessAsync;

    internal static Func<bool> IsAdministrator { get; set; } = CoreTools.IsAdministrator;

    internal static TimeSpan UninstallSettleTime { get; set; } = TimeSpan.FromMinutes(5);

    internal static TimeSpan UninstallPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public static async Task<OperationVeredict> InstallAsync(WinFeedItem item, bool allowUnsigned, Action<string> log,
        Action<OperationProgress> progress, CancellationToken cancellationToken)
    {
        string staging = Directory.CreateTempSubdirectory("UniGetUI-installer-").FullName;
        try
        {
            string staged = Path.Combine(staging, StagedName(item));
            await FeedStaging.StageAsync(item.Location, item.Size, staged, log, progress, cancellationToken);
            using var held = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
            FeedStaging.VerifyHash(item.Sha256, staged);
            VerifyIdentity(item, staged);
            VerifySignature(staged, allowUnsigned, log);

            cancellationToken.ThrowIfCancellationRequested();
            progress(OperationProgress.ForStage(OperationProgressStage.Installing));
            string logFile = Path.Combine(staging, "install.log");
            var process = InstallProcess(item, staged, logFile);
            log($"Installing {item.Name} {item.Version}: {Path.GetFileName(process.FileName)} {process.Arguments}");
            if (await RunAsync(process, log) is not { } code)
                return OperationVeredict.Canceled;
            var verdict = Verdict(code, log);
            if (verdict == OperationVeredict.Failure && File.Exists(logFile))
                LogMsiErrors(logFile, log);
            return verdict;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static string StagedName(WinFeedItem item)
    {
        string name = Uri.UnescapeDataString(Path.GetFileName(item.Location.AbsolutePath));
        string extension = item.Details.Kind == InstallerKind.Msi ? ".msi" : ".exe";
        bool safe = name.Length is > 4 and < 120 && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '-' or '(' or ')' or '+');
        return safe ? name : "setup" + extension;
    }

    internal static void VerifyIdentity(WinFeedItem item, string staged)
    {
        if (!item.Location.IsFile)
            return;
        string original = item.Location.LocalPath;
        var manifest = InstallerFile.ReadManifest(original);
        string stem = Path.GetFileNameWithoutExtension(original);
        var metadata = InstallerFile.IsMsi(staged)
            ? InstallerFile.FromMsi(MsiDatabase.Read(staged), manifest, stem)
            : InstallerFile.FromExe(PeReader.Read(staged), manifest, stem);
        if (!string.Equals(metadata.Id, item.Id, StringComparison.OrdinalIgnoreCase) || metadata.Version != item.Version)
            throw new InvalidDataException(
                $"The installer is {metadata.Id} {metadata.Version}, but the feed announced {item.Id} {item.Version}. Reload the feed, then try again.");
    }

    internal static void VerifySignature(string staged, bool allowUnsigned, Action<string> log)
    {
        var check = Authenticode.Verify(staged);
        if (check.IsValid)
            log($"The installer is {check.Describe()}.");
        else if (allowUnsigned)
            log($"The installer is {check.Describe()}, but its catalog allows unsigned packages.");
        else
            throw new InvalidDataException($"Windows does not trust this installer: it is {check.Describe()}. "
                + "Publish an installer with a valid Authenticode signature, or set allowUnsignedPackages on its catalog.");
    }

    internal static InstallerProcess InstallProcess(WinFeedItem item, string staged, string logFile)
    {
        bool elevated = !IsAdministrator() && (item.Details.Kind == InstallerKind.Msi ? !item.Details.PerUser : item.Details.PerMachine);
        string arguments = item.Details.InstallArguments ?? InstallerKinds.InstallArguments(item.Details.Kind)
            ?? throw new InvalidDataException($"No silent switches are known for {item.Name}.");
        return item.Details.Kind == InstallerKind.Msi
            ? new InstallerProcess(MsiExecPath, $"/i \"{staged}\" {arguments} /l*v \"{logFile}\"", elevated)
            : new InstallerProcess(staged, arguments, elevated);
    }

    public static async Task<OperationVeredict> UninstallAsync(string name, InstallerDetails? details, ArpEntry entry,
        Action<string> log, Action<OperationProgress> progress)
    {
        if (UninstallProcess(details, entry) is not { } process)
        {
            log($"UniGetUI does not know how to uninstall {name} silently. Uninstall it from Apps and Features, "
                + "or ask your administrator to give its uninstallArguments in the feed.");
            return OperationVeredict.Failure;
        }
        progress(OperationProgress.ForStage(OperationProgressStage.Uninstalling));
        log($"Uninstalling {name}: {Path.GetFileName(process.FileName)} {process.Arguments}");
        if (await RunAsync(process, log) is not { } code)
            return OperationVeredict.Canceled;
        var verdict = Verdict(code, log);
        if (verdict != OperationVeredict.Success || string.Equals(process.FileName, MsiExecPath, StringComparison.OrdinalIgnoreCase))
            return verdict;

        var deadline = DateTime.UtcNow + UninstallSettleTime;
        while (true)
        {
            if (!ArpEntries.Read().Any(e => e.PerMachine == entry.PerMachine && string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase)))
                return OperationVeredict.Success;
            if (DateTime.UtcNow >= deadline)
                break;
            await Task.Delay(UninstallPollInterval);
        }
        log($"The uninstaller finished, but {name} is still listed in Apps and Features.");
        return OperationVeredict.Failure;
    }

    internal static InstallerProcess? UninstallProcess(InstallerDetails? details, ArpEntry entry)
    {
        bool elevated = entry.PerMachine && !IsAdministrator();
        string? extra = details?.UninstallArguments;
        if (entry.WindowsInstaller && InstallerDetails.Guid(entry.Key) is { } productCode)
            return new InstallerProcess(MsiExecPath, $"/x {productCode} {extra ?? "/qn /norestart"}", elevated);

        var kind = details?.Kind is { } known && known != InstallerKind.Msi ? known
            : entry.Key.EndsWith("_is1", StringComparison.OrdinalIgnoreCase) ? InstallerKind.Inno
            : entry.BundleUpgradeCodes.Count > 0 ? InstallerKind.Burn
            : InstallerKind.Exe;
        if (extra is null && entry.QuietUninstallString is { } quiet && SplitCommandLine(quiet) is { } quietCommand)
            return quietCommand with { Elevated = elevated };
        if (entry.UninstallString is null || SplitCommandLine(entry.UninstallString) is not { } command)
            return null;
        if ((extra ?? InstallerKinds.UninstallArguments(kind)) is not { } silent)
            return null;
        return command with { Arguments = $"{command.Arguments} {silent}".Trim(), Elevated = elevated };
    }

    internal static InstallerProcess? SplitCommandLine(string commandLine)
    {
        string text = Environment.ExpandEnvironmentVariables(commandLine).Trim();
        string file, arguments;
        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            if (end < 0)
                return null;
            file = text[1..end];
            arguments = text[(end + 1)..];
        }
        else
        {
            int end = -1;
            for (int at = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); at >= 0;
                at = text.IndexOf(".exe", at + 1, StringComparison.OrdinalIgnoreCase))
            {
                if (at + 4 == text.Length || char.IsWhiteSpace(text[at + 4]) || text[at + 4] == '/')
                {
                    end = at + 4;
                    break;
                }
            }
            if (end < 0)
                return null;
            file = text[..end];
            arguments = text[end..];
        }
        if (!Path.IsPathFullyQualified(file))
            file = Path.Combine(Environment.SystemDirectory, file);
        return File.Exists(file) ? new InstallerProcess(file, arguments.Trim(), false) : null;
    }

    private static async Task<int?> RunAsync(InstallerProcess process, Action<string> log)
    {
        if (process.Elevated && Settings.Get(Settings.K.ProhibitElevation))
            throw new InvalidOperationException("This package installs for every user, which needs administrator rights, "
                + "and elevation is prohibited in UniGetUI's settings.");
        try
        {
            return await RunProcess(process);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired && !process.Elevated)
        {
            log("The installer asks for administrator rights.");
            return await RunAsync(process with { Elevated = true }, log);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            log("The administrator prompt was canceled.");
            return null;
        }
    }

    internal static async Task<int> RunProcessAsync(InstallerProcess process)
    {
        var startInfo = new ProcessStartInfo(process.FileName, process.Arguments)
        {
            WorkingDirectory = Path.GetDirectoryName(process.FileName)!,
        };
        if (process.Elevated)
        {
            startInfo.UseShellExecute = true;
            startInfo.Verb = "runas";
            CoreTools.PrepareForegroundForElevation();
        }
        else
        {
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
        }
        using var started = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {process.FileName}.");
        await started.WaitForExitAsync(CancellationToken.None);
        return started.ExitCode;
    }

    internal static OperationVeredict Verdict(int code, Action<string> log)
    {
        switch (code)
        {
            case 0:
                return OperationVeredict.Success;
            case RestartRequired:
            case RestartInitiated:
                log("Windows must restart to finish.");
                return OperationVeredict.Success;
            case MsiUserExit:
                log("The installation was canceled.");
                return OperationVeredict.Canceled;
            default:
                log($"The installer exited with code {code}.");
                return OperationVeredict.Failure;
        }
    }

    private static void LogMsiErrors(string logFile, Action<string> log)
    {
        try
        {
            foreach (string line in File.ReadLines(logFile).Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)
                && !l.Contains("ErrorDialog", StringComparison.Ordinal) && !l.Contains("Note: 1: 2205", StringComparison.Ordinal)
                && !l.Contains("FROM `Error`", StringComparison.Ordinal)).TakeLast(15))
                log(line.Trim());
        }
        catch (IOException) { }
    }
}
