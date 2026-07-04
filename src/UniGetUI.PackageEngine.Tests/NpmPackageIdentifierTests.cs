using UniGetUI.PackageEngine.Managers.NpmManager;

namespace UniGetUI.PackageEngine.Tests;

public sealed class NpmPackageIdentifierTests
{
    [Fact]
    public void ParseSeparatesAliasComponents()
    {
        var identifier = NpmPackageIdentifier.Parse("eslint-v9:eslint@^9.39.4");

        Assert.True(identifier.IsAlias);
        Assert.Equal("eslint-v9", identifier.LocalName);
        Assert.Equal("eslint", identifier.TargetName);
        Assert.Equal("eslint-v9@npm:eslint@10.6.0", identifier.GetInstallSpec("10.6.0"));
        Assert.Equal("eslint", identifier.GetRegistryName());
        Assert.Equal("eslint-v9", identifier.GetInstallLocationName());
    }

    [Fact]
    public void ParseHandlesScopedAliasTargets()
    {
        var identifier = NpmPackageIdentifier.Parse("babel-core-legacy:@babel/core@^7.20.0");

        Assert.True(identifier.IsAlias);
        Assert.Equal("babel-core-legacy", identifier.LocalName);
        Assert.Equal("@babel/core", identifier.TargetName);
        Assert.Equal("babel-core-legacy@npm:@babel/core@7.28.0", identifier.GetInstallSpec("7.28.0"));
        Assert.Equal("@babel/core", identifier.GetRegistryName());
        Assert.Equal("babel-core-legacy", identifier.GetInstallLocationName());
    }

    [Fact]
    public void ParseLeavesOrdinaryNamesUntouched()
    {
        var identifier = NpmPackageIdentifier.Parse("contoso-tool");

        Assert.False(identifier.IsAlias);
        Assert.Equal("contoso-tool", identifier.LocalName);
        Assert.Equal("contoso-tool", identifier.TargetName);
        Assert.Equal("contoso-tool@2.0.0", identifier.GetInstallSpec("2.0.0"));
        Assert.Equal("contoso-tool", identifier.GetRegistryName());
        Assert.Equal("contoso-tool", identifier.GetInstallLocationName());
    }
}
