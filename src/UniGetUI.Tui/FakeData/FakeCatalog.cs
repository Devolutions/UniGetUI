namespace UniGetUI.Tui.FakeData;

/// <summary>A package the fake managers can find, install, update and uninstall.</summary>
internal sealed record FakeCatalogPackage(
    string Manager,
    string Id,
    string Name,
    string Publisher,
    string Description,
    string License,
    string[] Versions,
    string[] Tags,
    string InstallerType,
    long InstallerSize,
    string[] Dependencies)
{
    /// <summary>The newest version, which is the last entry of <see cref="Versions"/>.</summary>
    public string LatestVersion => Versions[^1];

    public string Homepage => $"https://fake.unigetui.invalid/{Manager.ToLowerInvariant()}/{Id}";
}

/// <summary>A source (bucket, feed, repository) a fake manager ships with.</summary>
internal sealed record FakeCatalogSource(string Manager, string Name, string Url);

/// <summary>A fake manager's identity and capability profile.</summary>
internal sealed record FakeManagerProfile(
    string Name,
    string DisplayName,
    string Description,
    string ExecutableName,
    bool Found,
    bool SupportsSources,
    bool SupportsVersions,
    bool SupportsScopes,
    bool SupportsArchitectures,
    bool CanRunAsAdmin,
    bool CanRunInteractively,
    bool CanSkipIntegrityChecks,
    bool CanDownloadInstaller,
    string DefaultSourceName,
    string DefaultSourceUrl);

/// <summary>
/// The deterministic data set behind <c>--fake-data</c>. Every name is invented (the publishers are
/// the fictitious Contoso / Fabrikam / Northwind family) and every URL uses the reserved
/// <c>.invalid</c> TLD, so nothing here can be mistaken for, or resolve to, a real package.
/// Package ids that contain <c>Failing</c> always fail to install/update/uninstall, and ids that
/// contain <c>Slow</c> take several seconds, so failure and cancellation can be exercised.
/// </summary>
internal static class FakeCatalog
{
    public static readonly FakeManagerProfile[] Managers =
    [
        new("Winget", "WinGet", "Fake stand-in for the Windows Package Manager", "winget.exe",
            Found: true, SupportsSources: true, SupportsVersions: true, SupportsScopes: true,
            SupportsArchitectures: true, CanRunAsAdmin: true, CanRunInteractively: true,
            CanSkipIntegrityChecks: true, CanDownloadInstaller: true,
            "winget", "https://fake.unigetui.invalid/winget/cdn"),
        new("Scoop", "Scoop", "Fake stand-in for the Scoop command-line installer", "scoop.cmd",
            Found: true, SupportsSources: true, SupportsVersions: false, SupportsScopes: true,
            SupportsArchitectures: true, CanRunAsAdmin: true, CanRunInteractively: false,
            CanSkipIntegrityChecks: true, CanDownloadInstaller: false,
            "main", "https://fake.unigetui.invalid/scoop/main"),
        new("Chocolatey", "Chocolatey", "Fake stand-in for the Chocolatey package manager", "choco.exe",
            Found: true, SupportsSources: true, SupportsVersions: true, SupportsScopes: false,
            SupportsArchitectures: true, CanRunAsAdmin: true, CanRunInteractively: true,
            CanSkipIntegrityChecks: true, CanDownloadInstaller: true,
            "community", "https://fake.unigetui.invalid/chocolatey/api/v2"),
        new("Pip", "Pip", "Fake stand-in for the Python package installer", "pip.exe",
            Found: true, SupportsSources: false, SupportsVersions: true, SupportsScopes: true,
            SupportsArchitectures: false, CanRunAsAdmin: true, CanRunInteractively: false,
            CanSkipIntegrityChecks: false, CanDownloadInstaller: true,
            "pypi", "https://fake.unigetui.invalid/pypi/simple"),
        new("Npm", "Npm", "Fake stand-in for the Node.js package manager", "npm.cmd",
            Found: true, SupportsSources: false, SupportsVersions: true, SupportsScopes: true,
            SupportsArchitectures: false, CanRunAsAdmin: true, CanRunInteractively: false,
            CanSkipIntegrityChecks: false, CanDownloadInstaller: true,
            "npm", "https://fake.unigetui.invalid/npm/registry"),
        new("Cargo", "Cargo", "Fake stand-in for the Rust package manager (reported as not installed)",
            "cargo.exe",
            Found: false, SupportsSources: false, SupportsVersions: true, SupportsScopes: false,
            SupportsArchitectures: false, CanRunAsAdmin: false, CanRunInteractively: false,
            CanSkipIntegrityChecks: false, CanDownloadInstaller: false,
            "crates.io", "https://fake.unigetui.invalid/crates"),
    ];

    public static readonly FakeCatalogSource[] DefaultSources =
    [
        new("Winget", "winget", "https://fake.unigetui.invalid/winget/cdn"),
        new("Winget", "msstore", "https://fake.unigetui.invalid/winget/msstore"),
        new("Scoop", "main", "https://fake.unigetui.invalid/scoop/main"),
        new("Scoop", "extras", "https://fake.unigetui.invalid/scoop/extras"),
        new("Chocolatey", "community", "https://fake.unigetui.invalid/chocolatey/api/v2"),
    ];

