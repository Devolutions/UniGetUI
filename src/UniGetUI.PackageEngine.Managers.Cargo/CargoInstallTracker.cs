using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tomlyn;
using Tomlyn.Model;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.Managers.CargoManager;

internal sealed record CargoTransactionEntry(string Name, bool Existed, int? UnixMode = null, string? InstalledHash = null);
internal sealed record CargoTransactionJournal(CargoTransactionEntry[] Entries, bool Committed,
    string? V1Hash = null, string? V2Hash = null);

internal static class CargoInstallTracker
{
    private const string TransactionDirectory = ".unigetui-cargo-transaction";

    internal static async Task<IReadOnlyList<string>> GetBuildArgumentsAsync(
        string root, string id, CancellationToken cancellationToken, string? hostTarget = null,
        string[]? expectedBinaries = null)
    {
        if (!File.Exists(Path.Join(root, ".crates2.json")))
            return [];
        using var metadata = await CargoTrackingLock.AcquireAsync(root, cancellationToken);
        Recover(root, metadata);
        byte[] bytes = Read(metadata.V2);
        if (bytes.Length == 0)
            return [];
        var document = JsonNode.Parse(bytes) as JsonObject
            ?? throw new InvalidDataException("Invalid Cargo installation metadata.");
        if (document["installs"] is not JsonObject installs)
            throw new InvalidDataException("Invalid Cargo installation map.");
        var entries = installs.Where(entry => entry.Key.StartsWith(id + " ", StringComparison.Ordinal)).ToArray();
        if (entries.Length == 0)
            return [];
        if (entries.Length > 1 || entries[0].Value is not JsonObject info)
            throw new InvalidDataException($"Cargo package {id} has ambiguous installed build settings.");
        List<string> arguments = [];
        if (info["features"] is JsonArray features && features.Count > 0)
        {
            string[] values = features.Select(feature => feature?.GetValue<string>()
                ?? throw new InvalidDataException("Invalid installed Cargo feature.")).ToArray();
            arguments.AddRange(["--features", UniGetUI.Core.Tools.CoreTools.EscapeCommandLineArgument(string.Join(',', values))]);
        }
        if (info["all_features"]?.GetValue<bool>() is true)
            arguments.Add("--all-features");
        if (info["no_default_features"]?.GetValue<bool>() is true)
            arguments.Add("--no-default-features");
        if (info["profile"]?.GetValue<string>() is { } profile && profile != "release")
            arguments.AddRange(["--profile", UniGetUI.Core.Tools.CoreTools.EscapeCommandLineArgument(profile)]);
        // An explicitly installed cross target cannot be inferred from the process architecture.
        if (info["target"]?.GetValue<string>() is { Length: > 0 } target
            && target != hostTarget)
            arguments.AddRange(["--target", UniGetUI.Core.Tools.CoreTools.EscapeCommandLineArgument(target)]);
        if (info["bins"] is JsonArray bins)
        {
            bool windows = info["target"]?.GetValue<string>()?.Contains("-windows-", StringComparison.Ordinal)
                ?? OperatingSystem.IsWindows();
            string[] names = bins.Select(bin => bin?.GetValue<string>()
                ?? throw new InvalidDataException("Invalid installed Cargo binary name."))
                .Select(bin => windows && bin.EndsWith(".exe", StringComparison.Ordinal) ? bin[..^4] : bin)
                .ToArray();
            if (expectedBinaries is not null
                && !names.Order(StringComparer.Ordinal).SequenceEqual(expectedBinaries.Order(StringComparer.Ordinal)))
            {
                foreach (string name in names)
                {
                    CargoBinaryResolver.ValidateBinaryName(name);
                    arguments.AddRange(["--bin", UniGetUI.Core.Tools.CoreTools.EscapeCommandLineArgument(name)]);
                }
            }
        }
        return arguments;
    }

