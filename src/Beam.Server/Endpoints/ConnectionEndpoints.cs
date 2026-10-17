using System.Net;
using System.Text.Json;
using Beam.Server.Connection;

namespace Beam.Server.Endpoints;

public static class ConnectionEndpoints
{
    private const int BodyLimit = 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapConnectionEndpoints(this WebApplication app) =>
        app.MapPost("/connection/heartbeat", HeartbeatAsync);

    public static async Task<IResult> HeartbeatAsync(HttpContext context, ConnectionPresence presence)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (context.Request.ContentLength > BodyLimit) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        // Bound chunked bodies too, independently of the much larger file-upload limit.
        var bytes = new byte[BodyLimit + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await context.Request.Body.ReadAsync(bytes.AsMemory(length), context.RequestAborted);
            if (read == 0) break;
            length += read;
        }
        if (length > BodyLimit) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        HeartbeatRequest? request;
        try { request = JsonSerializer.Deserialize<HeartbeatRequest>(bytes.AsSpan(0, length), JsonOptions); }
        catch (JsonException) { return Results.BadRequest(); }
        if (request?.SessionId?.Length != 32 || !Guid.TryParseExact(request.SessionId, "N", out _)) return Results.BadRequest();

        var remote = Normalize(context.Connection.RemoteIpAddress);
        var local = Normalize(context.Connection.LocalIpAddress);
        // Local previews and diagnostic checks must not claim that a phone paired.
        if (remote is not null && !IPAddress.IsLoopback(remote) && !remote.Equals(local))
            presence.RecordHeartbeat(request!.SessionId!);
        return Results.NoContent();
    }

    private static IPAddress? Normalize(IPAddress? address) => address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;
    private sealed record HeartbeatRequest(string? SessionId);
}
