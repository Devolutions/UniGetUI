using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.Shared;

internal static class AutoUpdaterHelpers
{
    internal static bool IsSourceUrlAllowed(string url, bool allowUnsafeUrls)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (allowUnsafeUrls)
        {
            Logger.Warn($"Registry override enabled: allowing potentially unsafe updater URL {url}");
            return true;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.Host.Equals("devolutions.net", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".devolutions.net", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals(
                "release-assets.githubusercontent.com",
                StringComparison.OrdinalIgnoreCase
            );
    }

    internal static ProductInfoFile SelectInstallerFile(List<ProductInfoFile> files)
    {
        string targetArch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => "x64",
        };

        ProductInfoFile? match = files.FirstOrDefault(file =>
            file.Type.Equals("exe", StringComparison.OrdinalIgnoreCase)
            && file.Arch.Equals(targetArch, StringComparison.OrdinalIgnoreCase)
        );

        match ??= files.FirstOrDefault(file =>
            file.Type.Equals("exe", StringComparison.OrdinalIgnoreCase)
            && file.Arch.Equals("Any", StringComparison.OrdinalIgnoreCase)
        );

        match ??= files.FirstOrDefault(file =>
            file.Type.Equals("msi", StringComparison.OrdinalIgnoreCase)
            && file.Arch.Equals(targetArch, StringComparison.OrdinalIgnoreCase)
        );

        match ??= files.FirstOrDefault(file =>
            file.Type.Equals("msi", StringComparison.OrdinalIgnoreCase)
            && file.Arch.Equals("Any", StringComparison.OrdinalIgnoreCase)
        );

        if (match is null)
        {
            throw new KeyNotFoundException(
                $"No compatible installer file found in productinfo for architecture '{targetArch}'"
            );
        }

        return match;
    }

    internal static Version ParseVersionOrFallback(
        string? rawVersion,
        Version fallbackVersion,
        Action<string>? logWarning = null
    )
    {
        if (TryExtractNormalizedCoreVersion(rawVersion, out Version? normalized, out _))
        {
            return normalized!;
        }

        string message = $"Could not parse version '{rawVersion}', using fallback '{fallbackVersion}'";
        if (logWarning is not null)
        {
            logWarning(message);
        }
        else
        {
            Logger.Warn(message);
        }

        return fallbackVersion;
    }

    internal static bool TryParseUpdaterVersion(string? rawVersion, out SemanticVersion version)
    {
        version = SemanticVersion.Invalid(rawVersion ?? string.Empty);
        if (!TryExtractNormalizedCoreVersion(rawVersion, out Version? normalizedCore, out string? preRelease))
        {
            return false;
        }

        string canonical = preRelease is not null
            ? $"{normalizedCore}-{CanonicalizeUpdaterPreRelease(preRelease)}"
            : normalizedCore!.ToString();

        return SemanticVersion.TryParse(canonical, SemVerLabels.CaseInsensitive, out version);
    }

    // Canonicalizes updater pre-release labels such as "beta1" or "rc10" into dot-separated
    // SemVer identifiers ("beta.1", "rc.10") before passing to SemanticVersion. This keeps
    // SemanticVersion strictly SemVer 2.0 compliant for package managers (npm, Cargo, NuGet)
    // while giving the self-updater a transitive total order where beta1 < beta2 < beta10.
    internal static string CanonicalizeUpdaterPreRelease(string preRelease)
    {
        string[] segments = preRelease.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            int digitStart = 0;
            while (digitStart < segment.Length && char.IsAsciiLetter(segment[digitStart]))
            {
                digitStart++;
            }

            if (digitStart == 0 || digitStart == segment.Length)
            {
                continue;
            }

            ReadOnlySpan<char> trailing = segment.AsSpan(digitStart);
            bool allDigits = true;
            for (int j = 0; j < trailing.Length; j++)
            {
                if (!char.IsAsciiDigit(trailing[j]))
                {
                    allDigits = false;
                    break;
                }
            }

            if (
                allDigits
                && int.TryParse(
                    trailing,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int numericSuffix
                )
            )
            {
                segments[i] =
                    $"{segment[..digitStart]}.{numericSuffix.ToString(CultureInfo.InvariantCulture)}";
            }
        }

