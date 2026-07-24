using UniGetUI.PackageEngine.Enums;
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
        OperationHistoryStore.Add(record);

        // Drop the in-memory cache so the next read must deserialize the file.
        OperationHistoryStore.InvalidateCache();

        var reloaded = OperationHistoryStore.Get("persisted");
        Assert.NotNull(reloaded);
        Assert.Equal("Contoso.persisted", reloaded!.PackageId);
        Assert.Equal(2, reloaded.Output.Count);
        Assert.Equal("boom", reloaded.Output[1].Text);
        Assert.Equal("Error", reloaded.Output[1].Type);
    }

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
