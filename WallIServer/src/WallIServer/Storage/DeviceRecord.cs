namespace WallIServer.Storage;

/// <summary>Persisted state of one device (<c>devices/{id}/device.json</c>).</summary>
public sealed class DeviceRecord
{
    public const int DefaultIntervalSeconds = 900;
    public const int DefaultLowBattMv = 3300;
    public const string DefaultTimeZone = "UTC";

    public required string Id { get; set; }
    public required string Name { get; set; }
    public int IntervalSeconds { get; set; } = DefaultIntervalSeconds;
    /// <summary>Local start of the night pause, <c>HH:mm</c>, or null.</summary>
    public string? NightStart { get; set; }
    /// <summary>Local end of the night pause, <c>HH:mm</c>, or null.</summary>
    public string? NightEnd { get; set; }
    /// <summary>IANA time zone, e.g. <c>Europe/Berlin</c>.</summary>
    public string TimeZone { get; set; } = DefaultTimeZone;
    public int LowBattMv { get; set; } = DefaultLowBattMv;
    /// <summary>When set, sent as <c>X-Url</c> to move the device to a new update URL.</summary>
    public string? PendingUrl { get; set; }

    /// <summary>SHA-256 of the device token, lowercase hex. The token itself is never stored.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Current image ETag as 16 hex chars (no quotes), or null when no image was uploaded yet.</summary>
    public string? ETagHex { get; set; }
    public DateTimeOffset? ImageUpdatedAt { get; set; }
    /// <summary>Stored versions (ETag hex), oldest first; the last entry is the current image.</summary>
    public List<string> Versions { get; set; } = [];

    public DeviceTelemetry Telemetry { get; set; } = new();
}

/// <summary>Last values reported by the device in its request headers (protocol.md §2.1).</summary>
public sealed class DeviceTelemetry
{
    public DateTimeOffset? LastSeen { get; set; }
    public string? RemoteIp { get; set; }
    public string? Firmware { get; set; }
    public int? BattMv { get; set; }
    public int? Rssi { get; set; }
    /// <summary><c>init</c> or <c>timer</c>.</summary>
    public string? Boot { get; set; }
    /// <summary>Error name (protocol.md §4) of the previous failed cycle; null when the header was absent.</summary>
    public string? LastError { get; set; }
    public string? UserAgent { get; set; }
    /// <summary>What the server answered: <c>full</c>, <c>region</c>, <c>not_modified</c> or <c>no_image</c>.</summary>
    public string? LastResponse { get; set; }
    /// <summary>
    /// The <c>X-Interval</c> the server last sent (seconds; longer during the night pause). Lets the
    /// admin page tell a sleeping panel from an overdue one.
    /// </summary>
    public int? LastIntervalSeconds { get; set; }
}
