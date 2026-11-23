using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WallIServer.Imaging;

namespace WallIServer.Tests;

public class PanelEndpointTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static byte[] WhiteLevels()
    {
        var l = new byte[Gray2.PixelCount];
        Array.Fill(l, Gray2.White);
        return l;
    }

    [Fact]
    public async Task MissingToken_Is401()
    {
        await _app.CreateDeviceAsync("kitchen");
        using var client = _app.CreateClient();
        var res = await client.GetAsync("/api/panel/kitchen");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task WrongToken_Is401()
    {
        await _app.CreateDeviceAsync("kitchen");
        await _app.SetImageAsync("kitchen", Gray2.CreateWhiteBody());
        using var client = _app.Device("not-the-token");
        var res = await client.GetAsync("/api/panel/kitchen");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task UnknownDevice_Is401_SameAsWrongToken()
    {
        // Device ids must not be discoverable without a valid token (protocol.md §2.2).
        await _app.CreateDeviceAsync("kitchen");
        using var client = _app.Device("x");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var wrongToken = await client.GetAsync("/api/panel/kitchen");
        var wrongTokenTime = sw.Elapsed;
        sw.Restart();
        var unknown = await client.GetAsync("/api/panel/nope");
        var unknownTime = sw.Elapsed;
        // Both rejections are padded to the same minimum duration (timing must not reveal ids).
        Assert.True(wrongTokenTime >= WallIServer.Endpoints.PanelEndpoint.MinRejectDuration - TimeSpan.FromMilliseconds(5), $"wrong token: {wrongTokenTime}");
        Assert.True(unknownTime >= WallIServer.Endpoints.PanelEndpoint.MinRejectDuration - TimeSpan.FromMilliseconds(5), $"unknown: {unknownTime}");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        // Identical apart from the per-request traceId.
        var a = await wrongToken.Content.ReadFromJsonAsync<JsonElement>();
        var b = await unknown.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(a.GetProperty("title").GetString(), b.GetProperty("title").GetString());
        Assert.Equal(a.GetProperty("status").GetInt32(), b.GetProperty("status").GetInt32());
        Assert.False(b.TryGetProperty("error", out _));
        Assert.Equal(wrongToken.Headers.WwwAuthenticate.ToString(), unknown.Headers.WwwAuthenticate.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/panel/Bad_Id")).StatusCode);
    }

    [Fact]
    public async Task RejectedRequest_DoesNotBlockTheRealDevice()
    {
        // The 401 padding delay must happen outside the device lock (DoS protection).
        var token = await _app.CreateDeviceAsync("kitchen");
        using var attacker = _app.Device("wrong-token");
        using var device = _app.Device(token);

        var rejected = attacker.GetAsync("/api/panel/kitchen");  // sits in its padding delay
        await Task.Delay(50);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await device.GetAsync("/api/panel/kitchen");   // same device, same lock
        var elapsed = sw.Elapsed;

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);    // no image yet, but served
        Assert.True(elapsed < TimeSpan.FromMilliseconds(150), $"real device waited {elapsed}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await rejected).StatusCode);
    }

    [Fact]
    public async Task NoImage_Is404WithNoImageError()
    {
        var token = await _app.CreateDeviceAsync("kitchen");
        using var client = _app.Device(token);
        var res = await client.GetAsync("/api/panel/kitchen");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no_image", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task FullImage_HasProtocolHeaders()
    {
        var token = await _app.CreateDeviceAsync("kitchen");
        var body = TestImages.WithRect(WhiteLevels(), 10, 10, 50, 50, Gray2.Black);
        var etag = await _app.SetImageAsync("kitchen", body);
        using var client = _app.Device(token);

        var res = await client.GetAsync("/api/panel/kitchen");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("gray2", Header(res, "X-Format"));
        Assert.Equal(etag, res.Headers.ETag!.Tag);
        Assert.Equal("\"" + ETag.ComputeHex(body) + "\"", etag);
        Assert.Equal("application/octet-stream", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal(53_856, res.Content.Headers.ContentLength);
        Assert.Equal("900", Header(res, "X-Interval"));
        Assert.Equal("3300", Header(res, "X-Low-Batt-mV"));
        Assert.False(res.Headers.Contains("X-Url"));
        Assert.False(res.Headers.Contains("X-Region"));
        Assert.False(res.Headers.Contains("X-Base"));
        Assert.Equal(body, await res.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task MatchingETag_Is304WithParameters()
    {
        var token = await _app.CreateDeviceAsync("kitchen", new { intervalSeconds = 1200, lowBattMv = 3500 });
        var etag = await _app.SetImageAsync("kitchen", Gray2.CreateWhiteBody());
        using var client = _app.Device(token);
        client.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", etag);

        var res = await client.GetAsync("/api/panel/kitchen");

        Assert.Equal(HttpStatusCode.NotModified, res.StatusCode);
        Assert.Equal(etag, res.Headers.ETag!.Tag);
        Assert.Equal("1200", Header(res, "X-Interval"));
        Assert.Equal("3500", Header(res, "X-Low-Batt-mV"));
        Assert.Empty(await res.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task OlderETag_SmallChange_SendsRegion()
    {
        var token = await _app.CreateDeviceAsync("kitchen");
        var a = Gray2.CreateWhiteBody();
        var oldEtag = await _app.SetImageAsync("kitchen", a);
        var b = TestImages.WithRect(WhiteLevels(), 100, 50, 20, 10, Gray2.Black);
        var newEtag = await _app.SetImageAsync("kitchen", b);
        using var client = _app.Device(token);
        client.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", oldEtag);

        var res = await client.GetAsync("/api/panel/kitchen");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("96,50,24,10", Header(res, "X-Region"));
        Assert.Equal(oldEtag, Header(res, "X-Base"));
        Assert.Equal(newEtag, res.Headers.ETag!.Tag);
        Assert.Equal("gray2", Header(res, "X-Format"));
        Assert.Equal(2 * 10 * 24 / 8, res.Content.Headers.ContentLength);

        var regionBody = await res.Content.ReadAsByteArrayAsync();
        var patched = (byte[])a.Clone();
        Gray2Regions.Apply(patched, new Gray2Region(96, 50, 24, 10), regionBody);
        Assert.Equal(b, patched);
    }

    [Fact]
    public async Task OlderETag_LargeChange_SendsFullImage()
    {
        var token = await _app.CreateDeviceAsync("kitchen");
        var oldEtag = await _app.SetImageAsync("kitchen", Gray2.CreateWhiteBody());
        // 70 % of the rows change → region would exceed 0.6 · 53856.
        var b = TestImages.WithRect(WhiteLevels(), 0, 0, 792, 200, Gray2.Black);
        await _app.SetImageAsync("kitchen", b);
        using var client = _app.Device(token);
        client.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", oldEtag);

        var res = await client.GetAsync("/api/panel/kitchen");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(res.Headers.Contains("X-Region"));
        Assert.False(res.Headers.Contains("X-Base"));
        Assert.Equal(53_856, res.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task UnknownETag_SendsFullImage()
    {
        var token = await _app.CreateDeviceAsync("kitchen");
        await _app.SetImageAsync("kitchen", Gray2.CreateWhiteBody());
        using var client = _app.Device(token);
        client.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", "\"0123456789abcdef\"");
        var res = await client.GetAsync("/api/panel/kitchen");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(53_856, res.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task PrunedVersion_SendsFullImage()
    {
        var token = await _app.CreateDeviceAsync("kitchen");
        var first = await _app.SetImageAsync("kitchen", Gray2.CreateWhiteBody());
        for (int i = 0; i < 8; i++)
            await _app.SetImageAsync("kitchen", TestImages.WithRect(WhiteLevels(), i * 8, 0, 8, 1, Gray2.Black));
        using var client = _app.Device(token);
        client.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", first);
        var res = await client.GetAsync("/api/panel/kitchen");
        Assert.False(res.Headers.Contains("X-Region"));
        Assert.Equal(53_856, res.Content.Headers.ContentLength);
        Assert.Equal(8, Directory.GetFiles(Path.Combine(_app.DataDir, "devices", "kitchen", "versions"), "*.g2").Length);
    }

    [Fact]
    public async Task Telemetry_IsRecorded()
    {
        var token = await _app.CreateDeviceAsync("kitchen");
        await _app.SetImageAsync("kitchen", Gray2.CreateWhiteBody());
        _app.Time.Now = new DateTimeOffset(2026, 11, 21, 8, 30, 0, TimeSpan.Zero);
        using var client = _app.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/panel/kitchen");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        req.Headers.TryAddWithoutValidation("X-Fw", "0.1.2");
        req.Headers.TryAddWithoutValidation("X-Batt-mV", "3712");
        req.Headers.TryAddWithoutValidation("X-Rssi", "-67");
        req.Headers.TryAddWithoutValidation("X-Boot", "init");
        req.Headers.TryAddWithoutValidation("X-Last-Error", "wifi_auth");
        req.Headers.TryAddWithoutValidation("User-Agent", "WallISetup/0.1.2");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(req)).StatusCode);

        using var admin = _app.Admin();
        var dev = await admin.GetFromJsonAsync<JsonElement>("/api/devices/kitchen");
        var t = dev.GetProperty("telemetry");
        Assert.Equal("0.1.2", t.GetProperty("firmware").GetString());
        Assert.Equal(3712, t.GetProperty("battMv").GetInt32());
        Assert.Equal(-67, t.GetProperty("rssi").GetInt32());
        Assert.Equal("init", t.GetProperty("boot").GetString());
        Assert.Equal("wifi_auth", t.GetProperty("lastError").GetString());
        Assert.Equal("WallISetup/0.1.2", t.GetProperty("userAgent").GetString());
        Assert.Equal("full", t.GetProperty("lastResponse").GetString());
        Assert.Equal(_app.Time.Now, t.GetProperty("lastSeen").GetDateTimeOffset());
    }

    [Fact]
    public async Task Telemetry_RecordsTheIntervalTheServerSent()
    {
        // The admin page needs this to avoid flagging a panel in its night pause as overdue.
        var token = await _app.CreateDeviceAsync("kitchen", new { nightStart = "22:00", nightEnd = "07:00", timeZone = "UTC" });
        await _app.SetImageAsync("kitchen", Gray2.CreateWhiteBody());
        _app.Time.Now = new DateTimeOffset(2026, 11, 21, 23, 0, 0, TimeSpan.Zero); // inside the night window
        using var device = _app.Device(token);
        var res = await device.GetAsync("/api/panel/kitchen");
        var sent = int.Parse(res.Headers.GetValues("X-Interval").Single());
        Assert.Equal(8 * 3600, sent); // until 07:00

        using var admin = _app.Admin();
        var dev = await admin.GetFromJsonAsync<JsonElement>("/api/devices/kitchen");
        Assert.Equal(sent, dev.GetProperty("telemetry").GetProperty("lastIntervalSeconds").GetInt32());
    }

    [Fact]
    public async Task NightWindow_IsAppliedInDeviceTimeZone()
    {
        var token = await _app.CreateDeviceAsync("kitchen", new { nightStart = "22:00", nightEnd = "06:00", timeZone = "Europe/Berlin" });
        await _app.SetImageAsync("kitchen", Gray2.CreateWhiteBody());
        _app.Time.Now = new DateTimeOffset(2026, 1, 15, 20, 50, 0, TimeSpan.Zero); // 21:50 in Berlin
        using var client = _app.Device(token);
        var res = await client.GetAsync("/api/panel/kitchen");
        Assert.Equal((8 * 3600 + 600).ToString(), Header(res, "X-Interval"));
    }

    [Fact]
    public async Task PendingUrl_IsSentAsXUrl()
    {
        var token = await _app.CreateDeviceAsync("kitchen", new { pendingUrl = "http://10.0.0.5:5080/api/panel/kitchen" });
        await _app.SetImageAsync("kitchen", Gray2.CreateWhiteBody());
        using var client = _app.Device(token);
        var res = await client.GetAsync("/api/panel/kitchen");
        Assert.Equal("http://10.0.0.5:5080/api/panel/kitchen", Header(res, "X-Url"));
    }

    private static string? Header(HttpResponseMessage res, string name) =>
        res.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() :
        res.Content.Headers.TryGetValues(name, out var c) ? c.FirstOrDefault() : null;
}
