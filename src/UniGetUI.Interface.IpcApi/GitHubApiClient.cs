using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;

namespace UniGetUI.Interface;

public sealed class GitHubApiClient : IDisposable
{
    private static readonly Uri GitHubBaseUri = new("https://github.com/");
    private static readonly Uri GitHubApiBaseUri = new("https://api.github.com/");
    private readonly HttpClient _httpClient;

    public GitHubApiClient(string? token = null)
    {
        _httpClient = new HttpClient(CoreTools.GenericHttpClientParameters)
        {
            BaseAddress = GitHubApiBaseUri,
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        if (!string.IsNullOrWhiteSpace(token))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public static Uri GetOAuthLoginUrl(
        string clientId,
        Uri redirectUri,
        IEnumerable<string> scopes
    )
    {
        string query = BuildQueryString(
            new Dictionary<string, string?>
            {
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri.ToString(),
                ["scope"] = string.Join(' ', scopes),
            }
        );
        return new Uri(GitHubBaseUri, "login/oauth/authorize" + query);
    }

    public Task<GitHubOAuthToken> CreateAccessTokenAsync(
        string clientId,
        string clientSecret,
        string code,
        Uri redirectUri
    )
    {
        return PostOAuthFormAsync(
            "login/oauth/access_token",
            new Dictionary<string, string?>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri.ToString(),
            },
            GitHubJsonContext.Default.GitHubOAuthToken
        );
    }

    public Task<GitHubDeviceFlow> InitiateDeviceFlowAsync(
        string clientId,
        IEnumerable<string> scopes,
        CancellationToken cancellationToken = default
    )
    {
        return PostOAuthFormAsync(
            "login/device/code",
            new Dictionary<string, string?>
            {
                ["client_id"] = clientId,
                ["scope"] = string.Join(' ', scopes),
            },
            GitHubJsonContext.Default.GitHubDeviceFlow,
            cancellationToken
        );
    }

    public Task<GitHubOAuthToken> CreateAccessTokenForDeviceFlowAsync(
        string clientId,
        GitHubDeviceFlow deviceFlow,
        CancellationToken cancellationToken = default
    )
    {
        return PostOAuthFormAsync(
            "login/oauth/access_token",
            new Dictionary<string, string?>
            {
                ["client_id"] = clientId,
                ["device_code"] = deviceFlow.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            },
            GitHubJsonContext.Default.GitHubOAuthToken,
            cancellationToken
        );
    }

    public Task<GitHubUser> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        return GetJsonAsync("user", GitHubJsonContext.Default.GitHubUser, cancellationToken);
    }

    public Task<IReadOnlyList<GitHubGist>> GetCurrentUserGistsAsync(
        CancellationToken cancellationToken = default
    )
    {
        return GetJsonAsync(
            "gists",
            GitHubJsonContext.Default.IReadOnlyListGitHubGist,
            cancellationToken
        );
    }

    public Task<GitHubGist> GetGistAsync(string gistId, CancellationToken cancellationToken = default)
    {
        return GetJsonAsync(
            "gists/" + Uri.EscapeDataString(gistId),
            GitHubJsonContext.Default.GitHubGist,
            cancellationToken
        );
    }

    public Task<GitHubGist> CreateGistAsync(
        string description,
        bool isPublic,
        IReadOnlyDictionary<string, string> files,
        CancellationToken cancellationToken = default
    )
    {
        return SendGistAsync(
            HttpMethod.Post,
            "gists",
            description,
            isPublic,
            files,
            cancellationToken
        );
    }

    public Task<GitHubGist> EditGistAsync(
        string gistId,
        string description,
        IReadOnlyDictionary<string, string> files,
        CancellationToken cancellationToken = default
    )
    {
        return SendGistAsync(
            HttpMethod.Patch,
            "gists/" + Uri.EscapeDataString(gistId),
            description,
            isPublic: false,
            files,
            cancellationToken
        );
    }

