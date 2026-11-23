using System.Security.Cryptography;
using System.Text;

namespace WallIServer.Security;

/// <summary>Device tokens and constant-time secret comparison.</summary>
public static class Tokens
{
    /// <summary>A new random device token: 32 bytes, base64url without padding (43 chars, fits the 64-char device limit).</summary>
    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>SHA-256 of the UTF-8 token, lowercase hex. This is all the server stores.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Constant-time check of a presented token against a stored hash.</summary>
    public static bool Verify(string? presented, string storedHashHex)
    {
        if (string.IsNullOrEmpty(presented) || storedHashHex.Length != 64) return false;
        Span<byte> stored = stackalloc byte[32];
        if (Convert.FromHexString(storedHashHex, stored, out _, out int written) != System.Buffers.OperationStatus.Done || written != 32)
            return false;
        Span<byte> actual = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(presented), actual);
        return CryptographicOperations.FixedTimeEquals(actual, stored);
    }

    /// <summary>Constant-time string comparison (hashes both sides first so lengths don't leak).</summary>
    public static bool SecretEquals(string? presented, string expected)
    {
        if (presented is null) return false;
        Span<byte> a = stackalloc byte[32];
        Span<byte> b = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(presented), a);
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), b);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
