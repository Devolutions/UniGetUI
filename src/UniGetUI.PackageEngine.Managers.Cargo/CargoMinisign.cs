using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace UniGetUI.PackageEngine.Managers.CargoManager;

internal sealed record CargoMinisignature(
    byte[] PublicKey, bool Prehashed, byte[] Signature, string TrustedComment, byte[] GlobalSignature);

internal static class CargoMinisign
{
    internal static CargoMinisignature Parse(string publicKey, string signatureFile)
    {
        string[] keyLines = publicKey.Replace("\r", "").Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (keyLines.Length == 2 && keyLines[0].StartsWith("untrusted comment:", StringComparison.Ordinal))
            keyLines = keyLines[1..];
        if (keyLines.Length != 1)
            throw new InvalidDataException("The publisher minisign public key is malformed.");
        byte[] key = Decode(keyLines[0], 42, "public key");
        if (key[0] != 'E' || key[1] != 'd')
            throw new InvalidDataException("The publisher minisign public key uses an unsupported algorithm.");
        string[] lines = signatureFile.Replace("\r", "").Split('\n');
        if (lines.Length < 4 || !lines[0].StartsWith("untrusted comment:", StringComparison.Ordinal)
            || !lines[2].StartsWith("trusted comment: ", StringComparison.Ordinal)
            || lines.Skip(4).Any(line => line.Length > 0))
            throw new InvalidDataException("The publisher signature file is malformed.");
        byte[] signature = Decode(lines[1], 74, "signature");
        bool prehashed = (signature[0], signature[1]) switch
        {
            ((byte)'E', (byte)'D') => true,
            ((byte)'E', (byte)'d') => false,
            _ => throw new InvalidDataException("The publisher signature uses an unsupported algorithm."),
        };
        if (!signature.AsSpan(2, 8).SequenceEqual(key.AsSpan(2, 8)))
            throw new InvalidDataException("The publisher signature was made with a different key.");
        return new(key[10..], prehashed, signature[10..], lines[2]["trusted comment: ".Length..],
            Decode(lines[3], 64, "global signature"));
    }

    internal static void Verify(CargoMinisignature signature, byte[] content)
    {
        byte[] message = signature.Prehashed ? Blake2b512(content) : content;
        if (!Ed25519.Verify(signature.PublicKey, message, signature.Signature)
            || !Ed25519.Verify(signature.PublicKey,
                [.. signature.Signature, .. Encoding.UTF8.GetBytes(signature.TrustedComment)],
                signature.GlobalSignature))
            throw new InvalidDataException("Cargo download does not match its publisher signature.");
    }

    private static byte[] Decode(string text, int length, string name)
    {
        byte[] bytes = new byte[length];
        if (!Convert.TryFromBase64String(text.Trim(), bytes, out int written) || written != length)
            throw new InvalidDataException($"The publisher minisign {name} is malformed.");
        return bytes;
    }

    private static readonly ulong[] Blake2bIv =
    [
        0x6a09e667f3bcc908, 0xbb67ae8584caa73b, 0x3c6ef372fe94f82b, 0xa54ff53a5f1d36f1,
        0x510e527fade682d1, 0x9b05688c2b3e6c1f, 0x1f83d9abfb41bd6b, 0x5be0cd19137e2179,
    ];

    private static readonly byte[][] Blake2bSigma =
    [
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
        [14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3],
        [11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4],
        [7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8],
        [9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13],
        [2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9],
        [12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11],
        [13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10],
        [6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5],
        [10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0],
    ];

    internal static byte[] Blake2b512(ReadOnlySpan<byte> data)
    {
        ulong[] h = (ulong[])Blake2bIv.Clone();
        h[0] ^= 0x01010040;
        Span<byte> block = stackalloc byte[128];
        UInt128 counter = 0;
        int offset = 0;
        while (data.Length - offset > 128)
        {
            counter += 128;
            Blake2bCompress(h, data.Slice(offset, 128), counter, false);
            offset += 128;
        }
        block.Clear();
        data[offset..].CopyTo(block);
        counter += (ulong)(data.Length - offset);
        Blake2bCompress(h, block, counter, true);
        byte[] result = new byte[64];
        for (int i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(i * 8), h[i]);
        return result;
    }

