using System.Security.Cryptography;

namespace UniGetUI.PackageEngine.Operations;

internal enum PublishedHashAlgorithm
{
    Md5,
    Sha1,
    Sha256,
    Sha384,
    Sha512,
}

internal readonly record struct PublishedInstallerHash(
    PublishedHashAlgorithm Algorithm,
    byte[] Digest
)
{
    private static readonly Dictionary<string, PublishedHashAlgorithm> _algorithmsByName = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["md5"] = PublishedHashAlgorithm.Md5,
        ["sha1"] = PublishedHashAlgorithm.Sha1,
        ["sha256"] = PublishedHashAlgorithm.Sha256,
        ["sha384"] = PublishedHashAlgorithm.Sha384,
        ["sha512"] = PublishedHashAlgorithm.Sha512,
    };

    private static readonly Dictionary<int, PublishedHashAlgorithm> _algorithmsByDigestLength =
        new()
        {
            [16] = PublishedHashAlgorithm.Md5,
            [20] = PublishedHashAlgorithm.Sha1,
            [32] = PublishedHashAlgorithm.Sha256,
            [48] = PublishedHashAlgorithm.Sha384,
            [64] = PublishedHashAlgorithm.Sha512,
        };

    public static bool TryParse(string? publishedHash, out PublishedInstallerHash parsed)
    {
        foreach (
            string candidate in (publishedHash ?? "").Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            if (TryParseSingle(candidate, out parsed))
                return true;
        }

        parsed = default;
        return false;
    }

    private static bool TryParseSingle(string candidate, out PublishedInstallerHash parsed)
    {
        int separator = candidate.IndexOfAny([':', '-']);
        if (
            separator > 0
            && _algorithmsByName.TryGetValue(
                candidate[..separator],
                out PublishedHashAlgorithm named
            )
        )
        {
            return TryBuild(named, candidate[(separator + 1)..], out parsed);
        }

        return TryBuild(null, candidate, out parsed);
    }

    private static bool TryBuild(
        PublishedHashAlgorithm? expected,
        string value,
        out PublishedInstallerHash parsed
    )
    {
        parsed = default;
        if (!TryDecode(value, out byte[]? digest))
            return false;

        if (!_algorithmsByDigestLength.TryGetValue(digest.Length, out PublishedHashAlgorithm sized))
            return false;

        if (expected is { } named && named != sized)
            return false;

        parsed = new PublishedInstallerHash(sized, digest);
        return true;
    }

    private static bool TryDecode(string value, out byte[] digest)
    {
        string hex = value.Replace(" ", "").Replace("-", "");
        if (hex.Length > 0 && hex.Length % 2 == 0 && hex.All(Uri.IsHexDigit))
        {
            digest = Convert.FromHexString(hex);
            return true;
        }

        Span<byte> buffer = stackalloc byte[64];
        if (Convert.TryFromBase64String(value, buffer, out int written))
        {
            digest = buffer[..written].ToArray();
            return true;
        }

        digest = [];
        return false;
    }

    public async Task<bool> MatchesAsync(string filePath, CancellationToken token)
    {
        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            8192,
            useAsync: true
        );

        byte[] actual = Algorithm switch
        {
            PublishedHashAlgorithm.Md5 => await MD5.HashDataAsync(stream, token),
            PublishedHashAlgorithm.Sha1 => await SHA1.HashDataAsync(stream, token),
            PublishedHashAlgorithm.Sha384 => await SHA384.HashDataAsync(stream, token),
            PublishedHashAlgorithm.Sha512 => await SHA512.HashDataAsync(stream, token),
            _ => await SHA256.HashDataAsync(stream, token),
        };

        return CryptographicOperations.FixedTimeEquals(actual, Digest);
    }
}
