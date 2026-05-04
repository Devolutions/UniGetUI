using UniGetUI.Interface;

namespace UniGetUI.Tests;

public sealed class IpcCliSyntaxTests
{
    private static string GetCommand(IpcCliParseResult result)
    {
        return Assert.IsType<string>(result.Command);
    }

    private static string[] GetEffectiveArgs(IpcCliParseResult result)
    {
        return Assert.IsType<string[]>(result.EffectiveArgs);
    }

    [Fact]
    public void ParseMapsTopLevelStatusCommand()
    {
        IpcCliParseResult result = IpcCliSyntax.Parse(["status"]);

        Assert.Equal(IpcCliParseStatus.Success, result.Status);
        Assert.Equal("status", GetCommand(result));
        Assert.Equal([], GetEffectiveArgs(result));
    }

    [Fact]
    public void ParsePreservesLeadingTransportOverrides()
    {
        IpcCliParseResult result = IpcCliSyntax.Parse(
            ["--transport", "named-pipe", "--pipe-name", "probe-1", "status"]
        );

        Assert.Equal(IpcCliParseStatus.Success, result.Status);
        Assert.Equal("status", GetCommand(result));
        Assert.Equal(["--transport", "named-pipe", "--pipe-name", "probe-1"], GetEffectiveArgs(result));
    }

    [Fact]
    public void ParseMapsOperationIdAlias()
    {
        IpcCliParseResult result = IpcCliSyntax.Parse(
            ["operation", "wait", "--id", "op-123", "--timeout", "30"]
        );

        Assert.Equal(IpcCliParseStatus.Success, result.Status);
        Assert.Equal("wait-operation", GetCommand(result));
        Assert.Equal(["--operation-id", "op-123", "--timeout", "30"], GetEffectiveArgs(result));
    }

    [Fact]
    public void ParseMapsPackageAliases()
    {
        IpcCliParseResult result = IpcCliSyntax.Parse(
            ["package", "details", "--manager", "dotnet-tool", "--id", "dotnetsay", "--source", "nuget.org"]
        );

        Assert.Equal(IpcCliParseStatus.Success, result.Status);
        Assert.Equal("package-details", GetCommand(result));
        Assert.Equal(
            ["--manager", "dotnet-tool", "--package-id", "dotnetsay", "--package-source", "nuget.org"],
            GetEffectiveArgs(result)
        );
    }

    [Fact]
    public void ParseMapsNestedBackupCommands()
    {
        IpcCliParseResult result = IpcCliSyntax.Parse(
            ["backup", "github", "login", "start", "--launch-browser"]
        );

        Assert.Equal(IpcCliParseStatus.Success, result.Status);
        Assert.Equal("start-github-sign-in", GetCommand(result));
        Assert.Equal(["--launch-browser"], GetEffectiveArgs(result));
    }

    [Fact]
    public void ParseMapsManagerNotificationSubcommands()
    {
        IpcCliParseResult result = IpcCliSyntax.Parse(
            ["manager", "notifications", "disable", "--manager", "dotnet-tool"]
        );

        Assert.Equal(IpcCliParseStatus.Success, result.Status);
        Assert.Equal("set-manager-update-notifications", GetCommand(result));
        Assert.Equal(["--enabled", "false", "--manager", "dotnet-tool"], GetEffectiveArgs(result));
    }

    [Fact]
    public void ParseMapsSecureSettingsDomain()
    {
        IpcCliParseResult result = IpcCliSyntax.Parse(
            ["settings", "secure", "list", "--user", "alice"]
        );

        Assert.Equal(IpcCliParseStatus.Success, result.Status);
        Assert.Equal("list-secure-settings", GetCommand(result));
        Assert.Equal(["--user", "alice"], GetEffectiveArgs(result));
    }

    [Fact]
    public void ParseMapsSourceDocumentationAliases()
    {
        IpcCliParseResult result = IpcCliSyntax.Parse(
            ["source", "add", "--manager", "dotnet-tool", "--source-name", "nuget.org", "--source-url", "https://api.nuget.org/v3/index.json"]
        );

        Assert.Equal(IpcCliParseStatus.Success, result.Status);
        Assert.Equal("add-source", GetCommand(result));
        Assert.Equal(
            ["--manager", "dotnet-tool", "--name", "nuget.org", "--url", "https://api.nuget.org/v3/index.json"],
            GetEffectiveArgs(result)
        );
    }

    [Fact]
    public void ParseTreatsHelpAsCliHelp()
    {
        IpcCliParseResult result = IpcCliSyntax.Parse(["help"]);

        Assert.Equal(IpcCliParseStatus.Help, result.Status);
    }

    [Fact]
    public void HasVerbCommandReturnsTrueForVerbInvocation()
    {
        Assert.True(IpcCliSyntax.HasVerbCommand(["package", "search", "--manager", "npm"]));
    }

    [Fact]
    public void HasVerbCommandReturnsTrueForHelpVerb()
    {
        Assert.True(IpcCliSyntax.HasVerbCommand(["help"]));
    }

    [Fact]
    public void HasVerbCommandReturnsTrueAfterLeadingTransportOverride()
    {
        Assert.True(IpcCliSyntax.HasVerbCommand(["--transport", "named-pipe", "status"]));
    }

    [Fact]
    public void HasVerbCommandReturnsFalseForStartupParameter()
    {
        Assert.False(IpcCliSyntax.HasVerbCommand(["--daemon"]));
    }

    [Fact]
    public void HasVerbCommandReturnsFalseForGlobalHelpFlag()
    {
        Assert.False(IpcCliSyntax.HasVerbCommand(["--help"]));
    }

    [Fact]
    public void HasVerbCommandReturnsFalseForShortHelpFlag()
    {
        Assert.False(IpcCliSyntax.HasVerbCommand(["-h"]));
    }

    [Fact]
    public void HasVerbCommandReturnsFalseForHeadlessStartup()
    {
        Assert.False(IpcCliSyntax.HasVerbCommand(["--headless"]));
    }

    [Fact]
    public void HasVerbCommandReturnsFalseForUnknownBareToken()
    {
        Assert.False(IpcCliSyntax.HasVerbCommand(["foo"]));
    }
}