    public static readonly FakeCatalogPackage[] Packages = BuildPackages();

    /// <summary>
    /// What is installed the first time a sandbox is created: (manager, id, installed version).
    /// Anything installed below its <see cref="FakeCatalogPackage.LatestVersion"/> shows up as an update.
    /// </summary>
    public static readonly (string Manager, string Id, string Version, string Source)[] InitiallyInstalled =
    [
        ("Winget", "Contoso.Editor", "4.1.0", "winget"),
        ("Winget", "Fabrikam.Browser", "118.0.2", "winget"),
        ("Winget", "Northwind.Terminal", "1.19.3", "winget"),
        ("Winget", "Contoso.Notes", "2.0.0", "msstore"),
        ("Winget", "Fabrikam.FailingUpdater", "1.0.0", "winget"),
        ("Winget", "Tailspin.SlowSync", "3.2.0", "winget"),
        ("Winget", "Woodgrove.Vault", "7.4.1", "winget"),
        ("Scoop", "fabrikam-grep", "13.0.0", "main"),
        ("Scoop", "contoso-7zip", "23.01", "main"),
        ("Scoop", "northwind-font", "3.1.1", "extras"),
        ("Chocolatey", "adventureworks-git", "2.44.0", "community"),
        ("Chocolatey", "contoso-node", "20.11.0", "community"),
        ("Pip", "contoso-requests", "2.31.0", "pypi"),
        ("Pip", "fabrikam-numbers", "1.26.4", "pypi"),
        ("Npm", "@contoso/cli", "9.8.1", "npm"),
        ("Npm", "northwind-lint", "8.57.0", "npm"),
    ];

    public static FakeManagerProfile? Profile(string managerName)
        => Array.Find(Managers, m => m.Name.Equals(managerName, StringComparison.OrdinalIgnoreCase));

