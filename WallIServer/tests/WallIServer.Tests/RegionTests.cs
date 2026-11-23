using WallIServer.Imaging;

namespace WallIServer.Tests;

public class RegionTests
{
    private static byte[] WhiteLevels()
    {
        var l = new byte[Gray2.PixelCount];
        Array.Fill(l, Gray2.White);
        return l;
    }

    [Fact]
    public void NoDiff_ReturnsNull()
    {
        var a = Gray2.CreateWhiteBody();
        Assert.Null(Gray2Regions.DiffBoundingBox(a, (byte[])a.Clone()));
    }

    [Fact]
    public void DiffInLastColumn_IsWidenedToLastGridCell()
    {
        var a = Gray2.CreateWhiteBody();
        var b = TestImages.WithRect(WhiteLevels(), 791, 100, 1, 1, Gray2.Black);
        var r = Gray2Regions.DiffBoundingBox(a, b);
        Assert.Equal(new Gray2Region(784, 100, 8, 1), r);
        Assert.True(r!.Value.IsValid);
        Assert.Equal(2, r.Value.BodyLength);
    }

    [Fact]
    public void DiffInFirstPixel_IsFirstGridCell()
    {
        var a = Gray2.CreateWhiteBody();
        var b = TestImages.WithRect(WhiteLevels(), 0, 0, 1, 1, Gray2.LightGrey);
        Assert.Equal(new Gray2Region(0, 0, 8, 1), Gray2Regions.DiffBoundingBox(a, b));
    }

    [Fact]
    public void FullImageDiff_IsWholePanel()
    {
        var r = Gray2Regions.DiffBoundingBox(TestImages.Flat(Gray2.White), TestImages.Flat(Gray2.Black));
        Assert.Equal(new Gray2Region(0, 0, 792, 272), r);
        Assert.True(r!.Value.IsFullImage);
        Assert.Equal(Gray2.BodySize, r.Value.BodyLength);
    }

    [Fact]
    public void SeveralChanges_GiveGridAlignedBoundingBox()
    {
        var l = WhiteLevels();
        l[5 * Gray2.Width + 10] = Gray2.Black;
        l[7 * Gray2.Width + 20] = Gray2.DarkGrey;
        var r = Gray2Regions.DiffBoundingBox(Gray2.CreateWhiteBody(), Gray2.Pack(l));
        Assert.Equal(new Gray2Region(8, 5, 16, 3), r);
    }

    [Fact]
    public void ChangeOnlyInPlane1_IsDetected()
    {
        // White (11) → dark grey (01) changes only plane 1.
        var b = TestImages.WithRect(WhiteLevels(), 300, 200, 1, 1, Gray2.DarkGrey);
        Assert.Equal(new Gray2Region(296, 200, 8, 1), Gray2Regions.DiffBoundingBox(Gray2.CreateWhiteBody(), b));
    }

    [Fact]
    public void ExtractApply_RoundTrip()
    {
        var rng = new Random(7);
        var la = new byte[Gray2.PixelCount];
        for (int i = 0; i < la.Length; i++) la[i] = (byte)rng.Next(4);
        var a = Gray2.Pack(la);
        var lb = (byte[])la.Clone();
        for (int y = 40; y < 90; y++)
            for (int x = 123; x < 345; x++)
                lb[y * Gray2.Width + x] = (byte)rng.Next(4);
        var b = Gray2.Pack(lb);

        var region = Gray2Regions.DiffBoundingBox(a, b)!.Value;
        Assert.True(region.IsValid);
        Assert.InRange(region.X, 120 - 8, 123);
        var body = Gray2Regions.Extract(b, region);
        Assert.Equal(2 * region.H * region.W / 8, body.Length);

        var patched = (byte[])a.Clone();
        Gray2Regions.Apply(patched, region, body);
        Assert.Equal(b, patched);
    }

    [Fact]
    public void Extract_UsesProtocolOffsets()
    {
        // Region byte (p, r, c) ↔ full offset p·26928 + (y+r)·99 + x/8 + c.
        var full = new byte[Gray2.BodySize];
        for (int i = 0; i < full.Length; i++) full[i] = (byte)(i * 31 + 7);
        var region = new Gray2Region(16, 3, 24, 2);
        var body = Gray2Regions.Extract(full, region);
        int rowBytes = region.W / 8, i2 = 0;
        for (int p = 0; p < 2; p++)
            for (int r = 0; r < region.H; r++)
                for (int c = 0; c < rowBytes; c++)
                    Assert.Equal(full[p * 26928 + (region.Y + r) * 99 + region.X / 8 + c], body[i2++]);
    }

    [Theory]
    [InlineData(0, 0, 792, 272, true)]
    [InlineData(784, 271, 8, 1, true)]
    [InlineData(4, 0, 8, 1, false)]
    [InlineData(0, 0, 12, 1, false)]
    [InlineData(0, 0, 0, 1, false)]
    [InlineData(0, 0, 8, 0, false)]
    [InlineData(788, 0, 8, 1, false)]
    [InlineData(784, 0, 16, 1, false)]
    [InlineData(0, 270, 8, 3, false)]
    public void RegionValidity(int x, int y, int w, int h, bool valid) =>
        Assert.Equal(valid, new Gray2Region(x, y, w, h).IsValid);

    [Fact]
    public void Apply_RejectsWrongLength()
    {
        var body = Gray2.CreateWhiteBody();
        Assert.Throws<ArgumentException>(() => Gray2Regions.Apply(body, new Gray2Region(0, 0, 8, 1), new byte[3]));
    }
}
