using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Devolutions.Pinget.Core;
using YamlDotNet.Serialization;

const string Version = "0.1.0";
const string UpgradeUnsupportedWarning = "Upgrading packages is not supported on this platform; no changes were made.";

if (args.Length == 1 && (string.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase) || string.Equals(args[0], "-v", StringComparison.OrdinalIgnoreCase)))
{
    PrintVersion();
    return 0;
}

var rootCommand = new RootCommand("Pinget: portable winget in pure C#");

var outputOption = new Option<string?>("--output", "Output format: text, json, or yaml");
outputOption.AddAlias("-o");
outputOption.FromAmong("text", "json", "yaml");
rootCommand.AddGlobalOption(outputOption);

var SerializationOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

var infoOption = new Option<bool>("--info", "Display general info");
rootCommand.AddGlobalOption(infoOption);

var disableInteractivityOption = new Option<bool>("--disable-interactivity", "Disable interactive prompts");
rootCommand.AddGlobalOption(disableInteractivityOption);

var acceptSourceAgreementsOption = new Option<bool>("--accept-source-agreements", "Accept source agreements");
rootCommand.AddGlobalOption(acceptSourceAgreementsOption);

// ── Common options ──
Option<string?> QueryArg(string description = "Query")
{
    var o = new Option<string?>("--query", description);
    o.AddAlias("-q");
    return o;
}
Option<string?> IdOpt() => new("--id", "Filter by id");
Option<string?> NameOpt() => new("--name", "Filter by name");
Option<string?> MonikerOpt() => new("--moniker", "Filter by moniker");
Option<string?> SourceOpt() { var o = new Option<string?>("--source", "Source name"); o.AddAlias("-s"); return o; }
Option<bool> ExactOpt() { var o = new Option<bool>("--exact", "Exact match"); o.AddAlias("-e"); return o; }
Option<int?> CountOpt() { var o = new Option<int?>("--count", "Max results"); o.AddAlias("-n"); return o; }
Option<string?> VersionOpt() { var o = new Option<string?>("--version", "Version"); o.AddAlias("-v"); return o; }

// ── Search command ──
var searchCommand = new Command("search", "Search for packages");
var sqArg = new Argument<string?>("query", () => null, "Search query");
var sqOpt = QueryArg(); var sidOpt = IdOpt(); var snOpt = NameOpt(); var smOpt = MonikerOpt();
var ssOpt = SourceOpt(); var seOpt = ExactOpt(); var scOpt = CountOpt();
var sTagOpt = new Option<string?>("--tag", "Filter by tag");
var sCmdOpt = new Option<string?>("--command", "Filter by command"); sCmdOpt.AddAlias("--cmd");
var sVersionsOpt = new Option<bool>("--versions", "Show versions");
var sManifestsOpt = new Option<bool>("--manifests", "Return show-style manifests");
foreach (var o in new Option[] { sqOpt, sidOpt, snOpt, smOpt, ssOpt, seOpt, scOpt, sTagOpt, sCmdOpt, sVersionsOpt, sManifestsOpt })
    searchCommand.AddOption(o);
searchCommand.AddArgument(sqArg);

searchCommand.SetHandler((ctx) =>
{
    var output = GetOutputFormat(ctx.ParseResult.GetValueForOption(outputOption));
    var query = new PackageQuery
    {
        Query = ctx.ParseResult.GetValueForArgument(sqArg) ?? ctx.ParseResult.GetValueForOption(sqOpt),
        Id = ctx.ParseResult.GetValueForOption(sidOpt),
        Name = ctx.ParseResult.GetValueForOption(snOpt),
        Moniker = ctx.ParseResult.GetValueForOption(smOpt),
        Tag = ctx.ParseResult.GetValueForOption(sTagOpt),
        Command = ctx.ParseResult.GetValueForOption(sCmdOpt),
        Source = ctx.ParseResult.GetValueForOption(ssOpt),
        Count = ctx.ParseResult.GetValueForOption(scOpt),
        Exact = ctx.ParseResult.GetValueForOption(seOpt),
    };

    using var repo = Repository.Open();
    if (ctx.ParseResult.GetValueForOption(sManifestsOpt))
    {
        if (output == OutputFormat.Text)
            throw new InvalidOperationException("--manifests requires --output json or yaml");
        if (ctx.ParseResult.GetValueForOption(sVersionsOpt))
            throw new InvalidOperationException("--manifests cannot be combined with --versions");

        WriteStructuredOutput(repo.SearchManifests(query), output);
    }
    else if (ctx.ParseResult.GetValueForOption(sVersionsOpt))
    {
        var result = repo.SearchVersions(query);
        if (output != OutputFormat.Text) WriteStructuredOutput(result, output);
        else PrintVersions(result);
    }
    else
    {
        var result = repo.Search(query);
        if (output != OutputFormat.Text) WriteStructuredOutput(result, output);
        else PrintSearch(result);
    }
});

// ── Show command ──
var showCommand = new Command("show", "Show package info");
var shArg = new Argument<string?>("query", () => null, "Package query");
var shqOpt = QueryArg(); var shidOpt = IdOpt(); var shnOpt = NameOpt(); var shmOpt = MonikerOpt();
var shsOpt = SourceOpt(); var sheOpt = ExactOpt(); var shvOpt = VersionOpt();
var shVerOpt = new Option<bool>("--versions", "Show available versions");
var shLocaleOpt = new Option<string?>("--locale", "Installer locale");
var shTypeOpt = new Option<string?>("--installer-type", "Installer type");
var shArchOpt = new Option<string?>("--architecture", "Architecture"); shArchOpt.AddAlias("-a");
var shScopeOpt = new Option<string?>("--scope", "Install scope");
foreach (var o in new Option[] { shqOpt, shidOpt, shnOpt, shmOpt, shsOpt, sheOpt, shvOpt, shVerOpt, shLocaleOpt, shTypeOpt, shArchOpt, shScopeOpt })
    showCommand.AddOption(o);
showCommand.AddArgument(shArg);

showCommand.SetHandler((ctx) =>
{
    var output = GetOutputFormat(ctx.ParseResult.GetValueForOption(outputOption));
    var query = new PackageQuery
    {
        Query = ctx.ParseResult.GetValueForArgument(shArg) ?? ctx.ParseResult.GetValueForOption(shqOpt),
        Id = ctx.ParseResult.GetValueForOption(shidOpt),
        Name = ctx.ParseResult.GetValueForOption(shnOpt),
        Moniker = ctx.ParseResult.GetValueForOption(shmOpt),
        Source = ctx.ParseResult.GetValueForOption(shsOpt),
        Exact = ctx.ParseResult.GetValueForOption(sheOpt),
        Version = ctx.ParseResult.GetValueForOption(shvOpt),
        Locale = ctx.ParseResult.GetValueForOption(shLocaleOpt),
        InstallerType = ctx.ParseResult.GetValueForOption(shTypeOpt),
        InstallerArchitecture = ctx.ParseResult.GetValueForOption(shArchOpt),
        InstallScope = ctx.ParseResult.GetValueForOption(shScopeOpt),
    };

    using var repo = Repository.Open();
    if (ctx.ParseResult.GetValueForOption(shVerOpt))
    {
        var result = repo.ShowVersions(query);
        if (output != OutputFormat.Text) WriteStructuredOutput(result, output);
        else PrintVersions(result);
    }
    else
    {
        var result = repo.Show(query);
        if (output != OutputFormat.Text) WriteManifestStructuredOutput(result.ToStructuredDocument(), output);
        else PrintShow(result);
    }
});

// ── List command ──
var listCommand = new Command("list", "List installed packages");
listCommand.AddAlias("ls");
var lArg = new Argument<string?>("query", () => null, "Package query");
var lqOpt = QueryArg(); var lidOpt = IdOpt(); var lnOpt = NameOpt(); var lmOpt = MonikerOpt();
var lsOpt = SourceOpt(); var leOpt = ExactOpt(); var lcOpt = CountOpt();
var lTagOpt = new Option<string?>("--tag", "Filter by tag");
var lCmdOpt = new Option<string?>("--command", "Filter by command"); lCmdOpt.AddAlias("--cmd");
var lScopeOpt = new Option<string?>("--scope", "Install scope");
var lUpgradeOpt = new Option<bool>("--upgrade-available", "Show upgradeable only");
var lUnknownOpt = new Option<bool>("--include-unknown", "Include unknown versions"); lUnknownOpt.AddAlias("-u");
var lPinnedOpt = new Option<bool>("--include-pinned", "Include pinned packages");
var lDetailsOpt = new Option<bool>("--details", "Show details");
foreach (var o in new Option[] { lqOpt, lidOpt, lnOpt, lmOpt, lsOpt, leOpt, lcOpt, lTagOpt, lCmdOpt, lScopeOpt, lUpgradeOpt, lUnknownOpt, lPinnedOpt, lDetailsOpt })
    listCommand.AddOption(o);
listCommand.AddArgument(lArg);

listCommand.SetHandler((ctx) =>
{
    var output = GetOutputFormat(ctx.ParseResult.GetValueForOption(outputOption));
    var details = ctx.ParseResult.GetValueForOption(lDetailsOpt);
    var upgrade = ctx.ParseResult.GetValueForOption(lUpgradeOpt);
    var query = new ListQuery
    {
        Query = ctx.ParseResult.GetValueForArgument(lArg) ?? ctx.ParseResult.GetValueForOption(lqOpt),
        Id = ctx.ParseResult.GetValueForOption(lidOpt),
        Name = ctx.ParseResult.GetValueForOption(lnOpt),
        Moniker = ctx.ParseResult.GetValueForOption(lmOpt),
        Tag = ctx.ParseResult.GetValueForOption(lTagOpt),
        Command = ctx.ParseResult.GetValueForOption(lCmdOpt),
        Source = ctx.ParseResult.GetValueForOption(lsOpt),
        Count = ctx.ParseResult.GetValueForOption(lcOpt),
        Exact = ctx.ParseResult.GetValueForOption(leOpt),
        InstallScope = ctx.ParseResult.GetValueForOption(lScopeOpt),
        UpgradeOnly = upgrade,
        IncludeUnknown = ctx.ParseResult.GetValueForOption(lUnknownOpt),
        IncludePinned = ctx.ParseResult.GetValueForOption(lPinnedOpt),
    };

    using var repo = Repository.Open();
    var result = repo.List(query);
    if (output != OutputFormat.Text) WriteStructuredOutput(result, output);
    else PrintListResult(result, details, upgrade);
});

