using System.Diagnostics;
using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public sealed class MacAppSignatureTests
{
    private const string TeamRequirement =
        "anchor apple generic and certificate leaf[field.1.2.840.113635.100.6.1.13] exists "
        + "and (certificate leaf[subject.OU] = \"N592S9ASDB\")";

    [Fact]
    public void Requirement_PinsADeveloperIdCertificateOfTheTeams()
    {
        Assert.Equal(TeamRequirement, MacAppSignature.Requirement(["N592S9ASDB"]));
        Assert.EndsWith(
            "(certificate leaf[subject.OU] = \"N592S9ASDB\" or certificate leaf[subject.OU] = \"ABCDE12345\")",
            MacAppSignature.Requirement(["N592S9ASDB", "ABCDE12345"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("N592S9ASD")]
    [InlineData("N592S9ASD\"")]
    [InlineData("N592S9AS B")]
    public void Requirement_RefusesSomethingThatIsNotATeamId(string teamId)
    {
        Assert.Throws<ArgumentException>(() => MacAppSignature.Requirement([teamId]));
    }

    [Fact]
    public void Requirement_RefusesAnEmptyTeamList()
    {
        Assert.Throws<ArgumentException>(() => MacAppSignature.Requirement([]));
    }

    [Fact]
    public void Verify_ChecksTheWholeBundleStrictlyAgainstTheRequirement()
    {
        List<IReadOnlyList<string>> calls = [];

        bool verified = MacAppSignature.Verify(
            "/tmp/UniGetUI.app",
            TeamRequirement,
            (tool, args) =>
            {
                Assert.Equal(MacAppSignature.CodeSign, tool);
                calls.Add(args);
                return (0, "");
            },
            out string problem);

        Assert.True(verified, problem);
        Assert.Equal(["--verify", "--deep", "--strict", $"-R={TeamRequirement}", "/tmp/UniGetUI.app"], Assert.Single(calls));
    }

    [Fact]
    public void Verify_RefusesWhenCodeSignRejectsTheBundle()
    {
        bool verified = MacAppSignature.Verify(
            "/tmp/UniGetUI.app",
            TeamRequirement,
            (_, _) => (1, "a sealed resource is missing or invalid"),
            out string problem);

        Assert.False(verified);
        Assert.Contains("a sealed resource is missing or invalid", problem);
    }

    [Fact]
    public void Verify_RefusesWhenTheSignatureCannotBeChecked()
    {
        bool verified = MacAppSignature.Verify(
            "/tmp/UniGetUI.app",
            TeamRequirement,
            (_, _) => throw new InvalidOperationException("codesign is missing"),
            out string problem);

        Assert.False(verified);
        Assert.Contains("codesign is missing", problem);
    }

    [Fact]
    public void ReadShortVersion_ReturnsNothingWhenTheVersionCannotBeRead()
    {
        Assert.Null(MacAppSignature.ReadShortVersion("/tmp/UniGetUI.app", (_, _) => (1, "No value at that key path")));
        Assert.Null(MacAppSignature.ReadShortVersion("/tmp/UniGetUI.app", (_, _) => throw new IOException()));
        Assert.Equal("2026.4.0", MacAppSignature.ReadShortVersion("/tmp/UniGetUI.app", (_, _) => (0, "2026.4.0\n")));
    }

    [MacOSFact]
    public void Verify_RefusesAnAppleSignedAppThroughTheRealCodeSign()
    {
        Assert.False(MacAppSignature.Verify("/System/Applications/Calculator.app", TeamRequirement, out string problem));
        Assert.Contains("not signed by a trusted team", problem);
    }

    [MacOSFact]
    public void Verify_AcceptsASignedBundleAndRefusesItOnceAFileChanges()
    {
        string root = Directory.CreateTempSubdirectory("unigetui-signature-").FullName;
        try
        {
            string app = SignedBundle(root);
            string requirement = $"identifier \"{FixtureIdentifier}\"";

            Assert.True(MacAppSignature.Verify(app, requirement, out string problem), problem);
            Assert.Equal("2026.4.0", MacAppSignature.ReadShortVersion(app));
            Assert.False(MacAppSignature.Verify(app, "identifier \"com.example.other\"", out _));

            File.WriteAllText(Path.Join(app, "Contents", "MacOS", "data.json"), "{\"tampered\": true}");
            Assert.False(MacAppSignature.Verify(app, requirement, out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const string FixtureIdentifier = "com.devolutions.unigetui.signaturetest";

    // An ad-hoc signed bundle whose main executable is a script, signed the way the release pipeline signs.
    private static string SignedBundle(string root)
    {
        string app = Path.Join(root, "UniGetUI.app");
        string macOs = Path.Join(app, "Contents", "MacOS");
        Directory.CreateDirectory(macOs);
        File.WriteAllText(
            Path.Join(app, "Contents", "Info.plist"),
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
            <key>CFBundleIdentifier</key><string>{FixtureIdentifier}</string>
            <key>CFBundleExecutable</key><string>UniGetUI</string>
            <key>CFBundleShortVersionString</key><string>2026.4.0</string>
            </dict></plist>
            """);
        File.WriteAllText(Path.Join(macOs, "UniGetUI"), "#!/bin/sh\n");
        File.SetUnixFileMode(Path.Join(macOs, "UniGetUI"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Join(macOs, "data.json"), "{}");

        Sign(Path.Join(macOs, "data.json"));
        Sign(app);
        return app;
    }

    private static void Sign(string path)
    {
        using Process codesign = Process.Start(MacAppSignature.CodeSign, ["--force", "--sign", "-", path])!;
        codesign.WaitForExit();
        Assert.Equal(0, codesign.ExitCode);
    }
}
