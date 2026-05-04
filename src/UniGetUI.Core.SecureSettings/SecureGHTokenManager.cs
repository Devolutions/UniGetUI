using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.Core.SecureSettings
{
    public static class SecureGHTokenManager
    {
        private const string GitHubResourceName = "UniGetUI/GitHubAccessToken";
        private const string CredentialNamespaceEnvironmentVariable = "UNIGETUI_GITHUB_TOKEN_NAMESPACE";
        private static readonly string UserName = Environment.UserName;

        public static void StoreToken(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                Logger.Warn("Attempted to store a null or empty token. Operation cancelled.");
                return;
            }

            try
            {
                if (GetToken() is not null)
                    DeleteToken(); // Delete any old token(s)

                CoreCredentialStore.SetSecret(GetScopedResourceName(), UserName, token);
                Logger.Info("GitHub access token stored/updated securely.");
            }
            catch (Exception ex)
            {
                Logger.Error(
                    "An error occurred while attempting to delete the currently stored GitHub Token"
                );
                Logger.Error(ex);
            }
        }

        public static string? GetToken()
        {
            try
            {
                string? token = CoreCredentialStore.GetSecret(GetScopedResourceName(), UserName);
                if (token is null)
                {
                    return null;
                }

                Logger.Debug("GitHub access token retrieved.");
                return token;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not retrieve token (it may not exist): {ex.Message}");
                return null;
            }
        }

        public static void DeleteToken()
        {
            try
            {
                CoreCredentialStore.DeleteSecret(GetScopedResourceName(), UserName);
                Logger.Info("GitHub access token deleted.");
            }
            catch (Exception ex)
            {
                Logger.Error(
                    "An error occurred while attempting to delete the currently stored GitHub Token"
                );
                Logger.Error(ex);
            }
        }

        private static string GetScopedResourceName()
        {
            string? credentialNamespace = Environment.GetEnvironmentVariable(
                CredentialNamespaceEnvironmentVariable
            );

            return string.IsNullOrWhiteSpace(credentialNamespace)
                ? GitHubResourceName
                : $"{GitHubResourceName}/{credentialNamespace.Trim()}";
        }
    }
}
