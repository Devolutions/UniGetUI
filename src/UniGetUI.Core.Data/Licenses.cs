namespace UniGetUI.Core.Data
{
    public static class LicenseData
    {
        public static Dictionary<string, string> LicenseNames = new()
        {
            { "UniGetUI", "MIT" },
            // C# Libraries
            { "Pickers", "MIT" },
            { "Community Toolkit", "MIT" },
            { "H.NotifyIcon", "MIT" },
            { "Windows App Sdk", "MIT" },
            { "PhotoSauce.MagicScaler", "MIT" },
            { "YamlDotNet", "MIT" },
            { "InnoDependencyInstaller", "CPOL 1.02" },
            // Package managers and related
            { "WinGet", "MIT" },
            { "Scoop", "MIT" },
            { "scoop-search", "MIT" },
            { "Chocolatey", "Apache v2" },
            { "npm", "Artistic License 2.0" },
            { "Pip", "MIT" },
            { "parse_pip_search", "MIT" },
            { "PowerShell Gallery", "Unknown" },
            { ".NET SDK", "MIT" },
            { "Cargo", "MIT" },
            { "cargo-binstall", "GPL-3.0-only" },
            { "cargo-update", "MIT" },
            { "vcpkg", "MIT" },
            // Other
            { "GSudo", "MIT" },
            { "UniGetUI Elevator", "MIT" },
            { "Icons", "By Icons8" },
        };

        public static Dictionary<string, Uri> LicenseURLs = new()
        {
            { "UniGetUI", new Uri("https://github.com/Devolutions/UniGetUI/blob/main/LICENSE") },
            // C# Libraries
            { "Pickers", new Uri("https://github.com/PavlikBender/Pickers/blob/master/LICENSE") },
            {
                "Community Toolkit",
                new Uri("https://github.com/CommunityToolkit/Windows/blob/main/License.md")
            },
            {
                "H.NotifyIcon",
                new Uri("https://github.com/HavenDV/H.NotifyIcon/blob/master/LICENSE.md")
            },
            {
                "Windows App Sdk",
                new Uri("https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE")
            },
            {
                "PhotoSauce.MagicScaler",
                new Uri("https://github.com/saucecontrol/PhotoSauce/blob/master/license")
            },
            {
                "YamlDotNet",
                new Uri("https://github.com/aaubry/YamlDotNet/blob/master/LICENSE.txt")
            },
            {
                "InnoDependencyInstaller",
                new Uri(
                    "https://github.com/DomGries/InnoDependencyInstaller/blob/master/LICENSE.md"
                )
            },
            // Package managers and related
            { "WinGet", new Uri("https://github.com/microsoft/winget-cli/blob/master/LICENSE") },
            { "Scoop", new Uri("https://github.com/ScoopInstaller/Scoop/blob/master/LICENSE") },
            {
                "scoop-search",
                new Uri("https://github.com/shilangyu/scoop-search/blob/master/LICENSE")
            },
            { "Chocolatey", new Uri("https://github.com/chocolatey/choco/blob/develop/LICENSE") },
            { "npm", new Uri("https://github.com/npm/cli/blob/latest/LICENSE") },
            { "Pip", new Uri("https://github.com/pypa/pip/blob/main/LICENSE.txt") },
            {
                "parse_pip_search",
                new Uri(
                    "https://github.com/marticliment/parseable_pip_search/blob/master/LICENSE.md"
                )
            },
            { ".NET SDK", new Uri("https://github.com/dotnet/sdk/blob/main/LICENSE.TXT") },
            { "PowerShell Gallery", new Uri("https://www.powershellgallery.com/") },
            { "Cargo", new Uri("https://github.com/rust-lang/cargo/blob/master/LICENSE-MIT") },
            { "cargo-binstall", new Uri("https://spdx.org/licenses/GPL-3.0-only.html") },
            {
                "cargo-update",
                new Uri("https://github.com/nabijaczleweli/cargo-update/blob/master/LICENSE")
            },
            { "vcpkg", new Uri("https://github.com/microsoft/vcpkg/blob/master/LICENSE.txt") },
            // Other
            { "GSudo", new Uri("https://github.com/gerardog/gsudo/blob/master/LICENSE.txt") },
            {
                "UniGetUI Elevator",
                new Uri("https://github.com/Devolutions/gsudo-distro/blob/master/LICENSE")
            },
            { "Icons", new Uri("https://icons8.com/license") },
        };

        public static Dictionary<string, Uri> HomepageUrls = new()
        {
            { "UniGetUI", new Uri("https://devolutions.net/unigetui") },
            // C# Libraries
            { "Pickers", new Uri("https://github.com/PavlikBender/Pickers/") },
            { "Community Toolkit", new Uri("https://github.com/CommunityToolkit/Windows/") },
            { "H.NotifyIcon", new Uri("https://github.com/HavenDV/H.NotifyIcon/") },
            { "Windows App Sdk", new Uri("https://github.com/microsoft/WindowsAppSDK/") },
            { "PhotoSauce.MagicScaler", new Uri("https://github.com/saucecontrol/PhotoSauce/") },
            { "YamlDotNet", new Uri("https://github.com/aaubry/YamlDotNet/") },
            {
                "InnoDependencyInstaller",
                new Uri("https://github.com/DomGries/InnoDependencyInstaller")
            },
            // Package managers and related
            { "WinGet", new Uri("https://github.com/microsoft/winget-cli/") },
            { "Scoop", new Uri("https://github.com/ScoopInstaller/Scoop/") },
            { "scoop-search", new Uri("https://github.com/shilangyu/scoop-search/") },
            { "Chocolatey", new Uri("https://github.com/chocolatey/choco/") },
            { "npm", new Uri("https://github.com/npm/cli/") },
            { "Pip", new Uri("https://github.com/pypa/pip/") },
            {
                "parse_pip_search",
                new Uri("https://github.com/marticliment/parseable_pip_search/")
            },
            { ".NET SDK", new Uri("https://dotnet.microsoft.com/") },
            { "PowerShell Gallery", new Uri("https://www.powershellgallery.com/") },
            { "Cargo", new Uri("https://github.com/rust-lang/cargo") },
            { "cargo-binstall", new Uri("https://github.com/cargo-bins/cargo-binstall") },
            { "cargo-update", new Uri("https://github.com/nabijaczleweli/cargo-update/") },
            { "vcpkg", new Uri("https://github.com/microsoft/vcpkg") },
            // Other
            { "GSudo", new Uri("https://github.com/gerardog/gsudo/") },
            { "UniGetUI Elevator", new Uri("https://github.com/Devolutions/gsudo-distro") },
            { "Icons", new Uri("https://icons8.com") },
        };
    }
}
