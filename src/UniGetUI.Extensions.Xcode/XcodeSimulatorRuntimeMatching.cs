namespace UniGetUI.Extensions.Xcode;

/// <summary>Joins catalog-reported SDK builds to Apple's explicit simulator build mappings.</summary>
public static class XcodeSimulatorRuntimeMatching
{
    public static IReadOnlyList<XcodeSimulatorRuntime> ForSDKs(
        IReadOnlyList<XcodeSdkIdentity> sdks,
        IReadOnlyList<XcodeSimulatorSdkMapping> mappings,
        IReadOnlyList<XcodeSimulatorRuntime> catalog,
        IReadOnlyList<XcodeSimulatorRuntime> installed)
    {
        HashSet<XcodeSdkIdentity> identities = [.. sdks];
        HashSet<(string Platform, string Build)> builds = [.. mappings
            .Where(mapping => identities.Contains(mapping.SDK))
            .Select(mapping => (mapping.SDK.Platform, mapping.SimulatorBuild))];
        XcodeSimulatorRuntime[] matchedCatalog = [.. catalog.Where(runtime =>
            runtime.Origin == XcodeSimulatorRuntimeOrigin.AppleCatalog
            && runtime.Build is not null && builds.Contains((runtime.Platform, runtime.Build)))];
        HashSet<(string Platform, string Version, string? Build, XcodeArchitecture Architecture)> exact = [.. matchedCatalog
            .Where(runtime => runtime.Architecture != XcodeArchitecture.Unspecified)
            .Select(runtime => (runtime.Platform, runtime.Version, runtime.Build, runtime.Architecture))];

        return [.. installed.Where(runtime =>
                runtime.Origin is XcodeSimulatorRuntimeOrigin.Installed or XcodeSimulatorRuntimeOrigin.Bundled
                && runtime.Architecture != XcodeArchitecture.Unspecified
                && runtime.Build is not null
                && exact.Contains((runtime.Platform, runtime.Version, runtime.Build, runtime.Architecture))),
            .. matchedCatalog];
    }
}
