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
                if (string.IsNullOrWhiteSpace(request.ContentType))
                {
                    return Results.BadRequest("Expected a form or a file body with Content-Type.");
                }

                if (request.ContentLength == 0)
                {
                    return Results.BadRequest("No file bytes received.");
                }

                // Shortcuts' Request Body: File sends the bytes directly, not multipart.
                var contentType = request.ContentType.Split(';')[0].Trim().ToLowerInvariant();
                var extension = contentType switch
                {
                    "image/jpeg" => ".jpg",
                    "image/png" => ".png",
                    "image/heic" => ".heic",
                    "image/heif" => ".heif",
                    "image/gif" => ".gif",
                    "image/webp" => ".webp",
                    "application/pdf" => ".pdf",
                    "text/plain" => ".txt",
                    "video/mp4" => ".mp4",
                    "video/quicktime" => ".mov",
                    "audio/mpeg" => ".mp3",
                    _ => ".bin"
                };
                var name = $"Beam-{Guid.NewGuid():N}{extension}";
                var savedPath = await store.SaveFileAsync("inbound", name, request.Body);
                var size = new FileInfo(savedPath).Length;
                if (size == 0)
                {
                    File.Delete(savedPath);
                    return Results.BadRequest("No file bytes received.");
                }

                var item = new HistoryItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Direction = "inbound",
                    Name = name,
                    ContentType = contentType,
                    SizeBytes = size,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Read = false,
                    IsText = false,
                    FilePath = savedPath
                };
                index.Add(item);
                return Results.Ok(new { ids = new[] { item.Id } });
            }

            var form = await request.ReadFormAsync();
            if (form.Files.Count == 0 && string.IsNullOrEmpty(form["text"].ToString()))
            {
                return Results.BadRequest("No file bytes or text received. For a single file in Shortcuts, use Request Body: File with Shortcut Input.");
            }
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
