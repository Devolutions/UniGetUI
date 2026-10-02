using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Devolutions.Now.Policy.Api;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;
using UniGetUIArchitecture = UniGetUI.PackageEngine.Enums.Architecture;

namespace UniGetUI.PackageEngine.AgentBroker;

/// <summary>
/// Thrown when a package operation cannot be sent to the package broker because the broker
/// would reject one of its fields. <see cref="Issues"/> holds one localized explanation per problem.
/// </summary>
public sealed class BrokerRequestValidationException(IReadOnlyList<string> issues)
    : InvalidOperationException(string.Join(Environment.NewLine, issues))
{
    public IReadOnlyList<string> Issues { get; } = issues;
}

/// <summary>
/// Client-side checks that mirror the field rules the package broker applies to package
/// operation requests, so that UniGetUI can explain a rejection before sending the request.
/// The broker remains the authority: these checks only cover rules that are known to be
/// enforced, and an accepted request can still be rejected or denied by the broker.
/// </summary>
public static partial class BrokerRequestValidator
{
    /// <summary>Characters that the broker refuses in values written to a batch script.</summary>
    private static readonly char[] BatchMetacharacters = ['"', '%', '!', '^', '&', '|', '<', '>', '\r', '\n', '\0'];

    private const string BatchMetacharactersDisplay = "\" % ! ^ & | < >";
    private const int MaxVersionLength = 128;
    private const int MaxSourceNameLength = 128;
    private const int MaxPackageIdLength = 256;
    private const int MaxCustomParameterLength = 512;

