using System.Globalization;
using System.Text.Json.Serialization;

namespace WallIServer.Imaging;

/// <summary>
/// A rectangle on the 8-pixel grid (protocol.md §1.1): x % 8 == 0, w % 8 == 0, w ≥ 8, h ≥ 1,
/// x + w ≤ 792, y + h ≤ 272.
/// </summary>
public readonly record struct Gray2Region(int X, int Y, int W, int H)
{
    /// <summary>Length of the region body: 2 · h · w/8.</summary>
    public int BodyLength => 2 * H * (W / 8);

    [JsonIgnore]
    public bool IsValid =>
        X >= 0 && Y >= 0 && X % 8 == 0 && W % 8 == 0 && W >= 8 && H >= 1 &&
        X + W <= Gray2.Width && Y + H <= Gray2.Height;

    [JsonIgnore]
    public bool IsFullImage => X == 0 && Y == 0 && W == Gray2.Width && H == Gray2.Height;

    /// <summary>Header value for <c>X-Region</c>: "x,y,w,h".</summary>
    public string ToHeaderValue() => string.Create(CultureInfo.InvariantCulture, $"{X},{Y},{W},{H}");

    public override string ToString() => ToHeaderValue();
}

/// <summary>Diff / extract / apply helpers working on packed gray2 bodies.</summary>
public static class Gray2Regions
{
    /// <summary>
    /// Bounding box of the pixels that differ between two bodies, widened to the 8-pixel grid.
    /// Returns null when the bodies are identical.
    /// </summary>
    /// <remarks>
    /// Works on packed bytes: every byte covers exactly one 8-pixel grid cell, so comparing bytes in
    /// both planes yields the grid-aligned box directly.
    /// </remarks>
    public static Gray2Region? DiffBoundingBox(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        CheckBody(a, nameof(a));
        CheckBody(b, nameof(b));

        int minRow = int.MaxValue, maxRow = -1, minCol = int.MaxValue, maxCol = -1;
        for (int y = 0; y < Gray2.Height; y++)
        {
            int rowOffset = y * Gray2.BytesPerRow;
            var a0 = a.Slice(rowOffset, Gray2.BytesPerRow);
            var b0 = b.Slice(rowOffset, Gray2.BytesPerRow);
            var a1 = a.Slice(Gray2.PlaneSize + rowOffset, Gray2.BytesPerRow);
            var b1 = b.Slice(Gray2.PlaneSize + rowOffset, Gray2.BytesPerRow);
            if (a0.SequenceEqual(b0) && a1.SequenceEqual(b1)) continue;

            if (y < minRow) minRow = y;
            maxRow = y;
            for (int c = 0; c < Gray2.BytesPerRow; c++)
            {
                if (a0[c] != b0[c] || a1[c] != b1[c])
                {
                    if (c < minCol) minCol = c;
                    break;
                }
            }
            for (int c = Gray2.BytesPerRow - 1; c >= 0; c--)
            {
                if (a0[c] != b0[c] || a1[c] != b1[c])
                {
                    if (c > maxCol) maxCol = c;
                    break;
                }
            }
        }

        if (maxRow < 0) return null;
        return new Gray2Region(minCol * 8, minRow, (maxCol - minCol + 1) * 8, maxRow - minRow + 1);
    }

    /// <summary>Region body: plane 0 region rows ‖ plane 1 region rows.</summary>
    public static byte[] Extract(ReadOnlySpan<byte> body, Gray2Region region)
    {
        CheckBody(body, nameof(body));
        CheckRegion(region);

        int rowBytes = region.W / 8;
        var result = new byte[region.BodyLength];
        int dst = 0;
        for (int p = 0; p < 2; p++)
        {
            for (int r = 0; r < region.H; r++)
            {
                int src = p * Gray2.PlaneSize + (region.Y + r) * Gray2.BytesPerRow + region.X / 8;
                body.Slice(src, rowBytes).CopyTo(result.AsSpan(dst, rowBytes));
                dst += rowBytes;
            }
        }
        return result;
    }

    /// <summary>Writes a region body into a full body (in place).</summary>
    public static void Apply(Span<byte> body, Gray2Region region, ReadOnlySpan<byte> regionBody)
    {
        CheckBody(body, nameof(body));
        CheckRegion(region);
        if (regionBody.Length != region.BodyLength)
            throw new ArgumentException($"Region body must be {region.BodyLength} bytes, got {regionBody.Length}.", nameof(regionBody));

        int rowBytes = region.W / 8;
        int src = 0;
        for (int p = 0; p < 2; p++)
        {
            for (int r = 0; r < region.H; r++)
            {
                int dst = p * Gray2.PlaneSize + (region.Y + r) * Gray2.BytesPerRow + region.X / 8;
                regionBody.Slice(src, rowBytes).CopyTo(body.Slice(dst, rowBytes));
                src += rowBytes;
            }
        }
    }

    private static void CheckBody(ReadOnlySpan<byte> body, string name)
    {
        if (body.Length != Gray2.BodySize)
            throw new ArgumentException($"Expected {Gray2.BodySize} bytes, got {body.Length}.", name);
    }

    private static void CheckRegion(Gray2Region region)
    {
        if (!region.IsValid) throw new ArgumentException($"Invalid region {region}.", nameof(region));
    }
}
