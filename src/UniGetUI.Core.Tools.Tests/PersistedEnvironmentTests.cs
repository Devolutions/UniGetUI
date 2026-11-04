using PersistedValue = UniGetUI.Core.Tools.PersistedEnvironment.PersistedValue;

namespace UniGetUI.Core.Tools.Tests;

public class PersistedEnvironmentTests
{
    private static PersistedValue Text(string value) => new(value, false);

    private static PersistedValue Expandable(string value) => new(value, true);

    private static PersistedEnvironment.Snapshot Snapshot(
        Dictionary<string, PersistedValue>? machine = null,
        Dictionary<string, PersistedValue>? user = null
    ) => PersistedEnvironment.CreateSnapshot(machine ?? [], user ?? []);

    private static IReadOnlyDictionary<string, string?> Changes(
        PersistedEnvironment.Snapshot before,
        PersistedEnvironment.Snapshot after,
        string? processValue,
        Func<string, string>? expand = null
    ) => PersistedEnvironment.ComputeChanges(before, after, _ => processValue, expand ?? (value => value));

    [Fact]
    public void ReportsNothingWhenTheEnvironmentIsUnchanged()
    {
        var before = Snapshot(new() { ["Path"] = Text(@"C:\Windows") }, new() { ["CARGO_HOME"] = Text(@"C:\cargo") });
        var after = Snapshot(new() { ["Path"] = Text(@"C:\Windows") }, new() { ["CARGO_HOME"] = Text(@"C:\cargo") });

        Assert.Empty(Changes(before, after, @"C:\Windows;C:\Bundled"));
    }

    [Fact]
    public void ReportsAVariableAddedToTheUserEnvironment()
    {
        var changes = Changes(Snapshot(), Snapshot(user: new() { ["CARGO_HOME"] = Text(@"C:\cargo") }), null);

        Assert.Equal(@"C:\cargo", Assert.Single(changes, c => c.Key == "CARGO_HOME").Value);
    }

    [Fact]
    public void ReportsARemovedVariableAsNull()
    {
        var changes = Changes(Snapshot(user: new() { ["CARGO_HOME"] = Text(@"C:\cargo") }), Snapshot(), @"C:\cargo");

        Assert.Null(Assert.Single(changes).Value);
    }

    [Fact]
    public void UserValueKeepsPrecedenceOverAMachineChange()
    {
        var changes = Changes(
            Snapshot(new() { ["RUSTUP_HOME"] = Text(@"C:\old") }, new() { ["RUSTUP_HOME"] = Text(@"D:\user") }),
            Snapshot(new() { ["RUSTUP_HOME"] = Text(@"C:\new") }, new() { ["RUSTUP_HOME"] = Text(@"D:\user") }),
            @"D:\user"
        );

        Assert.Empty(changes);
    }

    [Fact]
    public void FallsBackToTheMachineValueWhenTheUserValueIsRemoved()
    {
        var changes = Changes(
            Snapshot(new() { ["RUSTUP_HOME"] = Text(@"C:\machine") }, new() { ["RUSTUP_HOME"] = Text(@"D:\user") }),
            Snapshot(new() { ["RUSTUP_HOME"] = Text(@"C:\machine") }),
            @"D:\user"
        );

        Assert.Equal(@"C:\machine", Assert.Single(changes).Value);
    }

    [Fact]
    public void AProcessValueSetOnPurposeIsNotOverwritten()
    {
        var changes = Changes(
            Snapshot(
                new() { ["ChocolateyInstall"] = Text(@"C:\ProgramData\chocolatey") },
                new() { ["ChocolateyInstall"] = Text(@"C:\legacy\choco") }
            ),
            Snapshot(new() { ["ChocolateyInstall"] = Text(@"C:\ProgramData\chocolatey") }),
            @"C:\Tools\chocolatey"
        );

        Assert.Empty(changes);
    }

    [Fact]
    public void AVariableTheLauncherAlreadyDefinedIsNotOverwritten()
    {
        var changes = Changes(Snapshot(), Snapshot(user: new() { ["CARGO_HOME"] = Text(@"C:\cargo") }), @"D:\from-launcher");

        Assert.Empty(changes);
    }

    [Fact]
    public void AnExpandableValueIsComparedUnexpandedAndAppliedExpanded()
    {
        var before = Snapshot(user: new() { ["CARGO_HOME"] = Expandable(@"%USERPROFILE%\.cargo") });
        var after = Snapshot(user: new() { ["CARGO_HOME"] = Expandable(@"%USERPROFILE%\cargo") });

        var changes = Changes(before, after, @"C:\Users\me\.cargo", value => value.Replace("%USERPROFILE%", @"C:\Users\me"));

        Assert.Equal(@"C:\Users\me\cargo", Assert.Single(changes).Value);
    }

