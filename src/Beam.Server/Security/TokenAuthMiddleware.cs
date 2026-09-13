namespace Beam.Server.Security;

public class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;

    public TokenAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, BeamServerOptions options)
    {
        var path = context.Request.Path;
        if (path == "/" || path.StartsWithSegments("/app"))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-Beam-Token", out var provided) ||
            provided.Count == 0 ||
            provided[0] != options.ExpectedToken)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Invalid or missing token");
            return;
        }

        await _next(context);
    }
}
