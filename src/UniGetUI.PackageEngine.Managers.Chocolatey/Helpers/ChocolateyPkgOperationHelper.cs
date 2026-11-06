using System.Text.RegularExpressions;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;
using Architecture = UniGetUI.PackageEngine.Enums.Architecture;

namespace UniGetUI.PackageEngine.Managers.ChocolateyManager;

internal sealed partial class ChocolateyPkgOperationHelper : BasePkgOperationHelper
{
    [GeneratedRegex(@"\bupgraded (\d+)/\d+ packages", RegexOptions.IgnoreCase)]
    private static partial Regex UpgradeSummary();

    [GeneratedRegex(@"^\s*(\S+) v(\S+) is the latest version available", RegexOptions.IgnoreCase)]
    private static partial Regex LatestVersionAvailable();

    private enum NoOpUpdate
    {
        None,
        AlreadyCurrent,
        NotUpgraded,
    }

    private static bool UpgradedNothing(IReadOnlyList<string> processOutput)
    {
        for (int i = processOutput.Count - 1; i >= 0; i--)
        {
            Match match = UpgradeSummary().Match(processOutput[i]);
            if (match.Success)
                return int.TryParse(match.Groups[1].Value, out int upgraded) && upgraded == 0;
        }

        return false;
    }

    private static string? LatestAvailableVersion(IPackage package, IReadOnlyList<string> processOutput)
    {
        for (int i = processOutput.Count - 1; i >= 0; i--)
        {
            Match match = LatestVersionAvailable().Match(processOutput[i]);
            if (match.Success && match.Groups[1].Value.Equals(package.Id, StringComparison.OrdinalIgnoreCase))
                return match.Groups[2].Value;
        }

        return null;
    }

    private static string? UpdateTarget(IPackage package, InstallOptions? options)
    {
        if (options?.WithoutSavedVersion().Version is { Length: > 0 } requested)
            return requested;

        return package.IsUpgradable && package.NewVersionString.Length > 0 ? package.NewVersionString : null;
    }

    private static bool Reaches(IPackage package, string latest, string target) =>
        package.Manager.CompareVersions(latest, target) is int comparison
            ? comparison >= 0
            : latest.Equals(target, StringComparison.OrdinalIgnoreCase);

    private static NoOpUpdate ClassifyNoOpUpdate(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode,
        InstallOptions? options
    )
    {
        if (returnCode is not 0 || operation is not OperationType.Update || !UpgradedNothing(processOutput))
            return NoOpUpdate.None;

        if (LatestAvailableVersion(package, processOutput) is not { } latest)
            return NoOpUpdate.NotUpgraded;

        return UpdateTarget(package, options) is not { } target || Reaches(package, latest, target)
            ? NoOpUpdate.AlreadyCurrent
            : NoOpUpdate.NotUpgraded;
    }

    public ChocolateyPkgOperationHelper(Chocolatey manager)
        : base(manager) { }

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation
    )
    {
        List<string> parameters =
        [
            operation switch
            {
                OperationType.Install => Manager.Properties.InstallVerb,
                OperationType.Update => Manager.Properties.UpdateVerb,
                OperationType.Uninstall => Manager.Properties.UninstallVerb,
                _ => throw new InvalidDataException("Invalid package operation"),
            },
        ];
        parameters.AddRange([package.Id, "-y"]);

        if (options.InteractiveInstallation)
            parameters.Add("--notsilent");

        if (operation is OperationType.Install or OperationType.Update)
        {
            parameters.Add("--no-progress");

            if (options.Architecture == Architecture.x86)
                parameters.Add("--forcex86");

            if (options.PreRelease)
                parameters.Add("--prerelease");

            if (options.SkipHashCheck)
                parameters.AddRange(["--ignore-checksums", "--force"]);

            if (options.Version != "")
                parameters.AddRange(
                    [
                        CoreTools.EscapeCommandLineArgument($"--version={options.Version}"),
                        "--allow-downgrade",
                    ]
                );
        }

        parameters.Add(Chocolatey.GetProxyArgument());

        parameters.AddRange(
            operation switch
            {
                OperationType.Update => options.CustomParameters_Update,
                OperationType.Uninstall => options.CustomParameters_Uninstall,
                _ => options.CustomParameters_Install,
            }
        );

        return parameters;
    }

    protected override OperationVeredict _getOperationResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode
    ) => _getOperationResult(package, operation, processOutput, returnCode, null);

    protected override OperationVeredict _getOperationResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode,
        InstallOptions? options
    )
    {
        if (returnCode is 3010 or 1641)
        {
            return OperationVeredict.RestartRequired;
        }

        switch (ClassifyNoOpUpdate(package, operation, processOutput, returnCode, options))
        {
            case NoOpUpdate.AlreadyCurrent:
                return OperationVeredict.Success;
            case NoOpUpdate.NotUpgraded:
                return OperationVeredict.Failure;
        }

        if (returnCode is 1614 or 1605 or 0)
        {
            return OperationVeredict.Success;
        }

        string output_string = string.Join("\n", processOutput);
        if (
            package.OverridenOptions.RunAsAdministrator != true
            && !Settings.Get(Settings.K.ProhibitElevation)
            && (
                output_string.Contains("Run as administrator")
                || output_string.Contains("The requested operation requires elevation")
                || output_string.Contains("Access to the path")
                || output_string.Contains("Access denied")
                || output_string.Contains("is denied")
                || output_string.Contains(
                    "WARNING: Unable to create shortcut. Error captured was Unable to save shortcut"
                )
                || output_string.Contains("access denied")
                || output_string.Contains("requires Administrative permissions")
                || output_string.Contains("run from elevated prompt")
            )
        )
        {
            package.OverridenOptions.RunAsAdministrator = true;
            return OperationVeredict.AutoRetry;
        }

        return OperationVeredict.Failure;
    }

    public override string? GetFailureExplanation(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode,
        InstallOptions? options = null
    )
    {
        if (ClassifyNoOpUpdate(package, operation, processOutput, returnCode, options) is not NoOpUpdate.NotUpgraded)
            return null;

        return LatestAvailableVersion(package, processOutput) is { } latest
            ? CoreTools.Translate(
                "Chocolatey did not update {0}: version {1} is the newest one its sources offer",
                package.Name,
                latest
            )
            : CoreTools.Translate(
                "Chocolatey did not update {0}. The package may be pinned, or its sources may offer no newer version",
                package.Name
            );
    }
}
