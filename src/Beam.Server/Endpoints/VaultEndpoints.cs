using System.Globalization;
using System.Net.Http.Headers;
using Beam.Server.Storage;

namespace Beam.Server.Endpoints;

public static class VaultEndpoints
{
    public static void MapVaultEndpoints(this WebApplication app)
    {
        app.MapGet("/vault", ListVault);
        app.MapGet("/vault/{id}/content", (string id, HistoryIndex index, ItemStore store, HttpContext context) =>
            GetFile(id, index, store, context, preview: false));
        app.MapGet("/vault/{id}/preview", (string id, HistoryIndex index, ItemStore store, HttpContext context) =>
            GetFile(id, index, store, context, preview: true));
    }

    private static IResult ListVault(HttpRequest request, HistoryIndex index)
    {
        var query = request.Query;
        foreach (var parameter in new[] { "search", "direction", "kind", "offset", "limit" })
        {
            if (query[parameter].Count > 1)
                return Results.BadRequest($"Provide only one {parameter} value.");
        }

        var direction = query.TryGetValue("direction", out var directionValue) ? directionValue.ToString() : null;
        if (direction is not null and not ("inbound" or "outbound"))
            return Results.BadRequest("direction must be inbound or outbound.");

        var kind = query.TryGetValue("kind", out var kindValue) ? kindValue.ToString() : null;
        if (kind is not null and not ("image" or "file" or "text"))
            return Results.BadRequest("kind must be image, file, or text.");

        var offset = 0;
        if (query.TryGetValue("offset", out var offsetValue) &&
            !int.TryParse(offsetValue.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out offset))
            return Results.BadRequest("offset must be a whole number from 0 to 2147483647.");

        var limit = 40;
        if (query.TryGetValue("limit", out var limitValue) &&
            (!int.TryParse(limitValue.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1))
            return Results.BadRequest("limit must be a positive whole number; at most 100 items are returned.");
        limit = Math.Min(limit, 100);

        var search = query["search"].ToString().Trim();
        IEnumerable<HistoryItem> filtered = index.List();
        if (direction is not null)
            filtered = filtered.Where(item => item.Direction == direction);
        if (kind is not null)
            filtered = filtered.Where(item => GetKind(item) == kind);
        if (search.Length > 0)
        {
            filtered = filtered.Where(item => item.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (item.IsText && item.TextContent?.Contains(search, StringComparison.OrdinalIgnoreCase) == true));
        }

        var matches = filtered.ToList();
        var items = matches.Skip(offset).Take(limit).Select(item => new
        {
            id = item.Id,
            direction = item.Direction,
            name = item.Name,
            contentType = GetContentType(item),
            sizeBytes = item.SizeBytes,
            timestampUtc = item.TimestampUtc,
            read = item.Read,
            isText = item.IsText,
            textContent = item.IsText ? item.TextContent : null,
            available = item.IsText || File.Exists(item.FilePath)
        }).ToArray();

        return Results.Ok(new { items, total = matches.Count, hasMore = (long)offset + items.Length < matches.Count });
    }

    private static string GetKind(HistoryItem item) =>
        item.IsText ? "text" : GetContentType(item).StartsWith("image/", StringComparison.Ordinal) ? "image" : "file";

    private static string GetContentType(HistoryItem item)
    {
        if (!MediaTypeHeaderValue.TryParse(item.ContentType, out var parsed))
            return "application/octet-stream";

        var type = parsed.MediaType?.ToLowerInvariant() ?? "application/octet-stream";
        if (type != "application/octet-stream") return type;

        // PC-queued files use a generic MIME type. Infer only known raster formats.
        return Path.GetExtension(item.Name).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".avif" => "image/avif",
            _ => type
        };
    }

    private static bool CanPreview(HistoryItem item, string contentType)
    {
        // A raster MIME claim must not turn an active document into an inline preview.
        if (Path.GetExtension(item.Name).ToLowerInvariant() is ".svg" or ".svgz" or ".html" or ".htm" or ".xhtml" or ".xml")
            return false;

        return contentType is "image/jpeg" or "image/png" or "image/gif" or "image/webp" or "image/bmp" or "image/avif";
    }

    private static IResult GetFile(string id, HistoryIndex index, ItemStore store, HttpContext context, bool preview)
    {
        var item = index.Get(id);
        if (item is null || item.IsText || item.FilePath is null)
            return Results.NotFound();

        var contentType = GetContentType(item);
        if (preview && !CanPreview(item, contentType))
            return Results.NotFound();

        Stream stream;
        try
        {
            stream = store.OpenRead(item.FilePath);
        }
        catch (IOException)
        {
            return Results.NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Results.NotFound();
        }

        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (preview) context.Response.Headers.ContentDisposition = "inline";
        return Results.Stream(stream, contentType, preview ? null : item.Name);
    }
}
