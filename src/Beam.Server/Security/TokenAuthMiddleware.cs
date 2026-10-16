namespace Beam.Server.Security;

public class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;

    public TokenAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, BeamServerOptions options, ILogger<TokenAuthMiddleware> logger)
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
            var detail = provided.Count == 0
                ? "X-Beam-Token header was not received."
                : $"X-Beam-Token received {provided.Count} value(s), {provided[0]?.Length ?? 0} character(s), but did not match.";
            logger.LogWarning("Authentication rejected for {Method} {Path} from {RemoteIp}: {Detail}",
                context.Request.Method, path, context.Connection.RemoteIpAddress, detail);
            await context.Response.WriteAsync($"Invalid or missing token. {detail}");
            return;
        }

        await _next(context);
    }
}
