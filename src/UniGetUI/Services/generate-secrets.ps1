param (
    [string]$OutputPath = "obj\Generated"
)

# Ensure directory exists
if (-not (Test-Path -Path "$OutputPath\Generated Files")) {
    New-Item -ItemType Directory -Path "$OutputPath\Generated Files" -Force | Out-Null
}

if (-not (Test-Path -Path "Generated Files")) {
    New-Item -ItemType Directory -Path "Generated Files" -Force | Out-Null
}


$clientId = $env:UNIGETUI_GITHUB_CLIENT_ID
$clientSecret = $env:UNIGETUI_GITHUB_CLIENT_SECRET
$openSearchUsername = $env:UNIGETUI_OPENSEARCH_USERNAME
$openSearchPassword = $env:UNIGETUI_OPENSEARCH_PASSWORD

if (-not $clientId) { $clientId = "CLIENT_ID_UNSET" }
if (-not $clientSecret) { $clientSecret = "CLIENT_SECRET_UNSET" }
if (-not $openSearchUsername) { $openSearchUsername = "OPENSEARCH_USERNAME_UNSET" }
if (-not $openSearchPassword) { $openSearchPassword = "OPENSEARCH_PASSWORD_UNSET" }

@"
// Auto-generated file - do not modify
namespace UniGetUI.Services
{
    internal static partial class Secrets
    {
        public static partial string GetGitHubClientId() => `"$clientId`";
        public static partial string GetGitHubClientSecret() => `"$clientSecret`";
        public static partial string GetOpenSearchUsername() => `"$openSearchUsername`";
        public static partial string GetOpenSearchPassword() => `"$openSearchPassword`";
    }
}
"@ | Set-Content -Encoding UTF8 "Generated Files\Secrets.Generated.cs"
Copy-Item "Generated Files\Secrets.Generated.cs" "$OutputPath\Generated Files\Secrets.Generated.cs"
