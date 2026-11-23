using WallIServer.Imaging;

namespace WallIServer.Tests;

public class Gray2PackingTests
{
    private static byte[] Levels(byte fill = Gray2.Black)
    {
        var l = new byte[Gray2.PixelCount];
        Array.Fill(l, fill);
        return l;
    }

    [Fact]
    public void Constants_MatchProtocol()
    {
        Assert.Equal(99, Gray2.BytesPerRow);
        Assert.Equal(26_928, Gray2.PlaneSize);
        Assert.Equal(53_856, Gray2.BodySize);
        Assert.Equal(53_856, Gray2.Pack(Levels()).Length);
    }

    [Fact]
    public void WhitePixelAtOrigin_SetsBit7OfByte0InBothPlanes()
    {
        var l = Levels();
        l[0] = Gray2.White;
        var body = Gray2.Pack(l);
        Assert.Equal(0x80, body[0]);
        Assert.Equal(0x80, body[Gray2.PlaneSize]);
        Assert.Equal(2, body.Count(b => b != 0));
    }

    [Fact]
    public void LightGrey_SetsOnlyPlane1()
    {
        var l = Levels();
        l[0] = Gray2.LightGrey;
        var body = Gray2.Pack(l);
        Assert.Equal(0x00, body[0]);
        Assert.Equal(0x80, body[Gray2.PlaneSize]);
    }

    [Fact]
    public void DarkGrey_SetsOnlyPlane0()
    {
        var l = Levels();
        l[0] = Gray2.DarkGrey;
        var body = Gray2.Pack(l);
        Assert.Equal(0x80, body[0]);
        Assert.Equal(0x00, body[Gray2.PlaneSize]);
    }

    [Fact]
    public void Black_IsZeroInBothPlanes_WhiteIsAllOnes()
    {
        Assert.All(Gray2.Pack(Levels(Gray2.Black)), b => Assert.Equal(0x00, b));
        Assert.All(Gray2.Pack(Levels(Gray2.White)), b => Assert.Equal(0xFF, b));
        Assert.Equal(Gray2.CreateWhiteBody(), Gray2.Pack(Levels(Gray2.White)));
    }

    [Theory]
    [InlineData(9, 1, 1 * 99 + 1, 0x40)]     // x=9 → byte 1, second pixel → bit 6
    [InlineData(7, 0, 0, 0x01)]              // last pixel of the first byte → bit 0
    [InlineData(791, 271, 26_927, 0x01)]     // bottom-right pixel → last byte of the plane, bit 0
    [InlineData(400, 136, 136 * 99 + 50, 0x80)]
    public void PixelPosition_MapsToRowMajorMsbFirst(int x, int y, int byteOffset, int mask)
    {
        var l = Levels();
        l[y * Gray2.Width + x] = Gray2.White;
        var body = Gray2.Pack(l);
        Assert.Equal(mask, body[byteOffset]);
        Assert.Equal(mask, body[Gray2.PlaneSize + byteOffset]);
        Assert.Equal(2, body.Count(b => b != 0));
    }

    [Fact]
    public void PackUnpack_RoundTrips()
    {
        var rng = new Random(42);
        var l = new byte[Gray2.PixelCount];
        for (int i = 0; i < l.Length; i++) l[i] = (byte)rng.Next(4);
        var body = Gray2.Pack(l);
        Assert.Equal(l, Gray2.Unpack(body));
        Assert.Equal(body, Gray2.Pack(Gray2.Unpack(body)));
    }

    [Fact]
    public void Pack_RejectsBadInput()
    {
        Assert.Throws<ArgumentException>(() => Gray2.Pack(new byte[10]));
        var l = Levels();
        l[5] = 4;
        Assert.Throws<ArgumentException>(() => Gray2.Pack(l));
    }

    [Fact]
    public void ETag_IsQuotedFirst16HexOfSha256()
    {
        var body = Gray2.CreateWhiteBody();
        var full = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body));
        Assert.Equal(full[..16], ETag.ComputeHex(body));
        Assert.Equal("\"" + full[..16] + "\"", ETag.Quote(ETag.ComputeHex(body)));
    }
}
