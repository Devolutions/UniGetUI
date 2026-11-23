using UniGetUI.Avalonia.ViewModels.Pages.AboutPages;
using UniGetUI.Core.Data;

namespace UniGetUI.Tests;

public class ThirdPartyLicensesViewModelTests
{
    [Fact]
    public void Constructor_ListsEveryComponent()
    {
        var viewModel = new ThirdPartyLicensesViewModel([
            new ThirdPartyComponent { Name = "Avalonia", Version = "12.0.4", License = "MIT" },
            new ThirdPartyComponent { Name = "rustls", Version = "0.23.45", License = "Apache-2.0 OR ISC OR MIT" },
        ]);

        Assert.Equal(["Avalonia", "rustls"], viewModel.Licenses.Select(item => item.Name));
        Assert.True(viewModel.HasLicenses);
    }

    [Fact]
    public void Constructor_WithNoComponents_ReportsMissingNotices()
    {
        var viewModel = new ThirdPartyLicensesViewModel([]);

        Assert.Empty(viewModel.Licenses);
        Assert.False(viewModel.HasLicenses);
    }

    [Fact]
    public void ToggleLicense_ShowsCopyrightLicenseAndNoticeOnlyWhileExpanded()
    {
        var item = new ThirdPartyComponentItem(new ThirdPartyComponent
        {
            Name = "aws-lc-sys",
            Version = "0.45.0",
            License = "ISC",
            Copyright = "Copyright Amazon.com, Inc.",
            LicenseText = "ISC license text",
            Notice = new ThirdPartyNotice { Text = "AWS-LC notice" },
        });

        Assert.Null(item.ExpandedText);

        item.ToggleLicenseCommand.Execute(null);

        Assert.True(item.IsExpanded);
        Assert.Equal(
            string.Join(Environment.NewLine, "Copyright Amazon.com, Inc.", "", "ISC license text", "", "AWS-LC notice"),
            item.ExpandedText);

        item.ToggleLicenseCommand.Execute(null);

        Assert.Null(item.ExpandedText);
    }

    [Theory]
    [InlineData("https://github.com/rustls/rustls", true)]
    [InlineData("http://example.com/project", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void SourceUrl_AcceptsOnlyWebLinks(string source, bool hasSource)
    {
        var item = new ThirdPartyComponentItem(new ThirdPartyComponent { Name = "x", Source = source });

        Assert.Equal(hasSource, item.HasSource);
        Assert.Equal(hasSource, item.SourceUrl is not null);
    }
}