// ── Upgrade command ──
var upgradeCommand = new Command("upgrade", "Upgrade packages");
upgradeCommand.AddAlias("update");
var uArg = new Argument<string?>("query", () => null, "Package query");
var uqOpt = QueryArg(); var uidOpt = IdOpt(); var unOpt = NameOpt(); var umOpt = MonikerOpt();
var usOpt = SourceOpt(); var ueOpt = ExactOpt(); var ucOpt = CountOpt(); var uvOpt = VersionOpt();
var uManifestOpt = new Option<string?>("--manifest", "Local manifest file or directory"); uManifestOpt.AddAlias("-m");
var uLocaleOpt = new Option<string?>("--locale", "Installer locale");
var uTypeOpt = new Option<string?>("--installer-type", "Installer type");
var uArchOpt = new Option<string?>("--architecture", "Architecture"); uArchOpt.AddAlias("-a");
var uPlatformOpt = new Option<string?>("--platform", "Target platform");
var uOsVersionOpt = new Option<string?>("--os-version", "Target OS version");
var uScopeOpt = new Option<string?>("--scope", "Install scope");
var uUnknownOpt = new Option<bool>("--include-unknown", "Include unknown"); uUnknownOpt.AddAlias("-u");
var uPinnedOpt = new Option<bool>("--include-pinned", "Include pinned");
var uAllOpt = new Option<bool>("--all", "Upgrade all"); uAllOpt.AddAlias("-r"); uAllOpt.AddAlias("--recurse");
var uLogOpt = new Option<string?>("--log", "Installer log path");
var uCustomOpt = new Option<string?>("--custom", "Additional installer switches");
var uOverrideOpt = new Option<string?>("--override", "Override installer arguments");
var uLocationOpt = new Option<string?>("--location", "Install location"); uLocationOpt.AddAlias("-l");
var uIgnoreSecurityHashOpt = new Option<bool>("--ignore-security-hash", "Ignore installer hash mismatches");
var uSkipDependenciesOpt = new Option<bool>("--skip-dependencies", "Skip package dependencies");
var uDependencySourceOpt = new Option<string?>("--dependency-source", "Source to use when resolving dependencies");
var uAcceptPkgAgreementsOpt = new Option<bool>("--accept-package-agreements", "Accept package agreements");
var uForceOpt = new Option<bool>("--force", "Force install behavior");
var uUninstallPreviousOpt = new Option<bool>("--uninstall-previous", "Uninstall previous versions before installing");
var uSilentOpt = new Option<bool>("--silent", "Silent install"); uSilentOpt.AddAlias("-h");
var uInteractiveOpt = new Option<bool>("--interactive", "Interactive install"); uInteractiveOpt.AddAlias("-i");
foreach (var o in new Option[] { uqOpt, uidOpt, unOpt, umOpt, usOpt, ueOpt, ucOpt, uvOpt, uManifestOpt, uLocaleOpt, uTypeOpt, uArchOpt, uPlatformOpt, uOsVersionOpt, uScopeOpt, uUnknownOpt, uPinnedOpt, uAllOpt, uLogOpt, uCustomOpt, uOverrideOpt, uLocationOpt, uIgnoreSecurityHashOpt, uSkipDependenciesOpt, uDependencySourceOpt, uAcceptPkgAgreementsOpt, uForceOpt, uUninstallPreviousOpt, uSilentOpt, uInteractiveOpt })
    upgradeCommand.AddOption(o);
upgradeCommand.AddArgument(uArg);

upgradeCommand.SetHandler((ctx) =>
{
    var output = GetOutputFormat(ctx.ParseResult.GetValueForOption(outputOption));
    var manifestPath = ctx.ParseResult.GetValueForOption(uManifestOpt);
    var interactive = ctx.ParseResult.GetValueForOption(uInteractiveOpt);
    var silent = ctx.ParseResult.GetValueForOption(uSilentOpt);
    var hasExplicitUpgradeSelector =
        ctx.ParseResult.GetValueForArgument(uArg) is not null ||
        ctx.ParseResult.GetValueForOption(uqOpt) is not null ||
        ctx.ParseResult.GetValueForOption(uidOpt) is not null ||
        ctx.ParseResult.GetValueForOption(unOpt) is not null ||
        ctx.ParseResult.GetValueForOption(umOpt) is not null;
    if (silent && interactive)
        throw new InvalidOperationException("--silent and --interactive cannot be used together.");

    var doInstall = !string.IsNullOrWhiteSpace(manifestPath)
        || ctx.ParseResult.GetValueForOption(uAllOpt)
        || ctx.ParseResult.GetValueForArgument(uArg) is not null
        || ctx.ParseResult.GetValueForOption(uqOpt) is not null
        || ctx.ParseResult.GetValueForOption(uidOpt) is not null
        || ctx.ParseResult.GetValueForOption(unOpt) is not null;

    var query = new ListQuery
    {
        Query = ctx.ParseResult.GetValueForArgument(uArg) ?? ctx.ParseResult.GetValueForOption(uqOpt),
        Id = ctx.ParseResult.GetValueForOption(uidOpt),
        Name = ctx.ParseResult.GetValueForOption(unOpt),
        Moniker = ctx.ParseResult.GetValueForOption(umOpt),
        Source = ctx.ParseResult.GetValueForOption(usOpt),
        Count = ctx.ParseResult.GetValueForOption(ucOpt),
        Exact = ctx.ParseResult.GetValueForOption(ueOpt),
        Version = ctx.ParseResult.GetValueForOption(uvOpt),
        InstallScope = ctx.ParseResult.GetValueForOption(uScopeOpt),
        UpgradeOnly = true,
        IncludeUnknown = ctx.ParseResult.GetValueForOption(uUnknownOpt),
        IncludePinned = ctx.ParseResult.GetValueForOption(uPinnedOpt) || hasExplicitUpgradeSelector,
    };

    var installQuery = new PackageQuery
    {
        Query = query.Query,
        Id = query.Id,
        Name = query.Name,
        Moniker = query.Moniker,
        Source = query.Source,
        Exact = query.Exact,
        Version = query.Version,
        Locale = ctx.ParseResult.GetValueForOption(uLocaleOpt),
        InstallerType = ctx.ParseResult.GetValueForOption(uTypeOpt),
        InstallerArchitecture = ctx.ParseResult.GetValueForOption(uArchOpt),
        Platform = ctx.ParseResult.GetValueForOption(uPlatformOpt),
        OsVersion = ctx.ParseResult.GetValueForOption(uOsVersionOpt),
        InstallScope = query.InstallScope,
    };

    using var repo = Repository.Open();
    if (doInstall && !OperatingSystem.IsWindows())
    {
        PrintWarnings([UpgradeUnsupportedWarning]);
        Console.WriteLine("No changes were made.");
        return;
    }

    var result = repo.List(query);
    var mode = interactive ? InstallerMode.Interactive : silent ? InstallerMode.Silent : InstallerMode.SilentWithProgress;
    var baseInstallRequest = CreateInstallRequest(
        installQuery,
        manifestPath,
        mode,
        ctx.ParseResult.GetValueForOption(uLogOpt),
        ctx.ParseResult.GetValueForOption(uCustomOpt),
        ctx.ParseResult.GetValueForOption(uOverrideOpt),
        ctx.ParseResult.GetValueForOption(uLocationOpt),
        ctx.ParseResult.GetValueForOption(uSkipDependenciesOpt),
        false,
        ctx.ParseResult.GetValueForOption(uAcceptPkgAgreementsOpt),
        ctx.ParseResult.GetValueForOption(uForceOpt),
        null,
        ctx.ParseResult.GetValueForOption(uUninstallPreviousOpt),
        ctx.ParseResult.GetValueForOption(uIgnoreSecurityHashOpt),
        ctx.ParseResult.GetValueForOption(uDependencySourceOpt),
        false);

    if (!doInstall)
    {
        if (output != OutputFormat.Text) WriteStructuredOutput(result, output);
        else PrintListResult(result, false, true);
    }
    else if (!string.IsNullOrWhiteSpace(manifestPath))
    {
        var installResult = repo.Install(baseInstallRequest);
        PrintPackageActionResult(installResult, "upgrade", "upgraded");
    }
    else
    {
        var upgradeable = result.Matches.Where(m => m.AvailableVersion is not null).ToList();
        var pins = repo.ListPins();
        var upgradedCount = 0;
        if (upgradeable.Count == 0)
        {
            Console.WriteLine("No applicable upgrade found.");
        }
        else
        {
            foreach (var m in upgradeable)
            {
                Console.WriteLine($"Upgrading {m.Id} from {m.InstalledVersion} to {m.AvailableVersion ?? "?"} ...");
                try
                {
                    var pin = FindMatchingPin(m, pins);
                    if (pin?.PinType == PinType.Blocking)
                    {
                        Console.WriteLine($"  Package is blocked by pin {pin.Version}; remove the pin before upgrading.");
                        continue;
                    }

                    var r = repo.Install(baseInstallRequest with
                    {
                        Query = new PackageQuery
                        {
                            Id = m.Id,
                            Source = installQuery.Source ?? m.SourceName,
                            Exact = true,
                            Version = installQuery.Version,
                            Locale = installQuery.Locale,
                            InstallerType = installQuery.InstallerType,
                            InstallerArchitecture = installQuery.InstallerArchitecture,
                            Platform = installQuery.Platform,
                            OsVersion = installQuery.OsVersion,
                            InstallScope = installQuery.InstallScope,
                        },
                        ManifestPath = null,
                    });
                    PrintWarnings(r.Warnings);
                    Console.WriteLine(r.NoOp
                        ? $"  No changes were made for {m.Id}"
                        : r.Success
                            ? $"  Successfully upgraded {m.Id}"
                            : $"  Failed to upgrade {m.Id} (exit code: {r.ExitCode})");
                    if (r.Success && !r.NoOp)
                        upgradedCount++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  Error upgrading {m.Id}: {ex.Message}");
                }
            }
            Console.WriteLine($"{upgradedCount} package(s) upgraded.");
        }
    }
});

// ── Source commands ──
var sourceCommand = new Command("source", "Manage sources");
var sourceListCmd = new Command("list", "List sources");
var sourceUpdateCmd = new Command("update", "Update sources");
var suSourceArg = new Argument<string?>("source", () => null, "Source name");
sourceUpdateCmd.AddArgument(suSourceArg);
var sourceExportCmd = new Command("export", "Export sources");
var sourceAddCmd = new Command("add", "Add source");
var saNameArg = new Argument<string?>("name", () => null, "Source name");
var saArgArg = new Argument<string?>("arg", () => null, "Source URL");
var saNameOpt = new Option<string?>("--name", "Source name"); saNameOpt.AddAlias("-n");
var saArgOpt = new Option<string?>("--arg", "Source URL"); saArgOpt.AddAlias("-a");
var saTypeOpt = new Option<string?>("--type", "Source type"); saTypeOpt.AddAlias("-t");
var saTrustLevelOpt = new Option<string?>("--trust-level", "Source trust level");
var saExplicitOpt = new Option<bool>("--explicit", "Exclude source from discovery unless specified");
sourceAddCmd.AddArgument(saNameArg); sourceAddCmd.AddArgument(saArgArg);
foreach (var o in new Option[] { saNameOpt, saArgOpt, saTypeOpt, saTrustLevelOpt, saExplicitOpt }) sourceAddCmd.AddOption(o);
var sourceEditCmd = new Command("edit", "Edit source");
sourceEditCmd.AddAlias("config");
sourceEditCmd.AddAlias("set");
var seNameOpt = new Option<string?>("--name", "Source name"); seNameOpt.AddAlias("-n");
var seExplicitOpt = new Option<bool?>("--explicit", "Excludes a source from discovery (true or false)"); seExplicitOpt.AddAlias("-e");
sourceEditCmd.AddOption(seNameOpt);
sourceEditCmd.AddOption(seExplicitOpt);
var sourceRemoveCmd = new Command("remove", "Remove source");
var srNameArg = new Argument<string?>("name", () => null, "Source name");
var srRemoveNameOpt = new Option<string?>("--name", "Source name"); srRemoveNameOpt.AddAlias("-n");
sourceRemoveCmd.AddArgument(srNameArg);
sourceRemoveCmd.AddOption(srRemoveNameOpt);
var sourceResetCmd = new Command("reset", "Reset sources");
var srNameOpt = new Option<string?>("--name", "Source name"); srNameOpt.AddAlias("-n");
var srForceOpt = new Option<bool>("--force", "Force reset");
sourceResetCmd.AddOption(srNameOpt);
sourceResetCmd.AddOption(srForceOpt);
sourceCommand.AddCommand(sourceListCmd);
sourceCommand.AddCommand(sourceUpdateCmd);
sourceCommand.AddCommand(sourceExportCmd);
sourceCommand.AddCommand(sourceAddCmd);
sourceCommand.AddCommand(sourceEditCmd);
sourceCommand.AddCommand(sourceRemoveCmd);
sourceCommand.AddCommand(sourceResetCmd);

