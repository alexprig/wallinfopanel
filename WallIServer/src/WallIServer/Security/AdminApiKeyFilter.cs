using Microsoft.Extensions.Options;

namespace WallIServer.Security;

/// <summary>Rejects admin requests without a matching <c>X-Api-Key</c> header (constant-time compare).</summary>
public sealed class AdminApiKeyFilter(IOptions<WallIServerOptions> options) : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var presented = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(presented) || !Tokens.SecretEquals(presented, options.Value.AdminApiKey))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: $"A valid {HeaderName} header is required.");
        }
        return await next(context);
    }
}
