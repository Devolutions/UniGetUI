using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Extensions.Drivers;

namespace UniGetUI.Tests;

public sealed class DriverItemDetailsTests
{
    private static DriverUpdate Sample(string driverClass = "Display", string supportUrl = "", string description = "Fixes flickering on external monitors") => new()
    {
        Id = "8c3e5f0a-6b1d-4e2f-9a7b-1c2d3e4f5a6b",
        Revision = 201,
        Title = "Contoso - Display - 31.0.101.5590",
        Description = description,
        DriverClass = driverClass,
        Manufacturer = "Contoso",
        Model = "Contoso Graphics 770",
        Provider = "Contoso Corporation",
        HardwareId = @"PCI\VEN_1234&DEV_5678",
        DriverDate = "2026-09-14",
        Size = 734003200,
        RebootBehavior = DriverRebootBehavior.AlwaysRequiresReboot,
        SupportUrl = supportUrl,
        DeviceName = "Contoso Graphics 770",
        InstalledVersion = "31.0.101.4502",
        InstalledDate = "2025-06-02",
    };

    [Theory]
    [InlineData("Display", "\uE7F4")]
    [InlineData("net", "\uE968")]
    [InlineData("Bluetooth", "\uE702")]
    [InlineData("MEDIA", "\uE7F5")]
    [InlineData("AudioEndpoint", "\uE7F5")]
    [InlineData("Camera", "\uE722")]
    [InlineData("HIDClass", "\uE962")]
    [InlineData(" Printer ", "\uE749")]
    [InlineData("System", null)]
    [InlineData("Firmware", null)]
    [InlineData("", null)]
    public void EachDeviceClassGetsItsIcon(string driverClass, string? glyph)
    {
        var item = new DriverUpdateItemViewModel(Sample(driverClass));

        Assert.Equal(glyph, item.ClassGlyph);
        Assert.Equal(glyph is not null, item.HasClassGlyph);
    }

    [Fact]
    public void TheDetailsListWhatWindowsUpdateReported()
    {
        var item = new DriverUpdateItemViewModel(Sample());

        Dictionary<string, DriverDetailRow> rows = item.Details.ToDictionary(row => row.Label);

        Assert.Equal("Contoso Corporation", rows["Driver provider"].Value);
        Assert.Equal("31.0.101.4502 (2025-06-02)", rows["Installed driver"].Value);
        Assert.Equal("Dated 2026-09-14", rows["New driver"].Value);
        Assert.Equal("Required after installing", rows["Restart"].Value);
        Assert.Equal(@"PCI\VEN_1234&DEV_5678", rows["Hardware ID"].CopyValue);
        Assert.Equal("8c3e5f0a-6b1d-4e2f-9a7b-1c2d3e4f5a6b (revision 201)", rows["Windows Update ID"].Value);
        Assert.Equal("8c3e5f0a-6b1d-4e2f-9a7b-1c2d3e4f5a6b", rows["Windows Update ID"].CopyValue);
        Assert.False(rows["Manufacturer"].CanCopy);
        Assert.Equal("Fixes flickering on external monitors", item.Description);
        Assert.True(item.HasDescription);
        Assert.False(item.IsExpanded);
    }

    [Fact]
    public void EmptyFieldsAreLeftOutAndATitleRepeatedAsDescriptionIsHidden()
    {
        DriverUpdate update = Sample(description: "Contoso - Display - 31.0.101.5590") with
        {
            Manufacturer = "",
            DeviceName = null,
            InstalledVersion = null,
            InstalledDate = null,
        };

        var item = new DriverUpdateItemViewModel(update);

        Assert.DoesNotContain(item.Details, row => row.Label is "Manufacturer" or "Device");
        Assert.Equal("Unknown", item.Details.Single(row => row.Label == "Installed driver").Value);
        Assert.False(item.HasDescription);
    }

    [Theory]
    [InlineData("https://support.contoso.com/drivers", "https://support.contoso.com/drivers")]
    [InlineData("  http://contoso.com/x  ", "http://contoso.com/x")]
    [InlineData("file:///C:/Windows/System32/cmd.exe", null)]
    [InlineData("ms-settings:windowsupdate", null)]
    [InlineData("not a url", null)]
    [InlineData("", null)]
    public void OnlyWebLinksAreOfferedAsMoreInformation(string supportUrl, string? expected)
    {
        var item = new DriverUpdateItemViewModel(Sample(supportUrl: supportUrl));

        Assert.Equal(expected, item.MoreInformationUrl);
        Assert.Equal(expected is not null, item.HasMoreInformation);
    }
}
