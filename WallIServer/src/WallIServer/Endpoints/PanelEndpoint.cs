using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using WallIServer.Imaging;
using WallIServer.Scheduling;
using WallIServer.Security;
using WallIServer.Storage;

namespace WallIServer.Endpoints;

/// <summary><c>GET /api/panel/{deviceId}</c> — the device update endpoint (protocol.md §2).</summary>
public static class PanelEndpoint
{
    public const string FormatGray2 = "gray2";

    public static IEndpointRouteBuilder MapPanelEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/panel/{deviceId}", HandleAsync)
            .WithName("GetPanelImage")
            .WithTags("Device")
            .WithSummary("Device update call: returns 304, a region or the full gray2 image plus parameter headers.")
            .ExcludeFromDescription(); // binary protocol endpoint; documented in protocol.md
        return app;
    }

    internal static async Task HandleAsync(
        string deviceId,
        HttpContext http,
        DeviceStore store,
        TimeProvider time,
        IOptions<WallIServerOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("WallIServer.Panel");
        var response = http.Response;
        response.Headers.CacheControl = "no-cache";

        // Unknown device, invalid id and wrong token all get the same 401, so device ids
        // can't be discovered without a valid token (protocol.md §2.2). Every rejection does the
        // same token work and is padded to a minimum duration, so timing doesn't tell them apart.
        var started = Stopwatch.GetTimestamp();
        var presented = GetBearerToken(http.Request);
        if (!DeviceStore.IsValidId(deviceId))
        {
            Tokens.Verify(presented, DummyTokenHash);
            await RejectAsync(response, started, ct);
            return;
        }

        using var deviceLock = await store.AcquireAsync(deviceId, ct);
        var device = await store.LoadAsync(deviceId, ct);
        var tokenOk = Tokens.Verify(presented, device?.TokenHash ?? DummyTokenHash); // always runs
        if (device is null || !tokenOk)
        {
            // Release the lock BEFORE the padding delay: rejected requests must never block a real
            // device that shares the lock stripe. (Release is idempotent; `using` is then a no-op.)
            deviceLock.Dispose();
            log.LogWarning("Device {DeviceId}: rejected (unknown device or wrong token) from {RemoteIp}",
                deviceId, http.Connection.RemoteIpAddress);
            await RejectAsync(response, started, ct);
            return;
        }

        var now = time.GetUtcNow();
        RecordTelemetry(device, http, now);
        ClearPendingUrlIfReached(device, http.Request);

        if (device.ETagHex is null)
        {
            device.Telemetry.LastResponse = "no_image";
            await store.SaveAsync(device, ct);
            await WriteJsonError(response, StatusCodes.Status404NotFound, "no_image", ct);
            return;
        }

        var currentHex = device.ETagHex;
        var current = await store.ReadVersionAsync(deviceId, currentHex, ct);
        if (current is null)
        {
            // Should not happen: device.json references a missing version file.
            log.LogError("Device {DeviceId}: current version {ETag} missing on disk", deviceId, currentHex);
            device.Telemetry.LastResponse = "no_image";
            await store.SaveAsync(device, ct);
            await WriteJsonError(response, StatusCodes.Status404NotFound, "no_image", ct);
            return;
        }

        WriteParameterHeaders(response, device, now);
        response.Headers.ETag = ETag.Quote(currentHex);

        var candidates = ETag.ParseIfNoneMatch(http.Request.Headers.IfNoneMatch.ToString());
        if (candidates.Contains(currentHex))
        {
            device.Telemetry.LastResponse = "not_modified";
            await store.SaveAsync(device, ct);
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        // Region against an older stored version, if it's small enough.
        foreach (var baseHex in candidates)
        {
            if (!device.Versions.Contains(baseHex)) continue;
            var baseBody = await store.ReadVersionAsync(deviceId, baseHex, ct);
            if (baseBody is null) continue;

            var region = Gray2Regions.DiffBoundingBox(baseBody, current);
            if (region is not { } r) continue; // identical content can't have a different ETag
            if (r.BodyLength > options.Value.RegionMaxRatio * Gray2.BodySize) continue; // too big: full image

            var regionBody = Gray2Regions.Extract(current, r);
            device.Telemetry.LastResponse = "region";
            await store.SaveAsync(device, ct);
            response.Headers["X-Region"] = r.ToHeaderValue();
            response.Headers["X-Base"] = ETag.Quote(baseHex);
            await WriteBody(response, regionBody, ct);
            return;
        }

        device.Telemetry.LastResponse = "full";
        await store.SaveAsync(device, ct);
        await WriteBody(response, current, ct);
    }

    internal static void WriteParameterHeaders(HttpResponse response, DeviceRecord device, DateTimeOffset nowUtc)
    {
        var interval = ComputeInterval(device, nowUtc);
        device.Telemetry.LastIntervalSeconds = interval; // saved by the caller with the rest of the telemetry
        response.Headers["X-Interval"] = interval.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-Low-Batt-mV"] = device.LowBattMv.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(device.PendingUrl))
            response.Headers["X-Url"] = device.PendingUrl;
    }

    internal static int ComputeInterval(DeviceRecord device, DateTimeOffset nowUtc)
    {
        TimeOnly? start = IntervalCalculator.TryParseTime(device.NightStart, out var s) ? s : null;
        TimeOnly? end = IntervalCalculator.TryParseTime(device.NightEnd, out var e) ? e : null;
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(device.TimeZone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { tz = TimeZoneInfo.Utc; }
        return IntervalCalculator.Compute(nowUtc, device.IntervalSeconds, start, end, tz);
    }

    private static void RecordTelemetry(DeviceRecord device, HttpContext http, DateTimeOffset now)
    {
        var h = http.Request.Headers;
        var t = device.Telemetry;
        t.LastSeen = now;
        t.RemoteIp = http.Connection.RemoteIpAddress?.ToString();
        t.Firmware = Trunc(h["X-Fw"].ToString(), 32);
        t.BattMv = ParseInt(h["X-Batt-mV"].ToString());
        t.Rssi = ParseInt(h["X-Rssi"].ToString());
        t.Boot = Trunc(h["X-Boot"].ToString(), 16);
        t.LastError = Trunc(h["X-Last-Error"].ToString(), 32);
        t.UserAgent = Trunc(h.UserAgent.ToString(), 64);
    }

    /// <summary>
    /// The server can't see whether the device adopted <c>X-Url</c>, except when the new URL points back to this
    /// server: a request arriving at exactly the pending URL proves the move worked, so stop sending it.
    /// </summary>
    private static void ClearPendingUrlIfReached(DeviceRecord device, HttpRequest request)
    {
        if (string.IsNullOrEmpty(device.PendingUrl)) return;
        var requested = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}";
        if (string.Equals(requested.TrimEnd('/'), device.PendingUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            device.PendingUrl = null;
    }

    private static string? GetBearerToken(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return auth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? auth[prefix.Length..].Trim() : null;
    }

    private static async Task WriteBody(HttpResponse response, byte[] body, CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/octet-stream";
        response.ContentLength = body.Length;
        response.Headers["X-Format"] = FormatGray2;
        await response.Body.WriteAsync(body, ct);
    }

    /// <summary>Hash no real token produces (all zeros), used so unknown devices cost the same as known ones.</summary>
    private static readonly string DummyTokenHash = new('0', 64);

    /// <summary>Minimum time before any 401 is sent: hides lookup timing and slows token guessing.</summary>
    internal static readonly TimeSpan MinRejectDuration = TimeSpan.FromMilliseconds(250);

    private static async Task RejectAsync(HttpResponse response, long startedTimestamp, CancellationToken ct)
    {
        var remaining = MinRejectDuration - Stopwatch.GetElapsedTime(startedTimestamp);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
        response.StatusCode = StatusCodes.Status401Unauthorized;
        response.Headers.WWWAuthenticate = "Bearer";
    }

    private static Task WriteJsonError(HttpResponse response, int status, string error, CancellationToken ct)
    {
        response.StatusCode = status;
        return response.WriteAsJsonAsync(new { error }, ct);
    }

    private static int? ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? Trunc(string value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}
