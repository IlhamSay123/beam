using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Beam.Server.Storage;
using Beam.Server.Transfers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Beam.Server.Tests;

public class SafeRetryTests
{
    private const string Key = "00112233445566778899aabbccddeeff";
    private const string Header = "X-Beam-Transfer-Id";

    [Fact]
    public async Task RepeatedFile_ReturnsOriginalReceiptAndOneExactFile()
    {
        await using var f = await Fixture.Start();
        using var first = await f.Post(Key);
        var ids = await Ids(first);
        using var repeated = await f.Post(Key.ToUpperInvariant());
        Assert.Equal(ids, await Ids(repeated));
        var item = Assert.Single(f.Index.List());
        Assert.Equal(Key, item.TransferId);
        Assert.Equal(new byte[] { 0, 255, 2, 13, 10 }, await File.ReadAllBytesAsync(item.FilePath!));
        Assert.Single(Directory.GetFiles(f.Store.InboxDir));
        using var receipt = await f.Client.GetAsync("/receive/receipts/" + Key);
        Assert.Equal(ids, await Ids(receipt));
        Assert.True(receipt.Headers.CacheControl?.NoStore);
        var body = await receipt.Content.ReadAsStringAsync();
        Assert.DoesNotContain("FilePath", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fingerprint", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sample.bin", body);
        Assert.False(item.Read);
    }

    [Fact]
    public async Task NoteRetryAndFreshIdenticalIntent_AreDistinctContracts()
    {
        await using var f = await Fixture.Start();
        using var first = await f.Note(Key, "hello\n\u00e9");
        var original = await Ids(first);
        using var repeat = await f.Note(Key, "hello\n\u00e9");
        Assert.Equal(original, await Ids(repeat));
        using var changed = await f.Note(Key, "different");
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var fresh = await f.Note(Guid.NewGuid().ToString("N"), "hello\n\u00e9");
        Assert.NotEqual(original.Single(), (await Ids(fresh)).Single());
        Assert.Equal(2, f.Index.List().Count);
        Assert.Empty(Directory.GetFiles(f.Store.InboxDir));
    }

    [Theory]
    [InlineData("other.bin", "application/octet-stream", false)]
    [InlineData("sample.bin", "image/png", false)]
    [InlineData("sample.bin", "application/octet-stream", true)]
    public async Task ReusedKeyWithDifferentPayload_IsConflictWithoutNewFile(string name, string type, bool changedBytes)
    {
        await using var f = await Fixture.Start();
        using var first = await f.Post(Key);
        first.EnsureSuccessStatusCode();
        using var changed = await f.Post(Key, name, type, changedBytes ? new byte[] { 1, 255, 2, 13, 10 } : null);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Single(f.Index.List());
        Assert.Single(Directory.GetFiles(f.Store.InboxDir));
    }

    [Fact]
    public async Task DifferentKeysWithIdenticalFile_AreIntentionalSeparateSends()
    {
        await using var f = await Fixture.Start();
        using var first = await f.Post(Key);
        using var next = await f.Post(Guid.NewGuid().ToString("N"));
        Assert.NotEqual((await Ids(first)).Single(), (await Ids(next)).Single());
        Assert.Equal(2, f.Index.List().Count);
        Assert.Equal(2, Directory.GetFiles(f.Store.InboxDir).Length);
    }

