using System.Security.Cryptography;

namespace WallIServer.Imaging;

/// <summary>ETag rules from protocol.md §2.4: <c>"</c> + first 16 hex chars of SHA-256(body) + <c>"</c>.</summary>
public static class ETag
{
    /// <summary>The 16 lowercase hex chars (no quotes); also used as the version file name.</summary>
    public static string ComputeHex(ReadOnlySpan<byte> body)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(body, hash);
        return Convert.ToHexStringLower(hash[..8]);
    }

    public static string Quote(string hex) => "\"" + hex + "\"";

    /// <summary>
    /// Parses an <c>If-None-Match</c> header into bare hex values. Accepts a quoted value as sent by the
    /// device, and tolerates a weak prefix, a missing pair of quotes or a comma-separated list.
    /// </summary>
    public static IReadOnlyList<string> ParseIfNoneMatch(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return [];
        var result = new List<string>();
        foreach (var raw in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var v = raw;
            if (v.StartsWith("W/", StringComparison.Ordinal)) v = v[2..];
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1];
            if (v.Length == 16 && v.All(Uri.IsHexDigit)) result.Add(v.ToLowerInvariant());
        }
        return result;
    }
}
