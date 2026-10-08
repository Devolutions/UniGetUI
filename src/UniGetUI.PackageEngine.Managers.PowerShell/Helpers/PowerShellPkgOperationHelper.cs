using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.PowerShellManager;

internal sealed class PowerShellPkgOperationHelper : BasePkgOperationHelper
{
    internal const string ErrorVariableName = "UniGetUIOperationError";

    public PowerShellPkgOperationHelper(PowerShell manager)
        : base(manager) { }

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation
    ) => _getOperationParameters(package, options, operation, standalone: false);

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation,
        bool standalone
    )
    {
        bool updatesThroughInstall =
            operation is OperationType.Update && options.SkipHashCheck;
        bool usesInstallVerb = operation is OperationType.Install || updatesThroughInstall;

        List<string> parameters =
        [
            usesInstallVerb
                ? Manager.Properties.InstallVerb
                : operation switch
                {
                    OperationType.Update => Manager.Properties.UpdateVerb,
                    OperationType.Uninstall => Manager.Properties.UninstallVerb,
                    _ => throw new InvalidDataException("Invalid package operation"),
                },
        ];
        parameters.AddRange(["-Name", package.Id, "-Confirm:$false", "-Force"]);

        if (!standalone)
            package.OverridenOptions.PowerShell_UpdateThroughInstall = updatesThroughInstall;

        bool shellInterpreted = standalone || Manager.Status.OperationCallArgs.Count is 0;

        if (updatesThroughInstall && package.Source.Name.Length > 0)
            parameters.AddRange(
                [
                    "-Repository",
                    shellInterpreted ? QuoteForPowerShell(package.Source.Name) : package.Source.Name,
                ]
            );

        if (operation is not OperationType.Uninstall)
        {
            if (options.PreRelease)
                parameters.Add("-AllowPrerelease");

            // Update-Module (PowerShellGet) has no -Scope parameter; only Install-Module accepts it
            if (usesInstallVerb && !package.OverridenOptions.PowerShell_DoNotSetScopeParameter)
            {
                // The scope chosen in the options dialog wins; fall back to the auto-detected install scope
                string scope = options.InstallationScope.Length > 0
                    ? options.InstallationScope
                    : package.OverridenOptions.Scope ?? "";

                if (operation is OperationType.Install || scope.Length > 0)
                    parameters.AddRange(
                        ["-Scope", scope == PackageScope.Global ? "AllUsers" : "CurrentUser"]
                    );
            }
        }

        if (usesInstallVerb)
        {
            if (package.OverridenOptions.PowerShell_AllowClobber)
                parameters.Add("-AllowClobber");

            if (options.SkipHashCheck)
                parameters.Add("-SkipPublisherCheck");

            if (operation is OperationType.Install && options.Version != "")
                parameters.AddRange(["-RequiredVersion", options.Version]);
        }

        IReadOnlyList<string> customParameters = operation switch
        {
            OperationType.Update => options.CustomParameters_Update,
            OperationType.Uninstall => options.CustomParameters_Uninstall,
            _ => options.CustomParameters_Install,
        };

        bool bindsOwnErrorVariable = customParameters.Any(parameter =>
            parameter.StartsWith("-ev", StringComparison.OrdinalIgnoreCase)
            || parameter.StartsWith("-errorv", StringComparison.OrdinalIgnoreCase)
        );

        if (!bindsOwnErrorVariable)
            parameters.AddRange(["-ErrorVariable", ErrorVariableName]);

        parameters.AddRange(customParameters);

        // The launcher owns the TLS selection and the error check when it is in use. When the call
        // falls back to -Command, and when the caller needs a command line that stands on its own,
        // both have to be script fragments in the parameter list as they were before.
        if (standalone || Manager.Status.OperationCallArgs.Count is 0)
        {
            if (operation is not OperationType.Uninstall)
                parameters.Insert(
                    0,
                    "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12;"
                );

            if (!bindsOwnErrorVariable)
                parameters.Add($";if(${ErrorVariableName}){{exit(1)}}");
        }

        return parameters;
    }

    private static string QuoteForPowerShell(string value) =>
        $"'{value.Replace("'", "''")}'";

    protected override OperationVeredict _getOperationResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode
    )
    {
        string output_string = string.Join("\n", processOutput);

        bool routedThroughInstall = package.OverridenOptions.PowerShell_UpdateThroughInstall;
        package.OverridenOptions.PowerShell_UpdateThroughInstall = false;

        if (
            package.OverridenOptions.RunAsAdministrator is not true
            && (
                output_string.Contains("AdminPrivilegesAreRequired")
                || output_string.Contains("AdminPrivilegeRequired")
            )
        )
        {
            package.OverridenOptions.RunAsAdministrator = true;
            return OperationVeredict.AutoRetry;
        }

        if (
            output_string.Contains("Scope")
            && output_string.Contains("NamedParameterNotFound")
            && !package.OverridenOptions.PowerShell_DoNotSetScopeParameter
        )
        {
            package.OverridenOptions.PowerShell_DoNotSetScopeParameter = true;
            return OperationVeredict.AutoRetry;
        }

        if (
            (operation is OperationType.Install || routedThroughInstall)
            && output_string.Contains("CommandAlreadyAvailable")
            && !package.OverridenOptions.PowerShell_AllowClobber
        )
        {
            package.OverridenOptions.PowerShell_AllowClobber = true;
            return OperationVeredict.AutoRetry;
        }

        return returnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;
    }
}