    /// <summary>
    /// Returns a localized explanation for every field of the given operation that the package
    /// broker is known to reject. An empty list means no known rule is violated.
    /// </summary>
    /// <param name="installLocation">The install location that would be sent, or null.</param>
    /// <param name="customParameters">
    /// The custom parameters that would be sent, when they differ from the ones saved in
    /// <paramref name="options"/> for the role (for example a parameter added by a retry).
    /// </param>
    public static IReadOnlyList<string> Validate(
        IPackage package,
        InstallOptions options,
        OperationType role,
        string? installLocation,
        IReadOnlyList<string>? customParameters = null)
    {
        if (!BrokerRequestBuilder.TryMapManagerName(package.Manager.Name, out ManagerName manager))
        {
            return [];
        }

        string managerName = package.Manager.DisplayName;
        List<string> issues = [];

        // Brokered operations do not go through BasePkgOperationHelper, so the command-line
        // safety guards that apply to every manager are repeated here: the broker builds a
        // command line from these values, and an identifier such as
        // "requests --index-url https://host" would become real options.
        // Only installs send the saved version (see BrokerRequestBuilder.ResolveVersion), so a
        // saved value never blocks an update or an uninstall.
        string requestedVersion = role is OperationType.Install ? options.Version : "";
        bool idIsOptionSafe = CoreTools.IsOptionSafeIdentifier(
            package.Id,
            package.Manager.IdentifiersAreQuotedOnCommandLine);
        if (!idIsOptionSafe)
        {
            issues.Add(CoreTools.Translate(
                "The package identifier \"{0}\" would be read as a command-line option or split into several arguments.",
                package.Id));
        }

        if (!CoreTools.IsOptionSafeValue(requestedVersion))
        {
            issues.Add(CoreTools.Translate(
                "The version \"{0}\" would be read as a command-line option.",
                requestedVersion));
        }

        if (ManagerCommandLineIsShellInterpreted(manager))
        {
            if (idIsOptionSafe && !CoreTools.IsValidPackageIdentifier(package.Id))
            {
                issues.Add(InvalidIdIssue(managerName, package.Id));
            }

            // Managers with known broker version rules are checked against those below
            // (stricter, and aware of each manager's range syntax).
            if (requestedVersion.Length > 0
                && !ManagerHasKnownVersionRules(manager)
                && CoreTools.IsOptionSafeValue(requestedVersion)
                && !CoreTools.IsValidPackageVersion(requestedVersion))
            {
                issues.Add(InvalidVersionIssue(managerName, requestedVersion));
            }
        }

        if (idIsOptionSafe)
        {
            AddIssue(issues, CheckPackageId(manager, managerName, package.Id));
        }

        AddIssue(issues, CheckSourceName(manager, managerName, package.Source.Name));

        if (requestedVersion.Length > 0 && CoreTools.IsOptionSafeValue(requestedVersion))
        {
            AddIssue(issues, CheckVersion(manager, managerName, requestedVersion));
        }

        if (BrokerRequestBuilder.ArchitectureApplies(manager, package, role)
            && string.Equals(options.Architecture, UniGetUIArchitecture.arm32, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(CoreTools.Translate(
                "The {0} architecture cannot be requested through the Devolutions Agent.",
                UniGetUIArchitecture.arm32));
        }

        string[] unsupportedOptions = [.. FindUnsupportedOptions(manager, package, options, role, installLocation)];
        if (unsupportedOptions.Length > 0)
        {
            issues.Add(CoreTools.Translate(
                "The Devolutions Agent does not support these options for {0} packages: {1}.",
                managerName,
                string.Join(", ", unsupportedOptions)));
        }

        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            AddIssue(issues, CheckInstallLocation(manager, installLocation));
        }

        bool elevated = RequestsElevation(package, options);
        if (elevated && ManagerRejectsElevation(manager))
        {
            issues.Add(CoreTools.Translate(
                "{0} operations cannot run with administrator rights through the Devolutions Agent. Turn off \"Run as admin\" for this package.",
                managerName));
        }

        // Pre/post commands would run with the elevated token, so the broker refuses them for
        // elevated and machine-scope operations whatever the policy says.
        if (HasPrePostCommands(options, role)
            && (elevated || BrokerRequestBuilder.ResolveScope(manager, package, options) is Scope.Machine))
        {
            issues.Add(CoreTools.Translate(
                "Pre-operation and post-operation commands cannot be used through the Devolutions Agent when the operation runs with administrator rights or for all users. Remove these commands, or turn off \"Run as admin\" and the machine-wide scope."));
        }

        IReadOnlyList<string> parameters = customParameters ?? GetCustomParameters(options, role);
        string[] nonEmptyParameters = [.. parameters.Where(parameter => !string.IsNullOrWhiteSpace(parameter))];
        if (ManagerRejectsCustomParameters(manager) && nonEmptyParameters.Length > 0)
        {
            issues.Add(CoreTools.Translate(
                "The Devolutions Agent does not accept custom arguments for {0} packages ({1}). Remove them from the installation options of this package.",
                managerName,
                string.Join(' ', nonEmptyParameters)));
        }
        else
        {
            // Blank entries are dropped from the request rather than sent.
            foreach (string parameter in nonEmptyParameters)
            {
                AddIssue(issues, CheckCustomParameter(manager, managerName, parameter));
            }
        }

        return [.. issues.Distinct()];
    }

    /// <summary>
    /// Whether the operation asks the broker for elevation: the package's own requirement (for
    /// example a WinGet installer that needs it) or the "Run as admin" option, unless elevation
    /// is prohibited. Shared by the brokered operation and the installation options dialog.
    /// </summary>
    public static bool RequestsElevation(IPackage package, InstallOptions options) =>
        !Settings.Get(Settings.K.ProhibitElevation)
        && (package.OverridenOptions.RunAsAdministrator is true || options.RunAsAdministrator);

