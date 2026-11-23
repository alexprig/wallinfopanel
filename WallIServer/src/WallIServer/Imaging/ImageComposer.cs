using SkiaSharp;

namespace WallIServer.Imaging;

public enum FitMode
{
    /// <summary>Scale to fit inside the panel, keep the aspect ratio, pad with white.</summary>
    Contain,
    /// <summary>Scale to fill the panel, keep the aspect ratio, crop the centre.</summary>
    Cover,
    /// <summary>Scale to exactly 792 × 272, ignoring the aspect ratio.</summary>
    Stretch,
}

/// <summary>Thrown when an uploaded file can't be decoded as an image.</summary>
public sealed class ImageDecodeException(string message) : Exception(message);

/// <summary>Decodes, resizes and composes uploaded images into gray2 bodies (SkiaSharp).</summary>
public static class ImageComposer
{
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>Decodes <paramref name="encoded"/>, fits it to 792 × 272 and quantises it.</summary>
    public static byte[] Render(ReadOnlySpan<byte> encoded, FitMode fit, bool dither)
    {
        using var source = Decode(encoded);
        using var canvasBitmap = NewCanvasBitmap();
        using (var canvas = new SKCanvas(canvasBitmap))
        {
            canvas.Clear(SKColors.White);
            var dest = FitRect(source.Width, source.Height, fit);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawImage(source, dest, Sampling, paint);
        }

        var luminance = ToLuminance(canvasBitmap);
        var levels = new byte[Gray2.PixelCount];
        Quantizer.Quantize(luminance, levels, Gray2.Width, Gray2.Height, dither);
        return Gray2.Pack(levels);
    }

    /// <summary>
    /// Draws <paramref name="encoded"/> unscaled at (<paramref name="x"/>, <paramref name="y"/>) onto
    /// <paramref name="currentBody"/> (clipped to the panel) and re-quantises only the pasted area.
    /// Transparent pixels show the current image.
    /// </summary>
    public static byte[] Paste(ReadOnlySpan<byte> currentBody, ReadOnlySpan<byte> encoded, int x, int y, bool dither)
    {
        var levels = Gray2.Unpack(currentBody);
        using var source = Decode(encoded);

        int left = Math.Max(0, x);
        int top = Math.Max(0, y);
        int right = (int)Math.Min(Gray2.Width, (long)x + source.Width);
        int bottom = (int)Math.Min(Gray2.Height, (long)y + source.Height);
        if (right <= left || bottom <= top)
            return currentBody.ToArray(); // entirely off-panel: nothing changes

        using var canvasBitmap = NewCanvasBitmap();
        // Start from the current image so that transparency composites over it.
        var pixels = canvasBitmap.GetPixelSpan();
        var greys = Gray2.LevelToGrey;
        for (int i = 0; i < Gray2.PixelCount; i++)
        {
            byte g = greys[levels[i]];
            int o = i * 4;
            pixels[o] = g; pixels[o + 1] = g; pixels[o + 2] = g; pixels[o + 3] = 0xFF;
        }
        using (var canvas = new SKCanvas(canvasBitmap))
        {
            canvas.DrawImage(source, x, y, new SKSamplingOptions(SKFilterMode.Nearest), null);
        }

        var luminance = ToLuminance(canvasBitmap);
        Quantizer.Quantize(luminance, levels, Gray2.Width, Gray2.Height, dither,
            left, top, right - left, bottom - top);
        return Gray2.Pack(levels);
    }

    /// <summary>Fit rectangle for a source of the given size inside 792 × 272.</summary>
    internal static SKRect FitRect(int srcW, int srcH, FitMode fit)
    {
        float panelW = Gray2.Width, panelH = Gray2.Height;
        switch (fit)
        {
            case FitMode.Stretch:
                return new SKRect(0, 0, panelW, panelH);
            case FitMode.Cover:
            {
                float scale = Math.Max(panelW / srcW, panelH / srcH);
                float w = srcW * scale, h = srcH * scale;
                float l = (panelW - w) / 2f, t = (panelH - h) / 2f;
                return new SKRect(l, t, l + w, t + h);
            }
            default:
            {
                float scale = Math.Min(panelW / srcW, panelH / srcH);
                float w = srcW * scale, h = srcH * scale;
                float l = (panelW - w) / 2f, t = (panelH - h) / 2f;
                return new SKRect(l, t, l + w, t + h);
            }
        }
    }

    private static SKImage Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty) throw new ImageDecodeException("The uploaded file is empty.");
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data)
            ?? throw new ImageDecodeException("The file is not a supported image (PNG, JPEG, BMP, GIF, WebP).");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 50_000_000)
            throw new ImageDecodeException("The image dimensions are invalid or too large.");

        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new ImageDecodeException($"The image could not be decoded ({result}).");

        // Honour EXIF orientation (phone photos).
        var origin = codec.EncodedOrigin;
        if (origin == SKEncodedOrigin.TopLeft)
            return SKImage.FromBitmap(bitmap);
        using var oriented = ApplyOrigin(bitmap, origin);
        return SKImage.FromBitmap(oriented);
    }

    private static SKBitmap ApplyOrigin(SKBitmap src, SKEncodedOrigin origin)
    {
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = swap ? src.Height : src.Width;
        int h = swap ? src.Width : src.Height;
        // x' = ScaleX·x + SkewX·y + TransX ; y' = SkewY·x + ScaleY·y + TransY
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(m);
        canvas.DrawBitmap(src, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        return dst;
    }

    private static SKBitmap NewCanvasBitmap() =>
        new(new SKImageInfo(Gray2.Width, Gray2.Height, SKColorType.Rgba8888, SKAlphaType.Premul));

    /// <summary>Rec. 709 luma of an opaque RGBA8888 bitmap (alpha is composited over white).</summary>
    private static float[] ToLuminance(SKBitmap bitmap)
    {
        var pixels = bitmap.GetPixelSpan();
        var lum = new float[bitmap.Width * bitmap.Height];
        for (int i = 0; i < lum.Length; i++)
        {
            int o = i * 4;
            // Premultiplied: colour + (1 - alpha) · white.
            float white = 255f - pixels[o + 3];
            float r = pixels[o] + white, g = pixels[o + 1] + white, b = pixels[o + 2] + white;
            lum[i] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        }
        return lum;
    }
}
