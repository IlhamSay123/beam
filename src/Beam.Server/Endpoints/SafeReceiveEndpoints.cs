using System.Security.Cryptography;
using System.Text;
using Beam.Server.Storage;
using Beam.Server.Transfers;

namespace Beam.Server.Endpoints;

public static class SafeReceiveEndpoints
{
    public const string TransferHeader = "X-Beam-Transfer-Id";

    public static IResult GetReceipt(string transferId, HistoryIndex index, ReceiveCoordinator coordinator, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!TryNormalizeId(transferId, out var key)) return Results.BadRequest("Invalid transfer ID.");
        var item = index.GetReceivedTransfer(key);
        if (item is not null)
            return item.TransferFingerprint is null ? Results.Conflict("The saved transfer cannot be verified.") : Receipt(item);
        return coordinator.IsPending(key) ? Results.StatusCode(StatusCodes.Status202Accepted) : Results.NotFound();
    }

    public static async Task<IResult> ReceiveAsync(HttpRequest request, HistoryIndex index, ItemStore store,
        BeamServerOptions options, ReceiveCoordinator coordinator)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        var values = request.Headers[TransferHeader];
        if (values.Count != 1 || !TryNormalizeId(values[0], out var key)) return Results.BadRequest("Invalid transfer ID.");
        if (request.ContentLength > options.MaxRequestBodyBytes) return TooLarge("Request", options.MaxRequestBodyBytes);
        if (!request.HasFormContentType) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        var cancellation = request.HttpContext.RequestAborted;
        string? createdPath = null;
        try
        {
            using var lease = await coordinator.AcquireAsync(key, cancellation);
            if (lease is null) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            var form = await request.ReadFormAsync(cancellation);
            var text = form["text"].ToString();
            if (form["text"].Count > 1 || form.Files.Count > 1 ||
                (form.Files.Count == 1 && text.Length > 0) || (form.Files.Count == 0 && text.Length == 0))
                return Results.BadRequest("A safe-retry upload must contain one file or one note.");
            var file = form.Files.SingleOrDefault();
            var textBytes = Encoding.UTF8.GetByteCount(text);
            if (file is not null && file.Length > options.MaxUploadBytes) return TooLarge("File", options.MaxUploadBytes);
            if (file is null && textBytes > options.MaxTextBytes) return TooLarge("Text", options.MaxTextBytes);

            var fingerprint = await FingerprintAsync(file, text, cancellation);
            var existing = index.GetReceivedTransfer(key);
            if (existing is not null)
                return string.Equals(existing.TransferFingerprint, fingerprint, StringComparison.Ordinal)
                    ? Receipt(existing) : Results.Conflict("This transfer ID was already used for different content. Start a new transfer.");

            if (file is not null)
            {
                await using var stream = file.OpenReadStream();
                createdPath = await store.SaveFileAsync("inbound", file.FileName, stream, cancellation, options.MaxUploadBytes);
            }
            var item = new HistoryItem
            {
                Id = Guid.NewGuid().ToString("N"), Direction = "inbound", Name = file?.FileName ?? "Text",
                ContentType = file?.ContentType ?? "text/plain", SizeBytes = file?.Length ?? textBytes,
                TimestampUtc = DateTimeOffset.UtcNow, Read = false, IsText = file is null,
                TextContent = file is null ? text : null, FilePath = createdPath,
                TransferId = key, TransferFingerprint = fingerprint
            };
            // The receipt is committed with the history item before a response can claim success.
            index.Add(item);
            createdPath = null;
            return Receipt(item);
        }
        catch (ItemSizeLimitExceededException) { return TooLarge("File", options.MaxUploadBytes); }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        { return TooLarge("Request", options.MaxRequestBodyBytes); }
        catch (InvalidDataException) { return Results.BadRequest("Invalid form data."); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return Results.StatusCode(499); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Results.Text("The PC could not commit this transfer. Retry safely when storage is available.", statusCode: StatusCodes.Status503ServiceUnavailable); }
        finally
        {
            if (createdPath is not null)
            {
                try { File.Delete(createdPath); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static IResult Receipt(HistoryItem item) => Results.Ok(new { ids = new[] { item.Id } });
    private static IResult TooLarge(string kind, long bytes) =>
        Results.Text($"{kind} is too large. The limit is {bytes} bytes.", statusCode: StatusCodes.Status413PayloadTooLarge);

    private static bool TryNormalizeId(string? value, out string normalized)
    {
        normalized = "";
        if (value?.Length != 32 || !Guid.TryParseExact(value, "N", out var id)) return false;
        normalized = id.ToString("N");
        return true;
    }

    private static async Task<string> FingerprintAsync(IFormFile? file, string text, CancellationToken cancellation)
    {
        byte[] contentHash;
        if (file is null) contentHash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        else
        {
            await using var stream = file.OpenReadStream();
            contentHash = await SHA256.HashDataAsync(stream, cancellation);
        }
        // BinaryWriter's length-prefixed UTF-8 strings keep metadata boundaries unambiguous.
        using var metadata = new MemoryStream();
        using (var writer = new BinaryWriter(metadata, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("beam-receive-v1");
            writer.Write(file is null);
            writer.Write(file?.FileName ?? "Text");
            writer.Write(file?.ContentType ?? "text/plain");
            writer.Write(file?.Length ?? Encoding.UTF8.GetByteCount(text));
            writer.Write(contentHash);
        }
        return Convert.ToHexString(SHA256.HashData(metadata.ToArray()));
    }
}