    public static FakeCatalogPackage? Find(string managerName, string id)
        => Array.Find(Packages, p => p.Manager.Equals(managerName, StringComparison.OrdinalIgnoreCase)
                                     && p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static FakeCatalogPackage[] BuildPackages()
    {
        List<FakeCatalogPackage> list = [];

        void Add(string manager, string id, string name, string publisher, string description,
            string[] versions, string[] tags, string license = "MIT", string installerType = "exe",
            long size = 42_000_000, string[]? deps = null)
            => list.Add(new FakeCatalogPackage(manager, id, name, publisher, description, license,
                versions, tags, installerType, size, deps ?? []));

        // WinGet
        Add("Winget", "Contoso.Editor", "Contoso Editor", "Contoso Ltd.",
            "A lightweight code editor with syntax highlighting, extensions and an integrated terminal.",
            ["4.0.0", "4.1.0", "4.2.0", "4.3.1"], ["editor", "development", "text"]);
        Add("Winget", "Fabrikam.Browser", "Fabrikam Browser", "Fabrikam, Inc.",
            "A fast, privacy-focused web browser with built-in tracker blocking.",
            ["117.0.1", "118.0.2", "119.0.0"], ["browser", "web", "internet"], "MPL-2.0", "msi", 96_500_000);
        Add("Winget", "Northwind.Terminal", "Northwind Terminal", "Northwind Traders",
            "A modern terminal emulator with tabs, panes and GPU-accelerated text rendering.",
            ["1.18.0", "1.19.3", "1.20.1"], ["terminal", "shell", "console"], "MIT", "msix", 31_000_000);
        Add("Winget", "Contoso.Notes", "Contoso Notes", "Contoso Ltd.",
            "Take notes, draw sketches and sync them across your devices.",
            ["1.9.0", "2.0.0"], ["notes", "productivity"], "Proprietary", "msix", 12_300_000);
        Add("Winget", "Fabrikam.FailingUpdater", "Fabrikam Failing Updater", "Fabrikam, Inc.",
            "A deliberately broken fake package: every operation on it fails.",
            ["1.0.0", "1.1.0"], ["test", "failure"]);
        Add("Winget", "Tailspin.SlowSync", "Tailspin Slow Sync", "Tailspin Toys",
            "A file synchronisation client whose fake operations take a long time.",
            ["3.2.0", "3.3.0"], ["sync", "cloud", "slow"], "Apache-2.0", "exe", 150_000_000);
        Add("Winget", "Woodgrove.Vault", "Woodgrove Vault", "Woodgrove Bank",
            "A password manager with end-to-end encrypted vaults.",
            ["7.3.0", "7.4.1"], ["security", "passwords"], "GPL-3.0", "msi", 58_000_000);
        Add("Winget", "Contoso.PhotoStudio", "Contoso Photo Studio", "Contoso Ltd.",
            "Edit, retouch and organise your photo library.",
            ["2023.1", "2024.2", "2025.1"], ["photo", "graphics", "editor"], "Proprietary", "exe", 812_000_000,
            ["Contoso.Runtime"]);
        Add("Winget", "Contoso.Runtime", "Contoso Runtime", "Contoso Ltd.",
            "Shared runtime libraries used by Contoso applications.",
            ["8.0.1", "8.0.2"], ["runtime", "library"]);
        Add("Winget", "Litware.MediaPlayer", "Litware Media Player", "Litware, Inc.",
            "Plays almost every audio and video format out of the box.",
            ["3.0.18", "3.0.20", "3.0.21"], ["media", "video", "audio"], "GPL-2.0", "exe", 44_000_000);
        Add("Winget", "Adatum.Chat", "Adatum Chat", "A. Datum Corporation",
            "Team chat with channels, threads and video calls.",
            ["5.1.0", "5.2.2"], ["chat", "communication"], "Proprietary", "msix", 120_000_000);
        Add("Winget", "Proseware.Archiver", "Proseware Archiver", "Proseware, Inc.",
            "Compress and extract archives in dozens of formats.",
            ["24.07", "24.08"], ["archive", "compression", "zip"], "LGPL-2.1", "msi", 1_600_000);

        // Scoop
        Add("Scoop", "fabrikam-grep", "fabrikam-grep", "Fabrikam, Inc.",
            "A line-oriented search tool that recursively searches the current directory.",
            ["13.0.0", "14.1.0"], ["search", "cli"], "Unlicense", "zip", 2_100_000);
        Add("Scoop", "contoso-7zip", "contoso-7zip", "Contoso Ltd.",
            "A file archiver with a high compression ratio.",
            ["23.01", "24.08"], ["archive", "cli"], "LGPL-2.1", "zip", 1_500_000);
        Add("Scoop", "northwind-font", "northwind-font", "Northwind Traders",
            "A monospaced programming font with ligatures.",
            ["3.1.1"], ["font"], "OFL-1.1", "zip", 8_000_000);
        Add("Scoop", "tailspin-jq", "tailspin-jq", "Tailspin Toys",
            "A lightweight and flexible command-line JSON processor.",
            ["1.6", "1.7.1"], ["json", "cli"], "MIT", "zip", 900_000);
        Add("Scoop", "litware-fzf", "litware-fzf", "Litware, Inc.",
            "A general-purpose command-line fuzzy finder.",
            ["0.54.0", "0.55.0"], ["search", "cli"], "MIT", "zip", 1_200_000);

        // Chocolatey
        Add("Chocolatey", "adventureworks-git", "AdventureWorks Git", "Adventure Works Cycles",
            "Distributed version control, packaged for the fake Chocolatey feed.",
            ["2.43.0", "2.44.0", "2.45.1"], ["git", "vcs", "development"], "GPL-2.0", "exe", 65_000_000);
        Add("Chocolatey", "contoso-node", "Contoso Node Runtime", "Contoso Ltd.",
            "A JavaScript runtime built for servers and command-line tools.",
            ["20.11.0", "20.18.0", "22.11.0"], ["javascript", "runtime"], "MIT", "msi", 30_000_000);
        Add("Chocolatey", "fabrikam-docker", "Fabrikam Containers", "Fabrikam, Inc.",
            "Build and run containers on your desktop.",
            ["4.30.0", "4.34.2"], ["containers", "development"], "Proprietary", "exe", 520_000_000);

        // Pip
        Add("Pip", "contoso-requests", "contoso-requests", "Contoso Ltd.",
            "HTTP for humans: a simple, elegant HTTP library.",
            ["2.30.0", "2.31.0", "2.32.3"], ["http", "python"], "Apache-2.0", "wheel", 64_000);
        Add("Pip", "fabrikam-numbers", "fabrikam-numbers", "Fabrikam, Inc.",
            "Fundamental package for array computing.",
            ["1.26.4", "2.1.0"], ["math", "python"], "BSD-3-Clause", "wheel", 16_000_000);
        Add("Pip", "northwind-plot", "northwind-plot", "Northwind Traders",
            "Create static, animated and interactive visualisations.",
            ["3.8.0", "3.9.2"], ["plotting", "python"], "PSF", "wheel", 8_300_000);

        // Npm
        Add("Npm", "@contoso/cli", "@contoso/cli", "Contoso Ltd.",
            "The Contoso command-line interface for scaffolding projects.",
            ["9.8.1", "10.2.0"], ["cli", "scaffolding"], "MIT", "tgz", 3_000_000);
        Add("Npm", "northwind-lint", "northwind-lint", "Northwind Traders",
            "A pluggable linting utility for JavaScript and TypeScript.",
            ["8.57.0", "9.14.0"], ["lint", "javascript"], "MIT", "tgz", 2_500_000);
        Add("Npm", "tailspin-bundler", "tailspin-bundler", "Tailspin Toys",
            "A zero-configuration web application bundler.",
            ["5.3.0", "5.4.10"], ["bundler", "javascript"], "MIT", "tgz", 7_700_000);

        // Cargo (manager reported as not found; packages exist so a bundle can reference them)
        Add("Cargo", "litware-bat", "litware-bat", "Litware, Inc.",
            "A cat clone with syntax highlighting and Git integration.",
            ["0.24.0"], ["cli"], "MIT", "crate", 4_000_000);

        return [.. list];
    }
}
