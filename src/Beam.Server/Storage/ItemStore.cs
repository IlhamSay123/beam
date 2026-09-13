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

    public async Task<string> SaveFileAsync(string direction, string fileName, Stream content)
    {
        var dir = direction == "inbound" ? InboxDir : OutboxDir;
        var safeName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        var fullPath = Path.Combine(dir, safeName);

        await using var fileStream = File.Create(fullPath);
        await content.CopyToAsync(fileStream);

        return fullPath;
    }

    public Stream OpenRead(string filePath) => File.OpenRead(filePath);
}
