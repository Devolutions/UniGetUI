#!/bin/bash
OUTPUT_PATH="${1:-obj/}"

if [ ! -d "${OUTPUT_PATH}Generated Files" ]; then mkdir -p "${OUTPUT_PATH}Generated Files"; fi

CLIENT_ID="${UNIGETUI_GITHUB_CLIENT_ID}"
if [ -z "$CLIENT_ID" ]; then CLIENT_ID="CLIENT_ID_UNSET"; fi

cat > "${OUTPUT_PATH}Generated Files/Secrets.Generated.cs" << CSEOF
// Auto-generated file - do not modify
namespace UniGetUI.Interface
{
    internal static partial class Secrets
    {
        public static partial string GetGitHubClientId() => "$CLIENT_ID";
    }
}
CSEOF
