# UniGetUI - Copilot Instructions

## Project Overview

UniGetUI is an Avalonia desktop app (C#/.NET 10) providing a GUI for CLI package managers (WinGet, Scoop, Chocolatey, Pip, Npm, .NET Tool, PowerShell Gallery, Cargo, Vcpkg).

Solution entry points:
- `src/UniGetUI.Windows.slnx` - official Windows solution; builds the Avalonia app and Windows-specific package-manager integrations
- `src/UniGetUI.Avalonia.slnx` - cross-platform Avalonia solution

## Architecture

The codebase follows a **layered, modular structure** with ~40 projects:

- **`UniGetUI.Avalonia/`** - Avalonia entry point, AXAML pages, controls, and app shell (`Program.cs`, `Views/MainWindow.axaml`)
- **`SharedAssets/`** - shared app icons, symbols, splash images, and utility scripts used by packaging
- **`UniGetUI.Core.*`** - Shared infrastructure: `Logger`, `Settings`, `Tools` (includes `CoreTools.Translate()`), `IconEngine`, `LanguageEngine`
- **`UniGetUI.PackageEngine.Interfaces`** - Contracts: `IPackageManager`, `IPackage`, `IManagerSource`, `IPackageDetails`
- **`UniGetUI.PackageEngine.PackageManagerClasses`** - Base implementations: `PackageManager` (abstract), `Package`, helpers (`BasePkgDetailsHelper`, `BasePkgOperationHelper`, `BaseSourceHelper`)
- **`UniGetUI.PackageEngine.Managers.*`** - Concrete manager implementations (one project per manager: `WinGet`, `Scoop`, `Chocolatey`, `Pip`, `npm`, etc.)
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
dotnet restore UniGetUI.Windows.slnx
dotnet test UniGetUI.Windows.slnx --verbosity q --nologo /p:Platform=x64

# Publish release build
dotnet publish src/UniGetUI.Avalonia/UniGetUI.Avalonia.csproj /p:Configuration=Release /p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

- Target framework: `net10.0-windows10.0.26100.0` (min `10.0.19041`)
- Build generates secrets via `src/UniGetUI.Avalonia/Infrastructure/generate-secrets.ps1` and integrity tree via `scripts/generate-integrity-tree.ps1`
- Self-contained, publish-trimmed (partial), Windows App SDK self-contained
- Tests use **xUnit** (`[Fact]`, `Assert.*`)

## NativeAOT and Trim Safety

Release packages are self-contained, fully trimmed NativeAOT binaries on every supported RID. Treat NativeAOT safety as a non-negotiable production requirement: changes must work without runtime-generated code or metadata that the trimmer cannot prove is required.

- Consider every `IL2xxx` trim warning and `IL3xxx` AOT warning a defect. Do not blanket-suppress warnings or use a broad `RequiresUnreferencedCode`, `RequiresDynamicCode`, `DynamicDependency`, `DynamicallyAccessedMembers`, linker descriptor, or root assembly as a workaround. An exception must be narrowly scoped, explain the concrete runtime requirement, and have a publish-path test that proves it is safe.
- Use `JsonSerializerContext`/`JsonTypeInfo<T>` source-generated metadata for every production JSON serialization or deserialization path, including generic helpers, HTTP payloads, settings, IPC, telemetry, and persisted state. Do not call reflection-based serializer overloads or retain a reflection fallback for unknown application types. Add the closed type to a context and test the persisted or transported path.
- Do not introduce production reflection-driven activation or discovery (`Assembly.Load`, `Type.GetType`, `Activator`, member lookup/invocation), runtime generic construction, expression compilation, runtime code generation, or dynamically loaded plugins. Prefer explicit registrations, direct constructors, generated metadata, and compiled XAML bindings. If a framework-owned dynamic boundary cannot be removed, isolate it and add published-NativeAOT runtime coverage.
- New shipping COM interop must use source-generated interfaces and wrappers (`GeneratedComInterface`, `StrategyBasedComWrappers`) or raw ABI calls; do not add `ComImport`, `CoClass`, RCW activation, or legacy marshal-to-object APIs to a NativeAOT path. Define the ABI precisely, balance COM reference ownership, and test both x64 and arm64 where applicable.
- Prefer `LibraryImport` for new P/Invokes. Existing `DllImport` declarations are acceptable only when their marshalling is supported by NativeAOT; keep platform guards, calling conventions, ownership, callback lifetimes, and x64/arm64 struct layouts explicit and covered.
- Audit all new or changed startup, XAML/view-loading, dependency-injection, serialization, COM/WinRT, P/Invoke, and package-manager code against the publish output, not only a Debug build. Run the relevant NativeAOT publish profile, for example:

  ```shell
  dotnet publish src/UniGetUI.Avalonia/UniGetUI.Avalonia.csproj --configuration Release --runtime win-x64 --self-contained true /p:Platform=x64 /p:PublishProfile=Win-x64-NativeAot
  ```

- When modifying a shared or portable path, retain the same safety guarantees for all checked-in NativeAOT publish profiles: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`. Record unsupported runtime-matrix coverage as a gap, never as a pass.

## Avalonia DevTools (Developer-Only)

Use these rules when changing Avalonia diagnostics/devtools behavior:

- Build-time switch is `EnableAvaloniaDiagnostics` in `src/Directory.Build.props`.
- Default policy: enabled in `Debug`, disabled in `Release`.
- `src/UniGetUI.Avalonia/UniGetUI.Avalonia.csproj` must condition `AvaloniaUI.DiagnosticsSupport` on `$(EnableAvaloniaDiagnostics)`.
- Compile-time diagnostics code in `src/UniGetUI.Avalonia/Program.cs` must be gated by `#if AVALONIA_DIAGNOSTICS_ENABLED` (not `#if DEBUG`).
- Runtime controls are developer-only and intentionally not listed in `docs/CLI.md`.
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
Use `CoreTools.Translate("text")` for all user-facing strings. Parameterized: `CoreTools.Translate("{0} packages found", count)`. In XAML, use the `TranslatedTextBlock` control. Translation assets live under `src/Languages/`; do not assume Tolgee-based automation exists in this repository.

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
| Windows solution | `src/UniGetUI.Windows.slnx` |
| Cross-platform solution | `src/UniGetUI.Avalonia.slnx` |
| Shared build props | `src/Directory.Build.props` |
| Version info | `src/SharedAssemblyInfo.cs` |
| Manager interface | `src/UniGetUI.PackageEngine.Interfaces/IPackageManager.cs` |
| Base manager class | `src/UniGetUI.PackageEngine.PackageManagerClasses/Manager/PackageManager.cs` |
| Package class | `src/UniGetUI.PackageEngine.PackageManagerClasses/Packages/Package.cs` |
| Settings engine | `src/UniGetUI.Core.Settings/SettingsEngine.cs` |
| Setting keys | `src/UniGetUI.Core.Settings/SettingsEngine_Names.cs` |
| Logger | `src/UniGetUI.Core.Logger/Logger.cs` |
| CI test workflow | `.github/workflows/dotnet-test.yml` |
