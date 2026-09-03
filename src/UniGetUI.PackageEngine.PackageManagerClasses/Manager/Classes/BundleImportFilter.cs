using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Classes.Manager.Classes;

public static class BundleImportFilter
{
    public static bool CliArgumentsAllowed() =>
        SecureSettings.Get(SecureSettings.K.AllowCLIArguments)
        && SecureSettings.Get(SecureSettings.K.AllowImportingCLIArguments);

    public static bool PrePostCommandsAllowed() =>
        SecureSettings.Get(SecureSettings.K.AllowPrePostOpCommand)
        && SecureSettings.Get(SecureSettings.K.AllowImportPrePostOpCommands);

    public static InstallOptions Apply(
        ref BundleReport report,
        string packageId,
        InstallOptions options,
        bool allowCliArguments,
        bool allowPrePostCommands,
        bool commandLineIsShellInterpreted
    )
    {
        ReportList(
            ref report,
            packageId,
            options.CustomParameters_Install,
            "Custom install arguments",
            allowCliArguments
        );
        ReportList(
            ref report,
            packageId,
            options.CustomParameters_Update,
            "Custom update arguments",
            allowCliArguments
        );
        ReportList(
            ref report,
            packageId,
            options.CustomParameters_Uninstall,
            "Custom uninstall arguments",
            allowCliArguments
        );

        options.PreInstallCommand = ReportString(
            ref report,
            packageId,
            options.PreInstallCommand,
            "Pre-install command",
            allowPrePostCommands
        );
        options.PostInstallCommand = ReportString(
            ref report,
            packageId,
            options.PostInstallCommand,
            "Post-install command",
            allowPrePostCommands
        );
        options.PreUpdateCommand = ReportString(
            ref report,
            packageId,
            options.PreUpdateCommand,
            "Pre-update command",
            allowPrePostCommands
        );
        options.PostUpdateCommand = ReportString(
            ref report,
            packageId,
            options.PostUpdateCommand,
            "Post-update command",
            allowPrePostCommands
        );
        options.PreUninstallCommand = ReportString(
            ref report,
            packageId,
            options.PreUninstallCommand,
            "Pre-uninstall command",
            allowPrePostCommands
        );
        options.PostUninstallCommand = ReportString(
            ref report,
            packageId,
            options.PostUninstallCommand,
            "Post-uninstall command",
            allowPrePostCommands
        );

        // Only where a shell would reinterpret it. WinGet publishes versions such as
        // "2021 Update", and stripping those would install something other than what the bundle
        // asked for; that value reaches WinGet as a single quoted argument.
        if (commandLineIsShellInterpreted)
            options.Version = ReportOutOfPatternValue(
                ref report,
                packageId,
                options.Version,
                "Requested version"
            );
        return options;
    }

    private static void ReportList(
        ref BundleReport report,
        string packageId,
        List<string> values,
        string label,
        bool allowed
    )
    {
        if (!values.Any(value => value.Any()))
            return;

        Add(ref report, packageId, $"{label}: [{string.Join(", ", values)}]", allowed);

        if (!allowed)
            values.Clear();
    }

    private static string ReportString(
        ref BundleReport report,
        string packageId,
        string value,
        string label,
        bool allowed
    )
    {
        if (!value.Any())
            return value;

        Add(ref report, packageId, $"{label}: {value}", allowed);
        return allowed ? value : "";
    }

    private static string ReportOutOfPatternValue(
        ref BundleReport report,
        string packageId,
        string value,
        string label
    )
    {
        if (value.Length is 0 || CoreTools.IsCommandLineInertValue(value))
            return value;

        Add(ref report, packageId, $"{label}: {value}", false);
        return "";
    }

    private static void Add(ref BundleReport report, string packageId, string line, bool allowed)
    {
        if (!report.Contents.TryGetValue(packageId, out var entries))
        {
            entries = [];
            report.Contents[packageId] = entries;
        }

        entries.Add(new BundleReportEntry(line, allowed));
        report.IsEmpty = false;
    }
}
