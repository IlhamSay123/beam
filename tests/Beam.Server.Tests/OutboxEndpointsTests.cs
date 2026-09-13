// tests/Beam.Server.Tests/OutboxEndpointsTests.cs
using System.Text.Json;
using Beam.Server;
using Beam.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Beam.Server.Tests;

public class OutboxEndpointsTests
{
    private static async Task<(WebApplication app, HttpClient client, BeamServerOptions options)> StartAppAsync()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new BeamServerOptions(dataDir, "test-token", Port: 0);
        var app = BeamServerHost.Build(options, webHost => webHost.UseUrls("http://127.0.0.1:0"));
        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        client.DefaultRequestHeaders.Add("X-Beam-Token", "test-token");
        return (app, client, options);
    }

    [Fact]
    public async Task GetOutbox_ReturnsOnlyUnreadOutboundItems()
    {
        var (app, client, options) = await StartAppAsync();
        try
        {
            var index = app.Services.GetRequiredService<HistoryIndex>();
            OutboxQueue.QueueText(index, "unread note");
            var readItem = OutboxQueue.QueueText(index, "already read");
            index.MarkRead(readItem.Id);

            var response = await client.GetAsync("/outbox");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            Assert.Equal(1, doc.RootElement.GetArrayLength());
            Assert.Equal("unread note", doc.RootElement[0].GetProperty("textContent").GetString());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task GetOutboxContent_ReturnsFileBytes()
    {
        var (app, client, options) = await StartAppAsync();
        try
        {
            var sourceFile = Path.Combine(options.DataDirectory, "source.txt");
            Directory.CreateDirectory(options.DataDirectory);
            await File.WriteAllTextAsync(sourceFile, "file body");

            var index = app.Services.GetRequiredService<HistoryIndex>();
            var store = app.Services.GetRequiredService<ItemStore>();
            var item = OutboxQueue.QueueFile(index, store, sourceFile);

            var response = await client.GetAsync($"/outbox/{item.Id}/content");
            response.EnsureSuccessStatusCode();
            Assert.Equal("file body", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task AckOutbox_MarksItemRead()
    {
        var (app, client, options) = await StartAppAsync();
        try
        {
            var index = app.Services.GetRequiredService<HistoryIndex>();
            var item = OutboxQueue.QueueText(index, "note");

            var response = await client.PostAsync($"/outbox/{item.Id}/ack", content: null);
            response.EnsureSuccessStatusCode();

            Assert.Empty(index.ListUnread("outbound"));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task GetOutboxContent_UnknownId_Returns404()
    {
        var (app, client, options) = await StartAppAsync();
        try
        {
            var response = await client.GetAsync("/outbox/does-not-exist/content");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
