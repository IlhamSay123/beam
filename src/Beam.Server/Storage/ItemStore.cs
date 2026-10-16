namespace Beam.Server.Storage;

public class ItemStore
{
    private readonly string _dataDirectory;

    public ItemStore(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(InboxDir);
        Directory.CreateDirectory(OutboxDir);
    }

    public string InboxDir => Path.Combine(_dataDirectory, "Inbox");
    public string OutboxDir => Path.Combine(_dataDirectory, "Outbox");

    public async Task<string> SaveFileAsync(string direction, string fileName, Stream content,
        CancellationToken cancellationToken = default, long? maxBytes = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dir = direction == "inbound" ? InboxDir : OutboxDir;
        var safeName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        var fullPath = Path.Combine(dir, safeName);

        try
        {
            var fileStream = File.Create(fullPath);
            await using (fileStream.ConfigureAwait(false))
            {
                if (maxBytes is long limit)
                {
                    var buffer = new byte[81920];
                    long copied = 0;
                    int count;
                    while ((count = await content.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        if (count > limit - copied)
                            throw new ItemSizeLimitExceededException(limit);
                        await fileStream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        copied += count;
                    }
                }
                else
                {
                    await content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch
        {
            File.Delete(fullPath);
            throw;
        }

        return fullPath;
    }

    public Stream OpenRead(string filePath) => File.OpenRead(filePath);
}

public sealed class ItemSizeLimitExceededException(long maxBytes)
    : IOException($"File exceeds the {maxBytes}-byte limit.")
{
}
