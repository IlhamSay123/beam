using Beam.Server.Storage;

namespace Beam.Server.Tests;

public class HistoryIndexTests
{
    private static HistoryItem MakeItem(string id, string direction, bool read = false, DateTimeOffset? ts = null) =>
        new()
        {
            Id = id,
            Direction = direction,
            Name = $"item-{id}",
            ContentType = "text/plain",
            SizeBytes = 10,
            TimestampUtc = ts ?? DateTimeOffset.UtcNow,
            Read = read,
            IsText = true,
            TextContent = "hello"
        };

    private static string TempIndexPath() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    [Fact]
    public void Add_ThenList_ReturnsItem()
    {
        var index = new HistoryIndex(TempIndexPath());
        index.Add(MakeItem("1", "inbound"));

        var all = index.List();
        Assert.Single(all);
        Assert.Equal("1", all[0].Id);
    }

    [Fact]
    public void Add_Persists_AcrossNewInstance()
    {
        var path = TempIndexPath();
        new HistoryIndex(path).Add(MakeItem("1", "inbound"));

        var reloaded = new HistoryIndex(path);
        Assert.Single(reloaded.List());
    }

    [Fact]
    public void ListUnread_FiltersByDirectionAndReadState()
    {
        var index = new HistoryIndex(TempIndexPath());
        index.Add(MakeItem("1", "outbound", read: false));
        index.Add(MakeItem("2", "outbound", read: true));
        index.Add(MakeItem("3", "inbound", read: false));

        var unread = index.ListUnread("outbound");
        Assert.Single(unread);
        Assert.Equal("1", unread[0].Id);
    }

    [Fact]
    public void MarkRead_SetsReadTrue_AndPersists()
    {
        var path = TempIndexPath();
        var index = new HistoryIndex(path);
        index.Add(MakeItem("1", "outbound"));

        var result = index.MarkRead("1");

        Assert.True(result);
        Assert.True(new HistoryIndex(path).Get("1")!.Read);
    }

    [Fact]
    public void MarkRead_UnknownId_ReturnsFalse()
    {
        var index = new HistoryIndex(TempIndexPath());
        Assert.False(index.MarkRead("missing"));
    }

    [Fact]
    public void Prune_RemovesItemsBeyondMaxItemsOrMaxAge()
    {
        var index = new HistoryIndex(TempIndexPath());
        index.Add(MakeItem("old", "inbound", ts: DateTimeOffset.UtcNow.AddDays(-10)));
        index.Add(MakeItem("new", "inbound", ts: DateTimeOffset.UtcNow));

        index.Prune(maxItems: 50, maxAge: TimeSpan.FromDays(7));

        var remaining = index.List();
        Assert.Single(remaining);
        Assert.Equal("new", remaining[0].Id);
    }
}
