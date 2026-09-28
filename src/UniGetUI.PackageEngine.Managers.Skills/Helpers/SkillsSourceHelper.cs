using Devolutions.AgentSkills;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.Providers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;

namespace UniGetUI.PackageEngine.Managers.SkillsManager;

/// <summary>
/// The skill sources are UniGetUI's own: the skills.sh catalog (on unless removed) and the
/// repositories and indexes the user added, kept in the settings rather than by a command-line tool.
/// </summary>
internal sealed class SkillsSourceHelper : BaseSourceHelper, IInProcessSourceHelper
{
    private readonly AgentSkills _skills;

    public SkillsSourceHelper(AgentSkills manager)
        : base(manager, manager.SourceFactory)
    {
        _skills = manager;
    }

    protected override IReadOnlyList<IManagerSource> GetSources_UnSafe() =>
        AgentSkills.GetConfiguredSources().Select(_skills.SourceFactory.GetOrCreate).ToArray();

    public Task<OperationVeredict> AddSourceAsync(
        IManagerSource source,
        IOperationOutput output,
        CancellationToken cancellationToken
    ) => Task.Run(() => AddSource(source, output, cancellationToken), cancellationToken);

    public Task<OperationVeredict> RemoveSourceAsync(
        IManagerSource source,
        IOperationOutput output,
        CancellationToken cancellationToken
    ) => Task.Run(() => RemoveSource(source), cancellationToken);

    private OperationVeredict AddSource(
        IManagerSource source,
        IOperationOutput output,
        CancellationToken cancellationToken
    )
    {
        var locator = _skills.SourceFactory.GetLocator(source);
        if (locator is null)
        {
            string message = CoreTools.Translate(
                "{0} is not a skill source. Enter a GitHub repository (owner/repo), a git repository URL, the address of a website that publishes skills, or the address of a Notion skills database, using HTTPS and without a user name or password.",
                WithoutCredentials(source.Url)
            );
            output.Error(message);
            output.SetFailureMessage(message);
            return OperationVeredict.Failure;
        }

        if (locator.Kind is SkillSourceKind.Catalog)
        {
            Settings.Set(Settings.K.DisableSkillsPublicCatalog, false);
            InvalidateSourcesCache();
            return OperationVeredict.Success;
        }

        if (AgentSkills.GetConfiguredSources().Any(s => s.Name.Equals(locator.Name, StringComparison.OrdinalIgnoreCase)))
        {
            output.Info($"{locator.Name} is already one of the sources");
            return OperationVeredict.Success;
        }

        // A source that offers no skill (a mistyped repository, a website without an index) is not
        // worth adding
        output.Info($"Looking for skills in {locator.InstallSource}");
        IReadOnlyList<AvailableSkill> skills;
        try
        {
            skills = _skills.Backend.GetAvailableSkills(
                locator.InstallSource,
                new OutputProgress(output),
                cancellationToken
            );
        }
        catch (SkillsException ex)
            when (locator.Kind is SkillSourceKind.Notion
                && ex.Failure is SkillsFailure.NotionCliMissing or SkillsFailure.NotionSignedOut)
        {
            // Its skills show once the Notion CLI is installed and signed in, in the Agent Skills settings
            output.Info(ex.Message);
            output.Info(
                CoreTools.Translate(
                    "Sign in to Notion in the Agent Skills settings to see the skills of this source."
                )
            );
            Settings.AddToList(AgentSkills.SourcesListKey, locator.InstallSource);
            _skills.Listings.Forget(locator);
            InvalidateSourcesCache();
            return OperationVeredict.Success;
        }
        catch (SkillsException ex)
        {
            output.Error(ex.Message);
            output.SetFailureMessage(ex.Message);
            return OperationVeredict.Failure;
        }

        output.Info($"Found {skills.Count} skills");

        Settings.AddToList(AgentSkills.SourcesListKey, locator.InstallSource);
        _skills.Listings.Store(locator, skills);
        InvalidateSourcesCache();
        return OperationVeredict.Success;
    }

    private OperationVeredict RemoveSource(IManagerSource source)
    {
        var locator = _skills.SourceFactory.GetLocator(source);
        if (locator?.Kind is SkillSourceKind.Catalog)
        {
            Settings.Set(Settings.K.DisableSkillsPublicCatalog, true);
            InvalidateSourcesCache();
            return OperationVeredict.Success;
        }

        string name = locator?.Name ?? source.Name;
        var remaining = (Settings.GetList<string>(AgentSkills.SourcesListKey) ?? [])
            .Where(entry =>
                !(SkillSourceLocator.Parse(entry)?.Name ?? entry).Equals(name, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        Settings.SetList(AgentSkills.SourcesListKey, remaining);
        if (locator is not null)
            _skills.Listings.Forget(locator);

        InvalidateSourcesCache();
        return OperationVeredict.Success;
    }

    // The address as entered, without the user name and password it may carry, which would otherwise
    // be shown and kept in the operation history
    private static string WithoutCredentials(Uri address) =>
        address.IsAbsoluteUri && address.UserInfo.Length > 0
            ? address.GetComponents(
                UriComponents.SchemeAndServer | UriComponents.PathAndQuery | UriComponents.Fragment,
                UriFormat.UriEscaped
            )
            : address.OriginalString;

    public override string[] GetAddSourceParameters(IManagerSource source) =>
        throw new InvalidOperationException($"{Manager.DisplayName} adds sources inside UniGetUI");

    public override string[] GetRemoveSourceParameters(IManagerSource source) =>
        throw new InvalidOperationException($"{Manager.DisplayName} removes sources inside UniGetUI");

    protected override OperationVeredict _getAddSourceOperationVeredict(
        IManagerSource source,
        int ReturnCode,
        string[] Output
    ) => throw new InvalidOperationException($"{Manager.DisplayName} adds sources inside UniGetUI");

    protected override OperationVeredict _getRemoveSourceOperationVeredict(
        IManagerSource source,
        int ReturnCode,
        string[] Output
    ) => throw new InvalidOperationException($"{Manager.DisplayName} removes sources inside UniGetUI");

    private sealed class OutputProgress(IOperationOutput output) : IProgress<string>
    {
        public void Report(string value) => output.Info(value);
    }
}
