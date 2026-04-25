# Snap Package Manager Implementation Plan

## Overview

This plan outlines the implementation of **Snap** (Canonical's universal Linux package manager) as a new package manager for UniGetUI. Snap is available on most Linux distributions via `snapd` and provides access to the Snapcraft store (https://snapcraft.io).

---

## 1. Project Structure

Create the following directory and files:

```
src/UniGetUI.PackageEngine.Managers.Snap/
├── Snap.cs                              # Main manager class
├── UniGetUI.PackageEngine.Managers.Snap.csproj  # Project file
└── Helpers/
    ├── SnapPkgDetailsHelper.cs          # Package details, icons, install location
    └── SnapPkgOperationHelper.cs        # Install/update/uninstall operations
```

---

## 2. Files to Create/Modify

### 2.1 New Files

#### `src/UniGetUI.PackageEngine.Managers.Snap/Snap.cs`

**Namespace:** `UniGetUI.PackageEngine.Managers.SnapManager`

**Key responsibilities:**
- Executable discovery (`snap` binary)
- Version loading (`snap --version`)
- Package search (`snap find <query>`)
- Installed packages listing (`snap list`)
- Available updates listing (`snap refresh --list`)
- Index refresh (no-op, snap handles this automatically)

**Key CLI commands and output formats:**

| Operation | Command | Output Format |
|-----------|---------|---------------|
| Version | `snap --version` | Multi-line: "snap X.Y.Z\nsnapd X.Y.Z\nseries 16\n..." |
| Search | `snap find <query>` | `<name>  <version>  <publisher>  <summary>` |
| Installed | `snap list` | `<name>  <version>  <rev>  <tracking>  <publisher>  <notes>` |
| Updates | `snap refresh --list` | Same as `snap list` but only upgradable packages |
| Info | `snap info <package>` | Multi-line key: value format |

**Implementation notes:**
- Set `LANG=C` and `LC_ALL=C` environment variables for consistent output parsing
- Handle exit code 1 as "no results found" (not an error) for search/updates
- Always drain ALL stdout before calling `WaitForExit()` to avoid pipe buffer deadlocks
- Include fallback paths: `/usr/bin/snap`, `/usr/local/bin/snap`

#### `src/UniGetUI.PackageEngine.Managers.Snap/Helpers/SnapPkgDetailsHelper.cs`

**Base class:** `BasePkgDetailsHelper`

**Methods to implement:**
- `GetDetails_UnSafe(IPackageDetails details)` - Parse `snap info <package-id>` output
  - Extract: Description, Homepage, Publisher, License, InstalledSize, ManifestUrl
- `GetIcon_UnSafe(IPackage package)` - Return `null` (icons handled by remote IconDatabase)
- `GetScreenshots_UnSafe(IPackage package)` - Return empty list (snap does not expose screenshots via CLI)
- `GetInstallLocation_UnSafe(IPackage package)` - Return `/snap/<name>/current`
- `GetInstallableVersions_UnSafe(IPackage package)` - Throw `InvalidOperationException` (Snap does not support arbitrary version selection via CLI)

#### `src/UniGetUI.PackageEngine.Managers.Snap/Helpers/SnapPkgOperationHelper.cs`

**Base class:** `BasePkgOperationHelper`

**Methods to implement:**
- `_getOperationParameters(IPackage package, InstallOptions options, OperationType operation)`
  - Install: `snap install <package-id> [--classic]`
  - Update: `snap refresh <package-id>`
  - Uninstall: `snap remove <package-id>`
  - Force `options.RunAsAdministrator = true` (snap always requires root)
- `_getOperationResult(...)` - Return `OperationVeredict.Success` if exit code is 0, `Failure` otherwise

#### `src/UniGetUI.PackageEngine.Managers.Snap/UniGetUI.PackageEngine.Managers.Snap.csproj`

Copy from `UniGetUI.PackageEngine.Managers.Apt.csproj` and update project name. Use `$(SharedTargetFrameworks)` (not `$(WindowsTargetFramework)`).

### 2.2 Modified Files

#### `src/UniGetUI.Interface.Enums/Enums.cs`

Add to `IconType` enum (after `Pacman = '\uE946'`):

```csharp
Snap = '\uE947',
```

#### `src/UniGetUI.PackageEngine.PackageEngine/UniGetUI.PackageEngine.PEInterface.csproj`

Add Snap to the non-Windows conditional project references (after the Pacman entry, line ~36):

```xml
<ItemGroup Condition="'$(TargetFramework)' != '$(WindowsTargetFramework)'">
    <ProjectReference Include="..\UniGetUI.PackageEngine.Managers.Apt\UniGetUI.PackageEngine.Managers.Apt.csproj" />
    <ProjectReference Include="..\UniGetUI.PackageEngine.Managers.Dnf\UniGetUI.PackageEngine.Managers.Dnf.csproj" />
    <ProjectReference Include="..\UniGetUI.PackageEngine.Managers.Pacman\UniGetUI.PackageEngine.Managers.Pacman.csproj" />
    <ProjectReference Include="..\UniGetUI.PackageEngine.Managers.Homebrew\UniGetUI.PackageEngine.Managers.Homebrew.csproj" />
    <ProjectReference Include="..\UniGetUI.PackageEngine.Managers.Snap\UniGetUI.PackageEngine.Managers.Snap.csproj" />
</ItemGroup>
```

This ensures the Snap project is transitively referenced by both the WinUI and Avalonia solutions.

#### `src/UniGetUI.PackageEngine.PackageEngine/PEInterface.cs`

1. Add import in the `#if !WINDOWS` block:
```csharp
using UniGetUI.PackageEngine.Managers.SnapManager;
```

2. Add static field in the `#if !WINDOWS` block:
```csharp
public static readonly Snap Snap = new();
```

3. Add to `CreateManagers()` method inside the `OperatingSystem.IsLinux()` block:
```csharp
if (unknown || families.Contains("ubuntu") || families.Contains("debian") || families.Contains("linuxmint") || families.Contains("pop"))
    managers.Add(Apt);
if (unknown || families.Contains("fedora") || families.Contains("rhel") || families.Contains("centos"))
    managers.Add(Dnf);
if (unknown || families.Contains("arch"))
    managers.Add(Pacman);
// Add Snap - available on most distros with snapd
if (unknown || families.Contains("ubuntu") || families.Contains("debian") || families.Contains("fedora") || families.Contains("arch"))
    managers.Add(Snap);
```

**Rationale:** Snap is distro-agnostic (available via snapd on most distros). Adding it to the `unknown` fallback ensures it's always available when `/etc/os-release` is unreadable. Consider whether to always add it regardless of distro family.

#### `src/UniGetUI.sln`

1. Add project entry (after Pacman, line ~67):
```
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "UniGetUI.PackageEngine.Managers.Snap", "UniGetUI.PackageEngine.Managers.Snap\UniGetUI.PackageEngine.Managers.Snap.csproj", "{A4B5C6D7-E8F9-0A1B-2C3D-4E5F6A7B8C9D}"
EndProject
```

2. Add build configurations in `GlobalSection(ProjectConfigurationPlatforms)` for all platform/config combinations (Debug/Release × x64/arm64/Any CPU/x86). Follow the pattern of existing Linux managers.

#### `src/UniGetUI.Avalonia.slnx`

Add the Snap project entry in the Managers section (after Homebrew, line ~173):

```xml
<Folder Name="/UniGetUI.PackageEngine.Managers.Snap/">
    <Project Path="UniGetUI.PackageEngine.Managers.Snap/UniGetUI.PackageEngine.Managers.Snap.csproj">
        <Platform Solution="*|arm64" Project="arm64" />
        <Platform Solution="*|x64" Project="x64" />
    </Project>
</Folder>
```

**Note:** While the project is transitively referenced via `PEInterface.csproj` and will build without this entry, adding it explicitly to the `.slnx` provides better IDE support and matches the pattern of other managers in the solution.

---

## 3. Implementation Details

### 3.1 Snap Manager Class (`Snap.cs`)

```csharp
public class Snap : PackageManager
{
    public Snap()
    {
        Dependencies = [];

        Capabilities = new ManagerCapabilities
        {
            CanRunAsAdmin = true,
            CanSkipIntegrityChecks = true,
            SupportsCustomSources = false,
            SupportsProxy = ProxySupport.No,
            SupportsProxyAuth = false,
        };

        var snapcraftSource = new ManagerSource(this, "snapcraft", new Uri("https://snapcraft.io"));

        Properties = new ManagerProperties
        {
            Name = "Snap",
            Description = CoreTools.Translate(
                "The universal Linux package manager by Canonical.<br>Contains: <b>Snap packages from the Snapcraft store</b>"
            ),
            IconId = IconType.Snap,
            ColorIconId = "snap",
            ExecutableFriendlyName = "snap",
            InstallVerb = "install",
            UpdateVerb = "refresh",
            UninstallVerb = "remove",
            DefaultSource = snapcraftSource,
            KnownSources = [snapcraftSource],
        };

        DetailsHelper = new SnapPkgDetailsHelper(this);
        OperationHelper = new SnapPkgOperationHelper(this);
    }

    public override IReadOnlyList<string> FindCandidateExecutableFiles()
    {
        var candidates = new List<string>(CoreTools.WhichMultiple("snap"));
        foreach (var path in new[] { "/usr/bin/snap", "/usr/local/bin/snap" })
        {
            if (File.Exists(path) && !candidates.Contains(path))
                candidates.Add(path);
        }
        return candidates;
    }

    protected override void _loadManagerExecutableFile(
        out bool found,
        out string path,
        out string callArguments)
    {
        (found, path) = GetExecutableFile();
        callArguments = "";
    }

    protected override void _loadManagerVersion(out string version)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Status.ExecutablePath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        p.Start();
        // First line: "snap X.Y.Z"
        var line = p.StandardOutput.ReadLine()?.Trim() ?? "";
        var parts = line.Split(' ');
        version = parts.Length >= 2 ? parts[1] : line;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }

    public override void RefreshPackageIndexes()
    {
        // Snap automatically refreshes its index; no manual refresh needed.
        // Optionally: run `snap refresh` to update all packages.
    }

    protected override IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
        // Parse `snap find <query>` output
        // Format: "<name>  <version>  <publisher>  <summary>"
        // Note: snap find may return results with "–" (en-dash) separators
    }

    protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe()
    {
        // Parse `snap list` output
        // Format: "<name>  <version>  <rev>  <tracking>  <publisher>  <notes>"
        // First line is a header — skip it
    }

    protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
    {
        // Parse `snap refresh --list` output
        // Same format as `snap list`, but only upgradable packages
        // Exit code 1 means "no updates available" — treat as success
    }
}
```

### 3.2 Snap CLI Output Parsing

#### `snap find` output format:
```
Name                     Version    Publisher              Summary
firefox                  123.0      mozilla✓               Mozilla Firefox web browser
code                     1.85.0     vscode✓                Code editing. Redefined.
```

- Columns are space-separated (variable width)
- Publisher may have a "✓" badge
- Need to parse carefully — summary can contain spaces

#### `snap list` output format:
```
Name         Version    Rev    Tracking       Publisher     Notes
core20       20231123   2105   latest/stable  canonical✓    base
firefox      123.0      3619   latest/stable  mozilla✓      -
```

- First line is header
- Columns are space-separated
- Notes column may contain "classic", "base", "-" etc.

#### `snap info` output format:
```
name:      firefox
summary:   Mozilla Firefox web browser
publisher: Mozilla✓
store-url: https://snapcraft.io/firefox
contact:   https://github.com/mozilla/snap-firefox/issues
license:   MPL-2.0
description: |
  An independent free and open-source web browser...
snap-id:   O3b8fG6Z2k4b5c6d7e8f9g0h1i2j3k4l
tracking:  latest/stable
refresh-date: today at 10:00 UTC
channels:
  latest/stable:    123.0 2024-01-15 (3619) 120MB -
  latest/candidate: 124.0 2024-01-20 (3620) 120MB -
installed:          123.0            (3619) 120MB -
```

- Key: value format
- Multi-line values (description) use `|` indicator and indentation
- Channels section lists available versions

### 3.3 Operation Parameters

| Operation | Command | Notes |
|-----------|---------|-------|
| Install | `snap install <id>` | Add `--classic` for classic confinement packages |
| Update | `snap refresh <id>` | |
| Uninstall | `snap remove <id>` | Add `--purge` to remove user data |

**Classic confinement detection:** Some snap packages require `--classic` flag (e.g., `code`, `multipass`). This can be detected from `snap info <id>` output by checking if any channel has "classic" in the notes column, or by catching the error message when installation fails without it.

---

## 4. Build System Changes

### 4.1 Project File

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <TargetFrameworks>$(SharedTargetFrameworks)</TargetFrameworks>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="..\UniGetUI.Core.Classes\UniGetUI.Core.Classes.csproj" />
        <ProjectReference Include="..\UniGetUI.Core.Data\UniGetUI.Core.Data.csproj" />
        <ProjectReference Include="..\UniGetUI.Core.IconStore\UniGetUI.Core.IconEngine.csproj" />
        <ProjectReference Include="..\UniGetUI.Core.LanguageEngine\UniGetUI.Core.LanguageEngine.csproj" />
        <ProjectReference Include="..\UniGetUI.Core.Logger\UniGetUI.Core.Logging.csproj" />
        <ProjectReference Include="..\UniGetUI.Core.Settings\UniGetUI.Core.Settings.csproj" />
        <ProjectReference Include="..\UniGetUI.Core.Tools\UniGetUI.Core.Tools.csproj" />
        <ProjectReference Include="..\UniGetUI.Interface.Enums\UniGetUI.Interface.Enums.csproj" />
        <ProjectReference Include="..\UniGetUI.PackageEngine.Enums\UniGetUI.PackageEngine.Structs.csproj" />
        <ProjectReference Include="..\UniGetUI.PAckageEngine.Interfaces\UniGetUI.PackageEngine.Interfaces.csproj" />
        <ProjectReference Include="..\UniGetUI.PackageEngine.PackageManagerClasses\UniGetUI.PackageEngine.Classes.csproj" />
    </ItemGroup>

    <ItemGroup>
        <Compile Include="..\SharedAssemblyInfo.cs" Link="SharedAssemblyInfo.cs" />
    </ItemGroup>
</Project>
```

### 4.2 Solution File Entries

#### `src/UniGetUI.sln` (Traditional format)
Generate a new GUID for the project. Add project entry and build configurations matching the pattern of existing Linux managers (Apt, Dnf, Pacman).

#### `src/UniGetUI.Avalonia.slnx` (XML format)
Add a `<Folder>` entry with `<Project>` and `<Platform>` mappings for x64 and arm64, matching the pattern of other managers in the solution.

#### `src/UniGetUI.PackageEngine.PackageEngine/UniGetUI.PackageEngine.PEInterface.csproj`
Add the Snap project reference to the non-Windows conditional `ItemGroup` so it's included in both WinUI and Avalonia builds on Linux.

---

## 5. Linux-Specific Considerations

### 5.1 Environment Variables
Always set for consistent output:
```csharp
p.StartInfo.Environment["LANG"] = "C";
p.StartInfo.Environment["LC_ALL"] = "C";
```

### 5.2 Exit Code Handling
- `snap find` with no results: exit code 1 (not an error)
- `snap refresh --list` with no updates: exit code 1 (not an error)
- Handle these cases by checking `p.ExitCode == 1 && packages.Count == 0`

### 5.3 Pipe Buffer Deadlock Prevention
Always drain ALL stdout before `WaitForExit()`:
```csharp
string? line;
while ((line = p.StandardOutput.ReadLine()) is not null) { /* process */ }
p.StandardError.ReadToEnd();  // Must drain stderr too
p.WaitForExit();
```

### 5.4 Root Elevation
Snap operations require root. Force elevation in operation helper:
```csharp
options.RunAsAdministrator = true;
```

### 5.5 Snapd Dependency
Consider adding a `ManagerDependency` to check for `snapd` service availability:
```csharp
Dependencies = [
    new ManagerDependency(
        "snapd",
        "systemctl",
        "is-active snapd",
        "systemctl is-active snapd",
        async () => { /* check if snapd is running */ }
    ),
];
```

---

## 6. Testing Strategy

### 6.1 Unit Tests
- Test regex patterns against sample CLI output
- Test package parsing with edge cases (special characters, Unicode publishers)

### 6.2 Integration Tests
- Verify `snap find` parsing with real queries
- Verify `snap list` parsing on a system with snaps installed
- Verify `snap refresh --list` parsing

### 6.3 Manual Testing Checklist
- [ ] Snap manager initializes correctly on a system with snapd
- [ ] Search returns results for common packages (firefox, code)
- [ ] Installed packages list matches `snap list` output
- [ ] Updates list matches `snap refresh --list` output
- [ ] Package details show correct information
- [ ] Install operation works (requires root)
- [ ] Update operation works (requires root)
- [ ] Uninstall operation works (requires root)
- [ ] Manager gracefully handles snapd not being installed
- [ ] Manager works on non-Ubuntu distros with snapd (Fedora, Arch)

---

## 7. Future Enhancements

### 7.1 Classic Confinement Detection
Automatically detect and apply `--classic` flag for packages that require it by:
1. Parsing `snap info <id>` output for classic confinement indicators
2. Catching installation errors and retrying with `--classic`

### 7.2 Channel Support
Snap supports channels (stable, candidate, beta, edge). Future work could:
- Allow users to select channels for installation
- Show available channels in package details
- Support `--channel` flag in install operations

### 7.3 Snap Store API Integration
For richer metadata (screenshots, detailed descriptions), consider integrating with the Snap Store API:
- `https://api.snapcraft.io/api/v1/snaps/details/<package>`
- Provides: screenshots, media, categories, download counts

### 7.4 Snap Refresh Index
Implement `RefreshPackageIndexes()` to run `snap refresh` for updating all packages, or document that snap handles this automatically via systemd timers.

---

## 8. Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| Snap CLI output format changes | Medium | Use robust regex patterns, add logging for parse failures |
| snapd not installed | Low | Manager gracefully handles missing executable |
| Classic confinement packages fail to install | Medium | Implement auto-detection or prompt user |
| Non-English locale breaks parsing | Low | Always set `LANG=C` and `LC_ALL=C` |
| Pipe buffer deadlock with large output | Low | Always drain stdout before WaitForExit() |

---

## 9. Implementation Order

1. **Create project structure** - Directory, `.csproj`
2. **Add IconType enum entry** - `Snap = '\uE947'`
3. **Add project reference to PEInterface.csproj** - Conditional reference for non-Windows builds
4. **Implement `Snap.cs`** - Core manager with executable discovery, version loading, package listing
5. **Implement `SnapPkgDetailsHelper.cs`** - Package details parsing
6. **Implement `SnapPkgOperationHelper.cs`** - Install/update/uninstall operations
7. **Register in `PEInterface.cs`** - Add manager to the list
8. **Add to solution files** - `UniGetUI.sln` and `UniGetUI.Avalonia.slnx`
9. **Build and test** - Verify compilation on both solutions, run unit tests
10. **Manual testing** - Test on a system with snapd installed
11. **Edge case handling** - Handle missing snapd, classic confinement, locale issues

---

## 10. Reference Files

| Purpose | Path |
|---------|------|
| Linux manager reference | `src/UniGetUI.PackageEngine.Managers.Apt/Apt.cs` |
| Linux manager reference | `src/UniGetUI.PackageEngine.Managers.Pacman/Pacman.cs` |
| Base manager class | `src/UniGetUI.PackageEngine.PackageManagerClasses/Manager/PackageManager.cs` |
| Base details helper | `src/UniGetUI.PackageEngine.PackageManagerClasses/Manager/Helpers/BasePkgDetailsHelper.cs` |
| Base operation helper | `src/UniGetUI.PackageEngine.PackageManagerClasses/Manager/Helpers/BasePkgOperationHelper.cs` |
| IconType enum | `src/UniGetUI.Interface.Enums/Enums.cs` |
| Manager registration | `src/UniGetUI.PackageEngine.PackageEngine/PEInterface.cs` |
| Project reference | `src/UniGetUI.PackageEngine.Managers.Apt/UniGetUI.PackageEngine.Managers.Apt.csproj` |