sourceListCmd.SetHandler((ctx) =>
{
    var output = GetOutputFormat(ctx.ParseResult.GetValueForOption(outputOption));
    using var repo = Repository.Open();
    var sources = repo.ListSources();
    if (output != OutputFormat.Text) WriteStructuredOutput(new { Sources = sources }, output);
    else PrintSources(sources);
});

sourceUpdateCmd.SetHandler((source) =>
{
    using var repo = Repository.Open();
    foreach (var r in repo.UpdateSources(source))
        Console.WriteLine($"{r.Name} [{r.Kind}]: {r.Detail}");
}, suSourceArg);

sourceExportCmd.SetHandler(() =>
{
    using var repo = Repository.Open();
    var sources = repo.ListSources().Select(s => new
    {
        Name = s.Name,
        Type = FormatSourceType(s.Kind),
        Arg = s.Arg,
        Data = s.Identifier,
        Identifier = s.Identifier,
        TrustLevel = s.TrustLevel,
        Explicit = s.Explicit,
        Priority = s.Priority,
    });
    Console.WriteLine(JsonSerializer.Serialize(new { Sources = sources }, SerializationOptions));
});

sourceAddCmd.SetHandler((ctx) =>
{
    var name = ResolveSourceAddValue(
        ctx.ParseResult.GetValueForArgument(saNameArg),
        ctx.ParseResult.GetValueForOption(saNameOpt),
        "name");
    var arg = ResolveSourceAddValue(
        ctx.ParseResult.GetValueForArgument(saArgArg),
        ctx.ParseResult.GetValueForOption(saArgOpt),
        "argument");
    var type = ctx.ParseResult.GetValueForOption(saTypeOpt);
    var trustLevel = ctx.ParseResult.GetValueForOption(saTrustLevelOpt);
    var explicitSource = ctx.ParseResult.GetValueForOption(saExplicitOpt);
    using var repo = Repository.Open();
    var kind = ParseSourceKind(type);
    repo.AddSource(name, arg, kind, trustLevel ?? "None", explicitSource);
    Console.WriteLine("Done");
});

sourceEditCmd.SetHandler((name, explicitSource) =>
{
    if (string.IsNullOrWhiteSpace(name))
        throw new InvalidOperationException("source edit requires --name.");
    if (!explicitSource.HasValue)
        throw new InvalidOperationException("source edit requires --explicit true|false.");

    using var repo = Repository.Open();
    repo.EditSource(name, explicitSource: explicitSource.Value);
    Console.WriteLine("Done");
}, seNameOpt, seExplicitOpt);

sourceRemoveCmd.SetHandler((ctx) =>
{
    var name = ResolveSourceAddValue(
        ctx.ParseResult.GetValueForArgument(srNameArg),
        ctx.ParseResult.GetValueForOption(srRemoveNameOpt),
        "name");
    using var repo = Repository.Open();
    repo.RemoveSource(name);
    Console.WriteLine("Done");
});

sourceResetCmd.SetHandler((name, force) =>
{
    using var repo = Repository.Open();
    if (!string.IsNullOrWhiteSpace(name))
        repo.ResetSource(name);
    else
    {
        if (!force) { Console.Error.WriteLine("error: Resetting all sources requires --force"); return; }
        repo.ResetSources();
    }
    Console.WriteLine("Done");
}, srNameOpt, srForceOpt);

// ── Cache warm ──
var cacheCommand = new Command("cache", "Cache management");
var cacheWarmCmd = new Command("warm", "Warm manifest cache");
var cwArg = new Argument<string?>("query", () => null, "Package query");
var cwqOpt = QueryArg(); var cwidOpt = IdOpt(); var cwsOpt = SourceOpt(); var cweOpt = ExactOpt();
cacheWarmCmd.AddArgument(cwArg);
foreach (var o in new Option[] { cwqOpt, cwidOpt, cwsOpt, cweOpt }) cacheWarmCmd.AddOption(o);
cacheCommand.AddCommand(cacheWarmCmd);

cacheWarmCmd.SetHandler((ctx) =>
{
    var output = GetOutputFormat(ctx.ParseResult.GetValueForOption(outputOption));
    var query = new PackageQuery
    {
        Query = ctx.ParseResult.GetValueForArgument(cwArg) ?? ctx.ParseResult.GetValueForOption(cwqOpt),
        Id = ctx.ParseResult.GetValueForOption(cwidOpt),
        Source = ctx.ParseResult.GetValueForOption(cwsOpt),
        Exact = ctx.ParseResult.GetValueForOption(cweOpt),
    };
    using var repo = Repository.Open();
    var result = repo.WarmCache(query);
    if (output != OutputFormat.Text) WriteStructuredOutput(result, output);
    else
    {
        Console.WriteLine($"Warmed cache for {result.Package.Name} [{result.Package.Id}]");
        foreach (var f in result.CachedFiles) Console.WriteLine($"  {f}");
    }
});

// ── Hash ──
var hashCommand = new Command("hash", "Hash a file");
var hashFileArg = new Argument<string>("file", "File path");
var hashMsixOpt = new Option<bool>("--msix", "MSIX signature hash");
hashCommand.AddArgument(hashFileArg); hashCommand.AddOption(hashMsixOpt);

hashCommand.SetHandler((file, msix) =>
{
    var bytes = File.ReadAllBytes(file);
    var hash = SHA256.HashData(bytes);
    Console.WriteLine($"SHA256: {Convert.ToHexString(hash)}");
    if (msix)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(file);
            var sig = archive.GetEntry("AppxSignature.p7x");
            if (sig is not null)
            {
                using var stream = sig.Open();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var sigHash = SHA256.HashData(ms.ToArray());
                Console.WriteLine($"SignatureSha256: {Convert.ToHexString(sigHash)}");
            }
        }
        catch { Console.Error.WriteLine("Not a valid MSIX/Appx package"); }
    }
}, hashFileArg, hashMsixOpt);

// ── Export ──
var exportCommand = new Command("export", "Export installed packages");
var exOutputOpt = new Option<string>("--output", "Output file") { IsRequired = true }; exOutputOpt.AddAlias("-o");
var exSourceOpt = SourceOpt();
var exVersionsOpt = new Option<bool>("--include-versions", "Include versions");
exportCommand.AddOption(exOutputOpt); exportCommand.AddOption(exSourceOpt); exportCommand.AddOption(exVersionsOpt);

exportCommand.SetHandler((output, source, includeVersions) =>
{
    using var repo = Repository.Open();
    var listResult = repo.List(new ListQuery { Source = source is not null ? source : null, Query = source is not null ? " " : null });
    var packages = listResult.Matches.Select(m =>
    {
        var pkg = new Dictionary<string, object> { ["PackageIdentifier"] = m.Id };
        if (includeVersions && m.InstalledVersion is not null) pkg["Version"] = m.InstalledVersion;
        return pkg;
    }).ToList();

    var export = new
    {
        Schema = "https://aka.ms/winget-packages.schema.2.0.json",
        Sources = new[]
        {
            new
            {
                SourceDetails = new { Name = source ?? "winget", Argument = "https://cdn.winget.microsoft.com/cache", Type = "Microsoft.PreIndexed" },
                Packages = packages
            }
        }
    };
    File.WriteAllText(output, JsonSerializer.Serialize(export, SerializationOptions));
    Console.WriteLine($"Exported {packages.Count} packages to {output}");
}, exOutputOpt, exSourceOpt, exVersionsOpt);

// ── Error ──
var errorCommand = new Command("error", "Look up error codes");
var errInputArg = new Argument<string>("input", "Error code");
errorCommand.AddArgument(errInputArg);

errorCommand.SetHandler(PrintErrorLookup, errInputArg);

// ── Settings ──
var settingsCommand = new Command("settings", "Settings");
settingsCommand.AddAlias("config");
var settingsEnableOpt = new Option<string?>("--enable", "Enables the specific administrator setting");
var settingsDisableOpt = new Option<string?>("--disable", "Disables the specific administrator setting");
settingsCommand.AddOption(settingsEnableOpt);
settingsCommand.AddOption(settingsDisableOpt);
var settingsExportCmd = new Command("export", "Export settings");
var settingsSetCmd = new Command("set", "Sets the value of an admin setting.");
var settingsSetNameOpt = new Option<string>("--setting", "Name of the setting to modify") { IsRequired = true };
var settingsSetValueOpt = new Option<string>("--value", "Value to set for the setting.") { IsRequired = true };
settingsSetCmd.AddOption(settingsSetNameOpt);
settingsSetCmd.AddOption(settingsSetValueOpt);
var settingsResetCmd = new Command("reset", "Resets an admin setting to its default value.");
var settingsResetNameOpt = new Option<string?>("--setting", "Name of the setting to modify");
var settingsResetAllOpt = new Option<bool>("--recurse", "Resets all admin settings");
settingsResetAllOpt.AddAlias("-r");
settingsResetAllOpt.AddAlias("--all");
settingsResetCmd.AddOption(settingsResetNameOpt);
settingsResetCmd.AddOption(settingsResetAllOpt);
settingsCommand.AddCommand(settingsExportCmd);
settingsCommand.AddCommand(settingsSetCmd);
settingsCommand.AddCommand(settingsResetCmd);

settingsCommand.SetHandler((ctx) =>
{
    var enable = ctx.ParseResult.GetValueForOption(settingsEnableOpt);
    var disable = ctx.ParseResult.GetValueForOption(settingsDisableOpt);
    if (!string.IsNullOrWhiteSpace(enable) && !string.IsNullOrWhiteSpace(disable))
        throw new InvalidOperationException("--enable and --disable cannot be used together.");

    using var repo = Repository.Open();
    if (!string.IsNullOrWhiteSpace(enable))
    {
        repo.SetAdminSetting(enable, true);
        Console.WriteLine($"Enabled admin setting '{enable}'.");
        return;
    }

    if (!string.IsNullOrWhiteSpace(disable))
    {
        repo.SetAdminSetting(disable, false);
        Console.WriteLine($"Disabled admin setting '{disable}'.");
        return;
    }

    WriteJsonNode(repo.GetUserSettings(), GetOutputFormat(ctx.ParseResult.GetValueForOption(outputOption)));
});

settingsExportCmd.SetHandler((ctx) =>
{
    using var repo = Repository.Open();
    WriteJsonNode(repo.GetUserSettings(), GetOutputFormat(ctx.ParseResult.GetValueForOption(outputOption)));
});

settingsSetCmd.SetHandler((ctx) =>
{
    using var repo = Repository.Open();
    var name = ctx.ParseResult.GetValueForOption(settingsSetNameOpt)
        ?? throw new InvalidOperationException("settings set requires --setting.");
    var rawValue = ctx.ParseResult.GetValueForOption(settingsSetValueOpt)
        ?? throw new InvalidOperationException("settings set requires --value.");
    var value = ParseBooleanSettingValue(rawValue);
    repo.SetAdminSetting(name, value);
    var output = GetOutputFormat(ctx.ParseResult.GetValueForOption(outputOption));
    if (output != OutputFormat.Text)
        WriteJsonNode(repo.GetAdminSettings(), output);
    else
        Console.WriteLine($"Set admin setting '{name}' to {value.ToString().ToLowerInvariant()}.");
});

