using System.Text;
using Beam.Server.Storage;

namespace Beam.Server.Tests;

public class ItemStoreTests
{
    private static string TempDataDir() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    [Fact]
    public void Constructor_CreatesInboxAndOutboxDirectories()
    {
        var dir = TempDataDir();
        var store = new ItemStore(dir);

        Assert.True(Directory.Exists(store.InboxDir));
        Assert.True(Directory.Exists(store.OutboxDir));
    }

    [Fact]
    public async Task SaveFileAsync_Inbound_WritesUnderInboxDir()
    {
        var store = new ItemStore(TempDataDir());
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("hello"));

        var savedPath = await store.SaveFileAsync("inbound", "note.txt", content);

        Assert.StartsWith(store.InboxDir, savedPath);
        Assert.EndsWith("note.txt", savedPath);
        Assert.Equal("hello", await File.ReadAllTextAsync(savedPath));
    }

    [Fact]
    public async Task SaveFileAsync_Outbound_WritesUnderOutboxDir()
    {
        var store = new ItemStore(TempDataDir());
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("hi"));

        var savedPath = await store.SaveFileAsync("outbound", "clip.mp4", content);

        Assert.StartsWith(store.OutboxDir, savedPath);
    }

    [Fact]
    public async Task SaveFileAsync_TwoFilesWithSameName_DoNotCollide()
    {
        var store = new ItemStore(TempDataDir());
        using var contentA = new MemoryStream(Encoding.UTF8.GetBytes("a"));
        using var contentB = new MemoryStream(Encoding.UTF8.GetBytes("b"));

        var pathA = await store.SaveFileAsync("inbound", "same.txt", contentA);
        var pathB = await store.SaveFileAsync("inbound", "same.txt", contentB);

        Assert.NotEqual(pathA, pathB);
        Assert.Equal("a", await File.ReadAllTextAsync(pathA));
        Assert.Equal("b", await File.ReadAllTextAsync(pathB));
    }
}
