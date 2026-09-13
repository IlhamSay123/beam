using Beam.Server.Storage;

namespace Beam.Server.Tests;

public class OutboxQueueTests
{
    private static string TempDataDir() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    [Fact]
    public async Task QueueFile_CopiesFileAndAddsUnreadOutboundHistoryItem()
    {
        var sourceDir = TempDataDir();
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "photo.jpg");
        await File.WriteAllBytesAsync(sourceFile, new byte[] { 1, 2, 3 });

        var index = new HistoryIndex(Path.Combine(TempDataDir(), "history.json"));
        var store = new ItemStore(TempDataDir());

        var item = OutboxQueue.QueueFile(index, store, sourceFile);

        Assert.Equal("outbound", item.Direction);
        Assert.False(item.Read);
        Assert.False(item.IsText);
        Assert.Equal("photo.jpg", item.Name);
        Assert.StartsWith(store.OutboxDir, item.FilePath);
        Assert.True(File.Exists(item.FilePath));
        Assert.Single(index.ListUnread("outbound"));
    }

    [Fact]
    public void QueueText_AddsUnreadOutboundTextHistoryItem()
    {
        var index = new HistoryIndex(Path.Combine(TempDataDir(), "history.json"));

        var item = OutboxQueue.QueueText(index, "call mom back");

        Assert.Equal("outbound", item.Direction);
        Assert.True(item.IsText);
        Assert.Equal("call mom back", item.TextContent);
        Assert.Null(item.FilePath);
        Assert.Single(index.ListUnread("outbound"));
    }
}
