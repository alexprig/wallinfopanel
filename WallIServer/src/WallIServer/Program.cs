using WallIServer;
using WallIServer.Endpoints;
using WallIServer.Security;
using WallIServer.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<WallIServerOptions>()
    .Bind(builder.Configuration.GetSection(WallIServerOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DeviceStore>();
builder.Services.AddSingleton<AdminApiKeyFilter>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); // /openapi/v1.json
}

// Setup page: /setup/ → wwwroot/setup/index.html
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/", () => Results.Redirect("/setup/")).ExcludeFromDescription();

app.MapPanelEndpoint();
app.MapServerInfoEndpoint();
app.MapAdminEndpoints();

app.Run();

/// <summary>Entry point marker for WebApplicationFactory in tests.</summary>
public partial class Program;
