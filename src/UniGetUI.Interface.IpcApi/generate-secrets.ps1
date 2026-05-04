param (
    [string]$OutputPath = "obj"
)

$generatedDir = [System.IO.Path]::Combine($OutputPath, "Generated Files")
if (-not (Test-Path -Path $generatedDir)) {
    New-Item -ItemType Directory -Path $generatedDir -Force | Out-Null
}

$clientId = $env:UNIGETUI_GITHUB_CLIENT_ID
if (-not $clientId) { $clientId = "CLIENT_ID_UNSET" }

@"
// Auto-generated file - do not modify
namespace UniGetUI.Interface
{
    internal static partial class Secrets
    {
        public static partial string GetGitHubClientId() => `"$clientId`";
    }
}
"@ | Set-Content -Encoding UTF8 ([System.IO.Path]::Combine($generatedDir, "Secrets.Generated.cs"))