    private async Task<GitHubGist> SendGistAsync(
        HttpMethod method,
        string relativeUri,
        string description,
        bool isPublic,
        IReadOnlyDictionary<string, string> files,
        CancellationToken cancellationToken
    )
    {
        var request = new GitHubGistRequest
        {
            Description = description,
            IsPublic = method == HttpMethod.Post ? isPublic : null,
            Files = files.ToDictionary(
                file => file.Key,
                file => new GitHubGistFileRequest { Content = file.Value },
                StringComparer.Ordinal
            ),
        };

        using var message = new HttpRequestMessage(method, relativeUri)
        {
            Content = CreateJsonContent(request, GitHubJsonContext.Default.GitHubGistRequest),
        };
        return await SendAsync(message, GitHubJsonContext.Default.GitHubGist, cancellationToken);
    }

    private async Task<T> GetJsonAsync<T>(
        string relativeUri,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken
    )
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, relativeUri);
        return await SendAsync(message, typeInfo, cancellationToken);
    }

    private async Task<T> PostOAuthFormAsync<T>(
        string relativeUri,
        IReadOnlyDictionary<string, string?> values,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default
    )
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(GitHubBaseUri, relativeUri))
        {
            Content = new FormUrlEncodedContent(
                values
                    .Where(value => !string.IsNullOrWhiteSpace(value.Value))
                    .Select(value => new KeyValuePair<string, string>(value.Key, value.Value!))
            ),
        };
        message.Headers.Accept.Clear();
        message.Headers.Accept.ParseAdd("application/json");
        return await SendAsync(message, typeInfo, cancellationToken);
    }

    private async Task<T> SendAsync<T>(
        HttpRequestMessage message,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken
    )
    {
        using HttpResponseMessage response = await _httpClient.SendAsync(message, cancellationToken);
        string content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {GetErrorMessage(content)}"
            );
        }

        return JsonSerializer.Deserialize(content, typeInfo)
            ?? throw new InvalidOperationException("GitHub returned an empty response.");
    }

    private static StringContent CreateJsonContent<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        return new StringContent(
            JsonSerializer.Serialize(value, typeInfo),
            System.Text.Encoding.UTF8,
            "application/json"
        );
    }

    private static string GetErrorMessage(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "No response body.";

        try
        {
            GitHubError? error = JsonSerializer.Deserialize(
                content,
                GitHubJsonContext.Default.GitHubError
            );
            if (!string.IsNullOrWhiteSpace(error?.ErrorDescription))
                return error.ErrorDescription;
            if (!string.IsNullOrWhiteSpace(error?.Error))
                return error.Error;
            if (!string.IsNullOrWhiteSpace(error?.Message))
                return error.Message;
        }
        catch (JsonException)
        {
            return content;
        }

        return content;
    }

    private static string BuildQueryString(IReadOnlyDictionary<string, string?> values)
    {
        string query = string.Join(
            '&',
            values
                .Where(value => !string.IsNullOrWhiteSpace(value.Value))
                .Select(value =>
                    Uri.EscapeDataString(value.Key) + "=" + Uri.EscapeDataString(value.Value!))
        );
        return string.IsNullOrEmpty(query) ? "" : "?" + query;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

public sealed class GitHubOAuthToken
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";
}

public sealed class GitHubDeviceFlow
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = "";

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = "";

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; }
}

public sealed class GitHubUser
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }
}

public sealed class GitHubGist
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("files")]
    public Dictionary<string, GitHubGistFile> Files { get; set; } = [];
}

public sealed class GitHubGistFile
{
    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

internal sealed class GitHubGistRequest
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("public")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsPublic { get; set; }

    [JsonPropertyName("files")]
    public Dictionary<string, GitHubGistFileRequest> Files { get; set; } = [];
}

internal sealed class GitHubGistFileRequest
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

internal sealed class GitHubError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(GitHubOAuthToken))]
[JsonSerializable(typeof(GitHubDeviceFlow))]
[JsonSerializable(typeof(GitHubUser))]
[JsonSerializable(typeof(GitHubGist))]
[JsonSerializable(typeof(GitHubGistFile))]
[JsonSerializable(typeof(GitHubGistRequest))]
[JsonSerializable(typeof(GitHubGistFileRequest))]
[JsonSerializable(typeof(GitHubError))]
[JsonSerializable(typeof(IReadOnlyList<GitHubGist>))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