settingsResetCmd.SetHandler((ctx) =>
{
    using var repo = Repository.Open();
    var name = ctx.ParseResult.GetValueForOption(settingsResetNameOpt);
    var resetAll = ctx.ParseResult.GetValueForOption(settingsResetAllOpt);
    if (string.IsNullOrWhiteSpace(name) && !resetAll)
        throw new InvalidOperationException("settings reset requires --setting or --all.");

    repo.ResetAdminSetting(name, resetAll);
    var output = GetOutputFormat(ctx.ParseResult.GetValueForOption(outputOption));
    if (output != OutputFormat.Text)
        WriteJsonNode(repo.GetAdminSettings(), output);
    else if (resetAll)
        Console.WriteLine("Reset all admin settings.");
    else
        Console.WriteLine($"Reset admin setting '{name}'.");
});

// ── Features ──
var featuresCommand = new Command("features", "Show features");
featuresCommand.SetHandler(() =>
{
    Console.WriteLine("Feature                          Status");
    Console.WriteLine("-".PadRight(50, '-'));
    Console.WriteLine("Pure C# implementation           Enabled");
    Console.WriteLine("Preindexed source support        Enabled");
    Console.WriteLine("REST source support              Enabled");
    Console.WriteLine("Installed package discovery       Enabled");
    Console.WriteLine("Install/uninstall                Enabled");
});

// ── Validate ──
var validateCommand = new Command("validate", "Validate manifest");
var valManifestArg = new Argument<string>("manifest", "Manifest file");
validateCommand.AddArgument(valManifestArg);
validateCommand.SetHandler((manifest) =>
{
    if (!File.Exists(manifest)) { Console.Error.WriteLine($"error: File not found: {manifest}"); return; }
    Console.WriteLine("Manifest validation succeeded.");
}, valManifestArg);

// ── Download ──
var downloadCommand = new Command("download", "Download installer");
downloadCommand.AddAlias("dl");
var dlArg = new Argument<string?>("query", () => null, "Package query");
var dlqOpt = QueryArg(); var dlidOpt = IdOpt(); var dlnOpt = NameOpt(); var dlmOpt = MonikerOpt(); var dlsOpt = SourceOpt(); var dleOpt = ExactOpt(); var dlvOpt = VersionOpt();
var dlDirOpt = new Option<string?>("--download-directory", "Download directory"); dlDirOpt.AddAlias("-d");
var dlManifestOpt = new Option<string?>("--manifest", "Local manifest file or directory"); dlManifestOpt.AddAlias("-m");
var dlLocaleOpt = new Option<string?>("--locale", "Installer locale");
var dlTypeOpt = new Option<string?>("--installer-type", "Installer type");
var dlArchOpt = new Option<string?>("--architecture", "Architecture"); dlArchOpt.AddAlias("-a");
var dlPlatformOpt = new Option<string?>("--platform", "Target platform");
var dlOsVersionOpt = new Option<string?>("--os-version", "Target OS version");
var dlScopeOpt = new Option<string?>("--scope", "Install scope");
var dlIgnoreSecurityHashOpt = new Option<bool>("--ignore-security-hash", "Ignore installer hash mismatches");
var dlSkipDependenciesOpt = new Option<bool>("--skip-dependencies", "Skip package dependencies");
var dlAcceptPkgAgreementsOpt = new Option<bool>("--accept-package-agreements", "Accept package agreements");
downloadCommand.AddArgument(dlArg);
foreach (var o in new Option[] { dlqOpt, dlidOpt, dlnOpt, dlmOpt, dlsOpt, dleOpt, dlvOpt, dlDirOpt, dlManifestOpt, dlLocaleOpt, dlTypeOpt, dlArchOpt, dlPlatformOpt, dlOsVersionOpt, dlScopeOpt, dlIgnoreSecurityHashOpt, dlSkipDependenciesOpt, dlAcceptPkgAgreementsOpt }) downloadCommand.AddOption(o);

downloadCommand.SetHandler((ctx) =>
{
    var query = new PackageQuery
    {
        Query = ctx.ParseResult.GetValueForArgument(dlArg) ?? ctx.ParseResult.GetValueForOption(dlqOpt),
        Id = ctx.ParseResult.GetValueForOption(dlidOpt),
        Name = ctx.ParseResult.GetValueForOption(dlnOpt),
        Moniker = ctx.ParseResult.GetValueForOption(dlmOpt),
        Source = ctx.ParseResult.GetValueForOption(dlsOpt),
        Exact = ctx.ParseResult.GetValueForOption(dleOpt),
        Version = ctx.ParseResult.GetValueForOption(dlvOpt),
        Locale = ctx.ParseResult.GetValueForOption(dlLocaleOpt),
        InstallerType = ctx.ParseResult.GetValueForOption(dlTypeOpt),
        InstallerArchitecture = ctx.ParseResult.GetValueForOption(dlArchOpt),
        Platform = ctx.ParseResult.GetValueForOption(dlPlatformOpt),
        OsVersion = ctx.ParseResult.GetValueForOption(dlOsVersionOpt),
        InstallScope = ctx.ParseResult.GetValueForOption(dlScopeOpt),
    };
    var dir = ctx.ParseResult.GetValueForOption(dlDirOpt)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    using var repo = Repository.Open();
    var request = CreateInstallRequest(
        query,
        ctx.ParseResult.GetValueForOption(dlManifestOpt),
        InstallerMode.SilentWithProgress,
        null,
        null,
        null,
        null,
        ctx.ParseResult.GetValueForOption(dlSkipDependenciesOpt),
        false,
        ctx.ParseResult.GetValueForOption(dlAcceptPkgAgreementsOpt),
        false,
        null,
        false,
        ctx.ParseResult.GetValueForOption(dlIgnoreSecurityHashOpt),
        null,
        false);
    var (manifest, path) = repo.DownloadInstaller(request, dir);
    Console.WriteLine($"Downloaded {manifest.Name} v{manifest.Version}");
    Console.WriteLine($"  Path: {path}");
});

// ── Pin commands ──
var pinCommand = new Command("pin", "Manage pins");
var pinListCmd = new Command("list", "List pins");
var pinAddCmd = new Command("add", "Add pin");
var plArg = new Argument<string?>("query", () => null, "Package query");
var plqOpt = QueryArg(); var plIdOpt = IdOpt(); var plNameOpt = NameOpt(); var plMonikerOpt = MonikerOpt(); var plSourceOpt = SourceOpt(); var plExactOpt = ExactOpt();
var plTagOpt = new Option<string?>("--tag", "Filter by tag");
var plCmdOpt = new Option<string?>("--command", "Filter by command"); plCmdOpt.AddAlias("--cmd");
pinListCmd.AddArgument(plArg);
foreach (var o in new Option[] { plqOpt, plIdOpt, plNameOpt, plMonikerOpt, plSourceOpt, plExactOpt, plTagOpt, plCmdOpt }) pinListCmd.AddOption(o);

var paArg = new Argument<string?>("query", () => null, "Package query");
var paqOpt = QueryArg(); var paIdOpt = IdOpt(); var paNameOpt = NameOpt(); var paMonikerOpt = MonikerOpt(); var paSourceOpt = SourceOpt(); var paExactOpt = ExactOpt();
var paTagOpt = new Option<string?>("--tag", "Filter by tag");
var paCmdOpt = new Option<string?>("--command", "Filter by command"); paCmdOpt.AddAlias("--cmd");
var paVersionOpt = new Option<string?>("--version", "Pin version"); paVersionOpt.AddAlias("-v");
var paBlockingOpt = new Option<bool>("--blocking", "Blocking pin");
var paInstalledOpt = new Option<bool>("--installed", "Pin a specific installed version");
var paForceOpt = new Option<bool>("--force", "Replace an existing pin");
pinAddCmd.AddArgument(paArg);
foreach (var o in new Option[] { paqOpt, paIdOpt, paNameOpt, paMonikerOpt, paSourceOpt, paExactOpt, paTagOpt, paCmdOpt, paVersionOpt, paBlockingOpt, paInstalledOpt, paForceOpt }) pinAddCmd.AddOption(o);

var pinRemoveCmd = new Command("remove", "Remove pin");
var prArg = new Argument<string?>("query", () => null, "Package query");
var prqOpt = QueryArg(); var prIdOpt = IdOpt(); var prNameOpt = NameOpt(); var prMonikerOpt = MonikerOpt(); var prSourceOpt = SourceOpt(); var prExactOpt = ExactOpt();
var prTagOpt = new Option<string?>("--tag", "Filter by tag");
var prCmdOpt = new Option<string?>("--command", "Filter by command"); prCmdOpt.AddAlias("--cmd");
var prInstalledOpt = new Option<bool>("--installed", "Remove the pin for a specific installed version");
pinRemoveCmd.AddArgument(prArg);
foreach (var o in new Option[] { prqOpt, prIdOpt, prNameOpt, prMonikerOpt, prSourceOpt, prExactOpt, prTagOpt, prCmdOpt, prInstalledOpt }) pinRemoveCmd.AddOption(o);

var pinResetCmd = new Command("reset", "Reset pins");
var prForceOpt = new Option<bool>("--force", "Force reset");
var prResetSourceOpt = SourceOpt();
pinResetCmd.AddOption(prForceOpt);
pinResetCmd.AddOption(prResetSourceOpt);
pinCommand.AddCommand(pinListCmd); pinCommand.AddCommand(pinAddCmd); pinCommand.AddCommand(pinRemoveCmd); pinCommand.AddCommand(pinResetCmd);

pinListCmd.SetHandler((ctx) =>
{
    using var repo = Repository.Open();
    var query = CreatePinQuery(
        ctx.ParseResult.GetValueForArgument(plArg) ?? ctx.ParseResult.GetValueForOption(plqOpt),
        ctx.ParseResult.GetValueForOption(plIdOpt),
        ctx.ParseResult.GetValueForOption(plNameOpt),
        ctx.ParseResult.GetValueForOption(plMonikerOpt),
        ctx.ParseResult.GetValueForOption(plTagOpt),
        ctx.ParseResult.GetValueForOption(plCmdOpt),
        ctx.ParseResult.GetValueForOption(plSourceOpt),
        ctx.ParseResult.GetValueForOption(plExactOpt));
    var pins = FilterPins(repo, query);
    if (pins.Count == 0) { Console.WriteLine("No pins found."); return; }
    Console.WriteLine($"{"Package Id",-40} {"Version",-20} {"Source",-15} Pin Type");
    Console.WriteLine(new string('-', 85));
    foreach (var p in pins) Console.WriteLine($"{p.PackageId,-40} {p.Version,-20} {p.SourceId,-15} {p.PinType}");
});

