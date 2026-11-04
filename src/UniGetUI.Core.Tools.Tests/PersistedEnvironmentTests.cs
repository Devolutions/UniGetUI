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
        Dictionary<string, string>? process = null
    )
    {
        var environment = new Dictionary<string, string>(process ?? [], StringComparer.OrdinalIgnoreCase);
        return PersistedEnvironment.ComputeChanges(
            before,
            after,
            name => environment.TryGetValue(name, out string? value) ? value : processValue
        );
    }

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

        var changes = Changes(
            before,
            after,
            null,
            new() { ["CARGO_HOME"] = @"C:\Users\me\.cargo", ["USERPROFILE"] = @"C:\Users\me" }
        );

        Assert.Equal(@"C:\Users\me\cargo", Assert.Single(changes).Value);
    }

    [Fact]
    public void AnExpandableValueWhoseReferenceChangedInTheProcessIsNotAChange()
    {
        var snapshot = Snapshot(new() { ["Path"] = Expandable(@"C:\Windows;%ChocolateyInstall%\bin") });

        var changes = Changes(
            snapshot,
            Snapshot(new() { ["Path"] = Expandable(@"C:\Windows;%ChocolateyInstall%\bin") }),
            null,
            new() { ["Path"] = @"C:\Windows;C:\ProgramData\chocolatey\bin", ["ChocolateyInstall"] = @"C:\Tools\chocolatey" }
        );

        Assert.Empty(changes);
    }

    [Fact]
    public void AValueReferencingAnUpdatedVariableResolvesToItsNewValue()
    {
        var changes = Changes(
            Snapshot(user: new() { ["CARGO_HOME"] = Text(@"C:\old") }),
            Snapshot(user: new() { ["CARGO_HOME"] = Text(@"C:\new"), ["RUSTUP_HOME"] = Expandable(@"%CARGO_HOME%\rustup") }),
            null,
            new() { ["CARGO_HOME"] = @"C:\old" }
        );

        Assert.Equal(@"C:\new", changes["CARGO_HOME"]);
        Assert.Equal(@"C:\new\rustup", changes["RUSTUP_HOME"]);
    }

    [Fact]
    public void ChainedReferencesResolveToTheirNewValues()
    {
        var changes = Changes(
            Snapshot(user: new() { ["ROOT"] = Text(@"C:\old") }),
            Snapshot(
                user: new()
                {
                    ["OUTER"] = Expandable(@"%INNER%\outer"),
                    ["INNER"] = Expandable(@"%ROOT%\inner"),
                    ["ROOT"] = Text(@"C:\new"),
                }
            ),
            null,
            new() { ["ROOT"] = @"C:\old" }
        );

        Assert.Equal(@"C:\new\inner\outer", changes["OUTER"]);
    }

    [Fact]
    public void AReferenceTheProcessKeptResolvesToTheProcessValue()
    {
        var changes = Changes(
            Snapshot(user: new() { ["CARGO_HOME"] = Text(@"C:\old") }),
            Snapshot(user: new() { ["CARGO_HOME"] = Text(@"C:\new"), ["RUSTUP_HOME"] = Expandable(@"%CARGO_HOME%\rustup") }),
            null,
            new() { ["CARGO_HOME"] = @"D:\from-launcher" }
        );

        Assert.Equal(@"D:\from-launcher\rustup", Assert.Single(changes).Value);
    }

    [Fact]
    public void APathEntryReferencingAnUpdatedVariableResolvesToItsNewValue()
    {
        var changes = Changes(
            Snapshot(new() { ["Path"] = Text(@"C:\Windows") }, new() { ["CARGO_HOME"] = Text(@"C:\old") }),
            Snapshot(
                new() { ["Path"] = Text(@"C:\Windows") },
                new() { ["CARGO_HOME"] = Text(@"C:\new"), ["Path"] = Expandable(@"%CARGO_HOME%\bin") }
            ),
            null,
            new() { ["CARGO_HOME"] = @"C:\old", ["Path"] = @"C:\Windows" }
        );

        Assert.Equal(@"C:\Windows;C:\new\bin", changes["Path"]);
    }

    [Fact]
    public void APathEntryWhoseReferenceChangesIsReplaced()
    {
        var changes = Changes(
            Snapshot(new() { ["Path"] = Expandable(@"C:\Windows;%CARGO_HOME%\bin") }, new() { ["CARGO_HOME"] = Text(@"C:\old") }),
            Snapshot(new() { ["Path"] = Expandable(@"C:\Windows;%CARGO_HOME%\bin") }, new() { ["CARGO_HOME"] = Text(@"C:\new") }),
            null,
            new() { ["CARGO_HOME"] = @"C:\old", ["Path"] = @"C:\Bundled;C:\Windows;C:\old\bin" }
        );

        Assert.Equal(@"C:\Bundled;C:\Windows;C:\new\bin", changes["Path"]);
    }

    [Fact]
    public void AnUnchangedValueFollowsAnUpdatedReferenceAcrossRefreshes()
    {
        var first = Snapshot(
            user: new() { ["CARGO_HOME"] = Text(@"C:\old"), ["RUSTUP_HOME"] = Expandable(@"%CARGO_HOME%\rustup") }
        );
        var second = Snapshot(
            user: new() { ["CARGO_HOME"] = Text(@"C:\new"), ["RUSTUP_HOME"] = Expandable(@"%CARGO_HOME%\rustup") }
        );
        var third = Snapshot(user: new() { ["CARGO_HOME"] = Text(@"C:\new"), ["RUSTUP_HOME"] = Text(@"D:\rustup") });
        var process = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CARGO_HOME"] = @"C:\old",
            ["RUSTUP_HOME"] = @"C:\old\rustup",
        };

        var changes = Changes(first, second, null, process);
        Assert.Equal(@"C:\new", changes["CARGO_HOME"]);
        Assert.Equal(@"C:\new\rustup", changes["RUSTUP_HOME"]);

        foreach (var (name, value) in changes)
            process[name] = value!;

        Assert.Equal(@"D:\rustup", Assert.Single(Changes(second, third, null, process)).Value);
    }

    [Fact]
    public void AnUnchangedValueTheProcessOverrodeIsNotRecomputed()
    {
        var changes = Changes(
            Snapshot(user: new() { ["CARGO_HOME"] = Text(@"C:\old"), ["RUSTUP_HOME"] = Expandable(@"%CARGO_HOME%\rustup") }),
            Snapshot(user: new() { ["CARGO_HOME"] = Text(@"C:\new"), ["RUSTUP_HOME"] = Expandable(@"%CARGO_HOME%\rustup") }),
            null,
            new() { ["CARGO_HOME"] = @"C:\old", ["RUSTUP_HOME"] = @"D:\from-launcher" }
        );

        Assert.Equal(@"C:\new", Assert.Single(changes).Value);
    }

    [Fact]
    public void ARuntimePathEntryPersistedLaterKeepsItsSlot()
    {
        var changes = Changes(
            Snapshot(new() { ["Path"] = Text(@"C:\Windows") }),
            Snapshot(new() { ["Path"] = Text(@"C:\Windows;C:\Bundled") }),
            @"C:\Bundled;C:\Windows"
        );

        Assert.Empty(changes);
    }

    [Fact]
    public void ARuntimePathEntryPersistedWithOthersKeepsItsSlot()
    {
        var changes = Changes(
            Snapshot(new() { ["Path"] = Text(@"C:\Windows") }),
            Snapshot(new() { ["Path"] = Text(@"C:\cargo\bin;C:\Windows;C:\Bundled") }),
            @"C:\Bundled;C:\Windows"
        );

        Assert.Equal(@"C:\Bundled;C:\cargo\bin;C:\Windows", Assert.Single(changes).Value);
    }

    [Fact]
    public void AReorderedPathIsAppliedAroundRuntimeEntries()
    {
        var changes = Changes(
            Snapshot(user: new() { ["Path"] = Text(@"C:\old-cargo\bin;C:\cargo\bin") }),
            Snapshot(user: new() { ["Path"] = Text(@"C:\cargo\bin;C:\old-cargo\bin") }),
            @"C:\Bundled;C:\old-cargo\bin;C:\Tools;C:\cargo\bin"
        );

        Assert.Equal(@"C:\Bundled;C:\cargo\bin;C:\Tools;C:\old-cargo\bin", Assert.Single(changes).Value);
    }

    [Theory]
    [InlineData(@"%HOME%\bin", @"C:\me\bin")]
    [InlineData(@"%home%;%MISSING%;%HOME%", @"C:\me;%MISSING%;C:\me")]
    [InlineData("100% done", "100% done")]
    [InlineData("%%HOME%", @"%C:\me")]
    [InlineData("%MISSING%HOME%", @"%MISSINGC:\me")]
    [InlineData("", "")]
    public void ExpandSubstitutesKnownVariablesAndKeepsUnknownOnes(string raw, string expected)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["HOME"] = @"C:\me" };

        Assert.Equal(expected, PersistedEnvironment.Expand(raw, name => environment.GetValueOrDefault(name)));
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

        Assert.Empty(changes);
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
