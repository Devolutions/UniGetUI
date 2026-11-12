using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace UniGetUI.Extensions.Xcode.AppleAuth;

/// <summary>
/// The client side of the SRP-6a exchange that Apple's sign-in service uses (RFC 5054 2048-bit group, SHA-256, with
/// the password stretched by PBKDF2 and an empty identity in the private key). One instance serves one sign-in attempt.
/// </summary>
/// <remarks>
/// <see cref="BigInteger.ModPow"/> is not constant-time. The private ephemeral is random and used once, which limits
/// what timing can reveal, but this is not a hardened implementation against local side channels.
/// </remarks>
public sealed class AppleSrpClient : IDisposable
{
    internal const int MaximumIterations = 1_000_000;

    private static readonly BigInteger N = Unsigned(Convert.FromHexString(
        "AC6BDB41324A9A9BF166DE5E1389582FAF72B6651987EE07FC3192943DB56050A37329CBB4A099ED8193E0757767A13DD52312AB4B03310D"
        + "CD7F48A9DA04FD50E8083969EDB767B0CF6095179A163AB3661A05FBD5FAAAE82918A9962F0B93B855F97993EC975EEAA80D740ADBF4FF7"
        + "47359D041D5C33EA71D281E446B14773BCA97B43A23FB801676BD207A436C6481F1D2B9078717461A5B9D32E688F87748544523B524B0D57"
        + "D5EA77A2775D2ECFA032CFBDBF52FB3786160279004E57AE6AF874E7303CE53299CCC041C7BC308D82A5698F3A8D0C38271AE35F8E9DBFBB"
        + "694B5C803D89F7AE435DE236D525F54759B65E372FCD68EF20FA7111F9E4AFF73"));
    private static readonly BigInteger G = 2;
    private const int Length = 256;

    private readonly byte[] _privateEphemeral;
    private bool _used;

    public AppleSrpClient() : this(null) { }

    internal AppleSrpClient(byte[]? privateEphemeral)
    {
        if (privateEphemeral is null)
        {
            BigInteger a;
            byte[] random = new byte[Length];
            do
            {
                RandomNumberGenerator.Fill(random);
                a = Unsigned(random) % N;
            } while (a <= 1);
            CryptographicOperations.ZeroMemory(random);
            _privateEphemeral = Bytes(a);
        }
        else
            _privateEphemeral = (byte[])privateEphemeral.Clone();

        PublicEphemeral = Bytes(BigInteger.ModPow(G, Unsigned(_privateEphemeral), N));
    }

    /// <summary>A, sent with the account name to start the exchange.</summary>
    public byte[] PublicEphemeral { get; }

    /// <summary>Whether a server-chosen password protocol is one this client can answer.</summary>
    public static bool IsSupportedProtocol(string? protocol) => protocol is "s2k" or "s2k_fo";

    /// <summary>Computes the proofs M1 and M2. The password bytes are not retained.</summary>
    public (byte[] M1, byte[] M2) ComputeProofs(
        string protocol, string accountName, ReadOnlySpan<byte> passwordUtf8, byte[] salt, int iterations, byte[] serverPublic)
    {
        if (_used)
            throw new InvalidOperationException("An SRP ephemeral may only be used once.");
        _used = true;
        if (!IsSupportedProtocol(protocol))
            throw new ArgumentException("Apple requested a password protocol that is not supported.", nameof(protocol));
        if (salt.Length is < 1 or > 1024)
            throw new ArgumentException("Apple sent an invalid sign-in salt.", nameof(salt));
        if (iterations is < 1 or > MaximumIterations)
            throw new ArgumentException("Apple sent an invalid sign-in iteration count.", nameof(iterations));
        if (serverPublic.Length is < 1 or > Length)
            throw new ArgumentException("Apple sent an invalid sign-in challenge.", nameof(serverPublic));
        BigInteger b = Unsigned(serverPublic);
        if (b.IsZero || b >= N)
            throw new ArgumentException("Apple sent an invalid sign-in challenge.", nameof(serverPublic));

        byte[] passwordHash = SHA256.HashData(passwordUtf8);
        byte[] input = protocol == "s2k" ? passwordHash : Encoding.ASCII.GetBytes(Convert.ToHexStringLower(passwordHash));
        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(input, salt, iterations, HashAlgorithmName.SHA256, 32);
        CryptographicOperations.ZeroMemory(passwordHash);
        CryptographicOperations.ZeroMemory(input);

        byte[] inner = Hash([(byte)':'], derived);
        CryptographicOperations.ZeroMemory(derived);
        byte[] xBytes = Hash(salt, inner);
        CryptographicOperations.ZeroMemory(inner);
        BigInteger x = Unsigned(xBytes);
        CryptographicOperations.ZeroMemory(xBytes);

        byte[] a = PublicEphemeral;
        BigInteger k = Unsigned(Hash(Bytes(N), Pad(G)));
        BigInteger u = Unsigned(Hash(Pad(Unsigned(a)), Pad(b)));
        if (u.IsZero)
            throw new ArgumentException("Apple sent an invalid sign-in challenge.", nameof(serverPublic));

        BigInteger baseValue = (b - k * BigInteger.ModPow(G, x, N)) % N;
        if (baseValue.Sign < 0)
            baseValue += N;
        BigInteger secret = BigInteger.ModPow(baseValue, Unsigned(_privateEphemeral) + u * x, N);
        byte[] secretBytes = Bytes(secret);
        byte[] key = SHA256.HashData(secretBytes);
        CryptographicOperations.ZeroMemory(secretBytes);
        try
        {
            byte[] groupHash = SHA256.HashData(Bytes(N));
            byte[] generatorHash = SHA256.HashData(Pad(G));
            for (int i = 0; i < groupHash.Length; i++)
                groupHash[i] ^= generatorHash[i];
            byte[] m1 = Hash(groupHash, SHA256.HashData(Encoding.UTF8.GetBytes(accountName)), salt, a, Bytes(b), key);
            byte[] m2 = Hash(a, m1, key);
            return (m1, m2);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Whether the server's M2 proves it knew the verifier.</summary>
    public static bool VerifyServerProof(byte[] expected, byte[] received)
        => expected.Length == received.Length && CryptographicOperations.FixedTimeEquals(expected, received);

    public void Dispose() => CryptographicOperations.ZeroMemory(_privateEphemeral);

    private static byte[] Hash(params byte[][] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (byte[] part in parts)
            hash.AppendData(part);
        return hash.GetHashAndReset();
    }

    private static BigInteger Unsigned(ReadOnlySpan<byte> value) => new(value, isUnsigned: true, isBigEndian: true);

    private static byte[] Bytes(BigInteger value) => value.ToByteArray(isUnsigned: true, isBigEndian: true);

    private static byte[] Pad(BigInteger value)
    {
        byte[] bytes = Bytes(value);
        if (bytes.Length >= Length)
            return bytes;
        byte[] padded = new byte[Length];
        bytes.CopyTo(padded, Length - bytes.Length);
        return padded;
    }
}