    private static void Blake2bCompress(ulong[] h, ReadOnlySpan<byte> block, UInt128 counter, bool last)
    {
        Span<ulong> m = stackalloc ulong[16];
        Span<ulong> v = stackalloc ulong[16];
        for (int i = 0; i < 16; i++)
            m[i] = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(i * 8, 8));
        h.CopyTo(v);
        Blake2bIv.CopyTo(v[8..]);
        v[12] ^= (ulong)counter;
        v[13] ^= (ulong)(counter >> 64);
        if (last)
            v[14] = ~v[14];
        for (int round = 0; round < 12; round++)
        {
            byte[] s = Blake2bSigma[round % 10];
            Mix(v, 0, 4, 8, 12, m[s[0]], m[s[1]]);
            Mix(v, 1, 5, 9, 13, m[s[2]], m[s[3]]);
            Mix(v, 2, 6, 10, 14, m[s[4]], m[s[5]]);
            Mix(v, 3, 7, 11, 15, m[s[6]], m[s[7]]);
            Mix(v, 0, 5, 10, 15, m[s[8]], m[s[9]]);
            Mix(v, 1, 6, 11, 12, m[s[10]], m[s[11]]);
            Mix(v, 2, 7, 8, 13, m[s[12]], m[s[13]]);
            Mix(v, 3, 4, 9, 14, m[s[14]], m[s[15]]);
        }
        for (int i = 0; i < 8; i++)
            h[i] ^= v[i] ^ v[i + 8];
    }

    private static void Mix(Span<ulong> v, int a, int b, int c, int d, ulong x, ulong y)
    {
        v[a] += v[b] + x;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] += v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] += v[b] + y;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] += v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }

    internal static class Ed25519
    {
        private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
        internal static readonly BigInteger L = BigInteger.Pow(2, 252)
            + BigInteger.Parse("27742317777372353535851937790883648493");
        private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
        private static readonly BigInteger SqrtMinusOne = BigInteger.ModPow(2, (P - 1) / 4, P);
        private static readonly Point Base = BasePoint();

        private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

        internal static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
        {
            if (publicKey.Length != 32 || signature.Length != 64
                || Decompress(publicKey) is not { } a || Decompress(signature[..32]) is not { } r
                || IsSmallOrder(a) || IsSmallOrder(r))
                return false;
            BigInteger s = new(signature[32..], isUnsigned: true);
            if (s >= L)
                return false;
            BigInteger k = Scalar([.. signature[..32], .. publicKey, .. message]);
            return AreEqual(Multiply(s, Base), Add(r, Multiply(k, a)));
        }

        internal static byte[] MultiplyBase(BigInteger scalar) => Compress(Multiply(scalar, Base));

        internal static BigInteger Scalar(byte[] data) =>
            new BigInteger(SHA512.HashData(data), isUnsigned: true) % L;

        private static BigInteger Mod(BigInteger value)
        {
            BigInteger result = value % P;
            return result.Sign < 0 ? result + P : result;
        }

        private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(value, P - 2, P);

        private static Point Add(Point p, Point q)
        {
            BigInteger a = Mod((p.Y - p.X) * (q.Y - q.X));
            BigInteger b = Mod((p.Y + p.X) * (q.Y + q.X));
            BigInteger c = Mod(2 * p.T * q.T * D);
            BigInteger d = Mod(2 * p.Z * q.Z);
            BigInteger e = b - a, f = d - c, g = d + c, h = b + a;
            return new(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
        }

        private static Point Multiply(BigInteger scalar, Point point)
        {
            Point result = new(0, 1, 1, 0);
            while (scalar.Sign > 0)
            {
                if (!scalar.IsEven)
                    result = Add(result, point);
                point = Add(point, point);
                scalar >>= 1;
            }
            return result;
        }

        private static bool IsSmallOrder(Point point)
        {
            Point multiple = Multiply(8, point);
            return multiple.X.IsZero && Mod(multiple.Y - multiple.Z).IsZero;
        }

        private static bool AreEqual(Point p, Point q) =>
            Mod(p.X * q.Z - q.X * p.Z).IsZero && Mod(p.Y * q.Z - q.Y * p.Z).IsZero;

        private static BigInteger? RecoverX(BigInteger y, bool sign)
        {
            if (y >= P)
                return null;
            BigInteger x2 = Mod((y * y - 1) * Inverse(Mod(D * y * y + 1)));
            if (x2.IsZero)
                return sign ? null : BigInteger.Zero;
            BigInteger x = BigInteger.ModPow(x2, (P + 3) / 8, P);
            if (!Mod(x * x - x2).IsZero)
                x = Mod(x * SqrtMinusOne);
            if (!Mod(x * x - x2).IsZero)
                return null;
            if (x.IsEven == sign)
                x = P - x;
            return x;
        }

        private static Point? Decompress(ReadOnlySpan<byte> encoded)
        {
            Span<byte> bytes = stackalloc byte[32];
            encoded.CopyTo(bytes);
            bool sign = (bytes[31] & 0x80) != 0;
            bytes[31] &= 0x7F;
            BigInteger y = new(bytes, isUnsigned: true);
            if (RecoverX(y, sign) is not { } x)
                return null;
            return new Point(x, y, 1, Mod(x * y));
        }

        private static byte[] Compress(Point point)
        {
            BigInteger inverse = Inverse(point.Z);
            BigInteger x = Mod(point.X * inverse), y = Mod(point.Y * inverse);
            byte[] result = new byte[32];
            y.TryWriteBytes(result, out _, isUnsigned: true);
            if (!x.IsEven)
                result[31] |= 0x80;
            return result;
        }

        private static Point BasePoint()
        {
            BigInteger y = Mod(4 * Inverse(5));
            BigInteger x = RecoverX(y, false)!.Value;
            return new(x, y, 1, Mod(x * y));
        }
    }
}
