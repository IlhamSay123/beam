// src/Beam.Server/Endpoints/ReceiveEndpoints.cs
using Beam.Server.Storage;

namespace Beam.Server.Endpoints;

public static class ReceiveEndpoints
{
    public static void MapReceiveEndpoints(this WebApplication app)
    {
        app.MapPost("/receive", async (HttpRequest request, HistoryIndex index, ItemStore store) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest("Expected multipart/form-data");
            }

            var form = await request.ReadFormAsync();
            var createdIds = new List<string>();

            foreach (var file in form.Files)
            {
                await using var stream = file.OpenReadStream();
                var savedPath = await store.SaveFileAsync("inbound", file.FileName, stream);

                var item = new HistoryItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Direction = "inbound",
                    Name = file.FileName,
                    ContentType = file.ContentType,
                    SizeBytes = file.Length,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Read = false,
                    IsText = false,
                    FilePath = savedPath
                };

                index.Add(item);
                createdIds.Add(item.Id);
            }

            var text = form["text"].ToString();
            if (!string.IsNullOrEmpty(text))
            {
                var item = new HistoryItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Direction = "inbound",
                    Name = "Text",
                    ContentType = "text/plain",
                    SizeBytes = System.Text.Encoding.UTF8.GetByteCount(text),
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Read = false,
                    IsText = true,
                    TextContent = text
                };

                index.Add(item);
                createdIds.Add(item.Id);
            }

            return Results.Ok(new { ids = createdIds });
        });
    }
}
