using System.Net.Http;

namespace UniGetUI.Extensions.Xcode;

internal static class XcodePublicCatalogDownload
{
    internal const int MaximumBytes = 8 * 1024 * 1024;
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    internal static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    })
    {
        Timeout = DefaultTimeout,
    };

    internal static async Task<byte[]> GetAsync(
        HttpClient client, Uri uri, TimeSpan timeout, string catalogName, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes)
            throw new XcodesException($"The {catalogName} catalog is too large.");

        await using Stream input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        return await ReadBoundedAsync(input, catalogName, deadline.Token).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream input, string catalogName, CancellationToken cancellationToken)
    {
        if (input.CanSeek && input.Length > MaximumBytes)
            throw new XcodesException($"The {catalogName} catalog is too large.");

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[32 * 1024];
        int read;
        while ((read = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumBytes)
                throw new XcodesException($"The {catalogName} catalog is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
