// src/Beam.Server/Endpoints/OutboxEndpoints.cs
using Beam.Server.Storage;

namespace Beam.Server.Endpoints;

public static class OutboxEndpoints
{
    public static void MapOutboxEndpoints(this WebApplication app)
    {
        app.MapGet("/outbox", (HistoryIndex index) =>
        {
            var unread = index.ListUnread("outbound").Select(i => new
            {
                id = i.Id,
                name = i.Name,
                contentType = i.ContentType,
                sizeBytes = i.SizeBytes,
                timestampUtc = i.TimestampUtc,
                isText = i.IsText,
                textContent = i.IsText ? i.TextContent : null
            });

            return Results.Ok(unread);
        });

        app.MapGet("/outbox/{id}/content", (string id, HistoryIndex index, ItemStore store) =>
        {
            var item = index.Get(id);
            if (item is null || item.IsText || item.FilePath is null)
            {
                return Results.NotFound();
            }

            var stream = store.OpenRead(item.FilePath);
            return Results.Stream(stream, item.ContentType, item.Name);
        });

        app.MapPost("/outbox/{id}/ack", (string id, HistoryIndex index) =>
        {
            return index.MarkRead(id) ? Results.Ok() : Results.NotFound();
        });
    }
}
