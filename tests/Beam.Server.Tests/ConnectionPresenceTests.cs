using System.Net;
using System.Net.Http.Json;
using System.Text;
using Beam.Server.Connection;
using Beam.Server.Endpoints;
using Beam.Server.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace Beam.Server.Tests;

public class ConnectionPresenceTests
{
    private const string Session = "00112233445566778899aabbccddeeff";

    [Fact]
    public void Presence_IsInactiveUntilExplicitHeartbeat_AndExpiresAtBoundary()
    {
        var clock = new ControlledClock();
        var presence = new ConnectionPresence(clock);
        Assert.Equal(new ConnectionSnapshot(false, 0, null), presence.Snapshot());
        presence.RecordHeartbeat(Session);
        Assert.Equal(new ConnectionSnapshot(true, 1, clock.GetUtcNow()), presence.Snapshot());
        clock.Advance(TimeSpan.FromSeconds(14));
        Assert.True(presence.Snapshot().PhoneActive);
        clock.Advance(TimeSpan.FromSeconds(1));
        var expired = presence.Snapshot();
        Assert.False(expired.PhoneActive);
        Assert.Equal(0, expired.ActiveBrowsers);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero), expired.LastSeenUtc);
    }

    [Fact]
    public void Presence_DeduplicatesBrowserAndTracksOnlyRecentSessions()
    {
        var clock = new ControlledClock();
        var presence = new ConnectionPresence(clock);
        presence.RecordHeartbeat(Session);
        presence.RecordHeartbeat(Session);
        Assert.Equal(1, presence.Snapshot().ActiveBrowsers);
        clock.Advance(TimeSpan.FromSeconds(10));
        presence.RecordHeartbeat("ffeeddccbbaa99887766554433221100");
        Assert.Equal(2, presence.Snapshot().ActiveBrowsers);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, presence.Snapshot().ActiveBrowsers);
        Assert.True(presence.Snapshot().PhoneActive);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(presence.Snapshot().PhoneActive);
    }

    [Fact]
    public void Presence_BoundsActiveSessions_AndResetsOnRestart()
    {
        var presence = new ConnectionPresence();
        for (var i = 0; i < 100; i++) presence.RecordHeartbeat(i.ToString("x32"));
        Assert.Equal(32, presence.Snapshot().ActiveBrowsers);
        Assert.Equal(new ConnectionSnapshot(false, 0, null), new ConnectionPresence().Snapshot());
    }

    [Fact]
    public void Presence_UsesMonotonicTimeWhenWallClockChanges()
    {
        var clock = new ControlledClock();
        var presence = new ConnectionPresence(clock);
        presence.RecordHeartbeat(Session);
        clock.AdjustWallClock(TimeSpan.FromHours(-1));
        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.False(presence.Snapshot().PhoneActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-session")]
    [InlineData("00112233-4455-6677-8899-aabbccddeeff")]
    [InlineData("00112233445566778899aabbccddeefg")]
    [InlineData(" 00112233445566778899aabbccddeeff ")]
    public void Presence_RejectsInvalidSessions(string session)
    {
        var presence = new ConnectionPresence();
        Assert.Throws<ArgumentException>(() => presence.RecordHeartbeat(session));
        Assert.Null(presence.Snapshot().LastSeenUtc);
    }

    [Theory]
    [InlineData("192.168.10.208", "192.168.10.112", true)]
    [InlineData("127.0.0.1", "127.0.0.1", false)]
    [InlineData("::1", "::1", false)]
    [InlineData("::ffff:127.0.0.1", "::ffff:127.0.0.1", false)]
    [InlineData("192.168.10.112", "192.168.10.112", false)]
    [InlineData("::ffff:192.168.10.112", "192.168.10.112", false)]
    public async Task Heartbeat_RecordsRemoteBrowserButNotLocalSelfCheck(string remote, string local, bool expected)
    {
        var presence = new ConnectionPresence();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Connection.LocalIpAddress = IPAddress.Parse(local);
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"sessionId\":\"" + Session + "\"}"));
        var result = await ConnectionEndpoints.HeartbeatAsync(context, presence);
        Assert.IsType<NoContent>(result);
        Assert.Equal(expected, presence.Snapshot().PhoneActive);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task Heartbeat_RequiresExistingToken(string? token)
    {
        await using var fixture = await Fixture.Start();
        if (token is not null) fixture.Client.DefaultRequestHeaders.Add("X-Beam-Token", token);
        using var response = await fixture.Client.PostAsJsonAsync("/connection/heartbeat", new { sessionId = Session });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(fixture.Presence.Snapshot().LastSeenUtc);
    }

    [Theory]
    [InlineData("{}", "application/json", 400)]
    [InlineData("{\"sessionId\":null}", "application/json", 400)]
    [InlineData("{\"sessionId\":17}", "application/json", 400)]
    [InlineData("{\"sessionId\":\"invalid\"}", "application/json", 400)]
    [InlineData("{\"sessionId\":\" 00112233445566778899aabbccddeeff \"}", "application/json", 400)]
    [InlineData("null", "application/json", 400)]
    [InlineData("not-json", "application/json", 400)]
    [InlineData("{}", "text/plain", 415)]
    public async Task Heartbeat_RejectsMalformedPayloadWithoutActivity(string body, string mediaType, int expected)
    {
        await using var fixture = await Fixture.Start();
        fixture.Client.DefaultRequestHeaders.Add("X-Beam-Token", "test-token");
        using var response = await fixture.Client.PostAsync("/connection/heartbeat", new StringContent(body, Encoding.UTF8, mediaType));
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.False(fixture.Presence.Snapshot().PhoneActive);
    }

    [Fact]
    public async Task Heartbeat_AcceptsValidRequestWithoutChangingHistoryOrCountingLocalBrowser()
    {
        await using var fixture = await Fixture.Start();
        fixture.Client.DefaultRequestHeaders.Add("X-Beam-Token", "test-token");
        var index = fixture.App.Services.GetRequiredService<HistoryIndex>();
        var item = OutboxQueue.QueueText(index, "still unread");
        using var response = await fixture.Client.PostAsJsonAsync("/connection/heartbeat", new { sessionId = Session });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.False(fixture.Presence.Snapshot().PhoneActive);
        Assert.Single(index.List());
        Assert.False(index.Get(item.Id)!.Read);
        using var outbox = await fixture.Client.GetAsync("/outbox");
        outbox.EnsureSuccessStatusCode();
        Assert.Null(fixture.Presence.Snapshot().LastSeenUtc);
    }

    [Fact]
    public async Task Heartbeat_RejectsOversizedBody()
    {
        await using var fixture = await Fixture.Start();
        fixture.Client.DefaultRequestHeaders.Add("X-Beam-Token", "test-token");
        using var response = await fixture.Client.PostAsync("/connection/heartbeat",
            new StringContent("{\"sessionId\":\"" + Session + "\",\"padding\":\"" + new string('a', 1100) + "\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Null(fixture.Presence.Snapshot().LastSeenUtc);
    }

    [Fact]
    public async Task Heartbeat_BoundsBodyEvenWithoutContentLength()
    {
        var presence = new ConnectionPresence();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.10.208");
        context.Connection.LocalIpAddress = IPAddress.Parse("192.168.10.112");
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', 1100)));
        var result = await ConnectionEndpoints.HeartbeatAsync(context, presence);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
        Assert.Null(presence.Snapshot().LastSeenUtc);
    }

    [Fact]
    public void Presence_ForegroundHeartbeatReactivatesExpiredBrowser()
    {
        var clock = new ControlledClock();
        var presence = new ConnectionPresence(clock);
        presence.RecordHeartbeat(Session);
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.False(presence.Snapshot().PhoneActive);
        presence.RecordHeartbeat(Session);
        Assert.Equal(new ConnectionSnapshot(true, 1, clock.GetUtcNow()), presence.Snapshot());
    }

    private sealed class ControlledClock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => _utc;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan value) { _utc += value; _ticks += value.Ticks; }
        public void AdjustWallClock(TimeSpan value) => _utc += value;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required Microsoft.AspNetCore.Builder.WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public ConnectionPresence Presence => App.Services.GetRequiredService<ConnectionPresence>();
        public required string DirectoryPath { get; init; }

        public static async Task<Fixture> Start()
        {
            var directory = Path.Combine(Path.GetTempPath(), "beam-presence-" + Guid.NewGuid().ToString("N"));
            var app = BeamServerHost.Build(new(directory, "test-token", 0), host => host.UseUrls("http://127.0.0.1:0"));
            await app.StartAsync();
            return new Fixture { App = app, Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, DirectoryPath = directory };
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
        }
    }
}
