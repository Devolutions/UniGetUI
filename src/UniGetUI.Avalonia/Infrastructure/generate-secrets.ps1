param (
    [string]$OutputPath = "obj"
)

if (-not (Test-Path -Path "Generated Files")) {
    New-Item -ItemType Directory -Path "Generated Files" -Force | Out-Null
}

$generatedDir = [System.IO.Path]::Combine($OutputPath, "Generated Files")
if (-not (Test-Path -Path $generatedDir)) {
    New-Item -ItemType Directory -Path $generatedDir -Force | Out-Null
}

$clientId = $env:UNIGETUI_GITHUB_CLIENT_ID

if (-not $clientId) { $clientId = "CLIENT_ID_UNSET" }

@"
// Auto-generated file - do not modify
namespace UniGetUI.Avalonia.Infrastructure
{
    internal static partial class Secrets
    {
        public static partial string GetGitHubClientId() => `"$clientId`";
    }
}
"@ | Set-Content -Encoding UTF8 "Generated Files\Secrets.Generated.cs"
Copy-Item "Generated Files\Secrets.Generated.cs" ([System.IO.Path]::Combine($generatedDir, "Secrets.Generated.cs"))
