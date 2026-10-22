using System.Buffers.Binary;
using System.Net;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.CargoManager;

internal static class CargoManagedInstaller
{
    internal static Task<OperationVeredict?> TryPerformAsync(
        Cargo manager, IPackage package, InstallOptions options, OperationType operation,
        Action<string> log, Action<OperationProgress> progress, CancellationToken cancellationToken)
        => TryPerformAsync(manager, package, options, operation, log, progress,
            CoreTools.IsAdministrator(), cancellationToken);

    internal static async Task<OperationVeredict?> TryPerformAsync(
        Cargo manager, IPackage package, InstallOptions options, OperationType operation,
        Action<string> log, Action<OperationProgress> progress, bool processIsElevated,
        CancellationToken cancellationToken)
    {
        if (operation == OperationType.Uninstall)
            return null;
        if (options.SkipHashCheck)
            throw new InvalidOperationException("Cargo installation requires integrity verification.");
        if (processIsElevated || options.RunAsAdministrator
            || package.OverridenOptions.RunAsAdministrator is true)
        {
            log("Elevated Cargo operations use Cargo source compilation.");
            return null;
        }
        if (package.Source.Name != manager.DefaultSource.Name)
        {
            log("Non-crates.io packages use their original source through Cargo.");
            return null;
        }
        var custom = operation == OperationType.Update
            ? options.CustomParameters_Update : options.CustomParameters_Install;
        if (custom.Count > 0 || CargoConfiguration.HasBuildOverrides()
            || manager.Status.ExecutableCallArgs.Length > 0
            || manager.Status.OperationCallArgs.Count > 0)
        {
            log("Custom Cargo arguments/build settings require source compilation.");
            return null;
        }
        string root = CargoConfiguration.GetRoot(options.CustomInstallLocation);
        try
        {
            CargoConfiguration.RequireRegularPath(root);
            CargoConfiguration.RequireRegularPath(Path.Join(root, "bin"));
        }
        catch (InvalidDataException ex)
        {
            log($"Linked Cargo installation paths use source compilation: {ex.Message}");
            return null;
        }
        await CargoInstallTracker.RecoverAsync(root, cancellationToken);
        string? target = await manager.GetHostTargetAsync(cancellationToken);
        if (target is null)
        {
            log("Cargo did not report a supported host target; using source compilation.");
            return null;
        }
        CargoManifest manifest = await CratesIOClient.GetManifestAsync(package.Id, cancellationToken);
        string version = CargoPkgOperationHelper.RequestedVersion(package, options, operation);
        if (version.Length == 0)
        {
            CargoManifestVersion? latest = manifest.versions
                .Where(v => !v.yanked && SemanticVersion.TryParse(v.num, out var parsed)
                    && (options.PreRelease || !parsed.IsPreRelease))
                .OrderByDescending(v =>
                {
                    SemanticVersion.TryParse(v.num, out var parsed);
                    return parsed;
                }).FirstOrDefault();
            version = latest?.num ?? throw new InvalidDataException("No installable Cargo package version was found.");
        }
        if (!manifest.versions.Any(v => v.num == version && !v.yanked))
        {
            log("The requested Cargo version requires Cargo's own version resolution.");
            return null;
        }
        options.Version = version;
        if (operation == OperationType.Update)
        {
            var preserved = await CargoInstallTracker.GetBuildArgumentsAsync(root, package.Id,
                cancellationToken, target, manifest.versions.Single(v => v.num == version).bin_names);
            if (preserved.Count > 0)
            {
                options.CustomParameters_Update.AddRange(preserved);
                log("Preserving the installed package's Cargo build settings through source compilation.");
                return null;
            }
        }
        using CargoHttp http = new();
        CargoBinaryPlan? plan;
        try
        {
            plan = await new CargoBinaryResolver(http).ResolveAsync(
                package.Id, version, target, manifest, log, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null or HttpStatusCode.NotFound
            or HttpStatusCode.Gone or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
        {
            log($"Publisher binaries are unavailable ({ex.Message}); building {package.Id} {version} with Cargo.");
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            log($"Publisher resolution timed out; building {package.Id} {version} with Cargo.");
            return null;
        }
        if (plan is null)
            return null;
        log($"Downloading publisher binary {plan.Url} for Cargo package {plan.Id} {plan.Version}.");
        byte[]? archive = await http.GetAsync(plan.Url, CargoHttp.ArchiveLimit, true, cancellationToken,
            (downloaded, total) => progress(OperationProgress.FromDownload(downloaded, total)));
        if (archive is null)
        {
            log($"The selected publisher binary is no longer available; building {package.Id} {version} with Cargo.");
            return null;
        }
        CargoBinaryResolver.VerifyHash(archive, plan.Hash);
        var binaries = ExtractBinaries(archive, plan, cancellationToken);
        progress(OperationProgress.ForStage(operation == OperationType.Update
            ? OperationProgressStage.Updating : OperationProgressStage.Installing));
        await CargoInstallTracker.InstallAsync(root, plan, binaries, cancellationToken);
        manager.InvalidateInstalledCache();
        return OperationVeredict.Success;
    }

    internal static IReadOnlyDictionary<string, byte[]> ExtractBinaries(byte[] archive,
        CargoBinaryPlan plan, CancellationToken cancellationToken)
    {
        bool windows = plan.Target.Contains("-windows-", StringComparison.Ordinal);
        string[] names = plan.Binaries.Select(name => name + (windows ? ".exe" : "")).ToArray();
        Dictionary<string, byte[]> files;
        if (plan.Format == "bin")
        {
            if (names.Length != 1)
                throw new InvalidDataException("A raw Cargo binary cannot contain multiple executables.");
            files = new() { [names[0]] = archive };
        }
        else
            files = CargoArchive.Read(archive, plan.Format,
                name => names.Contains(name.Split('/')[^1], StringComparer.Ordinal), cancellationToken);
        Dictionary<string, byte[]> result = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            KeyValuePair<string, byte[]>[] matches;
            if (plan.BinaryPath is not null && plan.Format != "bin")
            {
                string bin = windows ? name[..^4] : name;
                string path = CargoBinaryResolver.Expand(plan.BinaryPath, plan.Id, plan.Version,
                    plan.Target, null, plan.Format, bin)
                    ?? throw new InvalidDataException("Unsupported publisher binary-path template.");
                CargoArchive.ValidateName(path);
                matches = files.Where(file => file.Key == path).ToArray();
            }
            else
                matches = files.Where(file => file.Key.Split('/')[^1] == name).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException($"The publisher archive does not contain one unambiguous {name} executable.");
            ValidateExecutable(matches[0].Value, plan.Target);
            result.Add(name, matches[0].Value);
        }
        return result;
    }

    internal static void ValidateExecutable(byte[] bytes, string target)
    {
        if (bytes.Length < 64)
            throw new InvalidDataException("The publisher executable is truncated.");
        bool arm = target.StartsWith("aarch64-", StringComparison.Ordinal);
        bool valid;
        if (target.Contains("-windows-", StringComparison.Ordinal))
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
            valid = bytes[0] == 'M' && bytes[1] == 'Z' && offset >= 64 && offset <= bytes.Length - 6
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)) == 0x4550
                && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 4)) == (arm ? 0xAA64 : 0x8664);
        }
        else if (target.Contains("-linux-", StringComparison.Ordinal))
            valid = bytes[0] == 0x7F && bytes[1] == 'E' && bytes[2] == 'L' && bytes[3] == 'F'
                && bytes[4] == 2 && bytes[5] == 1
                && BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18)) == (arm ? 183 : 62);
        else
            valid = BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0xFEEDFACF
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) == (arm ? 0x100000C : 0x1000007);
        if (!valid)
            throw new InvalidDataException($"The downloaded executable does not match Cargo target {target}.");
    }
}