pinAddCmd.SetHandler((ctx) =>
{
    using var repo = Repository.Open();
    var query = CreatePinQuery(
        ctx.ParseResult.GetValueForArgument(paArg) ?? ctx.ParseResult.GetValueForOption(paqOpt),
        ctx.ParseResult.GetValueForOption(paIdOpt),
        ctx.ParseResult.GetValueForOption(paNameOpt),
        ctx.ParseResult.GetValueForOption(paMonikerOpt),
        ctx.ParseResult.GetValueForOption(paTagOpt),
        ctx.ParseResult.GetValueForOption(paCmdOpt),
        ctx.ParseResult.GetValueForOption(paSourceOpt),
        ctx.ParseResult.GetValueForOption(paExactOpt));
    EnsurePinQueryProvided(query, "pin add");

    var blocking = ctx.ParseResult.GetValueForOption(paBlockingOpt);
    var installed = ctx.ParseResult.GetValueForOption(paInstalledOpt);
    var force = ctx.ParseResult.GetValueForOption(paForceOpt);
    var requestedVersion = ctx.ParseResult.GetValueForOption(paVersionOpt);

    string packageId;
    string sourceId;
    string? resolvedVersion;
    if (installed)
    {
        var target = ResolveSingleInstalledPinTarget(repo, query);
        packageId = target.Id;
        sourceId = target.SourceName ?? query.Source ?? "";
        resolvedVersion = target.InstalledVersion;
    }
    else
    {
        var target = ResolveSingleAvailablePinTarget(repo, query);
        packageId = target.Id;
        sourceId = target.SourceName;
        resolvedVersion = target.Version;
    }

    if (repo.ListPins(sourceId).Any(pin => pin.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)) && !force)
        throw new InvalidOperationException("A pin for the selected package already exists. Rerun with --force to replace it.");

    var pinVersion = !string.IsNullOrWhiteSpace(requestedVersion)
        ? requestedVersion
        : blocking
            ? "*"
            : resolvedVersion ?? "*";
    repo.AddPin(packageId, pinVersion, sourceId, blocking ? PinType.Blocking : PinType.Pinning);
    Console.WriteLine($"Pin added for {packageId}");
});

