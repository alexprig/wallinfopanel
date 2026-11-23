using WallIServer.Scheduling;

namespace WallIServer.Tests;

public class IntervalCalculatorTests
{
    private static readonly TimeOnly T2200 = new(22, 0), T0600 = new(6, 0);
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    [Fact]
    public void NoNightWindow_ReturnsInterval()
    {
        Assert.Equal(900, IntervalCalculator.Compute(Utc(2026, 1, 1, 23, 0), 900, null, null, TimeZoneInfo.Utc));
    }

    [Fact]
    public void IntervalIsClamped()
    {
        Assert.Equal(180, IntervalCalculator.Compute(Utc(2026, 1, 1, 12, 0), 10, null, null, TimeZoneInfo.Utc));
        Assert.Equal(86400, IntervalCalculator.Compute(Utc(2026, 1, 1, 12, 0), 999_999, null, null, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData(21, 30, 900)]            // wake 21:45 → before the night
    [InlineData(21, 50, 8 * 3600 + 600)] // wake 22:05 → inside → until 06:00 = 8 h 10 min
    [InlineData(23, 30, 6 * 3600 + 1800)]// after midnight-crossing start
    [InlineData(1, 0, 5 * 3600)]         // past midnight, inside
    [InlineData(5, 50, 900)]             // wake 06:05 → already over
    [InlineData(5, 40, 1200)]            // wake 05:55 → inside → until 06:00 = 20 min
    [InlineData(12, 0, 900)]
    public void NightAcrossMidnight_Utc(int hour, int minute, int expected)
    {
        Assert.Equal(expected, IntervalCalculator.Compute(Utc(2026, 1, 15, hour, minute), 900, T2200, T0600, TimeZoneInfo.Utc));
    }

    [Fact]
    public void NightWithinOneDay()
    {
        var start = new TimeOnly(12, 0);
        var end = new TimeOnly(14, 0);
        Assert.Equal(2 * 3600 + 600, IntervalCalculator.Compute(Utc(2026, 1, 15, 11, 50), 900, start, end, TimeZoneInfo.Utc));
        Assert.Equal(900, IntervalCalculator.Compute(Utc(2026, 1, 15, 14, 0), 900, start, end, TimeZoneInfo.Utc));
    }

    [Fact]
    public void TimeZone_Winter_UsesLocalTime()
    {
        // 20:50 UTC = 21:50 CET → wake 22:05 local → night ends 06:00 CET = 05:00 UTC.
        Assert.Equal(8 * 3600 + 600, IntervalCalculator.Compute(Utc(2026, 1, 15, 20, 50), 900, T2200, T0600, Berlin));
        // Same UTC instant without the zone is outside the night.
        Assert.Equal(900, IntervalCalculator.Compute(Utc(2026, 1, 15, 20, 50), 900, T2200, T0600, TimeZoneInfo.Utc));
    }

    [Fact]
    public void TimeZone_Summer_UsesLocalTime()
    {
        // 19:50 UTC = 21:50 CEST → night ends 06:00 CEST = 04:00 UTC.
        Assert.Equal(8 * 3600 + 600, IntervalCalculator.Compute(Utc(2026, 7, 15, 19, 50), 900, T2200, T0600, Berlin));
    }

    [Fact]
    public void TimeZone_DstSpringForwardNight_IsOneHourShorter()
    {
        // 2026-03-29 02:00 CET → 03:00 CEST. 21:50 CET → 06:00 CEST is 7 h 10 min.
        Assert.Equal(7 * 3600 + 600, IntervalCalculator.Compute(Utc(2026, 3, 28, 20, 50), 900, T2200, T0600, Berlin));
    }

    [Fact]
    public void LongWait_IsCappedAt86400()
    {
        // Wake exactly 24 h later at 00:00, inside 23:59–00:30 → would be 24 h 30 min.
        Assert.Equal(86400, IntervalCalculator.Compute(Utc(2026, 1, 15, 0, 0), 86400, new TimeOnly(23, 59), new TimeOnly(0, 30), TimeZoneInfo.Utc));
    }

    [Fact]
    public void EqualStartAndEnd_IsNoWindow()
    {
        Assert.Equal(900, IntervalCalculator.Compute(Utc(2026, 1, 15, 22, 0), 900, T2200, T2200, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData("22:00", true)]
    [InlineData("06:30", true)]
    [InlineData("23:59", true)]
    [InlineData("24:00", false)]
    [InlineData("6:30", false)]
    [InlineData("06:60", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ParseTime(string? text, bool ok) => Assert.Equal(ok, IntervalCalculator.TryParseTime(text, out _));
}
