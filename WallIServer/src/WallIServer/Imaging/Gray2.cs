namespace WallIServer.Imaging;

/// <summary>
/// The <c>gray2</c> image format (protocol.md §1): 792 × 272 px, 4 grey levels,
/// stored as two 1-bit planes (plane 0 = bit 0, plane 1 = bit 1), 99 bytes per row,
/// MSB = leftmost pixel.
/// </summary>
public static class Gray2
{
    public const int Width = 792;
    public const int Height = 272;
    public const int BytesPerRow = Width / 8;           // 99
    public const int PlaneSize = BytesPerRow * Height;  // 26,928
    public const int BodySize = PlaneSize * 2;          // 53,856
    public const int PixelCount = Width * Height;

    public const byte Black = 0;
    public const byte DarkGrey = 1;
    public const byte LightGrey = 2;
    public const byte White = 3;

    /// <summary>8-bit grey value used to render each level (preview and compositing).</summary>
    public static ReadOnlySpan<byte> LevelToGrey => [0x00, 0x55, 0xAA, 0xFF];

    /// <summary>Packs one level (0–3) per pixel, row-major, into the two-plane body.</summary>
    public static byte[] Pack(ReadOnlySpan<byte> levels)
    {
        if (levels.Length != PixelCount)
            throw new ArgumentException($"Expected {PixelCount} levels, got {levels.Length}.", nameof(levels));

        var body = new byte[BodySize];
        for (int y = 0; y < Height; y++)
        {
            int rowPixel = y * Width;
            int rowByte = y * BytesPerRow;
            for (int bx = 0; bx < BytesPerRow; bx++)
            {
                int p0 = 0, p1 = 0;
                int px = rowPixel + bx * 8;
                for (int bit = 0; bit < 8; bit++)
                {
                    int level = levels[px + bit];
                    if (level > 3) throw new ArgumentException($"Level {level} out of range at pixel {px + bit}.", nameof(levels));
                    int mask = 0x80 >> bit;
                    if ((level & 1) != 0) p0 |= mask;
                    if ((level & 2) != 0) p1 |= mask;
                }
                body[rowByte + bx] = (byte)p0;
                body[PlaneSize + rowByte + bx] = (byte)p1;
            }
        }
        return body;
    }

    /// <summary>Unpacks a two-plane body into one level (0–3) per pixel.</summary>
    public static byte[] Unpack(ReadOnlySpan<byte> body)
    {
        if (body.Length != BodySize)
            throw new ArgumentException($"Expected {BodySize} bytes, got {body.Length}.", nameof(body));

        var levels = new byte[PixelCount];
        for (int y = 0; y < Height; y++)
        {
            int rowPixel = y * Width;
            int rowByte = y * BytesPerRow;
            for (int bx = 0; bx < BytesPerRow; bx++)
            {
                int p0 = body[rowByte + bx];
                int p1 = body[PlaneSize + rowByte + bx];
                int px = rowPixel + bx * 8;
                for (int bit = 0; bit < 8; bit++)
                {
                    int mask = 0x80 >> bit;
                    levels[px + bit] = (byte)(((p0 & mask) != 0 ? 1 : 0) | ((p1 & mask) != 0 ? 2 : 0));
                }
            }
        }
        return levels;
    }

    /// <summary>A body filled with white (all bits set).</summary>
    public static byte[] CreateWhiteBody()
    {
        var body = new byte[BodySize];
        Array.Fill(body, (byte)0xFF);
        return body;
    }
}