        return string.Join('.', segments);
    }

    internal static SemanticVersion ParseUpdaterVersionOrFallback(
        string? rawVersion,
        Version fallbackVersion,
        Action<string>? logWarning = null
    )
    {
        if (TryParseUpdaterVersion(rawVersion, out SemanticVersion parsed))
        {
            return parsed;
        }

        string message = $"Could not parse version '{rawVersion}', using fallback '{fallbackVersion}'";
        if (logWarning is not null)
        {
            logWarning(message);
        }
        else
        {
            Logger.Warn(message);
        }

        return SemanticVersion.TryParse(
            fallbackVersion.ToString(),
            SemVerLabels.CaseInsensitive,
            out SemanticVersion fallback
        )
            ? fallback
            : SemanticVersion.Invalid(rawVersion ?? string.Empty);
    }

    internal static int CompareUpdaterVersions(
        string? currentRaw,
        string? availableRaw,
        Version? currentFallback = null,
        Action<string>? logWarning = null
    )
    {
        SemanticVersion current = ParseUpdaterVersionOrFallback(
            currentRaw,
            currentFallback ?? new Version(0, 0, 0, 0),
            logWarning
        );
        SemanticVersion available = ParseUpdaterVersionOrFallback(
            availableRaw,
            new Version(0, 0, 0, 0),
            logWarning
        );
        return current.CompareTo(available);
    }

    internal static bool IsUpdateAvailable(
        string? currentRaw,
        string? availableRaw,
        Version? currentFallback = null,
        Action<string>? logWarning = null
    ) => CompareUpdaterVersions(currentRaw, availableRaw, currentFallback, logWarning) < 0;

    internal static bool VersionsMatch(string? a, string? b)
    {
        if (
            TryParseUpdaterVersion(a, out SemanticVersion va)
            && TryParseUpdaterVersion(b, out SemanticVersion vb)
        )
        {
            return va.CompareTo(vb) == 0;
        }

        string sa = (a ?? string.Empty).Trim().TrimStart('v', 'V');
        string sb = (b ?? string.Empty).Trim().TrimStart('v', 'V');
        return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPreReleaseVersion(string? rawVersion) =>
        TryParseUpdaterVersion(rawVersion, out SemanticVersion parsed) && parsed.IsPreRelease;

    internal static ProductInfoChannel? SelectChannel(
        ProductInfoChannel? current,
        ProductInfoChannel? beta,
        bool useBeta
    )
    {
        if (!useBeta)
        {
            return current;
        }

        if (beta is null)
        {
            return current;
        }

        if (current is null)
        {
            return beta;
        }

        // Prefer Beta only when its version is strictly newer than Current; once stable
        // catches up (e.g. 2026.3.2.0 > 2026.3.2-beta3 or 2026.3.2.0 == 2026.3.2.0),
        // prefer Current so beta users upgrade seamlessly to the final release.
        return IsUpdateAvailable(current.Version, beta.Version) ? beta : current;
    }

    internal static ProductInfoChannel? SelectChannel(ProductInfoEntry product, bool useBeta) =>
        SelectChannel(product.Current, product.Beta, useBeta);

    private static bool TryExtractNormalizedCoreVersion(
        string? rawVersion,
        out Version? normalizedCore,
        out string? preRelease
    )
    {
        normalizedCore = null;
        preRelease = null;

        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return false;
        }

        string sanitized = rawVersion.Trim().TrimStart('v', 'V');
        int plusIndex = sanitized.IndexOf('+');
        if (plusIndex >= 0)
        {
            sanitized = sanitized[..plusIndex];
        }

        int dashIndex = sanitized.IndexOf('-');
        string corePart;
        if (dashIndex >= 0)
        {
            corePart = sanitized[..dashIndex];
            preRelease = sanitized[(dashIndex + 1)..];
            if (string.IsNullOrWhiteSpace(preRelease))
            {
                return false;
            }
        }
        else
        {
            corePart = sanitized;
        }

        if (!Version.TryParse(corePart, out Version? parsed))
        {
            return false;
        }

        normalizedCore = parsed.Revision == 0
            ? new Version(parsed.Major, parsed.Minor, parsed.Build)
            : parsed;
        return true;
    }

    internal static bool IsDefaultDevVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return false;
        }

        string sanitized = rawVersion.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(sanitized, out Version? parsed))
        {
            return false;
        }

        return parsed.Major == 1
            && parsed.Minor == 0
            && parsed.Build <= 0
            && parsed.Revision <= 0;
    }

    internal static string NormalizeThumbprint(string thumbprint)
    {
        char[] normalized = thumbprint.ToLowerInvariant().Where(char.IsAsciiHexDigit).ToArray();

        return new string(normalized);
    }

    internal static string? GetRegistryString(RegistryKey? key, string valueName)
    {
#pragma warning disable CA1416
        object? value = key?.GetValue(valueName);
#pragma warning restore CA1416
        if (value is null)
        {
            return null;
        }

        string? parsedValue = value.ToString();
        if (string.IsNullOrWhiteSpace(parsedValue))
        {
            return null;
        }

        return parsedValue.Trim();
    }

