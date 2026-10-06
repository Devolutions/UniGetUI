using System.Text.Json;
using UniGetUI.Interface;

namespace UniGetUI.Tests;

public sealed class GitHubOAuthResponseTests
{
    [Fact]
    public void GeneratedContext_PreservesErrorFields()
    {
        var response = JsonSerializer.Deserialize(
            """{"error":"access_denied","error_description":"The user refused this request."}""",
            GitHubJsonContext.Default.GitHubOAuthToken)!;

        Assert.Equal("access_denied", response.Error);
        Assert.Equal("The user refused this request.", response.ErrorDescription);
        Assert.Equal("", response.AccessToken);
    }

    [Fact]
    public void GeneratedContext_PreservesSuccessfulAccessToken()
    {
        var response = JsonSerializer.Deserialize(
            """{"access_token":"token-5447"}""", GitHubJsonContext.Default.GitHubOAuthToken)!;

        Assert.Equal("token-5447", response.AccessToken);
        Assert.Null(response.Error);
        Assert.Null(response.ErrorDescription);
    }
}