    [Fact]
    public void AnExpandableValueWhoseReferenceChangedInTheProcessIsNotAChange()
    {
        var snapshot = Snapshot(new() { ["Path"] = Expandable(@"C:\Windows;%ChocolateyInstall%\bin") });

        var changes = Changes(
            snapshot,
            Snapshot(new() { ["Path"] = Expandable(@"C:\Windows;%ChocolateyInstall%\bin") }),
            @"C:\Windows;C:\ProgramData\chocolatey\bin",
            value => value.Replace("%ChocolateyInstall%", @"C:\Tools\chocolatey")
        );

        Assert.Empty(changes);
    }

    [Fact]
    public void PathChangesAreMergedIntoTheProcessPath()
    {
        var before = Snapshot(
            new() { ["Path"] = Text(@"C:\Windows;C:\Removed") },
            new() { ["Path"] = Text(@"C:\Users\me\bin") }
        );
        var after = Snapshot(
            new() { ["Path"] = Text(@"C:\Windows") },
            new() { ["Path"] = Text(@"C:\Users\me\bin;C:\cargo\bin") }
        );

        var changes = Changes(before, after, @"C:\Bundled;C:\Windows;c:\removed;C:\Users\me\bin");

        Assert.Equal(@"C:\Bundled;C:\Windows;C:\Users\me\bin;C:\cargo\bin", Assert.Single(changes).Value);
    }

    [Fact]
    public void APrependedPathEntryKeepsItsPrecedence()
    {
        var before = Snapshot(new() { ["Path"] = Text(@"C:\Windows") }, new() { ["Path"] = Text(@"C:\old-cargo\bin") });
        var after = Snapshot(
            new() { ["Path"] = Text(@"C:\Windows") },
            new() { ["Path"] = Text(@"C:\cargo\bin;C:\old-cargo\bin") }
        );

        var changes = Changes(before, after, @"C:\Bundled;C:\Windows;C:\old-cargo\bin");

        Assert.Equal(@"C:\Bundled;C:\Windows;C:\cargo\bin;C:\old-cargo\bin", Assert.Single(changes).Value);
    }

    [Fact]
    public void ARemovedPathEntryIsMatchedWhateverItsSpelling()
    {
        var changes = Changes(
            Snapshot(user: new() { ["Path"] = Text(@"C:\Tools\bin\;C:\Keep") }),
            Snapshot(user: new() { ["Path"] = Text(@"C:\Keep") }),
            @"C:\Windows;""c:\tools\BIN"";C:\Keep"
        );

        Assert.Equal(@"C:\Windows;C:\Keep", Assert.Single(changes).Value);
    }

    [Fact]
    public void AnAddedPathEntryAlreadyInTheProcessPathIsNotDuplicated()
    {
        var changes = Changes(
            Snapshot(user: new() { ["PATH"] = Text("") }),
            Snapshot(user: new() { ["PATH"] = Text(@"C:\cargo\bin\") }),
            @"C:\Windows;c:\cargo\bin"
        );

        Assert.Equal(@"C:\Windows;c:\cargo\bin", Assert.Single(changes).Value);
    }

    [Fact]
    public void AFailedReadLeavesTheEnvironmentAndBaselineAlone()
    {
        string name = "UNIGETUI_PERSISTED_FAILED_READ_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
        var baseline = Snapshot(new() { [name] = Text("kept") });
        try
        {
            PersistedEnvironment.TEST_ResetBaseline(baseline);
            Environment.SetEnvironmentVariable(name, "kept", EnvironmentVariableTarget.Process);

            Assert.Empty(PersistedEnvironment.Refresh(() => null));
            Assert.Equal("kept", Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process));

            Assert.Contains(name, PersistedEnvironment.Refresh(() => Snapshot(new() { [name] = Text("changed") })));
            Assert.Equal("changed", Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
            PersistedEnvironment.TEST_ResetBaseline();
        }
    }

    [WindowsFact]
    public void RefreshPicksUpAVariablePersistedForTheUser()
    {
        string name = "UNIGETUI_PERSISTED_ENVIRONMENT_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
        try
        {
            PersistedEnvironment.TEST_ResetBaseline();
            Environment.SetEnvironmentVariable(name, "persisted", EnvironmentVariableTarget.User);
            Assert.Null(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process));

            Assert.Contains(name, PersistedEnvironment.Refresh());
            Assert.Equal("persisted", Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process));

            Assert.DoesNotContain(name, PersistedEnvironment.Refresh());

            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
            Assert.Contains(name, PersistedEnvironment.Refresh());
            Assert.Null(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
            PersistedEnvironment.TEST_ResetBaseline();
        }
    }
}
