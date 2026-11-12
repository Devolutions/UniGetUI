using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.Operations.History;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class OperationHistoryTests : IDisposable
{
    private readonly string _tempFile;

    public OperationHistoryTests()
    {
        _tempFile = Path.Combine(
            Path.GetTempPath(),
            $"unigetui-histtest-{Guid.NewGuid():N}.json");
        OperationHistoryStore.TestFilePathOverride = _tempFile;
        OperationHistoryStore.InvalidateCache();
        OperationHistoryStore.Clear();
    }

    public void Dispose()
    {
        OperationHistoryStore.TestFilePathOverride = null;
        OperationHistoryStore.InvalidateCache();
        try { if (File.Exists(_tempFile)) File.Delete(_tempFile); }
        catch { /* best-effort cleanup */ }
    }

    private static OperationHistoryRecord Record(string id, string kind = "install-package")
        => new()
        {
            Id = id,
            Kind = kind,
            PackageId = "Contoso." + id,
            PackageName = "Contoso " + id,
            ManagerName = "winget",
            SourceName = "winget",
            VersionBefore = "1.0.0",
            VersionAfter = "1.0.0",
            Status = OperationHistoryRecord.StatusSucceeded,
            TimestampUtc = DateTime.UtcNow.ToString("O"),
        };

    [Fact]
    public void Add_PrependsNewestFirst()
    {
        OperationHistoryStore.Add(Record("a"));
        OperationHistoryStore.Add(Record("b"));
        OperationHistoryStore.Add(Record("c"));

        var all = OperationHistoryStore.GetAll();
        Assert.Equal(3, all.Count);
        Assert.Equal("c", all[0].Id);
        Assert.Equal("b", all[1].Id);
        Assert.Equal("a", all[2].Id);
    }

    [Fact]
    public void GetById_And_Remove()
    {
        OperationHistoryStore.Add(Record("a"));
        OperationHistoryStore.Add(Record("b"));

        Assert.NotNull(OperationHistoryStore.Get("a"));
        Assert.Null(OperationHistoryStore.Get("does-not-exist"));

        OperationHistoryStore.Remove("a");
        Assert.Null(OperationHistoryStore.Get("a"));
        Assert.Single(OperationHistoryStore.GetAll());
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        OperationHistoryStore.Add(Record("a"));
        OperationHistoryStore.Add(Record("b"));
        OperationHistoryStore.Clear();
        Assert.Empty(OperationHistoryStore.GetAll());
    }

    [Fact]
    public void ChangedEvent_FiresOnMutation()
    {
        int fired = 0;
        void Handler(object? _, EventArgs __) => fired++;
        OperationHistoryStore.Changed += Handler;
        try
        {
            OperationHistoryStore.Add(Record("a"));
            OperationHistoryStore.Remove("a");
            OperationHistoryStore.Clear();
        }
        finally
        {
            OperationHistoryStore.Changed -= Handler;
        }
        Assert.Equal(3, fired);
    }

    [Fact]
    public void PersistsToDiskAndReloads()
    {
        var record = Record("persisted");
        record.Output.Add(new OperationHistoryOutputLine { Text = "line one", Type = "Information" });
        record.Output.Add(new OperationHistoryOutputLine { Text = "boom", Type = "Error" });
        record.RanElevated = true;
        OperationHistoryStore.Add(record);

        // Drop the in-memory cache so the next read must deserialize the file.
        OperationHistoryStore.InvalidateCache();

        var reloaded = OperationHistoryStore.Get("persisted");
        Assert.NotNull(reloaded);
        Assert.Equal("Contoso.persisted", reloaded!.PackageId);
        Assert.Equal(2, reloaded.Output.Count);
        Assert.Equal("boom", reloaded.Output[1].Text);
        Assert.Equal("Error", reloaded.Output[1].Type);
        Assert.True(reloaded.RanElevated);
    }

    [Fact]
    public void PersistsANonElevatedRunAsFalseRatherThanUnknown()
    {
        var record = Record("standard");
        record.RanElevated = false;
        OperationHistoryStore.Add(record);
        OperationHistoryStore.InvalidateCache();

        var reloaded = OperationHistoryStore.Get("standard");
        Assert.NotNull(reloaded);
        Assert.False(reloaded!.RanElevated);
    }

    [Fact]
    public void RecordsWrittenBeforeElevationWasTrackedReloadAsUnknown()
    {
        File.WriteAllText(_tempFile, LegacyRecordJson);
        OperationHistoryStore.InvalidateCache();

        var reloaded = OperationHistoryStore.Get("legacy");
        Assert.NotNull(reloaded);
        Assert.Equal("Contoso.Legacy", reloaded!.PackageId);
        Assert.Null(reloaded.RanElevated);
    }

    private const string LegacyRecordJson = """
        [
          {
            "Id": "legacy",
            "Kind": "install-package",
            "Role": 0,
            "PackageId": "Contoso.Legacy",
            "PackageName": "Contoso Legacy",
            "ManagerName": "winget",
            "SourceName": "winget",
            "Status": "succeeded",
            "TimestampUtc": "2026-01-01T00:00:00.0000000Z",
            "Output": []
          }
        ]
        """;

    [Fact]
    public void CapsAtMaxEntries()
    {
        for (int i = 0; i < 1005; i++)
            OperationHistoryStore.Add(Record(i.ToString()));

        Assert.Equal(1000, OperationHistoryStore.GetAll().Count);
        // Newest (last added) survives; oldest is trimmed.
        Assert.Equal("1004", OperationHistoryStore.GetAll()[0].Id);
    }

    [Fact]
    public void BuildOutput_TrimsToTailWithMarkerWhenOverCap()
    {
        int total = OperationHistoryRecord.MaxOutputLines + 10;
        var lines = new List<(string, AbstractOperation.LineType)>();
        for (int i = 0; i < total; i++)
            lines.Add(($"line{i}", AbstractOperation.LineType.Information));

        var result = OperationHistoryRecord.BuildOutput(lines);

        // marker + exactly MaxOutputLines kept lines
        Assert.Equal(OperationHistoryRecord.MaxOutputLines + 1, result.Count);
        Assert.Contains("omitted", result[0].Text);
        Assert.Equal("line10", result[1].Text);          // first kept line (10 were dropped)
        Assert.Equal($"line{total - 1}", result[^1].Text); // tail preserved
    }

    [Fact]
    public void BuildOutput_KeepsEverythingWhenUnderCap()
    {
        var lines = new List<(string, AbstractOperation.LineType)>
        {
            ("a", AbstractOperation.LineType.Information),
            ("b", AbstractOperation.LineType.Error),
        };

        var result = OperationHistoryRecord.BuildOutput(lines);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, l => l.Text.Contains("omitted"));
        Assert.Equal("Error", result[1].Type);
    }

    [Fact]
    public void BuildOutput_KeepsBothEndsOfAnOversizedLine()
    {
        string huge = "HEAD" + new string('x', 25_000_000) + "TAIL";
        var lines = new List<(string, AbstractOperation.LineType)>
        {
            ("before", AbstractOperation.LineType.Information),
            (huge, AbstractOperation.LineType.Error),
            ("after", AbstractOperation.LineType.Information),
        };

        var result = OperationHistoryRecord.BuildOutput(lines);

        Assert.Equal(3, result.Count);
        Assert.Equal("before", result[0].Text);
        Assert.Equal("after", result[2].Text);
        Assert.Equal("Error", result[1].Type);
        Assert.True(result[1].Text.Length <= OperationHistoryRecord.MaxOutputLineChars);
        Assert.StartsWith("HEAD", result[1].Text);
        Assert.EndsWith("TAIL", result[1].Text);
    }

    [Fact]
    public void BuildOutput_CapsTotalCharactersAndKeepsTheTail()
    {
        var lines = new List<(string, AbstractOperation.LineType)>();
        for (int i = 0; i < 1000; i++)
            lines.Add(($"line{i:D4}" + new string('y', 2000), AbstractOperation.LineType.Information));

        var result = OperationHistoryRecord.BuildOutput(lines);

        Assert.True(result.Sum(l => l.Text.Length) <= OperationHistoryRecord.MaxOutputChars);
        Assert.StartsWith("line0999", result[^1].Text);
        int firstKept = int.Parse(result[1].Text[4..8]);
        Assert.Contains(firstKept.ToString(), result[0].Text);
        Assert.Equal(1000 - firstKept, result.Count - 1);
    }

    [Fact]
    public void ClampStoredOutput_IsIdempotent()
    {
        var record = Record("bloated");
        for (int i = 0; i < 200; i++)
            record.Output.Add(new OperationHistoryOutputLine { Text = new string('z', 10_000), Type = "Information" });

        Assert.True(record.ClampStoredOutput());
        var once = record.Output.Select(l => l.Text).ToList();
        Assert.False(record.ClampStoredOutput());
        Assert.Equal(once, record.Output.Select(l => l.Text));
    }

    [Fact]
    public void Load_ShrinksAStoreBloatedByAnOlderVersion()
    {
        var bloated = Record("bloated");
        bloated.Output.Add(new OperationHistoryOutputLine { Text = new string('q', 2_000_000), Type = "Error" });
        var small = Record("small");
        small.Output.Add(new OperationHistoryOutputLine { Text = "ok", Type = "Information" });
        var typeInfo = OperationHistoryJsonContext.Default.ListOperationHistoryRecord;
        File.WriteAllText(_tempFile, System.Text.Json.JsonSerializer.Serialize([bloated, small], typeInfo));
        long before = new FileInfo(_tempFile).Length;
        OperationHistoryStore.InvalidateCache();

        var reloaded = OperationHistoryStore.Get("bloated");

        Assert.NotNull(reloaded);
        Assert.Single(reloaded!.Output);
        Assert.True(reloaded.Output[0].Text.Length <= OperationHistoryRecord.MaxOutputLineChars);
        Assert.Equal("ok", OperationHistoryStore.Get("small")!.Output[0].Text);
        Assert.True(new FileInfo(_tempFile).Length < before / 100);
    }

    [Fact]
    public void ClampedLineNeverSplitsASurrogatePairAndStillPersists()
    {
        for (int shift = 0; shift < 2; shift++)
        {
            string huge = new string('a', shift) + string.Concat(Enumerable.Repeat("\U0001F600", 10_000));
            var record = Record("emoji" + shift);
            record.Output = OperationHistoryRecord.BuildOutput([(huge, AbstractOperation.LineType.Error)]);
            string text = record.Output[0].Text;

            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]))
                    Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]));
                else if (char.IsLowSurrogate(text[i]))
                    Assert.True(i > 0 && char.IsHighSurrogate(text[i - 1]));
            }

            OperationHistoryStore.Add(record);
            OperationHistoryStore.InvalidateCache();
            Assert.Equal(text, OperationHistoryStore.Get("emoji" + shift)!.Output[0].Text);
        }
    }

    [Fact]
    public void Load_KeepsTheHistoryWhenARecordHasNullOutput()
    {
        File.WriteAllText(_tempFile, """
            [
              { "Id": "nulloutput", "Kind": "install-package", "Output": null },
              { "Id": "nulltext", "Kind": "install-package", "Output": [ { "Text": null, "Type": "Information" }, null ] },
              { "Id": "fine", "Kind": "install-package", "Output": [ { "Text": "ok", "Type": "Information" } ] }
            ]
            """);
        OperationHistoryStore.InvalidateCache();

        Assert.Equal(3, OperationHistoryStore.GetAll().Count);
        Assert.Empty(OperationHistoryStore.Get("nulloutput")!.Output);
        Assert.Equal("", Assert.Single(OperationHistoryStore.Get("nulltext")!.Output).Text);
        Assert.Equal("ok", OperationHistoryStore.Get("fine")!.Output[0].Text);
    }

    [Fact]
    public void ClampStoredOutput_KeepsEveryLineOfTheLegacyLog()
    {
        var record = Record("legacy", OperationHistoryRecord.KindLegacyLog);
        for (int i = 0; i < 1000; i++)
            record.Output.Add(new OperationHistoryOutputLine { Text = new string('l', 1000), Type = "Information" });
        record.Output.Add(new OperationHistoryOutputLine { Text = new string('h', 100_000), Type = "Information" });

        Assert.True(record.ClampStoredOutput());

        Assert.Equal(1001, record.Output.Count);
        Assert.Equal(new string('l', 1000), record.Output[0].Text);
        Assert.True(record.Output[^1].Text.Length <= OperationHistoryRecord.MaxOutputLineChars);
    }

    [Fact]
    public void ClampStoredOutput_AddsToTheCountOfAnExistingOmittedMarker()
    {
        var shortLines = new List<(string, AbstractOperation.LineType)>();
        for (int i = 0; i < OperationHistoryRecord.MaxOutputLines + 10; i++)
            shortLines.Add(("x", AbstractOperation.LineType.Information));
        var marker = OperationHistoryRecord.BuildOutput(shortLines)[0];

        var record = Record("capped");
        record.Output.Add(marker);
        for (int i = 0; i < OperationHistoryRecord.MaxOutputLines; i++)
            record.Output.Add(new OperationHistoryOutputLine { Text = new string('w', 100), Type = "Information" });

        Assert.True(record.ClampStoredOutput());

        int kept = record.Output.Count - 1;
        int expectedOmitted = 10 + OperationHistoryRecord.MaxOutputLines - kept;
        Assert.Contains(expectedOmitted.ToString(), record.Output[0].Text);
        Assert.False(record.ClampStoredOutput());
    }

    [Theory]
    [InlineData("[... 10 frühere Zeilen ausgelassen ...]")]
    [InlineData("[... предыдущих строк пропущено: 10 ...]")]
    [InlineData("[... 以前の10行は省略されました ...]")]
    [InlineData("[... تم حذف 10 من الأسطر السابقة ...]")]
    [InlineData("[... 10 earlier lines omitted ...]")]
    public void ClampStoredOutput_KeepsTheOmittedCountOfAMarkerInAnyLanguage(string storedMarker)
    {
        var record = Record("localized");
        record.Output.Add(new OperationHistoryOutputLine { Text = storedMarker, Type = "Information" });
        for (int i = 0; i < OperationHistoryRecord.MaxOutputLines; i++)
            record.Output.Add(new OperationHistoryOutputLine { Text = new string('w', 100), Type = "Information" });

        Assert.True(record.ClampStoredOutput());

        int expectedOmitted = 10 + OperationHistoryRecord.MaxOutputLines - (record.Output.Count - 1);
        Assert.Contains(expectedOmitted.ToString(), record.Output[0].Text);
        Assert.DoesNotContain(record.Output, line => line.Text == storedMarker);
    }

    [Fact]
    public void ClampStoredOutput_TreatsAnOrdinaryFirstLineAsOutput()
    {
        var record = Record("ordinary");
        record.Output.Add(new OperationHistoryOutputLine { Text = "[... 10 of 20 ...]", Type = "Information" });
        for (int i = 0; i < OperationHistoryRecord.MaxOutputLines; i++)
            record.Output.Add(new OperationHistoryOutputLine { Text = new string('w', 100), Type = "Information" });

        Assert.True(record.ClampStoredOutput());

        int expectedOmitted = 1 + OperationHistoryRecord.MaxOutputLines - (record.Output.Count - 1);
        Assert.Contains(expectedOmitted.ToString(), record.Output[0].Text);
    }

    private static List<OperationHistoryOutputLine> Lines(params (string text, string type)[] items)
        => items.Select(i => new OperationHistoryOutputLine { Text = i.text, Type = i.type }).ToList();

    [Fact]
    public void FailureSummary_PrefersLastErrorLine()
    {
        var output = Lines(("installing...", "Information"), ("E: it broke", "Error"), ("trailing", "Information"));
        Assert.Equal("E: it broke",
            OperationHistoryRecord.DeriveFailureSummary(output, OperationHistoryRecord.StatusFailed));
    }

    [Fact]
    public void FailureSummary_FallsBackToLastInformationWhenNoErrorLine()
    {
        // Managers commonly print failures to stdout (Information), not stderr (Error).
        var output = Lines(("step 1", "Information"), ("fatal: could not install package", "Information"));
        Assert.Equal("fatal: could not install package",
            OperationHistoryRecord.DeriveFailureSummary(output, OperationHistoryRecord.StatusFailed));
    }

    [Fact]
    public void FailureSummary_SkipsBlankAndVerboseLines()
    {
        var output = Lines(
            ("real error here", "Information"),
            ("   ", "Information"),
            ("End Time: 2026", "VerboseDetails"));
        Assert.Equal("real error here",
            OperationHistoryRecord.DeriveFailureSummary(output, OperationHistoryRecord.StatusFailed));
    }

    [Fact]
    public void FailureSummary_EmptyWhenNotFailed()
    {
        var output = Lines(("whatever", "Error"));
        Assert.Equal("", OperationHistoryRecord.DeriveFailureSummary(output, OperationHistoryRecord.StatusSucceeded));
    }

    [Fact]
    public void FromOperation_Install_CapturesKindRoleAndVersion()
    {
        var manager = new PackageManagerBuilder().WithName("Scoop").Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.2.3")
            .Build();

        using var op = new InstallPackageOperation(package, new InstallOptions(), IgnoreParallelInstalls: true);
        var record = OperationHistoryRecord.FromOperation(op, OperationHistoryRecord.StatusSucceeded);

        Assert.Equal("install-package", record.Kind);
        Assert.Equal((int)OperationType.Install, record.Role);
        Assert.Equal("Contoso.Tool", record.PackageId);
        Assert.Equal(manager.Id, record.ManagerName);
        Assert.Equal("1.2.3", record.VersionAfter);
        Assert.Equal(OperationHistoryRecord.StatusSucceeded, record.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FromOperation_CapturesTheElevationTheOperationRanWith(bool elevated)
    {
        var manager = new PackageManagerBuilder().WithName("Scoop").Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.2.3")
            .Build();

        using var op = new ElevationStubInstallOperation(package, new InstallOptions(), elevated);
        var record = OperationHistoryRecord.FromOperation(op, OperationHistoryRecord.StatusSucceeded);

        Assert.Equal(elevated, record.RanElevated);
    }

    private sealed class ElevationStubInstallOperation : InstallPackageOperation
    {
        private readonly bool _elevated;

        public ElevationStubInstallOperation(IPackage package, InstallOptions options, bool elevated)
            : base(package, options, IgnoreParallelInstalls: true)
        {
            _elevated = elevated;
        }

        public override bool WillRunElevated => _elevated;
    }

    // The package a Discover install starts from carries the feed's LATEST version, while the
    // user may have pinned an older one in the install options. Recording the package version
    // then claims a version that was never installed - and the retry-from-history flow rebuilds
    // the package from these fields.
    [Fact]
    public void FromOperation_Install_RecordsThePinnedVersionRatherThanTheLatest()
    {
        var manager = new PackageManagerBuilder().WithName("Scoop").Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("3.0.3")
            .Build();

        using var op = new InstallPackageOperation(
            package,
            new InstallOptions { Version = "2.1.7" },
            IgnoreParallelInstalls: true
        );
        var record = OperationHistoryRecord.FromOperation(
            op,
            OperationHistoryRecord.StatusSucceeded
        );

        Assert.Equal("", record.VersionBefore);
        Assert.Equal("2.1.7", record.VersionAfter);
    }

    [Fact]
    public void FromOperation_Update_RecordsThePinnedVersionRatherThanTheNewVersion()
    {
        var manager = new PackageManagerBuilder().WithName("Scoop").Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();

        using var op = new UpdatePackageOperation(
            package,
            new InstallOptions { Version = "2.0.0", VersionIsExplicit = true }
        );
        var record = OperationHistoryRecord.FromOperation(
            op,
            OperationHistoryRecord.StatusSucceeded
        );

        Assert.Equal("1.0.0", record.VersionBefore);
        Assert.Equal("2.0.0", record.VersionAfter);
    }

    [Fact]
    public void FromOperation_Update_RecordsTheNewVersionWhenTheVersionIsOnlySaved()
    {
        var manager = new PackageManagerBuilder().WithName("Scoop").Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("3.0.0")
            .Build();

        using var op = new UpdatePackageOperation(package, new InstallOptions { Version = "1.0.0" });
        var record = OperationHistoryRecord.FromOperation(
            op,
            OperationHistoryRecord.StatusSucceeded
        );

        Assert.Equal("3.0.0", record.VersionAfter);
    }

    [Fact]
    public void FromOperation_Uninstall_RecordsNoVersionAfterEvenWhenPinned()
    {
        var manager = new PackageManagerBuilder().WithName("Scoop").Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();

        using var op = new UninstallPackageOperation(
            package,
            new InstallOptions { Version = "2.0.0" }
        );
        var record = OperationHistoryRecord.FromOperation(
            op,
            OperationHistoryRecord.StatusSucceeded
        );

        Assert.Equal("1.0.0", record.VersionBefore);
        Assert.Equal("", record.VersionAfter);
    }

    [Fact]
    public void FromOperation_Update_IgnoresAnInstallOnlyPinnedVersion()
    {
        var manager = new PackageManagerBuilder()
            .WithName("Scoop")
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.SupportsCustomVersionsOnUpdate = false;
                return capabilities;
            })
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("jq")
            .WithVersion("1.7.1")
            .WithNewVersion("1.8.2")
            .Build();

        using var op = new UpdatePackageOperation(
            package,
            new InstallOptions { Version = "1.7.1" },
            IgnoreParallelInstalls: true
        );
        var record = OperationHistoryRecord.FromOperation(op, OperationHistoryRecord.StatusSucceeded);

        Assert.Equal("1.7.1", record.VersionBefore);
        Assert.Equal("1.8.2", record.VersionAfter);
    }

    [Theory]
    [InlineData(true, "1.2.0")]
    [InlineData(false, "2.0.0")]
    public void FromOperation_Update_RecordsAnExplicitVersionTheManagerHonors(bool canUpdateToExactVersion, string recorded)
    {
        var manager = new PackageManagerBuilder()
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.SupportsCustomVersionsOnUpdate = false;
                return capabilities;
            })
            .Build();
        manager.CanUpdateToExactVersionOverride = canUpdateToExactVersion;
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .Build();

        using var op = new UpdatePackageOperation(
            package,
            new InstallOptions { SkipMajorUpdates = true, Version = "1.2.0", VersionIsExplicit = true },
            IgnoreParallelInstalls: true
        );
        var record = OperationHistoryRecord.FromOperation(op, OperationHistoryRecord.StatusSucceeded);

        Assert.Equal(recorded, record.VersionAfter);
    }

    [Fact]
    public void FromOperation_Update_CapturesVersionTransition()
    {
        var manager = new PackageManagerBuilder().WithName("Scoop").Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .Build();

        using var op = new UpdatePackageOperation(package, new InstallOptions(), IgnoreParallelInstalls: true);
        var record = OperationHistoryRecord.FromOperation(op, OperationHistoryRecord.StatusSucceeded);

        Assert.Equal("update-package", record.Kind);
        Assert.Equal((int)OperationType.Update, record.Role);
        Assert.Equal("1.0.0", record.VersionBefore);
        Assert.Equal("2.0.0", record.VersionAfter);
    }

    [Fact]
    public void FromOperation_Uninstall_SerializesOptions()
    {
        var manager = new PackageManagerBuilder().WithName("Scoop").Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("3.1.4")
            .Build();

        var options = new InstallOptions { RunAsAdministrator = true };
        using var op = new UninstallPackageOperation(package, options, IgnoreParallelInstalls: true);
        var record = OperationHistoryRecord.FromOperation(op, OperationHistoryRecord.StatusFailed);

        Assert.Equal("uninstall-package", record.Kind);
        Assert.Equal((int)OperationType.Uninstall, record.Role);
        Assert.Equal(OperationHistoryRecord.StatusFailed, record.Status);
        Assert.Contains("RunAsAdministrator", record.OptionsJson);
    }
}
