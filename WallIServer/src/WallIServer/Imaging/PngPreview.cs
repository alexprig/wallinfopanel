using SkiaSharp;

namespace WallIServer.Imaging;

/// <summary>Renders a gray2 body as an 8-bit grey PNG (levels → 0x00, 0x55, 0xAA, 0xFF).</summary>
public static class PngPreview
{
    public static byte[] Render(ReadOnlySpan<byte> body)
    {
        var levels = Gray2.Unpack(body);
        using var bitmap = new SKBitmap(new SKImageInfo(Gray2.Width, Gray2.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        var pixels = bitmap.GetPixelSpan();
        var greys = Gray2.LevelToGrey;
        int stride = bitmap.RowBytes;
        for (int y = 0; y < Gray2.Height; y++)
            for (int x = 0; x < Gray2.Width; x++)
                pixels[y * stride + x] = greys[levels[y * Gray2.Width + x]];

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
