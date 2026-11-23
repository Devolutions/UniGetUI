namespace UniGetUI.Core.Data.Tests;

public sealed class ThirdPartyNoticesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ugui-notices-" + Guid.NewGuid().ToString("N"));

    public ThirdPartyNoticesTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }

    [Fact]
    public void Load_ReadsTheGeneratorSchemaSortedByName()
    {
        string path = Path.Combine(_directory, ThirdPartyNotices.JsonFileName);
        File.WriteAllText(path, """
            {
              "format": "devolutions-third-party-notices/1",
              "appName": "UniGetUI",
              "appVersion": "2026.1.0",
              "components": [
                {
                  "name": "YamlDotNet",
                  "version": "18.1.0",
                  "ecosystem": "nuget",
                  "license": "MIT",
                  "source": "https://github.com/aaubry/YamlDotNet",
                  "download": "https://www.nuget.org/packages/YamlDotNet/18.1.0",
                  "repository": "https://github.com/aaubry/YamlDotNet",
                  "website": "",
                  "copyright": "Copyright (c) Antoine Aubry and contributors",
                  "licenseText": "MIT text",
                  "licenseTextOrigin": "package",
                  "isRuntime": false,
                  "notice": null
                },
                {
                  "name": "aws-lc-sys",
                  "version": "0.45.0",
                  "ecosystem": "manual",
                  "license": "ISC AND Apache-2.0",
                  "source": "https://github.com/aws/aws-lc-rs",
                  "copyright": "Copyright Amazon.com, Inc. or its affiliates.",
                  "licenseText": "ISC text",
                  "isRuntime": false,
                  "notice": { "repository": "aws/aws-lc-rs", "file": "NOTICE", "url": "", "text": "AWS-LC notice" },
                  "sharedTexts": [ { "id": "GPL-3.0", "text": "unused" } ]
                },
                { "name": "", "version": "1.0" }
              ]
            }
            """);

        IReadOnlyList<ThirdPartyComponent> components = ThirdPartyNotices.Load(path);

        Assert.Equal(["aws-lc-sys", "YamlDotNet"], components.Select(c => c.Name));
        Assert.Equal("ISC AND Apache-2.0", components[0].License);
        Assert.Equal("AWS-LC notice", components[0].Notice?.Text);
        Assert.Equal("https://github.com/aaubry/YamlDotNet", components[1].Source);
        Assert.Equal("Copyright (c) Antoine Aubry and contributors", components[1].Copyright);
        Assert.Equal("MIT text", components[1].LicenseText);
        Assert.Null(components[1].Notice);
    }

    [Fact]
    public void Load_ReadsTheCommittedPingetCrateNotices()
    {
        string? directory = AppContext.BaseDirectory;
        string? path = null;
        while (directory is not null && path is null)
        {
            string candidate = Path.Combine(directory, "scripts", "third-party-licenses", "config", "pinget-crates.json");
            path = File.Exists(candidate) ? candidate : null;
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(path);
        IReadOnlyList<ThirdPartyComponent> components = ThirdPartyNotices.Load(path);

        Assert.NotEmpty(components);
        Assert.All(components, component =>
        {
            Assert.False(string.IsNullOrWhiteSpace(component.Version), component.Name);
            Assert.False(string.IsNullOrWhiteSpace(component.License), component.Name);
        });
        Assert.Contains(components, component => component.Name == "rustls" && component.LicenseText.Length > 0);
    }

    [Fact]
    public void Load_ReturnsNothingWhenTheFileIsMissing()
    {
        Assert.Empty(ThirdPartyNotices.Load(Path.Combine(_directory, "missing.json")));
    }

    [Fact]
    public void Load_ReturnsNothingWhenTheFileIsNotJson()
    {
        string path = Path.Combine(_directory, ThirdPartyNotices.JsonFileName);
        File.WriteAllText(path, "not json");

        Assert.Empty(ThirdPartyNotices.Load(path));
    }
}
