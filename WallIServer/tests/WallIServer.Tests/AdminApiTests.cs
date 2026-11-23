using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SkiaSharp;
using WallIServer.Imaging;
using WallIServer.Security;

namespace WallIServer.Tests;

public class AdminApiTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task ApiKey_IsRequired()
    {
        using var anon = _app.CreateClient();
        var res = await anon.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType!.MediaType);

        using var wrong = _app.CreateClient();
        wrong.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.PostAsJsonAsync("/api/devices", new { id = "x" })).StatusCode);

        using var admin = _app.Admin();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/devices")).StatusCode);
    }

    [Fact]
    public async Task Create_ReturnsTokenOnce_AndStoresOnlyItsHash()
    {
        using var admin = _app.Admin();
        var res = await admin.PostAsJsonAsync("/api/devices", new { id = "livingroom", name = "Living room", timeZone = "Europe/Berlin" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal("/api/devices/livingroom", res.Headers.Location!.OriginalString);
        var created = await res.Content.ReadFromJsonAsync<JsonElement>();
        var token = created.GetProperty("token").GetString()!;
        Assert.InRange(token.Length, 1, 64);
        Assert.Equal("livingroom", created.GetProperty("device").GetProperty("id").GetString());
        Assert.Equal(900, created.GetProperty("device").GetProperty("intervalSeconds").GetInt32());

        var getText = await admin.GetStringAsync("/api/devices/livingroom");
        Assert.DoesNotContain(token, getText);
        Assert.DoesNotContain("token", getText, StringComparison.OrdinalIgnoreCase);
        var listText = await admin.GetStringAsync("/api/devices");
        Assert.DoesNotContain(token, listText);
        Assert.Contains("livingroom", listText);

        var json = await File.ReadAllTextAsync(Path.Combine(_app.DataDir, "devices", "livingroom", "device.json"));
        Assert.DoesNotContain(token, json);
        Assert.Contains(Tokens.Hash(token), json);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/devices", new { id = "livingroom" })).StatusCode);
    }

    [Theory]
    [InlineData("{\"id\":\"Bad_Id\"}", "id")]
    [InlineData("{}", "id")]
    [InlineData("{\"id\":\"a\",\"intervalSeconds\":100}", "intervalSeconds")]
    [InlineData("{\"id\":\"a\",\"intervalSeconds\":90000}", "intervalSeconds")]
    [InlineData("{\"id\":\"a\",\"lowBattMv\":2000}", "lowBattMv")]
    [InlineData("{\"id\":\"a\",\"timeZone\":\"Mars/Olympus\"}", "timeZone")]
    [InlineData("{\"id\":\"a\",\"nightStart\":\"25:00\",\"nightEnd\":\"06:00\"}", "nightStart")]
    [InlineData("{\"id\":\"a\",\"nightStart\":\"22:00\"}", "nightEnd")]
    [InlineData("{\"id\":\"a\",\"pendingUrl\":\"https://x/y\"}", "pendingUrl")]
    public async Task Create_ValidatesInput(string body, string field)
    {
        using var admin = _app.Admin();
        var res = await admin.PostAsync("/api/devices", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType!.MediaType);
        var problem = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), $"expected an error for {field}: {problem}");
    }

    [Fact]
    public async Task Update_ChangesFields_AndEmptyStringClearsNight()
    {
        await _app.CreateDeviceAsync("hall", new { nightStart = "22:00", nightEnd = "06:00" });
        using var admin = _app.Admin();
        var res = await admin.PutAsJsonAsync("/api/devices/hall", new { name = "Hall", intervalSeconds = 1800, nightStart = "", nightEnd = "" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Hall", d.GetProperty("name").GetString());
        Assert.Equal(1800, d.GetProperty("intervalSeconds").GetInt32());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("nightStart").ValueKind);
        Assert.Equal("UTC", d.GetProperty("timeZone").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/devices/hall", new { intervalSeconds = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/devices/nobody", new { name = "x" })).StatusCode);
    }

    [Fact]
    public async Task RotateToken_InvalidatesOldToken()
    {
        var oldToken = await _app.CreateDeviceAsync("hall");
        await _app.SetImageAsync("hall", Gray2.CreateWhiteBody());
        using var admin = _app.Admin();
        var res = await admin.PostAsync("/api/devices/hall/token", null);
        var newToken = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        Assert.NotEqual(oldToken, newToken);

        using var oldClient = _app.Device(oldToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldClient.GetAsync("/api/panel/hall")).StatusCode);
        using var newClient = _app.Device(newToken);
        Assert.Equal(HttpStatusCode.OK, (await newClient.GetAsync("/api/panel/hall")).StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesDevice()
    {
        await _app.CreateDeviceAsync("hall");
        using var admin = _app.Admin();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/devices/hall")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/devices/hall")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/devices/hall")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_app.DataDir, "devices", "hall")));
    }

    [Fact]
    public async Task UploadImage_ThenPreviewPngDecodes()
    {
        await _app.CreateDeviceAsync("hall");
        using var admin = _app.Admin();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/devices/hall/image.png")).StatusCode);

        var res = await admin.PostAsync("/api/devices/hall/image?fit=cover&dither=true", ImageForm(TestImages.GradientPng(640, 480), "photo.png"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var upload = await res.Content.ReadFromJsonAsync<JsonElement>();
        var etag = upload.GetProperty("etag").GetString()!;
        Assert.Matches("^\"[0-9a-f]{16}\"$", etag);
        Assert.Equal(53_856, upload.GetProperty("bytes").GetInt32());

        var dev = await admin.GetFromJsonAsync<JsonElement>("/api/devices/hall");
        Assert.Equal(etag, dev.GetProperty("etag").GetString());

        var png = await admin.GetAsync("/api/devices/hall/image.png");
        Assert.Equal(HttpStatusCode.OK, png.StatusCode);
        Assert.Equal("image/png", png.Content.Headers.ContentType!.MediaType);
        using var bmp = SKBitmap.Decode(await png.Content.ReadAsByteArrayAsync());
        Assert.NotNull(bmp);
        Assert.Equal(792, bmp.Width);
        Assert.Equal(272, bmp.Height);
        var greys = new HashSet<byte>();
        for (int x = 0; x < bmp.Width; x += 3) greys.Add(bmp.GetPixel(x, 100).Red);
        Assert.Subset(new HashSet<byte> { 0x00, 0x55, 0xAA, 0xFF }, greys);
        Assert.Equal(4, greys.Count);
    }

    [Fact]
    public async Task UploadImage_PasteMode_CreatesPartialChange()
    {
        var token = await _app.CreateDeviceAsync("hall");
        using var admin = _app.Admin();
        var first = await admin.PostAsync("/api/devices/hall/image?fit=stretch&dither=false", ImageForm(TestImages.SolidPng(10, 10, SKColors.White), "white.png"));
        var baseEtag = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("etag").GetString()!;

        var res = await admin.PostAsync("/api/devices/hall/image?x=400&y=100", ImageForm(TestImages.SolidPng(16, 16, SKColors.Black), "dot.png"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var changed = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changedRegion");
        Assert.Equal(400, changed.GetProperty("x").GetInt32());
        Assert.Equal(100, changed.GetProperty("y").GetInt32());
        Assert.Equal(16, changed.GetProperty("w").GetInt32());
        Assert.Equal(16, changed.GetProperty("h").GetInt32());

        using var device = _app.Device(token);
        device.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", baseEtag);
        var panel = await device.GetAsync("/api/panel/hall");
        Assert.Equal("400,100,16,16", panel.Headers.GetValues("X-Region").Single());
        Assert.Equal(2 * 16 * 2, panel.Content.Headers.ContentLength);
    }

    [Theory]
    [InlineData("?fit=zoom", "fit")]
    [InlineData("?dither=maybe", "dither")]
    [InlineData("?x=5", "y")]
    [InlineData("?x=abc&y=1", "x")]
    public async Task UploadImage_ValidatesQuery(string query, string field)
    {
        await _app.CreateDeviceAsync("hall");
        using var admin = _app.Admin();
        var res = await admin.PostAsync("/api/devices/hall/image" + query, ImageForm(TestImages.SolidPng(4, 4, SKColors.Gray), "a.png"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task UploadImage_RejectsNonImage_AndMissingFile()
    {
        await _app.CreateDeviceAsync("hall");
        using var admin = _app.Admin();
        var bad = await admin.PostAsync("/api/devices/hall/image", ImageForm("hello"u8.ToArray(), "a.txt"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var missing = await admin.PostAsync("/api/devices/hall/image", new MultipartFormDataContent());
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var notFound = await admin.PostAsync("/api/devices/ghost/image", ImageForm(TestImages.SolidPng(4, 4, SKColors.Gray), "a.png"));
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task VersionsArePrunedToMax()
    {
        await _app.CreateDeviceAsync("hall");
        using var admin = _app.Admin();
        for (int i = 0; i < 10; i++)
        {
            var png = TestImages.SolidPng(8, 8, new SKColor((byte)(i * 25), (byte)(i * 25), (byte)(i * 25)));
            var res = await admin.PostAsync($"/api/devices/hall/image?x={i * 8}&y=0", ImageForm(png, "p.png"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
        var dev = await admin.GetFromJsonAsync<JsonElement>("/api/devices/hall");
        Assert.Equal(8, dev.GetProperty("storedVersions").GetInt32());
        Assert.Equal(8, Directory.GetFiles(Path.Combine(_app.DataDir, "devices", "hall", "versions")).Length);
    }

    private static MultipartFormDataContent ImageForm(byte[] bytes, string fileName)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);
        return content;
    }
}
