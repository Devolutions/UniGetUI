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
        // uv refuses to upgrade a tool installed with an exact version pin, so its update
        // reinstalls the tool instead, which replaces the pin.
        bool reinstallsTool =
            operation is OperationType.Update && package.RequiresReinstallToUpdate;

        List<string> parameters =
        [
            reinstallsTool
                ? Manager.Properties.InstallVerb
                : operation switch
                {
                    OperationType.Install => Manager.Properties.InstallVerb,
                    OperationType.Update => Manager.Properties.UpdateVerb,
                    OperationType.Uninstall => Manager.Properties.UninstallVerb,
                    _ => throw new InvalidDataException("Invalid package operation"),
                },
        ];

        if (reinstallsTool)
            parameters.Add("--upgrade");

        parameters.Add(
            options.Version.Any()
                ? CoreTools.EscapeCommandLineArgument($"{package.Id}=={options.Version}")
                : package.Id
        );

        if (operation is not OperationType.Uninstall)
        {
            if (options.PreRelease)
            {
                parameters.Add("--prerelease");
                parameters.Add("allow");
            }

            ResolvedPythonOptions python = ResolvePythonOptions(package, options, operation);

            if (python.Request is not null)
            {
                parameters.Add("--python");
                parameters.Add(CoreTools.EscapeCommandLineArgument(python.Request));
            }

            if (python.ManagedPython is bool managed)
                parameters.Add(managed ? "--managed-python" : "--no-managed-python");

            if (python.NoPythonDownloads)
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
    /// <c>package.OverridenOptions</c>. Only an explicit choice is copied, and it replaces the
    /// previous one down to its cleared value, so choosing Default really clears the interpreter
    /// instead of leaving the earlier one in place.
    /// </summary>
    public override void ApplyOperationOverrides(
        IPackage package,
        InstallOptions options,
        OperationType operation
    )
    {
        if (!PythonOptionsWereChosen(options))
            return;

        package.OverridenOptions.Uv_PythonRequest =
            options.Uv_PythonRequest.Length > 0 ? options.Uv_PythonRequest : null;
        package.OverridenOptions.Uv_ManagedPython =
            bool.TryParse(options.Uv_ManagedPython, out bool managedPython) ? managedPython : null;
        package.OverridenOptions.Uv_NoPythonDownloads = options.Uv_NoPythonDownloads;
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

    /// <summary>
    /// Returns what UniGetUI installed this tool with, so the install-options dialog can show it
    /// instead of starting from Default.
    /// </summary>
    public override PythonInterpreterOptions? GetLastUsedPythonOptions(IPackage package)
    {
        UvToolInstallRecord? recorded = UvToolInstallRecords.Get(package.Id);
        return recorded is null
            ? null
            : new PythonInterpreterOptions(
                recorded.PythonRequest,
                recorded.ManagedPython,
                recorded.NoPythonDownloads
            );
    }

    protected override OperationVeredict _getOperationResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode
    ) => _getOperationResult(package, operation, processOutput, returnCode, options: null);

    protected override OperationVeredict _getOperationResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode,
        InstallOptions? options
    )
    {
        if (returnCode != 0)
            return OperationVeredict.Failure;

        switch (operation)
        {
            case OperationType.Install or OperationType.Update:
                {
                    // Store what this operation actually used. An explicit Default therefore
                    // clears the interpreter, and the flags no longer accumulate over time.
                    ResolvedPythonOptions used = ResolvePythonOptions(package, options, operation);
                    UvToolInstallRecords.Record(
                        new UvToolInstallRecord
                        {
                            Id = package.Id,
                            Version = optionsVersionFor(package),
                            PythonRequest = used.Request,
                            ManagedPython = used.ManagedPython,
                            NoPythonDownloads = used.NoPythonDownloads,
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

    /// <summary>
    /// True when the options themselves say which interpreter to use, including an explicit
    /// Default. Any value already set counts, so options saved before the flag existed keep
    /// working, and the flag is only needed to express "Default on purpose".
    /// </summary>
    private static bool PythonOptionsWereChosen(InstallOptions? options) =>
        options is not null
        && (
            options.Uv_PythonOptionsChosen
            || options.Uv_PythonRequest.Length > 0
            || options.Uv_ManagedPython.Length > 0
            || options.Uv_NoPythonDownloads
        );

    /// <summary>
    /// Resolves the interpreter flags for an operation and, at the same time, for the record
    /// written afterwards. An explicit choice wins outright; otherwise <c>OverridenOptions</c>
    /// and the record from the last install fill in.
    /// </summary>
    private static ResolvedPythonOptions ResolvePythonOptions(
        IPackage package,
        InstallOptions? options,
        OperationType operation
    )
    {
        UvToolInstallRecord? recorded = operation is OperationType.Update
            ? UvToolInstallRecords.Get(package.Id)
            : null;

        if (PythonOptionsWereChosen(options))
        {
            return new ResolvedPythonOptions(
                options!.Uv_PythonRequest.Length > 0 ? options.Uv_PythonRequest : null,
                bool.TryParse(options.Uv_ManagedPython, out bool managedPython)
                    ? managedPython
                    : null,
                options.Uv_NoPythonDownloads
            );
        }

        OverridenInstallationOptions overrides = package.OverridenOptions;
        return new ResolvedPythonOptions(
            overrides.Uv_PythonRequest ?? recorded?.PythonRequest,
            overrides.Uv_ManagedPython ?? recorded?.ManagedPython,
            overrides.Uv_NoPythonDownloads || (recorded?.NoPythonDownloads ?? false)
        );
    }

    private sealed record ResolvedPythonOptions(
        string? Request,
        bool? ManagedPython,
        bool NoPythonDownloads
    );

    private static string optionsVersionFor(IPackage package)
    {
        if (package.NewVersionString is { } newer && newer.Length > 0)
            return newer;
        return package.VersionString;
    }
}
