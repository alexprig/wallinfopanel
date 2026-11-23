using System.Globalization;
using WallIServer.Imaging;
using WallIServer.Scheduling;
using WallIServer.Security;
using WallIServer.Storage;

namespace WallIServer.Endpoints;

/// <summary>Admin API (protocol.md §5), guarded by <c>X-Api-Key</c>.</summary>
public static class AdminEndpoints
{
    public const long MaxUploadBytes = 20 * 1024 * 1024;
    private const int MaxPasteOffset = 10_000;

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/devices")
            .AddEndpointFilter<AdminApiKeyFilter>()
            .WithTags("Admin")
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        g.MapGet("/", ListAsync)
            .WithName("ListDevices").WithSummary("List devices with telemetry (tokens are never returned).");
        g.MapPost("/", CreateAsync)
            .WithName("CreateDevice").WithSummary("Create a device. The token is returned only in this response.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
        g.MapGet("/{id}", GetAsync)
            .WithName("GetDevice").ProducesProblem(StatusCodes.Status404NotFound);
        g.MapPut("/{id}", UpdateAsync)
            .WithName("UpdateDevice").WithSummary("Update settings. Omitted fields stay; \"\" clears nightStart/nightEnd/pendingUrl.")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);
        g.MapDelete("/{id}", DeleteAsync)
            .WithName("DeleteDevice").ProducesProblem(StatusCodes.Status404NotFound);
        g.MapPost("/{id}/token", RotateTokenAsync)
            .WithName("RotateToken").WithSummary("Issue a new token; the old one stops working.")
            .ProducesProblem(StatusCodes.Status404NotFound);
        g.MapPost("/{id}/image", UploadImageAsync)
            .WithName("UploadImage")
            .WithSummary("Upload an image (multipart field 'file'). Query: fit=contain|cover|stretch, dither=true|false, optional x,y paste.")
            .Accepts<IFormFile>("multipart/form-data")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);
        g.MapGet("/{id}/image.png", PreviewAsync)
            .WithName("PreviewImage").WithSummary("PNG preview of the current gray2 image.")
            .Produces(StatusCodes.Status200OK, contentType: "image/png")
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<IResult> ListAsync(DeviceStore store, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var devices = await store.ListAsync(ct);
        return Results.Ok(devices.Select(d => DeviceResponse.From(d, now)).ToList());
    }

    private static async Task<IResult> CreateAsync(DeviceRequest? body, DeviceStore store, TimeProvider time, CancellationToken ct)
    {
        if (body is null) return BodyRequired();
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrEmpty(body.Id)) errors["id"] = ["id is required."];
        else if (!DeviceStore.IsValidId(body.Id)) errors["id"] = ["id must match ^[a-z0-9-]{1,32}$."];

        var now = time.GetUtcNow();
        var record = new DeviceRecord
        {
            Id = body.Id ?? "",
            Name = body.Id ?? "",
            TokenHash = "",
            CreatedAt = now,
            UpdatedAt = now,
        };
        ApplyAndValidate(record, body, errors);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var token = Tokens.Generate();
        record.TokenHash = Tokens.Hash(token);
        using (await store.AcquireAsync(record.Id, ct))
        {
            if (await store.LoadAsync(record.Id, ct) is not null)
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict",
                    detail: $"Device '{record.Id}' already exists.");
            await store.SaveAsync(record, ct);
        }
        return Results.Created($"/api/devices/{record.Id}", new CreateDeviceResponse(DeviceResponse.From(record, now), token));
    }

    private static async Task<IResult> GetAsync(string id, DeviceStore store, TimeProvider time, CancellationToken ct)
    {
        if (!DeviceStore.IsValidId(id)) return DeviceNotFound(id);
        using (await store.AcquireAsync(id, ct))
        {
            var d = await store.LoadAsync(id, ct);
            return d is null ? DeviceNotFound(id) : Results.Ok(DeviceResponse.From(d, time.GetUtcNow()));
        }
    }

    private static async Task<IResult> UpdateAsync(string id, DeviceRequest? body, DeviceStore store, TimeProvider time, CancellationToken ct)
    {
        if (!DeviceStore.IsValidId(id)) return DeviceNotFound(id);
        if (body is null) return BodyRequired();
        using (await store.AcquireAsync(id, ct))
        {
            var d = await store.LoadAsync(id, ct);
            if (d is null) return DeviceNotFound(id);

            var errors = new Dictionary<string, string[]>();
            if (body.Id is not null && body.Id != id) errors["id"] = ["id can't be changed."];
            ApplyAndValidate(d, body, errors);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var now = time.GetUtcNow();
            d.UpdatedAt = now;
            await store.SaveAsync(d, ct);
            return Results.Ok(DeviceResponse.From(d, now));
        }
    }

    private static async Task<IResult> DeleteAsync(string id, DeviceStore store, CancellationToken ct)
    {
        if (!DeviceStore.IsValidId(id)) return DeviceNotFound(id);
        using (await store.AcquireAsync(id, ct))
        {
            return store.Delete(id) ? Results.NoContent() : DeviceNotFound(id);
        }
    }

    private static async Task<IResult> RotateTokenAsync(string id, DeviceStore store, TimeProvider time, CancellationToken ct)
    {
        if (!DeviceStore.IsValidId(id)) return DeviceNotFound(id);
        using (await store.AcquireAsync(id, ct))
        {
            var d = await store.LoadAsync(id, ct);
            if (d is null) return DeviceNotFound(id);
            var token = Tokens.Generate();
            d.TokenHash = Tokens.Hash(token);
            d.UpdatedAt = time.GetUtcNow();
            await store.SaveAsync(d, ct);
            return Results.Ok(new TokenResponse(token));
        }
    }

    private static async Task<IResult> UploadImageAsync(string id, HttpRequest request, DeviceStore store, TimeProvider time, CancellationToken ct)
    {
        if (!DeviceStore.IsValidId(id)) return DeviceNotFound(id);

        var q = request.Query;
        var errors = new Dictionary<string, string[]>();

        var fit = FitMode.Contain;
        var fitText = q["fit"].ToString();
        if (fitText.Length > 0 && !TryParseFit(fitText, out fit))
            errors["fit"] = ["fit must be contain, cover or stretch."];

        bool dither = true;
        var ditherText = q["dither"].ToString();
        if (ditherText.Length > 0 && !bool.TryParse(ditherText, out dither))
            errors["dither"] = ["dither must be true or false."];

        int? x = ParseOffset(q["x"].ToString(), "x", errors);
        int? y = ParseOffset(q["y"].ToString(), "y", errors);
        if (x.HasValue != y.HasValue && !errors.ContainsKey("x") && !errors.ContainsKey("y"))
            errors[x.HasValue ? "y" : "x"] = ["x and y must be given together."];

        if (!request.HasFormContentType)
        {
            errors["file"] = ["Send the image as multipart/form-data in the field 'file'."];
            return Results.ValidationProblem(errors);
        }
        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
        {
            errors["file"] = [$"The multipart body could not be read: {ex.Message}"];
            return Results.ValidationProblem(errors);
        }
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) errors["file"] = ["The multipart field 'file' is missing or empty."];
        else if (file.Length > MaxUploadBytes) errors["file"] = [$"The file is larger than {MaxUploadBytes / (1024 * 1024)} MB."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        byte[] encoded;
        using (var ms = new MemoryStream((int)file!.Length))
        {
            await file.CopyToAsync(ms, ct);
            encoded = ms.ToArray();
        }

        using (await store.AcquireAsync(id, ct))
        {
            var d = await store.LoadAsync(id, ct);
            if (d is null) return DeviceNotFound(id);

            byte[]? previous = d.ETagHex is null ? null : await store.ReadVersionAsync(id, d.ETagHex, ct);
            byte[] body;
            try
            {
                body = x is { } px && y is { } py
                    ? ImageComposer.Paste(previous ?? Gray2.CreateWhiteBody(), encoded, px, py, dither)
                    : ImageComposer.Render(encoded, fit, dither);
            }
            catch (ImageDecodeException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [ex.Message] });
            }

            var now = time.GetUtcNow();
            var hex = await store.AddVersionAsync(d, body, now, ct);
            d.UpdatedAt = now;
            await store.SaveAsync(d, ct);
            var changed = previous is null ? new Gray2Region(0, 0, Gray2.Width, Gray2.Height) : Gray2Regions.DiffBoundingBox(previous, body);
            return Results.Ok(new ImageUploadResponse(ETag.Quote(hex), body.Length, changed));
        }
    }

    private static async Task<IResult> PreviewAsync(string id, DeviceStore store, CancellationToken ct)
    {
        if (!DeviceStore.IsValidId(id)) return DeviceNotFound(id);
        byte[]? body;
        using (await store.AcquireAsync(id, ct))
        {
            var d = await store.LoadAsync(id, ct);
            if (d is null) return DeviceNotFound(id);
            body = d.ETagHex is null ? null : await store.ReadVersionAsync(id, d.ETagHex, ct);
        }
        if (body is null)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No image", detail: $"Device '{id}' has no image yet.");
        return Results.File(PngPreview.Render(body), "image/png");
    }

    /// <summary>Validates <paramref name="req"/> and applies the valid fields to <paramref name="d"/>.</summary>
    internal static void ApplyAndValidate(DeviceRecord d, DeviceRequest req, Dictionary<string, string[]> errors)
    {
        if (req.Name is not null)
        {
            var name = req.Name.Trim();
            if (name.Length is < 1 or > 64) errors["name"] = ["name must be 1–64 characters."];
            else d.Name = name;
        }

        if (req.IntervalSeconds is { } interval)
        {
            if (interval is < IntervalCalculator.MinInterval or > IntervalCalculator.MaxInterval)
                errors["intervalSeconds"] = ["intervalSeconds must be between 180 and 86400."];
            else d.IntervalSeconds = interval;
        }

        if (req.LowBattMv is { } lowBatt)
        {
            if (lowBatt is < 2800 or > 4200) errors["lowBattMv"] = ["lowBattMv must be between 2800 and 4200."];
            else d.LowBattMv = lowBatt;
        }

        if (req.TimeZone is not null)
        {
            if (!TimeZoneExists(req.TimeZone)) errors["timeZone"] = [$"Unknown time zone '{req.TimeZone}'. Use an IANA id such as Europe/Berlin."];
            else d.TimeZone = req.TimeZone;
        }

        string? nightStart = d.NightStart, nightEnd = d.NightEnd;
        if (req.NightStart is not null)
        {
            if (req.NightStart.Length == 0) nightStart = null;
            else if (!IntervalCalculator.TryParseTime(req.NightStart, out _)) errors["nightStart"] = ["nightStart must be HH:mm (00:00–23:59)."];
            else nightStart = req.NightStart;
        }
        if (req.NightEnd is not null)
        {
            if (req.NightEnd.Length == 0) nightEnd = null;
            else if (!IntervalCalculator.TryParseTime(req.NightEnd, out _)) errors["nightEnd"] = ["nightEnd must be HH:mm (00:00–23:59)."];
            else nightEnd = req.NightEnd;
        }
        if (!errors.ContainsKey("nightStart") && !errors.ContainsKey("nightEnd"))
        {
            if ((nightStart is null) != (nightEnd is null))
                errors[nightStart is null ? "nightStart" : "nightEnd"] = ["nightStart and nightEnd must be set together."];
            else if (nightStart is not null && nightStart == nightEnd)
                errors["nightEnd"] = ["nightEnd must differ from nightStart."];
            else
            {
                d.NightStart = nightStart;
                d.NightEnd = nightEnd;
            }
        }

        if (req.PendingUrl is not null)
        {
            if (req.PendingUrl.Length == 0) d.PendingUrl = null;
            else if (!req.PendingUrl.StartsWith("http://", StringComparison.Ordinal) || req.PendingUrl.Length > 160
                     || !Uri.TryCreate(req.PendingUrl, UriKind.Absolute, out _))
                errors["pendingUrl"] = ["pendingUrl must be an absolute URL starting with http:// and at most 160 characters."];
            else d.PendingUrl = req.PendingUrl;
        }
    }

    private static bool TimeZoneExists(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static bool TryParseFit(string text, out FitMode fit)
    {
        switch (text.ToLowerInvariant())
        {
            case "contain": fit = FitMode.Contain; return true;
            case "cover": fit = FitMode.Cover; return true;
            case "stretch": fit = FitMode.Stretch; return true;
            default: fit = FitMode.Contain; return false;
        }
    }

    private static int? ParseOffset(string text, string name, Dictionary<string, string[]> errors)
    {
        if (text.Length == 0) return null;
        if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) && Math.Abs(v) <= MaxPasteOffset)
            return v;
        errors[name] = [$"{name} must be an integer between -{MaxPasteOffset} and {MaxPasteOffset}."];
        return null;
    }

    private static IResult DeviceNotFound(string id) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"Device '{id}' does not exist.");

    private static IResult BodyRequired() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Bad request", detail: "A JSON body is required.");
}
