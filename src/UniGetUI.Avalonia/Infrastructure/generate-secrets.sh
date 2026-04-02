#!/bin/bash
OUTPUT_PATH="${1:-obj}"

if [ ! -d "Generated Files" ]; then mkdir -p "Generated Files"; fi
if [ ! -d "${OUTPUT_PATH}Generated Files" ]; then mkdir -p "${OUTPUT_PATH}Generated Files"; fi

CLIENT_ID="${UNIGETUI_GITHUB_CLIENT_ID}"
OPENSEARCH_USERNAME="${UNIGETUI_OPENSEARCH_USERNAME}"
OPENSEARCH_PASSWORD="${UNIGETUI_OPENSEARCH_PASSWORD}"

if [ -z "$CLIENT_ID" ]; then CLIENT_ID="CLIENT_ID_UNSET"; fi
if [ -z "$OPENSEARCH_USERNAME" ]; then OPENSEARCH_USERNAME="OPENSEARCH_USERNAME_UNSET"; fi
if [ -z "$OPENSEARCH_PASSWORD" ]; then OPENSEARCH_PASSWORD="OPENSEARCH_PASSWORD_UNSET"; fi

cat > "Generated Files/Secrets.Generated.cs" << CSEOF
// Auto-generated file - do not modify
namespace UniGetUI.Avalonia.Infrastructure
{
    internal static partial class Secrets
    {
        public static partial string GetGitHubClientId() => "$CLIENT_ID";
        public static partial string GetOpenSearchUsername() => "$OPENSEARCH_USERNAME";
        public static partial string GetOpenSearchPassword() => "$OPENSEARCH_PASSWORD";
    }
}
CSEOF

cp "Generated Files/Secrets.Generated.cs" "${OUTPUT_PATH}Generated Files/Secrets.Generated.cs"
