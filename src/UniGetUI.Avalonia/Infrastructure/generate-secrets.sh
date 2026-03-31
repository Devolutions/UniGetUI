#!/bin/bash
OUTPUT_PATH="${1:-obj}"

if [ ! -d "Generated Files" ]; then mkdir -p "Generated Files"; fi
if [ ! -d "${OUTPUT_PATH}Generated Files" ]; then mkdir -p "${OUTPUT_PATH}Generated Files"; fi

CLIENT_ID="${UNIGETUI_GITHUB_CLIENT_ID}"

if [ -z "$CLIENT_ID" ]; then CLIENT_ID="CLIENT_ID_UNSET"; fi

cat > "Generated Files/Secrets.Generated.cs" << CSEOF
// Auto-generated file - do not modify
namespace UniGetUI.Avalonia.Infrastructure
{
    internal static partial class Secrets
    {
        public static partial string GetGitHubClientId() => "$CLIENT_ID";
    }
}
CSEOF

cp "Generated Files/Secrets.Generated.cs" "${OUTPUT_PATH}Generated Files/Secrets.Generated.cs"