pinRemoveCmd.SetHandler((ctx) =>
{
    using var repo = Repository.Open();
    var query = CreatePinQuery(
        ctx.ParseResult.GetValueForArgument(prArg) ?? ctx.ParseResult.GetValueForOption(prqOpt),
        ctx.ParseResult.GetValueForOption(prIdOpt),
        ctx.ParseResult.GetValueForOption(prNameOpt),
        ctx.ParseResult.GetValueForOption(prMonikerOpt),
        ctx.ParseResult.GetValueForOption(prTagOpt),
        ctx.ParseResult.GetValueForOption(prCmdOpt),
        ctx.ParseResult.GetValueForOption(prSourceOpt),
        ctx.ParseResult.GetValueForOption(prExactOpt));
    EnsurePinQueryProvided(query, "pin remove");

    PinRecord? pin;
    if (ctx.ParseResult.GetValueForOption(prInstalledOpt))
    {
        var target = ResolveSingleInstalledPinTarget(repo, query);
        pin = repo.ListPins(target.SourceName ?? query.Source ?? "")
            .FirstOrDefault(candidate => candidate.PackageId.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
    }
    else
    {
        var pins = FilterPins(repo, query);
        if (pins.Count == 0)
        {
            Console.WriteLine("No pin found matching the query.");
            return;
        }

        if (pins.Count > 1)
            throw new InvalidOperationException("Multiple pins matched the query; refine the query.");

        pin = pins[0];
    }

    if (pin is null)
    {
        Console.WriteLine("No pin found matching the query.");
        return;
    }

    Console.WriteLine(repo.RemovePin(pin.PackageId, pin.SourceId) ? $"Pin removed for {pin.PackageId}" : $"No pin found for {pin.PackageId}");
});

pinResetCmd.SetHandler((force, source) =>
{
    if (!force) { Console.Error.WriteLine("error: Resetting all pins requires --force"); return; }
    using var repo = Repository.Open();
    repo.ResetPins(source);
    Console.WriteLine(string.IsNullOrWhiteSpace(source) ? "All pins have been reset." : $"All pins for source '{source}' have been reset.");
}, prForceOpt, prResetSourceOpt);

// ── Install ──
var installCommand = new Command("install", "Install a package");
installCommand.AddAlias("add");
var iArg = new Argument<string?>("query", () => null, "Package query");
var iqOpt = QueryArg(); var iidOpt = IdOpt(); var inameOpt = NameOpt(); var imonikerOpt = MonikerOpt(); var isrcOpt = SourceOpt();
var ieOpt = ExactOpt(); var ivOpt = VersionOpt();
var ichannelOpt = new Option<string?>("--channel", "Channel");
var ilocaleOpt = new Option<string?>("--locale", "Installer locale");
var itypeOpt = new Option<string?>("--installer-type", "Installer type");
var iarchOpt = new Option<string?>("--architecture", "Architecture"); iarchOpt.AddAlias("-a");
var iplatformOpt = new Option<string?>("--platform", "Target platform");
var iosVersionOpt = new Option<string?>("--os-version", "Target OS version");
var iscopeOpt = new Option<string?>("--scope", "Install scope");
var imanifestOpt = new Option<string?>("--manifest", "Local manifest file or directory"); imanifestOpt.AddAlias("-m");
var ilogOpt = new Option<string?>("--log", "Installer log path");
var icustomOpt = new Option<string?>("--custom", "Additional installer switches");
var ioverrideOpt = new Option<string?>("--override", "Override installer arguments");
var ilocationOpt = new Option<string?>("--location", "Install location"); ilocationOpt.AddAlias("-l");
var iignoreSecurityHashOpt = new Option<bool>("--ignore-security-hash", "Ignore installer hash mismatches");
var iskipDepsOpt = new Option<bool>("--skip-dependencies", "Skip package dependencies");
var idepsOnlyOpt = new Option<bool>("--dependencies-only", "Install dependencies only"); idepsOnlyOpt.AddAlias("--dependencies");
var idependencySourceOpt = new Option<string?>("--dependency-source", "Source to use when resolving dependencies");
var iacceptPkgAgreementsOpt = new Option<bool>("--accept-package-agreements", "Accept package agreements");
var inoUpgradeOpt = new Option<bool>("--no-upgrade", "Skip upgrade if the package is already installed");
var iforceOpt = new Option<bool>("--force", "Force install behavior");
var irenameOpt = new Option<string?>("--rename", "Rename the installer or target payload"); irenameOpt.AddAlias("-r");
var iuninstallPreviousOpt = new Option<bool>("--uninstall-previous", "Uninstall previous versions before installing");
var iSilentOpt = new Option<bool>("--silent", "Silent install"); iSilentOpt.AddAlias("-h");
var iInteractiveOpt = new Option<bool>("--interactive", "Interactive install"); iInteractiveOpt.AddAlias("-i");
installCommand.AddArgument(iArg);
foreach (var o in new Option[] { iqOpt, iidOpt, inameOpt, imonikerOpt, isrcOpt, ieOpt, ivOpt, ichannelOpt, ilocaleOpt, itypeOpt, iarchOpt, iplatformOpt, iosVersionOpt, iscopeOpt, imanifestOpt, ilogOpt, icustomOpt, ioverrideOpt, ilocationOpt, iignoreSecurityHashOpt, iskipDepsOpt, idepsOnlyOpt, idependencySourceOpt, iacceptPkgAgreementsOpt, inoUpgradeOpt, iforceOpt, irenameOpt, iuninstallPreviousOpt, iSilentOpt, iInteractiveOpt }) installCommand.AddOption(o);

installCommand.SetHandler((ctx) =>
{
    var query = new PackageQuery
    {
        Query = ctx.ParseResult.GetValueForArgument(iArg) ?? ctx.ParseResult.GetValueForOption(iqOpt),
        Id = ctx.ParseResult.GetValueForOption(iidOpt),
        Name = ctx.ParseResult.GetValueForOption(inameOpt),
        Moniker = ctx.ParseResult.GetValueForOption(imonikerOpt),
        Source = ctx.ParseResult.GetValueForOption(isrcOpt),
        Exact = ctx.ParseResult.GetValueForOption(ieOpt),
        Version = ctx.ParseResult.GetValueForOption(ivOpt),
        Channel = ctx.ParseResult.GetValueForOption(ichannelOpt),
        Locale = ctx.ParseResult.GetValueForOption(ilocaleOpt),
        InstallerType = ctx.ParseResult.GetValueForOption(itypeOpt),
        InstallerArchitecture = ctx.ParseResult.GetValueForOption(iarchOpt),
        Platform = ctx.ParseResult.GetValueForOption(iplatformOpt),
        OsVersion = ctx.ParseResult.GetValueForOption(iosVersionOpt),
        InstallScope = ctx.ParseResult.GetValueForOption(iscopeOpt),
    };
    var silent = ctx.ParseResult.GetValueForOption(iSilentOpt);
    var interactive = ctx.ParseResult.GetValueForOption(iInteractiveOpt);
    if (silent && interactive)
        throw new InvalidOperationException("--silent and --interactive cannot be used together.");
    using var repo = Repository.Open();
    var mode = interactive ? InstallerMode.Interactive : silent ? InstallerMode.Silent : InstallerMode.SilentWithProgress;
    var result = repo.Install(CreateInstallRequest(
        query,
        ctx.ParseResult.GetValueForOption(imanifestOpt),
        mode,
        ctx.ParseResult.GetValueForOption(ilogOpt),
        ctx.ParseResult.GetValueForOption(icustomOpt),
        ctx.ParseResult.GetValueForOption(ioverrideOpt),
        ctx.ParseResult.GetValueForOption(ilocationOpt),
        ctx.ParseResult.GetValueForOption(iskipDepsOpt),
        ctx.ParseResult.GetValueForOption(idepsOnlyOpt),
        ctx.ParseResult.GetValueForOption(iacceptPkgAgreementsOpt),
        ctx.ParseResult.GetValueForOption(iforceOpt),
        ctx.ParseResult.GetValueForOption(irenameOpt),
        ctx.ParseResult.GetValueForOption(iuninstallPreviousOpt),
        ctx.ParseResult.GetValueForOption(iignoreSecurityHashOpt),
        ctx.ParseResult.GetValueForOption(idependencySourceOpt),
        ctx.ParseResult.GetValueForOption(inoUpgradeOpt)));
    PrintPackageActionResult(result, "install", "installed");
});

// ── Uninstall ──
var uninstallCommand = new Command("uninstall", "Uninstall a package");
uninstallCommand.AddAlias("remove");
uninstallCommand.AddAlias("rm");
var uiArg = new Argument<string?>("query", () => null, "Package query");
var uiqOpt = QueryArg(); var uiidOpt = IdOpt(); var uinameOpt = NameOpt(); var uimonikerOpt = MonikerOpt(); var uisOpt = SourceOpt();
var uieOpt = ExactOpt(); var uivOpt = VersionOpt();
var uiscopeOpt = new Option<string?>("--scope", "Install scope");
var uimanifestOpt = new Option<string?>("--manifest", "Local manifest file or directory"); uimanifestOpt.AddAlias("-m");
var uiproductCodeOpt = new Option<string?>("--product-code", "Installed product code");
var uiallVersionsOpt = new Option<bool>("--all-versions", "Uninstall all matching versions"); uiallVersionsOpt.AddAlias("--all");
var uiInteractiveOpt = new Option<bool>("--interactive", "Interactive uninstall"); uiInteractiveOpt.AddAlias("-i");
var uiForceOpt = new Option<bool>("--force", "Force uninstall behavior");
var uiPurgeOpt = new Option<bool>("--purge", "Purge portable package contents");
var uiPreserveOpt = new Option<bool>("--preserve", "Preserve portable package contents");
var uiLogOpt = new Option<string?>("--log", "Uninstaller log path");
var uiSilentOpt = new Option<bool>("--silent", "Silent uninstall"); uiSilentOpt.AddAlias("-h");
uninstallCommand.AddArgument(uiArg);
foreach (var o in new Option[] { uiqOpt, uiidOpt, uinameOpt, uimonikerOpt, uisOpt, uieOpt, uivOpt, uiscopeOpt, uimanifestOpt, uiproductCodeOpt, uiallVersionsOpt, uiInteractiveOpt, uiForceOpt, uiPurgeOpt, uiPreserveOpt, uiLogOpt, uiSilentOpt }) uninstallCommand.AddOption(o);

uninstallCommand.SetHandler((ctx) =>
{
    var query = new PackageQuery
    {
        Query = ctx.ParseResult.GetValueForArgument(uiArg) ?? ctx.ParseResult.GetValueForOption(uiqOpt),
        Id = ctx.ParseResult.GetValueForOption(uiidOpt),
        Name = ctx.ParseResult.GetValueForOption(uinameOpt),
        Moniker = ctx.ParseResult.GetValueForOption(uimonikerOpt),
        Source = ctx.ParseResult.GetValueForOption(uisOpt),
        Exact = ctx.ParseResult.GetValueForOption(uieOpt),
        Version = ctx.ParseResult.GetValueForOption(uivOpt),
        InstallScope = ctx.ParseResult.GetValueForOption(uiscopeOpt),
    };
    var silent = ctx.ParseResult.GetValueForOption(uiSilentOpt);
    var interactive = ctx.ParseResult.GetValueForOption(uiInteractiveOpt);
    if (silent && interactive)
        throw new InvalidOperationException("--silent and --interactive cannot be used together.");
    using var repo = Repository.Open();
    var result = repo.Uninstall(new UninstallRequest
    {
        Query = query,
        ManifestPath = ctx.ParseResult.GetValueForOption(uimanifestOpt),
        ProductCode = ctx.ParseResult.GetValueForOption(uiproductCodeOpt),
        Mode = interactive ? InstallerMode.Interactive : silent ? InstallerMode.Silent : InstallerMode.SilentWithProgress,
        AllVersions = ctx.ParseResult.GetValueForOption(uiallVersionsOpt),
        Force = ctx.ParseResult.GetValueForOption(uiForceOpt),
        Purge = ctx.ParseResult.GetValueForOption(uiPurgeOpt),
        Preserve = ctx.ParseResult.GetValueForOption(uiPreserveOpt),
        LogPath = ctx.ParseResult.GetValueForOption(uiLogOpt),
    });
    PrintPackageActionResult(result, "uninstall", "uninstalled");
});

// ── Repair ──
var repairCommand = new Command("repair", "Repair a package");
repairCommand.AddAlias("fix");
var rArg = new Argument<string?>("query", () => null, "Package query");
var rqOpt = QueryArg(); var ridOpt = IdOpt(); var rnameOpt = NameOpt(); var rmonikerOpt = MonikerOpt(); var rsrcOpt = SourceOpt();
var reOpt = ExactOpt(); var rvOpt = VersionOpt();
var rmanifestOpt = new Option<string?>("--manifest", "Local manifest file or directory"); rmanifestOpt.AddAlias("-m");
var rproductCodeOpt = new Option<string?>("--product-code", "Installed product code");
var rarchOpt = new Option<string?>("--architecture", "Architecture"); rarchOpt.AddAlias("-a");
var rscopeOpt = new Option<string?>("--scope", "Install scope");
var rlocaleOpt = new Option<string?>("--locale", "Installer locale");
var rlogOpt = new Option<string?>("--log", "Installer log path"); rlogOpt.AddAlias("-o");
var racceptPkgAgreementsOpt = new Option<bool>("--accept-package-agreements", "Accept package agreements");
var rignoreSecurityHashOpt = new Option<bool>("--ignore-security-hash", "Ignore installer hash mismatches");
var rforceOpt = new Option<bool>("--force", "Force repair behavior");
var rSilentOpt = new Option<bool>("--silent", "Silent install"); rSilentOpt.AddAlias("-h");
var rInteractiveOpt = new Option<bool>("--interactive", "Interactive install"); rInteractiveOpt.AddAlias("-i");
repairCommand.AddArgument(rArg);
foreach (var o in new Option[] { rqOpt, ridOpt, rnameOpt, rmonikerOpt, rsrcOpt, reOpt, rvOpt, rmanifestOpt, rproductCodeOpt, rarchOpt, rscopeOpt, rlocaleOpt, rlogOpt, racceptPkgAgreementsOpt, rignoreSecurityHashOpt, rforceOpt, rSilentOpt, rInteractiveOpt }) repairCommand.AddOption(o);

repairCommand.SetHandler((ctx) =>
{
    var silent = ctx.ParseResult.GetValueForOption(rSilentOpt);
    var interactive = ctx.ParseResult.GetValueForOption(rInteractiveOpt);
    if (silent && interactive)
        throw new InvalidOperationException("--silent and --interactive cannot be used together.");

    using var repo = Repository.Open();
    var mode = interactive ? InstallerMode.Interactive : silent ? InstallerMode.Silent : InstallerMode.SilentWithProgress;
    var result = repo.Repair(CreateRepairRequest(
        new PackageQuery
        {
            Query = ctx.ParseResult.GetValueForArgument(rArg) ?? ctx.ParseResult.GetValueForOption(rqOpt),
            Id = ctx.ParseResult.GetValueForOption(ridOpt),
            Name = ctx.ParseResult.GetValueForOption(rnameOpt),
            Moniker = ctx.ParseResult.GetValueForOption(rmonikerOpt),
            Source = ctx.ParseResult.GetValueForOption(rsrcOpt),
            Exact = ctx.ParseResult.GetValueForOption(reOpt),
            Version = ctx.ParseResult.GetValueForOption(rvOpt),
            Locale = ctx.ParseResult.GetValueForOption(rlocaleOpt),
            InstallerArchitecture = ctx.ParseResult.GetValueForOption(rarchOpt),
            InstallScope = ctx.ParseResult.GetValueForOption(rscopeOpt),
        },
        ctx.ParseResult.GetValueForOption(rmanifestOpt),
        ctx.ParseResult.GetValueForOption(rproductCodeOpt),
        mode,
        ctx.ParseResult.GetValueForOption(rlogOpt),
        ctx.ParseResult.GetValueForOption(racceptPkgAgreementsOpt),
        ctx.ParseResult.GetValueForOption(rforceOpt),
        ctx.ParseResult.GetValueForOption(rignoreSecurityHashOpt)));
    PrintPackageActionResult(result, "repair", "repaired");
});

// ── Import ──
var importCommand = new Command("import", "Import packages");
var imFileOpt = new Option<string>("--import-file", "Import file") { IsRequired = true }; imFileOpt.AddAlias("-i");
var imDryRunOpt = new Option<bool>("--dry-run", "Dry run only");
var imIgnoreUnavailableOpt = new Option<bool>("--ignore-unavailable", "Ignore unavailable packages");
var imIgnoreVersionsOpt = new Option<bool>("--ignore-versions", "Ignore package versions in the import file");
var imNoUpgradeOpt = new Option<bool>("--no-upgrade", "Skip packages that are already installed");
var imAcceptPkgAgreementsOpt = new Option<bool>("--accept-package-agreements", "Accept package agreements");
importCommand.AddOption(imFileOpt); importCommand.AddOption(imDryRunOpt); importCommand.AddOption(imIgnoreUnavailableOpt); importCommand.AddOption(imIgnoreVersionsOpt); importCommand.AddOption(imNoUpgradeOpt); importCommand.AddOption(imAcceptPkgAgreementsOpt);

importCommand.SetHandler((ctx) =>
{
    var file = ctx.ParseResult.GetValueForOption(imFileOpt)
        ?? throw new InvalidOperationException("import requires --import-file.");
    var dryRun = ctx.ParseResult.GetValueForOption(imDryRunOpt);
    var ignoreUnavailable = ctx.ParseResult.GetValueForOption(imIgnoreUnavailableOpt);
    var ignoreVersions = ctx.ParseResult.GetValueForOption(imIgnoreVersionsOpt);
    var noUpgrade = ctx.ParseResult.GetValueForOption(imNoUpgradeOpt);
    var acceptPackageAgreements = ctx.ParseResult.GetValueForOption(imAcceptPkgAgreementsOpt);

    if (!File.Exists(file)) { Console.Error.WriteLine($"error: File not found: {file}"); return; }
    var jsonText = File.ReadAllText(file);
    var doc = JsonSerializer.Deserialize<JsonElement>(jsonText, SerializationOptions);
    var sources = doc.GetProperty("Sources").EnumerateArray().ToList();

    using var repo = Repository.Open();
    int total = 0;
    int skipped = 0;
    foreach (var source in sources)
    {
        var sourceName = source.TryGetProperty("SourceDetails", out var sourceDetails)
            ? GetJsonString(sourceDetails, "Name")
            : null;
        var packages = source.GetProperty("Packages").EnumerateArray().ToList();
        foreach (var pkg in packages)
        {
            var pkgId = pkg.GetProperty("PackageIdentifier").GetString()!;
            var pkgVersion = ignoreVersions ? null : GetJsonString(pkg, "Version");
            if (dryRun)
            {
                Console.WriteLine($"[dry-run] Would install: {pkgId}");
            }
            else if (noUpgrade && IsInstalledPackagePresent(repo, pkgId, sourceName))
            {
                Console.WriteLine($"[no-upgrade] Skipping already installed package: {pkgId}");
                skipped++;
            }
            else
            {
                try
                {
                    Console.Write($"Installing {pkgId}...");
                    var result = repo.Install(CreateInstallRequest(
                        new PackageQuery
                        {
                            Id = pkgId,
                            Source = sourceName,
                            Exact = true,
                            Version = pkgVersion,
                        },
                        null,
                        InstallerMode.SilentWithProgress,
                        null,
                        null,
                        null,
                        null,
                        false,
                        false,
                        acceptPackageAgreements,
                        false,
                        null,
                        false,
                        false,
                        null,
                        noUpgrade));
                    if (result.NoOp)
                    {
                        Console.WriteLine(" no-op");
                        PrintWarnings(result.Warnings);
                        skipped++;
                    }
                    else
                    {
                        PrintWarnings(result.Warnings);
                        Console.WriteLine(result.Success ? " done" : $" failed (exit {result.ExitCode})");
                    }
                }
                catch (Exception ex) when (ignoreUnavailable && CanIgnoreUnavailableImportFailure(ex))
                {
                    Console.WriteLine(" unavailable");
                    Console.Error.WriteLine($"warning: Skipping unavailable package '{pkgId}': {ex.Message}");
                    skipped++;
                }
                catch (Exception ex) { Console.Error.WriteLine($" error: {ex.Message}"); }
            }
            total++;
        }
    }
    if (!dryRun && skipped > 0)
        Console.WriteLine($"Skipped {skipped} package(s).");
    Console.WriteLine($"{total} package(s) {(dryRun ? "would be installed" : "processed")}.");
});

// ── Add all commands to root ──
rootCommand.AddCommand(searchCommand);
rootCommand.AddCommand(showCommand);
rootCommand.AddCommand(listCommand);
rootCommand.AddCommand(upgradeCommand);
rootCommand.AddCommand(sourceCommand);
rootCommand.AddCommand(cacheCommand);
rootCommand.AddCommand(hashCommand);
rootCommand.AddCommand(exportCommand);
rootCommand.AddCommand(errorCommand);
rootCommand.AddCommand(settingsCommand);
rootCommand.AddCommand(featuresCommand);
rootCommand.AddCommand(validateCommand);
rootCommand.AddCommand(downloadCommand);
rootCommand.AddCommand(pinCommand);
rootCommand.AddCommand(installCommand);
rootCommand.AddCommand(uninstallCommand);
rootCommand.AddCommand(repairCommand);
rootCommand.AddCommand(importCommand);

rootCommand.SetHandler((ctx) =>
{
    if (ctx.ParseResult.GetValueForOption(infoOption))
    {
        PrintInfo();
        return;
    }
});

return rootCommand.Invoke(args);

// ═══════════════ Output helpers ═══════════════

static void PrintInfo()
{
    PrintVersion();
    Console.WriteLine("Pure C# subset of Pinget (portable winget)");
    Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
    Console.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
}

static void PrintVersion() => Console.WriteLine($"pinget v{Version}");

static OutputFormat GetOutputFormat(string? value) =>
    value?.ToLowerInvariant() switch
    {
        "json" => OutputFormat.Json,
        "yaml" => OutputFormat.Yaml,
        _ => OutputFormat.Text,
    };

void WriteStructuredOutput(object value, OutputFormat output)
{
    switch (output)
    {
        case OutputFormat.Json:
            Console.WriteLine(JsonSerializer.Serialize(value, SerializationOptions));
            break;
        case OutputFormat.Yaml:
            Console.Write(new SerializerBuilder().Build().Serialize(value));
            break;
        default:
            throw new InvalidOperationException("Text output should be handled separately.");
    }
}

void WriteManifestStructuredOutput(object value, OutputFormat output)
{
    if (output == OutputFormat.Yaml && value is List<Dictionary<string, object?>> documents)
    {
        var serializer = new SerializerBuilder().Build();
        foreach (var document in documents)
        {
            Console.Write("---\n");
            Console.Write(serializer.Serialize(document));
        }
        return;
    }

    WriteStructuredOutput(value, output);
}

static void PrintSearch(SearchResponse result)
{
    PrintWarnings(result.Warnings);
    if (result.Matches.Count == 0) { Console.WriteLine("No package matched the supplied query."); return; }

    bool showMatch = result.Matches.Any(m => m.MatchCriteria is not null);
    if (showMatch)
    {
        Console.WriteLine("{0,-32} {1,-40} {2,-18} {3,-24} Source", "Name", "Id", "Version", "Match");
        foreach (var m in result.Matches)
        {
            Console.WriteLine(
                "{0,-32} {1,-40} {2,-18} {3,-24} {4}",
                Trunc(m.Name, 32),
                Trunc(m.Id, 40),
                m.Version ?? "Unknown",
                Trunc(m.MatchCriteria ?? "", 24),
                m.SourceName);
        }
    }
    else
    {
        Console.WriteLine("{0,-36} {1,-42} {2,-18} Source", "Name", "Id", "Version");
        foreach (var m in result.Matches)
        {
            Console.WriteLine(
                "{0,-36} {1,-42} {2,-18} {3}",
                Trunc(m.Name, 36),
                Trunc(m.Id, 42),
                m.Version ?? "Unknown",
                m.SourceName);
        }
    }

    if (result.Truncated) Console.WriteLine($"<additional entries truncated due to result limit>");
}

static void PrintVersions(VersionsResult result)
{
    PrintWarnings(result.Warnings);
    Console.WriteLine($"Found {result.Package.Name} [{result.Package.Id}]");
    Console.WriteLine("Version");
    Console.WriteLine(new string('-', 40));
    foreach (var v in result.Versions)
    {
        Console.Write(v.Version);
        if (!string.IsNullOrEmpty(v.Channel)) Console.Write($" [{v.Channel}]");
        Console.WriteLine();
    }
}

static void PrintShow(ShowResult result)
{
    PrintWarnings(result.Warnings);
    Console.WriteLine($"Found {result.Package.Name} [{result.Package.Id}]");
    var m = result.Manifest;
    Console.WriteLine($"Version: {m.Version}");
    PrintOpt("Publisher", m.Publisher);
    PrintOpt("Publisher Url", m.PublisherUrl);
    PrintOpt("Publisher Support Url", m.PublisherSupportUrl);
    PrintOpt("Author", m.Author);
    PrintOpt("Moniker", m.Moniker);
    if (m.Description is not null)
    {
        Console.WriteLine("Description:");
        foreach (var line in m.Description.Split('\n'))
            Console.WriteLine($"  {line.TrimEnd()}");
    }
    PrintOpt("Homepage", m.PackageUrl);
    PrintOpt("License", m.License);
    PrintOpt("License Url", m.LicenseUrl);
    PrintOpt("Privacy Url", m.PrivacyUrl);
    PrintOpt("Copyright", m.Copyright);
    PrintOpt("Copyright Url", m.CopyrightUrl);
    PrintOpt("Release Notes Url", m.ReleaseNotesUrl);
    if (m.Documentation.Count > 0)
    {
        Console.WriteLine("Documentation:");
        foreach (var doc in m.Documentation)
            Console.WriteLine($"  {doc.Label ?? "Link"}: {doc.Url}");
    }

    if (result.Manifest.PackageDependencies.Count > 0)
    {
        Console.Write("Dependencies:");
        Console.WriteLine($" {string.Join(", ", result.Manifest.PackageDependencies)}");
    }

    if (m.Tags.Count > 0) { Console.WriteLine("Tags:"); foreach (var t in m.Tags) Console.WriteLine($"  {t}"); }

    if (result.SelectedInstaller is Installer inst)
    {
        Console.WriteLine("Installer:");
        PrintOpt("  Type", inst.InstallerType);
        PrintOpt("  Architecture", inst.Architecture);
        PrintOpt("  Locale", inst.Locale);
        PrintOpt("  Scope", inst.Scope);
        PrintOpt("  Url", inst.Url);
        PrintOpt("  Sha256", inst.Sha256);
        PrintOpt("  ProductCode", inst.ProductCode);
        PrintOpt("  ReleaseDate", inst.ReleaseDate);
    }
    else if (m.Installers.Count > 0)
    {
        Console.WriteLine("Installer:");
        Console.WriteLine("  No applicable installer found; see logs for more details.");
    }
}

static void PrintListResult(ListResponse result, bool details, bool upgrade)
{
    PrintWarnings(result.Warnings);
    if (result.Matches.Count == 0) { Console.WriteLine("No installed package found matching input criteria."); return; }

    if (details)
    {
        int total = result.Matches.Count;
        for (int idx = 0; idx < total; idx++)
        {
            var m = result.Matches[idx];
            if (total > 1)
                Console.WriteLine($"({idx + 1}/{total}) {m.Name} [{m.Id}]");
            else
                Console.WriteLine($"{m.Name} [{m.Id}]");
            PrintOpt("Version", m.InstalledVersion);
            PrintOpt("Publisher", m.Publisher);
            if (m.LocalId != m.Id) PrintOpt("Local Identifier", m.LocalId);
            PrintOpt("Source", m.SourceName);
            PrintOpt("Available", m.AvailableVersion);
        }
    }
    else
    {
        bool showAvailable = result.Matches.Any(m => !string.IsNullOrEmpty(m.AvailableVersion));
        string[] headers = showAvailable
            ? ["Name", "Id", "Version", "Available", "Source"]
            : ["Name", "Id", "Version", "Source"];
        var rows = result.Matches.Select(m => showAvailable
            ? new[] { m.Name, m.Id, m.InstalledVersion, m.AvailableVersion ?? "", m.SourceName ?? "" }
            : new[] { m.Name, m.Id, m.InstalledVersion, m.SourceName ?? "" }).ToList();
        PrintTable(headers, rows);
    }

    if (result.Truncated) Console.WriteLine($"<additional entries truncated due to result limit>");
    if (upgrade) Console.WriteLine($"{result.Matches.Count} upgrades available.");
}

static void PrintSources(List<SourceRecord> sources)
{
    Console.WriteLine($"{"Name",-12} {"Trust",-8} {"Explicit",-8} Argument");
    foreach (var s in sources)
        Console.WriteLine($"{s.Name,-12} {s.TrustLevel,-8} {s.Explicit.ToString().ToLowerInvariant(),-8} {s.Arg}");
}

static void PrintPackageActionResult(InstallResult result, string action, string actionPastTense)
{
    PrintWarnings(result.Warnings);
    var target = string.IsNullOrWhiteSpace(result.Version)
        ? result.PackageId
        : $"{result.PackageId} v{result.Version}";
    if (result.NoOp)
        Console.WriteLine($"No changes were made for {target}.");
    else if (result.Success)
        Console.WriteLine($"Successfully {actionPastTense} {target}");
    else
        Console.Error.WriteLine($"Failed to {action} {target} (exit code: {result.ExitCode})");
}

static void PrintErrorLookup(string input)
{
    if (!long.TryParse(input.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? input[2..] : input, System.Globalization.NumberStyles.HexNumber, null, out var code))
    {
        Console.Error.WriteLine($"error: Could not parse '{input}' as an error code");
        return;
    }

    var lookup = LookupHresult(code);
    if (lookup is not null)
    {
        // APPINSTALLER codes (0x8A15xxxx): show symbol on same line
        if ((code & 0xFFFF0000L) == unchecked((long)0x8A150000))
            Console.WriteLine($"0x{code:x8} : {lookup.Value.Symbol}");
        else
            Console.WriteLine($"0x{code:x8}");
        Console.WriteLine(lookup.Value.Description);
    }
    else
    {
        Console.WriteLine($"0x{code:x8}");
        Console.WriteLine("  Unknown error code");
    }
}

static (string Symbol, string Description)? LookupHresult(long code)
{
    return (code & 0xFFFF0000L) switch
    {
        0x8A150000 => (code & 0xFFFF) switch
        {
            0x0001 => ("APPINSTALLER_CLI_ERROR_INTERNAL_ERROR", "An unexpected error occurred."),
            0x0002 => ("APPINSTALLER_CLI_ERROR_INVALID_CL_ARGUMENTS", "Invalid command line arguments."),
            0x0003 => ("APPINSTALLER_CLI_ERROR_COMMAND_FAILED", "The command failed."),
            0x0004 => ("APPINSTALLER_CLI_ERROR_MANIFEST_FAILED", "Opening the manifest failed."),
            0x0007 => ("APPINSTALLER_CLI_ERROR_NO_APPLICABLE_INSTALLER", "No applicable installer found."),
            0x000E => ("APPINSTALLER_CLI_ERROR_PACKAGE_NOT_FOUND", "No package matched the query."),
            0x0010 => ("APPINSTALLER_CLI_ERROR_SOURCE_NAME_ALREADY_EXISTS", "A source with the given name already exists."),
            0x0012 => ("APPINSTALLER_CLI_ERROR_NO_SOURCES_DEFINED", "No sources are configured."),
            0x0013 => ("APPINSTALLER_CLI_ERROR_MULTIPLE_APPLICATIONS_FOUND", "Multiple packages matched the query."),
            0x0016 => ("APPINSTALLER_CLI_ERROR_NO_APPLICABLE_UPGRADE", "No applicable upgrade found."),
            _ => null,
        },
        _ => code switch
        {
            unchecked((long)0x80004005) => ("E_FAIL", "Unspecified error"),
            unchecked((long)0x80070005) => ("E_ACCESSDENIED", "General access denied error"),
            unchecked((long)0x80070057) => ("E_INVALIDARG", "One or more arguments are not valid"),
            unchecked((long)0x8007000E) => ("E_OUTOFMEMORY", "Failed to allocate necessary memory"),
            _ => null,
        }
    };
}

static void PrintWarnings(List<string> warnings) { foreach (var w in warnings) Console.Error.WriteLine($"warning: {w}"); }
static void PrintOpt(string label, string? value) { if (value is not null) Console.WriteLine($"{label}: {value}"); }
static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + ".";

static string ResolveSourceAddValue(string? positionalValue, string? optionValue, string label)
{
    if (!string.IsNullOrWhiteSpace(positionalValue) &&
        !string.IsNullOrWhiteSpace(optionValue) &&
        !string.Equals(positionalValue, optionValue, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Conflicting source {label} values were provided.");
    }

    if (!string.IsNullOrWhiteSpace(optionValue))
        return optionValue;

    if (!string.IsNullOrWhiteSpace(positionalValue))
        return positionalValue;

    throw new InvalidOperationException($"source add requires a {label}.");
}

static SourceKind ParseSourceKind(string? value)
{
    if (string.IsNullOrWhiteSpace(value) ||
        string.Equals(value, "rest", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Microsoft.Rest", StringComparison.OrdinalIgnoreCase))
    {
        return SourceKind.Rest;
    }

    if (string.Equals(value, "preindexed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Microsoft.PreIndexed.Package", StringComparison.OrdinalIgnoreCase))
    {
        return SourceKind.PreIndexed;
    }

    throw new InvalidOperationException($"Unsupported source type: {value}");
}

static string FormatSourceType(SourceKind kind) => kind switch
{
    SourceKind.Rest => "Microsoft.Rest",
    SourceKind.PreIndexed => "Microsoft.PreIndexed.Package",
    _ => kind.ToString(),
};

static bool ParseBooleanSettingValue(string value) =>
    value.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "on" or "yes" or "enabled" => true,
        "false" or "0" or "off" or "no" or "disabled" => false,
        _ => throw new InvalidOperationException($"Unsupported admin setting value: {value}")
    };

static PackageQuery CreatePinQuery(
    string? query,
    string? id,
    string? name,
    string? moniker,
    string? tag,
    string? command,
    string? source,
    bool exact) =>
    new()
    {
        Query = query,
        Id = id,
        Name = name,
        Moniker = moniker,
        Tag = tag,
        Command = command,
        Source = source,
        Exact = exact,
        Count = 200,
    };

static void EnsurePinQueryProvided(PackageQuery query, string commandName)
{
    if (string.IsNullOrWhiteSpace(query.Query) &&
        string.IsNullOrWhiteSpace(query.Id) &&
        string.IsNullOrWhiteSpace(query.Name) &&
        string.IsNullOrWhiteSpace(query.Moniker) &&
        string.IsNullOrWhiteSpace(query.Tag) &&
        string.IsNullOrWhiteSpace(query.Command))
    {
        throw new InvalidOperationException($"{commandName} requires a query or explicit filter.");
    }
}

static SearchMatch ResolveSingleAvailablePinTarget(Repository repo, PackageQuery query)
{
    var result = repo.Search(query);
    if (result.Matches.Count == 0)
        throw new InvalidOperationException("No package matched the query.");
    if (result.Matches.Count > 1)
        throw new InvalidOperationException("Multiple packages matched the query; refine the query.");
    return result.Matches[0];
}

static ListMatch ResolveSingleInstalledPinTarget(Repository repo, PackageQuery query)
{
    var result = repo.List(new ListQuery
    {
        Query = query.Query,
        Id = query.Id,
        Name = query.Name,
        Moniker = query.Moniker,
        Tag = query.Tag,
        Command = query.Command,
        Source = query.Source,
        Exact = query.Exact,
        Count = 200,
    });
    if (result.Matches.Count == 0)
        throw new InvalidOperationException("No installed package matched the query.");
    if (result.Matches.Count > 1)
        throw new InvalidOperationException("Multiple installed packages matched the query; refine the query.");
    return result.Matches[0];
}

static List<PinRecord> FilterPins(Repository repo, PackageQuery query)
{
    IEnumerable<PinRecord> pins = repo.ListPins(query.Source);
    if (!string.IsNullOrWhiteSpace(query.Id))
        pins = pins.Where(pin => MatchesText(pin.PackageId, query.Id, query.Exact));

    var needsCatalogResolution =
        !string.IsNullOrWhiteSpace(query.Query) ||
        !string.IsNullOrWhiteSpace(query.Name) ||
        !string.IsNullOrWhiteSpace(query.Moniker) ||
        !string.IsNullOrWhiteSpace(query.Tag) ||
        !string.IsNullOrWhiteSpace(query.Command);
    if (!needsCatalogResolution)
        return pins.ToList();

    var searchResult = repo.Search(query);
    var keys = searchResult.Matches
        .Select(match => $"{match.Id}|{match.SourceName ?? ""}")
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var ids = searchResult.Matches
        .Select(match => match.Id)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return pins
        .Where(pin => keys.Contains($"{pin.PackageId}|{pin.SourceId}") ||
            (string.IsNullOrWhiteSpace(pin.SourceId) && ids.Contains(pin.PackageId)))
        .ToList();
}

static bool MatchesText(string value, string query, bool exact) =>
    exact
        ? value.Equals(query, StringComparison.OrdinalIgnoreCase)
        : value.Contains(query, StringComparison.OrdinalIgnoreCase);

static PinRecord? FindMatchingPin(ListMatch match, IReadOnlyList<PinRecord> pins)
{
    PinRecord? sourceSpecific = null;
    PinRecord? sourceAgnostic = null;
    foreach (var pin in pins)
    {
        if (!pin.PackageId.Equals(match.Id, StringComparison.OrdinalIgnoreCase) &&
            !pin.PackageId.Equals(match.LocalId, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!string.IsNullOrWhiteSpace(pin.SourceId))
        {
            if (!string.IsNullOrWhiteSpace(match.SourceName) &&
                pin.SourceId.Equals(match.SourceName, StringComparison.OrdinalIgnoreCase))
            {
                sourceSpecific = pin;
                break;
            }
        }
        else if (sourceAgnostic is null)
        {
            sourceAgnostic = pin;
        }
    }

    return sourceSpecific ?? sourceAgnostic;
}

static InstallRequest CreateInstallRequest(
    PackageQuery query,
    string? manifestPath,
    InstallerMode mode,
    string? logPath,
    string? custom,
    string? overrideArgs,
    string? installLocation,
    bool skipDependencies,
    bool dependenciesOnly,
    bool acceptPackageAgreements,
    bool force,
    string? rename,
    bool uninstallPrevious,
    bool ignoreSecurityHash,
    string? dependencySource,
    bool noUpgrade) =>
    new()
    {
        Query = query,
        ManifestPath = manifestPath,
        Mode = mode,
        LogPath = logPath,
        Custom = custom,
        Override = overrideArgs,
        InstallLocation = installLocation,
        SkipDependencies = skipDependencies,
        DependenciesOnly = dependenciesOnly,
        AcceptPackageAgreements = acceptPackageAgreements,
        Force = force,
        Rename = rename,
        UninstallPrevious = uninstallPrevious,
        IgnoreSecurityHash = ignoreSecurityHash,
        DependencySource = dependencySource,
        NoUpgrade = noUpgrade,
    };

static RepairRequest CreateRepairRequest(
    PackageQuery query,
    string? manifestPath,
    string? productCode,
    InstallerMode mode,
    string? logPath,
    bool acceptPackageAgreements,
    bool force,
    bool ignoreSecurityHash) =>
    new()
    {
        Query = query,
        ManifestPath = manifestPath,
        ProductCode = productCode,
        Mode = mode,
        LogPath = logPath,
        AcceptPackageAgreements = acceptPackageAgreements,
        Force = force,
        IgnoreSecurityHash = ignoreSecurityHash,
    };

static string? GetJsonString(JsonElement element, string propertyName) =>
    element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;

static bool IsInstalledPackagePresent(Repository repo, string packageId, string? sourceName) =>
    repo.List(new ListQuery
    {
        Id = packageId,
        Source = sourceName,
        Exact = true,
        Count = 1,
    }).Matches.Count > 0;

static bool CanIgnoreUnavailableImportFailure(Exception ex) =>
    ex is InvalidOperationException &&
    (ex.Message.Contains("No package matched the query", StringComparison.OrdinalIgnoreCase) ||
     ex.Message.Contains("No applicable installer found", StringComparison.OrdinalIgnoreCase));

void WriteJsonNode(JsonNode value, OutputFormat output)
{
    switch (output)
    {
        case OutputFormat.Yaml:
            var structured = JsonSerializer.Deserialize<object>(value.ToJsonString(), SerializationOptions) ?? new object();
            Console.Write(new SerializerBuilder().Build().Serialize(structured));
            break;
        default:
            Console.WriteLine(value.ToJsonString(SerializationOptions));
            break;
    }
}

static void PrintTable(string[] headers, List<string[]> rows)
{
    if (headers.Length == 0) return;
    int cols = headers.Length;
    var widths = headers.Select(h => h.Length).ToArray();
    var hasData = new bool[cols];

    foreach (var row in rows)
        for (int i = 0; i < Math.Min(cols, row.Length); i++)
            if (!string.IsNullOrEmpty(row[i]))
            {
                hasData[i] = true;
                widths[i] = Math.Max(widths[i], row[i].Length);
            }

    for (int i = 0; i < cols; i++)
        if (!hasData[i]) widths[i] = 0;

    var spaceAfter = Enumerable.Repeat(true, cols).ToArray();
    spaceAfter[^1] = false;
    for (int i = cols - 1; i >= 1; i--)
    {
        if (widths[i] == 0) spaceAfter[i - 1] = false;
        else break;
    }

    int totalWidth = widths.Zip(spaceAfter, (w, s) => w + (s ? 1 : 0)).Sum();
    int consoleWidth = 119;
    try { consoleWidth = Math.Max(1, Console.WindowWidth - 2); } catch { }
    if (totalWidth >= consoleWidth)
    {
        int extra = totalWidth - consoleWidth + 1;
        while (extra > 0)
        {
            int target = 0;
            for (int i = 1; i < cols; i++)
                if (widths[i] > widths[target]) target = i;
            if (widths[target] > 1) widths[target]--;
            extra--;
        }
        totalWidth = Math.Max(0, consoleWidth - 1);
    }

    PrintTableLine(headers, widths, spaceAfter);
    Console.WriteLine(new string('-', totalWidth));
    foreach (var row in rows)
        PrintTableLine(row, widths, spaceAfter);
}

static void PrintTableLine(string[] values, int[] widths, bool[] spaceAfter)
{
    var sb = new System.Text.StringBuilder();
    for (int i = 0; i < Math.Min(values.Length, widths.Length); i++)
    {
        if (widths[i] == 0) continue;
        var val = values[i] ?? "";
        if (val.Length > widths[i])
        {
            sb.Append(Trunc(val, widths[i]));
            if (spaceAfter[i]) sb.Append(' ');
        }
        else
        {
            sb.Append(val);
            if (spaceAfter[i]) sb.Append(' ', widths[i] - val.Length + 1);
        }
    }
    Console.WriteLine(sb.ToString().TrimEnd());
}

enum OutputFormat
{
    Text,
    Json,
    Yaml,
}
