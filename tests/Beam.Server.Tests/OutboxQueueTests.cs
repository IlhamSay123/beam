using Beam.Server.Storage;

namespace Beam.Server.Tests;

public class OutboxQueueTests
{
    [Fact]
    public async Task QueueFile_OnSingleThreadedContext_DoesNotDeadlock()
    {
        var directory = TempDataDir();
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "screenshot.png");
        await File.WriteAllBytesAsync(source, new byte[1024 * 1024]);
        var index = new HistoryIndex(Path.Combine(directory, "history.json"));
        var store = new ItemStore(directory);
        using var release = new ManualResetEventSlim();
        var completed = new TaskCompletionSource<HistoryItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new PausedContext(release));
            try { completed.SetResult(OutboxQueue.QueueFile(index, store, source)); }
            catch (Exception error) { completed.SetException(error); }
        }) { IsBackground = true };
        thread.Start();
        try
        {
            var winner = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(completed.Task, winner);
            Assert.Equal(1024 * 1024, (await completed.Task).SizeBytes);
        }
        finally
        {
            release.Set();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class PausedContext(ManualResetEventSlim release) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                release.Wait();
                callback(state);
            });
        }
    }

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
