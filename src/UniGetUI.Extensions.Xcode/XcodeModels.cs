using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace UniGetUI.Extensions.Xcode;

/// <summary>
/// An Xcode version as Apple names it: <c>26.4</c>, <c>26.4.1</c>, <c>26.1 Beta 2</c>,
/// <c>26.1 Release Candidate</c>, <c>11.2 GM seed</c>. Ordered the way Apple ships them: betas, then
/// release candidates, then golden masters, then the release itself.
/// </summary>
public readonly partial record struct XcodeVersion(int Major, int Minor, int Patch, int Stage, int Iteration)
    : IComparable<XcodeVersion>
{
    // Lowest first: any stage not named here sorts below a beta.
    private const int OtherPrerelease = 0;
    private const int Beta = 1;
    private const int ReleaseCandidate = 2;
    private const int GoldenMasterSeed = 3;
    private const int GoldenMaster = 4;
    private const int Release = 5;

    public bool IsPrerelease => Stage != Release;

    public static bool TryParse(string? text, out XcodeVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text) || Pattern().Match(text) is not { Success: true } match)
            return false;

        string label = match.Groups["label"].Success ? match.Groups["label"].Value.Trim().ToLowerInvariant() : "";
        bool hasNumber = match.Groups["number"].Success;
        // "26.4 3" names no release.
        if (label.Length == 0 && hasNumber)
            return false;

        int stage = label.Length == 0 ? Release
            : label.Contains("beta") ? Beta
            : label.Contains("candidate") || label == "rc" ? ReleaseCandidate
            : label.Contains("seed") ? GoldenMasterSeed
            : label.StartsWith("gm", StringComparison.Ordinal) ? GoldenMaster
            : OtherPrerelease;

        version = new XcodeVersion(
            Number(match, "major"),
            Number(match, "minor"),
            Number(match, "patch"),
            stage,
            hasNumber ? Number(match, "number") : 0);
        return true;
    }

    /// <summary>Compares two version texts; text that is not a version sorts below any that is.</summary>
    public static int Compare(string? left, string? right)
    {
        bool leftParsed = TryParse(left, out XcodeVersion leftVersion);
        bool rightParsed = TryParse(right, out XcodeVersion rightVersion);
        if (leftParsed && rightParsed) return leftVersion.CompareTo(rightVersion);
        if (leftParsed) return 1;
        if (rightParsed) return -1;
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Compares the numeric product version while ignoring release or prerelease labels.</summary>
    public static bool SameProductVersion(string? left, string? right)
        => TryParse(left, out XcodeVersion first) && TryParse(right, out XcodeVersion second)
           && first.Major == second.Major && first.Minor == second.Minor && first.Patch == second.Patch;

    public int CompareTo(XcodeVersion other)
    {
        int result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        result = Stage.CompareTo(other.Stage);
        return result != 0 ? result : Iteration.CompareTo(other.Iteration);
    }

    private static int Number(Match match, string group)
        => match.Groups[group].Success
            && int.TryParse(match.Groups[group].Value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : 0;

    [GeneratedRegex(
        @"^\s*(?:Xcode\s+)?(?<major>\d{1,4})(?:\.(?<minor>\d{1,4}))?(?:\.(?<patch>\d{1,4}))?"
        + @"(?:\s+(?<label>[A-Za-z][A-Za-z ]*?))?(?:\s+(?<number>\d{1,4}))?\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>Which processors a downloaded Xcode runs on.</summary>
public enum XcodeArchitecture
{
    Unspecified,
    Universal,
    AppleSilicon,
    Intel,
}

/// <summary>An SDK identity supplied by a public catalog, including its Apple build number.</summary>
public sealed record XcodeSdkIdentity(string Platform, string Version, string Build)
{
    internal static bool TryCreate(string platform, string? version, string? build,
        [NotNullWhen(true)] out XcodeSdkIdentity? identity)
    {
        identity = null;
        if (platform is not ("iOS" or "watchOS" or "tvOS" or "visionOS")
            || string.IsNullOrEmpty(version)
            || version.Any(character => !char.IsAsciiDigit(character) && character != '.')
            || !XcodeVersion.TryParse(version, out XcodeVersion parsed) || parsed.IsPrerelease
            || string.IsNullOrEmpty(build) || !XcodeSimulatorImageFile.IsValidBuild(build))
            return false;

        string normalized = parsed.Patch == 0
            ? FormattableString.Invariant($"{parsed.Major}.{parsed.Minor}")
            : FormattableString.Invariant($"{parsed.Major}.{parsed.Minor}.{parsed.Patch}");
        identity = new XcodeSdkIdentity(platform, normalized, build);
        return true;
    }
}

/// <summary>An Xcode that can be installed.</summary>
/// <param name="Version">The version as shown to the user, such as <c>26.1 Beta 2</c>.</param>
public sealed record XcodeRelease(
    string Version,
    string? Build,
    XcodeArchitecture Architecture,
    bool IsInstalled,
    bool IsSelected)
{
    public bool IsPrerelease => XcodeVersion.TryParse(Version, out XcodeVersion parsed) && parsed.IsPrerelease;

    /// <summary>The archive cannot yet be matched to a valid exact build.</summary>
    public bool IsIdentityUnverified { get; init; }

    /// <summary>The archive size from Apple's listing, when its address matches this exact release.</summary>
    public int? AppleSizeMB { get; init; }
}

/// <summary>The installed path and bundle identity verified at the end of an installation.</summary>
public sealed record XcodeInstallationResult(string Path, string Build, XcodeArchitecture Architecture);

/// <summary>An Xcode on this Mac.</summary>
public sealed record InstalledXcode(
    string Version,
    string? Build,
    XcodeArchitecture Architecture,
    bool IsSelected,
    string Path)
{
    public bool IsPrerelease => XcodeVersion.TryParse(Version, out XcodeVersion parsed) && parsed.IsPrerelease;
}

/// <summary>The step an installation has reached, from lines such as <c>(1/6) Downloading Xcode 26.4: 42%</c>.</summary>
/// <param name="Fraction">How far through the step it is, when it says (only a download does).</param>
public sealed record XcodeProgress(int Step, int StepCount, string Text, double? Fraction);

/// <summary>What an installation does besides installing, as chosen in Settings.</summary>
/// <param name="Select">Make it the Xcode that command line tools use.</param>
/// <param name="SkipAdministratorSteps">Do not ask for an administrator's password to finish setting it up.</param>
/// <param name="DeleteArchive">Delete the downloaded .xip instead of keeping it for a later reinstall.</param>
/// <param name="InstallDirectory">The Xcode installation directory, or null for /Applications.</param>
/// <param name="ArchiveDirectory">Where downloaded archives and expansion staging live, or null for the default cache.</param>
public sealed record XcodeInstallOptions(
    bool Select = false,
    bool SkipAdministratorSteps = false,
    bool DeleteArchive = false,
    string? InstallDirectory = null,
    string? ArchiveDirectory = null);
