using System.Text;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Interfaces;
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
        BundleReportSubject subject,
        InstallOptions options,
        bool allowCliArguments,
        bool allowPrePostCommands,
        bool commandLineIsShellInterpreted,
        string sourceName = "",
        BundleSourceStatus sourceStatus = BundleSourceStatus.Default
    )
    {
        ReportList(
            ref report,
            subject,
            options.CustomParameters_Install,
            nameof(options.CustomParameters_Install),
            "Custom install arguments",
            allowCliArguments
        );
        ReportList(
            ref report,
            subject,
            options.CustomParameters_Update,
            nameof(options.CustomParameters_Update),
            "Custom update arguments",
            allowCliArguments
        );
        ReportList(
            ref report,
            subject,
            options.CustomParameters_Uninstall,
            nameof(options.CustomParameters_Uninstall),
            "Custom uninstall arguments",
            allowCliArguments
        );

        options.PreInstallCommand = ReportString(
            ref report,
            subject,
            options.PreInstallCommand,
            nameof(options.PreInstallCommand),
            "Pre-install command",
            allowPrePostCommands
        );
        options.PostInstallCommand = ReportString(
            ref report,
            subject,
            options.PostInstallCommand,
            nameof(options.PostInstallCommand),
            "Post-install command",
            allowPrePostCommands
        );
        options.PreUpdateCommand = ReportString(
            ref report,
            subject,
            options.PreUpdateCommand,
            nameof(options.PreUpdateCommand),
            "Pre-update command",
            allowPrePostCommands
        );
        options.PostUpdateCommand = ReportString(
            ref report,
            subject,
            options.PostUpdateCommand,
            nameof(options.PostUpdateCommand),
            "Post-update command",
            allowPrePostCommands
        );
        options.PreUninstallCommand = ReportString(
            ref report,
            subject,
            options.PreUninstallCommand,
            nameof(options.PreUninstallCommand),
            "Pre-uninstall command",
            allowPrePostCommands
        );
        options.PostUninstallCommand = ReportString(
            ref report,
            subject,
            options.PostUninstallCommand,
            nameof(options.PostUninstallCommand),
            "Post-uninstall command",
            allowPrePostCommands
        );

        // Only where a shell would reinterpret it. WinGet publishes versions such as
        // "2021 Update", and stripping those would install something other than what the bundle
        // asked for; that value reaches WinGet as a single quoted argument.
        if (commandLineIsShellInterpreted)
            options.Version = ReportOutOfPatternValue(
                ref report,
                subject,
                options.Version,
                nameof(options.Version),
                "Requested version"
            );

        ReportFlag(
            ref report,
            subject,
            options.SkipHashCheck,
            nameof(options.SkipHashCheck),
            "Installer integrity check disabled",
            BundleReportSeverity.High
        );
        ReportFlag(
            ref report,
            subject,
            options.RunAsAdministrator,
            nameof(options.RunAsAdministrator),
            "Runs elevated",
            BundleReportSeverity.Info
        );
        ReportInformativeList(
            ref report,
            subject,
            options.KillBeforeOperation,
            nameof(options.KillBeforeOperation),
            "Processes terminated before the operation",
            BundleReportSeverity.High
        );
        ReportInformativeString(
            ref report,
            subject,
            options.CustomInstallLocation,
            nameof(options.CustomInstallLocation),
            "Custom install location",
            BundleReportSeverity.Info
        );

        if (sourceStatus is not BundleSourceStatus.Default)
            ReportInformativeString(
                ref report,
                subject,
                sourceName,
                "Source",
                sourceStatus is BundleSourceStatus.Unknown
                    ? "Unknown package source"
                    : "Non-default package source",
                BundleReportSeverity.Info
            );

        return options;
    }

    public static (string Name, BundleSourceStatus Status) ClassifySource(
        IPackageManager? manager,
        string declaredSource
    )
    {
        string name = declaredSource.Contains(": ")
            ? declaredSource.Split(": ")[^1]
            : declaredSource;

        if (manager is null || name.Length is 0 || !manager.Capabilities.SupportsCustomSources)
            return (name, BundleSourceStatus.Default);

        if (manager.DefaultSource.Name == name)
            return (name, BundleSourceStatus.Default);

        var factory = manager.SourcesHelper?.Factory;
        if (factory is null || factory.GetAvailableSources().Length is 0)
            return (name, BundleSourceStatus.Default);

        return (
            name,
            factory.GetSourceIfExists(name) is not null
                ? BundleSourceStatus.Known
                : BundleSourceStatus.Unknown
        );
    }

    private static void ReportList(
        ref BundleReport report,
        BundleReportSubject subject,
        List<string> values,
        string field,
        string label,
        bool allowed
    )
    {
        if (!values.Any(value => value.Any()))
            return;

        string value = string.Join(", ", values);
        Add(
            ref report,
            subject,
            field,
            label,
            value,
            $"{label}: [{value}]",
            BundleReportSeverity.High,
            allowed,
            !allowed
        );

        if (!allowed)
            values.Clear();
    }

    private static string ReportString(
        ref BundleReport report,
        BundleReportSubject subject,
        string value,
        string field,
        string label,
        bool allowed
    )
    {
        if (!value.Any())
            return value;

        Add(
            ref report,
            subject,
            field,
            label,
            value,
            $"{label}: {value}",
            BundleReportSeverity.High,
            allowed,
            !allowed
        );
        return allowed ? value : "";
    }

    private static string ReportOutOfPatternValue(
        ref BundleReport report,
        BundleReportSubject subject,
        string value,
        string field,
        string label
    )
    {
        if (value.Length is 0 || CoreTools.IsCommandLineInertValue(value))
            return value;

        Add(
            ref report,
            subject,
            field,
            label,
            value,
            $"{label}: {value}",
            BundleReportSeverity.High,
            false
        );
        return "";
    }

    private static void ReportFlag(
        ref BundleReport report,
        BundleReportSubject subject,
        bool value,
        string field,
        string label,
        BundleReportSeverity severity
    )
    {
        if (!value)
            return;

        Add(ref report, subject, field, label, "true", label, severity, true);
    }

    private static void ReportInformativeString(
        ref BundleReport report,
        BundleReportSubject subject,
        string value,
        string field,
        string label,
        BundleReportSeverity severity
    )
    {
        if (!value.Any())
            return;

        Add(ref report, subject, field, label, value, $"{label}: {value}", severity, true);
    }

    private static void ReportInformativeList(
        ref BundleReport report,
        BundleReportSubject subject,
        List<string> values,
        string field,
        string label,
        BundleReportSeverity severity
    )
    {
        if (!values.Any(value => value.Any()))
            return;

        string value = string.Join(", ", values);
        Add(ref report, subject, field, label, value, $"{label}: [{value}]", severity, true);
    }

    private static void Add(
        ref BundleReport report,
        BundleReportSubject subject,
        string field,
        string label,
        string value,
        string line,
        BundleReportSeverity severity,
        bool allowed,
        bool strippedBySetting = false
    )
    {
        if (!report.Contents.TryGetValue(subject.Key, out var package))
        {
            package = new BundleReportPackage(subject);
            report.Contents[subject.Key] = package;
        }

        package.Entries.Add(
            new BundleReportEntry(
                field,
                label,
                value,
                line,
                severity,
                allowed,
                strippedBySetting
            )
        );
        report.IsEmpty = false;
    }

    public static void LogReport(BundleReport report, string source)
    {
        if (report.IsEmpty)
            return;

        Logger.Warn(
            $"Bundle \"{Sanitize(source)}\" carries {report.HighSeverityCount} high-severity "
                + $"and {report.InformationalCount} informational security findings"
        );

        foreach (var package in report.Contents.Values)
            foreach (var entry in package.Entries)
                Logger.Warn(
                    $"  [{entry.Severity}] {Sanitize(package.Subject.Id)} "
                        + $"({Sanitize(package.Subject.ManagerName)}): {entry.Label}"
                        + (entry.Allowed ? "" : " -- stripped on import")
                );
    }

    private const int MaxLoggedLength = 120;

    private static string Sanitize(string value)
    {
        if (value.Length is 0)
            return value;

        var builder = new StringBuilder(Math.Min(value.Length, MaxLoggedLength));
        foreach (char character in value)
        {
            if (builder.Length >= MaxLoggedLength)
                return builder.Append("...").ToString();

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString();
    }
}
