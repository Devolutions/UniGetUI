namespace UniGetUI.Core.Tools.Tests;

public class InstallerHostDisplayTests
{
    [Fact]
    public void FromUrlsReturnsTheLowercasedHostOfASingleUrl()
    {
        Assert.Equal(
            "github.com",
            InstallerHostDisplay.FromUrls(["https://GitHub.com/org/app/releases/app.exe"])
        );
    }

    [Fact]
    public void FromUrlsListsEveryDistinctHostAlphabetically()
    {
        Assert.Equal(
            "cdn.example.test, github.com",
            InstallerHostDisplay.FromUrls(
                [
                    "https://github.com/org/app/app-x64.exe",
                    "https://cdn.example.test/app-arm64.exe",
                    "https://github.com/org/app/app-x86.exe",
                ]
            )
        );
    }

    [Fact]
    public void FromUrlsKeepsSubdomainsAndPunycodeAsWritten()
    {
        Assert.Equal(
            "www.example.test, xn--e1afmkfd.test",
            InstallerHostDisplay.FromUrls(
                ["https://www.example.test/app.msi", "https://пример.test/app.msi"]
            )
        );
    }

    [Fact]
    public void FromUrlsSkipsEmptyAndNonAbsoluteUrls()
    {
        Assert.Equal(
            "example.test",
            InstallerHostDisplay.FromUrls(["", "   ", null, "app.exe", "https://example.test/app.exe"])
        );
    }

    [Fact]
    public void FromUrlsReturnsAnEmptyStringWhenNothingResolves()
    {
        Assert.Equal("", InstallerHostDisplay.FromUrls(null));
        Assert.Equal("", InstallerHostDisplay.FromUrls([]));
        Assert.Equal("", InstallerHostDisplay.FromUrls(["not a url"]));
    }

    [Fact]
    public void JoinUrlsListsDistinctUrlsOnSeparateLines()
    {
        Assert.Equal(
            "https://example.test/a.exe\nhttps://example.test/b.exe",
            InstallerHostDisplay.JoinUrls(
                [
                    "https://example.test/a.exe",
                    "https://EXAMPLE.test/A.exe",
                    " https://example.test/b.exe ",
                ]
            )
        );
    }

    [Fact]
    public void JoinUrlsTruncatesLongInstallerLists()
    {
        string[] urls =
        [
            "https://example.test/1.exe",
            "https://example.test/2.exe",
            "https://example.test/3.exe",
            "https://example.test/4.exe",
            "https://example.test/5.exe",
            "https://example.test/6.exe",
            "https://example.test/7.exe",
        ];

        string joined = InstallerHostDisplay.JoinUrls(urls);

        Assert.EndsWith("\n\u2026", joined);
        Assert.DoesNotContain("7.exe", joined);
        Assert.Equal(7, joined.Split('\n').Length);
    }
}