#if DEBUG
    internal static bool GetRegistryBool(RegistryKey? key, string valueName)
    {
#pragma warning disable CA1416
        object? value = key?.GetValue(valueName);
#pragma warning restore CA1416
        if (value is null)
        {
            return false;
        }

        if (value is int intValue)
        {
            return intValue != 0;
        }

        if (value is long longValue)
        {
            return longValue != 0;
        }

        string normalized = value.ToString()?.Trim() ?? "";
        return normalized.Equals("1", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
#endif

    internal sealed class ProductInfoEntry
    {
        public ProductInfoChannel? Current { get; set; }
        public ProductInfoChannel? Beta { get; set; }
    }

    internal sealed class ProductInfoChannel
    {
        public string Version { get; set; } = string.Empty;
        public List<ProductInfoFile> Files { get; set; } = [];
    }

    internal sealed class ProductInfoFile
    {
        public string Arch { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
    }

    internal sealed class UpdaterAttemptCoordinator
    {
        internal sealed class AttemptScope : IDisposable
        {
            private readonly UpdaterAttemptCoordinator _owner;
            private readonly CancellationTokenSource _consentCts = new();
            private bool _isManualCheck;
            private string? _awaitingConsentVersion;
            private bool _isSuperseded;

            internal AttemptScope(UpdaterAttemptCoordinator owner, bool useBeta, bool isManualCheck)
            {
                _owner = owner;
                UseBeta = useBeta;
                _isManualCheck = isManualCheck;
            }

            public bool UseBeta { get; }

            public bool IsManualCheck
            {
                get
                {
                    lock (_owner._lock)
                    {
                        return _isManualCheck;
                    }
                }
            }

            public bool IsSuperseded
            {
                get
                {
                    lock (_owner._lock)
                    {
                        return _isSuperseded;
                    }
                }
            }

            public CancellationToken ConsentCancellationToken => _consentCts.Token;

            public void MarkAwaitingConsent(string versionName)
            {
                bool shouldCancel = false;
                lock (_owner._lock)
                {
                    if (_isSuperseded)
                    {
                        shouldCancel = true;
                    }
                    else
                    {
                        _awaitingConsentVersion = versionName;
                    }
                }

                if (shouldCancel)
                {
                    try
                    {
                        _consentCts.Cancel();
                    }
                    catch (ObjectDisposedException) { }
                }
            }

            public void ClearAwaitingConsent()
            {
                lock (_owner._lock)
                {
                    _awaitingConsentVersion = null;
                }
            }

            internal string? AwaitingConsentVersionNoLock => _awaitingConsentVersion;

            internal bool IsSupersededNoLock => _isSuperseded;

            internal bool PromoteToManualNoLock()
            {
                if (_isManualCheck)
                {
                    return false;
                }

                _isManualCheck = true;
                return true;
            }

            internal void MarkSupersededNoLock()
            {
                _isSuperseded = true;
                _awaitingConsentVersion = null;
                try
                {
                    _consentCts.Cancel();
                }
                catch (ObjectDisposedException) { }
            }

            public void Dispose()
            {
                _consentCts.Dispose();
            }
        }

        private readonly Lock _lock = new();
        private Task<bool>? _activeAttempt;
        private AttemptScope? _activeScope;

        public bool IsActive
        {
            get
            {
                lock (_lock)
                {
                    return _activeAttempt is { IsCompleted: false };
                }
            }
        }

        public bool IsManualCheck
        {
            get
            {
                lock (_lock)
                {
                    return _activeScope is not null && _activeScope.IsManualCheck;
                }
            }
        }

        public bool? ActiveUseBeta
        {
            get
            {
                lock (_lock)
                {
                    return _activeAttempt is { IsCompleted: false } && _activeScope is not null
                        ? _activeScope.UseBeta
                        : null;
                }
            }
        }

        public void CancelIfChannelMismatch(bool currentUseBeta)
        {
            lock (_lock)
            {
                if (
                    _activeAttempt is { IsCompleted: false }
                    && _activeScope is not null
                    && _activeScope.UseBeta != currentUseBeta
                )
                {
                    _activeScope.MarkSupersededNoLock();
                }
            }
        }

        public Task<bool> RunOrJoinAsync(
            bool manualCheck,
            bool useBeta,
            Func<AttemptScope, Task<bool>> attemptFactory,
            Action? onJoinedActiveAttempt = null,
            Action<string>? onManualJoinWhileAwaitingConsent = null,
            Action? onManualJoinWhileBusy = null
        )
        {
            Task<bool> taskToAwait;
            bool joinedExisting = false;
            bool upgradedToManual = false;
            string? pendingConsentVersion = null;

            lock (_lock)
            {
                if (_activeAttempt is { IsCompleted: false } existing && _activeScope is not null)
                {
                    if (_activeScope.UseBeta == useBeta && !_activeScope.IsSupersededNoLock)
                    {
                        taskToAwait = existing;
                        joinedExisting = true;
                        if (manualCheck)
                        {
                            upgradedToManual = _activeScope.PromoteToManualNoLock();
                            pendingConsentVersion = _activeScope.AwaitingConsentVersionNoLock;
                        }
                    }
                    else
                    {
                        _activeScope.MarkSupersededNoLock();
                        var nextScope = new AttemptScope(this, useBeta, manualCheck);
                        _activeScope = nextScope;
                        Task<bool> started = RunCoreAsync(existing, nextScope, attemptFactory);
                        if (ReferenceEquals(_activeScope, nextScope))
                        {
                            _activeAttempt = started;
                        }

                        taskToAwait = started;
                    }
                }
                else
                {
                    var scope = new AttemptScope(this, useBeta, manualCheck);
                    _activeScope = scope;
                    Task<bool> started = RunCoreAsync(previousAttempt: null, scope, attemptFactory);
                    if (ReferenceEquals(_activeScope, scope))
                    {
                        _activeAttempt = started;
                    }

                    taskToAwait = started;
                }
            }

            if (joinedExisting)
            {
                onJoinedActiveAttempt?.Invoke();
                if (manualCheck)
                {
                    if (pendingConsentVersion is not null)
                    {
                        onManualJoinWhileAwaitingConsent?.Invoke(pendingConsentVersion);
                    }
                    else if (upgradedToManual)
                    {
                        onManualJoinWhileBusy?.Invoke();
                    }
                }
            }

            return taskToAwait;
        }

        private async Task<bool> RunCoreAsync(
            Task<bool>? previousAttempt,
            AttemptScope scope,
            Func<AttemptScope, Task<bool>> attemptFactory
        )
        {
            try
            {
                if (previousAttempt is not null)
                {
                    try
                    {
                        await previousAttempt.ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore failures from the superseded attempt so the new channel check still runs.
                    }
                }

                if (scope.IsSuperseded)
                {
                    return true;
                }

                return await attemptFactory(scope);
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_activeScope, scope))
                    {
                        _activeAttempt = null;
                        _activeScope = null;
                    }
                }

                scope.Dispose();
            }
        }
    }
}
