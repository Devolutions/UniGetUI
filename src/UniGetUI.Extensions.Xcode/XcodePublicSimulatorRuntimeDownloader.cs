using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace UniGetUI.Extensions.Xcode;

public sealed record XcodeSimulatorRuntimeDownloadPaths(string ImagePath)
{
    public string PartialPath => ImagePath + ".partial";
}

public interface IXcodePublicSimulatorRuntimeDownloader
{
    Task<string> DownloadAsync(
        XcodeSimulatorRuntime runtime, string destinationDirectory,
        Action<XcodeSimulatorRuntimeDownloadPaths> started, Action<XcodesOutputLine>? output,
        CancellationToken cancellationToken);
}

/// <summary>Downloads legacy images from Apple's public CDN without an account or installation.</summary>
public sealed class XcodePublicSimulatorRuntimeDownloader : IXcodePublicSimulatorRuntimeDownloader
{
    private const int BufferSize = 1024 * 1024;
    private const long MaximumBytes = 20L * 1024 * 1024 * 1024;
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(2);
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly HttpClient _client;
    private readonly Func<bool> _isMacOS;
    private readonly Func<Guid> _newId;
    private readonly TimeSpan _transferTimeout;
    private readonly long _maximumBytes;

    public XcodePublicSimulatorRuntimeDownloader()
        : this(Client, OperatingSystem.IsMacOS)
    {
    }

    internal XcodePublicSimulatorRuntimeDownloader(
        HttpClient client, Func<bool> isMacOS, Func<Guid>? newId = null,
        TimeSpan? transferTimeout = null, long maximumBytes = MaximumBytes)
    {
        _client = client;
        _isMacOS = isMacOS;
        _newId = newId ?? Guid.NewGuid;
        _transferTimeout = transferTimeout ?? TransferTimeout;
        _maximumBytes = maximumBytes;
    }

    public static bool Supports(XcodeSimulatorRuntime runtime)
        => runtime.Origin == XcodeSimulatorRuntimeOrigin.AppleCatalog
           && runtime.Distribution == XcodeSimulatorRuntimeDistribution.PublicImage
           && runtime.Platform is "iOS" or "watchOS" or "tvOS"
           && XcodeSimulatorImageFile.IsValidBuild(runtime.Build)
           && XcodeVersion.TryParse(runtime.Version, out _)
           && TryGetPublicUri(runtime.DownloadUrl, out _);

    internal static bool TryGetPublicUri(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Contains('\\')
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? candidate)
            || !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(candidate.Host, "devimages-cdn.apple.com", StringComparison.OrdinalIgnoreCase)
            || !candidate.IsDefaultPort || candidate.UserInfo.Length != 0
            || candidate.Query.Length != 0 || candidate.Fragment.Length != 0
            || candidate.AbsolutePath.Contains('%')
            || !candidate.AbsolutePath.StartsWith("/downloads/xcode/simulators/", StringComparison.Ordinal)
            || !candidate.AbsolutePath.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))
            return false;
        uri = candidate;
        return true;
    }

    public async Task<string> DownloadAsync(
        XcodeSimulatorRuntime runtime, string destinationDirectory,
        Action<XcodeSimulatorRuntimeDownloadPaths> started, Action<XcodesOutputLine>? output,
        CancellationToken cancellationToken)
    {
        if (!_isMacOS())
            throw new XcodesException("Simulator runtimes can only be managed on macOS.");
        if (!Supports(runtime) || !TryGetPublicUri(runtime.DownloadUrl, out Uri? uri))
            throw new XcodesException("This simulator runtime has no supported public download.");
        if (!Path.IsPathFullyQualified(destinationDirectory) || destinationDirectory.Any(char.IsControl)
            || !Directory.Exists(destinationDirectory))
            throw new XcodesException("Choose an existing absolute folder for the simulator runtime image.");
        cancellationToken.ThrowIfCancellationRequested();

        string name = $"{runtime.Platform}-{runtime.Build}-{_newId():N}.dmg";
        var paths = new XcodeSimulatorRuntimeDownloadPaths(Path.Combine(destinationDirectory, name));
        byte[] expectedHash;
        await using (var destination = new FileStream(
            paths.PartialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            started(paths);
            expectedHash = await DownloadImageAsync(uri, destination, output, cancellationToken).ConfigureAwait(false);
        }
        output?.Invoke(new XcodesOutputLine("Verifying copied runtime image..."));
        await XcodeSimulatorImageFile.VerifyAsync(paths.PartialPath, expectedHash, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if ((File.GetAttributes(paths.PartialPath) & FileAttributes.ReparsePoint) != 0)
            throw new XcodesException("The copied simulator runtime disk image is not a regular file.");
        File.Move(paths.PartialPath, paths.ImagePath, overwrite: false);
        return paths.ImagePath;
    }

    private async Task<byte[]> DownloadImageAsync(
        Uri uri, FileStream destination, Action<XcodesOutputLine>? output, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_transferTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using HttpResponseMessage response = await _client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException("The Apple runtime download failed.", null, response.StatusCode);
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "application/x-apple-diskimage", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
                throw new XcodesException("Apple did not return a simulator runtime disk image.");
            if (response.Content.Headers.ContentEncoding.Any(value =>
                    !string.Equals(value, "identity", StringComparison.OrdinalIgnoreCase)))
                throw new XcodesException("Apple returned an unsupported runtime image encoding.");
            long? expectedLength = response.Content.Headers.ContentLength;
            if (expectedLength is <= 0 || expectedLength > _maximumBytes)
                throw new XcodesException("The simulator runtime image has an invalid or unsupported size.");

            deadline.CancelAfter(_transferTimeout);
            await using Stream input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            deadline.CancelAfter(_transferTimeout);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            output?.Invoke(new XcodesOutputLine(expectedLength is null
                ? "Downloading runtime image..." : "Downloading runtime image: 0%"));
            int previousProgress = 0;
            long total = 0;
            byte[] buffer = new byte[BufferSize];
            int count;
            while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (total > _maximumBytes - count)
                    throw new XcodesException("The simulator runtime image has an invalid or unsupported size.");
                await destination.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, count);
                total += count;
                if (expectedLength is { } length)
                {
                    int progress = (int)Math.Min(100, total * 100d / length);
                    if (progress != previousProgress)
                    {
                        output?.Invoke(new XcodesOutputLine($"Downloading runtime image: {progress}%", ReplacesPrevious: true));
                        previousProgress = progress;
                    }
                }
                deadline.CancelAfter(_transferTimeout);
            }
            if (total == 0 || expectedLength is { } declared && total != declared)
                throw new XcodesException("The simulator runtime image download was incomplete.");
            await destination.FlushAsync(deadline.Token).ConfigureAwait(false);
            return hash.GetHashAndReset();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new XcodesException("The Apple runtime download timed out.");
        }
    }
}
