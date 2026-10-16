using System.Text;
using Beam.Server.Storage;

namespace Beam.Server.Tests;

public class ItemStoreTests
{
    [Fact]
    public async Task SaveFileAsync_AlreadyCancelled_LeavesNoOutput()
    {
        var store = new ItemStore(TempDataDir());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveFileAsync("inbound", "cancelled.bin", content, cancellation.Token));

        Assert.Empty(Directory.GetFiles(store.InboxDir));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFileAsync_CancelledDuringRead_RemovesPartialOutput(bool bounded)
    {
        var store = new ItemStore(TempDataDir());
        using var cancellation = new CancellationTokenSource();
        using var content = new CancellingReadStream(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveFileAsync("inbound", "cancelled.bin", content, cancellation.Token, maxBytes: bounded ? 8 : null));

        Assert.Empty(Directory.GetFiles(store.InboxDir));
    }

    private sealed class CancellingReadStream(CancellationTokenSource cancellation) : MemoryStream(new byte[16])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == 0) return base.ReadAsync(buffer[..1], cancellationToken);
            cancellation.Cancel();
            return base.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (Position == 0) return base.ReadAsync(buffer, offset, 1, cancellationToken);
            cancellation.Cancel();
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SaveFileAsync_CopyFailure_RemovesPartialOutput(bool cancellation, bool bounded)
    {
        var store = new ItemStore(TempDataDir());
        using var content = new FailingReadStream(cancellation);

        if (cancellation)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.SaveFileAsync("inbound", "partial.bin", content, maxBytes: bounded ? 8 : null));
        else
            await Assert.ThrowsAsync<IOException>(() =>
                store.SaveFileAsync("inbound", "partial.bin", content, maxBytes: bounded ? 8 : null));

        Assert.Empty(Directory.GetFiles(store.InboxDir));
    }

    [Fact]
    public async Task SaveFileAsync_SizeLimitAfterPartialWrite_RemovesOnlyFailedOutput()
    {
        var store = new ItemStore(TempDataDir());
        using var existingContent = new MemoryStream(new byte[] { 42 });
        var existingPath = await store.SaveFileAsync("inbound", "keep.bin", existingContent);
        using var content = new MemoryStream(new byte[81921]);

        await Assert.ThrowsAsync<ItemSizeLimitExceededException>(() =>
            store.SaveFileAsync("inbound", "too-large.bin", content, maxBytes: 81920));

        Assert.Equal(existingPath, Assert.Single(Directory.GetFiles(store.InboxDir)));
        Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(existingPath));
    }

    private sealed class FailingReadStream(bool cancellation) : Stream
    {
        private bool _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read)
            {
                if (cancellation) throw new OperationCanceledException();
                throw new IOException("Source read failed.");
            }
            _read = true;
            buffer[offset] = 42;
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var bytes = new byte[1];
            var count = Read(bytes, 0, 1);
            bytes.AsMemory(0, count).CopyTo(buffer);
            return ValueTask.FromResult(count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

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
