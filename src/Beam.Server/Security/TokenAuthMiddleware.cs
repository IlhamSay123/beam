namespace Beam.Server.Security;

public class TokenAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, PairingTokenStore tokens, ILogger<TokenAuthMiddleware> logger)
    {
        var path = context.Request.Path;
        if (path == "/" || path.StartsWithSegments("/app"))
        {
            await next(context);
            return;
        }
        context.Response.Headers.CacheControl = "no-store";
        var provided = context.Request.Headers["X-Beam-Token"];
        using var lease = provided.Count == 1 ? tokens.TryAcquire(provided[0]) : null;
        if (lease is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            logger.LogWarning("Authentication rejected for {Method} {Path} from {RemoteIp}.",
                context.Request.Method, path, context.Connection.RemoteIpAddress);
            await context.Response.WriteAsync("Invalid or missing token. Pair again with the PC.");
            return;
        }
        // Revocation aborts real transfers and waits for this handler's lease.
        using var revoked = lease.Revoked.Register(static state => ((HttpContext)state!).Abort(), context);
        lease.Revoked.ThrowIfCancellationRequested();
        await next(context);
    }
}
