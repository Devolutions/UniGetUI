// Ported from xcodes (Sources/AppleAPI/Hashcash.swift), MIT License, Copyright (c) 2019 Robots and Pencils.
// See THIRD_PARTY_NOTICES.md in this project.
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UniGetUI.Extensions.Xcode.AppleAuth;

/// <summary>Mints the proof-of-work token Apple's sign-in service asks for in its X-Apple-HC-* headers.</summary>
public static class AppleHashcash
{
    internal const int MaximumBits = 32;
    internal const int MaximumAttempts = 1 << 28;

    public static string Make(int bits, string challenge, DateTime utcNow, CancellationToken cancellationToken = default)
        => Make(bits, challenge, utcNow.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture), cancellationToken);

    internal static string Make(int bits, string challenge, string date, CancellationToken cancellationToken = default)
    {
        if (bits is < 0 or > MaximumBits)
            throw new ArgumentOutOfRangeException(nameof(bits), "Apple requested an unsupported proof of work.");
        if (string.IsNullOrEmpty(challenge) || challenge.Length > 512 || challenge.Any(char.IsControl))
            throw new ArgumentException("Apple sent an invalid proof-of-work challenge.", nameof(challenge));

        string prefix = string.Create(CultureInfo.InvariantCulture,
            $"1:{bits}:{date}:{challenge}::");
        for (int counter = 0; counter < MaximumAttempts; counter++)
        {
            if ((counter & 0xFFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            string candidate = prefix + counter.ToString(CultureInfo.InvariantCulture);
            byte[] digest = SHA1.HashData(Encoding.UTF8.GetBytes(candidate));
            if (System.Numerics.BitOperations.LeadingZeroCount(BinaryPrimitives.ReadUInt32BigEndian(digest)) == bits)
                return candidate;
        }
        throw new InvalidOperationException("Could not answer Apple's proof-of-work challenge.");
    }
}
