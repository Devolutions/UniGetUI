using Xunit;

namespace UniGetUI.Core.Tools.Tests;

public class PackageFieldValidationTests
{
    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4")]
    [InlineData("2.0.0-preview1")]
    [InlineData("1.2.3-alpha.1+build.5")]
    [InlineData("1:2.3.4-1ubuntu0.1~esm1")]
    [InlineData("1.2.3_1")]
    [InlineData("2.0.post1")]
    [InlineData("v1.0.0")]
    [InlineData("20240101")]
    public void IsValidPackageVersion_AcceptsRealVersions(string version)
    {
        Assert.True(CoreTools.IsValidPackageVersion(version));
    }

    [Theory]
    [InlineData("1.2.3.4.5; Start-Process calc")]
    [InlineData("1.0.0 & calc")]
    [InlineData("1.0.0`ncalc")]
    [InlineData("1.0.0$(calc)")]
    [InlineData("1.0.0'; calc; '")]
    [InlineData("1.0.0\" ; calc")]
    [InlineData("1.0 --index-url http://evil.example")]
    [InlineData("1.0.0 | calc")]
    [InlineData("-Version")]
    [InlineData("")]
    public void IsValidPackageVersion_RejectsInjectionPayloads(string version)
    {
        Assert.False(CoreTools.IsValidPackageVersion(version));
    }

    [Fact]
    public void IsValidPackageVersion_RejectsOverlyLongValues()
    {
        Assert.False(CoreTools.IsValidPackageVersion(new string('1', CoreTools.MaxPackageVersionLength + 1)));
        Assert.True(CoreTools.IsValidPackageVersion(new string('1', CoreTools.MaxPackageVersionLength)));
    }

    [Theory]
    [InlineData("powershell-yaml")]
    [InlineData("Devolutions.PowerShell")]
    [InlineData("@babel/core")]
    [InlineData("main/git")]
    [InlineData("eslint-v9:eslint@^9.x")]
    [InlineData("awscli@2")]
    [InlineData("zlib:x64-windows")]
    [InlineData("libstdc++6")]
    [InlineData("zope.interface")]
    public void IsValidPackageIdentifier_AcceptsRealIdentifiers(string identifier)
    {
        Assert.True(CoreTools.IsValidPackageIdentifier(identifier));
    }

    [Theory]
    [InlineData("powershell-yaml; Start-Process calc")]
    [InlineData("pkg & calc")]
    [InlineData("pkg$(calc)")]
    [InlineData("pkg'; calc; '")]
    [InlineData("pkg`ncalc")]
    [InlineData("-Name")]
    [InlineData("@")]
    [InlineData("@/etc")]
    [InlineData(@"MSIX\Microsoft.Foo_1.0_x64__8wekyb3d8bbwe")]
    [InlineData("{e46eca4f-393b-40df-9f49-076faf788d83}")]
    [InlineData("")]
    public void IsValidPackageIdentifier_RejectsUnsafeIdentifiers(string identifier)
    {
        Assert.False(CoreTools.IsValidPackageIdentifier(identifier));
    }

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("2021 Update", false)]
    [InlineData("1.2.3; calc", false)]
    [InlineData("1.0$(calc)", false)]
    [InlineData("{e46eca4f-393b-40df-9f49-076faf788d83}", false)]
    public void IsCommandLineInertValue_FlagsValuesThatCanAlterACommandLine(string value, bool inert)
    {
        Assert.Equal(inert, CoreTools.IsCommandLineInertValue(value));
    }
}
