using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using UniGetUI.Core.Logging;

namespace UniGetUI.Core.Tools;

public readonly record struct UpdaterDownloadIdentity(
    string Version,
    string ExpectedHash,
    string Url
);

public sealed class UpdaterInstallerDownloadMetadata
{
    public string Version { get; set; } = "";
    public string ExpectedHash { get; set; } = "";
    public string Url { get; set; } = "";
    public string ETag { get; set; } = "";
    public DateTimeOffset? LastModified { get; set; }
    public long? TotalLength { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class UpdaterDownloadFailureState
{
    public string Version { get; set; } = "";
    public string ExpectedHash { get; set; } = "";
    public string Url { get; set; } = "";
    public string FailureClass { get; set; } = "";
    public int AttemptCount { get; set; }
    public DateTime LastFailureUtc { get; set; }
    public DateTime NextAttemptUtc { get; set; }
}

public readonly record struct UpdaterDownloadResult(
    string PartialPath,
    long BytesInPartialFile,
    long? TotalBytes,
    HttpStatusCode StatusCode,
    bool Resumed
);

public static class UpdaterDownloadEngine
{
    public static readonly TimeSpan DefaultAutomaticUpdateCheckInterval = TimeSpan.FromMinutes(60);

    private const int CopyBufferSize = 81920;
    private const string PartialMarker = ".part";
    private const string MetadataSuffix = ".json";
    private const string FailureStateSuffix = ".download-failed.json";
    private static readonly TimeSpan BaseFailureBackoff = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan MaxFailureBackoff = TimeSpan.FromHours(24);
    private const double MaxJitterRatio = 0.20;

    public static string GetPartialPath(string destinationPath)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        string fileName = Path.GetFileName(destinationPath);
        string extension = Path.GetExtension(fileName);
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        string partialFileName = $"{fileNameWithoutExtension}{PartialMarker}{extension}";
        return string.IsNullOrEmpty(directory)
            ? partialFileName
            : Path.Join(directory, partialFileName);
    }

    public static string GetPartialMetadataPath(string destinationPath)
    {
        return GetPartialPath(destinationPath) + MetadataSuffix;
    }

    public static string GetFailureStatePath(string destinationPath)
    {
        return destinationPath + FailureStateSuffix;
    }

    public static async Task<UpdaterDownloadResult> DownloadInstallerPartAsync(
        HttpClient client,
        UpdaterDownloadIdentity identity,
        string destinationPath,
        Action<string>? log = null,
        CancellationToken cancellationToken = default
    )
    {
        return await DownloadInstallerPartAsync(
            client,
            identity,
            destinationPath,
            allowRestart: true,
            log,
            cancellationToken
        );
    }

    private static async Task<UpdaterDownloadResult> DownloadInstallerPartAsync(
        HttpClient client,
        UpdaterDownloadIdentity identity,
        string destinationPath,
        bool allowRestart,
        Action<string>? log,
        CancellationToken cancellationToken
    )
    {
        string partialPath = GetPartialPath(destinationPath);
        string metadataPath = GetPartialMetadataPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath) ?? ".");

        UpdaterInstallerDownloadMetadata? metadata = TryReadMetadata(metadataPath, log);
        bool canUsePartial = IsCompatible(metadata, identity);
        if (!canUsePartial)
        {
            DeletePartialDownload(destinationPath, log);
            metadata = null;
        }

        long partialLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (metadata?.TotalLength is > 0 && partialLength > metadata.TotalLength.Value)
        {
            log?.Invoke(
                $"Partial updater download is larger than expected ({partialLength}>{metadata.TotalLength}); restarting."
            );
            DeletePartialDownload(destinationPath, log);
            metadata = null;
            partialLength = 0;
        }

        if (metadata?.TotalLength is > 0 && partialLength == metadata.TotalLength.Value)
        {
            log?.Invoke(
                $"Partial updater download is already complete ({partialLength} bytes); validating cached partial."
            );
            return new UpdaterDownloadResult(
                partialPath,
                partialLength,
                metadata.TotalLength,
                HttpStatusCode.NotModified,
                Resumed: false
            );
        }

