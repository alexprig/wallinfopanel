using WallIServer.Imaging;
using WallIServer.Storage;

namespace WallIServer.Endpoints;

/// <summary>
/// Body of <c>POST /api/devices</c> and <c>PUT /api/devices/{id}</c>.
/// On PUT, omitted (null) fields keep their value; an empty string clears
/// <c>nightStart</c>, <c>nightEnd</c> and <c>pendingUrl</c>.
/// </summary>
public sealed record DeviceRequest
{
    /// <summary>Device id, <c>^[a-z0-9-]{1,32}$</c>. POST only; must be omitted or equal on PUT.</summary>
    public string? Id { get; init; }
    public string? Name { get; init; }
    /// <summary>180 … 86400, default 900.</summary>
    public int? IntervalSeconds { get; init; }
    /// <summary><c>HH:mm</c> local time; set together with <c>nightEnd</c>.</summary>
    public string? NightStart { get; init; }
    /// <summary><c>HH:mm</c> local time; set together with <c>nightStart</c>.</summary>
    public string? NightEnd { get; init; }
    /// <summary>IANA time zone, default <c>UTC</c>.</summary>
    public string? TimeZone { get; init; }
    /// <summary>2800 … 4200, default 3300.</summary>
    public int? LowBattMv { get; init; }
    /// <summary>Extension: when set, the server sends it as <c>X-Url</c> (must start with http://, ≤ 160 chars).</summary>
    public string? PendingUrl { get; init; }
}

public sealed record DeviceResponse(
    string Id,
    string Name,
    int IntervalSeconds,
    string? NightStart,
    string? NightEnd,
    string TimeZone,
    int LowBattMv,
    string? PendingUrl,
    string? Etag,
    DateTimeOffset? ImageUpdatedAt,
    int StoredVersions,
    int NextInterval,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DeviceTelemetry Telemetry)
{
    public static DeviceResponse From(DeviceRecord d, DateTimeOffset now) => new(
        d.Id, d.Name, d.IntervalSeconds, d.NightStart, d.NightEnd, d.TimeZone, d.LowBattMv, d.PendingUrl,
        d.ETagHex is null ? null : Imaging.ETag.Quote(d.ETagHex),
        d.ImageUpdatedAt, d.Versions.Count, PanelEndpoint.ComputeInterval(d, now),
        d.CreatedAt, d.UpdatedAt, d.Telemetry);
}

public sealed record CreateDeviceResponse(DeviceResponse Device, string Token);

public sealed record TokenResponse(string Token);

public sealed record ImageUploadResponse(string Etag, int Bytes, Gray2Region? ChangedRegion);
