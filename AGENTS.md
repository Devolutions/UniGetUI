# UniGetUI - Copilot Instructions

## Project Overview

UniGetUI is a WinUI 3 desktop app (C#/.NET 10, Windows App SDK) providing a GUI for CLI package managers (WinGet, Scoop, Chocolatey, Pip, Npm, .NET Tool, PowerShell Gallery, Cargo, Vcpkg).

Solution entry points:
- `src/UniGetUI.sln` - official Windows application based on WinUI 3
- `src/UniGetUI.Avalonia.slnx` - experimental cross-platform Avalonia port

## Architecture

The codebase follows a **layered, modular structure** with ~40 projects:

- **`UniGetUI/`** - WinUI 3 entry point, XAML pages, controls, and app shell (`EntryPoint.cs`, `MainWindow.xaml`)
- **`UniGetUI.Core.*`** - Shared infrastructure: `Logger`, `Settings`, `Tools` (includes `CoreTools.Translate()`), `IconEngine`, `LanguageEngine`
- **`UniGetUI.PackageEngine.Interfaces`** - Contracts: `IPackageManager`, `IPackage`, `IManagerSource`, `IPackageDetails`
- **`UniGetUI.PackageEngine.PackageManagerClasses`** - Base implementations: `PackageManager` (abstract), `Package`, helpers (`BasePkgDetailsHelper`, `BasePkgOperationHelper`, `BaseSourceHelper`)
- **`UniGetUI.PackageEngine.Managers.*`** - Concrete manager implementations (one project per manager: `WinGet`, `Scoop`, `Chocolatey`, `Pip`, `Npm`, etc.)
- **`UniGetUI.PackageEngine.Operations`** - Install/update/uninstall operation orchestration
- **`UniGetUI.Interface.*`** - Enums, telemetry, background API

## Adding a New Package Manager

Each manager extends `PackageManager` and must override three abstract methods:

```csharp
protected override IReadOnlyList<Package> FindPackages_UnSafe(string query);
protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe();
protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe();
```

Each manager also provides three helper classes (in a `Helpers/` subfolder):
- `*PkgDetailsHelper` extends `BasePkgDetailsHelper` - overrides `GetDetails_UnSafe`, `GetInstallableVersions_UnSafe`, `GetIcon_UnSafe`, etc.
- `*PkgOperationHelper` extends `BasePkgOperationHelper` - overrides `_getOperationParameters`, `_getOperationResult`
- `*SourceHelper` extends `BaseSourceHelper` - overrides `GetSources_UnSafe`, `GetAddSourceParameters`, etc.

The constructor sets `Capabilities`, `Properties`, and wires the helpers. See `src/UniGetUI.PackageEngine.Managers.Scoop/Scoop.cs` as a clean reference implementation.

## Build & Test

```shell
# Restore & test (from src/)
dotnet restore
dotnet test --verbosity q --nologo

# Publish release build
dotnet publish src/UniGetUI/UniGetUI.csproj /p:Configuration=Release /p:Platform=x64
```

- Target framework: `net10.0-windows10.0.26100.0` (min `10.0.19041`)
- Build generates secrets via `src/UniGetUI/Services/generate-secrets.ps1` and integrity tree via `scripts/generate-integrity-tree.ps1`
- Self-contained, publish-trimmed (partial), Windows App SDK self-contained
- Tests use **xUnit** (`[Fact]`, `Assert.*`)

## Avalonia DevTools (Developer-Only)

Use these rules when changing Avalonia diagnostics/devtools behavior:

- Build-time switch is `EnableAvaloniaDiagnostics` in `src/Directory.Build.props`.
- Default policy: enabled in `Debug`, disabled in `Release`.
- `src/UniGetUI.Avalonia/UniGetUI.Avalonia.csproj` must condition `AvaloniaUI.DiagnosticsSupport` on `$(EnableAvaloniaDiagnostics)`.
- Compile-time diagnostics code in `src/UniGetUI.Avalonia/Program.cs` must be gated by `#if AVALONIA_DIAGNOSTICS_ENABLED` (not `#if DEBUG`).
- Runtime controls are developer-only and intentionally not listed in `cli-arguments.md`.
- Runtime precedence in `Program.cs`: CLI flags > `UNIGETUI_AVALONIA_DEVTOOLS` environment variable > `Auto` default.
- Accepted runtime env/CLI values for mode parsing: `auto`, `enabled`, `disabled`, `on`, `off`, `true`, `false`, `1`, `0`.
- `Auto` mode must remain WSL-safe (DevTools disabled by default on WSL).
- If diagnostics were excluded at build time, runtime toggle requests should log a no-op warning.

## Key Patterns & Conventions

### Settings
File-based settings via `Settings.Get(Settings.K.*)` / `Settings.Set(Settings.K.*, value)` and `Settings.GetValue(Settings.K.*)` / `Settings.SetValue(Settings.K.*, value)`. Setting keys are defined in the `Settings.K` enum in `SettingsEngine_Names.cs`. Boolean settings are stored as file existence; string settings as file content.

### Logging
Use `Logger.Info()`, `Logger.Warn()`, `Logger.Error()`, `Logger.Debug()`, `Logger.ImportantInfo()` from `UniGetUI.Core.Logging`. Accepts both `string` and `Exception` parameters.

### Localization
Use `CoreTools.Translate("text")` for all user-facing strings. Parameterized: `CoreTools.Translate("{0} packages found", count)`. In XAML, use the `TranslatedTextBlock` control. Translation assets live under `src/UniGetUI.Core.LanguageEngine/Assets/`; do not assume Tolgee-based automation exists in this repository.

### Naming
- Types, methods, properties: **PascalCase**
- Private fields: `__doubleUnderscore` or `_singleUnderscore` prefix
- Internal unsafe methods: suffix `_UnSafe` (e.g., `FindPackages_UnSafe`)
- Nullable enabled globally; `LangVersion` is `latest`
- Code style enforced in build (`EnforceCodeStyleInBuild=true`)

### Manager conventions
- `FALSE_PACKAGE_NAMES`, `FALSE_PACKAGE_IDS`, `FALSE_PACKAGE_VERSIONS` static arrays filter CLI parsing noise
- Manager initialization flows through `Initialize()` -> `_loadManagerExecutableFile()` -> `_loadManagerVersion()` -> `_performExtraLoadingSteps()`
- Operations that may fail return `OperationVeredict` (note: intentional misspelling used throughout codebase)

## Key Files

| Purpose | Path |
|---|---|
| Solution | `src/UniGetUI.sln` |
| Experimental cross-platform solution | `src/UniGetUI.Avalonia.slnx` |
| Shared build props | `src/Directory.Build.props` |
| Version info | `src/SharedAssemblyInfo.cs` |
| Manager interface | `src/UniGetUI.PAckageEngine.Interfaces/IPackageManager.cs` |
| Base manager class | `src/UniGetUI.PackageEngine.PackageManagerClasses/Manager/PackageManager.cs` |
| Package class | `src/UniGetUI.PackageEngine.PackageManagerClasses/Packages/Package.cs` |
| Settings engine | `src/UniGetUI.Core.Settings/SettingsEngine.cs` |
| Setting keys | `src/UniGetUI.Core.Settings/SettingsEngine_Names.cs` |
| Logger | `src/UniGetUI.Core.Logger/Logger.cs` |
| CI test workflow | `.github/workflows/dotnet-test.yml` |
