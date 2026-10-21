using System.Collections;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Managers.CargoManager;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class CargoInstallTrackerTests
{
    private const string Transaction = "root/.unigetui-cargo-transaction";
    private const string Registry = "(registry+https://github.com/rust-lang/crates.io-index)";
    private static string Key(string version) => $"fixture-tool {version} {Registry}";
    private static string Bin(string name) => name + (OperatingSystem.IsWindows() ? ".exe" : "");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static CargoBinaryPlan Plan(string version = "1.2.3") => CargoTestData.Plan(version: version);

    private static Task Install(CargoTestSandbox sandbox, string version, params (string Name, byte[] Bytes)[] binaries) =>
        CargoInstallTracker.InstallAsync(sandbox.PathFor("root"), Plan(version),
            binaries.ToDictionary(b => Bin(b.Name), b => b.Bytes), default);

    private static JsonObject Metadata(CargoTestSandbox sandbox) =>
        JsonNode.Parse(File.ReadAllText(sandbox.PathFor("root/.crates2.json")))!.AsObject();

    private static void WriteMetadata(CargoTestSandbox sandbox, JsonObject metadata) =>
        sandbox.WriteText("root/.crates2.json", JsonSerializer.Serialize(metadata, CargoJsonContext.Default.JsonObject));

    [Fact]
    public async Task MultiBinaryInstallAndUpdatePreserveUnrelatedAndUnknownMetadata()
    {
        using var sandbox = new CargoTestSandbox();
        await Install(sandbox, "1.2.3", ("tool", [1, 2]), ("obsolete", [3]));
        sandbox.WriteText("root/.crates.toml", File.ReadAllText(sandbox.PathFor("root/.crates.toml")) + """

            "unrelated 9.0.0 (path+file:///local)" = ["unrelated"]
            [future]
            marker = "keep-toml"
            """);
        sandbox.Write("root/bin/unrelated", [99]);
        var before = Metadata(sandbox);
        before["future"] = new JsonObject { ["marker"] = "keep-json" };
        before["installs"]![Key("1.2.3")]!["publisher-field"] = "preserve-on-upgrade";
        before["installs"]!["unrelated 9.0.0 (path+file:///local)"] =
            new JsonObject { ["bins"] = new JsonArray("unrelated"), ["opaque"] = new JsonArray(7, 8) };
        WriteMetadata(sandbox, before);

        await Install(sandbox, "2.0.0", ("tool", [4, 5]), ("companion", [6, 7, 8]));
        Assert.Equal([4, 5], File.ReadAllBytes(sandbox.PathFor("root/bin/" + Bin("tool"))));
        Assert.Equal([6, 7, 8], File.ReadAllBytes(sandbox.PathFor("root/bin/" + Bin("companion"))));
        Assert.False(File.Exists(sandbox.PathFor("root/bin/" + Bin("obsolete"))));
        Assert.Equal([99], File.ReadAllBytes(sandbox.PathFor("root/bin/unrelated")));
        var v1 = CargoConfiguration.Parse(File.ReadAllText(sandbox.PathFor("root/.crates.toml")));
        Assert.Equal("keep-toml", CargoConfiguration.String(CargoConfiguration.Table(v1, "future")!, "marker"));
        var installsV1 = CargoConfiguration.Table(v1, "v1")!;
        Assert.False(installsV1.ContainsKey(Key("1.2.3")));
        Assert.Equal(new[] { Bin("tool"), Bin("companion") }, CargoInstallTracker.ReadBins(installsV1[Key("2.0.0")]));
        Assert.Equal(["unrelated"], CargoInstallTracker.ReadBins(installsV1["unrelated 9.0.0 (path+file:///local)"]));
        var after = Metadata(sandbox);
        Assert.True(JsonNode.DeepEquals(before["future"], after["future"]));
        Assert.True(JsonNode.DeepEquals(before["installs"]!["unrelated 9.0.0 (path+file:///local)"],
            after["installs"]!["unrelated 9.0.0 (path+file:///local)"]));
        Assert.Null(after["installs"]![Key("1.2.3")]);
        var info = after["installs"]![Key("2.0.0")]!;
        Assert.Equal("preserve-on-upgrade", info["publisher-field"]!.GetValue<string>());
        Assert.Equal("=2.0.0", info["version_req"]!.GetValue<string>());
        Assert.Equal("release", info["profile"]!.GetValue<string>());
        Assert.Equal(CargoTestData.Target, info["target"]!.GetValue<string>());
        Assert.Equal(new[] { Bin("tool"), Bin("companion") }, info["bins"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(Directory.Exists(sandbox.PathFor(Transaction)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UntrackedAndOtherPackageCollisionsLeaveBothMetadataFilesUntouched(bool tracked)
    {
        using var sandbox = new CargoTestSandbox();
        await Install(sandbox, "1.2.3", ("original", [1]));
        sandbox.Write("root/bin/" + Bin("collision"), [90, 91]);
        if (tracked)
        {
            sandbox.WriteText("root/.crates.toml", File.ReadAllText(sandbox.PathFor("root/.crates.toml"))
                + $"\n\"another 1.0.0 {Registry}\" = [\"{Bin("collision")}\"]\n");
            var document = Metadata(sandbox);
            document["installs"]![$"another 1.0.0 {Registry}"] =
                new JsonObject { ["bins"] = new JsonArray(Bin("collision")) };
            WriteMetadata(sandbox, document);
        }
        byte[] oldV1 = File.ReadAllBytes(sandbox.PathFor("root/.crates.toml"));
        byte[] oldV2 = File.ReadAllBytes(sandbox.PathFor("root/.crates2.json"));
        var error = await Assert.ThrowsAsync<IOException>(() =>
            Install(sandbox, "2.0.0", ("original", [2]), ("collision", [3])));
        Assert.Contains(tracked ? "owned by another" : "untracked executable", error.Message);
        Assert.Equal(oldV1, File.ReadAllBytes(sandbox.PathFor("root/.crates.toml")));
        Assert.Equal(oldV2, File.ReadAllBytes(sandbox.PathFor("root/.crates2.json")));
        Assert.Equal([1], File.ReadAllBytes(sandbox.PathFor("root/bin/" + Bin("original"))));
        Assert.Equal([90, 91], File.ReadAllBytes(sandbox.PathFor("root/bin/" + Bin("collision"))));
        Assert.False(Directory.Exists(sandbox.PathFor(Transaction)));
    }

    [Fact]
    public async Task FailureAfterFirstBinaryRollsBackAllBinariesAndBothMetadataFiles()
    {
        using var sandbox = new CargoTestSandbox();
        await Install(sandbox, "1.2.3", ("aaa", [1]), ("obsolete", [7]));
        byte[] oldV1 = File.ReadAllBytes(sandbox.PathFor("root/.crates.toml"));
        byte[] oldV2 = File.ReadAllBytes(sandbox.PathFor("root/.crates2.json"));
        // Sorted commit writes aaa first, deletes obsolete, then encounters an actual IO
        // failure moving a file onto this directory. No timing or injected production hooks.
        Directory.CreateDirectory(sandbox.PathFor("root/bin/" + Bin("zzz")));
        Exception? error = await Record.ExceptionAsync(() =>
            Install(sandbox, "2.0.0", ("aaa", [2]), ("zzz", [9])));
        if (OperatingSystem.IsWindows())
            Assert.IsType<UnauthorizedAccessException>(error);
        else
            Assert.IsAssignableFrom<IOException>(error);
        Assert.Equal([1], File.ReadAllBytes(sandbox.PathFor("root/bin/" + Bin("aaa"))));
        Assert.Equal([7], File.ReadAllBytes(sandbox.PathFor("root/bin/" + Bin("obsolete"))));
        Assert.Equal(oldV1, File.ReadAllBytes(sandbox.PathFor("root/.crates.toml")));
        Assert.Equal(oldV2, File.ReadAllBytes(sandbox.PathFor("root/.crates2.json")));
        Assert.True(Directory.Exists(sandbox.PathFor("root/bin/" + Bin("zzz"))));
        Assert.False(Directory.Exists(sandbox.PathFor(Transaction)));
    }

    [Fact]
    public async Task PreCanceledInstallDoesNotChangeExistingFilesOrMetadata()
    {
        using var sandbox = new CargoTestSandbox();
        await Install(sandbox, "1.2.3", ("tool", [1]));
        byte[] oldV1 = File.ReadAllBytes(sandbox.PathFor("root/.crates.toml"));
        byte[] oldV2 = File.ReadAllBytes(sandbox.PathFor("root/.crates2.json"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CargoInstallTracker.InstallAsync(
            sandbox.PathFor("root"), Plan("2.0.0"), new Dictionary<string, byte[]> { [Bin("tool")] = [2] },
            cancellation.Token));
        Assert.Equal([1], File.ReadAllBytes(sandbox.PathFor("root/bin/" + Bin("tool"))));
        Assert.Equal(oldV1, File.ReadAllBytes(sandbox.PathFor("root/.crates.toml")));
        Assert.Equal(oldV2, File.ReadAllBytes(sandbox.PathFor("root/.crates2.json")));
        Assert.False(Directory.Exists(sandbox.PathFor(Transaction)));
    }

    [Fact]
    public async Task CancellationDuringMultiBinaryCommitRestoresOriginalFilesAndMetadata()
    {
        using var sandbox = new CargoTestSandbox();
        await Install(sandbox, "1.2.3", ("aaa", [1]), ("bbb", [2]));
        byte[] oldV1 = File.ReadAllBytes(sandbox.PathFor("root/.crates.toml"));
        byte[] oldV2 = File.ReadAllBytes(sandbox.PathFor("root/.crates2.json"));
        using var cancellation = new CancellationTokenSource();
        var binaries = new CancelingBinaryMap(new Dictionary<string, byte[]>
        {
            [Bin("aaa")] = [3],
            [Bin("bbb")] = [4],
        }, cancellation, sandbox.PathFor(Transaction + "/journal.json"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CargoInstallTracker.InstallAsync(
            sandbox.PathFor("root"), Plan("2.0.0"), binaries, cancellation.Token));
        Assert.Equal(1, binaries.CommitLookups);
        Assert.Equal([1], File.ReadAllBytes(sandbox.PathFor("root/bin/" + Bin("aaa"))));
        Assert.Equal([2], File.ReadAllBytes(sandbox.PathFor("root/bin/" + Bin("bbb"))));
        Assert.Equal(oldV1, File.ReadAllBytes(sandbox.PathFor("root/.crates.toml")));
        Assert.Equal(oldV2, File.ReadAllBytes(sandbox.PathFor("root/.crates2.json")));
        Assert.False(Directory.Exists(sandbox.PathFor(Transaction)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverRestoresUncommittedTransactionsAndKeepsCommittedOnes(bool committed)
    {
        using var sandbox = new CargoTestSandbox();
        await Install(sandbox, "1.2.3", ("tool", [1, 2]));
        byte[] oldV1 = File.ReadAllBytes(sandbox.PathFor("root/.crates.toml"));
        byte[] oldV2 = File.ReadAllBytes(sandbox.PathFor("root/.crates2.json"));
        sandbox.Write(Transaction + "/metadata-v1", oldV1);
        sandbox.Write(Transaction + "/metadata-v2", oldV2);
        sandbox.Write(Transaction + "/backup-0", [1, 2]);
        var journal = new CargoTransactionJournal(
            [new(Bin("tool"), true, InstalledHash: Hash([5, 6])),
                new(Bin("new-tool"), false, InstalledHash: Hash([9]))], committed,
            Hash("[v1]\n"u8.ToArray()), Hash("""{"installs":{}}"""u8.ToArray()));
        sandbox.WriteText(Transaction + "/journal.json",
            JsonSerializer.Serialize(journal, CargoJsonContext.Default.CargoTransactionJournal));
        sandbox.Write("root/bin/" + Bin("tool"), [5, 6]);
        sandbox.Write("root/bin/" + Bin("new-tool"), [9]);
        sandbox.WriteText("root/.crates.toml", "[v1]\n");
        sandbox.WriteText("root/.crates2.json", """{"installs":{}}""");
        await CargoInstallTracker.RecoverAsync(sandbox.PathFor("root"), default);
        Assert.Equal(committed ? new byte[] { 5, 6 } : [1, 2],
            File.ReadAllBytes(sandbox.PathFor("root/bin/" + Bin("tool"))));
        Assert.Equal(committed, File.Exists(sandbox.PathFor("root/bin/" + Bin("new-tool"))));
        Assert.Equal(committed ? "[v1]\n"u8.ToArray() : oldV1,
            File.ReadAllBytes(sandbox.PathFor("root/.crates.toml")));
        Assert.Equal(committed ? """{"installs":{}}"""u8.ToArray() : oldV2,
            File.ReadAllBytes(sandbox.PathFor("root/.crates2.json")));
        Assert.False(Directory.Exists(sandbox.PathFor(Transaction)));
        await CargoInstallTracker.RecoverAsync(sandbox.PathFor("root"), default);
        Assert.False(Directory.Exists(sandbox.PathFor(Transaction)));
    }

    [Fact]
    public async Task MissingJournalAndUnsafeRecoveryNamesFailWithoutTouchingOutsideFiles()
    {
        using var sandbox = new CargoTestSandbox();
        sandbox.WriteText("outside", "untouched");
        Directory.CreateDirectory(sandbox.PathFor(Transaction));
        await CargoInstallTracker.RecoverAsync(sandbox.PathFor("root"), default);
        Assert.False(Directory.Exists(sandbox.PathFor(Transaction)));
        sandbox.Write(Transaction + "/staged-binary", [1]);
        Assert.Contains("needs inspection", (await Assert.ThrowsAsync<IOException>(() =>
            CargoInstallTracker.RecoverAsync(sandbox.PathFor("root"), default))).Message);
        sandbox.WriteText(Transaction + "/journal.json", """
            {"Entries":[{"Name":"../../outside","Existed":false}],"Committed":false}
            """);
        sandbox.Write(Transaction + "/metadata-v1", File.ReadAllBytes(sandbox.PathFor("root/.crates.toml")));
        sandbox.Write(Transaction + "/metadata-v2", File.ReadAllBytes(sandbox.PathFor("root/.crates2.json")));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CargoInstallTracker.RecoverAsync(sandbox.PathFor("root"), default));
        Assert.Equal("untouched", File.ReadAllText(sandbox.PathFor("outside")));
        Assert.True(Directory.Exists(sandbox.PathFor(Transaction)));
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    [InlineData("binary")]
    public async Task RecoveryRefusesToOverwriteLaterExternalCargoChanges(string changed)
    {
        using var sandbox = new CargoTestSandbox();
        await Install(sandbox, "1.2.3", ("tool", [1, 2]));
        sandbox.Write(Transaction + "/metadata-v1", File.ReadAllBytes(sandbox.PathFor("root/.crates.toml")));
        sandbox.Write(Transaction + "/metadata-v2", File.ReadAllBytes(sandbox.PathFor("root/.crates2.json")));
        sandbox.Write(Transaction + "/backup-0", [1, 2]);
        byte[] plannedV1 = "[v1]\n"u8.ToArray();
        byte[] plannedV2 = """{"installs":{}}"""u8.ToArray();
        var journal = new CargoTransactionJournal(
            [new(Bin("tool"), true, InstalledHash: Hash([5, 6])),
                new(Bin("new-tool"), false, InstalledHash: Hash([9]))],
            false, Hash(plannedV1), Hash(plannedV2));
        sandbox.WriteText(Transaction + "/journal.json",
            JsonSerializer.Serialize(journal, CargoJsonContext.Default.CargoTransactionJournal));
        sandbox.Write("root/.crates.toml", changed == "v1" ? "# later Cargo\n[v1]\n"u8.ToArray() : plannedV1);
        sandbox.Write("root/.crates2.json", changed == "v2" ? """{"installs":{},"external":true}"""u8.ToArray() : plannedV2);
        sandbox.Write("root/bin/" + Bin("tool"), [5, 6]);
        sandbox.Write("root/bin/" + Bin("new-tool"), changed == "binary" ? [99] : [9]);
        byte[] currentV1 = File.ReadAllBytes(sandbox.PathFor("root/.crates.toml"));
        byte[] currentV2 = File.ReadAllBytes(sandbox.PathFor("root/.crates2.json"));
        var error = await Assert.ThrowsAsync<IOException>(() =>
            CargoInstallTracker.RecoverAsync(sandbox.PathFor("root"), default));
        Assert.Contains(changed == "binary" ? "executable" : "metadata", error.Message);
        Assert.Equal(currentV1, File.ReadAllBytes(sandbox.PathFor("root/.crates.toml")));
        Assert.Equal(currentV2, File.ReadAllBytes(sandbox.PathFor("root/.crates2.json")));
        Assert.Equal([5, 6], File.ReadAllBytes(sandbox.PathFor("root/bin/" + Bin("tool"))));
        Assert.Equal(changed == "binary" ? new byte[] { 99 } : [9],
            File.ReadAllBytes(sandbox.PathFor("root/bin/" + Bin("new-tool"))));
        Assert.True(File.Exists(sandbox.PathFor(Transaction + "/journal.json")));
        Assert.Equal([1, 2], File.ReadAllBytes(sandbox.PathFor(Transaction + "/backup-0")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyRecoveryWithoutHashesOnlyAcceptsUnchangedOriginalFiles(bool externallyChanged)
    {
        using var sandbox = new CargoTestSandbox();
        await Install(sandbox, "1.2.3", ("tool", [1, 2]));
        byte[] oldV1 = File.ReadAllBytes(sandbox.PathFor("root/.crates.toml"));
        byte[] oldV2 = File.ReadAllBytes(sandbox.PathFor("root/.crates2.json"));
        sandbox.Write(Transaction + "/metadata-v1", oldV1);
        sandbox.Write(Transaction + "/metadata-v2", oldV2);
        sandbox.Write(Transaction + "/backup-0", [1, 2]);
        sandbox.WriteText(Transaction + "/journal.json", $$"""
            {"Entries":[{"Name":"{{Bin("tool")}}","Existed":true}],"Committed":false}
            """);
        if (externallyChanged)
            sandbox.Write("root/bin/" + Bin("tool"), [99]);
        if (externallyChanged)
            Assert.Contains("changed after", (await Assert.ThrowsAsync<IOException>(() =>
                CargoInstallTracker.RecoverAsync(sandbox.PathFor("root"), default))).Message);
        else
            await CargoInstallTracker.RecoverAsync(sandbox.PathFor("root"), default);
        Assert.Equal(oldV1, File.ReadAllBytes(sandbox.PathFor("root/.crates.toml")));
        Assert.Equal(oldV2, File.ReadAllBytes(sandbox.PathFor("root/.crates2.json")));
        Assert.Equal(externallyChanged ? new byte[] { 99 } : [1, 2],
            File.ReadAllBytes(sandbox.PathFor("root/bin/" + Bin("tool"))));
        Assert.Equal(externallyChanged, Directory.Exists(sandbox.PathFor(Transaction)));
    }

    [Fact]
    public async Task SourceUpdateArgumentsPreserveInstalledFeaturesProfileAndCrossTarget()
    {
        using var sandbox = new CargoTestSandbox();
        Assert.Empty(await CargoInstallTracker.GetBuildArgumentsAsync(sandbox.PathFor("root"), "fixture-tool", default));
        await Install(sandbox, "1.2.3", ("tool", [1]));
        var metadata = Metadata(sandbox);
        var info = metadata["installs"]![Key("1.2.3")]!;
        info["features"] = new JsonArray("fast", "color");
        info["all_features"] = true;
        info["no_default_features"] = true;
        info["profile"] = "custom-release";
        info["target"] = "aarch64-unknown-linux-musl";
        WriteMetadata(sandbox, metadata);
        string[] expected =
        [
            "--features", CoreTools.EscapeCommandLineArgument("fast,color"), "--all-features",
            "--no-default-features", "--profile", CoreTools.EscapeCommandLineArgument("custom-release"),
            "--target", CoreTools.EscapeCommandLineArgument("aarch64-unknown-linux-musl"),
        ];
        Assert.Equal(expected, await CargoInstallTracker.GetBuildArgumentsAsync(sandbox.PathFor("root"),
            "fixture-tool", default, CargoTestData.Target));
        Assert.Equal(expected[..^2], await CargoInstallTracker.GetBuildArgumentsAsync(sandbox.PathFor("root"),
            "fixture-tool", default, "aarch64-unknown-linux-musl"));
        Assert.Empty(await CargoInstallTracker.GetBuildArgumentsAsync(sandbox.PathFor("root"), "other", default));
        metadata["installs"]![Key("2.0.0")] = info.DeepClone();
        WriteMetadata(sandbox, metadata);
        Assert.Contains("ambiguous", (await Assert.ThrowsAsync<InvalidDataException>(() =>
            CargoInstallTracker.GetBuildArgumentsAsync(sandbox.PathFor("root"), "fixture-tool", default))).Message);
    }

    [Fact]
    public async Task InvalidMetadataAndEmptyInstallFailBeforeBinaryMutation()
    {
        using var sandbox = new CargoTestSandbox();
        await Assert.ThrowsAsync<InvalidDataException>(() => Install(sandbox, "1.2.3"));
        sandbox.WriteText("root/.crates.toml", "v1 = 42");
        sandbox.WriteText("root/.crates2.json", """{"installs":{}}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => Install(sandbox, "1.2.3", ("tool", [1])));
        sandbox.WriteText("root/.crates.toml", "[v1]");
        sandbox.WriteText("root/.crates2.json", """{"installs":[]}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => Install(sandbox, "1.2.3", ("tool", [1])));
        Assert.False(File.Exists(sandbox.PathFor("root/bin/" + Bin("tool"))));
        Assert.Throws<InvalidDataException>(() => CargoInstallTracker.ReadBins("not-an-array"));
    }

    // A deterministic cancellation trigger on the dictionary contract used by the
    // commit loop: no sleeps, filesystem watcher races, or production test hooks.
    private sealed class CancelingBinaryMap(
        IReadOnlyDictionary<string, byte[]> inner, CancellationTokenSource cancellation, string journalPath)
        : IReadOnlyDictionary<string, byte[]>
    {
        internal int CommitLookups { get; private set; }
        public byte[] this[string key] => inner[key];
        public IEnumerable<string> Keys => inner.Keys;
        public IEnumerable<byte[]> Values => inner.Values;
        public int Count => inner.Count;
        public bool ContainsKey(string key) => inner.ContainsKey(key);
        public bool TryGetValue(string key, out byte[] value)
        {
            if (File.Exists(journalPath))
            {
                CommitLookups++;
                cancellation.Cancel();
            }
            return inner.TryGetValue(key, out value!);
        }
        public IEnumerator<KeyValuePair<string, byte[]>> GetEnumerator() => inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
