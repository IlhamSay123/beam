namespace Beam.Server.Storage;

public static class OutboxQueue
{
    public static HistoryItem QueueFile(HistoryIndex index, ItemStore store, string sourceFilePath)
    {
        using var sourceStream = File.OpenRead(sourceFilePath);
        var savedPath = store
            .SaveFileAsync("outbound", Path.GetFileName(sourceFilePath), sourceStream)
            .GetAwaiter()
            .GetResult();

        var item = new HistoryItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Direction = "outbound",
            Name = Path.GetFileName(sourceFilePath),
            ContentType = "application/octet-stream",
            SizeBytes = new FileInfo(savedPath).Length,
            TimestampUtc = DateTimeOffset.UtcNow,
            Read = false,
            IsText = false,
            FilePath = savedPath
        };

        return index.Add(item);
    }

    public static HistoryItem QueueText(HistoryIndex index, string text)
    {
        var item = new HistoryItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Direction = "outbound",
            Name = "Text",
            ContentType = "text/plain",
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(text),
            TimestampUtc = DateTimeOffset.UtcNow,
            Read = false,
            IsText = true,
            TextContent = text
        };

        return index.Add(item);
    }
}
