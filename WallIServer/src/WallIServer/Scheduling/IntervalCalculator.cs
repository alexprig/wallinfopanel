using System.Globalization;

namespace WallIServer.Scheduling;

/// <summary>
/// Computes <c>X-Interval</c> (protocol.md §2.4): if <c>now + interval</c> falls inside the night window
/// (local time in the device's time zone), the interval becomes the seconds until <c>nightEnd</c>,
/// capped at 86400. The result is always clamped to 180 … 86400.
/// </summary>
public static class IntervalCalculator
{
    public const int MinInterval = 180;
    public const int MaxInterval = 86400;

    public static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>True when <paramref name="t"/> lies in [start, end), handling windows across midnight.</summary>
    public static bool IsInWindow(TimeOnly t, TimeOnly start, TimeOnly end)
    {
        if (start == end) return false; // empty window
        return start < end ? t >= start && t < end : t >= start || t < end;
    }

    public static int Compute(DateTimeOffset nowUtc, int intervalSeconds, TimeOnly? nightStart, TimeOnly? nightEnd, TimeZoneInfo tz)
    {
        int interval = Math.Clamp(intervalSeconds, MinInterval, MaxInterval);
        if (nightStart is not { } start || nightEnd is not { } end || start == end)
            return interval;

        var wakeUtc = nowUtc.AddSeconds(interval);
        var wakeLocal = TimeZoneInfo.ConvertTime(wakeUtc, tz);
        if (!IsInWindow(TimeOnly.FromDateTime(wakeLocal.DateTime), start, end))
            return interval;

        // End of the night window that contains the wake time: the first nightEnd at or after it.
        var endLocal = wakeLocal.Date + end.ToTimeSpan();
        if (endLocal <= wakeLocal.DateTime) endLocal = endLocal.AddDays(1);
        var endUtc = LocalToUtc(endLocal, tz);

        double seconds = Math.Ceiling((endUtc - nowUtc).TotalSeconds);
        return (int)Math.Clamp(seconds, MinInterval, MaxInterval);
    }

    /// <summary>Converts a local wall-clock time to UTC; a time skipped by a DST jump moves forward to the jump.</summary>
    private static DateTimeOffset LocalToUtc(DateTime local, TimeZoneInfo tz)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (int i = 0; i < 4 * 24 && tz.IsInvalidTime(local); i++)
            local = local.AddMinutes(15);
        // For ambiguous times (DST fall-back) use the earlier instant (the daylight offset).
        var offset = tz.IsAmbiguousTime(local) ? tz.GetAmbiguousTimeOffsets(local).Max() : tz.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
