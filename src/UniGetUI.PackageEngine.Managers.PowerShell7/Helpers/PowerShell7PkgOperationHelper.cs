using System.Text.RegularExpressions;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.PowerShell7Manager;

internal sealed partial class PowerShell7PkgOperationHelper : BasePkgOperationHelper
{
    public PowerShell7PkgOperationHelper(PowerShell7 manager)
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
        parameters.AddRange(["-Name", package.Id, "-Confirm:$false"]);

        if (operation is OperationType.Install)
        {
            if (options.Version != "")
                parameters.AddRange(["-Version", options.Version]);
        }
        else if (operation is OperationType.Update)
        {
            parameters.Add("-Force");
        }
        else if (operation is OperationType.Uninstall)
        {
            if (!CoreTools.IsValidPackageVersion(package.VersionString))
                throw new InvalidOperationException(
                    $"Refusing to build a {Manager.Name} command line for package {package.Id}: the installed version \"{package.VersionString}\" is not a valid package version."
                );

            parameters.AddRange(["-Version", package.VersionString]);
        }

        if (operation is not OperationType.Uninstall)
        {
            parameters.AddRange(["-TrustRepository", "-AcceptLicense"]);

            if (options.PreRelease)
                parameters.Add("-Prerelease");
        }

        List<string> customParameters = operation switch
        {
            OperationType.Update => options.CustomParameters_Update,
            OperationType.Uninstall => options.CustomParameters_Uninstall,
            _ => options.CustomParameters_Install,
        };

        // Uninstall targets the scope the copy was detected in; otherwise the scope chosen in the
        // options dialog wins, falling back to the auto-detected install scope
        string? scope = operation is OperationType.Uninstall
            ? package.OverridenOptions.Scope
            : options.InstallationScope.Length > 0
                ? options.InstallationScope
                : package.OverridenOptions.Scope;

        if (!customParameters.Any(IsScopeParameter))
            parameters.AddRange(["-Scope", scope == PackageScope.Global ? "AllUsers" : "CurrentUser"]);

        parameters.AddRange(customParameters);

        return parameters;
    }

    private static bool IsScopeParameter(string argument)
    {
        if (!argument.StartsWith('-'))
            return false;

        string name = argument[1..].Split(':', 2)[0];
        return name.Length >= 2 && "Scope".StartsWith(name, StringComparison.OrdinalIgnoreCase);
    }

    protected override OperationVeredict _getOperationResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode
    )
    {
        string output_string = string.Join("\n", processOutput);

        bool needsElevation =
            output_string.Contains("AdminPrivilegesAreRequired")
            || output_string.Contains("AdminPrivilegeRequired");

        if (operation is OperationType.Uninstall && returnCode != 0)
        {
            var failures = NotDeletedRegex()
                .Matches(output_string)
                .Select(match => IsLeftoverOnly(match, package.Id))
                .ToArray();

            if (failures.Length > 0 && failures.All(leftoverOnly => leftoverOnly))
                return OperationVeredict.Success;

            needsElevation |=
                failures.Length > 0 && package.OverridenOptions.Scope == PackageScope.Global;
        }

        if (package.OverridenOptions.RunAsAdministrator is not true && needsElevation)
        {
            package.OverridenOptions.RunAsAdministrator = true;
            return OperationVeredict.AutoRetry;
        }

        return returnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;
    }

    private static bool IsLeftoverOnly(Match match, string packageId) =>
        match.Groups["kind"].Value switch
        {
            "Script metadata file" => true,
            "Parent directory" => Path.GetFileName(
                    match.Groups["path"].Value.TrimEnd('\\', '/')
                )
                .Equals(packageId, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    [GeneratedRegex(@"(?<kind>Parent directory|Script metadata file|Script) '(?<path>[^']+)' could not be deleted")]
    private static partial Regex NotDeletedRegex();
}
