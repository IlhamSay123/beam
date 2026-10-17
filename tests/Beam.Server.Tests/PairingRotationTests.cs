using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Beam.Server.Connection;
using Beam.Server.Security;
using Beam.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Beam.Server.Tests;

public class PairingRotationTests
{
    private const string TransferId = "11223344556677889900aabbccddeeff";

    private static Task<string> Rotate(WebApplication app) =>
        app.Services.GetRequiredService<PairingTokenStore>().RotateAsync();

    [Fact]
    public async Task DuplicateTokens_EvenWhenFirstMatches_AreRejected()
    {
        await using var f = await Fixture.Start();
        foreach (var second in new[] { f.Token, "another-key" })
        {
            using var socket = new TcpClient();
            var address = f.Client.BaseAddress!;
            await socket.ConnectAsync(address.Host, address.Port);
            await using var stream = socket.GetStream();
            var raw = $"GET /capabilities HTTP/1.1\r\nHost: {address.Host}:{address.Port}\r\nX-Beam-Token: {f.Token}\r\nX-Beam-Token: {second}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(raw));
            using var reader = new StreamReader(stream);
            var response = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.StartsWith("HTTP/1.1 401", response);
            Assert.DoesNotContain(f.Token, response);
        }
    }

    [Fact]
    public async Task Rotate_RevokesOldKeyAndKeepsSavedDataAndReceipts()
    {
        await using var f = await Fixture.Start();
        var outbound = OutboxQueue.QueueText(f.Index, "keep this queued note");
        var source = Path.Combine(f.Root, "source.bin");
        await File.WriteAllBytesAsync(source, [1, 7, 0, 255]);
        var queuedFile = OutboxQueue.QueueFile(f.Index, f.Store, source);
        using var upload = await f.Send(HttpMethod.Post, "/receive", f.Token,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["text"] = "saved receipt note" }), TransferId);
        upload.EnsureSuccessStatusCode();
        var historyBefore = await File.ReadAllBytesAsync(Path.Combine(f.Root, "history.json"));
        f.Presence.RecordHeartbeat("00112233445566778899aabbccddeeff");
        Assert.True(f.Presence.Snapshot().PhoneActive);
        using (var resetApi = await f.Send(HttpMethod.Post, "/pairing/reset", f.Token))
            Assert.Equal(HttpStatusCode.NotFound, resetApi.StatusCode);
        var replacement = await Rotate(f.App);
        Assert.NotEqual(f.Token, replacement);
        Assert.Matches("^[A-Za-z0-9_-]{32}$", replacement);
        Assert.Equal(replacement, PairingToken.LoadOrCreate(f.Root));
        Assert.Equal(historyBefore, await File.ReadAllBytesAsync(Path.Combine(f.Root, "history.json")));
        Assert.Equal(new byte[] { 1, 7, 0, 255 }, await File.ReadAllBytesAsync(queuedFile.FilePath!));
        Assert.False(f.Index.Get(outbound.Id)!.Read);
        Assert.Equal(new ConnectionSnapshot(false, 0, null), f.Presence.Snapshot());
        foreach (var endpoint in new[]
        {
            (HttpMethod.Get, "/capabilities"), (HttpMethod.Get, "/outbox"),
            (HttpMethod.Get, "/vault"), (HttpMethod.Get, "/vault/" + queuedFile.Id + "/content"),
            (HttpMethod.Get, "/outbox/" + queuedFile.Id + "/content"),
            (HttpMethod.Post, "/outbox/" + outbound.Id + "/ack"),
            (HttpMethod.Get, "/receive/receipts/" + TransferId),
            (HttpMethod.Post, "/receive"), (HttpMethod.Post, "/connection/heartbeat")
        })
        {
            using var revoked = await f.Send(endpoint.Item1, endpoint.Item2, f.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            var body = await revoked.Content.ReadAsStringAsync();
            Assert.DoesNotContain(f.Token, body);
            Assert.DoesNotContain(replacement, body);
        }
        foreach (var endpoint in new[] { "/capabilities", "/outbox", "/vault", "/receive/receipts/" + TransferId })
        {
            using var current = await f.Send(HttpMethod.Get, endpoint, replacement);
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        }
        using var replay = await f.Send(HttpMethod.Post, "/receive", replacement,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["text"] = "saved receipt note" }), TransferId);
        replay.EnsureSuccessStatusCode();
        Assert.Equal(3, f.Index.List().Count);
        Assert.Equal(historyBefore, await File.ReadAllBytesAsync(Path.Combine(f.Root, "history.json")));
        using var publicPage = await f.Client.GetAsync("/app/");
        Assert.Equal(HttpStatusCode.OK, publicPage.StatusCode);
        Assert.DoesNotContain(replacement, await publicPage.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rotation_PersistsAcrossCleanRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "beam-rotation-restart-" + Guid.NewGuid().ToString("N"));
        string original, replacement;
        try
        {
            await using (var f = await Fixture.Start(root, delete: false))
            {
                original = f.Token;
                replacement = await Rotate(f.App);
            }
            await using var restarted = await Fixture.Start(root, delete: false);
            Assert.Equal(replacement, restarted.Token);
            using var revoked = await restarted.Send(HttpMethod.Get, "/vault", original);
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            using var current = await restarted.Send(HttpMethod.Get, "/vault", replacement);
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailedTokenReplacement_KeepsPreviousAccessAndCleansTemporaryFile()
    {
        await using var f = await Fixture.Start();
        var tokenPath = Path.Combine(f.Root, "token.txt");
        var backup = tokenPath + ".fixture-backup";
        File.Move(tokenPath, backup);
        Directory.CreateDirectory(tokenPath); // A directory cannot be atomically replaced with a token file.
        try
        {
            var error = await Record.ExceptionAsync(() => Rotate(f.App));
            Assert.True(error is IOException or UnauthorizedAccessException);
            using var current = await f.Send(HttpMethod.Get, "/capabilities", f.Token);
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
            Assert.DoesNotContain(Directory.GetFiles(f.Root), path => path.EndsWith(".tmp", StringComparison.Ordinal));
        }
        finally { Directory.Delete(tokenPath); File.Move(backup, tokenPath); }
        Assert.Equal(f.Token, PairingToken.LoadOrCreate(f.Root));
        var recovered = await Rotate(f.App);
        Assert.NotEqual(f.Token, recovered);
    }

    [Fact]
    public async Task Rotation_CancelsAndDrainsOldRequestsBeforeReportingSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = await Fixture.Start(configure: app => app.MapGet("/held", async context =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
            finally { cancelled.TrySetResult(); await release.Task; }
        }));
        var request = f.Send(HttpMethod.Get, "/held", f.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var rotating = Rotate(f.App);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(rotating.IsCompleted, "Reset must wait for old authenticated handlers to leave.");
            using var blocked = await f.Send(HttpMethod.Get, "/capabilities", f.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);
            release.TrySetResult();
            var replacement = await rotating.WaitAsync(TimeSpan.FromSeconds(3));
            using var current = await f.Send(HttpMethod.Get, "/capabilities", replacement);
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
            var error = await Record.ExceptionAsync(async () => { using var response = await request; });
            Assert.True(error is HttpRequestException or OperationCanceledException);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ConcurrentRotations_PersistOnlyTheCurrentUniqueKey()
    {
        await using var f = await Fixture.Start();
        var replacements = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Rotate(f.App)));
        Assert.Equal(5, replacements.Distinct().Count());
        var currentKey = PairingToken.LoadOrCreate(f.Root);
        Assert.Contains(currentKey, replacements);
        foreach (var key in replacements.Append(f.Token))
        {
            using var response = await f.Send(HttpMethod.Get, "/capabilities", key);
            Assert.Equal(key == currentKey ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required string Root { get; init; }
        public required string Token { get; init; }
        public bool Delete { get; init; }
        public HistoryIndex Index => App.Services.GetRequiredService<HistoryIndex>();
        public ItemStore Store => App.Services.GetRequiredService<ItemStore>();
        public ConnectionPresence Presence => App.Services.GetRequiredService<ConnectionPresence>();
        public static async Task<Fixture> Start(string? root = null, bool delete = true, Action<WebApplication>? configure = null)
        {
            root ??= Path.Combine(Path.GetTempPath(), "beam-pairing-" + Guid.NewGuid().ToString("N"));
            var token = PairingToken.LoadOrCreate(root);
            var app = BeamServerHost.Build(new(root, token, 0), host => host.UseUrls("http://127.0.0.1:0"));
            configure?.Invoke(app);
            await app.StartAsync();
            return new Fixture { App = app, Client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) }, Root = root, Token = token, Delete = delete };
        }
        public Task<HttpResponseMessage> Send(HttpMethod method, string path, string token, HttpContent? content = null, string? transferId = null)
        {
            var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Add("X-Beam-Token", token);
            if (transferId is not null) request.Headers.Add("X-Beam-Transfer-Id", transferId);
            return SendAndDispose(request);
        }
        private async Task<HttpResponseMessage> SendAndDispose(HttpRequestMessage request)
        {
            using (request) return await Client.SendAsync(request);
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            if (Delete && Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