        RangeHeaderValue? range = null;
        RangeConditionHeaderValue? ifRange = null;
        bool requestResume = partialLength > 0 && TryConfigureResume(metadata, partialLength, out range, out ifRange);
        if (partialLength > 0 && !requestResume)
        {
            log?.Invoke("Partial updater download cannot be resumed safely; restarting.");
            DeletePartialDownload(destinationPath, log);
            metadata = null;
            partialLength = 0;
        }

        using HttpRequestMessage request = new(HttpMethod.Get, identity.Url);
        if (requestResume)
        {
            request.Headers.Range = range!;
            request.Headers.IfRange = ifRange;
            log?.Invoke($"Resuming updater download from byte {partialLength}.");
        }

        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );

        if (response.StatusCode is HttpStatusCode.RequestedRangeNotSatisfiable && allowRestart)
        {
            log?.Invoke("Updater download range was not satisfiable; restarting from byte 0.");
            DeletePartialDownload(destinationPath, log);
            return await DownloadInstallerPartAsync(
                client,
                identity,
                destinationPath,
                allowRestart: false,
                log,
                cancellationToken
            );
        }

        response.EnsureSuccessStatusCode();

        bool appendToPartial = response.StatusCode is HttpStatusCode.PartialContent;
        if (appendToPartial && !IsValidPartialResponse(response, partialLength))
        {
            if (!allowRestart)
            {
                throw new InvalidDataException(
                    "The updater download server returned an invalid partial content range."
                );
            }

            log?.Invoke("Updater download returned an invalid partial response; restarting.");
            DeletePartialDownload(destinationPath, log);
            return await DownloadInstallerPartAsync(
                client,
                identity,
                destinationPath,
                allowRestart: false,
                log,
                cancellationToken
            );
        }

        if (appendToPartial && !IsResumeValidatorCompatible(metadata, response))
        {
            if (!allowRestart)
            {
                throw new InvalidDataException(
                    "The updater download server returned partial content with a changed validator."
                );
            }

            log?.Invoke("Updater download validator changed during resume; restarting.");
            DeletePartialDownload(destinationPath, log);
            return await DownloadInstallerPartAsync(
                client,
                identity,
                destinationPath,
                allowRestart: false,
                log,
                cancellationToken
            );
        }

        if (!appendToPartial && partialLength > 0)
        {
            log?.Invoke("Updater download server ignored Range; restarting from byte 0.");
            partialLength = 0;
        }

        UpdaterInstallerDownloadMetadata newMetadata = CreateMetadata(
            identity,
            response,
            appendToPartial
        );
        WriteMetadata(metadataPath, newMetadata, log);

        FileMode fileMode = appendToPartial ? FileMode.Append : FileMode.Create;
        await using (FileStream fileStream = new(
            partialPath,
            fileMode,
            FileAccess.Write,
            FileShare.Read,
            CopyBufferSize,
            useAsync: true
        ))
        await using (Stream downloadStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            await downloadStream.CopyToAsync(fileStream, cancellationToken);
        }

        long bytesInPartial = new FileInfo(partialPath).Length;
        newMetadata.TotalLength ??= bytesInPartial;
        newMetadata.UpdatedUtc = DateTime.UtcNow;
        WriteMetadata(metadataPath, newMetadata, log);

        return new UpdaterDownloadResult(
            partialPath,
            bytesInPartial,
            newMetadata.TotalLength,
            response.StatusCode,
            Resumed: appendToPartial
        );
    }

    public static void PromotePartialDownload(string destinationPath, Action<string>? log = null)
    {
        string partialPath = GetPartialPath(destinationPath);
        if (!File.Exists(partialPath))
        {
            throw new FileNotFoundException("The completed updater partial file was not found.", partialPath);
        }

        File.Move(partialPath, destinationPath, overwrite: true);
        DeleteFileIfExists(GetPartialMetadataPath(destinationPath), log);
    }

    public static void DeletePartialDownload(string destinationPath, Action<string>? log = null)
    {
        DeleteFileIfExists(GetPartialPath(destinationPath), log);
        DeleteFileIfExists(GetPartialMetadataPath(destinationPath), log);
    }

    public static bool IsFailureBackoffActive(
        string statePath,
        UpdaterDownloadIdentity identity,
        DateTime utcNow,
        out TimeSpan remaining,
        out UpdaterDownloadFailureState? state,
        Action<string>? log = null
    )
    {
        remaining = TimeSpan.Zero;
        state = TryReadFailureState(statePath, log);
        if (state is null || !IsCompatible(state, identity))
        {
            return false;
        }

        remaining = state.NextAttemptUtc - utcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }

        if (remaining > MaxFailureBackoff + TimeSpan.FromHours(1))
        {
            log?.Invoke(
                $"Ignoring updater download backoff with unexpected future timestamp ({state.NextAttemptUtc:O})."
            );
            return false;
        }

        return true;
    }

    public static void RecordFailure(
        string statePath,
        UpdaterDownloadIdentity identity,
        string failureClass,
        DateTime utcNow,
        Action<string>? log = null
    )
    {
        try
        {
            UpdaterDownloadFailureState? previous = TryReadFailureState(statePath, log);
            int attemptCount = previous is not null
                    && IsCompatible(previous, identity)
                    && string.Equals(
                        previous.FailureClass,
                        failureClass,
                        StringComparison.OrdinalIgnoreCase
                    )
                ? previous.AttemptCount + 1
                : 1;
            TimeSpan delay = CalculateFailureBackoff(identity, failureClass, attemptCount);
            UpdaterDownloadFailureState state = new()
            {
                Version = identity.Version,
                ExpectedHash = identity.ExpectedHash,
                Url = identity.Url,
                FailureClass = failureClass,
                AttemptCount = attemptCount,
                LastFailureUtc = utcNow,
                NextAttemptUtc = utcNow + delay,
            };

            string? directory = Path.GetDirectoryName(statePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            WriteFailureState(statePath, state);
            log?.Invoke(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Recorded updater download failure '{0}' attempt {1}; next automatic attempt after {2:O}.",
                    failureClass,
                    attemptCount,
                    state.NextAttemptUtc
                )
            );
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not record updater download failure state: {ex.Message}");
        }
    }

    public static void ClearFailureState(string statePath, Action<string>? log = null)
    {
        DeleteFileIfExists(statePath, log);
    }

    public static TimeSpan CalculateFailureBackoff(
        UpdaterDownloadIdentity identity,
        string failureClass,
        int attemptCount
    )
    {
        if (attemptCount < 1)
        {
            attemptCount = 1;
        }

        int exponent = Math.Min(attemptCount - 1, 10);
        double multiplier = Math.Pow(2, exponent);
        TimeSpan delay = TimeSpan.FromTicks((long)(BaseFailureBackoff.Ticks * multiplier));
        if (delay > MaxFailureBackoff)
        {
            delay = MaxFailureBackoff;
        }

        double jitterRatio = GetStableJitterRatio(identity, failureClass, attemptCount);
        TimeSpan jitter = TimeSpan.FromTicks((long)(delay.Ticks * jitterRatio));
        TimeSpan jitteredDelay = delay + jitter;
        return jitteredDelay > MaxFailureBackoff ? MaxFailureBackoff : jitteredDelay;
    }

    private static bool TryConfigureResume(
        UpdaterInstallerDownloadMetadata? metadata,
        long partialLength,
        out RangeHeaderValue range,
        out RangeConditionHeaderValue? ifRange
    )
    {
        range = new RangeHeaderValue(partialLength, null);
        ifRange = null;
        if (metadata is null)
        {
            return false;
        }

        if (
            !string.IsNullOrWhiteSpace(metadata.ETag)
            && EntityTagHeaderValue.TryParse(metadata.ETag, out EntityTagHeaderValue? entityTag)
        )
        {
            ifRange = new RangeConditionHeaderValue(entityTag);
            return true;
        }

        if (metadata.LastModified is not null)
        {
            ifRange = new RangeConditionHeaderValue(metadata.LastModified.Value);
            return true;
        }

        return false;
    }

    private static bool IsValidPartialResponse(HttpResponseMessage response, long expectedStart)
    {
        ContentRangeHeaderValue? contentRange = response.Content.Headers.ContentRange;
        return contentRange?.From == expectedStart;
    }

    private static bool IsResumeValidatorCompatible(
        UpdaterInstallerDownloadMetadata? metadata,
        HttpResponseMessage response
    )
    {
        if (metadata is null)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(metadata.ETag) && response.Headers.ETag is not null)
        {
            return string.Equals(
                metadata.ETag,
                response.Headers.ETag.ToString(),
                StringComparison.Ordinal
            );
        }

        if (metadata.LastModified is not null && response.Content.Headers.LastModified is not null)
        {
            return metadata.LastModified.Value.Equals(response.Content.Headers.LastModified.Value);
        }

        return true;
    }

    private static UpdaterInstallerDownloadMetadata CreateMetadata(
        UpdaterDownloadIdentity identity,
        HttpResponseMessage response,
        bool partialResponse
    )
    {
        long? totalLength = partialResponse
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;
        return new UpdaterInstallerDownloadMetadata
        {
            Version = identity.Version,
            ExpectedHash = identity.ExpectedHash,
            Url = identity.Url,
            ETag = response.Headers.ETag?.ToString() ?? "",
            LastModified = response.Content.Headers.LastModified,
            TotalLength = totalLength,
            UpdatedUtc = DateTime.UtcNow,
        };
    }

    private static bool IsCompatible(
        UpdaterInstallerDownloadMetadata? metadata,
        UpdaterDownloadIdentity identity
    )
    {
        return metadata is not null
            && string.Equals(metadata.Version, identity.Version, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                metadata.ExpectedHash,
                identity.ExpectedHash,
                StringComparison.OrdinalIgnoreCase
            )
            && string.Equals(metadata.Url, identity.Url, StringComparison.Ordinal);
    }

    private static bool IsCompatible(
        UpdaterDownloadFailureState state,
        UpdaterDownloadIdentity identity
    )
    {
        return string.Equals(state.Version, identity.Version, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                state.ExpectedHash,
                identity.ExpectedHash,
                StringComparison.OrdinalIgnoreCase
            )
            && string.Equals(state.Url, identity.Url, StringComparison.Ordinal);
    }

    private static UpdaterInstallerDownloadMetadata? TryReadMetadata(
        string metadataPath,
        Action<string>? log
    )
    {
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            return DeserializeJson(
                File.ReadAllText(metadataPath),
                UpdaterDownloadJsonContext.Default.UpdaterInstallerDownloadMetadata
            );
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not read updater partial metadata; restarting download: {ex.Message}");
            return null;
        }
    }

    private static UpdaterDownloadFailureState? TryReadFailureState(
        string statePath,
        Action<string>? log
    )
    {
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            return DeserializeJson(
                File.ReadAllText(statePath),
                UpdaterDownloadJsonContext.Default.UpdaterDownloadFailureState
            );
        }
        catch (Exception ex)
        {
            log?.Invoke($"Ignoring unreadable updater download failure state: {ex.Message}");
            return null;
        }
    }

    private static void WriteMetadata(
        string metadataPath,
        UpdaterInstallerDownloadMetadata metadata,
        Action<string>? log
    )
    {
        try
        {
            WriteJsonFile(
                metadataPath,
                SerializeJson(
                    metadata,
                    UpdaterDownloadJsonContext.Default.UpdaterInstallerDownloadMetadata
                )
            );
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not write updater partial metadata: {ex.Message}");
            throw;
        }
    }

    private static void WriteFailureState(string statePath, UpdaterDownloadFailureState state)
    {
        WriteJsonFile(
            statePath,
            SerializeJson(
                state,
                UpdaterDownloadJsonContext.Default.UpdaterDownloadFailureState
            )
        );
    }

    private static T? DeserializeJson<T>(string json, JsonTypeInfo<T> typeInfo)
    {
        return JsonSerializer.Deserialize(json, typeInfo);
    }

    private static string SerializeJson<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        return JsonSerializer.Serialize(value, typeInfo);
    }

    private static void WriteJsonFile(string path, string json)
    {
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void DeleteFileIfExists(string path, Action<string>? log)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not delete '{path}': {ex.Message}");
        }
    }

    private static double GetStableJitterRatio(
        UpdaterDownloadIdentity identity,
        string failureClass,
        int attemptCount
    )
    {
        string key =
            $"{identity.Version}|{identity.ExpectedHash}|{identity.Url}|{failureClass}|{attemptCount}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(hash);
        return value / (double)ushort.MaxValue * MaxJitterRatio;
    }
}

[JsonSourceGenerationOptions(AllowTrailingCommas = true, WriteIndented = true)]
[JsonSerializable(typeof(UpdaterInstallerDownloadMetadata))]
[JsonSerializable(typeof(UpdaterDownloadFailureState))]
internal sealed partial class UpdaterDownloadJsonContext : JsonSerializerContext;
