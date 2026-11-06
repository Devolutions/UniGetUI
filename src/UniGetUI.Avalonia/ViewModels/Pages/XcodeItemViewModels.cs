using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.Xcode;

namespace UniGetUI.Avalonia.ViewModels.Pages;

/// <summary>Turns what xcodes reports about an Xcode into the words the page shows.</summary>
internal static class XcodeText
{
    public static string Architecture(XcodeArchitecture architecture) => architecture switch
    {
        XcodeArchitecture.AppleSilicon => CoreTools.Translate("Apple Silicon"),
        XcodeArchitecture.Intel => CoreTools.Translate("Intel"),
        XcodeArchitecture.Universal => CoreTools.Translate("Universal"),
        _ => "",
    };

    /// <summary>"Build 17E192 · Apple Silicon"; whichever of them xcodes gave.</summary>
    public static string Details(string? build, XcodeArchitecture architecture)
    {
        List<string> parts = [];
        if (build is { Length: > 0 })
            parts.Add(CoreTools.Translate("Build {0}", build));
        if (Architecture(architecture) is { Length: > 0 } processors)
            parts.Add(processors);
        return string.Join(" · ", parts);
    }
}

/// <summary>An Xcode that is on this Mac.</summary>
public partial class InstalledXcodeItemViewModel(InstalledXcode xcode) : ViewModelBase
{
    public InstalledXcode Xcode { get; } = xcode;

    public string Version => Xcode.Version;
    public string Title => CoreTools.Translate("Xcode {0}", Xcode.Version);
    public string Location => Xcode.Path;
    public string DetailText => XcodeText.Details(Xcode.Build, Xcode.Architecture);

    /// <summary>The Xcode command line tools such as xcodebuild use.</summary>
    public bool IsSelected => Xcode.IsSelected;
    public bool CanSelect => !Xcode.IsSelected;
    public bool IsPrerelease => Xcode.IsPrerelease;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;
}

/// <summary>An Xcode that can be installed.</summary>
public sealed class AvailableXcodeItemViewModel(XcodeRelease release)
{
    public XcodeRelease Release { get; } = release;

    public string Version => Release.Version;
    public string Title => CoreTools.Translate("Xcode {0}", Release.Version);
    public string DetailText => XcodeText.Details(Release.Build, Release.Architecture);
    public bool IsPrerelease => Release.IsPrerelease;
}