    /// <summary>
    /// The options the broker's command builder for this manager refuses, as localized names,
    /// for the values that the request would actually carry for this role.
    /// </summary>
    private static IEnumerable<string> FindUnsupportedOptions(
        ManagerName manager,
        IPackage package,
        InstallOptions options,
        OperationType role,
        string? installLocation)
    {
        bool isUninstall = role is OperationType.Uninstall;
        Scope? scope = BrokerRequestBuilder.ResolveScope(manager, package, options);
        bool architecture = BrokerRequestBuilder.ResolveArchitecture(manager, package, options, role) is not null;
        bool perUserOnly = manager is ManagerName.Npm or ManagerName.Bun or ManagerName.Cargo or ManagerName.Scoop
            or ManagerName.Vcpkg or ManagerName.Dotnet or ManagerName.Pip;
        bool packageManagerPicksTheBuild = manager is ManagerName.Npm or ManagerName.Bun or ManagerName.Cargo
            or ManagerName.Pip or ManagerName.Vcpkg;

        if (scope is Scope.Machine && perUserOnly)
            yield return CoreTools.Translate("installing for all users");

        if (scope is Scope.User && manager is ManagerName.Chocolatey)
            yield return CoreTools.Translate("installing for the current user only");

        if (architecture
            && (packageManagerPicksTheBuild
                || (manager is ManagerName.Chocolatey
                    && string.Equals(options.Architecture, UniGetUIArchitecture.arm64, StringComparison.OrdinalIgnoreCase))))
            yield return CoreTools.Translate("choosing the architecture");

        if (!isUninstall && options.PreRelease
            && manager is ManagerName.Npm or ManagerName.Bun or ManagerName.Cargo or ManagerName.Scoop or ManagerName.Vcpkg)
            yield return CoreTools.Translate("pre-release versions");

        if (options.InteractiveInstallation
            && manager is ManagerName.Npm or ManagerName.Bun or ManagerName.Cargo or ManagerName.Scoop
                or ManagerName.Vcpkg or ManagerName.Dotnet or ManagerName.Pip)
            yield return CoreTools.Translate("interactive installation");

        if (!isUninstall && options.SkipHashCheck
            && manager is ManagerName.Npm or ManagerName.Bun or ManagerName.Cargo or ManagerName.Vcpkg
                or ManagerName.Dotnet or ManagerName.Pip)
            yield return CoreTools.Translate("skipping the hash check");

        if (!string.IsNullOrWhiteSpace(installLocation)
            && manager is ManagerName.Npm or ManagerName.Bun or ManagerName.Scoop or ManagerName.Chocolatey
                or ManagerName.Vcpkg or ManagerName.Pip)
            yield return CoreTools.Translate("a custom install location");

        if (role is OperationType.Update && options.UninstallPreviousVersionsOnUpdate
            && manager is not (ManagerName.Winget or ManagerName.PowerShell or ManagerName.PowerShell7))
            yield return CoreTools.Translate("uninstalling previous versions");

        if (manager is ManagerName.Cargo)
        {
            if ((options.KillBeforeOperation ?? []).Count > 0)
                yield return CoreTools.Translate("closing apps before the operation");
            if (HasPrePostCommands(options, role))
                yield return CoreTools.Translate("pre-operation and post-operation commands");
        }
    }

    /// <summary>Managers whose broker command builder refuses elevated operations.</summary>
    private static bool ManagerRejectsElevation(ManagerName manager) =>
        manager
            is ManagerName.Npm
                or ManagerName.Scoop
                or ManagerName.Bun
                or ManagerName.Cargo
                or ManagerName.Pip
                or ManagerName.Vcpkg;

    private static bool HasPrePostCommands(InstallOptions options, OperationType role) => role switch
    {
        OperationType.Install => !string.IsNullOrWhiteSpace(options.PreInstallCommand)
            || !string.IsNullOrWhiteSpace(options.PostInstallCommand),
        OperationType.Update => !string.IsNullOrWhiteSpace(options.PreUpdateCommand)
            || !string.IsNullOrWhiteSpace(options.PostUpdateCommand),
        OperationType.Uninstall => !string.IsNullOrWhiteSpace(options.PreUninstallCommand)
            || !string.IsNullOrWhiteSpace(options.PostUninstallCommand),
        _ => false,
    };

    /// <summary>
    /// The managers whose broker command builder refuses every custom parameter. This is an
    /// explicit list: managers not listed here (WinGet, Scoop and others) pass them through.
    /// </summary>
    internal static bool ManagerRejectsCustomParameters(ManagerName manager) =>
        manager
            is ManagerName.Chocolatey
                or ManagerName.PowerShell
                or ManagerName.PowerShell7
                or ManagerName.Npm
                or ManagerName.Bun
                or ManagerName.Cargo
                or ManagerName.Dotnet
                or ManagerName.Pip
                or ManagerName.Vcpkg;

