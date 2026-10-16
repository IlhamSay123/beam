// src/Beam.Server/Endpoints/ReceiveEndpoints.cs
using Beam.Server.Storage;
using System.Text;

namespace Beam.Server.Endpoints;

public static class ReceiveEndpoints
{
    public static void MapReceiveEndpoints(this WebApplication app)
    {
        app.MapPost("/receive", ReceiveAsync);
    }

    private static async Task<IResult> ReceiveAsync(HttpRequest request, HistoryIndex index, ItemStore store,
        BeamServerOptions options)
    {
        var cancellationToken = request.HttpContext.RequestAborted;
        try
        {
            if (request.ContentLength > options.MaxRequestBodyBytes)
                return RequestTooLarge(options);

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

                if (request.ContentLength > options.MaxUploadBytes)
                    return FileTooLarge(options);

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
                var savedPath = await store.SaveFileAsync("inbound", name, request.Body,
                    cancellationToken, options.MaxUploadBytes);
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

            var form = await request.ReadFormAsync(cancellationToken);
            var text = form["text"].ToString();
            if (form.Files.Count == 0 && string.IsNullOrEmpty(text))
            {
                return Results.BadRequest("No file bytes or text received. For a single file in Shortcuts, use Request Body: File with Shortcut Input.");
            }

            // Validate the entire form before any files or history entries are saved.
            if (form.Files.Any(file => file.Length > options.MaxUploadBytes))
                return FileTooLarge(options);
            var textBytes = Encoding.UTF8.GetByteCount(text);
            if (textBytes > options.MaxTextBytes)
                return Results.Text($"Text is too large. The limit is {options.MaxTextBytes} UTF-8 bytes.",
                    statusCode: StatusCodes.Status413PayloadTooLarge);

            var createdIds = new List<string>();

            foreach (var file in form.Files)
            {
                await using var stream = file.OpenReadStream();
                var savedPath = await store.SaveFileAsync("inbound", file.FileName, stream,
                    cancellationToken, options.MaxUploadBytes);

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

            if (!string.IsNullOrEmpty(text))
            {
                var item = new HistoryItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Direction = "inbound",
                    Name = "Text",
                    ContentType = "text/plain",
                    SizeBytes = textBytes,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Read = false,
                    IsText = true,
                    TextContent = text
                };

                index.Add(item);
                createdIds.Add(item.Id);
            }

            return Results.Ok(new { ids = createdIds });
        }
        catch (ItemSizeLimitExceededException)
        {
            return FileTooLarge(options);
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return RequestTooLarge(options);
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest("Invalid form data.");
        }
    }

    private static IResult FileTooLarge(BeamServerOptions options) =>
        Results.Text($"File is too large. The limit is {options.MaxUploadBytes} bytes per file.",
            statusCode: StatusCodes.Status413PayloadTooLarge);

    private static IResult RequestTooLarge(BeamServerOptions options) =>
        Results.Text($"Request is too large. The limit is {options.MaxRequestBodyBytes} bytes including form overhead.",
            statusCode: StatusCodes.Status413PayloadTooLarge);
}
