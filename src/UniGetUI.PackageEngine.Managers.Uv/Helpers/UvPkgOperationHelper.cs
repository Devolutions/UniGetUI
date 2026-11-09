using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Managers.UvManager.Internal;
using UniGetUI.PackageEngine.Managers.UvManager.Models;
using UniGetUI.PackageEngine.Managers.UvManager.Services;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;

namespace UniGetUI.PackageEngine.Managers.UvManager.Helpers;

internal sealed class UvPkgOperationHelper : BasePkgOperationHelper
{
    public UvPkgOperationHelper(Uv manager)
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

        parameters.Add(
            options.Version.Any()
                ? CoreTools.EscapeCommandLineArgument($"{package.Id}=={options.Version}")
                : package.Id
        );

        if (operation is not OperationType.Uninstall)
        {
            OverridenInstallationOptions overrides = package.OverridenOptions;
            UvToolInstallRecord? recorded = operation is OperationType.Update
                ? UvToolInstallRecords.Get(package.Id)
                : null;

            // The dialog previews a command through GetStandaloneParameters, which hands the
            // options straight to this method without applying them to the package. Read the
            // options first so the preview matches the operation the same dialog would start.
            string? pythonRequest = options.Uv_PythonRequest.Length > 0
                ? options.Uv_PythonRequest
                : overrides.Uv_PythonRequest ?? recorded?.PythonRequest;
            bool? managedPython = bool.TryParse(options.Uv_ManagedPython, out bool chosenManagedPython)
                ? chosenManagedPython
                : overrides.Uv_ManagedPython ?? recorded?.ManagedPython;
            bool noPythonDownloads = options.Uv_NoPythonDownloads
                || overrides.Uv_NoPythonDownloads
                || (recorded?.NoPythonDownloads ?? false);

            if (pythonRequest is not null)
            {
                parameters.Add("--python");
                parameters.Add(CoreTools.EscapeCommandLineArgument(pythonRequest));
            }

            if (managedPython is bool managed)
                parameters.Add(managed ? "--managed-python" : "--no-managed-python");

            if (noPythonDownloads)
                parameters.Add("--no-python-downloads");
        }

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

    /// <summary>
    /// Copies the uv-specific options persisted by the install-options GUI into
    /// <c>package.OverridenOptions</c> for <see cref="_getOperationParameters"/> to read, and
    /// never clobbers an override that is already set with an unset value.
    /// </summary>
    public override void ApplyOperationOverrides(
        IPackage package,
        InstallOptions options,
        OperationType operation
    )
    {
        if (!string.IsNullOrWhiteSpace(options.Uv_PythonRequest))
            package.OverridenOptions.Uv_PythonRequest = options.Uv_PythonRequest;

        if (bool.TryParse(options.Uv_ManagedPython, out bool managedPython))
            package.OverridenOptions.Uv_ManagedPython = managedPython;

        if (options.Uv_NoPythonDownloads)
            package.OverridenOptions.Uv_NoPythonDownloads = true;
    }

    /// <summary>
    /// Reads the interpreter catalogue through <see cref="UvPythonService"/> so the
    /// install-options dialog can offer a <c>--python</c> request. Entries already on disk
    /// come first, then the ones uv could download on demand.
    /// </summary>
    public override IReadOnlyList<PythonInterpreterChoice> ListPythonInterpreters()
    {
        List<PythonInterpreterChoice> installed = [];
        List<PythonInterpreterChoice> downloadable = [];
        try
        {
            foreach (UvPythonInstall interpreter in UvPythonService.List((Uv)Manager, allVersions: false))
            {
                var choice = new PythonInterpreterChoice(
                    Request: interpreter.Key,
                    DisplayName: DisplayNameFor(interpreter),
                    Installed: interpreter.Path is not null,
                    Downloadable: interpreter.DownloadUrl is not null && interpreter.Path is null
                );
                if (choice.Installed)
                    installed.Add(choice);
                else if (choice.Downloadable)
                    downloadable.Add(choice);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(
                $"Could not list the Python interpreters uv knows about: {ex.Message}"
            );
        }

        installed.AddRange(downloadable);
        return installed;
    }

    private static string DisplayNameFor(UvPythonInstall interpreter)
    {
        List<string> parts =
        [
            interpreter.Implementation switch
            {
                "cpython" => "CPython",
                "pypy" => "PyPy",
                "graalpy" => "GraalPy",
                _ => interpreter.Implementation,
            },
            interpreter.Version,
        ];
        if (interpreter.Variant is not ("" or "default"))
            parts.Add(interpreter.Variant);
        return string.Join(' ', parts);
    }

    protected override OperationVeredict _getOperationResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode
    )
    {
        if (returnCode != 0)
            return OperationVeredict.Failure;

        switch (operation)
        {
            case OperationType.Install or OperationType.Update:
                {
                    OverridenInstallationOptions overrides = package.OverridenOptions;
                    UvToolInstallRecord? recorded = overrides.Uv_PythonRequest is null
                        ? UvToolInstallRecords.Get(package.Id)
                        : null;

                    string? pythonRequest = overrides.Uv_PythonRequest ?? recorded?.PythonRequest;
                    UvToolInstallRecords.Record(
                        new UvToolInstallRecord
                        {
                            Id = package.Id,
                            Version = optionsVersionFor(package),
                            PythonRequest = pythonRequest,
                            ManagedPython = overrides.Uv_ManagedPython ?? recorded?.ManagedPython,
                            NoPythonDownloads = overrides.Uv_NoPythonDownloads
                                || (recorded?.NoPythonDownloads ?? false),
                            InstalledAt = DateTime.UtcNow.ToString("o"),
                            Source = package.Source.Name,
                            SchemaVersion = UvToolInstallRecords.SchemaVersion,
                        }
                    );
                    break;
                }
            case OperationType.Uninstall:
                UvToolInstallRecords.Forget(package.Id);
                break;
        }

        return OperationVeredict.Success;
    }

    private static string optionsVersionFor(IPackage package)
    {
        if (package.NewVersionString is { } newer && newer.Length > 0)
            return newer;
        return package.VersionString;
    }
}