    /// <summary>
    /// Whether the broker accepts a package source URL for this manager. The other managers
    /// identify their source by name only and refuse a URL, or only accept their default one.
    /// </summary>
    internal static bool ManagerAcceptsSourceUrl(ManagerName manager) =>
        manager
            is not (ManagerName.Chocolatey
                or ManagerName.PowerShell
                or ManagerName.PowerShell7
                or ManagerName.Npm
                or ManagerName.Bun
                or ManagerName.Cargo
                or ManagerName.Dotnet
                or ManagerName.Pip
                or ManagerName.Vcpkg);

    /// <summary>
    /// Whether the custom parameters of the operation pass WinGet <c>--override</c> or
    /// <c>--custom</c>, which hand arbitrary arguments to the package installer.
    /// </summary>
    public static bool UsesWinGetInstallerArguments(IPackage package, InstallOptions options, OperationType role) =>
        BrokerRequestBuilder.TryMapManagerName(package.Manager.Name, out ManagerName manager)
        && manager is ManagerName.Winget
        && HasWinGetInstallerArguments(GetCustomParameters(options, role));

    /// <summary>
    /// Whether any parameter is WinGet's <c>--override</c> or <c>--custom</c> option, matched
    /// the way WinGet matches long options: ignoring letter case, with or without an attached value.
    /// </summary>
    public static bool HasWinGetInstallerArguments(IEnumerable<string> parameters) =>
        parameters.Any(parameter =>
        {
            string option = parameter.Trim();
            if (!option.StartsWith("--", StringComparison.Ordinal))
            {
                return false;
            }

            string name = option[2..].Split('=', 2)[0].Trim();
            return name.Equals("override", StringComparison.OrdinalIgnoreCase)
                || name.Equals("custom", StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>
    /// Whether the broker accepts a package version for this manager. Scoop and vcpkg always
    /// install the version their manifest or port describes.
    /// </summary>
    internal static bool ManagerAcceptsVersion(ManagerName manager) =>
        manager is not (ManagerName.Scoop or ManagerName.Vcpkg);

    /// <summary>
    /// Whether the broker applies a version allowlist to this manager, in which case a version
    /// UniGetUI already knows can be checked before it is sent.
    /// </summary>
    internal static bool ManagerHasKnownVersionRules(ManagerName manager) =>
        manager
            is ManagerName.Winget
                or ManagerName.Chocolatey
                or ManagerName.PowerShell
                or ManagerName.PowerShell7
                or ManagerName.Npm
                or ManagerName.Bun
                or ManagerName.Cargo
                or ManagerName.Dotnet
                or ManagerName.Pip;

    /// <summary>Returns a localized explanation when the broker would reject the version.</summary>
    internal static string? CheckVersion(ManagerName manager, string managerName, string version)
    {
        if (!ManagerAcceptsVersion(manager))
        {
            return CoreTools.Translate(
                "{0} packages always install the version provided by their source when the Devolutions Agent is used, so a specific version cannot be selected.",
                managerName);
        }

        if (manager is ManagerName.Npm or ManagerName.Bun or ManagerName.Cargo
            && version.IndexOfAny(['^', '<', '>']) >= 0)
        {
            return CoreTools.Translate(
                "Version ranges such as \"{0}\" cannot be sent to the Devolutions Agent for {1} packages. Select a specific version instead.",
                version,
                managerName);
        }

        if (Encoding.UTF8.GetByteCount(version) > MaxVersionLength)
        {
            return CoreTools.Translate(
                "The version \"{0}\" is longer than the Devolutions Agent accepts (at most {1} bytes).",
                version,
                MaxVersionLength);
        }

        if (manager is ManagerName.Bun)
        {
            return IsCanonicalSemanticVersion(version)
                ? null
                : CoreTools.Translate(
                    "{0} package versions must be complete semantic versions, such as 1.2.3.",
                    managerName);
        }

        char[]? extraCharacters = manager switch
        {
            ManagerName.Winget or ManagerName.Chocolatey or ManagerName.PowerShell => [],
            ManagerName.Npm => ['~', '*'],
            ManagerName.Cargo => ['=', '~', '*'],
            ManagerName.Dotnet => ['[', ']', '(', ')', ',', '*'],
            ManagerName.PowerShell7 => ['[', ']', '(', ')', ',', '*', ' '],
            ManagerName.Pip => ['!'],
            _ => null,
        };

        if (extraCharacters is null)
        {
            return null;
        }

        bool valid = version.Length is > 0 and <= MaxVersionLength
            && !version.StartsWith('-')
            && version.All(c =>
                char.IsAsciiLetterOrDigit(c)
                || c is '.' or '-' or '+' or '_'
                || extraCharacters.Contains(c));

        return valid ? null : InvalidVersionIssue(managerName, version);

    }

    /// <summary>Returns a localized explanation when the broker would reject the package identifier.</summary>
    internal static string? CheckPackageId(ManagerName manager, string managerName, string id)
    {
        // Every request is refused while it is read when its identifier breaks the API-wide rules.
        if (Encoding.UTF8.GetByteCount(id) > MaxPackageIdLength)
        {
            return CoreTools.Translate(
                "The package identifier \"{0}\" is longer than the Devolutions Agent accepts (at most {1} bytes).",
                id,
                MaxPackageIdLength);
        }

        if (id.Length == 0 || !id.All(IsPackageIdCharacter))
        {
            return InvalidIdIssue(managerName, id);
        }

        string? issue = manager switch
        {
            ManagerName.Chocolatey when !IsNuGetStylePackageId(id) => CoreTools.Translate(
                "The Chocolatey package identifier \"{0}\" is not accepted by the Devolutions Agent. Identifiers can only contain letters, digits, periods, hyphens and underscores, and cannot name a package file or every package.",
                id),
            ManagerName.Scoop when !IsSinglePackageName(id) => CoreTools.Translate(
                "The {0} package identifier \"{1}\" is not accepted by the Devolutions Agent: it must name a single package, without wildcard characters (* ? [ ]) or a leading hyphen.",
                managerName,
                id),
            ManagerName.PowerShell or ManagerName.PowerShell7 when id.IndexOfAny(['*', '?', '[', ']']) >= 0 =>
                CoreTools.Translate(
                    "The {0} package identifier \"{1}\" is not accepted by the Devolutions Agent: module names cannot contain wildcard characters (* ? [ ]).",
                    managerName,
                    id),
            ManagerName.Npm when !IsNpmPackageId(id) => RegistryNameIssue(managerName, id),
            ManagerName.Bun when !IsNpmRegistryName(id) => RegistryNameIssue(managerName, id),
            ManagerName.Cargo when !IsCrateName(id) => InvalidIdIssue(managerName, id),
            ManagerName.Pip when !IsPythonDistributionName(id) => InvalidIdIssue(managerName, id),
            _ => null,
        };

        if (issue is null && RunsThroughBatchScript(manager) && id.IndexOfAny(BatchMetacharacters) >= 0)
        {
            issue = InvalidIdIssue(managerName, id);
        }

        return issue;
    }

    /// <summary>Returns a localized explanation when the broker would reject the source name.</summary>
    internal static string? CheckSourceName(ManagerName manager, string managerName, string sourceName)
    {
        if (manager is not (ManagerName.Winget or ManagerName.Chocolatey or ManagerName.PowerShell or ManagerName.PowerShell7))
        {
            return null;
        }

        // The broker refuses surrounding whitespace, so the name is checked as it is sent.
        string name = sourceName;
        bool valid = name.Length is > 0 and <= MaxSourceNameLength
            && char.IsAsciiLetterOrDigit(name[0])
            && !name.EndsWith(' ')
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ' ');

        return valid
            ? null
            : CoreTools.Translate(
                "The source name \"{0}\" contains characters that the Devolutions Agent does not accept for {1} packages.",
                sourceName,
                managerName);
    }

    /// <summary>Returns a localized explanation when the broker would reject the install location.</summary>
    internal static string? CheckInstallLocation(ManagerName manager, string location)
    {
        if (!IsPlainLocalDrivePath(location))
        {
            return CoreTools.Translate(
                "The install location \"{0}\" is not accepted by the Devolutions Agent. Use a full path on a local drive, such as C:\\Apps\\MyApp, without \".\" or \"..\" segments, names ending with a period or a space, or special characters.",
                location);
        }

        if (RunsThroughBatchScript(manager) && location.IndexOfAny(BatchMetacharacters) >= 0)
        {
            return CoreTools.Translate(
                "The install location \"{0}\" cannot contain any of the following characters when the Devolutions Agent is used: {1}",
                location,
                BatchMetacharactersDisplay);
        }

        return null;
    }

    /// <summary>Returns a localized explanation when the broker would reject a custom parameter.</summary>
    internal static string? CheckCustomParameter(ManagerName manager, string managerName, string parameter)
    {
        if (Encoding.UTF8.GetByteCount(parameter) > MaxCustomParameterLength)
        {
            return CoreTools.Translate(
                "The custom argument \"{0}\" is longer than the Devolutions Agent accepts (at most {1} bytes).",
                parameter,
                MaxCustomParameterLength);
        }

        if (RunsThroughBatchScript(manager) && parameter.IndexOfAny(BatchMetacharacters) >= 0)
        {
            return CoreTools.Translate(
                "The custom argument \"{0}\" cannot contain any of the following characters when the Devolutions Agent is used: {1}",
                parameter,
                BatchMetacharactersDisplay);
        }

        if (manager is ManagerName.Scoop && !IsAcceptedScoopParameter(parameter))
        {
            return CoreTools.Translate(
                "The {0} custom argument \"{1}\" is not accepted by the Devolutions Agent. Only single options are allowed, and options that select every app, the architecture or the global scope are not.",
                managerName,
                parameter);
        }

        return null;
    }

    /// <summary>
    /// Whether the broker runs this manager through a generated batch script, where it
    /// rejects batch metacharacters in every argument instead of escaping them.
    /// </summary>
    private static bool RunsThroughBatchScript(ManagerName manager) =>
        manager
            is ManagerName.Winget
                or ManagerName.Chocolatey
                or ManagerName.Cargo
                or ManagerName.Vcpkg
                or ManagerName.Bun;

    /// <summary>
    /// A plain absolute path on a local drive letter: no relative, UNC or device paths, no
    /// <c>.</c>/<c>..</c> segments, no segment starting with a space or ending with a period or a
    /// space, no alternate data stream, and no wildcard or control characters.
    /// </summary>
    internal static bool IsPlainLocalDrivePath(string location)
    {
        string path = location.Replace('/', '\\');
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
        {
            return false;
        }

        string rest = path[3..];
        if (rest.Any(c => char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|'))
        {
            return false;
        }

        return rest
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .All(segment => !segment.EndsWith('.') && !segment.EndsWith(' ') && !segment.StartsWith(' '));
    }

    private static bool IsNuGetStylePackageId(string id)
    {
        if (id.Length is 0 or > 100 || id[0] is '-' or '.')
        {
            return false;
        }

        if (!id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            return false;
        }

        return !id.EndsWith(".config", StringComparison.OrdinalIgnoreCase)
            && !id.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)
            && !id.Equals("all", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSinglePackageName(string id) =>
        id.Trim().Length > 0
        && id.IndexOfAny(['*', '?', '[', ']']) < 0
        && !id.StartsWith('-')
        && id.IndexOfAny(['\0', '\r', '\n']) < 0;

    /// <summary>A registry name, or an alias whose local and target names are both registry names.</summary>
    private static bool IsNpmPackageId(string id)
    {
        if (id.Contains('%'))
        {
            return false;
        }

        int colonIndex = id.IndexOf(':');
        if (colonIndex <= 0)
        {
            return IsNpmRegistryName(id);
        }

        string targetSpec = id[(colonIndex + 1)..];
        int atIndex = targetSpec.LastIndexOf('@');
        string targetName = atIndex > 0 ? targetSpec[..atIndex] : targetSpec;
        return IsNpmRegistryName(id[..colonIndex]) && IsNpmRegistryName(targetName);
    }

    /// <summary>An npm registry package name: <c>name</c> or <c>@scope/name</c>.</summary>
    private static bool IsNpmRegistryName(string name)
    {
        if (name.Length is 0 or > 214)
        {
            return false;
        }

        bool shapeIsValid;
        if (name.StartsWith('@'))
        {
            string[] parts = name[1..].Split('/');
            shapeIsValid = parts.Length == 2 && IsNpmNamePart(parts[0]) && IsNpmNamePart(parts[1]);
        }
        else
        {
            shapeIsValid = IsNpmNamePart(name);
        }

        return shapeIsValid
            && !name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNpmNamePart(string part) =>
        part.Length > 0
        && part[0] is not ('.' or '_' or '-')
        && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~');

    private static bool IsCrateName(string id) =>
        id.Length is > 0 and <= 64
        && !id.StartsWith('-')
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsPythonDistributionName(string id) =>
        id.Length > 0
        && char.IsAsciiLetterOrDigit(id[0])
        && char.IsAsciiLetterOrDigit(id[^1])
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>
    /// A single Scoop option that names one app: no positional argument or option terminator,
    /// no whitespace, and no option selecting every app (<c>--all</c>), the architecture
    /// (<c>--arch</c>, or <c>a</c> in a short-option cluster) or the global scope.
    /// </summary>
    private static bool IsAcceptedScoopParameter(string parameter)
    {
        if (parameter == "--" || !parameter.StartsWith('-') || parameter.Any(char.IsWhiteSpace))
        {
            return false;
        }

        string option = parameter.ToLowerInvariant();
        if (option.StartsWith("--", StringComparison.Ordinal))
        {
            string name = option[2..].Split('=', 2)[0];
            return name is not ("all" or "arch" or "global");
        }

        string shortOptions = option[1..];
        return !shortOptions.Contains('a') && !shortOptions.Contains('g');
    }

    private static string RegistryNameIssue(string managerName, string id) =>
        CoreTools.Translate(
            "The {0} package identifier \"{1}\" is not accepted by the Devolutions Agent: packages must be referenced by their registry name, such as \"name\" or \"@scope/name\", and not by a URL, a Git repository or a local path.",
            managerName,
            id);

    private static string InvalidVersionIssue(string managerName, string version) =>
        CoreTools.Translate(
            "The version \"{0}\" contains characters that the Devolutions Agent does not accept for {1} packages.",
            version,
            managerName);

    /// <summary>Managers whose broker command runs through a PowerShell script.</summary>
    private static bool ManagerCommandLineIsShellInterpreted(ManagerName manager) =>
        manager
            is ManagerName.PowerShell
                or ManagerName.PowerShell7
                or ManagerName.Scoop
                or ManagerName.Npm;

    private static string InvalidIdIssue(string managerName, string id) =>
        CoreTools.Translate(
            "The package identifier \"{0}\" contains characters that the Devolutions Agent does not accept for {1} packages.",
            id,
            managerName);

    private static IReadOnlyList<string> GetCustomParameters(InstallOptions options, OperationType role) => role switch
    {
        OperationType.Install => options.CustomParameters_Install ?? [],
        OperationType.Update => options.CustomParameters_Update ?? [],
        OperationType.Uninstall => options.CustomParameters_Uninstall ?? [],
        _ => [],
    };

    private static void AddIssue(List<string> issues, string? issue)
    {
        if (issue is not null)
        {
            issues.Add(issue);
        }
    }

    /// <summary>
    /// A SemVer 2.0 version with the canonical numeric rules the broker parser applies: no
    /// leading zeros in numeric identifiers, and release components that fit 64 bits.
    /// </summary>
    internal static bool IsCanonicalSemanticVersion(string version)
    {
        Match match = SemanticVersionRegex().Match(version);
        return match.Success
            && ulong.TryParse(match.Groups["major"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && ulong.TryParse(match.Groups["minor"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && ulong.TryParse(match.Groups["patch"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>Characters the broker API accepts in any package identifier.</summary>
    private static bool IsPackageIdCharacter(char c) =>
        char.IsAsciiLetterOrDigit(c)
        || c is '.' or '-' or '_' or '+' or '@' or '/' or ':' or '[' or ']' or ',' or '#' or '$' or '%' or '{' or '}';

    [GeneratedRegex(
        @"^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(-(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionRegex();
}
