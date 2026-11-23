using SkiaSharp;
using WallIServer.Imaging;

namespace WallIServer.Tests;

public class QuantizerTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(42f, 0)]
    [InlineData(43f, 1)]
    [InlineData(85f, 1)]
    [InlineData(127f, 1)]
    [InlineData(128f, 2)]
    [InlineData(212f, 2)]
    [InlineData(213f, 3)]
    [InlineData(255f, 3)]
    [InlineData(-30f, 0)]
    [InlineData(400f, 3)]
    public void NearestLevel(float lum, int expected) => Assert.Equal(expected, Quantizer.NearestLevel(lum));

    [Fact]
    public void Dithering_UsesOnlyLevels0To3()
    {
        int w = 200, h = 50;
        var lum = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                lum[y * w + x] = x * 255f / (w - 1);
        var levels = new byte[w * h];
        Quantizer.Quantize(lum, levels, w, h, dither: true);
        Assert.All(levels, l => Assert.InRange(l, (byte)0, (byte)3));
        Assert.Equal(4, levels.Distinct().Count());
    }

    [Fact]
    public void FlatHalfGrey_DithersToAMix_WithMatchingMean()
    {
        int w = 100, h = 100;
        var lum = Enumerable.Repeat(127.5f, w * h).ToArray();
        var levels = new byte[w * h];
        Quantizer.Quantize(lum, levels, w, h, dither: true);
        Assert.Contains((byte)1, levels);
        Assert.Contains((byte)2, levels);
        Assert.InRange(levels.Average(l => l * 85.0), 124.0, 131.0);

        var nearest = new byte[w * h];
        Quantizer.Quantize(lum, nearest, w, h, dither: false);
        Assert.Single(nearest.Distinct());
    }

    [Fact]
    public void Quantize_Rect_LeavesOutsideUntouched()
    {
        int w = 32, h = 32;
        var lum = Enumerable.Repeat(0f, w * h).ToArray();
        var levels = Enumerable.Repeat((byte)3, w * h).ToArray();
        Quantizer.Quantize(lum, levels, w, h, dither: true, 8, 8, 8, 8);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Assert.Equal(x is >= 8 and < 16 && y is >= 8 and < 16 ? 0 : 3, levels[y * w + x]);
    }

    [Fact]
    public void Render_FlatGreyPng_DithersToMix_AndNoDitherIsUniform()
    {
        var png = TestImages.SolidPng(792, 272, new SKColor(128, 128, 128));
        var dithered = Gray2.Unpack(ImageComposer.Render(png, FitMode.Stretch, dither: true));
        Assert.True(dithered.Distinct().Count() >= 2);
        Assert.All(dithered, l => Assert.InRange(l, (byte)0, (byte)3));

        var flat = Gray2.Unpack(ImageComposer.Render(png, FitMode.Stretch, dither: false));
        Assert.All(flat, l => Assert.Equal(Gray2.LightGrey, l));
    }

    [Fact]
    public void Render_Contain_PadsWithWhite()
    {
        // A square black image in contain mode: 272×272 in the middle, white left and right.
        var png = TestImages.SolidPng(100, 100, SKColors.Black);
        var levels = Gray2.Unpack(ImageComposer.Render(png, FitMode.Contain, dither: false));
        int row = 136 * Gray2.Width;
        Assert.Equal(Gray2.White, levels[row + 10]);
        Assert.Equal(Gray2.White, levels[row + 780]);
        Assert.Equal(Gray2.Black, levels[row + 396]);
        Assert.Equal(Gray2.Black, levels[396]); // full height
    }

    [Fact]
    public void Render_Cover_FillsWholePanel()
    {
        var png = TestImages.SolidPng(100, 100, SKColors.Black);
        var levels = Gray2.Unpack(ImageComposer.Render(png, FitMode.Cover, dither: false));
        Assert.All(levels, l => Assert.Equal(Gray2.Black, l));
    }

    [Fact]
    public void FitRect_Modes()
    {
        Assert.Equal(new SKRect(260, 0, 532, 272), ImageComposer.FitRect(100, 100, FitMode.Contain));
        Assert.Equal(new SKRect(0, -260, 792, 532), ImageComposer.FitRect(100, 100, FitMode.Cover));
        Assert.Equal(new SKRect(0, 0, 792, 272), ImageComposer.FitRect(100, 100, FitMode.Stretch));
    }

    [Fact]
    public void Paste_ChangesOnlyThePastedArea()
    {
        var white = Gray2.CreateWhiteBody();
        var png = TestImages.SolidPng(20, 10, SKColors.Black);
        var result = ImageComposer.Paste(white, png, 100, 50, dither: true);
        var region = Gray2Regions.DiffBoundingBox(white, result);
        Assert.Equal(new Gray2Region(96, 50, 24, 10), region);
        var levels = Gray2.Unpack(result);
        Assert.Equal(Gray2.Black, levels[50 * Gray2.Width + 100]);
        Assert.Equal(Gray2.White, levels[50 * Gray2.Width + 99]);
        Assert.Equal(200, levels.Count(l => l == Gray2.Black));
    }

    [Fact]
    public void Paste_IsClippedAtTheEdges()
    {
        var white = Gray2.CreateWhiteBody();
        var png = TestImages.SolidPng(40, 40, SKColors.Black);
        var result = ImageComposer.Paste(white, png, 780, -30, dither: false);
        Assert.Equal(12 * 10, Gray2.Unpack(result).Count(l => l == Gray2.Black));
        Assert.Equal(white, ImageComposer.Paste(white, png, 900, 0, dither: false));
    }

    [Fact]
    public void Render_RejectsGarbage()
    {
        Assert.Throws<ImageDecodeException>(() => ImageComposer.Render("not an image"u8.ToArray(), FitMode.Contain, true));
    }
}
