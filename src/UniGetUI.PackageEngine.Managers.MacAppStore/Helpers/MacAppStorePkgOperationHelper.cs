using System.Diagnostics;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Managers.MacAppStoreManager.Internal;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.MacAppStoreManager;

/// <summary>
/// Built-in Mac App Store operations run through <c>UniGetUIMasHelper</c> (<see cref="ElevatedMas"/>),
/// using <c>CommerceKit</c>/<c>StoreFoundation</c>, <c>/usr/sbin/installer</c> for package installation,
/// and macOS Trash for uninstalls, with live download progress reporting.
/// </summary>
internal sealed class MacAppStorePkgOperationHelper : BasePkgOperationHelper, IManagedPackageOperationHelper
{
    private readonly MacAppStore _manager;

    /// <summary>Whether UniGetUI already runs as root. Replaceable for tests.</summary>
    internal static Func<bool> IsRoot { get; set; } = CoreTools.IsAdministrator;

    public MacAppStorePkgOperationHelper(MacAppStore manager)
        : base(manager) { _manager = manager; }

    /// <summary>
    /// The operation verb for <c>UniGetUIMasHelper</c>. "get" acquires a free app, whether or not
    /// the account already owns it; a paid app must already be bought, and "install" redownloads it.
    /// </summary>
    internal string Verb(IPackage package, OperationType operation) => operation switch
    {
        OperationType.Uninstall => "uninstall",
        OperationType.Update => "update",
        _ => _manager.Catalog.Peek(package.Id) is { } app && AppStoreRecord.Price(app) > 0
            ? "install"
            : "get",
    };

    internal bool IsReinstall(IPackage package, OperationType operation) =>
        operation == OperationType.Install
        && (package.Tag is PackageTag.AlreadyInstalled or PackageTag.IsUpgradable
            || _manager.HasRecordedInstallPath(package));

    internal static bool NeedsRoot() => !IsRoot();

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation)
    {
        List<string> parameters = [Verb(package, operation)];
        if (IsReinstall(package, operation))
            parameters.Add("--force");
        parameters.Add(package.Id);
        return parameters;
    }

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation,
        bool standalone) =>
        standalone
            ? throw new InvalidOperationException("Mac App Store apps are installed by UniGetUI itself and have no standalone CLI command line.")
            : _getOperationParameters(package, options, operation);

    protected override OperationVeredict _getOperationResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode) =>
        Verdict(package, operation, returnCode, processOutput, message => Logger.Warn(message));

    public override string? GetFailureExplanation(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode,
        InstallOptions? options = null) =>
        ExplainFailure(processOutput);

    internal static string? ExplainFailure(IReadOnlyList<string> output)
    {
        string elevationBlocked = CoreTools.Translate(
            "The Mac App Store needs administrator rights for this operation, but elevation is prohibited in UniGetUI's settings.");
        if (output.Any(line => line.Contains(elevationBlocked, StringComparison.Ordinal)))
            return elevationBlocked;

        if (output.Any(line => line.Contains("No downloads initiated", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Redownload Unavailable", StringComparison.OrdinalIgnoreCase)))
            return CoreTools.Translate(
                "The Mac App Store did not start a download. Mac administrator authorization and Apple Account sign-in are separate. Open the App Store, try this app there, and complete any account prompts. Also check that the signed-in Apple Account owns it and that it is available in your region.");

        if (output.Any(line => line.Contains("Sign in to the App Store", StringComparison.OrdinalIgnoreCase)
                || line.Contains("not signed in", StringComparison.OrdinalIgnoreCase)))
            return CoreTools.Translate(
                "Sign in to your Apple Account in the Mac App Store app, then try again.");

        return null;
    }

    public Task<OperationVeredict?> TryPerformAsync(IPackage package, InstallOptions options,
        OperationType operation, Action<string> log, Action<OperationProgress> progress,
        CancellationToken cancellationToken) =>
        // Operations start on the UI thread; the price lookup and the store download run off it.
        Task.Run(() => PerformAsync(package, options, operation, log, progress, cancellationToken), CancellationToken.None);

    private async Task<OperationVeredict?> PerformAsync(IPackage package, InstallOptions options,
        OperationType operation, Action<string> log, Action<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        if (operation == OperationType.Install)
            _manager.Catalog.Get(package.Id);

        if (!NeedsRoot())
        {
            IReadOnlyList<string> directArgs = GetParameters(package, options, operation);
            var (userId, _) = ElevatedMas.UserIds();
            ProcessStartInfo start = new(_manager.Status.ExecutablePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("perform-root");
            start.ArgumentList.Add(userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (string arg in directArgs)
                start.ArgumentList.Add(arg);

            List<string> rootLines = [];
            int directExit = await ElevatedMas.RunProcess(start, line =>
            {
                if (NativeMas.TryParseProgressLine(line, out OperationProgress parsed))
                {
                    if (parsed != OperationProgress.Unknown)
                        progress(parsed);
                    return;
                }
                lock (rootLines)
                    rootLines.Add(line);
                log(line);
            }, CancellationToken.None);
            return Verdict(package, operation, directExit, rootLines, log);
        }

        if (Settings.Get(Settings.K.ProhibitElevation))
        {
            log(CoreTools.Translate(
                "The Mac App Store needs administrator rights for this operation, but elevation is prohibited in UniGetUI's settings."));
            return OperationVeredict.Failure;
        }

        IReadOnlyList<string> arguments = GetParameters(package, options, operation);
        var (exitCode, output) = await ElevatedMas.RunAsync(
            _manager.Status.ExecutablePath,
            arguments,
            Prompt(package, operation, IsReinstall(package, operation)),
            log,
            progress,
            cancellationToken);

        return Verdict(package, operation, exitCode, output, log);
    }

    internal OperationVeredict Verdict(int exitCode, IReadOnlyList<string> output, Action<string> log) =>
        Verdict(null, OperationType.None, exitCode, output, log);

    internal OperationVeredict Verdict(
        IPackage? package,
        OperationType operation,
        int exitCode,
        IReadOnlyList<string> output,
        Action<string> log)
    {
        if (exitCode == 0)
        {
            if (operation == OperationType.Uninstall && package is not null)
                _manager.ForgetInstallPath(package.Id);
            _manager.InvalidateInstalledPackages();
            return OperationVeredict.Success;
        }

        if (exitCode == NativeMas.CancellationResult
            || output.Any(line => line.TrimEnd().EndsWith("(-128)", StringComparison.Ordinal)))
            return OperationVeredict.Canceled;

        if (ExplainFailure(output) is { } explanation
            && !output.Any(line => line.Contains(explanation, StringComparison.Ordinal)))
            log(explanation);

        return OperationVeredict.Failure;
    }

    internal static string Prompt(IPackage package, OperationType operation, bool reinstall = false) => operation switch
    {
        OperationType.Uninstall => CoreTools.Translate("UniGetUI wants to uninstall {0}.", package.Name),
        OperationType.Update => CoreTools.Translate("UniGetUI wants to update {0} from the Mac App Store.", package.Name),
        _ when reinstall => CoreTools.Translate("UniGetUI wants to reinstall {0} from the Mac App Store.", package.Name),
        _ => CoreTools.Translate("UniGetUI wants to install {0} from the Mac App Store.", package.Name),
    };
}
