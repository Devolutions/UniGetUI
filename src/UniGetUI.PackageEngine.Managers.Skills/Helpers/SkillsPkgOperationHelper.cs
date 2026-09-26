using Devolutions.AgentSkills;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.SkillsManager;

/// <summary>
/// Installs, updates and removes skills through the library, inside UniGetUI. Skills have no
/// command line of their own, so the command-line methods refuse, which also keeps them out of the
/// command preview, manual installs and exported scripts.
/// </summary>
internal sealed class SkillsPkgOperationHelper : BasePkgOperationHelper, IInProcessPackageOperationHelper
{
    // The library serializes its lock-file writes, but two installs from one repository would each
    // fetch it, so skill operations run one at a time
    private static readonly SemaphoreSlim OperationGate = new(1, 1);

    private readonly AgentSkills _skills;

    public SkillsPkgOperationHelper(AgentSkills manager)
        : base(manager)
    {
        _skills = manager;
    }

    public async Task<OperationVeredict> PerformAsync(
        IPackage package,
        InstallOptions options,
        OperationType operation,
        IOperationOutput output,
        CancellationToken cancellationToken
    )
    {
        await OperationGate.WaitAsync(cancellationToken);
        try
        {
            var progress = new OutputProgress(output);
            return await Task.Run(
                () =>
                    operation switch
                    {
                        OperationType.Install => Install(package, output, progress, cancellationToken),
                        OperationType.Update => Update(package, output, progress, cancellationToken),
                        OperationType.Uninstall => Uninstall(package, output, progress, cancellationToken),
                        _ => throw new InvalidOperationException($"Unsupported operation {operation}"),
                    },
                cancellationToken
            );
        }
        catch (Exception ex) when (ex is SkillsException or ArgumentException)
        {
            return Fail(output, ex.Message);
        }
        finally
        {
            OperationGate.Release();
        }
    }

    private OperationVeredict Install(
        IPackage package,
        IOperationOutput output,
        IProgress<string> progress,
        CancellationToken cancellationToken
    )
    {
        if (_skills.GetInstallSource(package.Source) is not { } source)
            return Fail(output, NotASourceMessage(package.Source.Name));

        var agents = AgentSkills.GetTargetAgents();
        output.Verbose($"Installing {package.Id} from {source.InstallSource}");
        output.Verbose(
            agents is null
                ? "Target agents: the detected agents and the agents that read .agents/skills"
                : $"Target agents: {string.Join(", ", agents)}"
        );

        var result = _skills.Backend.Install(source.InstallSource, package.Id, agents, progress, cancellationToken);
        var outcome =
            result.Skills.FirstOrDefault(s => s.Name.Equals(package.Id, StringComparison.OrdinalIgnoreCase))
            ?? result.Skills.FirstOrDefault();
        if (outcome is null)
            return Fail(output, CoreTools.Translate("{0} could not be installed", package.Name));

        if (outcome.Status is not SkillOperationStatus.Succeeded)
            return Fail(output, outcome.Error ?? CoreTools.Translate("{0} could not be installed", package.Name));

        output.Info($"Installed {outcome.Name} for {string.Join(", ", outcome.Agents)}");
        if (outcome.SkippedAgents.Count > 0)
            output.Info($"Skipped {string.Join(", ", outcome.SkippedAgents)}: no skills folder for them");
        return OperationVeredict.Success;
    }

    private OperationVeredict Update(
        IPackage package,
        IOperationOutput output,
        IProgress<string> progress,
        CancellationToken cancellationToken
    )
    {
        // An update found by the last check is applied as found. Otherwise the skill is checked again,
        // and its update is held to the same sources: the library updates a skill from the source it
        // was installed from, whatever source the package names.
        if (_skills.TakePendingUpdate(package.Id) is not { } update)
        {
            var check = _skills.Backend.CheckForUpdates([package.Id], progress, cancellationToken);
            if (check.Unchecked.FirstOrDefault(s => s.Name.Equals(package.Id, StringComparison.OrdinalIgnoreCase)) is { } skipped)
            {
                output.Error(skipped.Reason);
                return Fail(output, CoreTools.Translate("{0} could not be updated", package.Name));
            }

            var found = check.Updates.FirstOrDefault(u => u.Name.Equals(package.Id, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                output.Info($"{package.Id} is already up to date");
                return OperationVeredict.Success;
            }

            var installed = _skills
                .Backend.GetInstalledSkills(cancellationToken)
                .FirstOrDefault(s => s.Name.Equals(package.Id, StringComparison.OrdinalIgnoreCase));
            var source = AgentSkills.GetUpdateSource(found, installed);
            if (source is null || !AgentSkills.IsAllowedSource(source))
                return Fail(output, NotASourceMessage(source?.Name ?? package.Source.Name));

            update = found;
        }

        var result = _skills.Backend.Update([update], progress, cancellationToken);
        var outcome = result.Skills.FirstOrDefault(s => s.Name.Equals(package.Id, StringComparison.OrdinalIgnoreCase));
        if (outcome is null)
        {
            output.Info($"{package.Id} is already up to date");
            return OperationVeredict.Success;
        }

        return outcome.Status is SkillOperationStatus.Succeeded
            ? OperationVeredict.Success
            : Fail(output, outcome.Error ?? CoreTools.Translate("{0} could not be updated", package.Name));
    }

    private OperationVeredict Uninstall(
        IPackage package,
        IOperationOutput output,
        IProgress<string> progress,
        CancellationToken cancellationToken
    )
    {
        var result = _skills.Backend.Remove(package.Id, progress, cancellationToken);
        var outcome = result.Skills.FirstOrDefault();
        switch (outcome?.Status)
        {
            case SkillOperationStatus.Succeeded:
                return OperationVeredict.Success;
            case SkillOperationStatus.NotFound:
                output.Info($"{package.Id} was already removed");
                return OperationVeredict.Success;
            default:
                return Fail(output, outcome?.Error ?? CoreTools.Translate("{0} could not be uninstalled", package.Name));
        }
    }

    private static string NotASourceMessage(string source) =>
        CoreTools.Translate(
            "{0} is not one of your Agent Skills sources. Add this source first, then try again.",
            source
        );

    private static OperationVeredict Fail(IOperationOutput output, string message)
    {
        output.Error(message);
        output.SetFailureMessage(message);
        return OperationVeredict.Failure;
    }

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation
    ) => throw new InvalidOperationException($"{Manager.DisplayName} runs inside UniGetUI and has no command line");

    protected override OperationVeredict _getOperationResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode
    ) => throw new InvalidOperationException($"{Manager.DisplayName} runs inside UniGetUI and has no process output");

    /// <summary>Forwards the library's progress lines to the operation's output, as they arrive.</summary>
    private sealed class OutputProgress(IOperationOutput output) : IProgress<string>
    {
        public void Report(string value) => output.Info(value);
    }
}