    internal static async Task RecoverAsync(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Join(root, TransactionDirectory)))
            return;
        using var metadata = await CargoTrackingLock.AcquireAsync(root, cancellationToken);
        Recover(root, metadata);
    }

    internal static async Task InstallAsync(string root, CargoBinaryPlan plan,
        IReadOnlyDictionary<string, byte[]> binaries, CancellationToken cancellationToken)
    {
        if (binaries.Count == 0)
            throw new InvalidDataException("A Cargo binary installation must contain executables.");
        using var metadata = await CargoTrackingLock.AcquireAsync(root, cancellationToken);
        Recover(root, metadata);
        string binDirectory = Path.Join(root, "bin");
        CargoConfiguration.RequireRegularPath(binDirectory);
        Directory.CreateDirectory(binDirectory);
        byte[] oldV1 = Read(metadata.V1);
        byte[] oldV2 = Read(metadata.V2);
        TomlTable v1Document = oldV1.Length == 0 ? new() : CargoConfiguration.Parse(Encoding.UTF8.GetString(oldV1));
        TomlTable v1 = CargoConfiguration.Table(v1Document, "v1") ?? new TomlTable();
        if (v1Document.ContainsKey("v1") && v1Document["v1"] is not TomlTable)
            throw new InvalidDataException("Invalid Cargo v1 installation metadata.");
        v1Document["v1"] = v1;
        JsonObject v2Document = oldV2.Length == 0 ? new() :
            JsonNode.Parse(oldV2) as JsonObject
            ?? throw new InvalidDataException("Invalid Cargo v2 installation metadata.");
        JsonObject installs = v2Document["installs"] as JsonObject ?? new();
        if (v2Document.ContainsKey("installs") && v2Document["installs"] is not JsonObject)
            throw new InvalidDataException("Invalid Cargo v2 installation map.");
        if (!v2Document.ContainsKey("installs"))
            v2Document["installs"] = installs;
        string source = "(registry+https://github.com/rust-lang/crates.io-index)";
        string key = $"{plan.Id} {plan.Version} {source}";
        string[] previous = v1.Keys.Where(k => k.StartsWith(plan.Id + " ", StringComparison.Ordinal)
            && k.EndsWith(source, StringComparison.Ordinal)).ToArray();
        HashSet<string> oldBinaries = [];
        JsonObject installedInfo = previous.Select(oldKey => installs[oldKey])
            .OfType<JsonObject>().FirstOrDefault()?.DeepClone() as JsonObject ?? new();
        foreach (string oldKey in previous)
            oldBinaries.UnionWith(ReadBins(v1[oldKey]));
        foreach (string binary in binaries.Keys)
        {
            CargoBinaryResolver.ValidateBinaryName(binary);
            CargoConfiguration.RequireRegularPath(Path.Join(binDirectory, binary));
            foreach (var package in v1)
            {
                if (!previous.Contains(package.Key, StringComparer.Ordinal)
                    && ReadBins(package.Value).Contains(binary, StringComparer.OrdinalIgnoreCase))
                    throw new IOException($"The executable {binary} is owned by another Cargo package.");
            }
            if (File.Exists(Path.Join(binDirectory, binary)) && !oldBinaries.Contains(binary))
                throw new IOException($"Refusing to overwrite untracked executable {binary}.");
        }
        string[] affected = oldBinaries.Union(binaries.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (string binary in affected)
        {
            CargoBinaryResolver.ValidateBinaryName(binary);
            CargoConfiguration.RequireRegularPath(Path.Join(binDirectory, binary));
        }
        foreach (string oldKey in previous)
        {
            v1.Remove(oldKey);
            installs.Remove(oldKey);
        }
        TomlArray trackedBins = new();
        foreach (string name in binaries.Keys)
            trackedBins.Add(name);
        v1[key] = trackedBins;
        JsonArray installedBins = new();
        foreach (string name in binaries.Keys)
        {
            JsonNode? node = JsonValue.Create(name);
            installedBins.Add(node);
        }
        installedInfo["version_req"] = "=" + plan.Version;
        installedInfo["bins"] = installedBins;
        installedInfo["features"] = new JsonArray();
        installedInfo["all_features"] = false;
        installedInfo["no_default_features"] = false;
        installedInfo["profile"] = "release";
        installedInfo["target"] = plan.Target;
        installedInfo["rustc"] = null;
        installs[key] = installedInfo;
        byte[] newV1 = Encoding.UTF8.GetBytes(TomlSerializer.Serialize(v1Document, CargoTomlContext.Default.TomlTable));
        byte[] newV2 = CargoJson.SerializeToUtf8Bytes(v2Document);
        if (newV1.Length > CargoHttp.ManifestLimit || newV2.Length > CargoHttp.ManifestLimit)
            throw new InvalidDataException("Updated Cargo tracking metadata exceeds the allowed size.");
        string transaction = Path.Join(root, TransactionDirectory);
        CargoConfiguration.RequireRegularPath(transaction);
        Directory.CreateDirectory(transaction);
        CargoTransactionJournal journal = new(
            affected.Select(name =>
            {
                string path = Path.Join(binDirectory, name);
                bool exists = File.Exists(path);
                return new CargoTransactionEntry(name, exists,
                    exists && !OperatingSystem.IsWindows() ? (int)File.GetUnixFileMode(path) : null,
                    binaries.TryGetValue(name, out byte[]? content) ? Hash(content) : null);
            }).ToArray(),
            false, Hash(newV1), Hash(newV2));
        // The journal is durable before the first installed binary or tracking file changes.
        try
        {
            WriteDurable(Path.Join(transaction, "metadata-v1"), oldV1);
            WriteDurable(Path.Join(transaction, "metadata-v2"), oldV2);
            for (int i = 0; i < journal.Entries.Length; i++)
            {
                var entry = journal.Entries[i];
                if (entry.Existed)
                    WriteDurable(Path.Join(transaction, $"backup-{i}"), File.ReadAllBytes(Path.Join(binDirectory, entry.Name)));
            }
            WriteJournal(transaction, journal);
        }
        catch
        {
            Cleanup(transaction);
            throw;
        }
        try
        {
            foreach (string name in affected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = Path.Join(binDirectory, name);
                CargoConfiguration.RequireRegularPath(destination);
                if (binaries.TryGetValue(name, out byte[]? content))
                {
                    string staged = Path.Join(transaction, "staged-binary");
                    WriteDurable(staged, content);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite
                            | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    File.Move(staged, destination, overwrite: true);
                }
                else if (File.Exists(destination))
                    File.Delete(destination);
            }
            cancellationToken.ThrowIfCancellationRequested();
            WriteLocked(metadata.V1, newV1);
            WriteLocked(metadata.V2, newV2);
            WriteJournal(transaction, journal with { Committed = true });
        }
        catch (Exception operationError)
        {
            try { Recover(root, metadata); }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Cargo installation failed and could not be rolled back.",
                    operationError, rollbackError);
            }
            throw;
        }
        Cleanup(transaction);
    }

    internal static string[] ReadBins(object? value)
    {
        if (value is not TomlArray array || array.Any(item => item is not string))
            throw new InvalidDataException("Invalid binary list in Cargo installation metadata.");
        return array.Cast<string>().ToArray();
    }

    private static void Recover(string root, CargoTrackingLock metadata)
    {
        string transaction = Path.Join(root, TransactionDirectory);
        if (!Directory.Exists(transaction))
            return;
        CargoConfiguration.RequireRegularPath(transaction);
        if (!Directory.EnumerateFileSystemEntries(transaction).Any())
        {
            Directory.Delete(transaction);
            return;
        }
        string journalPath = Path.Join(transaction, "journal.json");
        CargoConfiguration.RequireRegularPath(journalPath);
        if (!File.Exists(journalPath))
            throw new IOException($"Incomplete Cargo transaction needs inspection before installation: {transaction}");
        var journal = CargoJson.Deserialize<CargoTransactionJournal>(File.ReadAllText(journalPath))
            ?? throw new InvalidDataException("Cargo transaction journal is empty.");
        if (!journal.Committed)
        {
            // Cargo may have acquired the locks and installed something else after a crash.
            // Never roll an interrupted transaction back over those later external changes.
            foreach (string name in new[] { "metadata-v1", "metadata-v2" })
                CargoConfiguration.RequireRegularPath(Path.Join(transaction, name));
            byte[] originalV1 = File.ReadAllBytes(Path.Join(transaction, "metadata-v1"));
            byte[] originalV2 = File.ReadAllBytes(Path.Join(transaction, "metadata-v2"));
            if (!MatchesTransaction(Read(metadata.V1), originalV1, journal.V1Hash)
                || !MatchesTransaction(Read(metadata.V2), originalV2, journal.V2Hash))
                throw new IOException($"Cargo metadata changed after an interrupted transaction; inspect {transaction} before recovery.");
            string binDirectory = Path.Join(root, "bin");
            for (int i = 0; i < journal.Entries.Length; i++)
            {
                var entry = journal.Entries[i];
                CargoBinaryResolver.ValidateBinaryName(entry.Name);
                string destination = Path.Join(binDirectory, entry.Name);
                CargoConfiguration.RequireRegularPath(destination);
                if (!File.Exists(destination))
                    continue;
                string backup = Path.Join(transaction, $"backup-{i}");
                if (entry.Existed)
                    CargoConfiguration.RequireRegularPath(backup);
                byte[]? original = entry.Existed ? File.ReadAllBytes(backup) : null;
                byte[] current = File.ReadAllBytes(destination);
                if ((original is null || !current.AsSpan().SequenceEqual(original))
                    && Hash(current) != entry.InstalledHash)
                    throw new IOException($"Cargo executable {entry.Name} changed after an interrupted transaction; manual recovery is required.");
            }
            for (int i = 0; i < journal.Entries.Length; i++)
            {
                var entry = journal.Entries[i];
                CargoBinaryResolver.ValidateBinaryName(entry.Name);
                string destination = Path.Join(binDirectory, entry.Name);
                CargoConfiguration.RequireRegularPath(destination);
                if (entry.Existed)
                {
                    string backup = Path.Join(transaction, $"backup-{i}");
                    CargoConfiguration.RequireRegularPath(backup);
                    if (!File.Exists(destination)
                        || !File.ReadAllBytes(destination).AsSpan().SequenceEqual(File.ReadAllBytes(backup)))
                        File.Copy(backup, destination, overwrite: true);
                    if (!OperatingSystem.IsWindows() && entry.UnixMode is { } mode)
                        File.SetUnixFileMode(destination, (UnixFileMode)mode);
                }

                else if (File.Exists(destination))
                    File.Delete(destination);
            }
            WriteLocked(metadata.V1, originalV1);
            WriteLocked(metadata.V2, originalV2);
            WriteJournal(transaction, journal with { Committed = true });
            Logger.Warn("Rolled back an interrupted UniGetUI Cargo installation.");
        }
        Cleanup(transaction);
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static bool MatchesTransaction(byte[] current, byte[] original, string? installedHash) =>
        current.AsSpan().SequenceEqual(original) || Hash(current) == installedHash;

    private static byte[] Read(FileStream stream)
    {
        if (stream.Length > CargoHttp.ManifestLimit)
            throw new InvalidDataException("Cargo tracking metadata exceeds the allowed size.");
        stream.Position = 0;
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void WriteLocked(FileStream stream, byte[] data)
    {
        stream.Position = 0;
        stream.Write(data);
        stream.SetLength(data.Length);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteDurable(string path, byte[] data)
    {
        CargoConfiguration.RequireRegularPath(path);
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(data);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteJournal(string transaction, CargoTransactionJournal journal)
    {
        string temporary = Path.Join(transaction, "journal.tmp");
        WriteDurable(temporary, CargoJson.SerializeToUtf8Bytes(journal));
        File.Move(temporary, Path.Join(transaction, "journal.json"), overwrite: true);
    }

    private static void Cleanup(string transaction)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(transaction)
            .OrderBy(path => Path.GetFileName(path) == "journal.json" ? 1 : 0))
        {
            CargoConfiguration.RequireRegularPath(path);
            if (Directory.Exists(path))
                throw new InvalidDataException("Unexpected directory in the Cargo transaction.");
            File.Delete(path);
        }
        Directory.Delete(transaction);
    }
}