    [Fact]
    public async Task ConcurrentSameKey_AndPendingReceipt_CreateOnlyOneItem()
    {
        await using var f = await Fixture.Start();
        var coordinator = f.App.Services.GetRequiredService<ReceiveCoordinator>();
        using var held = await coordinator.AcquireAsync(Key, CancellationToken.None);
        Assert.NotNull(held);
        var first = f.Post(Key);
        var second = f.Post(Key);
        using var pending = await f.Client.GetAsync("/receive/receipts/" + Key);
        Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
        Assert.True(pending.Headers.CacheControl?.NoStore);
        Assert.Empty(f.Index.List());
        held.Dispose();
        using var a = await first;
        using var b = await second;
        Assert.Equal(await Ids(a), await Ids(b));
        Assert.Single(f.Index.List());
        Assert.Single(Directory.GetFiles(f.Store.InboxDir));
        Assert.False(coordinator.IsPending(Key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad")]
    [InlineData("00112233-4455-6677-8899-aabbccddeeff")]
    [InlineData("00112233445566778899aabbccddeefg")]
    public async Task SuppliedInvalidKey_NeverFallsBackToUnsafeUpload(string key)
    {
        await using var f = await Fixture.Start();
        using var response = await f.Post(key);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(f.Index.List());
        Assert.Empty(Directory.GetFiles(f.Store.InboxDir));
    }

    [Fact]
    public async Task DuplicateHeader_IsRejected()
    {
        await using var f = await Fixture.Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/receive") { Content = FileForm() };
        request.Headers.TryAddWithoutValidation(Header, new[] { Key, Key });
        using var response = await f.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(f.Index.List());
    }

    [Fact]
    public async Task KeyedMixedBatchAndDirectBodies_AreRejectedBeforeSaving()
    {
        await using var f = await Fixture.Start();
        foreach (var mixedText in new[] { false, true })
        {
            using var form = FileForm();
            if (mixedText) form.Add(new StringContent("note"), "text");
            else form.Add(new ByteArrayContent([2]), "files", "other.bin");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/receive") { Content = form };
            request.Headers.Add(Header, Key);
            using var response = await f.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var rawRequest = new HttpRequestMessage(HttpMethod.Post, "/receive") { Content = new ByteArrayContent([1, 2]) };
        rawRequest.Content.Headers.ContentType = new("application/octet-stream");
        rawRequest.Headers.Add(Header, Key);
        using var rawResponse = await f.Client.SendAsync(rawRequest);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, rawResponse.StatusCode);
        Assert.Empty(f.Index.List());
        Assert.Empty(Directory.GetFiles(f.Store.InboxDir));
    }

    [Fact]
    public async Task FailedHistoryWrite_HasNoFalseReceiptOrOrphanAndCanRetry()
    {
        await using var f = await Fixture.Start();
        var path = Path.Combine(f.DirectoryPath, "history.json");
        Directory.CreateDirectory(path);
        using var failed = await f.Post(Key);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Empty(f.Index.List());
        Assert.Empty(Directory.GetFiles(f.Store.InboxDir));
        using var receipt = await f.Client.GetAsync("/receive/receipts/" + Key);
        Assert.Equal(HttpStatusCode.NotFound, receipt.StatusCode);
        Directory.Delete(path);
        using var retry = await f.Post(Key);
        await Ids(retry);
        Assert.Single(f.Index.List());
        Assert.Single(Directory.GetFiles(f.Store.InboxDir));
    }

    [Fact]
    public void HistoryAdd_FailedSaveRollsBackMemory()
    {
        var root = Path.Combine(Path.GetTempPath(), "beam-history-failure-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "history.json");
        var index = new HistoryIndex(path);
        Directory.CreateDirectory(path);
        try
        {
            var error = Record.Exception(() => index.Add(new HistoryItem { Id = "not-committed", Direction = "inbound", Name = "text", ContentType = "text/plain", TimestampUtc = DateTimeOffset.UtcNow, IsText = true }));
            Assert.True(error is IOException or UnauthorizedAccessException, "The intentionally blocked history destination must fail to save.");
            Assert.Empty(index.List());
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RestartRetainsReceiptsAndLegacyHistoryWithoutResaving()
    {
        string id;
        string root;
        await using (var f = await Fixture.Start(deleteOnDispose: false))
        {
            root = f.DirectoryPath;
            using var legacy = await f.Post(null);
            await Ids(legacy);
            using var keyed = await f.Post(Key);
            id = (await Ids(keyed)).Single();
        }
        await using var restarted = await Fixture.Start(root);
        using var receipt = await restarted.Client.GetAsync("/receive/receipts/" + Key);
        Assert.Equal(id, (await Ids(receipt)).Single());
        using var replay = await restarted.Post(Key);
        Assert.Equal(id, (await Ids(replay)).Single());
        Assert.Equal(2, restarted.Index.List().Count);
        Assert.Equal(2, Directory.GetFiles(restarted.Store.InboxDir).Length);
        Assert.Single(restarted.Index.List().Where(item => item.TransferId is null));
    }

    [Fact]
    public async Task Receipt_RequiresAuth_ValidatesIdAndKeepsUnknownDistinct()
    {
        await using var f = await Fixture.Start();
        using var unknown = await f.Client.GetAsync("/receive/receipts/" + Key);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var invalid = await f.Client.GetAsync("/receive/receipts/not-a-key");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        f.Client.DefaultRequestHeaders.Remove("X-Beam-Token");
        using var unauthorized = await f.Client.GetAsync("/receive/receipts/" + Key);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var upload = await f.Post(Key);
        Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);
        Assert.Empty(f.Index.List());
    }

    [Fact]
    public async Task KeyedLimitFailure_LeavesKeyAvailableForAValidRetry()
    {
        await using var f = await Fixture.Start(limit: 8);
        using var failed = await f.Post(Key, bytes: new byte[9]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, failed.StatusCode);
        Assert.Empty(f.Index.List());
        using var retry = await f.Post(Key);
        await Ids(retry);
        Assert.Single(f.Index.List());
    }

    [Fact]
    public async Task CoordinatorBoundsCapacityAndReleasesCancelledWaiters()
    {
        var coordinator = new ReceiveCoordinator();
        var leases = new List<IDisposable>();
        try
        {
            for (var i = 0; i < 32; i++) leases.Add((await coordinator.AcquireAsync(i.ToString("x32"), CancellationToken.None))!);
            Assert.Null(await coordinator.AcquireAsync(new string('f', 32), CancellationToken.None));
            using var cancelled = new CancellationTokenSource();
            var pending = coordinator.AcquireAsync(new string('0', 32), cancelled.Token);
            Assert.False(pending.IsCompleted);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
        Assert.False(coordinator.IsPending(new string('0', 32)));
        using var next = await coordinator.AcquireAsync(new string('f', 32), CancellationToken.None);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task CoordinatorBoundsSameKeyWaiters_AndBusyRequestsDoNotSave()
    {
        await using var f = await Fixture.Start();
        var coordinator = f.App.Services.GetRequiredService<ReceiveCoordinator>();
        using var owner = await coordinator.AcquireAsync(Key, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var waiting = Enumerable.Range(0, 3).Select(_ => coordinator.AcquireAsync(Key, cancel.Token)).ToArray();
        Assert.All(waiting, task => Assert.False(task.IsCompleted));
        using var busy = await f.Post(Key);
        Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
        Assert.Empty(f.Index.List());
        Assert.Empty(Directory.GetFiles(f.Store.InboxDir));
        cancel.Cancel();
        foreach (var task in waiting) await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        owner!.Dispose();
        Assert.False(coordinator.IsPending(Key));
        using var retry = await f.Post(Key);
        await Ids(retry);
        Assert.Single(f.Index.List());
    }

    [Fact]
    public async Task PartialUploadDisconnect_ReleasesReceiptAndRetriesAsOneFile()
    {
        await using var f = await Fixture.Start();
        using var form = FileForm();
        using var content = new PausedContent(await form.ReadAsByteArrayAsync(), form.Headers.ContentType!);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/receive") { Content = content };
        request.Headers.Add(Header, Key);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sending = f.Client.SendAsync(request, cancellation.Token);
        try
        {
            await content.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitForReceipt(f.Client, HttpStatusCode.Accepted);
            Assert.Empty(f.Index.List());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sending);
            await WaitForReceipt(f.Client, HttpStatusCode.NotFound);
            Assert.Empty(f.Index.List());
            Assert.Empty(Directory.GetFiles(f.Store.InboxDir));
            using var retry = await f.Post(Key);
            await Ids(retry);
            Assert.Single(f.Index.List());
            Assert.Single(Directory.GetFiles(f.Store.InboxDir));
        }
        finally { cancellation.Cancel(); }
    }

    private static async Task WaitForReceipt(HttpClient client, HttpStatusCode status)
    {
        for (var i = 0; i < 200; i++)
        {
            using var response = await client.GetAsync("/receive/receipts/" + Key);
            if (response.StatusCode == status) return;
            await Task.Delay(10);
        }
        Assert.Fail("Receipt did not reach " + status);
    }

    private sealed class PausedContent : HttpContent
    {
        private readonly byte[] _bytes;
        public PausedContent(byte[] bytes, MediaTypeHeaderValue mediaType) { _bytes = bytes; Headers.ContentType = mediaType; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length) { length = _bytes.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellation)
        {
            await stream.WriteAsync(_bytes.AsMemory(0, _bytes.Length / 2), cancellation);
            await stream.FlushAsync(cancellation);
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellation);
        }
    }

    private static MultipartFormDataContent FileForm(string name = "sample.bin", string type = "application/octet-stream", byte[]? bytes = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes ?? [0, 255, 2, 13, 10]);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "files", name);
        return form;
    }

    private static async Task<string[]> Ids(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = json.RootElement.GetProperty("ids").EnumerateArray().Select(id => id.GetString()!).ToArray();
        Assert.Single(ids);
        Assert.False(string.IsNullOrWhiteSpace(ids[0]));
        return ids;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required string DirectoryPath { get; init; }
        public bool DeleteOnDispose { get; init; }
        public HistoryIndex Index => App.Services.GetRequiredService<HistoryIndex>();
        public ItemStore Store => App.Services.GetRequiredService<ItemStore>();
        public static async Task<Fixture> Start(string? directory = null, bool deleteOnDispose = true, long limit = 104857600)
        {
            directory ??= Path.Combine(Path.GetTempPath(), "beam-safe-retry-" + Guid.NewGuid().ToString("N"));
            var app = BeamServerHost.Build(new(directory, "test-token", 0) { MaxUploadBytes = limit }, host => host.UseUrls("http://127.0.0.1:0"));
            await app.StartAsync();
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("X-Beam-Token", "test-token");
            return new Fixture { App = app, Client = client, DirectoryPath = directory, DeleteOnDispose = deleteOnDispose };
        }
        public Task<HttpResponseMessage> Post(string? key, string name = "sample.bin", string type = "application/octet-stream", byte[]? bytes = null) => Send(key, FileForm(name, type, bytes));
        public Task<HttpResponseMessage> Note(string key, string text) => Send(key, new FormUrlEncodedContent(new Dictionary<string, string> { ["text"] = text }));
        private async Task<HttpResponseMessage> Send(string? key, HttpContent content)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/receive") { Content = content };
            if (key is not null) request.Headers.TryAddWithoutValidation(Header, key);
            return await Client.SendAsync(request);
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            if (DeleteOnDispose && Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
        }
    }
}
