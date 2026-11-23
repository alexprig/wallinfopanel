using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using WallIServer.Imaging;
using WallIServer.Storage;

namespace WallIServer.Tests;

/// <summary>A controllable clock.</summary>
public sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>The app on a TestServer with its own temp data directory and a fake clock.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public const string AdminKey = "test-admin-key-123";

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "walliserver-tests", Guid.NewGuid().ToString("N"));
    public FakeTime Time { get; } = new(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WallIServer:AdminApiKey"] = AdminKey,
            ["WallIServer:DataDirectory"] = DataDir,
        }));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
        });
    }

    public DeviceStore Store => Services.GetRequiredService<DeviceStore>();

    public HttpClient Admin()
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", AdminKey);
        return c;
    }

    public HttpClient Device(string token)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        c.DefaultRequestHeaders.Add("X-Fw", "0.1.0");
        c.DefaultRequestHeaders.Add("X-Batt-mV", "3900");
        c.DefaultRequestHeaders.Add("X-Rssi", "-60");
        c.DefaultRequestHeaders.Add("X-Boot", "timer");
        c.DefaultRequestHeaders.UserAgent.ParseAdd("WallISetup/0.1.0");
        return c;
    }

    /// <summary>Creates a device through the admin API and returns its token.</summary>
    public async Task<string> CreateDeviceAsync(string id, object? extra = null)
    {
        using var admin = Admin();
        var body = new Dictionary<string, object?> { ["id"] = id };
        if (extra is not null)
            foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
        var res = await admin.PostAsJsonAsync("/api/devices", body);
        res.EnsureSuccessStatusCode();
        var created = await res.Content.ReadFromJsonAsync<CreatedDto>();
        return created!.Token;
    }

    /// <summary>Stores a raw gray2 body as the device's new current image (bypasses the image pipeline).</summary>
    public async Task<string> SetImageAsync(string id, byte[] body)
    {
        using (await Store.AcquireAsync(id))
        {
            var d = await Store.LoadAsync(id) ?? throw new InvalidOperationException("no device");
            var hex = await Store.AddVersionAsync(d, body, Time.GetUtcNow());
            await Store.SaveAsync(d);
            return ETag.Quote(hex);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public sealed record CreatedDto(System.Text.Json.JsonElement Device, string Token);
}

public static class TestImages
{
    /// <summary>Encodes a solid-colour PNG.</summary>
    public static byte[] SolidPng(int w, int h, SKColor color)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        bmp.Erase(color);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A horizontal black→white gradient PNG.</summary>
    public static byte[] GradientPng(int w, int h)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (int x = 0; x < w; x++)
        {
            byte v = (byte)(x * 255 / Math.Max(1, w - 1));
            for (int y = 0; y < h; y++) bmp.SetPixel(x, y, new SKColor(v, v, v));
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Body with every pixel at <paramref name="level"/>.</summary>
    public static byte[] Flat(byte level)
    {
        var levels = new byte[Gray2.PixelCount];
        Array.Fill(levels, level);
        return Gray2.Pack(levels);
    }

    /// <summary>Copy of <paramref name="levels"/> with a filled rectangle, packed.</summary>
    public static byte[] WithRect(byte[] levels, int x, int y, int w, int h, byte level)
    {
        var copy = (byte[])levels.Clone();
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
                copy[yy * Gray2.Width + xx] = level;
        return Gray2.Pack(copy);
    }
}
