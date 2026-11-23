namespace WallIServer.Imaging;

/// <summary>
/// Converts luminance (0–255, one float per pixel, row-major) to the 4 gray2 levels,
/// either by nearest level or with Floyd–Steinberg error diffusion.
/// </summary>
public static class Quantizer
{
    private const float Step = 255f / 3f; // 85

    /// <summary>Nearest gray2 level for a luminance value.</summary>
    public static byte NearestLevel(float luminance)
    {
        int level = (int)MathF.Round(luminance / Step, MidpointRounding.AwayFromZero);
        return (byte)Math.Clamp(level, 0, 3);
    }

    /// <summary>
    /// Quantises the rectangle <paramref name="rx"/>,<paramref name="ry"/>,<paramref name="rw"/>,<paramref name="rh"/>
    /// of <paramref name="luminance"/> (an image <paramref name="width"/> pixels wide) into <paramref name="levels"/>.
    /// Pixels outside the rectangle are left untouched, and diffused error never leaves the rectangle.
    /// </summary>
    public static void Quantize(
        ReadOnlySpan<float> luminance, Span<byte> levels, int width, int height,
        bool dither, int rx = 0, int ry = 0, int rw = -1, int rh = -1)
    {
        if (rw < 0) rw = width;
        if (rh < 0) rh = height;
        if (luminance.Length != width * height || levels.Length != width * height)
            throw new ArgumentException("Buffer sizes do not match width × height.");
        if (rx < 0 || ry < 0 || rx + rw > width || ry + rh > height)
            throw new ArgumentOutOfRangeException(nameof(rx), "Rectangle outside the image.");
        if (rw == 0 || rh == 0) return;

        if (!dither)
        {
            for (int y = ry; y < ry + rh; y++)
                for (int x = rx; x < rx + rw; x++)
                    levels[y * width + x] = NearestLevel(luminance[y * width + x]);
            return;
        }

        // Two rolling error rows (current, next) with one guard cell on each side.
        var cur = new float[rw + 2];
        var next = new float[rw + 2];
        for (int y = 0; y < rh; y++)
        {
            int row = (ry + y) * width + rx;
            for (int x = 0; x < rw; x++)
            {
                float value = luminance[row + x] + cur[x + 1];
                byte level = NearestLevel(value);
                levels[row + x] = level;
                float err = value - level * Step;

                cur[x + 2] += err * 7f / 16f;   // right
                next[x] += err * 3f / 16f;      // below-left
                next[x + 1] += err * 5f / 16f;  // below
                next[x + 2] += err * 1f / 16f;  // below-right
            }
            (cur, next) = (next, cur);
            Array.Clear(next);
        }
    }
}
