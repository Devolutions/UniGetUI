// Apple's download-list endpoint and response shape follow XcodesKit, MIT License.
// See THIRD_PARTY_NOTICES.md.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniGetUI.Extensions.Xcode.AppleAuth;

internal sealed record AppleXcodeDownload(string Version, Uri Address, long? SizeBytes, DateOnly? ModifiedDate);

/// <summary>Reads the signed-in account's Apple downloads without inventing build or architecture metadata.</summary>
internal sealed class AppleDownloadsCatalog(HttpClient client, TimeSpan? requestTimeout = null)
{
    internal const int MaximumResponseBytes = 16 * 1024 * 1024;
    internal static readonly Uri DownloadsUri = new("https://developer.apple.com/services-account/QH65B2/downloadws/listDownloads.action");
    private static readonly Uri DownloadHost = new("https://download.developer.apple.com/");

    public async Task<IReadOnlyList<AppleXcodeDownload>> GetAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout ?? TimeSpan.FromMinutes(2));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, DownloadsUri);
            request.Headers.Accept.ParseAdd("application/json");
            using HttpResponseMessage response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new XcodesException($"Apple's download list returned HTTP {(int)response.StatusCode}.")
                {
                    IsAccessFailure = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                };

            byte[] data;
            try
            {
                data = await AppleAuthClient.ReadBoundedAsync(response, timeout.Token, MaximumResponseBytes).ConfigureAwait(false);
            }
            catch (AppleAuthException)
            {
                throw new XcodesException("Apple's download list is too large to read.");
            }
            try
            {
                return Parse(data);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(data);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new XcodesException("Apple did not send its download list in time.");
        }
    }

    internal static AppleDownloadList? DeserializeList(ReadOnlySpan<byte> data)
        => JsonSerializer.Deserialize(data, AppleDownloadsJsonContext.Default.AppleDownloadList);

    internal static IReadOnlyList<AppleXcodeDownload> Parse(ReadOnlySpan<byte> data)
    {
        AppleDownloadList? list;
        try
        {
            list = DeserializeList(data);
        }
        catch (JsonException)
        {
            throw new XcodesException("Apple's download list could not be read.");
        }
        if (list?.ResultCode is { } code && code != 0)
            throw new XcodesException($"Apple could not provide its download list (result {code}).");
        if (list?.Downloads is null)
            throw new XcodesException("Apple did not provide a download list.");

        List<AppleXcodeDownload> result = [];
        foreach (AppleDownloadItem? item in list.Downloads)
        {
            string? name = item?.Name?.Trim();
            if (name is null || name.Length > 128 || !name.StartsWith("Xcode ", StringComparison.OrdinalIgnoreCase)
                || !XcodeVersion.TryParse(name, out _))
                continue;
            string version = name["Xcode ".Length..].Trim();
            DateOnly? modified = DateTime.TryParseExact(item!.DateModified, "MM/dd/yy HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime date) ? DateOnly.FromDateTime(date) : null;
            foreach (AppleDownloadFile? file in item.Files ?? [])
            {
                Uri? address = Address(file?.RemotePath);
                if (address is not null)
                    result.Add(new AppleXcodeDownload(version, address, file?.FileSize is > 0 ? file.FileSize : null, modified));
            }
        }
        return result;
    }

    private static Uri? Address(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 8192
            || path.StartsWith("//", StringComparison.Ordinal)
            || path.Any(character => char.IsControl(character) || character is '\\' or '?' or '#')
            || !Uri.TryCreate(DownloadHost, path, out Uri? address))
            return null;
        return XcodeReleasesCatalog.TryDownload(address.AbsoluteUri);
    }
}

internal sealed class AppleDownloadList
{
    public int? ResultCode { get; set; }
    public AppleDownloadItem?[]? Downloads { get; set; }
}

internal sealed class AppleDownloadItem
{
    public string? Name { get; set; }
    public string? DateModified { get; set; }
    public AppleDownloadFile?[]? Files { get; set; }
}

internal sealed class AppleDownloadFile
{
    public string? RemotePath { get; set; }
    public long? FileSize { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppleDownloadList))]
internal partial class AppleDownloadsJsonContext : JsonSerializerContext;
